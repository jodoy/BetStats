using System.Data;
using System.Security.Cryptography;
using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Domain.Sports;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using BetStats.Application.Quality;
using BetStats.Domain.Quality;
using BetStats.Infrastructure.Quality;

namespace BetStats.Infrastructure.Ingestion;

public sealed class FootballIngestionPersistence(BetStatsDbContext context, ISourcePolicyEvaluator policies, ISourceOperationalStatus sourceStatus) : IFootballIngestionPersistence
{
    private static readonly Guid Football = ReferenceSports.All.Single(s => s.Code == "football").Id;
    private Guid? lastPolicy;
    private Guid? lastApproval;
    private string[] issues = [];
    private Task<DateTime> Now(CancellationToken token) => context.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);

    public async Task<ImportReport> BeginAsync(Guid attemptId, Guid sourceId, CancellationToken cancellationToken)
    {
        context.ChangeTracker.Clear();
        lastPolicy = null; lastApproval = null; issues = [];
        var now = await Now(cancellationToken);
        Guid? runId = null;
        if (await context.DataSources.AnyAsync(s => s.Id == sourceId, cancellationToken))
        {
            runId = Guid.NewGuid(); context.Add(new IngestionRun { Id = runId.Value, DataSourceId = sourceId, CreatedAtUtc = now, StartedAtUtc = now, Status = IngestionRunStatus.Running });
        }
        var report = new ImportReport(attemptId, runId, sourceId, ImportOutcome.Running);
        context.Add(Audit(report, 1, now)); await context.SaveChangesAsync(cancellationToken); return report;
    }
    public Task EnsureCaptureAllowedAsync(Guid sourceId, CancellationToken cancellationToken) => Guard(sourceId, [DataPurpose.RawPayloadStorage, DataPurpose.HistoricalRetention], cancellationToken);
    private async Task Guard(Guid sourceId, DataPurpose[] purposes, CancellationToken token)
    {
        var status = await sourceStatus.ReadAsync(sourceId, token);
        if (status != SourceOperationalStatus.Enabled) throw new IngestionDeniedException(status == SourceOperationalStatus.Missing ? "source_missing" : "source_disabled");
        var now = await Now(token);
        foreach (var purpose in purposes)
        {
            var evaluated = await policies.EvaluateAsync(sourceId, purpose, now, new(), token);
            if (!evaluated.Allowed) throw new IngestionDeniedException("policy_" + evaluated.Reason);
            lastPolicy = evaluated.PolicyId;
        }
        if (lastPolicy is { } id)
            lastApproval = await context.PolicyAudits.Where(a => a.SourcePolicyId == id && a.Status == PolicyStatus.Approved).Select(a => (Guid?)a.Id).SingleAsync(token);
    }
    private async Task LockSource(Guid sourceId, CancellationToken token) =>
        _ = await context.Database.SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM ingestion.\"DataSources\" WHERE \"Id\" = {sourceId} FOR UPDATE").ToListAsync(token);

    public async Task<RawCapture> CaptureAsync(ImportReport attempt, RetrievedContent content, StoredPayload payload, CancellationToken cancellationToken)
    {
        if (payload.Length != content.Bytes.Length || payload.Hash != Convert.ToHexStringLower(SHA256.HashData(content.Bytes.Span)))
            throw new IngestionDeniedException("raw_manifest_mismatch", "Provenance");
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await LockSource(attempt.DataSourceId, cancellationToken);
        await Guard(attempt.DataSourceId, [DataPurpose.DataRetrieval, DataPurpose.RawPayloadStorage, DataPurpose.HistoricalRetention], cancellationToken);
        var now = await Now(cancellationToken);
        var raw = new RawPayload { Id = Guid.NewGuid(), DataSourceId = attempt.DataSourceId, IngestionRunId = attempt.RunId,
            RetrievedAtUtc = content.RetrievedAtUtc, CreatedAtUtc = now, ContentType = content.ContentType, ContentHashSha256 = payload.Hash,
            StorageKey = payload.StorageKey, ByteLength = payload.Length, ExternalReference = "fixture:metadata-v1" };
        context.Add(raw); await Save(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return new(raw.Id, payload, raw.RetrievedAtUtc, raw.CreatedAtUtc, raw.RecordedAtUtc);
    }

    public async Task<ImportReport> PublishAsync(ImportReport attempt, RawCapture raw, FootballImportScope scope, FootballParseResult parsed, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await LockSource(attempt.DataSourceId, cancellationToken);
        await Guard(attempt.DataSourceId, [DataPurpose.DataRetrieval, DataPurpose.RawPayloadStorage, DataPurpose.HistoricalRetention, DataPurpose.InternalAnalytics], cancellationToken);
        var storedRaw = await context.RawPayloads.AsNoTracking().SingleOrDefaultAsync(r => r.Id == raw.Id, cancellationToken);
        if (storedRaw is null || storedRaw.DataSourceId != attempt.DataSourceId || storedRaw.StorageKey != raw.Object.StorageKey ||
            storedRaw.ContentHashSha256 != raw.Object.Hash || (storedRaw.ByteLength is { } length && length != raw.Object.Length) ||
            storedRaw.RetrievedAtUtc != raw.RetrievedAtUtc || storedRaw.CreatedAtUtc != raw.CreatedAtUtc || storedRaw.RecordedAtUtc != raw.RecordedAtUtc)
            throw new IngestionDeniedException("raw_manifest_mismatch", "Provenance");
        var key = FootballPublicationKeys.Batch(scope, raw.Object.Hash);
        var previous = await context.IngestionPublications.SingleOrDefaultAsync(r => r.DataSourceId == attempt.DataSourceId && r.Key == key, cancellationToken);
        issues = parsed.Issues.Select(i => $"{i.Row}:{i.Code}").ToArray();
        if (previous is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return attempt with { Outcome = ImportOutcome.Reused, AcceptedRecords = previous.AcceptedRecords, PolicyId = lastPolicy };
        }
        var now = await Now(cancellationToken); var unresolved = new HashSet<Guid>(); var accepted = 0;
        var rowIssues = new List<string>(issues);
        foreach (var issue in QualityPersistence.ExpandIssues(parsed))
            QualityPersistence.Record(context, attempt.AttemptId, storedRaw, attempt.RunId, issue.Row, "source-row:" + issue.Row, null,
                lastPolicy, now, [FootballQualityRules.ParseIssue(issue.Code)]);
        foreach (var record in parsed.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assessment = await QualityPersistence.Assess(context, storedRaw, record, now, cancellationToken);
            QualityPersistence.Record(context, attempt.AttemptId, storedRaw, attempt.RunId, record.Row, record.MatchReference, assessment.Identity,
                lastPolicy, now, assessment.Issues);
            var blocking = assessment.Issues.FirstOrDefault(i => i.BlocksEligibility && i.Classification != QualityClassification.IdentityAmbiguous);
            // Legacy identity mismatches still fail the publication transaction.
            if (blocking is not null && blocking.Classification is not QualityClassification.CanonicalMismatch)
            {
                rowIssues.Add($"{record.Row}:{blocking.ReasonCode}");
                continue;
            }
            var competitionAnchor = await Anchor(CanonicalEntityKind.Competition, "provider:competition:" + record.CompetitionReference);
            var seasonAnchor = await Anchor(CanonicalEntityKind.Season, FootballDataCsvParser.SeasonReference(record.CompetitionReference, record.SeasonReference));
            var homeAnchor = await Anchor(CanonicalEntityKind.Participant, record.HomeReference);
            var awayAnchor = await Anchor(CanonicalEntityKind.Participant, record.AwayReference);
            var eventAnchor = await Anchor(CanonicalEntityKind.SportingEvent, record.MatchReference);
            var competitionDecision = await Decision(competitionAnchor); var seasonDecision = await Decision(seasonAnchor);
            var homeDecision = await Decision(homeAnchor); var awayDecision = await Decision(awayAnchor); var eventDecision = await Decision(eventAnchor);
            var competition = competitionDecision?.CanonicalCompetitionId is { } competitionId ? await context.Competitions.SingleAsync(c => c.Id == competitionId, cancellationToken) : null;
            var season = seasonDecision?.CanonicalSeasonId is { } seasonId ? await context.Seasons.SingleAsync(s => s.Id == seasonId, cancellationToken) : null;
            var home = homeDecision?.CanonicalParticipantId is { } homeId ? await context.Participants.SingleAsync(p => p.Id == homeId, cancellationToken) : null;
            var away = awayDecision?.CanonicalParticipantId is { } awayId ? await context.Participants.SingleAsync(p => p.Id == awayId, cancellationToken) : null;
            if ((competition is not null && competition.SportId != Football) || (home is not null && (home.SportId != Football || home.ParticipantType != ParticipantType.Team)) ||
                (away is not null && (away.SportId != Football || away.ParticipantType != ParticipantType.Team)) || (season is not null && competition is not null && season.CompetitionId != competition.Id) || (home is not null && away is not null && home.Id == away.Id))
                throw new IngestionDeniedException("identity_context_mismatch", "Identity");
            SportingEvent? sportingEvent = null;
            if (competition is not null && season is not null && home is not null && away is not null)
            {
                if (eventDecision?.Status == ResolutionStatus.Ambiguous) { unresolved.Add(eventAnchor.Id); }
                else if (eventDecision?.CanonicalSportingEventId is { } eventId)
                {
                    sportingEvent = await context.SportingEvents.Include(e => e.Participants).SingleAsync(e => e.Id == eventId, cancellationToken);
                    if (sportingEvent.CompetitionId != competition.Id || sportingEvent.SeasonId != season.Id || sportingEvent.SportId != Football ||
                        !sportingEvent.Participants.Any(p => p.ParticipantId == home.Id && p.Role == ParticipantRole.Home) ||
                        !sportingEvent.Participants.Any(p => p.ParticipantId == away.Id && p.Role == ParticipantRole.Away))
                        throw new IngestionDeniedException("event_identity_collision", "Identity");
                }
                else
                {
                    // No scheduling/status guess: only create events whose source establishes completion.
                    if (record.Status == SportingEventStatus.Completed)
                    {
                        sportingEvent = new SportingEvent(Guid.NewGuid(), competition, season, null, SportingEventStatus.Completed, now);
                        sportingEvent.AddParticipant(home, ParticipantRole.Home, 1); sportingEvent.AddParticipant(away, ParticipantRole.Away, 2); context.Add(sportingEvent);
                        context.Add(new IdentityResolution(Guid.NewGuid(), eventAnchor, ResolutionStatus.Resolved, new(CanonicalEntityKind.SportingEvent, sportingEvent.Id),
                            "ingestion:exact-reviewed-context", "New event from explicit competition/season/participant mappings", now, eventDecision, raw.Id));
                    }
                }
            }
            if (sportingEvent is null) { unresolved.Add(eventAnchor.Id); rowIssues.Add($"{record.Row}:unresolved_context"); }
            else { accepted++; unresolved.Remove(eventAnchor.Id); }
            if (sportingEvent is not null)
            {
                var rowKey = FootballPublicationKeys.Row(key, record.MatchReference, competition!.Id, season!.Id, home!.Id, away!.Id, sportingEvent.Id);
                if (await context.IngestionPublications.AnyAsync(r => r.DataSourceId == attempt.DataSourceId && r.Key == rowKey, cancellationToken)) continue;
                context.Add(new IngestionPublication { Id = Guid.NewGuid(), DataSourceId = attempt.DataSourceId, Key = rowKey, RawPayloadId = raw.Id, RunId = attempt.RunId!.Value, AcceptedRecords = 1 });
            }
            await Name(homeAnchor, home?.Id, record.HomeName); await Name(awayAnchor, away?.Id, record.AwayName);
            await Fact(eventAnchor, sportingEvent?.Id, ObservationType.EventDate, record.MatchDate, null);
            if (record.Status is { } status) await Fact(eventAnchor, sportingEvent?.Id, ObservationType.EventStatus, null, status);
        }
        issues = rowIssues.ToArray();
        await Save(cancellationToken);
        var outcome = issues.Length == 0 && unresolved.Count == 0 ? ImportOutcome.Succeeded : ImportOutcome.Partial;
        if (outcome == ImportOutcome.Succeeded && parsed.CompletePayload)
            context.Add(new IngestionPublication { Id = Guid.NewGuid(), DataSourceId = attempt.DataSourceId, Key = key, RawPayloadId = raw.Id, RunId = attempt.RunId!.Value, AcceptedRecords = accepted, IsBatch = true });
        await Save(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return attempt with { Outcome = outcome, AcceptedRecords = accepted, UnresolvedIdentities = unresolved.Count, ErrorCode = outcome == ImportOutcome.Partial ? "validation_or_identity_incomplete" : null,
            ErrorCategory = outcome == ImportOutcome.Partial ? "ValidationOrIdentity" : null, PolicyId = lastPolicy };

        async Task<ProviderIdentity> Anchor(CanonicalEntityKind kind, string reference)
        {
            var anchor = context.ProviderIdentities.Local.FirstOrDefault(i => i.DataSourceId == attempt.DataSourceId && i.EntityKind == kind && i.ExternalId == reference)
                ?? await context.ProviderIdentities.SingleOrDefaultAsync(i => i.DataSourceId == attempt.DataSourceId && i.EntityKind == kind && i.ExternalId == reference, cancellationToken);
            if (anchor is null) { anchor = new(Guid.NewGuid(), attempt.DataSourceId, kind, reference, now); context.Add(anchor); }
            return anchor;
        }
        async Task<IdentityResolution?> Decision(ProviderIdentity identity)
        {
            var decision = context.IdentityResolutions.Local.Where(d => d.ProviderIdentityId == identity.Id).OrderByDescending(d => d.Version).FirstOrDefault()
                ?? await context.IdentityResolutions.Where(d => d.ProviderIdentityId == identity.Id && d.DecidedAtUtc <= now && d.RecordedAtUtc <= now).OrderByDescending(d => d.Version).FirstOrDefaultAsync(cancellationToken);
            if (decision is null) { decision = new(Guid.NewGuid(), identity, ResolutionStatus.Unresolved, null, "ingestion:unresolved", "Explicit mapping required", now, rawPayloadId: raw.Id); context.Add(decision); }
            if (decision.Status != ResolutionStatus.Resolved) unresolved.Add(identity.Id);
            return decision;
        }
        async Task Name(ProviderIdentity identity, Guid? target, string name)
        {
            var previousObservation = await Latest(identity, ObservationType.DisplayName);
            if (previousObservation?.TextValue == name && previousObservation.CanonicalId == target) return;
            context.Add(new Observation(Guid.NewGuid(), identity, target is { } id ? new(CanonicalEntityKind.Participant, id) : null,
                ObservationType.DisplayName, raw.RetrievedAtUtc, now, now, textValue: name, rawPayloadId: raw.Id, corrects: previousObservation));
        }
        async Task Fact(ProviderIdentity identity, Guid? target, ObservationType type, DateOnly? date, SportingEventStatus? status)
        {
            var previousObservation = await Latest(identity, type);
            if (previousObservation is not null && previousObservation.DateValue == date && previousObservation.StatusValue == status && previousObservation.CanonicalId == target) return;
            context.Add(new Observation(Guid.NewGuid(), identity, target is { } id ? new(CanonicalEntityKind.SportingEvent, id) : null, type,
                raw.RetrievedAtUtc, now, now, statusValue: status, rawPayloadId: raw.Id, corrects: previousObservation, dateValue: date));
        }
        async Task<Observation?> Latest(ProviderIdentity identity, ObservationType type) => context.Observations.Local.Where(o => o.ProviderIdentityId == identity.Id && o.Type == type).OrderByDescending(o => o.Version).FirstOrDefault()
            ?? await context.Observations.Where(o => o.ProviderIdentityId == identity.Id && o.Type == type).OrderByDescending(o => o.Version).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task CompleteAsync(ImportReport report, CancellationToken cancellationToken)
    {
        // Failed writes must not be retried as part of outcome auditing.
        context.ChangeTracker.Clear();
        if (await context.IngestionAuditEvents.AnyAsync(a => a.AttemptId == report.AttemptId && a.Sequence == 2, cancellationToken)) return;
        var now = await Now(cancellationToken);
        if (report.RunId is { } id)
        {
            var run = await context.IngestionRuns.SingleAsync(r => r.Id == id, cancellationToken);
            run.Status = report.Outcome is ImportOutcome.Succeeded or ImportOutcome.Reused ? IngestionRunStatus.Succeeded : IngestionRunStatus.Failed;
            run.CompletedAtUtc = now; run.ErrorCode = report.ErrorCode;
        }
        context.Add(Audit(report, 2, now)); await context.SaveChangesAsync(cancellationToken);
    }
    private IngestionAuditEvent Audit(ImportReport report, int sequence, DateTime now) => new()
    {
        Id = Guid.NewGuid(), AttemptId = report.AttemptId, Sequence = sequence, RunId = report.RunId, DataSourceId = report.DataSourceId,
        Outcome = report.Outcome, AtUtc = now, RetrievedPayloads = report.RetrievedPayloads, ParsedRecords = report.ParsedRecords,
        AcceptedRecords = report.AcceptedRecords, RejectedRecords = report.RejectedRecords, UnresolvedIdentities = report.UnresolvedIdentities,
        ErrorCode = report.ErrorCode, ErrorCategory = report.ErrorCategory, PolicyId = report.PolicyId ?? lastPolicy, ApprovalAuditId = lastApproval, Issues = issues
    };
    private async Task Save(CancellationToken token)
    {
        try { await context.SaveChangesAsync(token); }
        catch (DbUpdateException) { throw new IngestionPersistenceException(); }
    }
}
