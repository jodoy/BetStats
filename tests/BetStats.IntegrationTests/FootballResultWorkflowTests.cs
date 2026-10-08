using System.Net;
using System.Text.Json;
using BetStats.Application.Coverage;
using BetStats.Application.Datasets;
using BetStats.Application.Football;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Domain.Football;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Quality;
using BetStats.Domain.Observations;
using BetStats.Infrastructure;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BetStats.IntegrationTests;

public sealed class FootballResultWorkflowTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed class Scenario : IAsyncDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public required IServiceScope Scope { get; init; }
        public required string Root { get; init; }
        public Guid Source { get; set; }
        public BetStatsDbContext Db => Scope.ServiceProvider.GetRequiredService<BetStatsDbContext>();
        public T Get<T>() where T : notnull => Scope.ServiceProvider.GetRequiredService<T>();
        public Task<DateTime> Now() => Db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        public Task<ImportReport> Import(string csv = SyntheticFootballResultsDemo.Csv) => Get<FootballIngestion>().RunAsync(
            new FootballDataFixtureAdapter(Source, SyntheticFootballDemo.Bytes(csv), TimeProvider.System), SyntheticFootballDemo.Scope, Get<RequestBudget>());
        public async Task<FootballResultQuery> Query(DateTime? at = null, Guid? eventId = null, bool history = false)
        {
            var r = await Db.FootballResults.FirstAsync(x => x.SourceId == Source);
            return new(r.CompetitionId, r.SeasonId, at ?? await Now(), DataPurpose.InternalAnalytics, new(), eventId, Source, IncludeSuperseded: history);
        }
        public async ValueTask DisposeAsync() { Scope.Dispose(); await Provider.DisposeAsync(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private async Task<Scenario> Create(bool publicDemo = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "betstats-results-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:BetStats"] = fixture.GetConnectionString(), ["Ingestion:RawStoragePath"] = root }).Build();
        var provider = new ServiceCollection().AddPersistence(config).BuildServiceProvider(); var scope = provider.CreateScope();
        var s = new Scenario { Provider = provider, Scope = scope, Root = root };
        s.Source = await SyntheticFootballDemo.PrepareAsync(s.Db, true, publicDemo ? SyntheticFootballResultsDemo.SourceCode : "synthetic-results-" + Guid.NewGuid().ToString("N"), allowSyntheticDisplay: publicDemo);
        return s;
    }
    [Fact]
    public async Task Pipeline_preserves_scores_status_unknowns_corrections_and_idempotency()
    {
        await using var s = await Create();
        var import = await s.Import(); Assert.Equal(ImportOutcome.Succeeded, import.Outcome); Assert.Equal(6, import.AcceptedRecords);
        var query = await s.Query(); var report = await s.Get<IFootballResults>().ReadAsync(query);
        Assert.Equal(6, report.Results.Count); Assert.All(report.Results, r => Assert.True(r.Eligible, string.Join(',', r.Reasons)));
        Assert.Equal(3, report.Results.Count(r => r.Labels is not null));
        var first = report.Results.Single(r => r.Observation.SourceEventReference == "provider:result-1");
        Assert.Equal("HomeWin", first.Labels!.Winner); Assert.Equal(1, first.Labels.HalfTimeTotalGoals);
        Assert.Null(report.Results.Single(r => r.Observation.SourceEventReference == "provider:result-3").Labels!.HalfTimeTotalGoals);
        Assert.Equal(ImportOutcome.Reused, (await s.Import()).Outcome); Assert.Equal(6, await s.Db.FootballResults.CountAsync(r => r.SourceId == s.Source));
        var cutoff = await s.Now(); Assert.Equal(ImportOutcome.Succeeded, (await s.Import(SyntheticFootballResultsDemo.Correction)).Outcome);
        var old = await s.Get<IFootballResults>().ReadAsync(await s.Query(cutoff, first.Observation.EventId));
        Assert.Equal(1, Assert.Single(old.Results).Observation.Value.FullTime.Away);
        var current = await s.Get<IFootballResults>().ReadAsync(await s.Query(eventId: first.Observation.EventId));
        Assert.Equal("Draw", Assert.Single(current.Results).Labels!.Winner);
        var history = await s.Get<IFootballResults>().ReadAsync(await s.Query(eventId: first.Observation.EventId, history: true));
        Assert.Equal(2, history.Results.Count); Assert.Equal(first.Observation.Id, history.Results[1].Observation.CorrectsId);
        Assert.Equal(ImportOutcome.Reused, (await s.Import(SyntheticFootballResultsDemo.Correction)).Outcome);
        await s.Import(); Assert.Equal(7, await s.Db.FootballResults.CountAsync(r => r.SourceId == s.Source));
    }
    [Theory]
    [InlineData("negative")]
    [InlineData("half")]
    [InlineData("transition")]
    [InlineData("published")]
    [InlineData("duplicate")]
    public async Task Invalid_result_reports_are_assessed_without_overwriting_history(string mode)
    {
        await using var s = await Create(); await s.Import();
        var row = SyntheticFootballResultsDemo.Correction;
        row = mode switch { "negative" => row.Replace(",2,2,1,0,", ",-1,2,1,0,", StringComparison.Ordinal),
            "half" => row.Replace(",2,2,1,0,", ",2,2,3,0,", StringComparison.Ordinal),
            "transition" => row.Replace("Finished,RegulationTime,2,2,1,0", "Scheduled,Unknown,,,,", StringComparison.Ordinal),
            "published" => row.TrimEnd('\n') + "2099-01-01T00:00:00.000000Z\n",
            _ => row + row.Split('\n')[1].Replace(",2,2,", ",3,2,", StringComparison.Ordinal) + "\n" };
        var report = await s.Import(row); Assert.Equal(ImportOutcome.Partial, report.Outcome);
        Assert.Equal(6, await s.Db.FootballResults.CountAsync(r => r.SourceId == s.Source));
        Assert.True(await s.Db.QualityAssessments.AnyAsync(a => a.ExecutionId == report.AttemptId && a.BlocksEligibility));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_corrupt_RAW_fails_closed(bool corrupt)
    {
        await using var s = await Create(); await s.Import(); var raw = await s.Db.RawPayloads.SingleAsync(r => r.DataSourceId == s.Source);
        var path = Path.Combine(s.Root, raw.StorageKey + ".raw");
        // Storage keys are opaque relative paths generated by this isolated temporary store.
        if (corrupt) await File.WriteAllTextAsync(path, "corrupt fictional bytes"); else File.Delete(path);
        var result = await s.Get<IFootballResults>().ReadAsync(await s.Query());
        Assert.All(result.Results, r => { Assert.False(r.Eligible); Assert.Null(r.Labels); Assert.Contains("result_raw_integrity_or_context", r.Reasons); });
    }
    [Fact]
    public async Task Revocation_denies_import_history_and_labels()
    {
        await using var s = await Create(); await s.Import();
        var p = await s.Db.SourcePolicies.Include(p => p.Audit).Include(p => p.Permissions).SingleAsync(p => p.DataSourceId == s.Source);
        p.Revoke(Guid.NewGuid(), "synthetic-reviewer", "Revoked fixture use", await s.Now()); await s.Db.SaveChangesAsync();
        Assert.Equal(ImportOutcome.Denied, (await s.Import(SyntheticFootballResultsDemo.Correction)).Outcome);
        var query = await s.Query();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Get<IFootballResults>().ReadAsync(query));
    }
    [Fact]
    public async Task Reconciliation_cannot_reinterpret_original_season()
    {
        await using var s = await Create(); await s.Import(); var raw = await s.Db.RawPayloads.SingleAsync(r => r.DataSourceId == s.Source);
        var reconciliation = await s.Get<BetStats.Application.Quality.IDataReconciliation>().RunAsync([new(raw.Id, new("FICT", "wrong-season"))], "synthetic-reviewer", "Wrong original season");
        Assert.Contains(reconciliation.Items, item => item.ReasonCode == "original_scope_mismatch");
        Assert.Equal(6, await s.Db.FootballResults.CountAsync(r => r.SourceId == s.Source));
    }

    [Fact]
    public async Task Dataset_v3_freezes_results_partial_coverage_and_preserves_pre_correction_hashes()
    {
        await using var s = await Create(); await s.Import();
        var target = await s.Db.FootballResults.SingleAsync(r => r.SourceId == s.Source && r.SourceEventReference == "provider:result-target");
        var cutoff = await s.Now();
        var d = new DatasetDefinition(2, BetStats.Application.Quality.FootballQualityRules.Football, target.CompetitionId, target.SeasonId,
            "FICT", "2026-fiction", new(2026, 1, 1), new(2026, 12, 31), cutoff, DatasetMode.HistoricalAsKnown, null, 1, 2,
            DataPurpose.InternalAnalytics, new(), "UTC-calendar", "eligible-observed-metadata-v1", "fail-closed-v1", [new(target.DateObservationId, cutoff)]);
        var request = new FootballResultDatasetRequest(new(d, "synthetic-reviewer", "Freeze eligible observed result history"), cutoff);
        var service = s.Get<IFootballResultDatasets>(); var first = await service.BuildAsync(request); var second = await service.BuildAsync(request);
        Assert.Equal(first.Id, second.Id); Assert.Equal(first.Hash, second.Hash);
        var row = Assert.Single(first.Manifest.Rows); Assert.Contains("partial", row.Features.CoverageSemantics);
        Assert.Equal(2, row.Features.Values.Single(v => v.Name == "home_observed_wins_last_30d").Numerator);
        Assert.Equal(3, row.Features.Values.Single(v => v.Name == "home_observed_goals_scored_last_30d").Numerator);
        Assert.Equal(1, row.Features.Values.Single(v => v.Name == "home_observed_goals_conceded_last_30d").Numerator);
        var points = row.Features.Values.Single(v => v.Name == "home_observed_points_per_match_last_30d"); Assert.Equal(6, points.Numerator); Assert.Equal(2, points.Denominator);
        var btts = row.Features.Values.Single(v => v.Name == "home_observed_btts_frequency_last_30d"); Assert.Equal(1, btts.Numerator); Assert.Equal(2, btts.Denominator);
        Assert.Null(row.Features.Values.Single(v => v.Name == "home_observed_away_points_per_match_last_30d").Numerator);
        Assert.Empty(row.Labels); Assert.DoesNotContain(target.Id, row.Features.ResultObservationIds);
        var verify = await service.VerifyAsync(first.Id); Assert.True(verify.Integrity); Assert.True(verify.FeaturesReproducible); Assert.True(verify.CurrentlyAuthorized);
        var metadata = await s.Db.DatasetArtifacts.SingleAsync(a => a.ManifestHash == first.Manifest.MetadataManifestHash);
        var originalBytes = metadata.Content.ToArray();
        await s.Import(SyntheticFootballResultsDemo.Correction);
        Assert.Equal(first.Hash, (await service.BuildAsync(request)).Hash);
        var later = await s.Now(); var changed = await service.BuildAsync(new(new(d with { AsOfUtc = later, Targets = [new(target.DateObservationId, later)] }, "synthetic-reviewer", "New evidence cutoff"), later));
        Assert.NotEqual(first.Hash, changed.Hash);
        Assert.Equal(1, changed.Manifest.Rows[0].Features.Values.Single(v => v.Name == "home_observed_wins_last_30d").Numerator);
        Assert.Equal(originalBytes, (await s.Db.DatasetArtifacts.AsNoTracking().SingleAsync(a => a.Id == metadata.Id)).Content);
        var policy = await s.Db.SourcePolicies.Include(p => p.Audit).Include(p => p.Permissions).SingleAsync(p => p.DataSourceId == s.Source);
        policy.Revoke(Guid.NewGuid(), "synthetic-reviewer", "Revoke frozen result use", await s.Now()); await s.Db.SaveChangesAsync();
        var denied = await service.VerifyAsync(first.Id); Assert.True(denied.Integrity); Assert.True(denied.FeaturesReproducible); Assert.False(denied.CurrentlyAuthorized);
    }

    [Fact]
    public async Task Result_received_after_prediction_cutoff_is_a_separate_label_and_never_a_feature()
    {
        await using var s = await Create(); await s.Import();
        var target = await s.Db.FootballResults.SingleAsync(r => r.SourceId == s.Source && r.SourceEventReference == "provider:result-target");
        var prediction = await s.Now();
        // Project-authored fictional timeline, not a statement about an actual future match.
        var labelsCsv = SyntheticFootballResultsDemo.Header + "FICT,20/12/2026,Amber Comets,Cobalt Owls,result-target,Finished,RegulationTime,2,1,1,0,\n";
        Assert.Equal(ImportOutcome.Succeeded, (await s.Import(labelsCsv)).Outcome); var labelCutoff = await s.Now();
        var d = new DatasetDefinition(2, BetStats.Application.Quality.FootballQualityRules.Football, target.CompetitionId, target.SeasonId,
            "FICT", "2026-fiction", new(2026, 1, 1), new(2026, 12, 31), labelCutoff, DatasetMode.HistoricalAsKnown, null, 1, 2,
            DataPurpose.InternalAnalytics, new(), "UTC-calendar", "eligible-observed-metadata-v1", "fail-closed-v1", [new(target.DateObservationId, prediction)]);
        var dataset = await s.Get<IFootballResultDatasets>().BuildAsync(new(new(d, "synthetic-reviewer", "Separate post-cutoff labels"), labelCutoff));
        var row = Assert.Single(dataset.Manifest.Rows); var label = Assert.Single(row.Labels);
        Assert.Equal("HomeWin", label.Winner); Assert.True(label.AvailableAtUtc > prediction); Assert.True(label.RecordedAtUtc > prediction);
        Assert.DoesNotContain(label.ResultObservationId, row.Features.ResultObservationIds);
        Assert.DoesNotContain(row.FeatureEvidence.Results, e => e.Observation.Id == label.ResultObservationId);
        Assert.True((await s.Get<IFootballResultDatasets>().VerifyAsync(dataset.Id)).FeaturesReproducible);
    }

    [Fact]
    public async Task Independent_source_disagreement_has_no_implicit_winner()
    {
        await using var s = await Create(); await s.Import();
        var first = await s.Db.FootballResults.SingleAsync(r => r.SourceId == s.Source && r.SourceEventReference == "provider:result-1");
        var sourceA = s.Source;
        var other = await SyntheticFootballDemo.PrepareAsync(s.Db, true, "synthetic-conflict-" + Guid.NewGuid().ToString("N"));
        var now = await s.Now();
        var original = await s.Db.ProviderIdentities.Where(i => i.DataSourceId == sourceA && i.EntityKind != CanonicalEntityKind.SportingEvent).ToArrayAsync();
        foreach (var anchor in await s.Db.ProviderIdentities.Where(i => i.DataSourceId == other).ToArrayAsync())
        {
            var corresponding = original.Single(i => i.EntityKind == anchor.EntityKind && i.ExternalId == anchor.ExternalId);
            var target = await s.Db.IdentityResolutions.Where(r => r.ProviderIdentityId == corresponding.Id).OrderByDescending(r => r.Version).FirstAsync();
            var previous = await s.Db.IdentityResolutions.Where(r => r.ProviderIdentityId == anchor.Id).OrderByDescending(r => r.Version).FirstAsync();
            s.Db.Add(new IdentityResolution(Guid.NewGuid(), anchor, ResolutionStatus.Resolved, new(anchor.EntityKind, target.CanonicalId!.Value), "synthetic-reviewer", "Explicit shared fictional identity", now, previous));
        }
        var eventAnchor = new ProviderIdentity(Guid.NewGuid(), other, CanonicalEntityKind.SportingEvent, first.SourceEventReference, now);
        s.Db.AddRange(eventAnchor, new IdentityResolution(Guid.NewGuid(), eventAnchor, ResolutionStatus.Resolved, new(CanonicalEntityKind.SportingEvent, first.EventId), "synthetic-reviewer", "Reviewed common event", now));
        await s.Db.SaveChangesAsync(); s.Source = other;
        Assert.Equal(ImportOutcome.Succeeded, (await s.Import(SyntheticFootballResultsDemo.Correction)).Outcome);
        s.Source = sourceA;
        var report = await s.Get<IFootballResults>().ReadAsync(await s.Query(eventId: first.EventId));
        var conflict = Assert.Single(report.Results); Assert.False(conflict.Eligible); Assert.Null(conflict.Labels);
        Assert.Contains("result_cross_source_conflict", conflict.Reasons);
    }

    [Fact]
    public async Task Simultaneous_source_reports_remain_conflicted_until_justified_later_correction()
    {
        await using var s = await Create();
        const string published = "2026-10-07T00:00:00.000000Z";
        var initial = SyntheticFootballResultsDemo.Header + SyntheticFootballResultsDemo.Csv.Split('\n')[1] + published + "\n";
        Assert.Equal(ImportOutcome.Succeeded, (await s.Import(initial)).Outcome); var before = await s.Now();
        var contradictory = SyntheticFootballResultsDemo.Correction.TrimEnd('\n') + published + "\n";
        Assert.Equal(ImportOutcome.Partial, (await s.Import(contradictory)).Outcome);
        var current = Assert.Single((await s.Get<IFootballResults>().ReadAsync(await s.Query())).Results);
        Assert.False(current.Eligible); Assert.Null(current.Labels); Assert.Contains("result_conflicting_report", current.Reasons);
        Assert.True(Assert.Single((await s.Get<IFootballResults>().ReadAsync(await s.Query(before))).Results).Eligible);
        var corrected = SyntheticFootballResultsDemo.Correction.TrimEnd('\n') + "2026-10-07T01:00:00.000000Z\n";
        Assert.Equal(ImportOutcome.Succeeded, (await s.Import(corrected)).Outcome);
        var resolved = Assert.Single((await s.Get<IFootballResults>().ReadAsync(await s.Query())).Results);
        Assert.True(resolved.Eligible, string.Join(',', resolved.Reasons)); Assert.Equal("Draw", resolved.Labels!.Winner);
    }

    [Fact]
    public async Task Conflicting_alias_streams_within_one_source_cannot_choose_a_result_by_UUID()
    {
        await using var s = await Create(); await s.Import();
        var original = await s.Db.FootballResults.SingleAsync(r => r.SourceId == s.Source && r.SourceEventReference == "provider:result-1");
        var now = await s.Now(); var alias = new ProviderIdentity(Guid.NewGuid(), s.Source, CanonicalEntityKind.SportingEvent, "provider:result-alias", now);
        s.Db.AddRange(alias, new IdentityResolution(Guid.NewGuid(), alias, ResolutionStatus.Resolved, new(CanonicalEntityKind.SportingEvent, original.EventId),
            "synthetic-reviewer", "Explicit fictional provider alias", now)); await s.Db.SaveChangesAsync();
        var csv = SyntheticFootballResultsDemo.Correction.Replace("result-1", "result-alias", StringComparison.Ordinal);
        Assert.Equal(ImportOutcome.Succeeded, (await s.Import(csv)).Outcome);
        var report = await s.Get<IFootballResults>().ReadAsync(await s.Query(eventId: original.EventId));
        Assert.Equal(2, report.Results.Count); Assert.All(report.Results, r => { Assert.False(r.Eligible); Assert.Null(r.Labels); Assert.Contains("result_identity_stream_conflict", r.Reasons); });
    }

    [Fact]
    public async Task Half_time_observation_has_a_period_label_without_a_finished_match_assumption()
    {
        await using var s = await Create();
        var csv = SyntheticFootballResultsDemo.Header + "FICT,08/10/2026,Amber Comets,Cobalt Owls,half-time-event,HalfTime,RegulationTime,,,1,0,\n";
        Assert.Equal(ImportOutcome.Succeeded, (await s.Import(csv)).Outcome);
        var evidence = Assert.Single((await s.Get<IFootballResults>().ReadAsync(await s.Query())).Results);
        Assert.True(evidence.Eligible); Assert.Equal(FootballMatchStatus.HalfTime, evidence.Observation.Value.Status);
        Assert.Equal(1, evidence.Labels!.HalfTimeTotalGoals); Assert.Null(evidence.Labels.Winner); Assert.Null(evidence.Labels.FullTimeTotalGoals);
        Assert.False(FootballResultRules.LabelEligible(evidence.Observation.Value));
    }

    private sealed class ApiHost(string connection, string root, string environment) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment(environment)
            .UseSetting("ConnectionStrings:BetStats", connection).UseSetting("Ingestion:RawStoragePath", root);
    }
    [Fact]
    public async Task Swagger_development_reads_are_historical_sanitized_and_unavailable_in_production()
    {
        await using var s = await Create(publicDemo: true); await s.Import();
        var cutoff = await s.Now(); var result = await s.Db.FootballResults.SingleAsync(r => r.SourceId == s.Source && r.SourceEventReference == "provider:result-1");
        await s.Import(SyntheticFootballResultsDemo.Correction);
        await using var dev = new ApiHost(fixture.GetConnectionString(), s.Root, "Development"); using var client = dev.CreateClient(new() { BaseAddress = new("https://localhost") });
        var query = "?asOfUtc=" + Uri.EscapeDataString(cutoff.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        var response = await client.GetAsync("/api/v1/dev/events/" + result.EventId + "/result" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var body = await response.Content.ReadAsStringAsync();
        using var value = JsonDocument.Parse(body); Assert.Equal(1, value.RootElement.GetProperty("fullTime").GetProperty("away").GetInt32());
        foreach (var sensitive in new[] { "raw", "policy", "provider", "operator", "quality", "storage", "recordedAt" }) Assert.DoesNotContain(sensitive, body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/dev/events" + query + "&limit=2")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/dev/events" + query + "&limit=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/dev/events")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/dev/events/" + result.EventId + query)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/dev/events/" + result.EventId + "/history" + query)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/api/v1/dev/events", null)).StatusCode);
        // PublicDisplay and an impersonated source code cannot disclose unregistered payloads.
        var unregistered = SyntheticFootballResultsDemo.Correction.Replace(",2,2,1,0,", ",4,2,1,0,", StringComparison.Ordinal);
        Assert.Equal(ImportOutcome.Succeeded, (await s.Import(unregistered)).Outcome);
        var laterQuery = "?asOfUtc=" + Uri.EscapeDataString((await s.Now()).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/dev/events/" + result.EventId + "/result" + laterQuery)).StatusCode);
        var policy = await s.Db.SourcePolicies.Include(p => p.Audit).Include(p => p.Permissions).SingleAsync(p => p.DataSourceId == s.Source);
        policy.Revoke(Guid.NewGuid(), "synthetic-reviewer", "Revoke development display", await s.Now()); await s.Db.SaveChangesAsync();
        var forbidden = await client.GetAsync("/api/v1/dev/events" + query);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.DoesNotContain("Amber Comets", await forbidden.Content.ReadAsStringAsync());
        await using var prod = new ApiHost(fixture.GetConnectionString(), s.Root, "Production"); using var production = prod.CreateClient(new() { BaseAddress = new("https://localhost") });
        foreach (var route in new[] { "/api/v1/dev/events", "/api/v1/dev/events/" + result.EventId, "/api/v1/dev/events/" + result.EventId + "/result", "/api/v1/dev/events/" + result.EventId + "/history" })
            Assert.Equal(HttpStatusCode.NotFound, (await production.GetAsync(route + query)).StatusCode);
    }

    [Theory]
    [InlineData("UPDATE football.\"Results\" SET \"Version\" = \"Version\"")]
    [InlineData("DELETE FROM football.\"Results\"")]
    [InlineData("TRUNCATE football.\"Results\" CASCADE")]
    [InlineData("UPDATE datasets.\"ResultArtifacts\" SET \"Hash\" = \"Hash\"")]
    [InlineData("DELETE FROM datasets.\"ResultArtifacts\"")]
    [InlineData("TRUNCATE datasets.\"ResultArtifacts\" CASCADE")]
    public async Task Ordinary_SQL_cannot_mutate_result_history_or_artifacts(string sql)
    {
        await using var db = fixture.CreateContext(); await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
    }
}
