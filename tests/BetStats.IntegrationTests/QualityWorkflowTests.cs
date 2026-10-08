using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Application.Quality;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Domain.Quality;
using BetStats.Domain.Sports;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BetStats.IntegrationTests;

public sealed class QualityWorkflowTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed class Scenario : IAsyncDisposable
    {
        public BetStatsDbContext Db { get; }
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "betstats-quality-" + Guid.NewGuid().ToString("N"));
        public FileSystemRawPayloadStore Store { get; }
        public Guid Source { get; set; }
        public Scenario(BetStatsDbContext db) { Db = db; Store = new(Root); }
        public SourcePolicyEvaluator Policies => new(new SourcePolicyHistory(Db));
        public FootballIngestionPersistence Publication => new(Db, Policies, new SourceOperationalStatusReader(Db));
        public IdentityReview Reviews => new(Db);
        public AnalyticalQualityGate Gate => new(Db, Policies, new SourceOperationalStatusReader(Db));
        public DataReconciliation Reconcile(IRawPayloadStore? store = null) => new(Db, store ?? Store, new FootballDataCsvParser(), Publication, Policies, new SourceOperationalStatusReader(Db));
        public Task<ImportReport> Import(string csv = SyntheticFootballDemo.Csv) => new FootballIngestion(new(Policies, TimeProvider.System, new SourceOperationalStatusReader(Db)), Publication, Store, new FootballDataCsvParser())
            .RunAsync(new FootballDataFixtureAdapter(Source, SyntheticFootballDemo.Bytes(csv), TimeProvider.System), SyntheticFootballDemo.Scope,
                new(new(100, 1000, 1, TimeSpan.FromSeconds(30)), TimeProvider.System));
        public Task<DateTime> Now() => Db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        public async Task<Guid> Raw(Guid? run) => (await Db.RawPayloads.AsNoTracking().SingleAsync(r => r.IngestionRunId == run)).Id;
        public async Task<ProviderIdentity> Unknown() => await Db.ProviderIdentities.AsNoTracking().SingleAsync(i => i.DataSourceId == Source && i.ExternalId == FootballDataCsvParser.TeamReference("FICT", "Unmapped Ravens"));
        public async Task<Guid> Team(string name) => (await Db.Participants.FirstAsync(p => p.Name == name)).Id;
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private async Task<Scenario> Create()
    {
        var s = new Scenario(fixture.CreateContext()); s.Source = await SyntheticFootballDemo.PrepareAsync(s.Db, true, "quality-fiction-" + Guid.NewGuid().ToString("N")); return s;
    }
    private static RawReconciliationRequest Request(Guid raw) => new(raw, SyntheticFootballDemo.Scope);
    private static async Task<ReviewResult> Resolve(Scenario s, string team = "Cobalt Owls")
    {
        var identity = await s.Unknown(); var previous = await s.Db.IdentityResolutions.Where(d => d.ProviderIdentityId == identity.Id).OrderByDescending(d => d.Version).FirstAsync();
        return await s.Reviews.DecideAsync(new(identity.Id, s.Source, ReviewAction.Approve, new(CanonicalEntityKind.Participant, await s.Team(team)), previous.Version, "operator:fictional", "Reviewed fictional participant"));
    }
    [Fact]
    public async Task Unknown_identity_review_reconciliation_is_append_only_and_has_no_historical_leakage()
    {
        await using var s = await Create(); var imported = await s.Import(); var raw = await s.Raw(imported.RunId); var cutoff = await s.Now();
        var identity = await s.Unknown(); var list = await s.Reviews.ListUnresolvedAsync(s.Source); Assert.Contains(list, i => i.Identity.Id == identity.Id);
        Assert.NotEmpty((await s.Reviews.InspectAsync(identity.Id)).RawPayloadIds); Assert.NotEmpty(await s.Reviews.CandidatesAsync(identity.Id));
        Assert.Equal("accepted", (await Resolve(s)).Result);
        var result = await s.Reconcile().RunAsync([Request(raw)], "operator:fictional", "Reprocess reviewed identity");
        Assert.Equal("Completed", result.Result); Assert.Contains(result.Items, i => i.Outcome == ReconciliationOutcome.NewlyResolved);
        var eventIdentity = await s.Db.ProviderIdentities.SingleAsync(i => i.DataSourceId == s.Source && i.ExternalId == "provider:fictional-unresolved");
        var dates = await s.Db.Observations.Where(o => o.ProviderIdentityId == eventIdentity.Id && o.Type == ObservationType.EventDate).OrderBy(o => o.Version).ToListAsync();
        Assert.Equal(2, dates.Count); Assert.Null(dates[0].CanonicalId); Assert.NotNull(dates[1].CanonicalId); Assert.True(dates[1].RecordedAtUtc > cutoff);
        var count = await s.Db.Observations.CountAsync(o => o.DataSourceId == s.Source);
        var repeat = await s.Reconcile().RunAsync([Request(raw)], "operator:fictional", "Repeat safely");
        Assert.DoesNotContain(repeat.Items, i => i.Outcome == ReconciliationOutcome.NewlyResolved);
        Assert.Equal(count, await s.Db.Observations.CountAsync(o => o.DataSourceId == s.Source));
        var past = await s.Gate.EvaluateAsync(new(dates[1].Id, cutoff, DataPurpose.InternalAnalytics, new()));
        Assert.False(past.Eligible); Assert.Contains("observation_not_known_at_cutoff", past.Reasons);
        var current = await s.Gate.EvaluateAsync(new(dates[1].Id, await s.Now(), DataPurpose.InternalAnalytics, new()));
        Assert.True(current.Eligible, string.Join(",", current.Reasons));
        Assert.Equal(2, await s.Db.MaintenanceEvents.CountAsync(e => e.ExecutionId == result.ExecutionId));
        Assert.All(await s.Db.QualityAssessments.Where(a => a.ExecutionId == result.ExecutionId).ToListAsync(), a => { Assert.NotNull(a.ContextKey); Assert.True(a.RecordedAtUtc >= a.AssessedAtUtc); });
    }
    [Fact]
    public async Task Contradictory_duplicate_reference_is_classified_without_publication()
    {
        await using var s = await Create(); var csv = SyntheticFootballDemo.CorrectionCsv + "FICT,04/01/2026,Amber Comets,Cobalt Owls,H,fictional-1\n";
        var imported = await s.Import(csv); Assert.Equal(0, imported.AcceptedRecords);
        Assert.Equal(0, await s.Db.Observations.CountAsync(o => o.DataSourceId == s.Source));
        var assessments = await s.Db.QualityAssessments.Where(a => a.ExecutionId == imported.AttemptId).ToListAsync();
        Assert.Equal(2, assessments.Count); Assert.All(assessments, a => Assert.Equal(QualityClassification.DuplicateContradictory, a.Classification));
        var result = await s.Reconcile().RunAsync([Request(await s.Raw(imported.RunId))], "operator:a", "Review collision");
        Assert.All(result.Items, i => Assert.Equal(ReconciliationOutcome.Conflict, i.Outcome));
    }
    [Fact]
    public async Task Later_correction_and_old_raw_replay_preserve_new_fact()
    {
        await using var s = await Create(); var original = await s.Import(); var oldRaw = await s.Raw(original.RunId); var before = await s.Now();
        var correction = await s.Import(SyntheticFootballDemo.CorrectionCsv);
        Assert.Contains(await s.Db.QualityAssessments.Where(a => a.ExecutionId == correction.AttemptId).ToListAsync(), a => a.Classification == QualityClassification.HistoricalCorrection && a.Passed);
        var count = await s.Db.Observations.CountAsync(o => o.DataSourceId == s.Source);
        var replay = await s.Reconcile().RunAsync([Request(oldRaw)], "operator:a", "Replay historical evidence");
        Assert.Contains(replay.Items, i => i.Outcome == ReconciliationOutcome.AlreadyProcessed);
        Assert.Equal(count, await s.Db.Observations.CountAsync(o => o.DataSourceId == s.Source));
        var identity = await s.Db.ProviderIdentities.SingleAsync(i => i.DataSourceId == s.Source && i.ExternalId == "provider:fictional-1");
        var dates = await s.Db.Observations.Where(o => o.ProviderIdentityId == identity.Id && o.Type == ObservationType.EventDate).OrderBy(o => o.Version).ToListAsync();
        Assert.Equal(new DateOnly(2026, 1, 3), dates[^1].DateValue);
        Assert.True((await s.Gate.EvaluateAsync(new(dates[0].Id, before, DataPurpose.InternalAnalytics, new()))).Eligible);
        Assert.Contains("superseded_observation", (await s.Gate.EvaluateAsync(new(dates[0].Id, await s.Now(), DataPurpose.InternalAnalytics, new()))).Reasons);
    }
    [Fact]
    public async Task Later_review_changes_only_explicit_reconstruction_interpretation()
    {
        await using var s = await Create(); await s.Import();
        var identity = await s.Db.ProviderIdentities.SingleAsync(i => i.DataSourceId == s.Source && i.ExternalId == "provider:fictional-1");
        var observation = await s.Db.Observations.SingleAsync(o => o.ProviderIdentityId == identity.Id && o.Type == ObservationType.EventDate);
        var cutoff = await s.Now(); var old = observation.CanonicalId;
        var originalEvent = await s.Db.SportingEvents.Include(e => e.Participants).SingleAsync(e => e.Id == old);
        var competition = await s.Db.Competitions.SingleAsync(c => c.Id == originalEvent.CompetitionId);
        var season = await s.Db.Seasons.SingleAsync(c => c.Id == originalEvent.SeasonId);
        var correctedTarget = new SportingEvent(Guid.NewGuid(), competition, season, null, SportingEventStatus.Completed, await s.Now());
        foreach (var participant in originalEvent.Participants) correctedTarget.AddParticipant(await s.Db.Participants.SingleAsync(p => p.Id == participant.ParticipantId), participant.Role, participant.Position);
        s.Db.Add(correctedTarget); await s.Db.SaveChangesAsync();
        var expectedVersion = await s.Db.IdentityResolutions.Where(d => d.ProviderIdentityId == identity.Id).MaxAsync(d => d.Version);
        var reviewed = await s.Reviews.DecideAsync(new(identity.Id, s.Source, ReviewAction.Approve, new(CanonicalEntityKind.SportingEvent, correctedTarget.Id), expectedVersion, "operator:correction", "Correct fictional mistaken mapping"));
        Assert.Equal("accepted", reviewed.Result);
        var historic = await s.Gate.EvaluateAsync(new(observation.Id, cutoff, DataPurpose.InternalAnalytics, new()));
        Assert.Equal(old, historic.InterpretedTargetId); Assert.Equal(old, historic.FrozenTargetId); Assert.True(historic.Eligible, string.Join(",", historic.Reasons));
        var reconstructed = await s.Gate.EvaluateAsync(new(observation.Id, cutoff, DataPurpose.InternalAnalytics, new(), DatasetMode.RetrospectiveReconstruction, await s.Now()));
        Assert.Equal(old, reconstructed.FrozenTargetId); Assert.Equal(correctedTarget.Id, reconstructed.InterpretedTargetId); Assert.Equal(reviewed.DecisionId, reconstructed.DecisionId);
        Assert.True(reconstructed.Eligible, string.Join(",", reconstructed.Reasons));
        await Assert.ThrowsAsync<ArgumentException>(() => s.Gate.EvaluateAsync(new(observation.Id, cutoff, DataPurpose.InternalAnalytics, new(), DatasetMode.RetrospectiveReconstruction)));
    }
    [Theory]
    [InlineData("revoked")][InlineData("disabled")]
    public async Task Current_authorization_denies_reconciliation_without_new_observations(string mode)
    {
        await using var s = await Create(); var imported = await s.Import(); var raw = await s.Raw(imported.RunId); var count = await s.Db.Observations.CountAsync(o => o.DataSourceId == s.Source);
        if (mode == "disabled") await s.Db.DataSources.Where(d => d.Id == s.Source).ExecuteUpdateAsync(b => b.SetProperty(d => d.IsEnabled, false));
        else { var policy = await s.Db.SourcePolicies.Include(p => p.Permissions).Include(p => p.Audit).SingleAsync(p => p.DataSourceId == s.Source); policy.Revoke(Guid.NewGuid(), "operator:a", "Fixture revoked", await s.Now()); await s.Db.SaveChangesAsync(); }
        var result = await s.Reconcile().RunAsync([Request(raw)], "operator:a", "Verify denied replay");
        Assert.Equal("Denied", result.Result); Assert.Equal(count, await s.Db.Observations.CountAsync(o => o.DataSourceId == s.Source));
        Assert.Equal("Denied", (await s.Db.MaintenanceEvents.SingleAsync(e => e.ExecutionId == result.ExecutionId && e.Sequence == 2)).Result);
    }
    [Fact]
    public async Task Concurrent_reviews_have_one_winner_and_controlled_conflict()
    {
        await using var s = await Create(); await s.Import(); var identity = await s.Unknown();
        var first = new IdentityReviewCommand(identity.Id, s.Source, ReviewAction.Approve, new(CanonicalEntityKind.Participant, await s.Team("Cobalt Owls")), 1, "operator:a", "First proposal");
        var second = first with { OperatorId = "operator:b", Target = new(CanonicalEntityKind.Participant, await s.Team("Silver Foxes")) };
        await using var other = fixture.CreateContext();
        var results = await Task.WhenAll(s.Reviews.DecideAsync(first), new IdentityReview(other).DecideAsync(second));
        Assert.Single(results, r => r.Result == "accepted"); Assert.Single(results, r => r.Result == "concurrent_review_conflict");
        Assert.Equal(2, await s.Db.IdentityResolutions.CountAsync(d => d.ProviderIdentityId == identity.Id));
    }
    [Theory]
    [InlineData("kind")][InlineData("sport")][InlineData("missing")][InlineData("source")]
    public async Task Invalid_identity_reviews_are_audited_and_do_not_append_resolution(string mode)
    {
        await using var s = await Create(); await s.Import(); var identity = await s.Unknown();
        var target = new CanonicalReference(CanonicalEntityKind.Participant, await s.Team("Cobalt Owls"));
        if (mode == "kind") target = new(CanonicalEntityKind.Sport, FootballQualityRules.Football);
        if (mode == "missing") target = new(CanonicalEntityKind.Participant, Guid.NewGuid());
        if (mode == "sport") { var tennis = ReferenceSports.All.Single(p => p.Code == "tennis").Id; var p = new Participant(Guid.NewGuid(), tennis, "Fictional tennis person", ParticipantType.Individual); s.Db.Add(p); await s.Db.SaveChangesAsync(); target = new(CanonicalEntityKind.Participant, p.Id); }
        var result = await s.Reviews.DecideAsync(new(identity.Id, mode == "source" ? Guid.NewGuid() : s.Source, ReviewAction.Approve, target, 1, "operator:a", "Validate proposed target"));
        Assert.NotEqual("accepted", result.Result); Assert.Null(result.DecisionId);
        Assert.Single(await s.Db.IdentityResolutions.Where(d => d.ProviderIdentityId == identity.Id).ToListAsync());
        Assert.NotNull(await s.Db.MaintenanceEvents.FindAsync(result.AuditId));
    }
    [Fact]
    public async Task Reject_and_ambiguous_reviews_preserve_previous_history_and_require_actor()
    {
        await using var s = await Create(); await s.Import(); var identity = await s.Unknown();
        await Assert.ThrowsAsync<ArgumentException>(() => s.Reviews.DecideAsync(new(identity.Id, s.Source, ReviewAction.Reject, null, 1, "", "Missing operator")));
        var rejected = await s.Reviews.DecideAsync(new(identity.Id, s.Source, ReviewAction.Reject, null, 1, "operator:a", "Reject proposal"));
        var ambiguous = await s.Reviews.DecideAsync(new(identity.Id, s.Source, ReviewAction.Ambiguous, null, 2, "operator:a", "Multiple plausible targets"));
        Assert.Equal(3, ambiguous.Version); var history = await s.Db.IdentityResolutions.Where(d => d.ProviderIdentityId == identity.Id).OrderBy(d => d.Version).ToListAsync();
        Assert.Equal(ResolutionStatus.Unresolved, history[1].Status); Assert.Equal(ResolutionStatus.Ambiguous, history[2].Status); Assert.Equal(rejected.DecisionId, history[2].PreviousDecisionId);
    }
    [Theory]
    [InlineData("update")][InlineData("delete")][InlineData("truncate")][InlineData("bulk")]
    public async Task Quality_and_maintenance_are_database_immutable(string operation)
    {
        await using var s = await Create(); await s.Import(); await Resolve(s);
        foreach (var table in new[] { "QualityAssessments", "MaintenanceEvents" })
        {
            await using var db = fixture.CreateContext();
            if (operation == "bulk")
            {
                if (table == "QualityAssessments") await Assert.ThrowsAsync<PostgresException>(() => db.QualityAssessments.ExecuteDeleteAsync());
                else await Assert.ThrowsAsync<PostgresException>(() => db.MaintenanceEvents.ExecuteDeleteAsync());
            }
            else
            {
                var sql = operation == "update" ? $"UPDATE quality.\"{table}\" SET \"Id\" = \"Id\"" : operation == "delete" ? $"DELETE FROM quality.\"{table}\"" : $"TRUNCATE quality.\"{table}\"";
                Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);
            }
        }
    }
    [Fact]
    public async Task Trusted_quality_clock_and_ef_guard_prevent_backdated_or_mutated_assessment()
    {
        await using var s = await Create(); var imported = await s.Import(); var assessment = await s.Db.QualityAssessments.FirstAsync(a => a.ExecutionId == imported.AttemptId);
        s.Db.Entry(assessment).Property(a => a.RecordedAtUtc).CurrentValue = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Db.SaveChangesAsync()); s.Db.ChangeTracker.Clear();
        var time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc); var id = Guid.NewGuid(); var execution = Guid.NewGuid(); var raw = await s.Raw(imported.RunId); var sport = FootballQualityRules.Football;
        await s.Db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO quality.\"QualityAssessments\" (\"Id\",\"ExecutionId\",\"DataSourceId\",\"RawPayloadId\",\"Row\",\"RecordReference\",\"SportId\",\"RuleId\",\"RuleVersion\",\"Passed\",\"Severity\",\"BlocksEligibility\",\"ReasonCode\",\"Classification\",\"AssessedAtUtc\",\"RecordedAtUtc\") VALUES ({id},{execution},{s.Source},{raw},1,'synthetic',{sport},'football.sport',1,true,'Info',false,'passed','Accepted',{time},{time})");
        Assert.True((await s.Db.QualityAssessments.SingleAsync(a => a.Id == id)).RecordedAtUtc > time);
    }
    [Theory]
    [InlineData("identity")][InlineData("policy")][InlineData("observation")][InlineData("sport")]
    public async Task Quality_provenance_source_mismatch_is_rejected_in_postgresql(string mismatch)
    {
        await using var s = await Create(); var imported = await s.Import();
        var otherSource = await SyntheticFootballDemo.PrepareAsync(s.Db, true, "quality-other-" + Guid.NewGuid().ToString("N"));
        var raw = await s.Raw(imported.RunId);
        var otherIdentity = await s.Db.ProviderIdentities.FirstAsync(i => i.DataSourceId == otherSource);
        var otherPolicy = await s.Db.SourcePolicies.SingleAsync(p => p.DataSourceId == otherSource);
        var observation = await s.Db.Observations.FirstAsync(o => o.DataSourceId == s.Source);
        var unrelatedRaw = await s.Db.RawPayloads.SingleAsync(r => r.Id == raw);
        // A real second capture from the same source cannot be substituted for an observation's RAW.
        if (mismatch == "observation") { var second = await s.Import(); unrelatedRaw = await s.Db.RawPayloads.SingleAsync(r => r.IngestionRunId == second.RunId); }
        s.Db.QualityAssessments.Add(new() { Id = Guid.NewGuid(), ExecutionId = Guid.NewGuid(), DataSourceId = s.Source, RawPayloadId = unrelatedRaw.Id,
            ProviderIdentityId = mismatch == "identity" ? otherIdentity.Id : null, PolicyId = mismatch == "policy" ? otherPolicy.Id : null,
            ObservationId = mismatch == "observation" ? observation.Id : null, Row = 2, RecordReference = "synthetic-check",
            SportId = mismatch == "sport" ? Guid.NewGuid() : FootballQualityRules.Football, RuleId = "football.provenance", RuleVersion = 1,
            Passed = true, Severity = QualitySeverity.Info, ReasonCode = "passed", Classification = QualityClassification.Accepted, AssessedAtUtc = await s.Now() });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }
    [Fact]
    public async Task Reports_have_consistent_exclusive_counts_safe_empty_rates_and_bounds()
    {
        await using var s = await Create(); var imported = await s.Import(); var reports = new QualityReports(s.Db, s.Gate);
        var report = await reports.ReadAsync(imported.AttemptId);
        Assert.Equal(6, report.Total); Assert.Equal(1, report.Invalid); Assert.Equal(1, report.Duplicates); Assert.Equal(1, report.Unresolved); Assert.Equal(3, report.Accepted);
        Assert.Equal(report.Total, report.Accepted + report.Unresolved + report.Rejected); Assert.Equal(3, report.Eligible);
        Assert.Equal(0.5, report.AnalyticalEligibilityRate); Assert.NotEmpty(report.RuleVersions); Assert.Single(report.PolicyIds);
        var empty = await reports.ReadAsync(Guid.NewGuid()); Assert.Equal(0, empty.Total); Assert.Equal(0, empty.ValidationPassRate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reports.ReadAsync(imported.AttemptId, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => s.Reviews.ListUnresolvedAsync(s.Source, 201));
    }
    [Fact]
    public async Task Tampered_raw_fails_without_publication_and_retains_audit()
    {
        await using var s = await Create(); var imported = await s.Import(); var raw = await s.Db.RawPayloads.SingleAsync(r => r.IngestionRunId == imported.RunId);
        await File.WriteAllTextAsync(Path.Combine(s.Root, raw.StorageKey + ".raw"), "synthetic tamper");
        var count = await s.Db.Observations.CountAsync(o => o.DataSourceId == s.Source);
        var result = await s.Reconcile().RunAsync([Request(raw.Id)], "operator:a", "Verify tamper");
        Assert.Contains(result.Items, i => i.Outcome == ReconciliationOutcome.Failed && i.ReasonCode == "raw_integrity_or_storage");
        Assert.Equal(count, await s.Db.Observations.CountAsync(o => o.DataSourceId == s.Source));
    }
    [Fact]
    public async Task Header_failures_count_rejected_rows_and_undecodable_payloads_are_separate()
    {
        await using var s = await Create();
        var headers = await s.Import("Wrong,Date,HomeTeam,AwayTeam\nFICT,02/01/2026,Amber Comets,Cobalt Owls\nFICT,03/01/2026,Amber Comets,Cobalt Owls\n");
        var report = await new QualityReports(s.Db, s.Gate).ReadAsync(headers.AttemptId);
        Assert.Equal(2, report.Total); Assert.Equal(2, report.Invalid); Assert.Equal(2, report.Rejected);
        var malformed = await s.Import("\"unterminated");
        var payload = await new QualityReports(s.Db, s.Gate).ReadAsync(malformed.AttemptId);
        Assert.Equal(0, payload.Total); Assert.Equal(1, payload.PayloadFailures); Assert.Equal(0, payload.AnalyticalEligibilityRate);
    }
    private sealed class CancellingStore(IRawPayloadStore inner, CancellationTokenSource cancellation) : IRawPayloadStore
    {
        public Task<ReadOnlyMemory<byte>> ReadAsync(StoredPayload payload, CancellationToken token = default) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return inner.ReadAsync(payload, token); }
        public Task<StoredPayload> StageAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default) => inner.StageAsync(bytes, token);
        public Task FinalizeAsync(StoredPayload payload, CancellationToken token = default) => inner.FinalizeAsync(payload, token);
        public IReadOnlyList<string> InventoryStaged() => inner.InventoryStaged();
    }
    [Fact]
    public async Task Repeated_capture_uses_existing_row_receipt_and_is_not_falsely_newly_resolved()
    {
        await using var s = await Create(); await s.Import(); var repeated = await s.Import();
        var count = await s.Db.Observations.CountAsync(o => o.DataSourceId == s.Source);
        var result = await s.Reconcile().RunAsync([Request(await s.Raw(repeated.RunId))], "operator:a", "Check receipt across captures");
        Assert.Equal(3, result.Items.Count(i => i.Outcome == ReconciliationOutcome.AlreadyProcessed));
        Assert.DoesNotContain(result.Items, i => i.Outcome == ReconciliationOutcome.NewlyResolved);
        Assert.Equal(count, await s.Db.Observations.CountAsync(o => o.DataSourceId == s.Source));
    }
    [Fact]
    public async Task Old_unresolved_raw_cannot_reverse_newer_unresolved_correction_after_mapping()
    {
        await using var s = await Create(); var original = await s.Import(); var old = await s.Raw(original.RunId);
        const string correction = "Div,Date,HomeTeam,AwayTeam,FTR,MatchId\nFICT,07/01/2026,Unmapped Ravens,Violet Herons,H,fictional-unresolved\n";
        var updated = await s.Import(correction); Assert.Equal(ImportOutcome.Partial, updated.Outcome); await Resolve(s);
        var replay = await s.Reconcile().RunAsync([Request(old)], "operator:a", "Replay before reconciling newer evidence");
        Assert.DoesNotContain(replay.Items, i => i.Outcome == ReconciliationOutcome.NewlyResolved);
        var result = await s.Reconcile().RunAsync([Request(await s.Raw(updated.RunId))], "operator:a", "Reconcile latest unresolved fact");
        Assert.Contains(result.Items, i => i.Outcome == ReconciliationOutcome.NewlyResolved);
        var identity = await s.Db.ProviderIdentities.SingleAsync(i => i.DataSourceId == s.Source && i.ExternalId == "provider:fictional-unresolved");
        var date = await s.Db.Observations.Where(o => o.ProviderIdentityId == identity.Id && o.Type == ObservationType.EventDate).OrderByDescending(o => o.Version).FirstAsync();
        Assert.Equal(new DateOnly(2026, 1, 7), date.DateValue); Assert.NotNull(date.CanonicalId);
    }
    [Fact]
    public async Task Independent_source_disagreement_is_preserved_with_conflict_evidence_not_a_winner()
    {
        await using var s = await Create(); var imported = await s.Import();
        var identity = await s.Db.ProviderIdentities.SingleAsync(i => i.DataSourceId == s.Source && i.ExternalId == "provider:fictional-1");
        var observation = await s.Db.Observations.SingleAsync(o => o.ProviderIdentityId == identity.Id && o.Type == ObservationType.EventDate);
        var cutoff = await s.Now(); var source2 = await SyntheticFootballDemo.PrepareAsync(s.Db, true, "independent-fiction-" + Guid.NewGuid().ToString("N"));
        var now = await s.Now();
        var otherIdentity = new ProviderIdentity(Guid.NewGuid(), source2, CanonicalEntityKind.SportingEvent, "independent:fictional-1", now);
        var raw = new BetStats.Infrastructure.Persistence.Entities.RawPayload { Id = Guid.NewGuid(), DataSourceId = source2, CreatedAtUtc = now, RetrievedAtUtc = now, ContentType = "text/csv", ContentHashSha256 = new string('a', 64), StorageKey = "synthetic-independent" };
        s.Db.AddRange(otherIdentity, raw); await s.Db.SaveChangesAsync(); now = await s.Now();
        var other = new Observation(Guid.NewGuid(), otherIdentity, new(CanonicalEntityKind.SportingEvent, observation.CanonicalId!.Value), ObservationType.EventDate, raw.RetrievedAtUtc, now, now, rawPayloadId: raw.Id, dateValue: new(2026, 1, 9));
        s.Db.Add(other); await s.Db.SaveChangesAsync();
        Assert.True((await s.Gate.EvaluateAsync(new(observation.Id, cutoff, DataPurpose.InternalAnalytics, new()))).Eligible);
        var conflict = await s.Gate.EvaluateAsync(new(observation.Id, await s.Now(), DataPurpose.InternalAnalytics, new()));
        Assert.False(conflict.Eligible); Assert.Contains("cross_source_observation_conflict", conflict.Reasons); Assert.Contains(other.Id, conflict.ConflictObservationIds!);
        var result = await s.Reconcile().RunAsync([Request(await s.Raw(imported.RunId))], "operator:a", "Assess independent conflict");
        Assert.Contains(result.Items, i => i.Outcome == ReconciliationOutcome.Conflict && i.ReasonCode == "cross_source_observation_conflict");
        Assert.Contains(await s.Db.QualityAssessments.Where(a => a.ExecutionId == result.ExecutionId).ToListAsync(), a => a.Classification == QualityClassification.ObservationConflict && a.ReasonCode == "cross_source_observation_conflict");
        Assert.Equal(new DateOnly(2026, 1, 2), (await s.Db.Observations.SingleAsync(o => o.Id == observation.Id)).DateValue);
    }
    [Fact]
    public async Task Rule_versions_and_requested_usage_rights_fail_closed_without_historical_leakage()
    {
        await using var s = await Create(); await s.Import();
        var identity = await s.Db.ProviderIdentities.SingleAsync(i => i.DataSourceId == s.Source && i.ExternalId == "provider:fictional-1");
        var observation = await s.Db.Observations.SingleAsync(o => o.ProviderIdentityId == identity.Id && o.Type == ObservationType.EventDate);
        var cutoff = await s.Now();
        Assert.Contains("historical_policy_UnknownPermission", (await s.Gate.EvaluateAsync(new(observation.Id, cutoff, DataPurpose.ModelTraining, new()))).Reasons);
        await Assert.ThrowsAsync<ArgumentException>(() => s.Gate.EvaluateAsync(new(observation.Id, cutoff, DataPurpose.DataRetrieval, new())));
        s.Db.QualityAssessments.Add(new() { Id = Guid.NewGuid(), ExecutionId = Guid.NewGuid(), DataSourceId = s.Source, RawPayloadId = observation.RawPayloadId!.Value,
            ProviderIdentityId = identity.Id, Row = 2, RecordReference = identity.ExternalId, SportId = FootballQualityRules.Football, RuleId = "football.sport", RuleVersion = 2,
            Passed = true, Severity = QualitySeverity.Info, ReasonCode = "passed", Classification = QualityClassification.Accepted, AssessedAtUtc = await s.Now() });
        await s.Db.SaveChangesAsync();
        Assert.True((await s.Gate.EvaluateAsync(new(observation.Id, cutoff, DataPurpose.InternalAnalytics, new()))).Eligible);
        Assert.Contains("unsupported_quality_version", (await s.Gate.EvaluateAsync(new(observation.Id, await s.Now(), DataPurpose.InternalAnalytics, new()))).Reasons);
    }
    [Fact]
    public async Task Cancellation_is_audited_and_operator_can_close_confirmed_interruption()
    {
        await using var s = await Create(); var imported = await s.Import(); var raw = await s.Raw(imported.RunId);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Reconcile(new CancellingStore(s.Store, cancellation)).RunAsync([Request(raw)], "operator:a", "Cancel reconciliation", cancellation.Token));
        var interrupted = await s.Db.MaintenanceEvents.SingleAsync(e => e.TargetId == raw && e.Sequence == 2); Assert.Equal("Interrupted", interrupted.Result);
        var execution = Guid.NewGuid(); var now = await s.Now();
        s.Db.MaintenanceEvents.Add(new() { Id = Guid.NewGuid(), ExecutionId = execution, Sequence = 1, Action = "Reconciliation", OperatorId = "operator:a", Reason = "Synthetic stopped owner", TargetId = raw, Result = "Started", ExecutedAtUtc = now });
        await s.Db.SaveChangesAsync(); await s.Reconcile().MarkInterruptedAsync(execution, "operator:b", "Owner confirmed stopped");
        await s.Reconcile().MarkInterruptedAsync(execution, "operator:b", "Repeat safe closure");
        Assert.Equal(2, await s.Db.MaintenanceEvents.CountAsync(e => e.ExecutionId == execution));
    }
}
