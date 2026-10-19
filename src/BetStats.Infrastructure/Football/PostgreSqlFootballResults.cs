using BetStats.Application.Datasets;
using BetStats.Application.Football;
using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Application.Quality;
using BetStats.Domain.Football;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Quality;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;
using System.Text;
using BetStats.Infrastructure.Persistence.Entities;

namespace BetStats.Infrastructure.Football;

public sealed class PostgreSqlFootballResults(BetStatsDbContext db, IRawPayloadStore storage,
    ISourcePolicyEvaluator policies, ISourceOperationalStatus sources, IAnalyticalQualityGate gate) : IFootballResults
{
    private static void Utc(DateTime t) { if (t.Kind != DateTimeKind.Utc || t.Ticks % 10 != 0) throw new ArgumentException("UTC microsecond cutoff required."); }
    public Task<FootballResultReport> ReadAsync(FootballResultQuery q, CancellationToken token = default) => ReadCoreAsync(q, token);
    public Task<IReadOnlyList<FootballResultReport>> ReadManyAsync(IReadOnlyList<FootballResultQuery> queries, CancellationToken token = default) => ReadManyAsync(queries, new RawReadSession(), token);
    public async Task<IReadOnlyList<FootballResultReport>> ReadManyAsync(IReadOnlyList<FootballResultQuery> queries, RawReadSession session, CancellationToken token = default)
    {
        if (queries.Count > 100) throw new ArgumentException("Result batch exceeds 100.");
        var reports = new List<FootballResultReport>();
        var parsed = new Dictionary<(Guid, FootballImportScope), FootballParseResult>();
        // Bounds remain per event. A season-wide Take(1001) would silently change
        // the individual query contract when the page contains many events.
        foreach (var batch in queries.Chunk(25))
        {
            var history = new Dictionary<FootballResultQuery, IReadOnlyList<FootballResultObservation>>();
            foreach (var group in batch.Where(q => q.EventId is not null).GroupBy(q => (q.CompetitionId, q.SeasonId, q.AsOfUtc)))
            {
                var events = System.Text.Json.JsonSerializer.Serialize(group.Select(q => q.EventId).Distinct());
                var rows = await db.FootballResults.FromSqlInterpolated($"""
                    SELECT r.* FROM jsonb_array_elements_text({events}::jsonb) event_id
                    CROSS JOIN LATERAL (
                        SELECT r.* FROM football."Results" r
                        WHERE r."CompetitionId" = {group.Key.CompetitionId} AND r."SeasonId" = {group.Key.SeasonId}
                          AND r."EventId" = event_id::uuid AND r."AvailableAtUtc" <= {group.Key.AsOfUtc} AND r."RecordedAtUtc" <= {group.Key.AsOfUtc}
                        ORDER BY r."EventId", r."ProviderIdentityId", r."Version" LIMIT 1001
                    ) r ORDER BY r."EventId", r."ProviderIdentityId", r."Version"
                    """).AsNoTracking().ToListAsync(token);
                var byEvent = rows.ToLookup(r => r.EventId);
                foreach (var q in group) history[q] = byEvent[q.EventId!.Value].ToArray();
            }
            var metadata = new Dictionary<Guid, RawPayload>();
            foreach (var ids in history.Values.SelectMany(r => r).Select(r => r.RawId).Distinct().Chunk(1000))
                foreach (var raw in await db.RawPayloads.AsNoTracking().Where(r => ids.Contains(r.Id)).ToListAsync(token)) metadata.Add(raw.Id, raw);
            foreach (var q in batch)
                reports.Add(await ReadCoreAsync(q, token, history.GetValueOrDefault(q), q.EventId is null ? null : metadata, parsed, session));
        }
        return reports;
    }
    private async Task<FootballResultReport> ReadCoreAsync(FootballResultQuery q, CancellationToken token,
        IReadOnlyList<FootballResultObservation>? prefetched = null, IReadOnlyDictionary<Guid, RawPayload>? metadata = null,
        Dictionary<(Guid, FootballImportScope), FootballParseResult>? parsedCache = null, RawReadSession? session = null)
    {
        Utc(q.AsOfUtc); var knowledge = q.ReconstructionAtUtc ?? q.AsOfUtc; Utc(knowledge);
        if (q.CompetitionId == Guid.Empty || q.SeasonId == Guid.Empty || q.Context is null || !Enum.IsDefined(q.Mode) ||
            q.Purpose is not (DataPurpose.InternalAnalytics or DataPurpose.ModelTraining or DataPurpose.PublicDisplay or DataPurpose.CommercialUse or DataPurpose.Redistribution) ||
            q.Mode == DatasetMode.HistoricalAsKnown && q.ReconstructionAtUtc is not null ||
            q.Mode == DatasetMode.RetrospectiveReconstruction && (q.ReconstructionAtUtc is null || knowledge < q.AsOfUtc) ||
            knowledge > await QualityPersistence.Now(db, token)) throw new ArgumentException("Explicit valid result scope and knowledge cutoffs required.");
        var all = prefetched ?? await db.FootballResults.AsNoTracking().Where(r => r.CompetitionId == q.CompetitionId && r.SeasonId == q.SeasonId &&
            (q.EventId == null || r.EventId == q.EventId) && r.AvailableAtUtc <= q.AsOfUtc && r.RecordedAtUtc <= q.AsOfUtc)
            .OrderBy(r => r.EventId).ThenBy(r => r.ProviderIdentityId).ThenBy(r => r.Version).Take(1001).ToListAsync(token);
        if (all.Count > 1000) throw new InvalidOperationException("Result history exceeds 1000 observations; narrow scope.");
        var latest = all.GroupBy(r => r.ProviderIdentityId).Select(g => g.MaxBy(r => r.Version)!).ToArray();
        var selected = (q.IncludeSuperseded ? all : latest.AsEnumerable()).Where(r => q.SourceId is null || r.SourceId == q.SourceId).ToArray();
        var output = new List<FootballResultEvidence>();
        foreach (var r in selected)
        {
            var reasons = new SortedSet<string>(StringComparer.Ordinal); var decisions = new List<Guid>(); var frozen = new List<DatasetFrozenRecord>();
            void Freeze<T>(string kind, Guid id, T value) => frozen.Add(new(kind, id, Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(value))));
            Freeze("football-result-v1", r.Id, r);
            var raw = metadata is null ? await db.RawPayloads.AsNoTracking().SingleAsync(x => x.Id == r.RawId, token) :
                metadata.TryGetValue(r.RawId, out var foundRaw) ? foundRaw : throw new InvalidOperationException("Result RAW missing.");
            Freeze("raw", raw.Id, raw);
            try
            {
                await Authorize(r.SourceId, raw.RetrievedAtUtc, q.Purpose, q.Context, knowledge, frozen, token);
                if (raw.DataSourceId != r.SourceId || raw.RecordedAtUtc > q.AsOfUtc || raw.RetrievedAtUtc != r.RetrievedAtUtc) reasons.Add("result_raw_not_known_at_cutoff");
                var scope = await FootballContext.ReadAsync(db, raw, new(r.CompetitionReference, r.SeasonReference), token, q.AsOfUtc);
                if (scope != new FootballImportScope(r.CompetitionReference, r.SeasonReference)) reasons.Add("result_original_scope_mismatch");
                if (raw.ByteLength is not { } length) reasons.Add("result_raw_length_missing");
                else
                {
                    if (parsedCache is null || !parsedCache.TryGetValue((raw.Id, scope), out var parsed))
                    {
                        var payload = new StoredPayload(raw.StorageKey, raw.ContentHashSha256, length);
                        var bytes = session is null ? await storage.ReadAsync(payload, token) : await session.ReadAsync(storage, payload, token);
                        parsed = new FootballFixtureParser().ParseProfile(bytes, scope,
                            raw.ExternalReference == "fixture:" + ApiFootballFixtureAdapter.Profile ? ApiFootballFixtureAdapter.Profile :
                            raw.ExternalReference == "fixture:" + HistoricalFootballCsvParser.Version ? HistoricalFootballCsvParser.Version : FootballResultsCsvParser.Version, token);
                        if (parsedCache is { Count: < 16 }) parsedCache.Add((raw.Id, scope), parsed);
                    }
                    var row = parsed.Records.SingleOrDefault(x => x.MatchReference == r.SourceEventReference);
                    if (row?.Result?.Value != r.Value || row.Result.PublishedAtUtc != r.PublishedAtUtc || row.MatchDate != r.EventDate) reasons.Add("result_raw_value_mismatch");
                    else
                    {
                        foreach (var pair in new[] { (CanonicalEntityKind.Competition, "provider:competition:" + row.CompetitionReference, r.CompetitionId),
                            (CanonicalEntityKind.Season, FootballDataCsvParser.SeasonReference(row.CompetitionReference, row.SeasonReference), r.SeasonId),
                            (CanonicalEntityKind.Participant, row.HomeReference, r.HomeId), (CanonicalEntityKind.Participant, row.AwayReference, r.AwayId),
                            (CanonicalEntityKind.SportingEvent, row.MatchReference, r.EventId) })
                        {
                            var anchor = await QualityPersistence.Anchor(db, r.SourceId, pair.Item1, pair.Item2, token);
                            var decision = await QualityPersistence.Decision(db, anchor?.Id, knowledge, token);
                            if (decision?.Status != ResolutionStatus.Resolved || decision.CanonicalId != pair.Item3) reasons.Add("result_identity_context_mismatch");
                            else { decisions.Add(decision.Id); Freeze("identity-decision", decision.Id, decision); }
                        }
                    }
                }
                var binding = await db.FootballRawContexts.AsNoTracking().SingleAsync(x => x.RawId == r.RawId, token); Freeze("football-original-context", binding.RawId, binding);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { reasons.Add("result_raw_integrity_or_context"); }
            var quality = await db.QualityAssessments.AsNoTracking().Where(a => a.RawPayloadId == r.RawId && a.RecordReference == r.SourceEventReference &&
                a.AssessedAtUtc <= knowledge && a.RecordedAtUtc <= knowledge).OrderByDescending(a => a.RecordedAtUtc).ThenByDescending(a => a.Id).Take(1001).ToListAsync(token);
            if (quality.Count > 1000) throw new InvalidOperationException("Result quality history exceeds bound.");
            var assessments = quality.GroupBy(a => a.RuleId).Select(g => g.First()).ToArray();
            // A rejected simultaneous report is still known conflict evidence. Do not silently
            // keep labeling the earlier value until a later justified correction resolves it.
            var streamCheck = await db.QualityAssessments.AsNoTracking().Where(a => a.DataSourceId == r.SourceId &&
                a.RecordReference == r.SourceEventReference && a.RuleId == "result_correction" && a.ReasonCode == "result_conflicting_report" &&
                a.AssessedAtUtc <= knowledge && a.RecordedAtUtc <= knowledge)
                .OrderByDescending(a => a.RecordedAtUtc).ThenByDescending(a => a.Id).FirstOrDefaultAsync(token);
            if (streamCheck is not null && streamCheck.RecordedAtUtc > latest.Single(x => x.ProviderIdentityId == r.ProviderIdentityId).RecordedAtUtc)
            {
                reasons.Add("result_conflicting_report");
                assessments = assessments.Append(streamCheck).DistinctBy(a => a.Id).ToArray();
            }
            if (FootballResultRules.Catalog.Any(id => !assessments.Any(a => a.RuleId == id && a.RuleVersion == FootballResultRules.Version))) reasons.Add("result_quality_missing");
            foreach (var assessment in assessments) { Freeze("quality", assessment.Id, assessment); if (assessment.BlocksEligibility) reasons.Add(assessment.ReasonCode); }
            var dateGate = await gate.EvaluateAsync(new(r.DateObservationId, q.AsOfUtc, q.Purpose, q.Context, q.Mode, q.ReconstructionAtUtc), token);
            if (!dateGate.Eligible || dateGate.InterpretedTargetId != r.EventId) reasons.Add("result_date_or_identity_ineligible");
            if (r.SchemaVersion != 1 || !FootballResultRules.Assess(r.Value).All(x => x.Passed)) reasons.Add("result_invalid_value");
            if (r.CorrectsId is { } predecessor)
            {
                var previous = await db.FootballResults.AsNoTracking().SingleAsync(x => x.Id == predecessor, token);
                if (previous.ProviderIdentityId != r.ProviderIdentityId || previous.Version + 1 != r.Version || previous.SourceId != r.SourceId || previous.AvailableAtUtc > r.AvailableAtUtc ||
                    !FootballResultRules.Assess(r.Value, previous.Value, r.RetrievedAtUtc > previous.RetrievedAtUtc,
                        r.PublishedAtUtc is not null && r.PublishedAtUtc == previous.PublishedAtUtc).All(x => x.Passed)) reasons.Add("result_correction_chain_invalid");
            }
            if (latest.Any(other => other.EventId == r.EventId && other.SourceId != r.SourceId && other.Value != r.Value)) reasons.Add("result_cross_source_conflict");
            if (latest.Any(other => other.EventId == r.EventId && other.SourceId == r.SourceId && other.ProviderIdentityId != r.ProviderIdentityId && other.Value != r.Value)) reasons.Add("result_identity_stream_conflict");
            var eligible = reasons.Count == 0;
            output.Add(new(r, eligible, reasons.ToArray(), decisions.Order().ToArray(), assessments.Select(a => a.Id).Order().ToArray(), frozen.OrderBy(f => f.Kind, StringComparer.Ordinal).ThenBy(f => f.Id).ToArray(), eligible ? FootballOutcomes.Derive(r) : null));
        }
        return new(q, output);
    }
    private async Task Authorize(Guid source, DateTime retrieved, DataPurpose purpose, UsageContext context, DateTime knowledge,
        List<DatasetFrozenRecord>? frozen, CancellationToken token)
    {
        if (await sources.ReadAsync(source, token) != SourceOperationalStatus.Enabled) throw new UnauthorizedAccessException("Result source disabled.");
        var now = await QualityPersistence.Now(db, token);
        var purposes = new[] { purpose, DataPurpose.InternalAnalytics, DataPurpose.RawPayloadStorage, DataPurpose.HistoricalRetention }.Distinct().ToArray();
        var decisions = new Dictionary<DateTime, IReadOnlyDictionary<DataPurpose, PolicyEvaluation>>();
        foreach (var t in new[] { knowledge, now }.Distinct()) decisions.Add(t, await policies.EvaluateManyAsync(source, purposes, t, context, token));
        var policyRows = new Dictionary<Guid, SourcePolicy>();
        var auditRows = new Dictionary<(Guid, DateTime), PolicyAudit[]>();
        foreach (var p in purposes)
        {
            foreach (var t in new[] { knowledge, now }.Distinct())
            {
                var decision = decisions[t][p];
                if (!decision.Allowed || decision.Restrictions.Any(r => r.MaximumRetentionDays is { } days && now - retrieved > TimeSpan.FromDays(days))) throw new UnauthorizedAccessException("Result usage denied.");
                if (frozen is not null && t == knowledge && decision.PolicyId is { } id)
                {
                    if (!policyRows.TryGetValue(id, out var policy))
                    {
                        policy = await db.SourcePolicies.AsNoTracking().Include(x => x.Permissions).SingleAsync(x => x.Id == id, token); policyRows.Add(id, policy);
                    }
                    // Freeze the exact available approval/revocation ledger, not future audits.
                    if (!auditRows.TryGetValue((id, t), out var audits))
                    {
                        audits = await db.PolicyAudits.AsNoTracking().Where(x => x.SourcePolicyId == id && x.RecordedAtUtc <= t).OrderBy(x => x.Sequence).ToArrayAsync(token); auditRows.Add((id, t), audits);
                    }
                    var content = new { policy.Id, policy.DataSourceId, policy.Version, policy.EffectiveFromUtc, policy.EffectiveToUtc, policy.TermsReference, policy.EvidenceReference, policy.RecordedAtUtc, Permissions = policy.Permissions.OrderBy(x => x.Purpose).ToArray(), Audits = audits, Purpose = p, KnowledgeUtc = t == now ? (DateTime?)null : t };
                    // Current authorization is a gate, not nondeterministic build-time evidence.
                    if (t == knowledge) frozen.Add(new("result-policy:" + p, id, Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(content))));
                }
            }
        }
    }
    public async Task EnsureCurrentAsync(IEnumerable<FootballResultEvidence> evidence, DataPurpose purpose, UsageContext context, CancellationToken token = default)
    {
        foreach (var group in evidence.GroupBy(e => e.Observation.SourceId).OrderBy(g => g.Key))
        {
            await QualityPersistence.Lock(db, group.Key, token);
            await Authorize(group.Key, group.Min(e => e.Observation.RetrievedAtUtc), purpose, context, await QualityPersistence.Now(db, token), null, token);
        }
    }
}
