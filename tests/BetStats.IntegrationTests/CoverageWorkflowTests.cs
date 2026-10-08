using System.Globalization;
using System.Text;
using BetStats.Application.Coverage;
using BetStats.Application.Datasets;
using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Domain.Coverage;
using BetStats.Domain.Governance;
using BetStats.Domain.Observations;
using BetStats.Domain.Quality;
using BetStats.Infrastructure.Coverage;
using BetStats.Infrastructure.Datasets;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BetStats.IntegrationTests;

public sealed class CoverageWorkflowTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string Contract = "project-owned-fixture-inventory-v1";
    private sealed class Scenario : IAsyncDisposable
    {
        public BetStatsDbContext Db { get; }
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "betstats-coverage-" + Guid.NewGuid().ToString("N"));
        public FileSystemRawPayloadStore Store { get; }
        public Guid Source { get; set; }
        public DatasetDefinition Definition { get; set; } = null!;
        public Scenario(BetStatsDbContext db) { Db = db; Store = new(Root); }
        public SourcePolicyEvaluator Policies => new(new SourcePolicyHistory(Db));
        public SourceOperationalStatusReader Sources => new(Db);
        public AnalyticalQualityGate Gate => new(Db, Policies, Sources);
        public HistoricalCoverage Coverage => new(Db, Store, new FootballDataCsvParser(), Gate, Policies, Sources);
        public PostgreSqlDatasets Datasets => new(Db, Store, new FootballDataCsvParser(), Gate, Policies, Sources, Coverage);
        public FootballIngestion Ingestion => new(new(Policies, TimeProvider.System, Sources), new FootballIngestionPersistence(Db, Policies, Sources), Store, new FootballDataCsvParser());
        public Task<DateTime> Now() => Db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        public CoverageScope Scope(DateOnly? start = null, DateOnly? end = null, Guid? participant = null) => new(Source, Definition.SportId, Definition.CompetitionId, Definition.SeasonId, participant,
            "football-match", ObservationType.EventDate, Definition.CompetitionReference, Definition.SeasonReference,
            new(IntervalKind.Calendar, null, null, start ?? new(2026, 1, 1), end ?? new(2026, 1, 6), "UTC-calendar"));
        public CoverageQuery Query(CoverageScope scope, DateTime at, DatasetMode mode = DatasetMode.HistoricalAsKnown, DateTime? reconstruction = null) => new(scope, at, mode, reconstruction, DataPurpose.InternalAnalytics, new());
        public async Task<RawPayload> Raw<T>(T value)
        {
            var stored = await Store.StageAsync(CanonicalDatasetJson.Serialize(value)); await Store.FinalizeAsync(stored); var now = await Now();
            var raw = new RawPayload { Id = Guid.NewGuid(), DataSourceId = Source, RetrievedAtUtc = now, CreatedAtUtc = now,
                ContentHashSha256 = stored.Hash, StorageKey = stored.StorageKey, ContentType = "application/json", ByteLength = stored.Length };
            Db.RawPayloads.Add(raw); await Db.SaveChangesAsync(); return raw;
        }
        public async Task<CoverageEvidence> Claim(CoverageScope scope, CoverageStatus status, CoverageBasis basis = CoverageBasis.OwnedFixtureInventory, Guid[]? ids = null)
        {
            ids ??= await Db.Observations.Where(o => o.DataSourceId == Source && o.Type == scope.ObservationType && o.DateValue >= scope.Interval.StartDate && o.DateValue < scope.Interval.EndDate)
                .OrderBy(o => o.Id).Select(o => o.Id).ToArrayAsync();
            var raw = await Raw(new CoverageInventory(Contract, scope, status, ids));
            return await Coverage.RecordAsync(new(scope, status, basis, "fictional-closed-inventory", raw.Id, 1, null, raw.RetrievedAtUtc, (await Now()).AddDays(30), "operator:fixture", "Synthetic inventory only"));
        }
        public Task<CoverageReview> Approve(CoverageEvidence evidence, int sequence = 0) => Coverage.ReviewAsync(new(evidence.Id, sequence, CoverageReviewStatus.Approved, Contract, "operator:review", "Review project-owned fictional inventory"));
        public async Task<DatasetDefinition> CurrentDefinition(int version = 2)
        { var now = await Now(); return Definition with { Version = version, FeatureSchemaVersion = version, AsOfUtc = now, Targets = [Definition.Targets[0] with { PredictionCutoffUtc = now }] }; }
        public Task<DatasetBuildResult> Build(DatasetDefinition definition) => Datasets.BuildAsync(new(definition, "operator:coverage", "Freeze fictional coverage"));
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private async Task<Scenario> Create(BetStatsDbContext? context = null)
    {
        var s = new Scenario(context ?? fixture.CreateContext()); var today = DateOnly.FromDateTime(await s.Now());
        (s.Source, s.Definition) = await SyntheticDatasetDemo.PrepareAsync(s.Db, s.Ingestion, new(new(100, 1000, 1, TimeSpan.FromSeconds(30)), TimeProvider.System),
            TimeProvider.System, true, "coverage-fiction-" + Guid.NewGuid().ToString("N"), today.AddDays(2));
        return s;
    }
    private static Guid Snapshot(DatasetBuildResult r) { Assert.True(r.Status == DatasetBuildStatus.Succeeded, r.FailureCode); return Assert.IsType<Guid>(r.SnapshotId); }
    [Fact]
    public async Task Ten_fictional_observed_events_without_a_contract_remain_unknown()
    {
        await using var s = await Create(); var csv = "Div,Date,HomeTeam,AwayTeam,FTR,MatchId\n" + string.Concat(Enumerable.Range(1, 10).Select(i =>
            "FICT," + new DateOnly(2026, 2, i).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) + ",Amber Comets,Cobalt Owls,H,coverage-ten-" + i + "\n"));
        var import = await s.Ingestion.RunAsync(new FootballDataFixtureAdapter(s.Source, SyntheticFootballDemo.Bytes(csv), TimeProvider.System), SyntheticFootballDemo.Scope,
            new(new(100, 1000, 1, TimeSpan.FromSeconds(30)), TimeProvider.System)); Assert.Equal(ImportOutcome.Succeeded, import.Outcome);
        var scope = s.Scope(new(2026, 2, 1), new(2026, 2, 11));
        Assert.Equal(10, await s.Db.Observations.CountAsync(o => o.DataSourceId == s.Source && o.Type == ObservationType.EventDate && o.DateValue >= scope.Interval.StartDate && o.DateValue < scope.Interval.EndDate));
        Assert.Equal(CoverageStatus.Unknown, (await s.Coverage.ReportAsync(s.Query(scope, await s.Now()))).Status);
    }
    [Fact]
    public async Task Exact_scope_review_and_historical_reconstruction_are_distinct()
    {
        await using var s = await Create(); var scope = s.Scope(); var claim = await s.Claim(scope, CoverageStatus.VerifiedComplete); var beforeReview = await s.Now();
        var approved = await s.Approve(claim); Assert.Equal(CoverageReviewStatus.Approved, approved.Status); var now = await s.Now();
        Assert.Equal(CoverageStatus.Unknown, (await s.Coverage.ReportAsync(s.Query(scope, beforeReview))).Status);
        Assert.Equal(CoverageStatus.VerifiedComplete, (await s.Coverage.ReportAsync(s.Query(scope, now))).Status);
        var retrospective = await s.Coverage.ReportAsync(s.Query(scope, beforeReview, DatasetMode.RetrospectiveReconstruction, now));
        Assert.Equal(DatasetMode.RetrospectiveReconstruction, retrospective.Query.Mode); Assert.Equal(CoverageStatus.VerifiedComplete, retrospective.Status);
        Assert.Equal(CoverageStatus.Unknown, (await s.Coverage.ReportAsync(s.Query(scope with { ParticipantId = Guid.NewGuid() }, now))).Status);
        Assert.Equal(CoverageStatus.Unknown, (await s.Coverage.ReportAsync(s.Query(scope with { ObservationType = ObservationType.EventStatus }, now))).Status);
        Assert.Equal(CoverageStatus.Partial, (await s.Coverage.ReportAsync(s.Query(scope with { Interval = scope.Interval with { EndDate = new(2026, 1, 10) } }, now))).Status);
    }
    [Fact]
    public async Task Manual_completeness_and_missing_inventory_are_not_approved()
    {
        await using var s = await Create(); var manual = await s.Claim(s.Scope(), CoverageStatus.VerifiedComplete, CoverageBasis.ManualStatement);
        Assert.Equal(CoverageReviewStatus.Rejected, (await s.Approve(manual)).Status);
        var id = await s.Db.Observations.Where(o => o.DataSourceId == s.Source && o.Type == ObservationType.EventDate && o.DateValue >= new DateOnly(2026, 1, 1) && o.DateValue < new DateOnly(2026, 1, 6)).Select(o => o.Id).FirstAsync();
        var incomplete = await s.Claim(s.Scope(), CoverageStatus.VerifiedComplete, ids: [id]);
        Assert.Equal(CoverageReviewStatus.Rejected, (await s.Approve(incomplete)).Status);
    }
    [Fact]
    public async Task Verified_empty_and_conflicting_new_inventory_do_not_hide_observations()
    {
        await using var s = await Create(); var scope = s.Scope(new(2026, 5, 1), new(2026, 5, 10)); var empty = await s.Claim(scope, CoverageStatus.VerifiedEmpty, ids: []);
        Assert.Equal(CoverageReviewStatus.Approved, (await s.Approve(empty)).Status); var before = await s.Now();
        Assert.Equal(CoverageStatus.VerifiedEmpty, (await s.Coverage.ReportAsync(s.Query(scope, before))).Status);
        var csv = "Div,Date,HomeTeam,AwayTeam,FTR,MatchId\nFICT,02/05/2026,Amber Comets,Cobalt Owls,H,coverage-contradiction\n";
        await s.Ingestion.RunAsync(new FootballDataFixtureAdapter(s.Source, SyntheticFootballDemo.Bytes(csv), TimeProvider.System), SyntheticFootballDemo.Scope,
            new(new(100, 1000, 1, TimeSpan.FromSeconds(30)), TimeProvider.System));
        var complete = await s.Claim(scope, CoverageStatus.VerifiedComplete); Assert.Equal(CoverageReviewStatus.Approved, (await s.Approve(complete)).Status);
        var report = await s.Coverage.ReportAsync(s.Query(scope, await s.Now())); Assert.Equal(CoverageStatus.Conflicting, report.Status); Assert.NotEmpty(report.ConflictingIntervals);
        Assert.Equal(CoverageStatus.VerifiedEmpty, (await s.Coverage.ReportAsync(s.Query(scope, before))).Status);
    }
    [Fact]
    public async Task Later_rejection_does_not_replace_earlier_approval()
    {
        await using var s = await Create(); var claim = await s.Claim(s.Scope(), CoverageStatus.VerifiedComplete); await s.Approve(claim); var before = await s.Now();
        await s.Coverage.ReviewAsync(new(claim.Id, 1, CoverageReviewStatus.Rejected, "fictional-withdrawal", "operator:review", "Withdraw fixture guarantee"));
        Assert.Equal(CoverageStatus.VerifiedComplete, (await s.Coverage.ReportAsync(s.Query(s.Scope(), before))).Status);
        Assert.Equal(CoverageStatus.Unknown, (await s.Coverage.ReportAsync(s.Query(s.Scope(), await s.Now()))).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Approve(claim, 1)); Assert.Equal(2, await s.Db.CoverageReviews.CountAsync(r => r.EvidenceId == claim.Id));
    }
    [Fact]
    public async Task Date_only_evidence_corrections_and_independent_claims_are_preserved()
    {
        await using var s = await Create(); var o = await s.Db.Observations.SingleAsync(o => o.Id == s.Definition.Targets[0].DateObservationId);
        var date = new EventTimeValue(o.DateValue, null, null, null, null, EventTimePrecision.DateOnly);
        var first = await s.Coverage.RecordTimeAsync(new(o.Id, o.RawPayloadId!.Value, date, "original-CSV-date", null, null, o.RetrievedAtUtc, "operator:time", "Keep source date precision"));
        var before = await s.Now(); Assert.Null(Assert.Single(await s.Coverage.TimesAsync(o.ProviderIdentityId, before, DatasetMode.HistoricalAsKnown, null, DataPurpose.InternalAnalytics, new())).Resolution.UtcInstant);
        var precise = new EventTimeValue(o.DateValue, new(12, 0), "Europe/Warsaw", null, null, EventTimePrecision.Minute); var identity = await s.Db.ProviderIdentities.SingleAsync(i => i.Id == o.ProviderIdentityId);
        var raw = await s.Raw(new EventTimeSourceClaim(1, o.RawPayloadId!.Value, identity.ExternalId, new(s.Definition.CompetitionReference, s.Definition.SeasonReference), precise));
        var correction = await s.Coverage.RecordTimeAsync(new(o.Id, raw.Id, precise, "explicit-fictional-time-correction", first.Id, null, raw.RetrievedAtUtc, "operator:time", "Explicit correction"));
        Assert.Equal(2, correction.Version); Assert.Equal(first.Id, correction.CorrectsId);
        Assert.Equal(first.Id, Assert.Single(await s.Coverage.TimesAsync(o.ProviderIdentityId, before, DatasetMode.HistoricalAsKnown, null, DataPurpose.InternalAnalytics, new())).Evidence.Id);
        Assert.NotNull(Assert.Single(await s.Coverage.TimesAsync(o.ProviderIdentityId, await s.Now(), DatasetMode.HistoricalAsKnown, null, DataPurpose.InternalAnalytics, new())).Resolution.UtcInstant);
        var independent = precise with { LocalDate = precise.LocalDate!.Value.AddDays(1) }; var otherRaw = await s.Raw(new EventTimeSourceClaim(1, o.RawPayloadId!.Value, identity.ExternalId, new(s.Definition.CompetitionReference, s.Definition.SeasonReference), independent));
        await s.Coverage.RecordTimeAsync(new(o.Id, otherRaw.Id, independent, "independent-reschedule-claim", null, null, otherRaw.RetrievedAtUtc, "operator:time", "Preserve conflict"));
        var results = await s.Coverage.TimesAsync(o.ProviderIdentityId, await s.Now(), DatasetMode.HistoricalAsKnown, null, DataPurpose.InternalAnalytics, new());
        Assert.Equal(2, results.Count); Assert.All(results, r => { Assert.Null(r.Resolution.UtcInstant); Assert.Equal("conflicting_event_time_claims", r.Resolution.Reason); });
        Assert.Equal(3, await s.Db.EventTimeEvidence.CountAsync(e => e.SourceId == s.Source));
    }
    [Fact]
    public async Task Backdated_recording_is_overwritten_by_database_and_future_claim_is_excluded()
    {
        await using var s = await Create(); var before = await s.Now(); var e = await s.Claim(s.Scope(), CoverageStatus.Partial, CoverageBasis.ManualStatement);
        Assert.True(e.RecordedAtUtc > before); Assert.Empty((await s.Coverage.ReportAsync(s.Query(s.Scope(), before))).Items);
        await s.Db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO coverage.\"Evidence\" (\"Id\",\"SourceId\",\"Scope\",\"Claim\",\"Basis\",\"EvidenceReference\",\"RawId\",\"RawHash\",\"PolicyId\",\"SupportingObservationIds\",\"Version\",\"RetrievedAtUtc\",\"AvailableAtUtc\",\"ValidUntilUtc\",\"OperatorId\",\"Reason\",\"RecordedAtUtc\") SELECT {Guid.NewGuid()},\"SourceId\",\"Scope\",\"Claim\",\"Basis\",\"EvidenceReference\",\"RawId\",\"RawHash\",\"PolicyId\",\"SupportingObservationIds\",\"Version\",\"RetrievedAtUtc\",\"AvailableAtUtc\",\"ValidUntilUtc\",\"OperatorId\",\"Reason\",{before.AddDays(-100)} FROM coverage.\"Evidence\" WHERE \"Id\"={e.Id}");
        Assert.All(await s.Db.CoverageEvidence.AsNoTracking().Where(c => c.SourceId == s.Source).ToListAsync(), c => Assert.True(c.RecordedAtUtc > before));
    }
    [Theory] [InlineData("Evidence")] [InlineData("Reviews")] [InlineData("EventTimes")]
    public async Task Sql_and_ef_bulk_cannot_mutate_even_empty_history(string table)
    {
        foreach (var operation in new[] { "UPDATE", "DELETE", "TRUNCATE" }) {
            await using var db = fixture.CreateContext(); var sql = operation == "UPDATE" ? $"UPDATE coverage.\"{table}\" SET \"Id\"=\"Id\"" : operation == "DELETE" ? $"DELETE FROM coverage.\"{table}\"" : $"TRUNCATE coverage.\"{table}\" CASCADE";
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql)); Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        await using var bulk = fixture.CreateContext();
        if (table == "Evidence") await Assert.ThrowsAsync<PostgresException>(() => bulk.CoverageEvidence.ExecuteUpdateAsync(p => p.SetProperty(e => e.Version, 1)));
        else if (table == "Reviews") await Assert.ThrowsAsync<PostgresException>(() => bulk.CoverageReviews.ExecuteDeleteAsync());
        else await Assert.ThrowsAsync<PostgresException>(() => bulk.EventTimeEvidence.ExecuteDeleteAsync());
    }
    [Fact]
    public async Task Tracked_ef_mutation_is_rejected_before_sql()
    {
        await using var s = await Create(); var claim = await s.Claim(s.Scope(), CoverageStatus.Partial, CoverageBasis.ManualStatement);
        s.Db.Entry(claim).Property(c => c.Version).CurrentValue = 2; await Assert.ThrowsAsync<InvalidOperationException>(() => s.Db.SaveChangesAsync());
    }
    [Fact]
    public async Task Expired_evidence_is_visible_and_cannot_qualify_a_feature()
    {
        await using var s = await Create(); var raw = await s.Raw(new CoverageInventory(Contract, s.Scope(), CoverageStatus.Partial, []));
        var claim = await s.Coverage.RecordAsync(new(s.Scope(), CoverageStatus.Partial, CoverageBasis.ManualStatement, "short-lived-fiction", raw.Id, 1, null,
            raw.RetrievedAtUtc, raw.RetrievedAtUtc.AddTicks(10), "operator:expiry", "Already expired synthetic validity"));
        var report = await s.Coverage.ReportAsync(s.Query(s.Scope(), await s.Now())); Assert.Equal(CoverageStatus.Expired, report.Status);
        Assert.Equal(claim.Id, Assert.Single(report.Items).Evidence.Id);
        Assert.Equal(FeatureCoverageOutcome.ExpiredCoverage, CoverageRules.Gate(new("observed", 1, [ObservationType.EventDate], null, false, "Completed", 1, true, false), [report]).Outcome);
        Assert.Equal(CoverageReviewStatus.Rejected, (await s.Approve(claim)).Status);
    }
    [Fact]
    public async Task A_sql_insert_cannot_turn_unrelated_csv_raw_into_an_exhaustive_inventory()
    {
        await using var s = await Create(); var scope = s.Scope(new(2026, 5, 1), new(2026, 5, 10));
        var claim = await s.Claim(scope, CoverageStatus.VerifiedEmpty, ids: []); var o = await s.Db.Observations.SingleAsync(o => o.Id == s.Definition.Targets[0].DateObservationId);
        var forgedId = Guid.NewGuid();
        await s.Db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO coverage.\"Evidence\" (\"Id\",\"SourceId\",\"Scope\",\"Claim\",\"Basis\",\"EvidenceReference\",\"RawId\",\"RawHash\",\"PolicyId\",\"SupportingObservationIds\",\"Version\",\"RetrievedAtUtc\",\"AvailableAtUtc\",\"ValidUntilUtc\",\"OperatorId\",\"Reason\") SELECT {forgedId},e.\"SourceId\",e.\"Scope\",e.\"Claim\",e.\"Basis\",e.\"EvidenceReference\",r.\"Id\",r.\"ContentHashSha256\",e.\"PolicyId\",e.\"SupportingObservationIds\",e.\"Version\",r.\"RetrievedAtUtc\",e.\"AvailableAtUtc\",e.\"ValidUntilUtc\",e.\"OperatorId\",e.\"Reason\" FROM coverage.\"Evidence\" e JOIN ingestion.\"RawPayloads\" r ON r.\"Id\"={o.RawPayloadId} WHERE e.\"Id\"={claim.Id}");
        // An ordinary writer may append a structurally valid ledger claim; RAW contract validation is still mandatory.
        var forged = await s.Coverage.InspectAsync(forgedId); Assert.Equal(CoverageReviewStatus.Rejected, (await s.Approve(forged)).Status);
        await s.Db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO coverage.\"Reviews\" (\"Id\",\"EvidenceId\",\"SourceId\",\"Sequence\",\"Status\",\"BasisReference\",\"OperatorId\",\"Reason\",\"ReviewedAtUtc\") VALUES ({Guid.NewGuid()},{forgedId},{s.Source},2,'Approved',{Contract},'operator:untrusted-sql','Test forged guarantee',clock_timestamp())");
        var report = await s.Coverage.ReportAsync(s.Query(scope, await s.Now())); Assert.Equal(CoverageStatus.Conflicting, report.Status);
        Assert.Contains(report.Items, i => i.Evidence.Id == forgedId && i.Status == CoverageStatus.Conflicting);
    }
    [Fact]
    public async Task Worker_read_report_time_feature_and_contract_actions_are_explicit()
    {
        await using var s = await Create(); var claim = await s.Claim(s.Scope(), CoverageStatus.Partial, CoverageBasis.ManualStatement); await s.Approve(claim);
        var o = await s.Db.Observations.SingleAsync(o => o.Id == s.Definition.Targets[0].DateObservationId);
        using var services = new ServiceCollection().AddSingleton<IHistoricalCoverage>(s.Coverage).BuildServiceProvider();
        var query = s.Query(s.Scope(), await s.Now());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Coverage:OperatorId"]="operator:worker-read", ["Coverage:Reason"]="Inspect fictional evidence",
            ["Coverage:EvidenceId"]=claim.Id.ToString(), ["Coverage:IdentityId"]=o.ProviderIdentityId.ToString(), ["Coverage:QueryJson"]=Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(query)),
            ["Coverage:QueriesJson"]=Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(new[] {query})),
            ["Coverage:RequirementJson"]=Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(new FeatureCoverageRequirement("observed",1,[ObservationType.EventDate],null,false,"Completed",1,true,false))) }).Build();
        foreach (var action in new[] { "inspect", "report", "time", "feature-gate", "evaluation-contracts" }) { config["Coverage:Action"]=action; Assert.Equal(0, await CoverageOperatorCommand.RunAsync(services, config, true)); }
        Assert.Equal(1, await s.Db.CoverageReviews.CountAsync(r => r.EvidenceId == claim.Id)); // Read actions never append approvals.
    }
    [Fact]
    public async Task V2_snapshots_freeze_new_reviews_preserve_v1_and_have_deterministic_hashes()
    {
        await using var s = await Create(); var v1 = await s.Build(s.Definition); var oldId = Snapshot(v1); var oldBytes = await s.Db.DatasetArtifacts.Where(a => a.Id == oldId).Select(a => a.Content).SingleAsync();
        var d = await s.CurrentDefinition(); var first = await s.Build(d); var id = Snapshot(first); Assert.Equal(first.ManifestHash, (await s.Build(d)).ManifestHash);
        var snapshot = await s.Datasets.InspectAsync(id); Assert.Equal(2, snapshot.Manifest.ManifestVersion);
        Assert.All(Assert.Single(snapshot.Manifest.Governance!.Rows).Gates, g => Assert.Equal(FeatureCoverageOutcome.EligibleWithPartialCoverage, g.Outcome));
        var row = snapshot.Manifest.Rows[0]; var scope = s.Scope(d.SeasonStart, new(2026, 1, 6), row.Target.HomeId);
        var claim = await s.Claim(scope, CoverageStatus.VerifiedComplete); Assert.Equal(CoverageReviewStatus.Approved, (await s.Approve(claim)).Status);
        Assert.Equal(first.ManifestHash, (await s.Build(d)).ManifestHash);
        var later = await s.Build(await s.CurrentDefinition()); var newer = await s.Datasets.InspectAsync(Snapshot(later)); Assert.NotEqual(first.ManifestHash, later.ManifestHash);
        Assert.Contains(newer.Manifest.Governance!.Rows[0].Coverage.SelectMany(r => r.Items), item => item.Evidence.Id == claim.Id && item.Review is not null);
        Assert.Equal(oldBytes, await s.Db.DatasetArtifacts.Where(a => a.Id == oldId).Select(a => a.Content).SingleAsync());
        foreach (var snapshotId in new[] { oldId, id, newer.Id }) { var verified = await s.Datasets.VerifyAsync(snapshotId); Assert.True(verified.ArtifactIntegrity); Assert.True(verified.EvidenceComplete); Assert.True(verified.FeaturesReproducible); }
    }
    [Fact]
    public async Task V2_freezes_target_and_historical_time_claims_without_inventing_kickoff()
    {
        await using var s = await Create();
        var dates = await s.Db.Observations.Where(o => o.DataSourceId == s.Source && o.Type == ObservationType.EventDate &&
            (o.Id == s.Definition.Targets[0].DateObservationId || o.DateValue == new DateOnly(2026, 1, 2))).ToListAsync(); Assert.Equal(2, dates.Count);
        foreach (var o in dates) await s.Coverage.RecordTimeAsync(new(o.Id, o.RawPayloadId!.Value, new(o.DateValue, null, null, null, null, EventTimePrecision.DateOnly),
            "fictional-source-date", null, null, o.RetrievedAtUtc, "operator:time", "Capture explicit DateOnly precision"));
        var definition = await s.CurrentDefinition(); var id = Snapshot(await s.Build(definition));
        var snapshot = await s.Datasets.InspectAsync(id); var governance = Assert.Single(snapshot.Manifest.Governance!.Rows); Assert.Equal(2, governance.EventTimes.Count);
        Assert.All(governance.EventTimes, t => { Assert.Null(t.Resolution.UtcInstant); Assert.Equal(EventTimePrecision.DateOnly, t.Resolution.Precision); });
        Assert.Null(governance.DateObservationPrecision.UtcInstant); Assert.True((await s.Datasets.VerifyAsync(id)).EvidenceComplete);
        Assert.Equal(snapshot.ManifestHash, (await s.Build(definition)).ManifestHash);
    }
    [Fact]
    public async Task A_season_starting_on_the_prediction_day_remains_missing_and_verifiable()
    {
        await using var s = await Create(); var definition = await s.CurrentDefinition();
        definition = definition with { SeasonStart = DateOnly.FromDateTime(definition.Targets[0].PredictionCutoffUtc) };
        var snapshot = await s.Datasets.InspectAsync(Snapshot(await s.Build(definition)));
        Assert.All(snapshot.Manifest.Rows[0].Features.Values, f => { Assert.Null(f.Value); Assert.NotNull(f.MissingReason); });
        var verified = await s.Datasets.VerifyAsync(snapshot.Id); Assert.True(verified.ArtifactIntegrity); Assert.True(verified.FeaturesReproducible);
    }
    [Fact]
    public async Task Policy_revocation_denies_reports_time_and_v2_inspection()
    {
        await using var s = await Create(); var id = Snapshot(await s.Build(await s.CurrentDefinition())); var claim = await s.Claim(s.Scope(), CoverageStatus.VerifiedComplete); await s.Approve(claim);
        var policy = await s.Db.SourcePolicies.Include(p => p.Audit).SingleAsync(p => p.DataSourceId == s.Source); policy.Revoke(Guid.NewGuid(), "operator:revocation", "Withdraw synthetic permission", await s.Now()); await s.Db.SaveChangesAsync();
        var report = await s.Coverage.ReportAsync(s.Query(s.Scope(), await s.Now())); Assert.False(report.Authorized); Assert.Empty(report.Items);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Coverage.InspectAsync(claim.Id));
        var verified = await s.Datasets.VerifyAsync(id); Assert.False(verified.CurrentlyAuthorized); Assert.True(verified.ArtifactIntegrity);
    }
    [Fact]
    public async Task Worker_review_requires_actor_and_appends_a_durable_decision()
    {
        await using var s = await Create(); var claim = await s.Claim(s.Scope(), CoverageStatus.VerifiedComplete);
        using var services = new ServiceCollection().AddSingleton<IHistoricalCoverage>(s.Coverage).BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Coverage:Action"]="review", ["Coverage:OperatorId"]="operator:worker",
            ["Coverage:Reason"]="Review fictional inventory", ["Coverage:EvidenceId"]=claim.Id.ToString(), ["Coverage:ExpectedSequence"]="0", ["Coverage:Decision"]="Approved", ["Coverage:BasisReference"]=Contract }).Build();
        Assert.Equal(0, await CoverageOperatorCommand.RunAsync(services, config, true)); Assert.Equal("operator:worker", (await s.Db.CoverageReviews.SingleAsync(r => r.EvidenceId == claim.Id)).OperatorId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CoverageOperatorCommand.RunAsync(services, config));
        config["Coverage:OperatorId"]=""; await Assert.ThrowsAsync<ArgumentException>(() => CoverageOperatorCommand.RunAsync(services, config, true));
    }
    [Fact]
    public async Task Upgrade_from_bs007_preserves_v1_artifact_hashes_and_recording()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("bs008_upgrade").WithUsername("bs008_upgrade").WithPassword(Guid.NewGuid().ToString("N")).Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); await container.StartAsync(timeout.Token);
        var db = new BetStatsDbContext(new DbContextOptionsBuilder<BetStatsDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await db.Database.MigrateAsync(timeout.Token);
        await using var s = await Create(db); var first = await s.Build(s.Definition); var id = Snapshot(first);
        var artifact = await db.DatasetArtifacts.AsNoTracking().SingleAsync(a => a.Id == id); var bytes = artifact.Content.ToArray(); var recorded = artifact.RecordedAtUtc;
        var observations = await db.Observations.AsNoTracking().OrderBy(o => o.Id).Select(o => new { o.Id, o.RecordedAtUtc }).ToListAsync();
        // v1 bytes use the original serializer. Remove only later additive schemas to recreate the BS-007 database state.
        await db.GetService<IMigrator>().MigrateAsync("20261008123611_DatasetSnapshotsFeatures", timeout.Token);
        await db.Database.MigrateAsync(timeout.Token);
        Assert.Equal(11, (await db.Database.GetAppliedMigrationsAsync()).Count()); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.CoverageEvidence.ToListAsync()); Assert.Empty(await db.CoverageReviews.ToListAsync()); Assert.Empty(await db.EventTimeEvidence.ToListAsync());
        var after = await db.DatasetArtifacts.AsNoTracking().SingleAsync(a => a.Id == id); Assert.Equal(bytes, after.Content); Assert.Equal(recorded, after.RecordedAtUtc);
        Assert.Equal(observations, await db.Observations.AsNoTracking().OrderBy(o => o.Id).Select(o => new { o.Id, o.RecordedAtUtc }).ToListAsync());
        var verified = await s.Datasets.VerifyAsync(id); Assert.True(verified.ArtifactIntegrity); Assert.True(verified.FeaturesReproducible); Assert.True(verified.EvidenceComplete);
        Assert.Equal(first.ManifestHash, (await s.Build(s.Definition)).ManifestHash);
        var triggers = await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='coverage' AND NOT t.tgisinternal").SingleAsync(); Assert.Equal(10, triggers);
    }
    [Fact]
    public async Task Upgrade_from_bs008_preserves_v1_and_legacy_v2_bytes_hashes_and_recording()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("bs0081_upgrade").WithUsername("bs0081_upgrade").WithPassword(Guid.NewGuid().ToString("N")).Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); await container.StartAsync(timeout.Token);
        var db = new BetStatsDbContext(new DbContextOptionsBuilder<BetStatsDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await db.Database.MigrateAsync(timeout.Token); await using var s = await Create(db);
        var v1 = Snapshot(await s.Build(s.Definition)); var currentV2 = Snapshot(await s.Build(await s.CurrentDefinition()));
        var current = await s.Datasets.InspectAsync(currentV2);
        // Recreate the actual BS-008 governance schema, with absent optional fact positions.
        var legacyGovernance = new DatasetGovernance(1, current.Manifest.Governance!.Rows.Select(r => r with { CoverageSchemaVersion = 1,
            Coverage = r.Coverage.Select(c => c with { Items = c.Items.Select(i => i with { Facts = null }).ToArray() }).ToArray() }).ToArray());
        var legacyRows = current.Manifest.Rows.Select(r => r with { FeatureHash = CanonicalDatasetJson.Fingerprint(new FeatureArtifact(2, r.Target, r.History, r.Features,
            legacyGovernance.Rows.Single(g => g.EventId == r.EventId && g.PredictionCutoffUtc == r.PredictionCutoffUtc))) }).ToArray();
        var legacyManifest = current.Manifest with { Governance = legacyGovernance, Rows = legacyRows }; var bytes = CanonicalDatasetJson.Serialize(legacyManifest);
        var legacy = new DatasetArtifact { Id = Guid.NewGuid(), Content = bytes, ManifestHash = CanonicalDatasetJson.Hash(bytes), DefinitionFingerprint = legacyManifest.DefinitionFingerprint,
            RowCount = legacyRows.Length, FeatureSchemaVersion = 2, BuiltAtUtc = await s.Now() }; db.DatasetArtifacts.Add(legacy);
        foreach (var row in legacyRows) db.DatasetFeatures.Add(new() { Id = Guid.NewGuid(), DatasetId = legacy.Id, EventId = row.EventId, PredictionCutoffUtc = row.PredictionCutoffUtc,
            Fingerprint = row.FeatureHash, Content = CanonicalDatasetJson.Serialize(new FeatureArtifact(2, row.Target, row.History, row.Features,
                legacyGovernance.Rows.Single(g => g.EventId == row.EventId && g.PredictionCutoffUtc == row.PredictionCutoffUtc))) });
        await db.SaveChangesAsync();
        await db.GetService<IMigrator>().MigrateAsync("20261008134149_CoverageEventTimeEvaluation", timeout.Token);
        var before = await db.DatasetArtifacts.AsNoTracking().Where(a => a.Id == v1 || a.Id == legacy.Id).OrderBy(a => a.Id).ToListAsync();
        var observed = await db.Observations.AsNoTracking().OrderBy(o => o.Id).Select(o => new { o.Id, o.RecordedAtUtc }).ToListAsync();
        await db.Database.MigrateAsync(timeout.Token); Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.FootballRawContexts.ToListAsync());
        var after = await db.DatasetArtifacts.AsNoTracking().Where(a => a.Id == v1 || a.Id == legacy.Id).OrderBy(a => a.Id).ToListAsync();
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Content, after[i].Content); Assert.Equal(before[i].ManifestHash, after[i].ManifestHash); Assert.Equal(before[i].RecordedAtUtc, after[i].RecordedAtUtc);
            var verified = await s.Datasets.VerifyDeepAsync(after[i].Id);
            Assert.True(verified.ArtifactIntegrity); Assert.True(verified.FrozenMetadataComplete); Assert.True(verified.FeaturesReproducible); Assert.True(verified.RawHashVerified);
        }
        Assert.Equal(observed, await db.Observations.AsNoTracking().OrderBy(o => o.Id).Select(o => new { o.Id, o.RecordedAtUtc }).ToListAsync());
    }
}
