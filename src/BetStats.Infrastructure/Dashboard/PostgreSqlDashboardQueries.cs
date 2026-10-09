using BetStats.Application.Dashboard;
using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Application.Football;
using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Models;
using BetStats.Domain.Governance;
using BetStats.Domain.Football;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using BetStats.Infrastructure.Quality;

namespace BetStats.Infrastructure.Dashboard;

public sealed class PostgreSqlDashboardQueries(BetStatsDbContext db, IDatasets datasets, IHistoricalBacktests backtests,
    ISourcePolicyEvaluator policies, IRawPayloadStore rawStore, IFootballResults results, IFootballResultDatasets resultDatasets) : IDashboardQueries
{
    // Scope filtering stays in PostgreSQL; only bounded page artifacts are materialized.
    private IQueryable<Guid> DatasetIds(DashboardFilter f) =>
        (from feature in db.DatasetFeatures.FromSqlInterpolated($"""
             SELECT * FROM datasets."Features"
             WHERE ({f.From}::date IS NULL OR (convert_from("Content",'UTF8')::jsonb->'Target'->>'EventDate')::date >= {f.From})
               AND ({f.To}::date IS NULL OR (convert_from("Content",'UTF8')::jsonb->'Target'->>'EventDate')::date <= {f.To})
               AND ({f.Status}::text IS NULL OR coalesce(convert_from("Content",'UTF8')::jsonb->'Target'->>'Status','Unknown')={f.Status})
             """).AsNoTracking()
         join ev in db.SportingEvents.AsNoTracking() on feature.EventId equals ev.Id
         where (f.Competition == null || ev.CompetitionId == f.Competition) && (f.Season == null || ev.SeasonId == f.Season) &&
             (f.Team == null || db.EventParticipants.Any(p => p.EventId == ev.Id && p.ParticipantId == f.Team))
         select feature.DatasetId).Distinct();

    private async Task AuthorizeRaw(IEnumerable<Guid> ids, CancellationToken token)
    {
        var keys = ids.Distinct().ToArray();
        if (keys.Length is < 1 or > 20000) throw new InvalidDataException("Missing or excessive evidence.");
        var raw = await db.RawPayloads.AsNoTracking().Where(r => keys.Contains(r.Id)).ToListAsync(token);
        if (raw.Count != keys.Length) throw new InvalidDataException("Missing historical RAW evidence.");
        var now = await QualityPersistence.Now(db, token);
        foreach (var source in raw.GroupBy(r => r.DataSourceId))
        {
            foreach (var purpose in new[] { DataPurpose.PublicDisplay, DataPurpose.InternalAnalytics, DataPurpose.HistoricalRetention, DataPurpose.RawPayloadStorage })
            {
                // Attribution restrictions fail closed: the MVP does not claim attribution it cannot render safely.
                var decision = await policies.EvaluateAsync(source.Key, purpose, now, new(PublicDisplay: true, IntendedRetentionDays: 1), token);
                if (!decision.Allowed || decision.Restrictions.Any(r => r.MaximumRetentionDays is { } days && source.Any(s => now - s.RetrievedAtUtc > TimeSpan.FromDays(days))))
                    throw new UnauthorizedAccessException("Display or retention permission denied.");
            }
        }
        foreach (var item in raw)
        {
            var bytes = await rawStore.ReadAsync(new(item.StorageKey, item.ContentHashSha256, item.ByteLength ?? throw new InvalidDataException("Missing RAW length.")), token);
            if (CanonicalDatasetJson.Hash(bytes.ToArray()) != item.ContentHashSha256) throw new InvalidDataException("RAW hash mismatch.");
        }
    }
    private static IEnumerable<Guid> RawIds(DatasetManifest m) => m.Rows.SelectMany(r => r.History.Prepend(r.Target))
        .SelectMany(e => e.FrozenRecords.Where(f => f.Kind == "raw").Select(f => f.Id).Append(e.RawId))
        .Concat(m.Governance?.Rows.SelectMany(r => r.FrozenRecords).Where(f => f.Kind == "raw").Select(f => f.Id) ?? [])
        .Concat(m.Governance?.Rows.SelectMany(r => r.EventTimes).Select(t => t.Evidence.RawId) ?? []);

    private sealed record FixtureKey(Guid EventId, Guid DatasetId);
    private async Task<FixtureKey[]> FixtureKeys(DashboardFilter f, CancellationToken token)
    {
        f.Validate();
        // JSON is immutable canonical content. All values are parameters; SQL selects only page keys.
        return await db.Database.SqlQuery<FixtureKey>($"""
            SELECT "EventId", "DatasetId" FROM (
              SELECT DISTINCT ON ((r->>'EventId')::uuid)
                (r->>'EventId')::uuid AS "EventId", s."Id" AS "DatasetId",
                (r->'Target'->>'EventDate')::date AS date, r->'Target'->>'Status' AS status,
                (r->'Target'->>'HomeId')::uuid AS home, (r->'Target'->>'AwayId')::uuid AS away,
                (m->'Definition'->>'CompetitionId')::uuid AS competition,
                (m->'Definition'->>'SeasonId')::uuid AS season
              FROM datasets."Snapshots" s
              CROSS JOIN LATERAL (SELECT convert_from(s."Content", 'UTF8')::jsonb AS m) content
              CROSS JOIN LATERAL jsonb_array_elements(m->'Rows') r
              ORDER BY (r->>'EventId')::uuid, (r->>'PredictionCutoffUtc')::timestamptz DESC, s."RecordedAtUtc" DESC, s."Id"
            ) latest
            WHERE ({f.Competition}::uuid IS NULL OR competition = {f.Competition})
              AND ({f.Season}::uuid IS NULL OR season = {f.Season})
              AND ({f.Team}::uuid IS NULL OR home = {f.Team} OR away = {f.Team})
              AND ({f.From}::date IS NULL OR date >= {f.From}) AND ({f.To}::date IS NULL OR date <= {f.To})
              AND ({f.Status}::text IS NULL OR coalesce(status, 'Unknown') = {f.Status})
            ORDER BY date, "EventId" OFFSET {f.Offset} LIMIT {f.Limit + 1}
            """).ToArrayAsync(token);
    }

    public async Task<DashboardPage<DashboardFixture>> FixturesAsync(DashboardFilter f, CancellationToken token = default)
    {
        var keys = await FixtureKeys(f, token);
        var snapshots = new List<DatasetSnapshot>();
        foreach (var id in keys.Take(f.Limit).Select(k => k.DatasetId).Distinct())
        {
            DatasetSnapshot s;
            try { s = await datasets.InspectAsync(id, token); }
            catch (Exception e) when (Datasets.PostgreSqlDatasets.IsPermissionDenial(e)) { throw new UnauthorizedAccessException("Display denied."); }
            if (CanonicalDatasetJson.Fingerprint(s.Manifest) != s.ManifestHash) throw new InvalidDataException("Canonical dataset integrity failed.");
            await AuthorizeRaw(RawIds(s.Manifest), token); snapshots.Add(s);
        }
        var rows = snapshots.SelectMany(s => s.Manifest.Rows.Select(r => new DashboardFixture(r.EventId, s.Id, s.ManifestHash,
            s.Manifest.Definition.CompetitionId, s.Manifest.Definition.SeasonId, r.Target.HomeId, r.Target.AwayId, r.Target.EventDate,
            r.Target.Status is "Scheduled" or "Postponed" or "Completed" or "Cancelled" or "InProgress" ? r.Target.Status : "Unknown",
            null, null, r.PredictionCutoffUtc, r.History.Count, r.Excluded.Count, "stored_integrity_verified")))
            .Where(r => keys.Take(f.Limit).Any(k => k.EventId == r.EventId && k.DatasetId == r.DatasetId) && (f.Competition == null || r.CompetitionId == f.Competition) && (f.Season == null || r.SeasonId == f.Season) &&
                (f.Team == null || r.HomeId == f.Team || r.AwayId == f.Team) && (f.From == null || r.Date >= f.From) && (f.To == null || r.Date <= f.To))
            .GroupBy(r => r.EventId).Select(g => g.OrderByDescending(r => r.CutoffUtc).ThenBy(r => r.DatasetId).First())
            .OrderBy(r => r.Date).ThenBy(r => r.EventId).ToArray();
        var projected = new List<DashboardFixture>();
        foreach (var r in rows)
        {
            var now = await QualityPersistence.Now(db, token);
            var evidence = await results.ReadAsync(new(r.CompetitionId, r.SeasonId, now, DataPurpose.InternalAnalytics, new(PublicDisplay: true, IntendedRetentionDays: 1), EventId: r.EventId), token);
            var eligible = evidence.Results.Where(e => e.Eligible).ToArray();
            if (eligible.Length > 0) await AuthorizeRaw(eligible.Select(e => e.Observation.RawId), token);
            var values = eligible.Select(e => e.Observation.Value).Distinct().ToArray();
            var conflict = values.Length > 1 || evidence.Results.Any(e => e.Reasons.Any(reason => reason.Contains("conflict", StringComparison.OrdinalIgnoreCase)));
            var value = values.Length == 1 ? values[0] : null;
            projected.Add(conflict ? r with { Status = "Conflict", HomeScore = null, AwayScore = null } :
                value is { Status: FootballMatchStatus.Finished, Basis: FootballScoreBasis.RegulationTime } ? r with { Status = "Completed", HomeScore = value.FullTime.Home, AwayScore = value.FullTime.Away } : r);
        }
        return new(projected, f.Offset, f.Limit, keys.Length > f.Limit);
    }
    private sealed record ChoiceKey(Guid Id, Guid DatasetId, Guid? Parent);
    public async Task<DashboardPage<DashboardChoice>> ChoicesAsync(string kind, DashboardFilter f, CancellationToken token = default)
    {
        f.Validate(); if (kind is not ("competitions" or "seasons" or "teams")) throw new ArgumentException("Unknown catalog.");
        var keys = await db.Database.SqlQuery<ChoiceKey>($"""
            SELECT "Id", "DatasetId", "Parent" FROM (
              SELECT DISTINCT ON (choice.id) choice.id AS "Id", s."Id" AS "DatasetId",
                CASE WHEN {kind}='seasons' THEN (m->'Definition'->>'CompetitionId')::uuid ELSE NULL END AS "Parent"
              FROM datasets."Snapshots" s
              CROSS JOIN LATERAL (SELECT convert_from(s."Content", 'UTF8')::jsonb AS m) content
              CROSS JOIN LATERAL jsonb_array_elements(m->'Rows') r
              CROSS JOIN LATERAL (
                SELECT CASE {kind} WHEN 'competitions' THEN (m->'Definition'->>'CompetitionId')::uuid
                  WHEN 'seasons' THEN (m->'Definition'->>'SeasonId')::uuid ELSE (r->'Target'->>'HomeId')::uuid END AS id
                UNION SELECT (r->'Target'->>'AwayId')::uuid WHERE {kind}='teams'
              ) choice
              WHERE ({f.Competition}::uuid IS NULL OR (m->'Definition'->>'CompetitionId')::uuid={f.Competition})
                AND ({f.Season}::uuid IS NULL OR (m->'Definition'->>'SeasonId')::uuid={f.Season})
              ORDER BY choice.id, s."RecordedAtUtc" DESC, s."Id"
            ) choices ORDER BY "Id" OFFSET {f.Offset} LIMIT {f.Limit + 1}
            """).ToArrayAsync(token);
        var choices = new List<DashboardChoice>();
        foreach (var key in keys.Take(f.Limit))
        {
            DatasetSnapshot s;
            try { s = await datasets.InspectAsync(key.DatasetId, token); }
            catch (Exception e) when (Datasets.PostgreSqlDatasets.IsPermissionDenial(e)) { throw new UnauthorizedAccessException("Display denied."); }
            if (CanonicalDatasetJson.Fingerprint(s.Manifest) != s.ManifestHash) throw new InvalidDataException("Dataset integrity failed.");
            await AuthorizeRaw(RawIds(s.Manifest), token);
            var name = kind switch
            {
                "competitions" => s.Manifest.Definition.CompetitionReference,
                "seasons" => s.Manifest.Definition.SeasonReference,
                _ => await db.Participants.Where(p => p.Id == key.Id).Select(p => p.Name).SingleAsync(token)
            };
            choices.Add(new(key.Id, name, key.Parent));
        }
        return new(choices, f.Offset, f.Limit, keys.Length > f.Limit);
    }
    public async Task<DashboardPage<DashboardBacktest>> BacktestsAsync(DashboardFilter f, CancellationToken token = default)
    {
        f.Validate(); var metadataIds = DatasetIds(f);
        var resultIds = db.FootballResultArtifacts.Where(a => metadataIds.Contains(a.MetadataSnapshotId)).Select(a => a.Id);
        var ids = await db.Backtests.AsNoTracking().Where(a => resultIds.Contains(a.DatasetId))
            .OrderByDescending(a => a.RecordedAtUtc).ThenBy(a => a.Id).Skip(f.Offset).Take(f.Limit + 1).Select(a => a.Id).ToArrayAsync(token);
        var items = new List<DashboardBacktest>();
        foreach (var id in ids.Take(f.Limit))
        {
            var snapshot = await backtests.InspectAsync(id, token); var m = snapshot.Manifest;
            m.Definition.Validate();
            var a = await db.Backtests.AsNoTracking().SingleAsync(a => a.Id == id, token);
            if (CanonicalDatasetJson.Fingerprint(m) != a.Hash || !await db.BacktestOperations.AnyAsync(o => o.SnapshotId == id && o.Status == ResultOperationStatus.Succeeded, token))
                throw new InvalidDataException("Finalized backtest integrity failed.");
            var featureDataset = await resultDatasets.InspectAsync(a.DatasetId, token);
            if (featureDataset.Hash != m.Definition.ExpectedDatasetHash || CanonicalDatasetJson.Fingerprint(featureDataset.Manifest) != featureDataset.Hash)
                throw new InvalidDataException("Frozen feature dataset binding failed.");
            foreach (var authorization in m.Authorizations ?? throw new InvalidDataException("Missing frozen authorization."))
                if (authorization.AuditIds.Count == 0 || !await db.SourcePolicies.AnyAsync(p => p.Id == authorization.PolicyId && p.DataSourceId == authorization.SourceId && p.Version == authorization.PolicyVersion, token) ||
                    await db.PolicyAudits.CountAsync(p => p.SourcePolicyId == authorization.PolicyId && authorization.AuditIds.Contains(p.Id), token) != authorization.AuditIds.Distinct().Count())
                    throw new InvalidDataException("Missing frozen policy audit.");
            await AuthorizeRaw(RawIds(m.EvaluationEvidence.MetadataManifest)
                .Concat(m.EvaluationEvidence.Rows.SelectMany(r => r.FeatureEvidence.Results.Concat(r.LabelEvidence.Results)).Select(e => e.Observation.RawId))
                .Concat(m.EvaluationEvidence.ResultGovernance?.Rows.SelectMany(r => r.FeatureCoverage.Items.Concat(r.LabelCoverage.Items)).Select(i => i.Evidence.RawId) ?? [])
                .Concat(m.EvaluationEvidence.ResultGovernance?.Rows.SelectMany(r => r.Ends).Select(e => e.Evidence.RawId) ?? []), token);
            foreach (var p in m.Predictions)
            {
                BacktestRules.Validate(p.Value, p.Target);
                if (p.FeatureDatasetId != a.DatasetId || p.FeatureDatasetHash != featureDataset.Hash ||
                    !featureDataset.Manifest.Rows.Any(r => r.Metadata.EventId == p.EventId && r.Metadata.PredictionCutoffUtc == p.PredictionCutoffUtc && r.FeatureHash == p.FeatureHash))
                    throw new InvalidDataException("Prediction feature binding failed.");
                if (p.Model is { } model)
                {
                    if (m.Definition.Model is null || model.DefinitionHash != CanonicalDatasetJson.Fingerprint(m.Definition.Model) ||
                        CanonicalDatasetJson.Fingerprint(model.Definition) != model.DefinitionHash || model.InputHash != p.InputHash ||
                        p.Predictor != m.Definition.Predictor || p.PredictorVersion != m.Definition.PredictorVersion)
                        throw new InvalidDataException("Frozen model definition binding failed.");
                    var forecast = m.ModelForecasts?.SingleOrDefault(v => v.EventId == p.EventId && v.CutoffUtc == p.PredictionCutoffUtc);
                    if (forecast is null || CanonicalDatasetJson.Fingerprint(PredictionModelProvenance.From(forecast.Provenance)) != CanonicalDatasetJson.Fingerprint(model))
                        throw new InvalidDataException("Frozen distribution binding failed.");
                }
            }
            var selectedEvents = m.EvaluationEvidence.MetadataManifest.Rows.Where(r => (f.Team is null || r.Target.HomeId == f.Team || r.Target.AwayId == f.Team) &&
                (f.From is null || r.Target.EventDate >= f.From) && (f.To is null || r.Target.EventDate <= f.To) && (f.Status is null || r.Target.Status == f.Status)).Select(r => r.EventId).ToHashSet();
            var predictions = m.Predictions.Where(p => selectedEvents.Contains(p.EventId)).Select(p => new DashboardPrediction(p.EventId, p.PredictionCutoffUtc,
                p.Predictor, p.PredictorVersion, p.FeatureHash, p.InputHash, p.Target.ToString(), p.Value.Probabilities, p.Value.ExpectedCount, p.EvidenceIds.Count, p.Model?.Warmed, p.Model?.HalfWarmed)).ToArray();
            items.Add(new(id, a.Hash, a.DatasetId, m.Definition.ExpectedDatasetHash, m.Definition.EvaluationCutoffUtc, m.ExecutionKind,
                m.Definition.Predictor, m.Definition.PredictorVersion, m.Definition.Model, predictions,
                m.Report.Targets.SelectMany(t => t.Metrics.Select(v => new DashboardMetric(t.Target.ToString(), t.Requested, t.Eligible, t.Excluded,
                    v.Name, v.Version, v.Denominator, v.Value, v.PositiveInfinity, v.MinimumSamplesMet, v.Calibration))).ToArray(),
                m.ModelForecasts?.Where(v=>selectedEvents.Contains(v.EventId)).ToArray() ?? [], m.Report.Targets.SelectMany(t => t.ExclusionReasons).GroupBy(v => v.Key).ToDictionary(g => g.Key, g => g.Sum(v => v.Value)),
                m.Report.Targets.SelectMany(t => t.Calibration).ToArray(), "stored_integrity_verified",
                CanonicalDatasetJson.Fingerprint(new { m.Definition.DatasetId, m.Definition.ExpectedDatasetHash, m.Definition.EvaluationCutoffUtc, m.Definition.Evaluations, m.EvaluationEvidence }),
                m.Samples.GroupBy(s => s.Target.ToString()).ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Where(s => s.Eligible).Select(s => s.EventId).Distinct().Order().ToArray())));
        }
        return new(items, f.Offset, f.Limit, ids.Length > f.Limit);
    }
    public async Task<DashboardQuality> QualityAsync(DashboardFilter f, CancellationToken token = default)
    {
        var rows = await FixturesAsync(f with { Offset = 0, Limit = 100 }, token);
        return new("stored_integrity_verified", true, rows.Items.Count, rows.Items.Sum(r => r.ExcludedCount), rows.Items.Count(r => r.HistoryCount == 0),
            rows.Items.Count, rows.Items.Count(r => r.Status == "Unknown"), rows.Items.Count(r => r.Status == "Conflict"), "snapshot_only_not_certified",
            ["first_100_filtered_fixtures", "readiness_not_certified_by_snapshot", "date_only_kickoff_unknown", "no_live_model_replay", "identity_and_correction_inventory_not_certified"]);
    }
}
