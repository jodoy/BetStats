using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Observations;
using BetStats.Application.Providers;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Domain.Sports;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BetStats.IntegrationTests;

public sealed class FootballIngestionTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed class Scenario : IAsyncDisposable
    {
        public Scenario(BetStatsDbContext context, string? root = null) { Context = context; Root = root ?? Path.Combine(Path.GetTempPath(), "betstats-ingestion-" + Guid.NewGuid().ToString("N")); Storage = new FileSystemRawPayloadStore(Root); }
        public BetStatsDbContext Context { get; }
        public string Root { get; }
        public FileSystemRawPayloadStore Storage { get; }
        public Guid Source { get; set; }
        public RequestBudget Budget { get; set; } = new(new(100, 1000, 1, TimeSpan.FromSeconds(30)), TimeProvider.System);
        public FootballIngestionPersistence Persistence => new(Context, new SourcePolicyEvaluator(new SourcePolicyHistory(Context)), new SourceOperationalStatusReader(Context));
        public FootballIngestion Pipeline(IRawPayloadStore? store = null, IFootballIngestionPersistence? persistence = null) =>
            new(new AuthorizedProviderExecutor(new SourcePolicyEvaluator(new SourcePolicyHistory(Context)), TimeProvider.System, new SourceOperationalStatusReader(Context)), persistence ?? Persistence, store ?? Storage, new FootballDataCsvParser());
        public Task<ImportReport> Run(string csv = SyntheticFootballDemo.Csv, IRawPayloadStore? store = null, IFootballIngestionPersistence? persistence = null, bool live = false, CancellationToken cancellationToken = default) =>
            Pipeline(store, persistence).RunAsync(new FootballDataFixtureAdapter(Source, SyntheticFootballDemo.Bytes(csv), TimeProvider.System, live), SyntheticFootballDemo.Scope, Budget, cancellationToken);
        public async ValueTask DisposeAsync() { await Context.DisposeAsync(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private async Task<Scenario> Create()
    {
        var scenario = new Scenario(fixture.CreateContext());
        scenario.Source = await SyntheticFootballDemo.PrepareAsync(scenario.Context, true, "synthetic-test-" + Guid.NewGuid().ToString("N"));
        return scenario;
    }
    private static Task<DateTime> Now(BetStatsDbContext context) => context.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
    private static Task<int> Observations(Scenario scenario) => scenario.Context.Observations.CountAsync(o => o.DataSourceId == scenario.Source);

    [Fact]
    public async Task Synthetic_end_to_end_preserves_bytes_history_corrections_and_repeat_safety()
    {
        await using var scenario = await Create(); var before = await Now(scenario.Context);
        var report = await scenario.Run();
        Assert.Equal(ImportOutcome.Partial, report.Outcome); Assert.Equal(1, report.RetrievedPayloads); Assert.Equal(6, report.ParsedRecords);
        Assert.Equal(3, report.AcceptedRecords); Assert.Equal(2, report.RejectedRecords); Assert.Equal(2, report.UnresolvedIdentities);
        Assert.Empty(await new ObservationHistory(scenario.Context).ReadAsOfAsync(new(CanonicalEntityKind.SportingEvent, before, dataSourceId: scenario.Source)));
        var raw = await scenario.Context.RawPayloads.SingleAsync(r => r.IngestionRunId == report.RunId);
        Assert.True(raw.RecordedAtUtc >= raw.CreatedAtUtc); Assert.NotNull(raw.ByteLength);
        Assert.Equal(SyntheticFootballDemo.Bytes(), (await scenario.Storage.ReadAsync(new(raw.StorageKey, raw.ContentHashSha256, raw.ByteLength!.Value))).ToArray());
        var terminal = await scenario.Context.IngestionAuditEvents.SingleAsync(a => a.AttemptId == report.AttemptId && a.Sequence == 2);
        Assert.NotNull(terminal.PolicyId); Assert.NotNull(terminal.ApprovalAuditId); Assert.Equal(3, terminal.Issues.Length);
        Assert.Equal("validation_or_identity_incomplete", (await scenario.Context.IngestionRuns.SingleAsync(r => r.Id == report.RunId)).ErrorCode);
        var count = await Observations(scenario);
        Assert.Equal(ImportOutcome.Partial, (await scenario.Run()).Outcome); Assert.Equal(count, await Observations(scenario));
        var cutoff = await Now(scenario.Context);
        var correction = await scenario.Run(SyntheticFootballDemo.CorrectionCsv);
        Assert.Equal(ImportOutcome.Succeeded, correction.Outcome); Assert.Equal(1, correction.AcceptedRecords);
        Assert.Equal(count + 1, await Observations(scenario));
        var firstIdentity = await scenario.Context.ProviderIdentities.SingleAsync(i => i.DataSourceId == scenario.Source && i.ExternalId == "provider:fictional-1");
        var dates = await scenario.Context.Observations.Where(o => o.ProviderIdentityId == firstIdentity.Id && o.Type == ObservationType.EventDate).OrderBy(o => o.Version).ToListAsync();
        Assert.Equal(2, dates.Count); Assert.Equal(dates[0].Id, dates[1].CorrectsObservationId);
        Assert.Equal(new DateOnly(2026, 1, 2), dates[0].DateValue); Assert.Equal(new DateOnly(2026, 1, 3), dates[1].DateValue);
        var past = await new ObservationHistory(scenario.Context).ReadAsOfAsync(new(CanonicalEntityKind.SportingEvent, cutoff, providerIdentityId: firstIdentity.Id));
        Assert.DoesNotContain(past, o => o.Id == dates[1].Id);
        Assert.Equal(ImportOutcome.Reused, (await scenario.Run(SyntheticFootballDemo.CorrectionCsv)).Outcome);
        Assert.Equal(count + 1, await Observations(scenario));
        Assert.Equal(4, await scenario.Context.RawPayloads.CountAsync(r => r.DataSourceId == scenario.Source));
        await scenario.Run(); // Replaying the old partial payload must not reverse the later date correction.
        Assert.Equal(count + 1, await Observations(scenario));
        Assert.All(await scenario.Context.Observations.Where(o => o.DataSourceId == scenario.Source).ToListAsync(), o => Assert.NotNull(o.RawPayloadId));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("revoked")]
    [InlineData("unknown")]
    [InlineData("missing")]
    public async Task Denied_sources_or_permissions_capture_no_raw_and_preserve_denial_audit(string mode)
    {
        await using var scenario = await Create();
        if (mode == "disabled") { (await scenario.Context.DataSources.SingleAsync(s => s.Id == scenario.Source)).IsEnabled = false; await scenario.Context.SaveChangesAsync(); }
        else
        {
            var policy = await scenario.Context.SourcePolicies.Include(p => p.Permissions).Include(p => p.Audit).SingleAsync(p => p.DataSourceId == scenario.Source);
            policy.Revoke(Guid.NewGuid(), "test-reviewer", "Synthetic revocation", await Now(scenario.Context)); await scenario.Context.SaveChangesAsync();
            if (mode is "unknown" or "missing")
            {
                var now = await Now(scenario.Context);
                var replacement = new SourcePolicy(Guid.NewGuid(), scenario.Source, 2, now, null, "synthetic:terms", "synthetic:evidence", now, []);
                scenario.Context.Add(replacement); await scenario.Context.SaveChangesAsync();
                if (mode == "unknown") { replacement.Approve(Guid.NewGuid(), "test-reviewer", "Unknown purposes remain denied", now, now); await scenario.Context.SaveChangesAsync(); }
            }
        }
        var report = await scenario.Run(); Assert.Equal(ImportOutcome.Denied, report.Outcome);
        Assert.Equal(0, await scenario.Context.RawPayloads.CountAsync(r => r.DataSourceId == scenario.Source)); Assert.Empty(scenario.Storage.InventoryStaged());
        Assert.Equal(2, await scenario.Context.IngestionAuditEvents.CountAsync(a => a.AttemptId == report.AttemptId));
        using var lease = scenario.Budget.TryAcquire(); Assert.NotNull(lease);
    }

    [Fact]
    public async Task Missing_source_is_audited_without_an_invalid_run_foreign_key()
    {
        await using var scenario = new Scenario(fixture.CreateContext()) { Source = Guid.NewGuid() };
        var report = await scenario.Run(); Assert.Equal(ImportOutcome.Denied, report.Outcome); Assert.Null(report.RunId);
        Assert.Equal("source_missing", report.ErrorCode); Assert.Equal(2, await scenario.Context.IngestionAuditEvents.CountAsync(a => a.AttemptId == report.AttemptId));
    }
    [Fact]
    public async Task Invalid_live_configuration_and_budget_exhaustion_do_not_capture_raw()
    {
        await using var scenario = await Create();
        Assert.Equal("invalid_configuration", (await scenario.Run(live: true)).ErrorCode);
        using var lease = scenario.Budget.TryAcquire(); Assert.NotNull(lease);
        Assert.Equal("request_budget_exhausted", (await scenario.Run()).ErrorCode);
        Assert.Equal(0, await scenario.Context.RawPayloads.CountAsync(r => r.DataSourceId == scenario.Source));
    }
    [Fact]
    public async Task Missing_policy_does_not_authorize_a_configured_source()
    {
        await using var scenario = new Scenario(fixture.CreateContext()) { Source = Guid.NewGuid() };
        scenario.Context.Add(new BetStats.Infrastructure.Persistence.Entities.DataSource { Id = scenario.Source, Code = "synthetic-no-policy-" + Guid.NewGuid().ToString("N"), DisplayName = "Synthetic", IsEnabled = true, CreatedAtUtc = await Now(scenario.Context) });
        await scenario.Context.SaveChangesAsync();
        var report = await scenario.Run(); Assert.Equal("policy_MissingPolicy", report.ErrorCode);
        Assert.Equal(ImportOutcome.Denied, report.Outcome); Assert.Equal(0, await scenario.Context.RawPayloads.CountAsync(r => r.DataSourceId == scenario.Source));
    }

    private sealed class StorageHook(IRawPayloadStore inner, Func<Task> hook, bool failStage = false, bool failFinalize = false) : IRawPayloadStore
    {
        public async Task<StoredPayload> StageAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        { if (failStage) throw new IOException("Synthetic storage failure"); var payload = await inner.StageAsync(bytes, cancellationToken); await hook(); return payload; }
        public Task FinalizeAsync(StoredPayload payload, CancellationToken cancellationToken = default) => failFinalize ? throw new IOException("Synthetic finalize failure") : inner.FinalizeAsync(payload, cancellationToken);
        public Task<ReadOnlyMemory<byte>> ReadAsync(StoredPayload payload, CancellationToken cancellationToken = default) => inner.ReadAsync(payload, cancellationToken);
        public IReadOnlyList<string> InventoryStaged() => inner.InventoryStaged();
    }
    [Fact]
    public async Task Revocation_between_retrieval_and_capture_denies_metadata_and_exposes_orphan_for_reconciliation()
    {
        await using var scenario = await Create();
        var storage = new StorageHook(scenario.Storage, async () =>
        {
            var policy = await scenario.Context.SourcePolicies.Include(p => p.Permissions).Include(p => p.Audit).SingleAsync(p => p.DataSourceId == scenario.Source);
            policy.Revoke(Guid.NewGuid(), "reviewer", "Synthetic revocation at boundary", await Now(scenario.Context)); await scenario.Context.SaveChangesAsync();
        });
        var report = await scenario.Run(store: storage); Assert.Equal(ImportOutcome.Denied, report.Outcome);
        Assert.Equal(0, await scenario.Context.RawPayloads.CountAsync(r => r.DataSourceId == scenario.Source));
        var recovered = await new IngestionRecovery(scenario.Context, scenario.Storage, scenario.Persistence).ReconcileStagedAsync();
        Assert.Equal("orphan_requires_authorized_maintenance", Assert.Single(recovered).Outcome); Assert.Single(scenario.Storage.InventoryStaged());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Filesystem_failure_is_not_reported_as_success_and_finalization_is_recoverable(bool afterMetadata)
    {
        await using var scenario = await Create();
        var storage = new StorageHook(scenario.Storage, () => Task.CompletedTask, failStage: !afterMetadata, failFinalize: afterMetadata);
        var report = await scenario.Run(store: storage); Assert.Equal(ImportOutcome.Failed, report.Outcome);
        Assert.Equal(afterMetadata ? 1 : 0, await scenario.Context.RawPayloads.CountAsync(r => r.DataSourceId == scenario.Source));
        Assert.Equal(0, await Observations(scenario));
        var recovered = await new IngestionRecovery(scenario.Context, scenario.Storage, scenario.Persistence).ReconcileStagedAsync();
        if (afterMetadata) Assert.Equal("finalized_verified", Assert.Single(recovered).Outcome); else Assert.Empty(recovered);
        Assert.Empty(scenario.Storage.InventoryStaged());
    }

    [Fact]
    public async Task Explicit_mapping_collision_is_rejected_without_overwriting_history()
    {
        await using var scenario = await Create(); Assert.Equal(ImportOutcome.Partial, (await scenario.Run()).Outcome);
        var count = await Observations(scenario);
        var collision = SyntheticFootballDemo.CorrectionCsv.Replace("Amber Comets,Cobalt Owls", "Amber Comets,Silver Foxes", StringComparison.Ordinal);
        var report = await scenario.Run(collision); Assert.Equal(ImportOutcome.Denied, report.Outcome); Assert.Equal("event_identity_collision", report.ErrorCode);
        Assert.Equal(count, await Observations(scenario));
    }
    [Fact]
    public async Task Interrupted_run_has_explicit_recovery_without_automatic_restart()
    {
        await using var scenario = await Create(); var persistence = scenario.Persistence;
        var report = await persistence.BeginAsync(Guid.NewGuid(), scenario.Source, CancellationToken.None);
        Assert.Single(await scenario.Context.IngestionAuditEvents.Where(a => a.AttemptId == report.AttemptId).ToListAsync());
        await new IngestionRecovery(scenario.Context, scenario.Storage, persistence).MarkInterruptedAsync(report.AttemptId);
        var terminal = await scenario.Context.IngestionAuditEvents.SingleAsync(a => a.AttemptId == report.AttemptId && a.Sequence == 2);
        Assert.Equal(ImportOutcome.Interrupted, terminal.Outcome); Assert.Equal("operator_confirmed_interruption", terminal.ErrorCode);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => scenario.Context.IngestionAuditEvents.ExecuteDeleteAsync());
    }
    [Fact]
    public async Task Cancellation_after_capture_check_records_interruption_and_keeps_manifest()
    {
        await using var scenario = await Create(); using var cancellation = new CancellationTokenSource();
        var storage = new StorageHook(scenario.Storage, () => { cancellation.Cancel(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.Run(store: storage, cancellationToken: cancellation.Token));
        Assert.Equal(ImportOutcome.Interrupted, (await scenario.Context.IngestionAuditEvents.SingleAsync(a => a.DataSourceId == scenario.Source && a.Sequence == 2)).Outcome);
        Assert.Single(scenario.Storage.InventoryStaged()); Assert.Equal(0, await Observations(scenario));
    }

    private sealed class PersistenceHook(IFootballIngestionPersistence inner, Func<Task>? beforePublish = null, bool failCapture = false) : IFootballIngestionPersistence
    {
        public Task<ImportReport> BeginAsync(Guid attemptId, Guid sourceId, CancellationToken token) => inner.BeginAsync(attemptId, sourceId, token);
        public Task EnsureCaptureAllowedAsync(Guid sourceId, CancellationToken token) => inner.EnsureCaptureAllowedAsync(sourceId, token);
        public Task<RawCapture> CaptureAsync(ImportReport attempt, RetrievedContent content, StoredPayload payload, CancellationToken token) =>
            failCapture ? throw new InvalidOperationException("Synthetic metadata persistence failure") : inner.CaptureAsync(attempt, content, payload, token);
        public async Task<ImportReport> PublishAsync(ImportReport attempt, RawCapture raw, FootballImportScope scope, FootballParseResult parsed, CancellationToken token)
        { if (beforePublish is not null) await beforePublish(); return await inner.PublishAsync(attempt, raw, scope, parsed, token); }
        public Task CompleteAsync(ImportReport report, CancellationToken token) => inner.CompleteAsync(report, token);
    }
    [Fact]
    public async Task Metadata_failure_leaves_an_inventoryable_orphan_and_failed_audit()
    {
        await using var scenario = await Create(); var report = await scenario.Run(persistence: new PersistenceHook(scenario.Persistence, failCapture: true));
        Assert.Equal(ImportOutcome.Failed, report.Outcome); Assert.Single(scenario.Storage.InventoryStaged());
        Assert.Equal(0, await scenario.Context.RawPayloads.CountAsync(r => r.DataSourceId == scenario.Source));
        Assert.Equal(ImportOutcome.Failed, (await scenario.Context.IngestionAuditEvents.SingleAsync(a => a.AttemptId == report.AttemptId && a.Sequence == 2)).Outcome);
    }
    [Fact]
    public async Task Revoked_policy_before_publication_preserves_raw_but_denies_observations()
    {
        await using var scenario = await Create();
        var persistence = new PersistenceHook(scenario.Persistence, async () =>
        {
            var policy = await scenario.Context.SourcePolicies.Include(p => p.Audit).Include(p => p.Permissions).SingleAsync(p => p.DataSourceId == scenario.Source);
            policy.Revoke(Guid.NewGuid(), "reviewer", "Synthetic publication boundary revocation", await Now(scenario.Context)); await scenario.Context.SaveChangesAsync();
        });
        var report = await scenario.Run(persistence: persistence); Assert.Equal(ImportOutcome.Denied, report.Outcome);
        Assert.Equal(1, await scenario.Context.RawPayloads.CountAsync(r => r.DataSourceId == scenario.Source)); Assert.Equal(0, await Observations(scenario));
    }
    [Fact]
    public async Task Concurrent_successful_imports_share_one_publication_and_do_not_duplicate_events()
    {
        await using var first = await Create(); await using var second = new Scenario(fixture.CreateContext(), first.Root) { Source = first.Source };
        first.Budget = second.Budget = new(new(100, 1000, 2, TimeSpan.FromSeconds(30)), TimeProvider.System);
        var reports = await Task.WhenAll(first.Run(SyntheticFootballDemo.CorrectionCsv), second.Run(SyntheticFootballDemo.CorrectionCsv));
        Assert.Contains(reports, r => r.Outcome == ImportOutcome.Succeeded); Assert.Contains(reports, r => r.Outcome == ImportOutcome.Reused);
        Assert.Equal(1, await first.Context.IngestionPublications.CountAsync(p => p.DataSourceId == first.Source && p.IsBatch));
        Assert.Equal(2, await first.Context.RawPayloads.CountAsync(p => p.DataSourceId == first.Source));
        Assert.Equal(4, await Observations(first));
    }

    [Fact]
    public async Task Operator_review_can_resolve_unknown_team_without_remapping_prior_observations()
    {
        await using var scenario = await Create(); Assert.Equal(2, (await scenario.Run()).UnresolvedIdentities);
        var identity = await scenario.Context.ProviderIdentities.SingleAsync(i => i.DataSourceId == scenario.Source && i.ExternalId == FootballDataCsvParser.TeamReference("FICT", "Unmapped Ravens"));
        var previous = await scenario.Context.IdentityResolutions.Where(d => d.ProviderIdentityId == identity.Id).OrderByDescending(d => d.Version).FirstAsync();
        var now = await Now(scenario.Context);
        var team = new Participant(Guid.NewGuid(), ReferenceSports.All.Single(s => s.Code == "football").Id, "Unmapped Ravens", ParticipantType.Team);
        scenario.Context.Add(team);
        scenario.Context.Add(new IdentityResolution(Guid.NewGuid(), identity, ResolutionStatus.Resolved, new(CanonicalEntityKind.Participant, team.Id), "operator:test-review", "Explicit fixture review", now, previous));
        await scenario.Context.SaveChangesAsync();
        var oldCutoff = await Now(scenario.Context); var report = await scenario.Run();
        Assert.Equal(4, report.AcceptedRecords); Assert.Equal(0, report.UnresolvedIdentities);
        var oldHistory = await new ObservationHistory(scenario.Context).ReadAsOfAsync(new(CanonicalEntityKind.Participant, oldCutoff, providerIdentityId: identity.Id));
        Assert.All(oldHistory, o => Assert.Null(o.CanonicalId));
        Assert.Contains(await scenario.Context.Observations.Where(o => o.ProviderIdentityId == identity.Id).ToListAsync(), o => o.CanonicalId == team.Id);
    }
    [Fact]
    public async Task Cross_sport_reviewed_mapping_fails_closed()
    {
        await using var scenario = await Create();
        var identity = await scenario.Context.ProviderIdentities.SingleAsync(i => i.DataSourceId == scenario.Source && i.ExternalId == FootballDataCsvParser.TeamReference("FICT", "Amber Comets"));
        var previous = await scenario.Context.IdentityResolutions.SingleAsync(d => d.ProviderIdentityId == identity.Id);
        var otherSport = new Participant(Guid.NewGuid(), ReferenceSports.All.Single(s => s.Code == "tennis").Id, "Fictional different sport", ParticipantType.Team);
        scenario.Context.Add(otherSport); scenario.Context.Add(new IdentityResolution(Guid.NewGuid(), identity, ResolutionStatus.Resolved, new(CanonicalEntityKind.Participant, otherSport.Id), "operator:test", "Deliberately invalid sport fixture", await Now(scenario.Context), previous));
        await scenario.Context.SaveChangesAsync();
        var report = await scenario.Run(); Assert.Equal("identity_context_mismatch", report.ErrorCode); Assert.Equal(0, await Observations(scenario));
    }
    private sealed class InvalidMetadataAdapter(Guid source) : IContentProviderAdapter
    {
        private readonly FootballDataFixtureAdapter inner = new(source, SyntheticFootballDemo.Bytes(), TimeProvider.System);
        public ProviderDescriptor Descriptor => inner.Descriptor;
        public RetrievedContent? Content => inner.Content is { } content ? content with { ContentType = "" } : null;
        public ConfigurationValidation ValidateConfiguration() => inner.ValidateConfiguration();
        public Task<ProviderResult> ExecuteAsync(ProviderRequest request, CancellationToken cancellationToken) => inner.ExecuteAsync(request, cancellationToken);
    }
    [Fact]
    public async Task Real_metadata_constraint_failure_rolls_back_capture_but_preserves_failure_audit()
    {
        await using var scenario = await Create();
        var report = await scenario.Pipeline().RunAsync(new InvalidMetadataAdapter(scenario.Source), SyntheticFootballDemo.Scope, scenario.Budget);
        Assert.Equal(ImportOutcome.Failed, report.Outcome); Assert.Single(scenario.Storage.InventoryStaged());
        Assert.Equal(0, await scenario.Context.RawPayloads.CountAsync(r => r.DataSourceId == scenario.Source));
        Assert.Equal(ImportOutcome.Failed, (await scenario.Context.IngestionAuditEvents.SingleAsync(a => a.AttemptId == report.AttemptId && a.Sequence == 2)).Outcome);
    }
}
