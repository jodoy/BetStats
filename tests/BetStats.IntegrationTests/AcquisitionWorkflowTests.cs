using System.Text.Json;
using BetStats.Application.Coverage;
using BetStats.Application.Datasets;
using BetStats.Application.Evaluation;
using BetStats.Application.Ingestion;
using BetStats.Application.Pipeline;
using BetStats.Application.Providers;
using BetStats.Application.Quality;
using BetStats.Domain.Coverage;
using BetStats.Domain.Identity;
using BetStats.Infrastructure.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using BetStats.Application.Football;
using BetStats.Domain.Governance;
using BetStats.Web.Components;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;

namespace BetStats.IntegrationTests;

[Trait("Category", "Security")]
public sealed class AcquisitionWorkflowTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed class Loopback : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        { app.Use((context, following) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; return following(); }); next(app); };
    }
    private sealed class ApiHost(string connection, string root, string environment = "Development") : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment(environment)
            .UseSetting("ConnectionStrings:BetStats", connection).UseSetting("Ingestion:RawStoragePath", root)
            .ConfigureServices(s => s.AddSingleton<IStartupFilter, Loopback>());
    }
    private static readonly PipelineApproval Approval = new("test:acquisition", "Explicit synthetic prospective acquisition", true);
    private async Task<ResultOperationsWorkflowTests.Scenario> Create(bool display = false) => await new ResultOperationsWorkflowTests(fixture).Create(display);
    private static Task<int> Command(ResultOperationsWorkflowTests.Scenario s, string action, Guid? execution = null) =>
        PipelineOperatorCommand.RunAsync(s.Scope.ServiceProvider, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Pipeline:Action"] = action, ["Pipeline:Actor"] = Approval.Actor, ["Pipeline:Reason"] = Approval.Reason,
            ["Pipeline:Approve"] = "true", ["Pipeline:ExecutionId"] = execution?.ToString() }).Build(), true);
    private static async Task<Guid> Job<T>(ResultOperationsWorkflowTests.Scenario s, T input, PipelineKind kind = PipelineKind.FootballAcquisition, int lease = 300)
    {
        var id = Guid.NewGuid(); var jobs = s.Get<PostgreSqlPipeline>();
        await jobs.PlanAsync(id, new(1, kind, new(await s.Now()), JsonSerializer.Serialize(input), LeaseSeconds: lease), Approval);
        await jobs.SetEnabledAsync(id, true, Approval); return id;
    }
    private static async Task<Guid> Execution(ResultOperationsWorkflowTests.Scenario s, Guid job) =>
        (await s.Db.Set<PipelineExecution>().AsNoTracking().SingleAsync(e => e.JobId == job)).Id;
    private static async Task ReviewEvents(ResultOperationsWorkflowTests.Scenario s)
    {
        var identities = await s.Db.ProviderIdentities.AsNoTracking().Where(i => i.DataSourceId == s.Source && i.EntityKind == CanonicalEntityKind.SportingEvent).ToArrayAsync();
        foreach (var identity in identities)
        {
            var decision = await s.Db.IdentityResolutions.AsNoTracking().Where(d => d.ProviderIdentityId == identity.Id).OrderByDescending(d => d.Version).FirstAsync();
            if (decision.CanonicalSportingEventId is { } id)
                Assert.Equal("accepted", (await s.Get<IIdentityReview>().DecideAsync(new(identity.Id, s.Source, ReviewAction.Approve, new(CanonicalEntityKind.SportingEvent, id), decision.Version, Approval.Actor, Approval.Reason))).Result);
        }
    }
    private static async Task<LocalAcquisitionInput> Csv(ResultOperationsWorkflowTests.Scenario s, AcquisitionCategory category = AcquisitionCategory.HistoricalResults)
    {
        await ReviewEvents(s); var path = Path.Combine(s.Root, "authorized-fiction.csv"); await System.IO.File.WriteAllTextAsync(path, s.Fixture.Csv);
        var plan = await s.Get<FootballAcquisition>().PlanAsync(s.Source, path, s.Fixture.Scope, category, ValidationEvidenceKind.Synthetic, null, null, default);
        return new(plan, path);
    }
    private static async Task<LocalAcquisitionInput> Kickoff(ResultOperationsWorkflowTests.Scenario s, string? invalid = null, Guid? correction = null)
    {
        var request = await s.Request(); var dateId = request.Metadata.Definition.Targets[0].DateObservationId;
        var observation = await s.Db.Observations.AsNoTracking().SingleAsync(o => o.Id == dateId);
        var identity = await s.Db.ProviderIdentities.AsNoTracking().SingleAsync(i => i.Id == observation.ProviderIdentityId);
        var value = invalid == "unknown" ? new EventTimeValue(null, null, null, null, null, EventTimePrecision.Unknown) :
            new(s.Fixture.TargetDate, new(12, 0), null, 0, null, EventTimePrecision.Minute);
        var claim = new EventTimeSourceClaim(1, observation.RawPayloadId!.Value, invalid == "identity" ? "provider:wrong-event" : identity.ExternalId,
            new(s.Fixture.Scope.CompetitionReference, s.Fixture.Scope.SeasonReference), value);
        var path = Path.Combine(s.Root, Guid.NewGuid() + ".json");
        await System.IO.File.WriteAllBytesAsync(path, invalid == "malformed" ? new byte[] { 1, 2, 3 } : CanonicalDatasetJson.Serialize(claim));
        return new(await s.Get<FootballAcquisition>().PlanAsync(s.Source, path, s.Fixture.Scope, AcquisitionCategory.KickoffEvidence,
            ValidationEvidenceKind.Synthetic, dateId, correction, default), path);
    }
    [Fact] public async Task Approved_csv_acquisition_preserves_unknown_publication_and_does_not_claim_prospective_prediction_or_coverage()
    {
        await using var s = await Create(); var input = await Csv(s); var job = await Job(s, input);
        Assert.Equal(0, await Command(s, "run-once")); var id = await Execution(s, job);
        var report = await s.Get<FootballAcquisition>().ReadAsync(id, false, default);
        Assert.Equal(input.Plan.Hash, report.Report.Raw.Hash); Assert.Equal(ValidationEvidenceKind.Synthetic, report.Report.EvidenceKind);
        Assert.NotEmpty(report.Report.Observations); Assert.All(report.Report.Observations, o => Assert.Null(o.SourcePublishedUtc));
        Assert.Contains(report.Report.Observations, o => o.HistoricalImport);
        Assert.All(report.Report.Observations, o => Assert.False(ProspectiveEvidenceRules.KnownBefore(o, report.Report.AssessedUtc)));
        Assert.Equal(AcquisitionState.Partial, report.Report.State);
        Assert.Contains("coverage_not_established_by_capture", report.Report.Limitations);
        var count = await s.Db.RawPayloads.CountAsync(r => r.DataSourceId == s.Source);
        Assert.Equal(0, await Command(s, "run-once")); Assert.Equal(count, await s.Db.RawPayloads.CountAsync(r => r.DataSourceId == s.Source));
        Assert.Equal(report.Hash, (await s.Get<FootballAcquisition>().ReadAsync(id, false, default)).Hash);
    }
    [Fact] public async Task Exact_kickoff_bytes_are_captured_before_interpretation_and_observed_before_cutoff()
    {
        await using var s = await Create(); var input = await Kickoff(s); var job = await Job(s, input);
        Assert.Equal(0, await Command(s, "run-once")); var execution = await Execution(s, job);
        var report = await s.Get<FootballAcquisition>().ReadAsync(execution, false, default); var receipt = Assert.Single(report.Report.Observations);
        Assert.True(ProspectiveEvidenceRules.KnownBefore(receipt, report.Report.AssessedUtc)); Assert.Null(receipt.SourcePublishedUtc);
        var raw = await s.Db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == report.Report.Raw.Id);
        var stored = await s.Get<IRawPayloadStore>().ReadAsync(new(raw.StorageKey, raw.ContentHashSha256, raw.ByteLength!.Value));
        Assert.Equal(await System.IO.File.ReadAllBytesAsync(input.Path), stored.ToArray());
        Assert.True(report.Report.Raw.RecordedUtc <= receipt.KickoffRecordedUtc);
        var inputCohort = new ProspectiveCohortInput(1, s.Source, [execution], [], []);
        var cohortJob = await Job(s, inputCohort, PipelineKind.ProspectiveCohort); Assert.Equal(0, await Command(s, "run-once"));
        var cohort = await s.Get<FootballProspectiveReports>().CohortAsync(await Execution(s, cohortJob), false);
        Assert.Equal(1, cohort.Report.PreEventObserved); Assert.Equal(0, cohort.Report.EligiblePredictions); Assert.Equal(0, cohort.Report.EligibleEvaluations);
        Assert.Equal("Unknown", Assert.Single(cohort.Report.Coverage).Status);
    }
    [Theory] [InlineData("unknown")] [InlineData("malformed")] [InlineData("identity")]
    public async Task Missing_precision_malformed_response_and_wrong_identity_never_produce_predictions(string invalid)
    {
        await using var s = await Create(); var input = await Kickoff(s, invalid); var before = await s.Db.RawPayloads.CountAsync(r => r.DataSourceId == s.Source);
        var job = await Job(s, input); Assert.Equal(invalid == "unknown" ? 0 : 1, await Command(s, "run-once"));
        Assert.Equal(before + 1, await s.Db.RawPayloads.CountAsync(r => r.DataSourceId == s.Source)); // RAW survives rejected interpretation.
        Assert.Empty(await s.Db.BacktestOperations.ToArrayAsync());
        if (invalid == "unknown")
        {
            var report = await s.Get<FootballAcquisition>().ReadAsync(await Execution(s, job), false, default);
            Assert.Equal(AcquisitionState.Partial, report.Report.State); Assert.Null(Assert.Single(report.Report.Observations).KickoffUtc);
        }
    }
    [Fact] public async Task Missing_or_revoked_policy_denies_before_file_access_and_denies_old_reports()
    {
        await using var s = await Create(); var input = await Kickoff(s);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Get<FootballAcquisition>().PlanAsync(Guid.NewGuid(), @"C:\missing.csv", s.Fixture.Scope,
            AcquisitionCategory.HistoricalResults, ValidationEvidenceKind.Synthetic, null, null, default));
        var job = await Job(s, input); Assert.Equal(0, await Command(s, "run-once")); var execution = await Execution(s, job);
        s.Db.ChangeTracker.Clear(); var policy = await s.Db.SourcePolicies.Include(p => p.Audit).SingleAsync(p => p.DataSourceId == s.Source);
        policy.Revoke(Guid.NewGuid(), Approval.Actor, "Withdraw synthetic authorization", await s.Now()); await s.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Get<FootballAcquisition>().ReadAsync(execution, false, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Get<FootballAcquisition>().PlanAsync(s.Source, @"C:\missing.csv", s.Fixture.Scope,
            AcquisitionCategory.HistoricalResults, ValidationEvidenceKind.Synthetic, null, null, default));
    }
    [Fact] public async Task Interrupted_kickoff_recovery_reuses_raw_and_event_time_and_cannot_overtake_live_owner()
    {
        await using var s = await Create(); var input = await Kickoff(s); var job = await Job(s, input, lease: 10); var jobs = s.Get<PostgreSqlPipeline>();
        var claim = (await jobs.AcquireAsync(Approval))!;
        await using (var owner = await jobs.OwnAsync(claim.ExecutionId))
        {
            var output = await s.Get<PipelineWork>().ExecuteAsync(claim, Approval, default); Assert.Equal(PipelineState.Completed, output.Outcome.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.RetryAsync(claim.ExecutionId, true, Approval));
        }
        var rawCount = await s.Db.RawPayloads.CountAsync(r => r.DataSourceId == s.Source); var timeCount = await s.Db.EventTimeEvidence.CountAsync(e => e.SourceId == s.Source);
        await Task.Delay(10200); Assert.Equal(0, await Command(s, "recover", claim.ExecutionId));
        Assert.Equal(rawCount, await s.Db.RawPayloads.CountAsync(r => r.DataSourceId == s.Source)); Assert.Equal(timeCount, await s.Db.EventTimeEvidence.CountAsync(e => e.SourceId == s.Source));
        Assert.Single(await s.Db.Set<PipelineArtifact>().Where(a => a.ExecutionId == claim.ExecutionId).ToArrayAsync());
    }
    [Fact] public async Task Source_lock_and_raw_corruption_fail_closed()
    {
        await using var s = await Create(); var input = await Kickoff(s); var job = await Job(s, input);
        await using (var owner = fixture.CreateContext())
        {
            await owner.Database.OpenConnectionAsync(); await owner.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock(hashtextextended({s.Source.ToString("D")},9017))");
            Assert.Equal(1, await Command(s, "run-once"));
            await owner.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock(hashtextextended({s.Source.ToString("D")},9017))");
        }
        var execution = await Execution(s, job); Assert.Equal(0, await Command(s, "retry", execution));
        var report = await s.Get<FootballAcquisition>().ReadAsync(execution, false, default);
        var raw = await s.Db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == report.Report.Raw.Id);
        await System.IO.File.WriteAllTextAsync(Path.Combine(s.Root, raw.StorageKey + ".raw"), "corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => s.Get<FootballAcquisition>().ReadAsync(execution, false, default));
    }
    [Fact] public async Task Legacy_frozen_predictions_and_ledgers_remain_byte_identical()
    {
        await using var s = await Create(); var definition = await BacktestWorkflowTests.Definition(s, true);
        _ = await s.Get<IHistoricalBacktests>().RunAsync(new(Guid.NewGuid(), definition, Approval.Actor, Approval.Reason, true));
        var before = CanonicalDatasetJson.Fingerprint(await s.Db.Backtests.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync());
        var ledger = CanonicalDatasetJson.Fingerprint(await s.Db.BacktestOperations.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync());
        var job = await Job(s, await Kickoff(s)); Assert.Equal(0, await Command(s, "run-once"));
        Assert.Equal(before, CanonicalDatasetJson.Fingerprint(await s.Db.Backtests.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync()));
        Assert.Equal(ledger, CanonicalDatasetJson.Fingerprint(await s.Db.BacktestOperations.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync()));
    }
    [Fact] public async Task Kickoff_binding_accepts_explicit_historical_csv_profile_without_reinterpreting_legacy_bytes()
    {
        await using var s = await Create(); var csv = await Csv(s, AcquisitionCategory.PrematchMetadata);
        var changedDate = s.Fixture.TargetDate.AddDays(1);
        await System.IO.File.WriteAllTextAsync(csv.Path, s.Fixture.Csv.Replace(s.Fixture.TargetDate.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture), changedDate.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        var plan = await s.Get<FootballAcquisition>().PlanAsync(s.Source, csv.Path, s.Fixture.Scope, AcquisitionCategory.PrematchMetadata, ValidationEvidenceKind.Synthetic, null, null, default);
        var job = await Job(s, new LocalAcquisitionInput(plan, csv.Path)); Assert.Equal(0, await Command(s, "run-once"));
        var identity = await s.Db.ProviderIdentities.AsNoTracking().SingleAsync(i => i.DataSourceId == s.Source && i.ExternalId == "provider:clock-target");
        var observation = await s.Db.Observations.AsNoTracking().Where(o => o.ProviderIdentityId == identity.Id && o.Type == BetStats.Domain.Observations.ObservationType.EventDate).OrderByDescending(o => o.Version).FirstAsync();
        var raw = await s.Db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == observation.RawPayloadId);
        Assert.Equal("fixture:football-history-v1", raw.ExternalReference);
        var claim = new EventTimeSourceClaim(1, raw.Id, identity.ExternalId, new(s.Fixture.Scope.CompetitionReference, s.Fixture.Scope.SeasonReference),
            new(changedDate, new(12, 0), null, 0, null, EventTimePrecision.Minute));
        var path = Path.Combine(s.Root, "time-for-history.json"); await System.IO.File.WriteAllBytesAsync(path, CanonicalDatasetJson.Serialize(claim));
        var kickoff = await s.Get<FootballAcquisition>().PlanAsync(s.Source, path, s.Fixture.Scope, AcquisitionCategory.KickoffEvidence, ValidationEvidenceKind.Synthetic, observation.Id, null, default);
        var timeJob = await Job(s, new LocalAcquisitionInput(kickoff, path)); Assert.Equal(0, await Command(s, "run-once"));
        var receipt = Assert.Single((await s.Get<FootballAcquisition>().ReadAsync(await Execution(s, timeJob), false, default)).Report.Observations);
        Assert.Equal(changedDate.ToDateTime(new(12, 0), DateTimeKind.Utc), receipt.KickoffUtc);
    }
    [Fact] public async Task Postmatch_correction_preserves_original_observation_and_records_new_result_clocks()
    {
        await using var s = await Create(); var original = await s.Db.FootballResults.AsNoTracking().SingleAsync(r => r.SourceId == s.Source && r.SourceEventReference == "provider:clock-result-1");
        var input = await Csv(s, AcquisitionCategory.PostmatchResults); await System.IO.File.WriteAllTextAsync(input.Path, s.Fixture.Correction);
        var correctedPlan = await s.Get<FootballAcquisition>().PlanAsync(s.Source, input.Path, s.Fixture.Scope, AcquisitionCategory.PostmatchResults, ValidationEvidenceKind.Synthetic, null, null, default);
        var job = await Job(s, new LocalAcquisitionInput(correctedPlan, input.Path)); Assert.Equal(0, await Command(s, "run-once"));
        var rows = await s.Db.FootballResults.AsNoTracking().Where(r => r.SourceId == s.Source && r.SourceEventReference == "provider:clock-result-1").OrderBy(r => r.Version).ToArrayAsync();
        Assert.Equal(2, rows.Length); Assert.Equal(original.Id, rows[1].CorrectsId); Assert.Equal(1, rows[0].Value.FullTime.Away); Assert.Equal(2, rows[1].Value.FullTime.Away);
        var report = await s.Get<FootballAcquisition>().ReadAsync(await Execution(s, job), false, default); var receipt = Assert.Single(report.Report.Observations);
        Assert.Equal(rows[1].Id, receipt.ResultObservationId); Assert.True(receipt.ResultRecordedUtc > original.RecordedAtUtc); Assert.Null(receipt.ResultPublishedUtc);
    }
    [Fact] public async Task Concurrent_or_unreviewed_completeness_and_cross_source_cohorts_are_not_certified()
    {
        await using var s = await Create(); var job = await Job(s, await Kickoff(s)); Assert.Equal(0, await Command(s, "run-once")); var execution = await Execution(s, job);
        var cohort = new ProspectiveCohortInput(1, s.Source, [execution], [], []);
        await Assert.ThrowsAsync<InvalidDataException>(() => s.Get<FootballProspectiveReports>().BuildAsync(cohort with { SourceId = Guid.NewGuid() }, default));
        var observed = await s.Get<FootballProspectiveReports>().BuildAsync(cohort, default);
        Assert.Equal("Unknown", Assert.Single(observed.Coverage).Status); Assert.Equal(0, observed.EligibleEvaluations);
        Assert.Contains("independent_source_verification_unavailable", observed.Limitations);
    }
    [Fact] public async Task Governed_prediction_is_frozen_by_existing_pipeline_and_cohort_does_not_count_unwarmed_priors_as_eligible()
    {
        await using var s = await Create(); var kickoffInput = await Kickoff(s); var acquisitionJob = await Job(s, kickoffInput); Assert.Equal(0, await Command(s, "run-once"));
        var acquired = await Execution(s, acquisitionJob); var request = await s.Request(); var predictor = await BacktestWorkflowTests.Definition(s, true);
        var now = await s.Now(); var planned = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc).AddSeconds(-2);
        var horizon = checked((int)(s.Fixture.TargetDate.ToDateTime(new(12, 0), DateTimeKind.Utc) - planned).TotalSeconds);
        var jobs = s.Get<PostgreSqlPipeline>(); var predictionJob = Guid.NewGuid();
        await jobs.PlanAsync(predictionJob, new(1, PipelineKind.PrematchPrediction, new(planned), JsonSerializer.Serialize(new PrematchInput(request, predictor)), PredictionHorizonSeconds: horizon), Approval);
        await jobs.SetEnabledAsync(predictionJob, true, Approval); Assert.Equal(0, await Command(s, "run-once"));
        var predictionExecution = await Execution(s, predictionJob);
        var original = await s.Db.RawPayloads.AsNoTracking().FirstAsync(r => r.DataSourceId == s.Source && r.ExternalReference == "fixture:results-v1");
        var validationInput = new FootballValidationInput(1, s.Source, original.Id, original.ContentHashSha256, "results-v1", s.Fixture.Scope,
            ValidationEvidenceKind.Synthetic, [new(predictionExecution)]);
        var validationJob = await Job(s, validationInput, PipelineKind.FootballValidation); Assert.Equal(0, await Command(s, "run-once"));
        var before = CanonicalDatasetJson.Fingerprint(await s.Db.Backtests.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync());
        var cohortInput = new ProspectiveCohortInput(1, s.Source, [acquired], [await Execution(s, validationJob)], []);
        var cohortJob = await Job(s, cohortInput, PipelineKind.ProspectiveCohort); Assert.Equal(0, await Command(s, "run-once"));
        var cohort = await s.Get<FootballProspectiveReports>().CohortAsync(await Execution(s, cohortJob), false);
        Assert.Equal(1, cohort.Report.FrozenPredictions); Assert.Equal(0, cohort.Report.EligiblePredictions); Assert.True(cohort.Report.WarmupExclusions > 0);
        Assert.Equal(0, cohort.Report.EligibleEvaluations); Assert.Equal(before, CanonicalDatasetJson.Fingerprint(await s.Db.Backtests.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync()));
    }
    [Fact] public async Task Development_http_monitoring_is_bounded_read_only_and_bilingual()
    {
        await using var s = await Create(true); var job = await Job(s, await Kickoff(s)); Assert.Equal(0, await Command(s, "run-once")); var execution = await Execution(s, job);
        var cohortJob = await Job(s, new ProspectiveCohortInput(1, s.Source, [execution], [], []), PipelineKind.ProspectiveCohort); Assert.Equal(0, await Command(s, "run-once"));
        var cohort = await Execution(s, cohortJob); var report = await s.Get<FootballAcquisition>().ReadAsync(execution, false, default);
        var receipts = await s.Db.Set<PipelineReceipt>().CountAsync(); var raws = await s.Db.RawPayloads.CountAsync(); var predictions = await s.Db.BacktestOperations.CountAsync();
        await using var host = new ApiHost(fixture.GetConnectionString(), s.Root); using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        foreach (var resource in new[] { "acquisition/" + execution, "prospective/" + cohort, "acquisition-status/" + job })
        {
            var response = await client.GetAsync("/api/v1/dashboard/" + resource); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain(s.Root, text); Assert.DoesNotContain("StorageKey", text);
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/api/v1/dashboard/" + resource, null)).StatusCode);
        }
        Assert.Equal(receipts, await s.Db.Set<PipelineReceipt>().CountAsync()); Assert.Equal(raws, await s.Db.RawPayloads.CountAsync()); Assert.Equal(predictions, await s.Db.BacktestOperations.CountAsync());
        await using var production = new ApiHost(fixture.GetConnectionString(), s.Root, "Production"); using var prod = production.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.Forbidden, (await prod.GetAsync("/api/v1/dashboard/acquisition/" + execution)).StatusCode);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider(); await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        foreach (var lang in new[] { "pl-PL", "en-GB" })
        {
            var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<AcquisitionView>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Artifact"] = report, ["Language"] = lang }))).ToHtmlString());
            Assert.Contains("DEMO", html); Assert.Contains("SHA256", html);
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Unsatisfied_attribution_or_retention_is_never_silently_approved(bool retention)
    {
        await using var s = await Create(); var input = await Kickoff(s); s.Db.ChangeTracker.Clear();
        var old = await s.Db.SourcePolicies.Include(p => p.Audit).SingleAsync(p => p.DataSourceId == s.Source); var now = await s.Now();
        old.Revoke(Guid.NewGuid(), Approval.Actor, Approval.Reason, now); await s.Db.SaveChangesAsync();
        now = await s.Now(); var policy = new SourcePolicy(Guid.NewGuid(), s.Source, 2, now, null, "project-owned-test-terms", "project-owned-test-proof", now,
            Enum.GetValues<DataPurpose>().Select(p => new PurposePermission(p, PermissionDecision.Allowed,
                !retention && p == DataPurpose.InternalAnalytics ? "Required explicit source attribution" : null,
                retention && p == DataPurpose.HistoricalRetention ? 1 : null)));
        s.Db.Add(policy); await s.Db.SaveChangesAsync(); now = await s.Now();
        policy.Approve(Guid.NewGuid(), Approval.Actor, Approval.Reason, now, now); await s.Db.SaveChangesAsync();
        if (!retention) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Get<FootballAcquisition>().PlanAsync(s.Source, input.Path, s.Fixture.Scope,
            AcquisitionCategory.KickoffEvidence, ValidationEvidenceKind.Synthetic, input.Plan.DateObservationId, null, default));
        else
        {
            var p = await s.Get<FootballAcquisition>().PlanAsync(s.Source, input.Path, s.Fixture.Scope, AcquisitionCategory.KickoffEvidence,
                ValidationEvidenceKind.Synthetic, input.Plan.DateObservationId, null, default);
            var job = await Job(s, new LocalAcquisitionInput(p, input.Path)); Assert.Equal(1, await Command(s, "run-once"));
            Assert.Equal(PipelineState.Blocked, (await s.Db.Set<PipelineExecution>().AsNoTracking().SingleAsync(e => e.JobId == job)).State);
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Coverage_requires_independent_proof_and_preserves_partial_evidence(bool unsupportedComplete)
    {
        await using var s = await Create(); var job = await Job(s, await Kickoff(s)); Assert.Equal(0, await Command(s, "run-once")); var acquired = await Execution(s, job);
        var query = await s.Query();
        var scope = new CoverageScope(s.Source, BetStats.Domain.Sports.ReferenceSports.All.Single(s => s.Code == "football").Id,
            query.CompetitionId, query.SeasonId, null, "football-match", BetStats.Domain.Observations.ObservationType.EventDate,
            s.Fixture.Scope.CompetitionReference, s.Fixture.Scope.SeasonReference,
            new(IntervalKind.Calendar, null, null, s.Fixture.HistoryStart, s.Fixture.TargetDate.AddDays(1), "UTC-calendar"));
        var raw = await s.Raw(new { Scope = scope, OperatorClaim = "partial observation is not an independent inventory" });
        var evidence = await s.Get<IHistoricalCoverage>().RecordAsync(new(scope, unsupportedComplete ? CoverageStatus.VerifiedComplete : CoverageStatus.Partial,
            CoverageBasis.ManualStatement, "test:bounded-statement", raw.Id, 1, null, raw.RetrievedAtUtc, (await s.Now()).AddDays(1), Approval.Actor, Approval.Reason));
        var review = await s.Get<IHistoricalCoverage>().ReviewAsync(new(evidence.Id, 0, CoverageReviewStatus.Approved, "manual-statement", Approval.Actor, Approval.Reason));
        Assert.Equal(unsupportedComplete ? CoverageReviewStatus.Rejected : CoverageReviewStatus.Approved, review.Status);
        var cohortInput = new ProspectiveCohortInput(1, s.Source, [acquired], [], [new(scope, await s.Now(), BetStats.Domain.Quality.DatasetMode.HistoricalAsKnown, null, DataPurpose.InternalAnalytics, new())]);
        var report = await s.Get<FootballProspectiveReports>().BuildAsync(cohortInput, default); var covered = Assert.Single(report.Coverage);
        Assert.NotEqual("VerifiedComplete", covered.Status); Assert.NotEmpty(covered.EvidenceRaws!);
        if (!unsupportedComplete) Assert.Equal("Partial", covered.Status);
    }
}
