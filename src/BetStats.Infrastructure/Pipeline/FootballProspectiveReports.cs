using System.Text.Json;
using BetStats.Application.Coverage;
using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Application.Pipeline;
using BetStats.Application.Providers;
using BetStats.Domain.Governance;
using BetStats.Domain.Quality;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Pipeline;

public sealed class FootballProspectiveReports(BetStatsDbContext db, FootballAcquisition acquisition, IFootballValidationReports validations, FootballValidation validationService,
    IHistoricalBacktests backtests, IHistoricalCoverage coverage) : IFootballAcquisitionReports
{
    public Task<AcquisitionArtifact> AcquisitionAsync(Guid execution, bool display, CancellationToken token = default) => acquisition.ReadAsync(execution, display, token);
    public Task<AcquisitionJobStatus> StatusAsync(Guid jobId, bool display, CancellationToken token = default) => acquisition.StatusAsync(jobId, display, token);
    public async Task<ProspectiveCohortReport> BuildAsync(ProspectiveCohortInput input, CancellationToken token)
    {
        input.Validate(); var acquisitions = new List<AcquisitionArtifact>();
        foreach (var id in input.AcquisitionExecutions.Order())
        {
            var a = await acquisition.ReadAsync(id, false, token);
            if (a.Report.Capability.SourceId != input.SourceId) throw new InvalidDataException("Cross-source cohort denied."); acquisitions.Add(a);
        }
        var at = await QualityPersistence.Now(db, token);
        var observed = acquisitions.SelectMany(a => a.Report.Observations).Where(o => o.EventId != Guid.Empty).GroupBy(o => o.EventId).ToArray();
        var validation = new List<FootballValidationArtifact>(); var frozen = new HashSet<Guid>(); var eligible = new HashSet<Guid>();
        foreach (var id in input.ValidationExecutions.Order())
        {
            var v = await validations.ReadAsync(id, false, token);
            if (v.Report.SourceId != input.SourceId || !acquisitions.Any(a => a.Report.EvidenceRaws.Any(r => r.Id == v.Report.RawId)))
                throw new InvalidDataException("Validation must bind acquired RAW in the same source.");
            var kinds = acquisitions.Select(a => a.Report.EvidenceKind).Distinct().ToArray();
            if (kinds.Length != 1 || kinds[0] != v.Report.EvidenceKind) throw new InvalidDataException("Ambiguous cohort origin labels.");
            validation.Add(v);
            foreach (var model in v.Report.Models)
            {
                var snapshot = await backtests.InspectAsync(model.BacktestId, token);
                foreach (var p in snapshot.Manifest.Predictions)
                {
                    frozen.Add(p.EventId);
                    if (p.Model?.Warmed == true && (p.Target is not (EvaluationTarget.FirstHalfGoalOccurrence or EvaluationTarget.FirstHalfTotalGoals) || p.Model.HalfWarmed) &&
                        observed.Any(g => g.Key == p.EventId && g.Any(o => ProspectiveEvidenceRules.KnownBefore(o, p.PredictionCutoffUtc)))) eligible.Add(p.EventId);
                }
            }
        }
        var statuses = new List<ProspectiveCoverage>();
        foreach (var query in input.CoverageQueries)
        {
            if (!acquisitions.Any(a => a.Report.Scope.CompetitionReference == query.Scope.CompetitionReference && a.Report.Scope.SeasonReference == query.Scope.SeasonReference) ||
                query.Mode != DatasetMode.HistoricalAsKnown || query.ReconstructionUtc is not null || query.Purpose != DataPurpose.InternalAnalytics || query.Context != new BetStats.Application.Governance.UsageContext())
                throw new ArgumentException("Exact acquired scope and historical coverage semantics required.");
            var report = await coverage.ReportAsync(query with { AsOfUtc = at }, token);
            if (!report.Authorized) throw new UnauthorizedAccessException("Coverage permission unavailable.");
            var synthetic = acquisitions.All(a => a.Report.EvidenceKind == ValidationEvidenceKind.Synthetic);
            var raws = new List<AcquisitionRaw>();
            foreach (var id in report.Items.Select(i => i.Evidence.RawId).Distinct().Order()) raws.Add(await acquisition.EvidenceAsync(id, acquisitions[0].Report.Capability, false, token));
            statuses.Add(new(synthetic ? report.Status.ToString() : "Unknown", query.Scope.ObservationType.ToString(),
                synthetic ? report.Reasons : ["independent_real_inventory_contract_unavailable"], query.Scope,
                report.Items.Select(i => i.Evidence.Id).Order().ToArray(), report.Items.Where(i => i.Review is not null).Select(i => i.Review!.Id).Order().ToArray(), raws,
                report.Items.Count == 0 ? null : report.Items.Min(i => i.Evidence.ValidUntilUtc)));
        }
        if (statuses.Count == 0) statuses.Add(new("Unknown", "metadata-and-results", ["independent_coverage_evidence_required"]));
        var models = validation.SelectMany(v => v.Report.Models).DistinctBy(m => new { m.PredictionExecutionId, m.EvaluationExecutionId }).ToArray();
        return new(1, CanonicalDatasetJson.Fingerprint(input), input.SourceId, at,
            eligible.Count > 0 && statuses.All(s => s.Status is "VerifiedComplete" or "VerifiedEmpty") ? AcquisitionState.Verified : AcquisitionState.Partial,
            observed.Length, observed.Count(g => g.Any(o => ProspectiveEvidenceRules.KnownBefore(o, at))), frozen.Count, eligible.Count,
            observed.Count(g => g.Any(o => o.FinalResultEligible)), models.Sum(m => m.Targets.Sum(t => t.Eligible)), models.Sum(m => m.WarmupExclusions),
            acquisitions, validation, statuses, ["independent_source_verification_unavailable", "no_operational_accuracy_certification",
                "original_evaluation_cohorts_preserved_no_model_ranking", "only_existing_pipeline_can_publish_predictions"]);
    }
    public async Task AuthorizeAsync(ProspectiveCohortInput input, ProspectiveCohortReport report, CancellationToken token)
    {
        await QualityPersistence.Lock(db, input.SourceId, token);
        // Prepublication calls use immutable receipts and do not recalculate eligibility or metrics.
        foreach (var id in input.AcquisitionExecutions) _ = await acquisition.ReadAsync(id, false, token);
        foreach (var id in input.ValidationExecutions)
        {
            var execution = await db.Set<PipelineExecution>().AsNoTracking().SingleAsync(e => e.Id == id, token);
            var version = await db.Set<PipelineJobVersion>().AsNoTracking().SingleAsync(v => v.JobId == execution.JobId && v.Version == execution.DefinitionVersion, token);
            if (version.Hash != CanonicalDatasetJson.Hash(version.Content)) throw new InvalidDataException("Validation definition corrupted.");
            var definition = CanonicalDatasetJson.Deserialize<PipelineDefinition>(version.Content);
            var validationInput = JsonSerializer.Deserialize<FootballValidationInput>(definition.PayloadJson, PipelineOperatorCommand.JsonOptions) ?? throw new InvalidDataException("Validation input missing.");
            await validationService.AuthorizeAsync(validationInput, token);
        }
        foreach (var item in report.Coverage)
        {
            if (item.ValidUntilUtc is { } until && until <= await QualityPersistence.Now(db, token)) throw new InvalidDataException("Coverage expired before publication.");
            foreach (var raw in item.EvidenceRaws ?? [])
                if (await acquisition.EvidenceAsync(raw.Id, report.Acquisitions[0].Report.Capability, false, token) != raw) throw new InvalidDataException("Coverage RAW changed.");
        }
    }
    public async Task<ProspectiveCohortArtifact> CohortAsync(Guid execution, bool display, CancellationToken token = default)
    {
        var artifact = await db.Set<PipelineArtifact>().AsNoTracking().SingleAsync(a => a.ExecutionId == execution, token);
        if (artifact.Hash != CanonicalDatasetJson.Hash(artifact.Content) || !await db.Set<PipelineExecution>().AnyAsync(e => e.Id == execution && e.State == PipelineState.Completed, token))
            throw new InvalidDataException("Completed cohort artifact required.");
        using var document = JsonDocument.Parse(artifact.Content); var result = document.RootElement.GetProperty("Result");
        if (result.GetProperty("Kind").GetString() != "prospective-cohort-v1" || document.RootElement.GetProperty("ExecutionId").GetGuid() != execution)
            throw new InvalidDataException("Cohort output binding mismatch.");
        var report = CanonicalDatasetJson.Deserialize<ProspectiveCohortReport>(System.Text.Encoding.UTF8.GetBytes(result.GetProperty("Report").GetRawText()));
        var row = await db.Set<PipelineExecution>().AsNoTracking().SingleAsync(e => e.Id == execution, token);
        var version = await db.Set<PipelineJobVersion>().AsNoTracking().SingleAsync(v => v.JobId == row.JobId && v.Version == row.DefinitionVersion, token);
        if (version.Hash != CanonicalDatasetJson.Hash(version.Content)) throw new InvalidDataException("Cohort definition corrupt.");
        var definition = CanonicalDatasetJson.Deserialize<PipelineDefinition>(version.Content);
        var input = JsonSerializer.Deserialize<ProspectiveCohortInput>(definition.PayloadJson, PipelineOperatorCommand.JsonOptions) ?? throw new InvalidDataException("Cohort input missing.");
        input.Validate();
        if (definition.Kind != PipelineKind.ProspectiveCohort || report.Version != 1 || report.InputHash != CanonicalDatasetJson.Fingerprint(input) || report.SourceId != input.SourceId ||
            !report.Acquisitions.Select(a => a.ExecutionId).Order().SequenceEqual(input.AcquisitionExecutions.Order()) ||
            !report.Validations.Select(v => v.ExecutionId).Order().SequenceEqual(input.ValidationExecutions.Order())) throw new InvalidDataException("Cohort input binding mismatch.");
        foreach (var a in report.Acquisitions) if ((await acquisition.ReadAsync(a.ExecutionId, display, token)).Hash != a.Hash) throw new InvalidDataException("Acquisition binding changed.");
        foreach (var v in report.Validations) if ((await validations.ReadAsync(v.ExecutionId, display, token)).Hash != v.Hash) throw new InvalidDataException("Validation binding changed.");
        foreach (var item in report.Coverage)
        {
            if (item.ValidUntilUtc is { } until && until <= await QualityPersistence.Now(db, token)) throw new InvalidDataException("Frozen coverage evidence expired.");
            foreach (var raw in item.EvidenceRaws ?? [])
                if (await acquisition.EvidenceAsync(raw.Id, report.Acquisitions[0].Report.Capability, display, token) != raw) throw new InvalidDataException("Coverage RAW changed.");
        }
        return new(execution, artifact.Hash, report);
    }
}
