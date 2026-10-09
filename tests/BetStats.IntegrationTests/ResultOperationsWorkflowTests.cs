using BetStats.Application.Coverage;
using BetStats.Application.Datasets;
using BetStats.Application.Football;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Domain.Coverage;
using BetStats.Domain.Football;
using BetStats.Domain.Governance;
using BetStats.Domain.Observations;
using BetStats.Domain.Quality;
using BetStats.Infrastructure;
using BetStats.Infrastructure.Football;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BetStats.IntegrationTests;

public sealed class ResultOperationsWorkflowTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed class Clock(DateTime at) : TimeProvider { public override DateTimeOffset GetUtcNow() => new(at); }
    internal sealed class Scenario : IAsyncDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public required IServiceScope Scope { get; init; }
        public required string Root { get; init; }
        public required SyntheticResultScenario Fixture { get; init; }
        public Guid Source { get; set; }
        public BetStatsDbContext Db => Get<BetStatsDbContext>();
        public T Get<T>() where T : notnull => Scope.ServiceProvider.GetRequiredService<T>();
        public Task<DateTime> Now() => Db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        public Task<ImportReport> Import(string? csv = null) => Get<FootballIngestion>().RunAsync(new FootballDataFixtureAdapter(Source,
            SyntheticFootballDemo.Bytes(csv ?? Fixture.Csv), TimeProvider.System), Fixture.Scope, Get<RequestBudget>());
        public async Task<FootballResultQuery> Query(DateTime? asOf = null)
        { var r = await Db.FootballResults.FirstAsync(r => r.SourceId == Source); return new(r.CompetitionId, r.SeasonId, asOf ?? await Now(), DataPurpose.InternalAnalytics, new(), SourceId: Source); }
        public async Task<ResultCoverageQuery> Coverage(DateTime? asOf = null)
        { var q = await Query(asOf); return new(new(Source, q.CompetitionId, q.SeasonId, null, Fixture.Scope.CompetitionReference, Fixture.Scope.SeasonReference,
            new(IntervalKind.Calendar, null, null, Fixture.HistoryStart, Fixture.HistoryStart.AddDays(2), "UTC-calendar")), q); }
        public async Task<RawCapture> Raw<T>(T value)
        {
            var persistence = Get<IFootballIngestionPersistence>(); await persistence.EnsureCaptureAllowedAsync(Source, default);
            var attempt = await persistence.BeginAsync(Guid.NewGuid(), Source, default); var bytes = CanonicalDatasetJson.Serialize(value);
            var store = Get<IRawPayloadStore>(); var staged = await store.StageAsync(bytes);
            var raw = await persistence.CaptureAsync(attempt, new(bytes, "application/json", await Now()), staged, Fixture.Scope, default);
            await store.FinalizeAsync(staged); await persistence.CompleteAsync(attempt with { Outcome = ImportOutcome.Succeeded, RetrievedPayloads = 1 }, default); return raw;
        }
        public async Task<ResultInventoryEvidence> Claim(ResultCoverageStatus status = ResultCoverageStatus.Complete, DateTime? until = null, ResultCoverageScope? scope = null, Guid? corrects = null, Guid[]? ids = null)
        {
            var query = await Coverage(); scope ??= query.Scope;
            var selected = await Db.FootballResults.AsNoTracking().Where(r => r.SourceId == Source && r.EventDate >= scope.Interval.StartDate && r.EventDate < scope.Interval.EndDate).ToArrayAsync();
            // Value is an owned JSON-converted property: filter status in memory below.
            selected = selected.Where(r => FootballResultRules.LabelEligible(r.Value)).GroupBy(r => r.ProviderIdentityId).Select(g => g.MaxBy(r => r.Version)!).ToArray();
            ids ??= status == ResultCoverageStatus.Empty ? [] : selected.Select(r => r.Id).Order().ToArray();
            var refs = selected.Where(r => ids.Contains(r.Id)).Select(r => r.SourceEventReference).Distinct().Order(StringComparer.Ordinal).ToArray();
            var proof = new ResultInventoryClaim(1, ResultCoverageRules.OwnedContract, scope, status, refs, ids);
            var raw = await Raw(proof); return await Get<IResultGovernance>().RecordAsync(new(raw.Id, proof, raw.RetrievedAtUtc,
                until ?? (await Now()).AddDays(10), "operator:fiction", "Explicit fictional inventory", true, corrects));
        }
        public Task<ResultInventoryReview> Approve(ResultInventoryEvidence e, int sequence = 0) => Get<IResultGovernance>().ReviewAsync(new(e.Id, sequence, true, "operator:review", "Verify closed fictional inventory", true));
        public async Task<FootballResultDatasetRequest> Request()
        {
            var q = await Query(); var target = await Db.Observations.Where(o => o.DataSourceId == Source && o.Type == ObservationType.EventDate &&
                Db.ProviderIdentities.Any(i => i.Id == o.ProviderIdentityId && i.ExternalId == "provider:clock-target")).SingleAsync();
            return new(new(new(2, BetStats.Domain.Sports.ReferenceSports.All.Single(s => s.Code == "football").Id, q.CompetitionId, q.SeasonId, Fixture.Scope.CompetitionReference, Fixture.Scope.SeasonReference,
                Fixture.HistoryStart.AddDays(-40), Fixture.TargetDate.AddDays(10), q.AsOfUtc, DatasetMode.HistoricalAsKnown, null, 1, 2,
                DataPurpose.InternalAnalytics, new(), "UTC-calendar", "eligible-observed-metadata-v1", "fail-closed-v1", [new(target.Id, q.AsOfUtc)]), "operator:build", "Freeze synthetic results"), q.AsOfUtc);
        }
        public async Task<EventEndEvidence> End(EventTimeValue? value = null, Guid? corrects = null)
        {
            var r = await Db.FootballResults.Where(r => r.SourceId == Source && r.SourceEventReference == "provider:clock-result-1").OrderByDescending(r => r.Version).FirstAsync();
            value ??= new(r.EventDate, new(18, 23), null, 0, null, EventTimePrecision.Minute);
            var claim = new EventEndSourceClaim(1, r.RawId, r.SourceEventReference, new(r.CompetitionReference, r.SeasonReference), r.Id, value);
            var raw = await Raw(claim); return await Get<IResultGovernance>().RecordEndAsync(new(raw.Id, claim, raw.RetrievedAtUtc, "operator:end", "Explicit fictional source end", true, corrects));
        }
        public async ValueTask DisposeAsync() { Scope.Dispose(); await Provider.DisposeAsync(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    internal async Task<Scenario> Create(bool allowDisplay = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "betstats-bs010-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:BetStats"] = fixture.GetConnectionString(), ["Ingestion:RawStoragePath"] = root }).Build();
        var provider = new ServiceCollection().AddPersistence(config).BuildServiceProvider(); var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BetStatsDbContext>(); var at = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        var scenario = new Scenario { Provider = provider, Scope = scope, Root = root, Fixture = SyntheticResultScenario.Create(new Clock(at)) };
        scenario.Source = await SyntheticFootballDemo.PrepareAsync(db, true, "bs010-fiction-" + Guid.NewGuid().ToString("N"), allowSyntheticDisplay: allowDisplay, fixtureScope: scenario.Fixture.Scope);
        Assert.Equal(ImportOutcome.Succeeded, (await scenario.Import()).Outcome); return scenario;
    }
    [Fact]
    public async Task Independent_inventory_requires_review_and_preserves_historical_knowledge()
    {
        await using var s = await Create(); var coverage = s.Get<IResultGovernance>();
        Assert.Equal(ResultCoverageStatus.Unknown, (await coverage.ReportAsync(await s.Coverage())).Status);
        var claim = await s.Claim(); var before = await s.Now(); Assert.True((await s.Approve(claim)).Approved);
        Assert.Equal(ResultCoverageStatus.Unknown, (await coverage.ReportAsync(await s.Coverage(before))).Status);
        Assert.Equal(ResultCoverageStatus.Complete, (await coverage.ReportAsync(await s.Coverage())).Status);
        var retrospective = await s.Coverage(before); retrospective = retrospective with { Results = retrospective.Results with { Mode = DatasetMode.RetrospectiveReconstruction, ReconstructionAtUtc = await s.Now() } };
        Assert.Equal(ResultCoverageStatus.Complete, (await coverage.ReportAsync(retrospective)).Status);
        var wider = await s.Coverage(); wider = wider with { Scope = wider.Scope with { Interval = wider.Scope.Interval with { EndDate = wider.Scope.Interval.EndDate!.Value.AddDays(1) } } };
        Assert.Equal(ResultCoverageStatus.Partial, (await coverage.ReportAsync(wider)).Status);
    }
    [Fact]
    public async Task Empty_is_explicit_and_cannot_hide_finished_metadata_without_results()
    {
        await using var s = await Create(); var q = await s.Coverage(); var emptyScope = q.Scope with { Interval = q.Scope.Interval with { StartDate = s.Fixture.HistoryStart.AddDays(-3), EndDate = s.Fixture.HistoryStart } };
        var empty = await s.Claim(ResultCoverageStatus.Empty, scope: emptyScope); Assert.True((await s.Approve(empty)).Approved);
        Assert.Equal(ResultCoverageStatus.Empty, (await s.Get<IResultGovernance>().ReportAsync(q with { Scope = emptyScope, Results = await s.Query() })).Status);
        var csv = "Div,Date,HomeTeam,AwayTeam,FTR,MatchId\nFICT," + s.Fixture.HistoryStart.AddDays(-1).ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture) + ",Amber Comets,Cobalt Owls,H,metadata-only\n";
        Assert.Equal(ImportOutcome.Succeeded, (await s.Import(csv)).Outcome);
        Assert.False((await s.Approve(empty, 1)).Approved);
        Assert.NotEqual(ResultCoverageStatus.Empty, (await s.Get<IResultGovernance>().ReportAsync(q with { Scope = emptyScope, Results = await s.Query() })).Status);
    }
    [Fact]
    public async Task Partial_and_expired_inventory_never_become_complete()
    {
        await using var s = await Create(); var first = await s.Db.FootballResults.Where(r => r.SourceId == s.Source && r.SourceEventReference == "provider:clock-result-1").Select(r => r.Id).SingleAsync();
        // Allow publication/review queries to finish under concurrent container load before testing expiry.
        var expires = (await s.Now()).AddSeconds(15);
        var claim = await s.Claim(ResultCoverageStatus.Partial, ids: [first], until: expires); Assert.True((await s.Approve(claim)).Approved);
        Assert.Equal(ResultCoverageStatus.Partial, (await s.Get<IResultGovernance>().ReportAsync(await s.Coverage())).Status);
        var remaining = expires - await s.Now();
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining + TimeSpan.FromMilliseconds(200));
        Assert.Equal(ResultCoverageStatus.Expired, (await s.Get<IResultGovernance>().ReportAsync(await s.Coverage())).Status);
    }
    [Fact]
    public async Task Metadata_completion_on_an_unchanged_date_cannot_prove_empty_result_coverage()
    {
        await using var s = await Create(); var q = await s.Coverage(); var day = s.Fixture.HistoryStart.AddDays(-1);
        var scope = q.Scope with { Interval = q.Scope.Interval with { StartDate = day, EndDate = s.Fixture.HistoryStart } };
        var date = day.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
        await s.Import(SyntheticFootballResultsDemo.Header + $"FICT,{date},Amber Comets,Cobalt Owls,status-only,Scheduled,Unknown,,,,,\n");
        var claim = await s.Claim(ResultCoverageStatus.Empty, scope: scope); Assert.True((await s.Approve(claim)).Approved);
        await s.Import($"Div,Date,HomeTeam,AwayTeam,FTR,MatchId\nFICT,{date},Amber Comets,Cobalt Owls,H,status-only\n");
        Assert.False((await s.Approve(claim, 1)).Approved);
        Assert.NotEqual(ResultCoverageStatus.Empty, (await s.Get<IResultGovernance>().ReportAsync(q with { Scope = scope, Results = await s.Query() })).Status);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Missing_or_corrupt_inventory_raw_fails_closed(bool corrupt)
    {
        await using var s = await Create(); var claim = await s.Claim(); Assert.True((await s.Approve(claim)).Approved);
        var raw = await s.Db.RawPayloads.SingleAsync(r => r.Id == claim.RawId); var path = Path.Combine(s.Root, raw.StorageKey + ".raw");
        if (corrupt) await File.WriteAllTextAsync(path, "corrupt"); else File.Delete(path);
        var report = await s.Get<IResultGovernance>().ReportAsync(await s.Coverage()); Assert.Equal(ResultCoverageStatus.Unknown, report.Status);
        Assert.Contains("result_inventory_raw_or_contract_unavailable", report.Reasons);
    }
    [Fact]
    public async Task Result_correction_requires_a_new_inventory_and_keeps_old_cutoff_complete()
    {
        await using var s = await Create(); var claim = await s.Claim(); Assert.True((await s.Approve(claim)).Approved); var before = await s.Now();
        Assert.Equal(ImportOutcome.Succeeded, (await s.Import(s.Fixture.Correction)).Outcome);
        Assert.Equal(ResultCoverageStatus.Conflict, (await s.Get<IResultGovernance>().ReportAsync(await s.Coverage())).Status);
        var corrected = await s.Claim(corrects: claim.Id); Assert.True((await s.Approve(corrected)).Approved);
        Assert.Equal(ResultCoverageStatus.Complete, (await s.Get<IResultGovernance>().ReportAsync(await s.Coverage())).Status);
        Assert.Equal(ResultCoverageStatus.Complete, (await s.Get<IResultGovernance>().ReportAsync(await s.Coverage(before))).Status);
    }
    [Fact]
    public async Task End_claims_preserve_explicit_corrections_and_do_not_guess_unknowns()
    {
        await using var s = await Create(); var unknown = await s.End(new(null, null, null, null, null, EventTimePrecision.Unknown)); var before = await s.Now();
        Assert.False(Assert.Single(await s.Get<IResultGovernance>().EndsAsync(await s.Query())).Eligible);
        var precise = await s.End(corrects: unknown.Id); Assert.Equal(2, precise.Version);
        Assert.True(Assert.Single(await s.Get<IResultGovernance>().EndsAsync(await s.Query())).Eligible);
        Assert.Equal(unknown.Id, Assert.Single(await s.Get<IResultGovernance>().EndsAsync(await s.Query(before))).Evidence.Id);
        await s.End(new(s.Fixture.HistoryStart, new(18, 45), null, 0, null, EventTimePrecision.Minute));
        Assert.All(await s.Get<IResultGovernance>().EndsAsync(await s.Query()), e => { Assert.False(e.Eligible); Assert.Contains("event_end_conflict", e.Reasons); });
    }
    [Fact]
    public async Task End_original_context_and_raw_are_verified()
    {
        await using var s = await Create(); var end = await s.End(); var r = await s.Db.FootballResults.SingleAsync(r => r.Id == end.ResultObservationId);
        var wrong = new EventEndSourceClaim(1, r.RawId, r.SourceEventReference, new(r.CompetitionReference, "wrong-season"), r.Id, end.Value); var raw = await s.Raw(wrong);
        await Assert.ThrowsAsync<InvalidDataException>(() => s.Get<IResultGovernance>().RecordEndAsync(new(raw.Id, wrong, raw.RetrievedAtUtc, "operator", "Wrong context", true)));
        var payload = await s.Db.RawPayloads.SingleAsync(x => x.Id == end.RawId); File.Delete(Path.Combine(s.Root, payload.StorageKey + ".raw"));
        Assert.Contains("event_end_raw_integrity_or_context", Assert.Single(await s.Get<IResultGovernance>().EndsAsync(await s.Query())).Reasons);
    }
    [Fact]
    public async Task Durable_build_replay_and_independent_operations_deduplicate_without_changing_legacy_bytes()
    {
        await using var s = await Create(); var request = await s.Request(); var legacy = await s.Get<IFootballResultDatasets>().BuildAsync(request);
        var legacyBytes = (await s.Db.FootballResultArtifacts.AsNoTracking().SingleAsync(a => a.Id == legacy.Id)).Content;
        var ops = s.Get<IResultDatasetOperations>(); var id = Guid.NewGuid(); var first = await ops.BuildAsync(new(id, request, "operator", "Build fictional v3", true));
        Assert.Equal(ResultOperationStatus.Succeeded, first.Status); Assert.Equal(3, first.Sequence);
        var replay = await ops.BuildAsync(new(id, request, "other-operator", "Explicit idempotent replay", true)); Assert.Equal(first.SnapshotId, replay.SnapshotId);
        var independent = await ops.BuildAsync(new(Guid.NewGuid(), request, "operator", "Same frozen evidence", true)); Assert.Equal(first.SnapshotId, independent.SnapshotId);
        Assert.NotNull((await s.Get<IFootballResultDatasets>().InspectAsync(first.SnapshotId!.Value)).Manifest.ResultGovernance);
        Assert.Equal(legacyBytes, (await s.Db.FootballResultArtifacts.AsNoTracking().SingleAsync(a => a.Id == legacy.Id)).Content);
        var deep = await ops.VerifyAsync(first.SnapshotId.Value, true); Assert.True(deep.Integrity && deep.FeaturesReproducible && deep.CurrentlyAuthorized && deep.RawAvailable == true && deep.RawHashVerified == true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.BuildAsync(new(id, request with { LabelAsOfUtc = request.LabelAsOfUtc.AddTicks(-10) }, "operator", "Mismatching request", true)));
        Assert.Equal(3, await s.Db.ResultOperations.CountAsync(e => e.OperationId == id));
    }
    [Fact]
    public async Task Concurrent_request_has_one_owner_and_one_successful_publication()
    {
        await using var s = await Create(); var request = await s.Request(); var operation = Guid.NewGuid();
        using var left = s.Provider.CreateScope(); using var right = s.Provider.CreateScope();
        var results = await Task.WhenAll(left.ServiceProvider.GetRequiredService<IResultDatasetOperations>().BuildAsync(new(operation, request, "left", "Concurrent build", true)),
            right.ServiceProvider.GetRequiredService<IResultDatasetOperations>().BuildAsync(new(operation, request, "right", "Concurrent build", true)));
        Assert.Contains(results, r => r.Status == ResultOperationStatus.Succeeded);
        Assert.Equal(1, await s.Db.ResultOperations.CountAsync(e => e.OperationId == operation && e.Status == ResultOperationStatus.Running));
        Assert.Equal(1, await s.Db.ResultOperations.CountAsync(e => e.OperationId == operation && e.Status == ResultOperationStatus.Succeeded));
    }
    private async Task<ResultOperationEvent> Interrupted(Scenario s, Guid operation, FootballResultDatasetRequest request, TimeSpan lease)
    {
        var bytes = CanonicalDatasetJson.Serialize(request); var fingerprint = ResultDatasetOperations.Fingerprint(request);
        s.Db.Add(new ResultOperationEvent { Id = Guid.NewGuid(), OperationId = operation, Sequence = 1, Status = ResultOperationStatus.Requested,
            Fingerprint = fingerprint, Request = bytes, OperatorId = "crashed-owner", Reason = "Simulate committed request before process death" }); await s.Db.SaveChangesAsync();
        var running = new ResultOperationEvent { Id = Guid.NewGuid(), OperationId = operation, Sequence = 2, Status = ResultOperationStatus.Running, Fingerprint = fingerprint,
            Request = bytes, OwnerToken = Guid.NewGuid(), LeaseUntilUtc = (await s.Now()) + lease, OperatorId = "crashed-owner", Reason = "Simulate process death after committed claim" };
        s.Db.Add(running); await s.Db.SaveChangesAsync(); return running;
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Database_rejects_missing_or_unbounded_running_leases(bool unbounded)
    {
        await using var s = await Create(); var request = await s.Request(); var operation = Guid.NewGuid(); var bytes = CanonicalDatasetJson.Serialize(request); var fingerprint = ResultDatasetOperations.Fingerprint(request);
        s.Db.Add(new ResultOperationEvent { Id = Guid.NewGuid(), OperationId = operation, Sequence = 1, Status = ResultOperationStatus.Requested, Fingerprint = fingerprint,
            Request = bytes, OperatorId = "operator", Reason = "Explicit request" }); await s.Db.SaveChangesAsync();
        s.Db.Add(new ResultOperationEvent { Id = Guid.NewGuid(), OperationId = operation, Sequence = 2, Status = ResultOperationStatus.Running, Fingerprint = fingerprint,
            Request = bytes, OwnerToken = Guid.NewGuid(), LeaseUntilUtc = unbounded ? (await s.Now()).AddHours(1) : null, OperatorId = "operator", Reason = "Invalid lease cannot grant publication" });
        await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync()); s.Db.ChangeTracker.Clear();
        Assert.Equal(ResultOperationStatus.Requested, (await s.Get<IResultDatasetOperations>().InspectAsync(operation)).Status);
    }
    [Fact]
    public async Task Recovery_rejects_live_lease_and_wrong_fingerprint_then_fences_old_owner()
    {
        await using var s = await Create(); var request = await s.Request(); var id = Guid.NewGuid(); var running = await Interrupted(s, id, request, TimeSpan.FromSeconds(2)); var ops = s.Get<IResultDatasetOperations>();
        var recovery = new ResultRecoveryRequest(id, running.Fingerprint, "operator:recovery", "Recover confirmed stopped owner after lease expiry", true);
        Assert.Equal(ResultOperationStatus.Running, (await ops.RecoverAsync(recovery)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.RecoverAsync(recovery with { ExpectedFingerprint = new('f', 64) }));
        await Task.Delay(2100);
        s.Db.Add(new ResultOperationEvent { Id = Guid.NewGuid(), OperationId = id, Sequence = 3, Status = ResultOperationStatus.Failed, Fingerprint = running.Fingerprint,
            Request = running.Request, OwnerToken = running.OwnerToken, OperatorId = "expired-owner", Reason = "Expired owner must be fenced before recovery too" });
        await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync()); s.Db.ChangeTracker.Clear();
        var final = await ops.RecoverAsync(recovery); Assert.Equal(ResultOperationStatus.Succeeded, final.Status); Assert.Equal(4, final.Sequence);
        s.Db.Add(new ResultOperationEvent { Id = Guid.NewGuid(), OperationId = id, Sequence = 5, Status = ResultOperationStatus.Failed, Fingerprint = running.Fingerprint,
            Request = running.Request, OwnerToken = running.OwnerToken, OperatorId = "old-owner", Reason = "Old owner must be fenced" });
        await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync()); s.Db.ChangeTracker.Clear();
        Assert.Equal(final.SnapshotId, (await ops.RecoverAsync(recovery)).SnapshotId);
    }
    [Fact]
    public async Task Concurrent_recovery_issues_one_new_owner_and_one_successful_artifact()
    {
        await using var s = await Create(); var request = await s.Request(); var operation = Guid.NewGuid(); var running = await Interrupted(s, operation, request, TimeSpan.FromSeconds(1));
        await Task.Delay(1100); using var left = s.Provider.CreateScope(); using var right = s.Provider.CreateScope();
        var recovery = new ResultRecoveryRequest(operation, running.Fingerprint, "operator", "Concurrent explicit recovery", true);
        var reports = await Task.WhenAll(left.ServiceProvider.GetRequiredService<IResultDatasetOperations>().RecoverAsync(recovery), right.ServiceProvider.GetRequiredService<IResultDatasetOperations>().RecoverAsync(recovery));
        Assert.Contains(reports, r => r.Status == ResultOperationStatus.Succeeded);
        Assert.Equal(2, await s.Db.ResultOperations.CountAsync(e => e.OperationId == operation && e.Status == ResultOperationStatus.Running));
        Assert.Equal(1, await s.Db.ResultOperations.CountAsync(e => e.OperationId == operation && e.Status == ResultOperationStatus.Succeeded));
    }
    [Fact]
    public async Task Failed_build_can_recover_after_raw_restoration_without_duplicate_artifacts()
    {
        await using var s = await Create(); var request = await s.Request(); var raw = await s.Db.RawPayloads.FirstAsync(r => r.DataSourceId == s.Source);
        var path = Path.Combine(s.Root, raw.StorageKey + ".raw"); var bytes = await File.ReadAllBytesAsync(path); await File.WriteAllTextAsync(path, "corrupt");
        var id = Guid.NewGuid(); var ops = s.Get<IResultDatasetOperations>(); var failed = await ops.BuildAsync(new(id, request, "operator", "Build with damaged RAW", true));
        Assert.Equal(ResultOperationStatus.Failed, failed.Status); Assert.Null(failed.SnapshotId);
        await File.WriteAllBytesAsync(path, bytes); var recovery = await ops.RecoverAsync(new(id, ResultDatasetOperations.Fingerprint(request), "operator", "RAW restored; retry same definition", true));
        Assert.Equal(ResultOperationStatus.Succeeded, recovery.Status); Assert.Equal(1, await s.Db.ResultOperations.CountAsync(e => e.OperationId == id && e.Status == ResultOperationStatus.Succeeded));
    }
    [Fact]
    public async Task Revocation_denies_coverage_end_build_recovery_and_deep_raw_reads()
    {
        await using var s = await Create(); var end = await s.End(); var request = await s.Request(); var ops = s.Get<IResultDatasetOperations>(); var built = await ops.BuildAsync(new(Guid.NewGuid(), request, "operator", "Build before revoke", true));
        Assert.Equal(ResultOperationStatus.Succeeded, built.Status);
        var p = await s.Db.SourcePolicies.Include(p => p.Audit).Include(p => p.Permissions).SingleAsync(p => p.DataSourceId == s.Source);
        p.Revoke(Guid.NewGuid(), "operator", "Revoke fictional usage", await s.Now()); await s.Db.SaveChangesAsync();
        var coverage = await s.Coverage(); var query = await s.Query();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Get<IResultGovernance>().ReportAsync(coverage));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Get<IResultGovernance>().EndsAsync(query));
        var verification = await ops.VerifyAsync(built.SnapshotId!.Value, true); Assert.True(verification.Integrity); Assert.False(verification.CurrentlyAuthorized); Assert.Null(verification.RawAvailable);
        Assert.Equal(ResultOperationStatus.Failed, (await ops.BuildAsync(new(Guid.NewGuid(), request, "operator", "Denied new build", true))).Status);
    }
    [Fact]
    public async Task Cancellation_after_durable_claim_is_terminal_and_explicitly_recoverable()
    {
        await using var s = await Create(); var request = await s.Request(); var operation = Guid.NewGuid();
        await using var blocker = fixture.CreateContext(); await using var tx = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM ingestion.\"DataSources\" WHERE \"Id\"={s.Source} FOR UPDATE");
        using var buildScope = s.Provider.CreateScope(); using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var build = buildScope.ServiceProvider.GetRequiredService<IResultDatasetOperations>().BuildAsync(new(operation, request, "operator", "Cancel after claimed work", true), cancellation.Token);
        for (var i = 0; i < 100 && !await s.Db.ResultOperations.AnyAsync(e => e.OperationId == operation && e.Status == ResultOperationStatus.Running); i++) await Task.Delay(20);
        Assert.True(await s.Db.ResultOperations.AnyAsync(e => e.OperationId == operation && e.Status == ResultOperationStatus.Running));
        cancellation.Cancel(); var cancelled = await build; Assert.Equal(ResultOperationStatus.Cancelled, cancelled.Status); Assert.Null(cancelled.SnapshotId);
        await tx.RollbackAsync();
        Assert.Equal(ResultOperationStatus.Succeeded, (await s.Get<IResultDatasetOperations>().RecoverAsync(new(operation, ResultDatasetOperations.Fingerprint(request), "operator", "Explicit retry cancelled work", true))).Status);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Deep_verification_reports_missing_or_corrupt_raw_without_changing_artifact_integrity(bool corrupt)
    {
        await using var s = await Create(); var request = await s.Request(); var ops = s.Get<IResultDatasetOperations>(); var built = await ops.BuildAsync(new(Guid.NewGuid(), request, "operator", "Freeze before RAW loss", true));
        Assert.Equal(ResultOperationStatus.Succeeded, built.Status);
        var raw = await s.Db.RawPayloads.FirstAsync(r => r.DataSourceId == s.Source); var path = Path.Combine(s.Root, raw.StorageKey + ".raw");
        if (corrupt) await File.WriteAllTextAsync(path, "corrupt"); else File.Delete(path);
        var verification = await ops.VerifyAsync(built.SnapshotId!.Value, true); Assert.True(verification.Integrity && verification.FeaturesReproducible); Assert.False(verification.RawHashVerified);
        if (!corrupt) Assert.False(verification.RawAvailable);
    }
    [Theory] [InlineData("coverage.\"ResultInventory\"")] [InlineData("coverage.\"ResultInventoryReviews\"")] [InlineData("football.\"EventEnds\"")] [InlineData("datasets.\"ResultOperations\"")]
    public async Task Ordinary_sql_cannot_update_delete_or_truncate_new_history(string table)
    {
        await using var db = fixture.CreateContext();
        foreach (var command in new[] { "UPDATE " + table + " SET \"Reason\"=\"Reason\"", "DELETE FROM " + table, "TRUNCATE " + table + " CASCADE" })
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(command));
    }
    [Fact]
    public void Clock_controlled_fixtures_are_deterministic_across_year_boundaries()
    {
        var clock = new Clock(new(2033, 1, 1, 12, 0, 0, DateTimeKind.Utc)); var one = SyntheticResultScenario.Create(clock); var two = SyntheticResultScenario.Create(clock);
        Assert.Equal(one, two); Assert.Equal(new DateOnly(2032, 12, 29), one.HistoryStart); Assert.Equal(new DateOnly(2033, 1, 3), one.TargetDate);
        Assert.Equal(4, new FootballResultsCsvParser().Parse(SyntheticFootballDemo.Bytes(one.Csv), one.Scope).Records.Count);
        Assert.Contains("01/10/2026", SyntheticFootballResultsDemo.Csv); // Legacy public demo bytes stay compatible.
    }
    [Fact]
    public async Task Development_operator_commands_enforce_approval_and_execute_build_verify_and_recovery()
    {
        await using var s = await Create(); var request = await s.Request(); var operation = Guid.NewGuid();
        IConfiguration Config(string action, bool approve = true) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Results:Action"] = action, ["Results:OperatorId"] = "operator:cli", ["Results:Reason"] = "Explicit fictional operator command test",
            ["Results:Approve"] = approve ? "true" : "false", ["Results:OperationId"] = operation.ToString(),
            ["Results:RequestJson"] = System.Text.Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(request)),
            ["Results:ExpectedFingerprint"] = ResultDatasetOperations.Fingerprint(request) }).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ResultOperatorCommand.RunAsync(s.Scope.ServiceProvider, Config("build-v3"), false));
        await Assert.ThrowsAsync<ArgumentException>(() => ResultOperatorCommand.RunAsync(s.Scope.ServiceProvider, Config("build-v3", false), true));
        Assert.Equal(0, await ResultOperatorCommand.RunAsync(s.Scope.ServiceProvider, Config("build-v3"), true));
        Assert.Equal(0, await ResultOperatorCommand.RunAsync(s.Scope.ServiceProvider, Config("operation"), true));
        Assert.Equal(0, await ResultOperatorCommand.RunAsync(s.Scope.ServiceProvider, Config("recover-v3"), true));
        var built = await s.Get<IResultDatasetOperations>().InspectAsync(operation);
        var verification = new ConfigurationBuilder().AddConfiguration(Config("verify-v3-deep")).AddInMemoryCollection(new Dictionary<string, string?> { ["Results:SnapshotId"] = built.SnapshotId.ToString() }).Build();
        Assert.Equal(0, await ResultOperatorCommand.RunAsync(s.Scope.ServiceProvider, verification, true));
        var query = await s.Coverage(); var inspect = new ConfigurationBuilder().AddConfiguration(Config("coverage")).AddInMemoryCollection(new Dictionary<string, string?> { ["Results:QueryJson"] = System.Text.Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(query)) }).Build();
        Assert.Equal(0, await ResultOperatorCommand.RunAsync(s.Scope.ServiceProvider, inspect, true));
    }
}
