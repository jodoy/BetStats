using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Application.Quality;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Quality;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Quality;

public sealed class DataReconciliation(BetStatsDbContext db, IRawPayloadStore storage, IFootballMetadataParser parser,
    IFootballIngestionPersistence publication, ISourcePolicyEvaluator policies, ISourceOperationalStatus sources) : IDataReconciliation
{
    private async Task<Guid?> Authorize(Guid source, CancellationToken token)
    {
        if (await sources.ReadAsync(source, token) != SourceOperationalStatus.Enabled) throw new IngestionDeniedException("source_disabled_or_missing");
        var now = await QualityPersistence.Now(db, token); Guid? policy = null;
        foreach (var purpose in new[] { DataPurpose.RawPayloadStorage, DataPurpose.HistoricalRetention, DataPurpose.InternalAnalytics })
        {
            var evaluation = await policies.EvaluateAsync(source, purpose, now, new(), token);
            if (!evaluation.Allowed) throw new IngestionDeniedException("policy_" + evaluation.Reason);
            policy = evaluation.PolicyId;
        }
        return policy;
    }
    public async Task<ReconciliationResult> RunAsync(IReadOnlyList<RawReconciliationRequest> requests, string operatorId, string reason, CancellationToken token = default)
    {
        QualityPersistence.Operator(operatorId, reason);
        if (requests.Count is < 1 or > 20 || requests.Select(r => r.RawPayloadId).Distinct().Count() != requests.Count || requests.Any(r => !r.Scope.IsValid))
            throw new ArgumentException("Supply 1..20 distinct RAW UUIDs and valid explicit publication contexts.");
        var execution = Guid.NewGuid(); db.ChangeTracker.Clear();
        db.MaintenanceEvents.Add(QualityPersistence.Audit(execution, 1, "Reconciliation", operatorId, reason, requests[0].RawPayloadId, "Started", await QualityPersistence.Now(db, token)));
        await db.SaveChangesAsync(token);
        var items = new List<ReconciliationItem>();
        try
        {
            foreach (var request in requests)
            {
                token.ThrowIfCancellationRequested(); db.ChangeTracker.Clear();
                var raw = await db.RawPayloads.AsNoTracking().SingleOrDefaultAsync(r => r.Id == request.RawPayloadId, token);
                if (raw is null || raw.ByteLength is null) { items.Add(new(request.RawPayloadId, 0, ReconciliationOutcome.Failed, "missing_verified_manifest")); await RawFailure(execution, request.RawPayloadId, operatorId, reason, "missing_verified_manifest", token); continue; }
                await Authorize(raw.DataSourceId, token);
                ReadOnlyMemory<byte> bytes;
                try { bytes = await storage.ReadAsync(new(raw.StorageKey, raw.ContentHashSha256, raw.ByteLength.Value), token); }
                catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException) { items.Add(new(raw.Id, 0, ReconciliationOutcome.Failed, "raw_integrity_or_storage")); await RawFailure(execution, raw.Id, operatorId, reason, "raw_integrity_or_storage", token); continue; }
                var parsed = parser.Parse(bytes, request.Scope, token);
                var selected = new List<FootballMatchRecord>();
                await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token))
                {
                    await QualityPersistence.Lock(db, raw.DataSourceId, token);
                    var policy = await Authorize(raw.DataSourceId, token); var now = await QualityPersistence.Now(db, token);
                    var refs = parsed.Records.SelectMany(r => new[] { r.MatchReference, r.HomeReference, r.AwayReference, "provider:competition:" + r.CompetitionReference,
                        FootballDataCsvParser.SeasonReference(r.CompetitionReference, r.SeasonReference) }).Distinct().ToArray();
                    var review = await db.IdentityResolutions.AsNoTracking().Where(d => d.DataSourceId == raw.DataSourceId &&
                        db.ProviderIdentities.Any(i => i.Id == d.ProviderIdentityId && refs.Contains(i.ExternalId)) &&
                        !db.IdentityResolutions.Any(n => n.ProviderIdentityId == d.ProviderIdentityId && n.Version > d.Version))
                        .OrderBy(d => d.ProviderIdentityId).Select(d => new { d.ProviderIdentityId, d.Id, d.Version }).Take(25001).ToListAsync(token);
                    if (review.Count > 25000) throw new InvalidOperationException("Review context exceeds bound.");
                    var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { raw.Id, raw.ContentHashSha256,
                        request.Scope, Parser = FootballDataCsvParser.Version, Rules = FootballQualityRules.Version, Review = review }))));
                    foreach (var issue in QualityPersistence.ExpandIssues(parsed))
                    {
                        var q = FootballQualityRules.ParseIssue(issue.Code);
                        QualityPersistence.Record(db, execution, raw, raw.IngestionRunId, issue.Row, "source-row:" + issue.Row, null, policy, now, [q], key);
                        items.Add(new(raw.Id, issue.Row, q.Classification == QualityClassification.DuplicateContradictory ? ReconciliationOutcome.Conflict : ReconciliationOutcome.Rejected, issue.Code));
                    }
                    foreach (var row in parsed.Records)
                    {
                        var evidence = await QualityPersistence.Assess(db, raw, row, now, token);
                        QualityPersistence.Record(db, execution, raw, raw.IngestionRunId, row.Row, row.MatchReference, evidence.Identity, policy, now, evidence.Issues, key);
                        var blocked = evidence.Issues.Where(i => i.BlocksEligibility).ToArray();
                        var published = await db.Observations.AnyAsync(o => o.ProviderIdentityId == evidence.Identity && o.RawPayloadId == raw.Id && o.CanonicalSportingEventId != null, token);
                        var conflict = blocked.FirstOrDefault(i => i.Classification is not (QualityClassification.IdentityAmbiguous or QualityClassification.Superseded));
                        if (conflict is not null)
                            items.Add(new(raw.Id, row.Row, conflict.Classification is QualityClassification.CanonicalMismatch or QualityClassification.ObservationConflict or QualityClassification.InvalidTransition ?
                                ReconciliationOutcome.Conflict : ReconciliationOutcome.Rejected, conflict.ReasonCode));
                        else if (published || await QualityPersistence.ReceiptExists(db, raw, request.Scope, row, now, token) || blocked.Any(i => i.Classification == QualityClassification.Superseded))
                            items.Add(new(raw.Id, row.Row, ReconciliationOutcome.AlreadyProcessed, "published_or_superseded"));
                        else if (blocked.Any(i => i.Classification == QualityClassification.IdentityAmbiguous) && blocked.All(i => i.Classification == QualityClassification.IdentityAmbiguous))
                            items.Add(new(raw.Id, row.Row, ReconciliationOutcome.StillUnresolved, "identity_unresolved"));
                        else if (blocked.Length > 0)
                            items.Add(new(raw.Id, row.Row, blocked.Any(i => i.Classification is QualityClassification.CanonicalMismatch or QualityClassification.ObservationConflict or QualityClassification.InvalidTransition) ?
                                ReconciliationOutcome.Conflict : ReconciliationOutcome.Rejected, blocked.OrderBy(i => i.Rule.Id).First().ReasonCode));
                        else selected.Add(row);
                    }
                    await db.SaveChangesAsync(token); await tx.CommitAsync(token);
                }
                if (selected.Count == 0) continue;
                var attempt = await publication.BeginAsync(Guid.NewGuid(), raw.DataSourceId, token);
                ImportReport report;
                try
                {
                    report = await publication.PublishAsync(attempt, new(raw.Id, new(raw.StorageKey, raw.ContentHashSha256, raw.ByteLength.Value), raw.RetrievedAtUtc, raw.CreatedAtUtc, raw.RecordedAtUtc),
                        request.Scope, new(selected.Count, selected, [], CompletePayload: false), token);
                    await publication.CompleteAsync(report, token);
                    foreach (var row in selected)
                    {
                        var anchor = await QualityPersistence.Anchor(db, raw.DataSourceId, CanonicalEntityKind.SportingEvent, row.MatchReference, token);
                        var anchorId = anchor?.Id;
                        var written = await db.Observations.AnyAsync(o => o.ProviderIdentityId == anchorId && o.RawPayloadId == raw.Id && o.CanonicalSportingEventId != null, token);
                        items.Add(new(raw.Id, row.Row, report.Outcome == ImportOutcome.Reused ? ReconciliationOutcome.AlreadyProcessed :
                            written ? ReconciliationOutcome.NewlyResolved : ReconciliationOutcome.Conflict,
                            written ? "new_evidence_published" : report.ErrorCode ?? "publication_not_completed"));
                    }
                }
                catch (IngestionDeniedException denied)
                {
                    await publication.CompleteAsync(attempt with { Outcome = ImportOutcome.Denied, ErrorCode = denied.Code, ErrorCategory = denied.Category }, token);
                    foreach (var row in selected) items.Add(new(raw.Id, row.Row, ReconciliationOutcome.Conflict, denied.Code));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await publication.CompleteAsync(attempt with { Outcome = ImportOutcome.Interrupted, ErrorCode = "reconciliation_cancelled", ErrorCategory = "Cancellation" }, deadline.Token);
                    throw;
                }
                catch (IngestionPersistenceException)
                {
                    await publication.CompleteAsync(attempt with { Outcome = ImportOutcome.Failed, ErrorCode = "reconciliation_persistence_failed", ErrorCategory = "Persistence" }, token);
                    foreach (var row in selected) items.Add(new(raw.Id, row.Row, ReconciliationOutcome.Failed, "reconciliation_persistence_failed"));
                }
            }
            var outcome = items.Any(i => i.Outcome is ReconciliationOutcome.Failed or ReconciliationOutcome.Conflict) ? "Partial" : "Completed";
            await Terminal(execution, operatorId, reason, requests[0].RawPayloadId, outcome, token);
            return new(execution, outcome, items.OrderBy(i => i.RawPayloadId).ThenBy(i => i.Row).ToArray());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Terminal(execution, operatorId, reason, requests[0].RawPayloadId, "Interrupted", deadline.Token); throw;
        }
        catch (IngestionDeniedException denied)
        {
            await Terminal(execution, operatorId, reason, requests[0].RawPayloadId, "Denied", token);
            return new(execution, "Denied", [.. items, new(requests[0].RawPayloadId, 0, ReconciliationOutcome.Rejected, denied.Code)]);
        }
        catch (Exception)
        {
            await Terminal(execution, operatorId, reason, requests[0].RawPayloadId, "Failed", token);
            return new(execution, "Failed", [.. items, new(requests[0].RawPayloadId, 0, ReconciliationOutcome.Failed, "reconciliation_failed")]);
        }
    }
    private async Task Terminal(Guid execution, string actor, string reason, Guid target, string result, CancellationToken token)
    {
        db.ChangeTracker.Clear();
        if (await db.MaintenanceEvents.AnyAsync(e => e.ExecutionId == execution && e.Sequence == 2, token)) return;
        db.MaintenanceEvents.Add(QualityPersistence.Audit(execution, 2, "Reconciliation", actor, reason, target, result, await QualityPersistence.Now(db, token), execution));
        await db.SaveChangesAsync(token);
    }
    private async Task RawFailure(Guid execution, Guid raw, string actor, string reason, string code, CancellationToken token)
    {
        db.MaintenanceEvents.Add(QualityPersistence.Audit(Guid.NewGuid(), 1, "ReconciliationRaw", actor, reason, raw, code, await QualityPersistence.Now(db, token), execution));
        await db.SaveChangesAsync(token);
    }
    public async Task MarkInterruptedAsync(Guid executionId, string operatorId, string reason, CancellationToken token = default)
    {
        QualityPersistence.Operator(operatorId, reason);
        var start = await db.MaintenanceEvents.AsNoTracking().SingleAsync(e => e.ExecutionId == executionId && e.Sequence == 1 && e.Action == "Reconciliation", token);
        await Terminal(executionId, operatorId, reason, start.TargetId, "Interrupted", token);
    }
}
