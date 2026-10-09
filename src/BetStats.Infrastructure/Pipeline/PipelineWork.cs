using System.Text.Json;
using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Application.Football;
using BetStats.Application.Ingestion;
using BetStats.Application.Pipeline;
using BetStats.Domain.Coverage;
using BetStats.Infrastructure.Evaluation;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Pipeline;

public sealed record LocalSynchronizationInput(FootballImportPlan Plan, string Path);
public sealed record PrematchInput(FootballResultDatasetRequest Dataset, BacktestDefinition Predictor);
public sealed record PostmatchInput(Guid PredictionExecutionId, Guid BacktestId, string BacktestHash);

public sealed class PipelineWork(BetStatsDbContext db, PostgreSqlPipeline jobs, FootballImportOperations imports,
    IResultDatasetOperations datasets, IFootballResultDatasets snapshots, IHistoricalBacktests backtests, IFootballIngestionPersistence ingestion)
{
    internal async Task AuthorizePublicationAsync(PipelineClaim claim, byte[] artifact, CancellationToken token)
    {
        if (claim.Definition.Kind == PipelineKind.LocalSynchronization)
        {
            var source = Input<LocalSynchronizationInput>(claim).Plan.SourceId;
            await QualityPersistence.Lock(db, source, token);
            await ingestion.EnsureCaptureAllowedAsync(source, token); await ingestion.EnsureParsingAllowedAsync(source, token); return;
        }
        using var document = JsonDocument.Parse(artifact); var result = document.RootElement.GetProperty("Result");
        BacktestManifest manifest;
        if (claim.Definition.Kind == PipelineKind.PrematchPrediction)
        {
            var id = result.GetProperty("published").GetProperty("SnapshotId").GetGuid();
            manifest = CanonicalDatasetJson.Deserialize<BacktestManifest>((await db.Backtests.AsNoTracking().SingleAsync(x => x.Id == id, token)).Content);
        }
        else manifest = CanonicalDatasetJson.Deserialize<BacktestManifest>(System.Text.Encoding.UTF8.GetBytes(result.GetProperty("Report").GetRawText()));
        try { await ((PostgreSqlBacktests)backtests).EnsurePipelinePublicationAsync(manifest, token); }
        catch (InvalidOperationException error) when (!PipelineOperatorCommand.InfrastructureFailure(error))
        { throw new InvalidDataException("publication_evidence_or_authorization_changed", error); }
    }
    private static T Input<T>(PipelineClaim claim) => JsonSerializer.Deserialize<T>(claim.Definition.PayloadJson, PipelineOperatorCommand.JsonOptions) ?? throw new ArgumentException("Pipeline payload required.");
    private static Guid Child(Guid execution, string stage) => PipelineOperationIds.Child(execution, stage);

    public async Task<(PipelineOutcome Outcome, byte[]? Artifact)> ExecuteAsync(PipelineClaim claim, PipelineApproval approval, CancellationToken token)
    {
        await jobs.CheckAsync(claim, token);
        object result;
        var artifactCutoff = claim.StartedUtc;
        switch (claim.Definition.Kind)
        {
            case PipelineKind.LocalSynchronization:
                var local = Input<LocalSynchronizationInput>(claim);
                if (!System.IO.Path.IsPathFullyQualified(local.Path)) throw new ArgumentException("Explicit absolute local input required.");
                var import = await imports.RunAsync(Child(claim.ExecutionId, "local-import"), local.Plan, local.Path, approval.Actor, approval.Reason, claim.Attempt > 1, token);
                if (import.Outcome is not (ImportOutcome.Succeeded or ImportOutcome.Reused))
                    return (new(import.Outcome == ImportOutcome.Failed ? PipelineState.Failed : import.Outcome == ImportOutcome.Interrupted ? PipelineState.Cancelled : PipelineState.Blocked,
                        import.Outcome == ImportOutcome.Partial ? "identity_review_or_source_conflict" : "local_input_unavailable"), null);
                result = new { Version = 1, Kind = "authorized-local-synchronization", import };
                break;
            case PipelineKind.PrematchPrediction:
                var predictionOperation = Child(claim.ExecutionId, "predictions");
                var previousPublication = await db.BacktestOperations.AsNoTracking().Where(x => x.OperationId == predictionOperation).OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(token);
                if (previousPublication is not null)
                {
                    if (previousPublication.Status != ResultOperationStatus.Succeeded || previousPublication.SnapshotId is not { } previousId)
                        return (new(PipelineState.Blocked, "new_job_required_for_unpublished_prediction"), null);
                    // A crash after child publication must reuse its immutable artifact, never run a second predictor.
                    var previous = await backtests.InspectAsync(previousId, token);
                    artifactCutoff = previous.Manifest.Predictions.Select(p => p.PredictionCutoffUtc).Distinct().Single();
                    var verified = await datasets.VerifyAsync(previous.Manifest.Definition.DatasetId, true, token);
                    if (!verified.CurrentlyAuthorized || !verified.Integrity || !verified.FeaturesReproducible || verified.RawAvailable != true || verified.RawHashVerified != true)
                        return (new(PipelineState.Blocked, "frozen_prediction_evidence_unavailable"), null);
                    var previousFeatures = await snapshots.InspectAsync(previous.Manifest.Definition.DatasetId, token);
                    if (!FrozenBeforeKickoff(previous, previousFeatures)) return (new(PipelineState.Blocked, "prediction_frozen_after_verified_kickoff"), null);
                    var replay = new ResultOperationResult(predictionOperation, ResultOperationStatus.Succeeded, previousPublication.Sequence, previous.Id, previous.Hash, null, previousPublication.Fingerprint);
                    result = new { Version = 1, Kind = "operational-prematch-publication", published = replay, DatasetId = previous.Manifest.Definition.DatasetId, DatasetHash = previous.Manifest.Definition.ExpectedDatasetHash };
                    break;
                }
                var input = Input<PrematchInput>(claim);
                if (input.Predictor.Version != 2 || input.Predictor.Model is null) throw new ArgumentException("Versioned BS-012 model required.");
                var d = input.Dataset.Metadata.Definition;
                // Bind the feature boundary to the actual database-clock acquisition, never to the scheduled time.
                d = d with { AsOfUtc = claim.StartedUtc, TargetTimePolicy = BetStats.Application.Coverage.PredictionTimeBoundary.SourceBoundKickoffV1,
                    Targets = d.Targets.Select(t => t with { PredictionCutoffUtc = claim.StartedUtc }).ToArray() };
                var request = input.Dataset with { Metadata = new(d, approval.Actor, approval.Reason), LabelAsOfUtc = claim.StartedUtc };
                var built = await datasets.BuildAsync(new(Child(claim.ExecutionId, "features-" + claim.Attempt), request, approval.Actor, approval.Reason, true), token);
                if (built.Status != ResultOperationStatus.Succeeded || built.SnapshotId is not { } datasetId) return (new(PipelineState.Blocked, "feature_dataset_unavailable"), null);
                var features = await snapshots.InspectAsync(datasetId, token);
                if (features.Manifest.Rows.Count == 0) return (new(PipelineState.Blocked, "eligible_prediction_input_missing"), null);
                var times = features.Manifest.MetadataManifest.Governance?.Rows.SelectMany(r => r.EventTimes).ToArray() ?? [];
                foreach (var row in features.Manifest.Rows)
                {
                    var time = times.Where(t => t.EventId == row.Metadata.EventId).ToArray();
                    if (time.Length != 1 || time[0].Resolution.Precision is not (EventTimePrecision.Minute or EventTimePrecision.Second) ||
                        time[0].Resolution.UtcInstant is not { } kickoff || kickoff <= claim.StartedUtc ||
                        kickoff.AddSeconds(-claim.Definition.PredictionHorizonSeconds) != claim.PlannedUtc ||
                        time[0].Evidence.AvailableAtUtc > claim.StartedUtc || time[0].Evidence.RecordedAtUtc > claim.StartedUtc)
                        return (new(PipelineState.Blocked, "source_bound_kickoff_or_horizon_unavailable"), null);
                }
                await jobs.CheckAsync(claim, token);
                var definition = input.Predictor with { DatasetId = datasetId, ExpectedDatasetHash = features.Hash, EvaluationCutoffUtc = claim.StartedUtc };
                var published = await backtests.RunAsync(new(predictionOperation, definition, approval.Actor, approval.Reason, true), token);
                if (published.Status != ResultOperationStatus.Succeeded) return (new(PipelineState.Blocked, "prediction_publication_unavailable"), null);
                if (!FrozenBeforeKickoff(await backtests.InspectAsync(published.SnapshotId!.Value, token), features))
                    return (new(PipelineState.Blocked, "prediction_frozen_after_verified_kickoff"), null);
                result = new { Version = 1, Kind = "operational-prematch-publication", published, DatasetId = datasetId, DatasetHash = features.Hash };
                break;
            case PipelineKind.PostmatchEvaluation:
                var evaluation = Input<PostmatchInput>(claim);
                var prior = await db.Set<PipelineArtifact>().AsNoTracking().SingleOrDefaultAsync(x => x.ExecutionId == evaluation.PredictionExecutionId, token);
                var priorExecution = await db.Set<PipelineExecution>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == evaluation.PredictionExecutionId, token);
                if (prior is null || priorExecution?.State != PipelineState.Completed || prior.RecordedUtc >= claim.StartedUtc)
                    return (new(PipelineState.Blocked, "previously_frozen_pipeline_prediction_required"), null);
                if (prior.Hash != CanonicalDatasetJson.Hash(prior.Content)) throw new InvalidDataException("Frozen pipeline artifact hash mismatch.");
                var frozen = await backtests.InspectAsync(evaluation.BacktestId, token);
                // The original execution output must bind exactly this immutable prediction artifact.
                using (var document = JsonDocument.Parse(prior.Content))
                {
                    var original = document.RootElement.GetProperty("Result");
                    if (original.GetProperty("Kind").GetString() != "operational-prematch-publication" ||
                        original.GetProperty("published").GetProperty("SnapshotId").GetGuid() != frozen.Id || frozen.Hash != evaluation.BacktestHash ||
                        original.GetProperty("published").GetProperty("Hash").GetString() != frozen.Hash)
                        throw new InvalidDataException("Frozen prediction binding mismatch.");
                }
                var originalFeatures = await snapshots.InspectAsync(frozen.Manifest.Definition.DatasetId, token);
                foreach (var row in originalFeatures.Manifest.MetadataManifest.Governance?.Rows ?? [])
                {
                    var kickoff = row.EventTimes.SingleOrDefault(t => t.EventId == row.EventId)?.Resolution.UtcInstant;
                    if (kickoff is null || frozen.RecordedAtUtc >= kickoff || kickoff >= claim.StartedUtc)
                        return (new(PipelineState.Blocked, "frozen_before_verified_kickoff_required"), null);
                }
                var evaluator = backtests as PostgreSqlBacktests ?? throw new InvalidOperationException("Configured governed evaluator required.");
                var report = await evaluator.EvaluateFrozenAsync(frozen, claim.StartedUtc, token);
                result = new { Version = 1, Kind = "postmatch-evaluation-of-frozen-predictions", OriginalId = frozen.Id, OriginalHash = frozen.Hash, Report = report };
                break;
            default: return (new(PipelineState.Blocked, "transport_disabled"), null);
        }
        await jobs.CheckAsync(claim, token);
        var bytes = CanonicalDatasetJson.Serialize(new { Version = 1, claim.ExecutionId, claim.PlannedUtc, ActualCutoffUtc = artifactCutoff, ActualExecutionUtc = claim.StartedUtc,
            LateExecution = claim.StartedUtc > claim.PlannedUtc, DefinitionHash = claim.Definition.Fingerprint, Result = result });
        return (new(PipelineState.Completed, "completed", claim.ExecutionId, CanonicalDatasetJson.Hash(bytes)), bytes);
    }
    private static bool FrozenBeforeKickoff(BacktestSnapshot frozen, FootballResultSnapshot features)
    {
        if (features.Manifest.Rows.Count == 0 || features.Manifest.MetadataManifest.Governance?.Rows.Count != features.Manifest.Rows.Count) return false;
        foreach (var row in features.Manifest.Rows)
        {
            var times = features.Manifest.MetadataManifest.Governance.Rows.Single(g => g.EventId == row.Metadata.EventId).EventTimes;
            if (times.Count != 1 || times[0].Resolution.UtcInstant is not { } kickoff || frozen.RecordedAtUtc >= kickoff) return false;
        }
        return true;
    }
}
