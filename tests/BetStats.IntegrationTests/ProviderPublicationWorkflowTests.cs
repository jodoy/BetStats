using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BetStats.Application.Datasets;
using BetStats.Application.Football;
using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Pipeline;
using BetStats.Application.Providers;
using BetStats.Application.Quality;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Domain.Sports;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using BetStats.Infrastructure.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using BetStats.Web.Components;
using BetStats.Application.Evaluation;
using BetStats.Application.Coverage;
using BetStats.Domain.Quality;

namespace BetStats.IntegrationTests;

public sealed class ProviderPublicationWorkflowTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly PipelineApproval Approval = new("test:publication", "Controlled fictional original-RAW publication", true);
    private static readonly ApiFootballCanary Canary = new(1, 999999, 2026, new(2026, 10, 13), new(2026, 10, 19));
    private static readonly FootballImportScope Scope = new("999999", "2026");
    internal sealed record Setup(ResultOperationsWorkflowTests.Scenario Scenario, Guid EventId, Guid HomeId, Guid AwayId, Guid PolicyId, Guid OriginalSourceId);
    internal async Task<Setup> Create(string? restriction = null, bool reviewed = true, Action<IServiceCollection>? configure = null)
    {
        var s = await new ResultOperationsWorkflowTests(fixture).Create(configure: configure); var originalSource = s.Source; var db = s.Db; var now = await s.Now();
        var source = new DataSource { Id = Guid.NewGuid(), Code = "api-football-fiction-" + Guid.NewGuid().ToString("N"), DisplayName = "Controlled fictional API JSON", IsEnabled = true, CreatedAtUtc = now };
        var football = ReferenceSports.All.Single(s => s.Code == "football").Id;
        var competition = new Competition(Guid.NewGuid(), football, "Fictional JSON league", null, CompetitionType.League);
        var season = new Season(Guid.NewGuid(), competition.Id, "Fictional JSON 2026", new(2026, 1, 1), new(2026, 12, 31));
        var home = new Participant(Guid.NewGuid(), football, "Fictional home", ParticipantType.Team);
        var away = new Participant(Guid.NewGuid(), football, "Fictional away", ParticipantType.Team);
        var e = new SportingEvent(Guid.NewGuid(), competition, season, null, SportingEventStatus.Scheduled, now);
        e.AddParticipant(home, ParticipantRole.Home, 1); e.AddParticipant(away, ParticipantRole.Away, 2);
        var permissions = FootballProviderContract.ImportPurposes.Concat([DataPurpose.PublicDisplay]).Distinct().Select(p =>
            new PurposePermission(p, PermissionDecision.Allowed, restriction == "attribution" && p == DataPurpose.InternalAnalytics ? "fictional attribution required" : null,
                restriction is "retention" or "retention-current" && p == DataPurpose.HistoricalRetention ? 1 : null));
        var policy = new SourcePolicy(Guid.NewGuid(), source.Id, 1, now.AddDays(-3), null, "synthetic:owned-json", "synthetic:explicit-test-grant", now, permissions);
        db.AddRange(source, competition, season, home, away, e, policy); await db.SaveChangesAsync();
        policy.Approve(Guid.NewGuid(), Approval.Actor, Approval.Reason, now, now); await db.SaveChangesAsync(); s.Source = source.Id;
        if (reviewed)
        {
            Bind(CanonicalEntityKind.Competition, "provider:competition:999999", competition.Id);
            Bind(CanonicalEntityKind.Season, FootballDataCsvParser.SeasonReference("999999", "2026"), season.Id);
            Bind(CanonicalEntityKind.Participant, ApiFootballPublicationMapping.Reference("team", 99901), home.Id);
            Bind(CanonicalEntityKind.Participant, ApiFootballPublicationMapping.Reference("team", 99902), away.Id);
            Bind(CanonicalEntityKind.SportingEvent, ApiFootballPublicationMapping.Reference("fixture", 999001), e.Id);
            await db.SaveChangesAsync();
        }
        return new(s, e.Id, home.Id, away.Id, policy.Id, originalSource);
        void Bind(CanonicalEntityKind kind, string reference, Guid target)
        {
            var identity = new ProviderIdentity(Guid.NewGuid(), source.Id, kind, reference, now); db.Add(identity);
            db.Add(new IdentityResolution(Guid.NewGuid(), identity, ResolutionStatus.Resolved, new(kind, target), Approval.Actor, Approval.Reason, now));
        }
    }
    internal static JsonObject Payload(string status = "NS", int goals = 2, int hour = 18) => JsonSerializer.SerializeToNode(new {
        get = "fixtures", errors = Array.Empty<string>(), results = 1, paging = new { current = 1, total = 1 },
        response = new[] { new { fixture = new { id = 999001, timezone = "UTC", date = $"2026-10-15T{hour:00}:00:00Z",
            timestamp = new DateTimeOffset(2026, 10, 15, hour, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), status = new { @short = status } },
            league = new { id = 999999, season = 2026 }, teams = new { home = new { id = 99901 }, away = new { id = 99902 } },
            goals = new { home = status == "FT" ? (int?)goals : null, away = status == "FT" ? (int?)1 : null },
            score = new { fulltime = new { home = status == "FT" ? (int?)goals : null, away = status == "FT" ? (int?)1 : null },
                halftime = new { home = status == "FT" ? (int?)1 : null, away = status == "FT" ? (int?)0 : null } } } }
    })!.AsObject();
    internal static async Task<Guid> Capture(Setup setup, JsonObject? json = null, DateTime? retrieved = null, bool complete = true)
    {
        var s = setup.Scenario; var persistence = s.Get<IFootballIngestionPersistence>(); var store = s.Get<IRawPayloadStore>();
        var bytes = Encoding.UTF8.GetBytes((json ?? Payload()).ToJsonString());
        var attempt = await persistence.BeginAsync(Guid.NewGuid(), s.Source, default); var staged = await store.StageAsync(bytes);
        var raw = await persistence.CaptureAsync(attempt, new(bytes, "application/json", retrieved ?? await s.Now(), ApiFootballFixtureAdapter.Profile), staged, Scope, default);
        await store.FinalizeAsync(staged);
        if (complete) await persistence.CompleteAsync(attempt with { Outcome = ImportOutcome.Succeeded }, default);
        return raw.Id;
    }
    private static async Task<(ProviderPublicationInput Input, PipelineClaim Claim)> Claim(Setup setup, Guid rawId, int maximum = 3)
    {
        var s = setup.Scenario; var publisher = s.Get<ProviderPublication>(); var input = await publisher.PlanAsync(rawId, Canary, ValidationEvidenceKind.Synthetic);
        var jobs = s.Get<PostgreSqlPipeline>(); var id = Guid.NewGuid();
        await jobs.PlanAsync(id, new(1, PipelineKind.ProviderPublication, new(await s.Now()), JsonSerializer.Serialize(input), MaximumAttempts: maximum), Approval);
        await jobs.SetEnabledAsync(id, true, Approval); return (input, (await jobs.AcquireAsync(Approval))!);
    }
    internal static async Task<ProviderPublicationArtifact> Publish(Setup setup, Guid rawId)
    { var p = await Claim(setup, rawId); return await setup.Scenario.Get<ProviderPublication>().PublishAsync(p.Claim, p.Input, Approval); }
    private static async Task Acknowledge(Setup setup, PipelineClaim claim)
    {
        var s = setup.Scenario; var work = s.Get<PipelineWork>(); var (outcome, bytes) = await work.ExecuteAsync(claim, Approval, default);
        Assert.Equal(PipelineState.Completed, outcome.State); await s.Get<PostgreSqlPipeline>().CompleteAsync(claim, outcome, Approval, artifact: bytes);
    }
    private static Task<int> Command(Setup setup, string action, Guid? execution = null, Guid? job = null, Guid? receipt = null) =>
        PipelineOperatorCommand.RunAsync(setup.Scenario.Scope.ServiceProvider, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Pipeline:Action"] = action, ["Pipeline:Actor"] = Approval.Actor, ["Pipeline:Reason"] = Approval.Reason, ["Pipeline:Approve"] = "true",
            ["Pipeline:ExecutionId"] = execution?.ToString(), ["Pipeline:JobId"] = job?.ToString(), ["Pipeline:ReceiptId"] = receipt?.ToString() }).Build(), true);

    [Fact] public async Task Committed_fixture_receipt_is_independently_verified_and_dispatch_is_idempotent()
    {
        var f = await Create(); await using var s = f.Scenario;
        // Simulate death after the original capture commits, before its audit acknowledgment.
        var rawId = await Capture(f, complete: false); var p = await Claim(f, rawId);
        var originalRun = (await s.Db.RawPayloads.SingleAsync(r => r.Id == rawId)).IngestionRunId;
        Assert.Equal(IngestionRunStatus.Running, (await s.Db.IngestionRuns.SingleAsync(r => r.Id == originalRun)).Status);
        var before = await s.Now(); var a = await s.Get<ProviderPublication>().PublishAsync(p.Claim, p.Input, Approval);
        Assert.True(a.Verified); Assert.Equal(ProviderPublicationState.Published, a.Report.State);
        Assert.Equal(3, await s.Db.Observations.CountAsync(o => o.RawPayloadId == rawId));
        Assert.Equal(f.EventId, Assert.Single(a.Report.Fixtures).EventId); Assert.True(a.RecordedUtc >= before);
        Assert.All(await s.Db.Observations.Where(o => o.RawPayloadId == rawId).ToArrayAsync(), o => Assert.Equal(a.Report.RetrievedUtc, o.RetrievedAtUtc));
        var repeat = await s.Get<ProviderPublication>().PublishAsync(p.Claim, p.Input, Approval);
        Assert.Equal(a.Hash, repeat.Hash); Assert.Equal(a.Report.ReceiptId, repeat.Report.ReceiptId);
        var another = await Publish(f, rawId); Assert.Equal(a.Report.ReceiptId, another.Report.ReceiptId);
        Assert.Equal(1, await s.Db.Set<ProviderPublicationRecord>().CountAsync(r => r.RawId == rawId));
        Assert.Equal(3, await s.Db.Observations.CountAsync(o => o.RawPayloadId == rawId));
    }
    [Fact] public async Task Review_required_raw_can_be_reprocessed_after_explicit_governed_review()
    {
        var f = await Create(reviewed: false); await using var s = f.Scenario; var raw = await Capture(f); var p = await Claim(f, raw);
        var blocked = await s.Get<ProviderPublication>().PublishAsync(p.Claim, p.Input, Approval);
        Assert.Equal(ProviderPublicationState.ReviewRequired, blocked.Report.State); Assert.Empty(await s.Db.Observations.Where(o => o.RawPayloadId == raw).ToArrayAsync());
        var row = Assert.Single(blocked.Report.Fixtures);
        var e = await s.Db.SportingEvents.SingleAsync(e => e.Id == f.EventId);
        Guid[] targets = [e.CompetitionId, e.SeasonId!.Value, f.HomeId, f.AwayId, e.Id];
        for (var index = 0; index < row.Bindings.Count; index++)
        {
            var b = row.Bindings[index]; var identity = await s.Db.ProviderIdentities.SingleAsync(i => i.Id == b.IdentityId);
            Assert.Equal("accepted", (await s.Get<IIdentityReview>().DecideAsync(new(identity.Id, s.Source, ReviewAction.Approve,
                new(identity.EntityKind, targets[index]), b.Version, Approval.Actor, Approval.Reason))).Result);
        }
        var published = await s.Get<ProviderPublication>().PublishAsync(p.Claim, p.Input, Approval);
        Assert.Equal(ProviderPublicationState.Published, published.Report.State); Assert.NotEqual(blocked.Report.Key, published.Report.Key);
        Assert.Equal(3, await s.Db.Observations.CountAsync(o => o.RawPayloadId == raw));
        Assert.Equal(2, await s.Db.Set<ProviderPublicationRecord>().CountAsync(r => r.RawId == raw));
    }
    [Theory] [InlineData("ambiguous")] [InlineData("conflicting")]
    public async Task Ambiguous_or_conflicting_team_bindings_never_publish(string fault)
    {
        var f = await Create(); await using var s = f.Scenario; var raw = await Capture(f); var p = await Claim(f, raw);
        var anchor = await s.Db.ProviderIdentities.SingleAsync(i => i.DataSourceId == s.Source && i.ExternalId == ApiFootballPublicationMapping.Reference("team", 99901));
        Assert.Equal("accepted", (await s.Get<IIdentityReview>().DecideAsync(new(anchor.Id, s.Source,
            fault == "ambiguous" ? ReviewAction.Ambiguous : ReviewAction.Approve,
            fault == "ambiguous" ? null : new(CanonicalEntityKind.Participant, f.AwayId), 1, Approval.Actor, Approval.Reason))).Result);
        var result = await s.Get<ProviderPublication>().PublishAsync(p.Claim, p.Input, Approval);
        Assert.NotEqual(ProviderPublicationState.Published, result.Report.State);
        Assert.Empty(await s.Db.Observations.Where(o => o.RawPayloadId == raw).ToArrayAsync());
        Assert.Equal("accepted", (await s.Get<IIdentityReview>().DecideAsync(new(anchor.Id, s.Source, ReviewAction.Approve,
            new(CanonicalEntityKind.Participant, f.HomeId), 2, Approval.Actor, Approval.Reason))).Result);
        Assert.Equal(ProviderPublicationState.Published, (await s.Get<ProviderPublication>().PublishAsync(p.Claim, p.Input, Approval)).Report.State);
    }
    [Fact] public async Task Approved_event_identity_correction_publishes_forward_without_rebinding_history()
    {
        var f = await Create(); await using var s = f.Scenario; var raw = await Capture(f); var original = await Publish(f, raw); var cutoff = await s.Now();
        var firstEvent = await s.Db.SportingEvents.SingleAsync(e => e.Id == f.EventId);
        var competition = await s.Db.Competitions.SingleAsync(c => c.Id == firstEvent.CompetitionId);
        var season = await s.Db.Seasons.SingleAsync(x => x.Id == firstEvent.SeasonId);
        var correction = new SportingEvent(Guid.NewGuid(), competition, season, null, SportingEventStatus.Scheduled, await s.Now());
        correction.AddParticipant(await s.Db.Participants.SingleAsync(p => p.Id == f.HomeId), ParticipantRole.Home, 1);
        correction.AddParticipant(await s.Db.Participants.SingleAsync(p => p.Id == f.AwayId), ParticipantRole.Away, 2);
        s.Db.Add(correction); await s.Db.SaveChangesAsync();
        var eventBinding = original.Report.Fixtures.Single().Bindings[4];
        Assert.Equal("accepted", (await s.Get<IIdentityReview>().DecideAsync(new(eventBinding.IdentityId, s.Source, ReviewAction.Approve,
            new(CanonicalEntityKind.SportingEvent, correction.Id), eventBinding.Version, Approval.Actor, Approval.Reason))).Result);
        var next = await Publish(f, await Capture(f)); Assert.Equal(ProviderPublicationState.Published, next.Report.State);
        Assert.Equal(correction.Id, next.Report.Fixtures.Single().EventId);
        var historical = await s.Db.Observations.Where(o => o.DataSourceId == s.Source && o.RecordedAtUtc <= cutoff && o.Type == ObservationType.EventDate).ToArrayAsync();
        Assert.Equal(f.EventId, Assert.Single(historical).CanonicalSportingEventId);
        Assert.Equal(f.EventId, (await s.Get<ProviderPublication>().VerifyAsync(original.Report.ReceiptId, false)).Report.Fixtures.Single().EventId);
    }
    [Theory] [InlineData("league")] [InlineData("season")] [InlineData("hash")] [InlineData("profile")] [InlineData("source")]
    public async Task Mismatched_immutable_input_is_rejected(string fault)
    {
        var f = await Create(); await using var s = f.Scenario; var json = Payload();
        if (fault == "league") json["response"]![0]!["league"]!["id"] = 1;
        if (fault == "season") json["response"]![0]!["league"]!["season"] = 2025;
        var raw = await Capture(f, json);
        if (fault is "league" or "season") { await Assert.ThrowsAsync<InvalidDataException>(() => Claim(f, raw)); return; }
        var p = await Claim(f, raw);
        var bad = fault switch { "hash" => p.Input with { RawHash = new('0', 64) }, "profile" => p.Input with { Profile = "metadata-v1" }, _ => p.Input with { SourceId = Guid.NewGuid() } };
        await Assert.ThrowsAsync<InvalidDataException>(() => s.Get<ProviderPublication>().PublishAsync(p.Claim, bad, Approval));
        Assert.Empty(await s.Db.Set<ProviderPublicationRecord>().Where(r => r.RawId == raw).ToArrayAsync());
    }
    [Theory] [InlineData("revoked")] [InlineData("disabled")] [InlineData("corrupt")]
    public async Task Rights_and_original_bytes_are_checked_at_publication(string fault)
    {
        var f = await Create(); await using var s = f.Scenario; var raw = await Capture(f); var p = await Claim(f, raw);
        if (fault == "revoked")
        {
            var policy = await s.Db.SourcePolicies.Include(p => p.Audit).SingleAsync(p => p.Id == f.PolicyId);
            policy.Revoke(Guid.NewGuid(), Approval.Actor, Approval.Reason, await s.Now()); await s.Db.SaveChangesAsync();
        }
        else if (fault == "disabled") { (await s.Db.DataSources.SingleAsync(x => x.Id == s.Source)).IsEnabled = false; await s.Db.SaveChangesAsync(); }
        else { var r = await s.Db.RawPayloads.SingleAsync(r => r.Id == raw); await File.WriteAllTextAsync(Path.Combine(s.Root, r.StorageKey + ".raw"), "corrupt"); }
        var error = await Record.ExceptionAsync(() => s.Get<ProviderPublication>().PublishAsync(p.Claim, p.Input, Approval));
        Assert.NotNull(error); Assert.Empty(await s.Db.Observations.Where(o => o.RawPayloadId == raw).ToArrayAsync());
    }
    [Theory] [InlineData("retention")] [InlineData("retention-current")] [InlineData("attribution")]
    public async Task Restrictions_fail_closed_without_an_implemented_satisfaction_mechanism(string restriction)
    {
        var f = await Create(restriction); await using var s = f.Scenario;
        // Deliberately persist a controlled RAW manifest which is no longer eligible
        // for use, proving a hand-written approved job cannot bypass commit rights.
        var bytes = Encoding.UTF8.GetBytes(Payload().ToJsonString()); var store = s.Get<IRawPayloadStore>(); var staged = await store.StageAsync(bytes);
        var now = await s.Now(); var raw = new RawPayload { Id = Guid.NewGuid(), DataSourceId = s.Source, ContentType = "application/json",
            ContentHashSha256 = staged.Hash, StorageKey = staged.StorageKey, ByteLength = staged.Length,
            RetrievedAtUtc = restriction == "retention" ? now.AddDays(-2) : now, CreatedAtUtc = now,
            ExternalReference = "fixture:" + ApiFootballFixtureAdapter.Profile };
        s.Db.Add(raw); s.Db.FootballRawContexts.Add(new() { RawId = raw.Id, SourceId = s.Source, CompetitionReference = "999999", SeasonReference = "2026" });
        await s.Db.SaveChangesAsync(); await store.FinalizeAsync(staged);
        var sourceCode = await s.Db.DataSources.Where(x => x.Id == s.Source).Select(x => x.Code).SingleAsync();
        var input = new ProviderPublicationInput(1, raw.Id, s.Source, sourceCode, staged.Hash, f.PolicyId, 1, Scope, Canary, ValidationEvidenceKind.Synthetic);
        var jobs = s.Get<PostgreSqlPipeline>(); var job = Guid.NewGuid();
        await jobs.PlanAsync(job, new(1, PipelineKind.ProviderPublication, new(await s.Now()), JsonSerializer.Serialize(input)), Approval);
        await jobs.SetEnabledAsync(job, true, Approval); var claim = (await jobs.AcquireAsync(Approval))!;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Get<ProviderPublication>().PublishAsync(claim, input, Approval));
        Assert.Empty(await s.Db.Set<ProviderPublicationRecord>().Where(r => r.RawId == raw.Id).ToArrayAsync());
    }
    [Theory] [InlineData("expired")] [InlineData("stale")] [InlineData("cancelled")]
    public async Task Lease_owner_and_cancellation_fence_all_writes(string fault)
    {
        var f = await Create(); await using var s = f.Scenario; var raw = await Capture(f); var p = await Claim(f, raw); var claim = p.Claim;
        if (fault == "expired") await s.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE pipeline.\"Executions\" SET \"LeaseUntilUtc\"=\"StartedUtc\"+interval '0.000001 seconds' WHERE \"Id\"={claim.ExecutionId}");
        if (fault == "stale") claim = claim with { Owner = Guid.NewGuid() };
        if (fault == "cancelled") await s.Get<PostgreSqlPipeline>().CancelAsync(claim.ExecutionId, Approval);
        Assert.NotNull(await Record.ExceptionAsync(() => s.Get<ProviderPublication>().PublishAsync(claim, p.Input, Approval)));
        Assert.Empty(await s.Db.Observations.Where(o => o.RawPayloadId == raw).ToArrayAsync());
        if (fault == "cancelled") Assert.Equal(PipelineState.Cancelled,
            (await s.Get<PostgreSqlPipeline>().CompleteAsync(claim, new(PipelineState.Cancelled, "operator_cancelled"), Approval)).State);
    }
    [Fact] public async Task Cancellation_after_child_commit_preserves_child_but_does_not_acknowledge_artifact()
    {
        var f = await Create(); await using var s = f.Scenario; var raw = await Capture(f); var p = await Claim(f, raw);
        var (outcome, artifact) = await s.Get<PipelineWork>().ExecuteAsync(p.Claim, Approval, default);
        await s.Get<PostgreSqlPipeline>().CancelAsync(p.Claim.ExecutionId, Approval);
        var terminal = await s.Get<PostgreSqlPipeline>().CompleteAsync(p.Claim, outcome, Approval, artifact: artifact);
        Assert.Equal(PipelineState.Cancelled, terminal.State); Assert.Null(terminal.ArtifactId);
        Assert.Empty(await s.Db.Set<PipelineArtifact>().Where(a => a.ExecutionId == p.Claim.ExecutionId).ToArrayAsync());
        Assert.Equal(1, await s.Db.Set<ProviderPublicationRecord>().CountAsync(r => r.RawId == raw));
        Assert.Equal(3, await s.Db.Observations.CountAsync(o => o.RawPayloadId == raw));
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Receipt_failure_rolls_back_all_canonical_writes_and_same_raw_can_be_recovered(bool reviewed)
    {
        var f = await Create(reviewed: reviewed); await using var s = f.Scenario; var raw = await Capture(f); var p = await Claim(f, raw);
        var suffix = Guid.NewGuid().ToString("N");
        // Test-only DDL identifiers and UUID literals are generated locally, never user input.
#pragma warning disable EF1002
        await s.Db.Database.ExecuteSqlRawAsync($"CREATE FUNCTION ingestion.fail_{suffix}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'controlled publication crash'; END $$; CREATE TRIGGER fail_{suffix} BEFORE INSERT ON ingestion.\"ProviderPublications\" FOR EACH ROW WHEN (NEW.\"RawId\"='{raw:D}'::uuid) EXECUTE FUNCTION ingestion.fail_{suffix}();");
        try { await Assert.ThrowsAsync<DbUpdateException>(() => s.Get<ProviderPublication>().PublishAsync(p.Claim, p.Input, Approval)); }
        finally { await s.Db.Database.ExecuteSqlRawAsync($"DROP TRIGGER fail_{suffix} ON ingestion.\"ProviderPublications\"; DROP FUNCTION ingestion.fail_{suffix}();"); }
#pragma warning restore EF1002
        s.Db.ChangeTracker.Clear(); Assert.Empty(await s.Db.Observations.Where(o => o.RawPayloadId == raw).ToArrayAsync());
        Assert.Empty(await s.Db.EventTimeEvidence.Where(o => o.RawId == raw).ToArrayAsync());
        Assert.Empty(await s.Db.Set<ProviderPublicationRecord>().Where(o => o.RawId == raw).ToArrayAsync());
        if (!reviewed) Assert.Empty(await s.Db.ProviderIdentities.Where(i => i.DataSourceId == s.Source).ToArrayAsync());
        Assert.Equal(reviewed ? ProviderPublicationState.Published : ProviderPublicationState.ReviewRequired,
            (await s.Get<ProviderPublication>().PublishAsync(p.Claim, p.Input, Approval)).Report.State);
    }
    [Fact] public async Task Commit_without_parent_ack_recovers_from_original_raw_and_reuses_receipt()
    {
        var f = await Create(); await using var s = f.Scenario; var raw = await Capture(f); var p = await Claim(f, raw);
        var committed = await s.Get<ProviderPublication>().PublishAsync(p.Claim, p.Input, Approval);
        // Simulate process death before pipeline acknowledgment, then operator recovery.
        await s.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE pipeline.\"Executions\" SET \"LeaseUntilUtc\"=\"StartedUtc\"+interval '0.000001 seconds' WHERE \"Id\"={p.Claim.ExecutionId}");
        Assert.Equal(0, await Command(f, "recover", p.Claim.ExecutionId));
        var recovered = await s.Db.Set<PipelineExecution>().AsNoTracking().SingleAsync(e => e.Id == p.Claim.ExecutionId);
        Assert.Equal(2, recovered.Attempt);
        var receipt = await s.Get<ProviderPublication>().VerifyAsync(committed.Report.ReceiptId, false);
        Assert.Equal(committed.Hash, receipt.Hash); Assert.Equal(committed.Report.RawRecordedUtc, receipt.Report.RawRecordedUtc);
        Assert.Equal(1, await s.Db.Set<ProviderPublicationRecord>().CountAsync(r => r.RawId == raw)); Assert.Equal(3, await s.Db.Observations.CountAsync(o => o.RawPayloadId == raw));
        var acquisition = await s.Get<IFootballAcquisitionReports>().AcquisitionAsync(recovered.Id, false);
        Assert.Equal(raw, acquisition.Report.Raw.Id); Assert.Equal(ValidationEvidenceKind.Synthetic, acquisition.Report.EvidenceKind);
    }
    [Fact] public async Task Retry_exhaustion_does_not_publish_or_fetch_new_raw()
    {
        var f = await Create(); await using var s = f.Scenario; var raw = await Capture(f); var p = await Claim(f, raw, maximum: 1);
        await s.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE pipeline.\"Executions\" SET \"LeaseUntilUtc\"=\"StartedUtc\"+interval '0.000001 seconds' WHERE \"Id\"={p.Claim.ExecutionId}");
        Assert.Null(await s.Get<PostgreSqlPipeline>().RetryAsync(p.Claim.ExecutionId, true, Approval));
        Assert.Empty(await s.Db.Set<ProviderPublicationRecord>().Where(r => r.RawId == raw).ToArrayAsync());
        Assert.Equal(1, await s.Db.RawPayloads.CountAsync(r => r.DataSourceId == s.Source));
    }
    [Fact] public async Task Development_worker_plan_and_dispatch_publish_without_provider_transport()
    {
        var f = await Create(); await using var s = f.Scenario; var raw = await Capture(f);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Pipeline:Action"] = "provider-publication-plan", ["Pipeline:RawId"] = raw.ToString(), ["Pipeline:League"] = "999999", ["Pipeline:Season"] = "2026",
            ["Pipeline:From"] = "2026-10-13", ["Pipeline:To"] = "2026-10-19", ["Pipeline:EvidenceKind"] = "Synthetic",
            ["Pipeline:Actor"] = Approval.Actor, ["Pipeline:Reason"] = Approval.Reason }).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => PipelineOperatorCommand.RunAsync(s.Scope.ServiceProvider, config, false));
        Assert.Equal(0, await PipelineOperatorCommand.RunAsync(s.Scope.ServiceProvider, config, true));
        Assert.Empty(await s.Db.Set<ProviderPublicationRecord>().Where(r => r.RawId == raw).ToArrayAsync());
        var input = await s.Get<ProviderPublication>().PlanAsync(raw, Canary, ValidationEvidenceKind.Synthetic);
        var jobs = s.Get<PostgreSqlPipeline>(); var job = Guid.NewGuid();
        await jobs.PlanAsync(job, new(1, PipelineKind.ProviderPublication, new(await s.Now()), JsonSerializer.Serialize(input)), Approval);
        await jobs.SetEnabledAsync(job, true, Approval); Assert.Equal(0, await Command(f, "run-once"));
        Assert.Equal(PipelineState.Completed, (await s.Db.Set<PipelineExecution>().AsNoTracking().SingleAsync(e => e.JobId == job)).State);
        Assert.Equal(1, await s.Db.Set<ProviderPublicationRecord>().CountAsync(r => r.RawId == raw));
    }
    [Fact] public async Task Concurrent_jobs_serialize_on_source_and_publish_one_receipt()
    {
        var f = await Create(); await using var s = f.Scenario; var raw = await Capture(f); var a = await Claim(f, raw); var b = await Claim(f, raw);
        using var scope = s.Provider.CreateScope(); var other = scope.ServiceProvider.GetRequiredService<ProviderPublication>();
        var results = await Task.WhenAll(s.Get<ProviderPublication>().PublishAsync(a.Claim, a.Input, Approval), other.PublishAsync(b.Claim, b.Input, Approval));
        Assert.Equal(results[0].Report.ReceiptId, results[1].Report.ReceiptId);
        Assert.Equal(1, await s.Db.Set<ProviderPublicationRecord>().CountAsync(r => r.RawId == raw));
    }
    [Theory] [InlineData("PST", SportingEventStatus.Postponed)] [InlineData("CANC", SportingEventStatus.Cancelled)] [InlineData("1H", SportingEventStatus.InProgress)]
    public async Task Status_progression_is_durable_without_fabricating_results(string status, SportingEventStatus expected)
    {
        var f = await Create(); await using var s = f.Scenario; await Publish(f, await Capture(f)); var raw = await Capture(f, Payload(status)); await Publish(f, raw);
        Assert.Equal(expected, (await s.Db.Observations.SingleAsync(o => o.RawPayloadId == raw && o.Type == ObservationType.EventStatus)).StatusValue);
        Assert.Empty(await s.Db.FootballResults.Where(r => r.RawId == raw).ToArrayAsync());
    }
    [Fact] public async Task Schedule_and_result_corrections_preserve_as_of_and_frozen_artifacts()
    {
        var f = await Create(); await using var s = f.Scenario;
        var jsonSource = s.Source; s.Source = f.OriginalSourceId;
        var definition = await BacktestWorkflowTests.Definition(s, true);
        var frozen = await s.Get<IHistoricalBacktests>().RunAsync(new(Guid.NewGuid(), definition, Approval.Actor, Approval.Reason, true));
        Assert.Equal(ResultOperationStatus.Succeeded, frozen.Status); s.Source = jsonSource;
        var frozenBefore = CanonicalDatasetJson.Fingerprint(await s.Db.Backtests.AsNoTracking().OrderBy(b => b.Id).ToArrayAsync());
        var firstRaw = await Capture(f, Payload("FT")); var first = await Publish(f, firstRaw); var cutoff = await s.Now();
        var nextRaw = await Capture(f, Payload("FT", goals: 3, hour: 19)); var next = await Publish(f, nextRaw);
        var original = await s.Db.FootballResults.SingleAsync(r => r.RawId == firstRaw); var correction = await s.Db.FootballResults.SingleAsync(r => r.RawId == nextRaw);
        Assert.Equal(original.Id, correction.CorrectsId); Assert.Equal(2, correction.Version); Assert.Equal(2, original.Value.FullTime.Home);
        Assert.Equal(3, correction.Value.FullTime.Home); Assert.True(correction.AvailableAtUtc > cutoff); Assert.True(correction.RecordedAtUtc > cutoff);
        var known = await s.Db.FootballResults.Where(r => r.SourceId == s.Source && r.RecordedAtUtc <= cutoff && r.AvailableAtUtc <= cutoff).ToArrayAsync();
        Assert.Equal(original.Id, Assert.Single(known).Id);
        var schedule = await s.Db.Observations.SingleAsync(o => o.RawPayloadId == nextRaw && o.Type == ObservationType.ScheduledStart);
        Assert.Equal(Assert.Single(first.Report.Fixtures).ScheduleObservationId, schedule.CorrectsObservationId);
        Assert.Equal(frozenBefore, CanonicalDatasetJson.Fingerprint(await s.Db.Backtests.AsNoTracking().OrderBy(b => b.Id).ToArrayAsync()));
        var e = await s.Db.SportingEvents.SingleAsync(e => e.Id == f.EventId);
        var resultReport = await s.Get<IFootballResults>().ReadAsync(new(e.CompetitionId, e.SeasonId!.Value, cutoff, DataPurpose.InternalAnalytics, new(), SourceId: s.Source));
        Assert.Equal(original.Id, Assert.Single(resultReport.Results).Observation.Id);
        Assert.True(resultReport.Results.Single().Eligible);
        Assert.NotEqual(first.Hash, next.Hash);
    }
    [Fact] public async Task Existing_validation_and_prospective_cohort_can_reference_published_raw()
    {
        var f = await Create(); await using var s = f.Scenario; var raw = await Capture(f); var p = await Claim(f, raw); await Acknowledge(f, p.Claim);
        var input = new FootballValidationInput(1, s.Source, raw, p.Input.RawHash, p.Input.Profile, Scope, ValidationEvidenceKind.Synthetic, []);
        var validation = await s.Get<FootballValidation>().BuildAsync(input);
        Assert.Equal(1, validation.ImportedEvents); Assert.Equal(1, validation.EligibleIdentities); Assert.Equal(0, validation.UnknownKickoffs);
        var cohort = await s.Get<FootballProspectiveReports>().BuildAsync(new(1, s.Source, [p.Claim.ExecutionId], [], []), default);
        Assert.Equal(1, cohort.ObservedEvents); Assert.Equal(0, cohort.FrozenPredictions); Assert.Equal(0, cohort.EligiblePredictions);
        Assert.Contains("independent_coverage_evidence_required", cohort.Coverage.Single().Reasons);
        var date = await s.Db.Observations.SingleAsync(o => o.RawPayloadId == raw && o.Type == ObservationType.EventDate);
        var times = await s.Get<IHistoricalCoverage>().TimesAsync(date.ProviderIdentityId, await s.Now(), DatasetMode.HistoricalAsKnown, null, DataPurpose.InternalAnalytics, new());
        Assert.NotNull(Assert.Single(times).Resolution.UtcInstant);
    }
    [Fact] public async Task Revocation_waiting_at_source_boundary_prevents_publication()
    {
        var f = await Create(); await using var s = f.Scenario; var raw = await Capture(f); var p = await Claim(f, raw);
        using var reviewScope = s.Provider.CreateScope(); var reviewer = reviewScope.ServiceProvider.GetRequiredService<BetStatsDbContext>();
        await using var tx = await reviewer.Database.BeginTransactionAsync();
        await reviewer.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM ingestion.\"DataSources\" WHERE \"Id\"={s.Source} FOR UPDATE");
        var publish = s.Get<ProviderPublication>().PublishAsync(p.Claim, p.Input, Approval);
        var policy = await reviewer.SourcePolicies.Include(x => x.Audit).SingleAsync(x => x.Id == f.PolicyId);
        policy.Revoke(Guid.NewGuid(), Approval.Actor, Approval.Reason, await reviewer.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync());
        await reviewer.SaveChangesAsync(); await tx.CommitAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => publish);
        Assert.Empty(await s.Db.Observations.Where(o => o.RawPayloadId == raw).ToArrayAsync());
    }
    private sealed class Loopback : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        { app.Use((context, following) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; return following(); }); next(app); };
    }
    internal sealed class ApiHost(string connection, string root, string environment = "Development") : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment(environment)
            .UseSetting("ConnectionStrings:BetStats", connection).UseSetting("Ingestion:RawStoragePath", root)
            .ConfigureServices(s => s.AddSingleton<IStartupFilter, Loopback>());
    }
    [Theory] [InlineData("pl-PL")] [InlineData("en-GB")]
    public async Task Monitoring_is_read_only_loopback_bilingual_and_currently_authorized(string language)
    {
        var f = await Create(); await using var s = f.Scenario; var raw = await Capture(f); var p = await Claim(f, raw);
        var published = await s.Get<ProviderPublication>().PublishAsync(p.Claim, p.Input, Approval);
        Assert.Equal(0, await Command(f, "provider-publication-inspect", job: p.Claim.JobId));
        Assert.Equal(0, await Command(f, "provider-publication-verify", receipt: published.Report.ReceiptId));
        var before = CanonicalDatasetJson.Fingerprint(new { Raw = await s.Db.RawPayloads.CountAsync(), Observations = await s.Db.Observations.CountAsync(),
            Receipts = await s.Db.Set<ProviderPublicationRecord>().CountAsync(), Predictions = await s.Db.Backtests.CountAsync() });
        await using var host = new ApiHost(fixture.GetConnectionString(), s.Root); using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        foreach (var resource in new[] { "publication/" + published.Report.ReceiptId, "publication-status/" + p.Claim.JobId })
        {
            var response = await client.GetAsync("/api/v1/dashboard/" + resource);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("StorageKey", text); Assert.DoesNotContain(s.Root, text);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/api/v1/dashboard/" + resource, null)).StatusCode);
        }
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider(); await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<ProviderPublicationView>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { ["Artifact"] = published, ["Language"] = language }))).ToHtmlString());
        Assert.Contains(language == "pl-PL" ? "Publikacja dostawcy" : "Provider publication", html); Assert.Contains("SHA256", html);
        Assert.Equal(before, CanonicalDatasetJson.Fingerprint(new { Raw = await s.Db.RawPayloads.CountAsync(), Observations = await s.Db.Observations.CountAsync(),
            Receipts = await s.Db.Set<ProviderPublicationRecord>().CountAsync(), Predictions = await s.Db.Backtests.CountAsync() }));
        await using var production = new ApiHost(fixture.GetConnectionString(), s.Root, "Production"); using var prod = production.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.Forbidden, (await prod.GetAsync("/api/v1/dashboard/publication/" + published.Report.ReceiptId)).StatusCode);
        var policy = await s.Db.SourcePolicies.Include(x => x.Audit).SingleAsync(x => x.Id == f.PolicyId);
        policy.Revoke(Guid.NewGuid(), Approval.Actor, Approval.Reason, await s.Now()); await s.Db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/dashboard/publication/" + published.Report.ReceiptId)).StatusCode);
    }
    [Fact] public async Task Receipt_cannot_be_mutated_through_direct_sql()
    {
        var f = await Create(); await using var s = f.Scenario; var published = await Publish(f, await Capture(f));
        await Assert.ThrowsAsync<PostgresException>(() => s.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ingestion.\"ProviderPublications\" SET \"Hash\"={new string('0',64)} WHERE \"Id\"={published.Report.ReceiptId}"));
    }
    [Theory] [InlineData("lease")] [InlineData("rights")]
    public async Task Database_deferred_boundary_rejects_expiry_or_revocation_at_commit(string fault)
    {
        var f = await Create(); await using var s = f.Scenario; var raw = await Capture(f); var original = await Publish(f, raw);
        var p = await Claim(f, raw); var id = Guid.NewGuid(); var key = CanonicalDatasetJson.Fingerprint(id);
        var manifest = original.Report with { ReceiptId = id, Key = key, JobId = p.Claim.JobId, ExecutionId = p.Claim.ExecutionId, Owner = p.Claim.Owner, Attempt = p.Claim.Attempt };
        var content = CanonicalDatasetJson.Serialize(manifest);
        await using (var tx = await s.Db.Database.BeginTransactionAsync())
        {
            s.Db.Add(new ProviderPublicationRecord { Id = id, Key = key, SourceId = s.Source, RawId = raw, PolicyId = f.PolicyId,
                ExecutionId = p.Claim.ExecutionId, Content = content, Hash = CanonicalDatasetJson.Hash(content) });
            await s.Db.SaveChangesAsync();
            if (fault == "lease") await s.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE pipeline.\"Executions\" SET \"LeaseUntilUtc\"=\"StartedUtc\"+interval '0.000001 seconds' WHERE \"Id\"={p.Claim.ExecutionId}");
            else
            {
                var policy = await s.Db.SourcePolicies.Include(x => x.Audit).SingleAsync(x => x.Id == f.PolicyId);
                policy.Revoke(Guid.NewGuid(), Approval.Actor, Approval.Reason, await s.Now()); await s.Db.SaveChangesAsync();
            }
            var error = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync()); Assert.Equal("23514", error.SqlState);
        }
        s.Db.ChangeTracker.Clear(); Assert.False(await s.Db.Set<ProviderPublicationRecord>().AnyAsync(r => r.Id == id));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Additive_migration_supports_fresh_creation_and_prior_schema_upgrade(bool upgrade)
    {
        var name = "publication_" + Guid.NewGuid().ToString("N");
        var builder = new NpgsqlConnectionStringBuilder(fixture.GetConnectionString()) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(builder.ConnectionString); await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            builder.Database = name;
            await using var db = new BetStatsDbContext(new DbContextOptionsBuilder<BetStatsDbContext>().UseNpgsql(builder.ConnectionString).Options);
            if (upgrade) await db.GetService<IMigrator>().MigrateAsync(db.Database.GetMigrations().Reverse().Skip(1).First());
            await db.Database.MigrateAsync(); Assert.Equal(0, await db.Set<ProviderPublicationRecord>().CountAsync());
            Assert.NotEmpty(await db.Sports.ToArrayAsync()); Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally { await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync(); }
    }
}
