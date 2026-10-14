using System.Text.Json;
using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Application.Football;
using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Pipeline;
using BetStats.Application.Providers;
using BetStats.Application.Quality;
using BetStats.Domain.Governance;
using BetStats.Domain.Observations;
using BetStats.Infrastructure.Evaluation;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Pipeline;

public sealed class FootballValidation(BetStatsDbContext db, IRawPayloadStore store, ISourcePolicyEvaluator policies,
    ISourceOperationalStatus sources, IAnalyticalQualityGate gate, IHistoricalBacktests backtests,
    IFootballResultDatasets datasets) : IFootballValidationReports
{
    private async Task<ReadOnlyMemory<byte>> Raw(FootballValidationInput input, bool display, CancellationToken token)
    {
        input.Validate();
        if (await sources.ReadAsync(input.SourceId, token) != SourceOperationalStatus.Enabled)
            throw new UnauthorizedAccessException("Source unavailable.");
        var raw = await db.RawPayloads.AsNoTracking().SingleAsync(x => x.Id == input.RawId, token);
        if (raw.DataSourceId != input.SourceId || raw.ContentHashSha256 != input.ExpectedRawHash || raw.ByteLength is not (> 0 and <= 1048576) ||
            raw.ExternalReference != "fixture:" + input.Profile || await FootballContext.ReadAsync(db, raw, input.Scope, token) != input.Scope)
            throw new InvalidDataException("RAW profile, hash or scope mismatch.");
        var now = await QualityPersistence.Now(db, token);
        foreach (var purpose in new[] { DataPurpose.DataRetrieval, DataPurpose.RawPayloadStorage, DataPurpose.HistoricalRetention, DataPurpose.InternalAnalytics }
            .Concat(display ? [DataPurpose.PublicDisplay] : Array.Empty<DataPurpose>()))
        {
            var policy = await policies.EvaluateAsync(input.SourceId, purpose, now, new(PublicDisplay: display, IntendedRetentionDays: 1), token);
            if (!policy.Allowed || policy.Restrictions.Any(r => r.MaximumRetentionDays is { } days && now - raw.RetrievedAtUtc > TimeSpan.FromDays(days)))
                throw new UnauthorizedAccessException("Current validation rights denied.");
        }
        // The store verifies length and SHA256. No source text or storage path enters the report.
        return await store.ReadAsync(new(raw.StorageKey, input.ExpectedRawHash, raw.ByteLength!.Value), token);
    }

    public async Task<FootballValidationReport> BuildAsync(FootballValidationInput input, CancellationToken token = default)
    {
        var bytes = await Raw(input, false, token);
        IFootballMetadataParser parser = input.Profile switch
        {
            ApiFootballFixtureAdapter.Profile => new ApiFootballJsonParser(),
            HistoricalFootballCsvParser.Version => new HistoricalFootballCsvParser(),
            FootballResultsCsvParser.Version => new FootballResultsCsvParser(),
            FootballDataCsvParser.Version => new FootballDataCsvParser(),
            _ => throw new ArgumentException("Unsupported profile.")
        };
        var parsed = parser.Parse(bytes, input.Scope, token);
        if (parsed.ParsedCount > 500 || parsed.Records.Count > 500 || parsed.Issues.Count > 2000)
            throw new ArgumentException("Validation supports at most 500 rows and 2000 parse issues.");
        var now = await QualityPersistence.Now(db, token);
        var raw = await db.RawPayloads.AsNoTracking().SingleAsync(x => x.Id == input.RawId, token);
        var issues = parsed.Issues.GroupBy(i => i.Code).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var authorizations = new List<ValidationAuthorization>();
        foreach (var purpose in new[] { DataPurpose.DataRetrieval, DataPurpose.RawPayloadStorage, DataPurpose.HistoricalRetention, DataPurpose.InternalAnalytics })
        {
            var decision = await policies.EvaluateAsync(input.SourceId, purpose, now, new(IntendedRetentionDays: 1), token);
            if (!decision.Allowed || decision.PolicyId is not { } policy || decision.Version is not { } version)
                throw new UnauthorizedAccessException("Validation policy changed.");
            var restriction = decision.Restrictions.Single(r => r.Purpose == purpose);
            authorizations.Add(new(purpose, policy, version, restriction.MaximumRetentionDays, restriction.Attribution is not null));
        }
        void Issue(string code) => issues[code] = issues.GetValueOrDefault(code) + 1;
        var observations = await db.Observations.AsNoTracking().Where(o => o.RawPayloadId == input.RawId && o.Type == ObservationType.EventDate && o.RecordedAtUtc <= now && o.AvailableAtUtc <= now)
            .OrderBy(o => o.Id).Take(501).ToArrayAsync(token);
        if (observations.Length > 500) throw new InvalidDataException("RAW observation bound exceeded.");
        var eligible = 0;
        foreach (var observation in observations)
        {
            var quality = await gate.EvaluateAsync(new(observation.Id, now, DataPurpose.InternalAnalytics, new(IntendedRetentionDays: 1)), token);
            if (quality.Eligible) eligible++;
            foreach (var reason in quality.Reasons) Issue(reason);
        }
        var results = await db.FootballResults.AsNoTracking().Where(r => r.RawId == input.RawId && r.RecordedAtUtc <= now && r.AvailableAtUtc <= now).OrderBy(r => r.Id).Take(501).ToArrayAsync(token);
        if (results.Length > 500) throw new InvalidDataException("RAW result bound exceeded.");
        // Imported result availability never implies independently complete label coverage.
        var missing = parsed.Records.Count(r => r.Result is null || !FootballResultRules.LabelEligible(r.Result.Value));
        var models = new List<ValidationModel>();
        foreach (var reference in input.Publications.OrderBy(p => p.PredictionExecutionId))
            models.Add(await Model(input, reference, true, token));
        var limitations = new List<string> { "source_inventory_completeness_not_certified", input.Profile == ApiFootballFixtureAdapter.Profile ? "origin_and_chronology_require_independent_prospective_gates" : "historical_import_does_not_establish_pre_event_observations",
            "operator_origin_classification_is_not_independent_proof", "no_operational_accuracy_certification", "recovery_not_exercised_by_report_inspection",
            input.Profile == ApiFootballFixtureAdapter.Profile ? "provider_json_not_independent_coverage_evidence" : "kickoff_not_certified_by_csv_profile", "model_metrics_preserve_original_cohorts_no_ranking" };
        if (input.EvidenceKind != ValidationEvidenceKind.OperatorDeclaredReal) limitations.Add("real_data_validation_unavailable");
        if (input.EvidenceKind == ValidationEvidenceKind.Synthetic) limitations.Add("synthetic_software_test_only");
        if (models.Count == 0) limitations.Add("prediction_and_evaluation_not_executed");
        if (!models.Any(m => m.Targets.Any(t => t.Eligible > 0))) limitations.Add("no_eligible_evaluation_samples");
        return new(1, CanonicalDatasetJson.Fingerprint(input), input.SourceId, input.RawId, input.ExpectedRawHash, input.Profile, input.Scope,
            input.EvidenceKind, FootballValidationRules.Readiness(parsed.ParsedCount, observations.Length, eligible, issues.Values.Sum(), models.Count,
                models.Sum(m => m.Targets.Sum(t => t.Eligible))), now, raw.RetrievedAtUtc, raw.RecordedAtUtc, parsed.ParsedCount,
            observations.Length, eligible, missing, input.Profile == ApiFootballFixtureAdapter.Profile ? parsed.Issues.Count : parsed.Records.Count, issues.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new ValidationIssue(p.Key, p.Value)).ToArray(),
            models, limitations, results.Select(r => new ValidationObservation(r.Id, r.EventId, r.EventDate, r.PublishedAtUtc, r.RetrievedAtUtc, r.AvailableAtUtc, r.RecordedAtUtc)).ToArray(), authorizations);
    }

    private async Task<(PipelineExecution Execution, JsonElement Result, DateTime CutoffUtc)> Output(Guid id, string kind, CancellationToken token)
    {
        var execution = await db.Set<PipelineExecution>().AsNoTracking().SingleAsync(e => e.Id == id, token);
        var artifact = await db.Set<PipelineArtifact>().AsNoTracking().SingleAsync(a => a.ExecutionId == id, token);
        if (execution.State != PipelineState.Completed || artifact.Hash != CanonicalDatasetJson.Hash(artifact.Content))
            throw new InvalidDataException("Completed immutable pipeline output required.");
        using var doc = JsonDocument.Parse(artifact.Content);
        if (doc.RootElement.GetProperty("ExecutionId").GetGuid() != id) throw new InvalidDataException("Execution output binding mismatch.");
        var result = doc.RootElement.GetProperty("Result").Clone();
        if (result.GetProperty("Kind").GetString() != kind) throw new InvalidDataException("Unexpected pipeline output kind.");
        var cutoff = doc.RootElement.GetProperty("ActualCutoffUtc").GetDateTime();
        if (cutoff > execution.StartedUtc) throw new InvalidDataException("Pipeline cutoff exceeds execution time.");
        return (execution, result, cutoff);
    }

    private async Task<ValidationModel> Model(FootballValidationInput input, ValidationPublication reference, bool deep, CancellationToken token)
    {
        var (execution, result, actualCutoff) = await Output(reference.PredictionExecutionId, "operational-prematch-publication", token);
        var id = result.GetProperty("published").GetProperty("SnapshotId").GetGuid();
        var frozen = await backtests.InspectAsync(id, token);
        var m = frozen.Manifest;
        if (frozen.Hash != result.GetProperty("published").GetProperty("Hash").GetString() || m.Definition.Version != 2 || m.Definition.Model is null)
            throw new InvalidDataException("Versioned BS-012 frozen publication required.");
        var features = await datasets.InspectAsync(m.Definition.DatasetId, token);
        var definition = features.Manifest.MetadataManifest.Definition;
        if (definition.CompetitionReference != input.Scope.CompetitionReference || definition.SeasonReference != input.Scope.SeasonReference ||
            !features.Manifest.Rows.Any(r => r.Metadata.History.Append(r.Metadata.Target).Any(e => e.SourceId == input.SourceId && e.RawId == input.RawId) ||
                r.FeatureEvidence.Results.Any(e => e.Observation.SourceId == input.SourceId && e.Observation.RawId == input.RawId)))
            throw new InvalidDataException("Prediction is not bound to the declared dataset scope and RAW.");
        foreach (var p in m.Predictions)
        {
            BacktestRules.Validate(p.Value, p.Target);
            if (p.PredictionCutoffUtc != actualCutoff || p.FeatureDatasetId != features.Id || p.FeatureDatasetHash != features.Hash || p.Model is null)
                throw new InvalidDataException("Prediction model, cutoff or feature binding mismatch.");
        }
        if (deep)
        {
            var verified = await backtests.VerifyAsync(id, true, token);
            if (!verified.Integrity || !verified.Reproducible || !verified.CurrentlyAuthorized || verified.RawAvailable != true || verified.RawHashVerified != true)
                throw new InvalidDataException("Prediction deep verification unavailable.");
        }
        await CheckRaw(m.EvaluationEvidence, false, token);
        IReadOnlyList<TargetEvaluation> targets = []; DateTime? evaluationCutoff = null;
        if (reference.EvaluationExecutionId is { } evaluationId)
        {
            var (evaluated, output, _) = await Output(evaluationId, "postmatch-evaluation-of-frozen-predictions", token);
            var report = CanonicalDatasetJson.Deserialize<BacktestManifest>(System.Text.Encoding.UTF8.GetBytes(output.GetProperty("Report").GetRawText()));
            if (output.GetProperty("OriginalId").GetGuid() != id || output.GetProperty("OriginalHash").GetString() != frozen.Hash ||
                CanonicalDatasetJson.Fingerprint(report.Predictions) != CanonicalDatasetJson.Fingerprint(m.Predictions) ||
                report.Definition.EvaluationCutoffUtc != evaluated.StartedUtc || evaluated.StartedUtc <= frozen.RecordedAtUtc)
                throw new InvalidDataException("Frozen evaluation binding mismatch.");
            if (backtests is not PostgreSqlBacktests governed) throw new InvalidOperationException("Governed evaluation required.");
            if (deep)
            {
                var replay = new BacktestExecutor(new FrozenPredictionProvider(m)).Execute(report.Definition, features, report.EvaluationEvidence)
                    with { Authorizations = report.Authorizations, ExecutionKind = "frozen-prediction-postmatch-evaluation" };
                if (CanonicalDatasetJson.Fingerprint(replay) != CanonicalDatasetJson.Fingerprint(report))
                    throw new InvalidDataException("Evaluation metrics are not reproducible.");
            }
            // This checks frozen evidence and current permissions without running a predictor on reads.
            await using var tx = await db.Database.BeginTransactionAsync(token);
            await governed.EnsurePipelinePublicationAsync(report, token); await tx.CommitAsync(token);
            await CheckRaw(report.EvaluationEvidence, false, token);
            targets = report.Report.Targets; evaluationCutoff = evaluated.StartedUtc;
        }
        return new(reference.PredictionExecutionId, reference.EvaluationExecutionId, id, frozen.Hash, m.Definition.Predictor, m.Definition.PredictorVersion,
            features.Id, features.Hash, execution.PlannedUtc, actualCutoff, execution.StartedUtc, frozen.RecordedAtUtc, evaluationCutoff,
            m.Predictions.Count, m.Predictions.Count(p => p.Target is EvaluationTarget.FirstHalfGoalOccurrence or EvaluationTarget.FirstHalfTotalGoals ? p.Model?.HalfWarmed != true : p.Model?.Warmed != true), targets);
    }

    private async Task CheckRaw(FootballResultManifest manifest, bool display, CancellationToken token)
    {
        var metadata = manifest.MetadataManifest;
        var ids = metadata.Rows.SelectMany(r => r.History.Prepend(r.Target))
            .SelectMany(e => e.FrozenRecords.Where(f => f.Kind == "raw").Select(f => f.Id).Append(e.RawId))
            .Concat(metadata.Governance?.Rows.SelectMany(r => r.FrozenRecords).Where(f => f.Kind == "raw").Select(f => f.Id) ?? [])
            .Concat(metadata.Governance?.Rows.SelectMany(r => r.EventTimes).Select(t => t.Evidence.RawId) ?? [])
            .Concat(manifest.Rows.SelectMany(r => r.FeatureEvidence.Results.Concat(r.LabelEvidence.Results)).Select(e => e.Observation.RawId))
            .Concat(manifest.ResultGovernance?.Rows.SelectMany(r => r.FeatureCoverage.Items.Concat(r.LabelCoverage.Items)).Select(i => i.Evidence.RawId) ?? [])
            .Concat(manifest.ResultGovernance?.Rows.SelectMany(r => r.Ends).Select(e => e.Evidence.RawId) ?? []).Distinct().Order().ToArray();
        if (ids.Length is < 1 or > 20000) throw new InvalidDataException("Bounded complete RAW evidence required.");
        foreach (var id in ids)
        {
            var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == id, token);
            if (await sources.ReadAsync(raw.DataSourceId, token) != SourceOperationalStatus.Enabled) throw new UnauthorizedAccessException("Linked source unavailable.");
            var now = await QualityPersistence.Now(db, token);
            foreach (var purpose in new[] { DataPurpose.RawPayloadStorage, DataPurpose.HistoricalRetention, DataPurpose.InternalAnalytics }
                .Concat(display ? [DataPurpose.PublicDisplay] : Array.Empty<DataPurpose>()))
            {
                var policy = await policies.EvaluateAsync(raw.DataSourceId, purpose, now, new(PublicDisplay: display, IntendedRetentionDays: 1), token);
                if (!policy.Allowed || policy.Restrictions.Any(r => r.MaximumRetentionDays is { } days && now - raw.RetrievedAtUtc > TimeSpan.FromDays(days)))
                    throw new UnauthorizedAccessException("Linked RAW rights denied.");
            }
            if (raw.ByteLength is not { } length) throw new InvalidDataException("Linked RAW manifest missing.");
            _ = await store.ReadAsync(new(raw.StorageKey, raw.ContentHashSha256, length), token);
        }
    }

    public async Task AuthorizeAsync(FootballValidationInput input, CancellationToken token)
    {
        // Called within fenced pipeline completion's transaction. Raw() does not start a transaction.
        var manifests = new List<BacktestManifest>();
        foreach (var reference in input.Publications)
        {
            var (_, output, _) = await Output(reference.PredictionExecutionId, "operational-prematch-publication", token);
            var id = output.GetProperty("published").GetProperty("SnapshotId").GetGuid();
            var frozen = await db.Backtests.AsNoTracking().SingleAsync(a => a.Id == id, token);
            if (frozen.Hash != CanonicalDatasetJson.Hash(frozen.Content)) throw new InvalidDataException("Publication changed.");
            manifests.Add(CanonicalDatasetJson.Deserialize<BacktestManifest>(frozen.Content));
            if (reference.EvaluationExecutionId is { } evaluation)
            {
                var (_, result, _) = await Output(evaluation, "postmatch-evaluation-of-frozen-predictions", token);
                var manifest = CanonicalDatasetJson.Deserialize<BacktestManifest>(System.Text.Encoding.UTF8.GetBytes(result.GetProperty("Report").GetRawText()));
                manifests.Add(manifest);
            }
        }
        foreach (var source in manifests.SelectMany(m => m.Authorizations ?? []).Select(a => a.SourceId).Append(input.SourceId).Distinct().Order())
            await QualityPersistence.Lock(db, source, token);
        _ = await Raw(input, false, token);
        foreach (var manifest in manifests)
        {
            await ((PostgreSqlBacktests)backtests).EnsurePipelinePublicationAsync(manifest, token);
            await CheckRaw(manifest.EvaluationEvidence, false, token);
        }
    }

    public async Task<FootballValidationArtifact> ReadAsync(Guid executionId, bool display, CancellationToken token = default)
    {
        if (executionId == Guid.Empty) throw new ArgumentException("Execution UUID required.");
        var (execution, result, _) = await Output(executionId, "football-validation-v1", token);
        var version = await db.Set<PipelineJobVersion>().AsNoTracking().SingleAsync(v => v.JobId == execution.JobId && v.Version == execution.DefinitionVersion, token);
        if (version.Hash != CanonicalDatasetJson.Hash(version.Content)) throw new InvalidDataException("Definition integrity failure.");
        var definition = CanonicalDatasetJson.Deserialize<PipelineDefinition>(version.Content);
        var input = JsonSerializer.Deserialize<FootballValidationInput>(definition.PayloadJson, PipelineOperatorCommand.JsonOptions) ?? throw new InvalidDataException("Missing input.");
        _ = await Raw(input, display, token);
        var report = CanonicalDatasetJson.Deserialize<FootballValidationReport>(System.Text.Encoding.UTF8.GetBytes(result.GetProperty("Report").GetRawText()));
        if (report.Version != 1 || report.InputHash != CanonicalDatasetJson.Fingerprint(input)) throw new InvalidDataException("Report input binding mismatch.");
        foreach (var reference in input.Publications)
        {
            var model = await Model(input, reference, false, token);
            if (display)
            {
                var frozen = await backtests.InspectAsync(model.BacktestId, token);
                await CheckRaw(frozen.Manifest.EvaluationEvidence, true, token);
                var authorizations = frozen.Manifest.Authorizations ?? [];
                if (reference.EvaluationExecutionId is { } evaluation)
                {
                    var (_, evaluated, _) = await Output(evaluation, "postmatch-evaluation-of-frozen-predictions", token);
                    var m = CanonicalDatasetJson.Deserialize<BacktestManifest>(System.Text.Encoding.UTF8.GetBytes(evaluated.GetProperty("Report").GetRawText()));
                    await CheckRaw(m.EvaluationEvidence, true, token);
                    authorizations = authorizations.Concat(m.Authorizations ?? []).ToArray();
                }
                foreach (var source in authorizations.Select(a => a.SourceId).Distinct().Order())
                {
                    var policy = await policies.EvaluateAsync(source, DataPurpose.PublicDisplay, await QualityPersistence.Now(db, token), new(PublicDisplay: true, IntendedRetentionDays: 1), token);
                    if (!policy.Allowed) throw new UnauthorizedAccessException("Linked evidence display denied.");
                }
            }
        }
        var artifact = await db.Set<PipelineArtifact>().AsNoTracking().SingleAsync(a => a.ExecutionId == executionId, token);
        return new(executionId, artifact.Hash, report);
    }
}
