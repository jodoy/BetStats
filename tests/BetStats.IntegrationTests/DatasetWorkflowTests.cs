using BetStats.Application.Datasets;
using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Application.Quality;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Domain.Quality;
using BetStats.Infrastructure.Datasets;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BetStats.IntegrationTests;

public sealed class DatasetWorkflowTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed class Scenario : IAsyncDisposable
    {
        public BetStatsDbContext Db { get; }
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "betstats-datasets-" + Guid.NewGuid().ToString("N"));
        public FileSystemRawPayloadStore Store { get; }
        public Guid Source { get; set; }
        public DatasetDefinition Definition { get; set; } = null!;
        public Scenario(BetStatsDbContext db) { Db = db; Store = new(Root); }
        public SourcePolicyEvaluator Policies => new(new SourcePolicyHistory(Db));
        public ISourceOperationalStatus Sources => new SourceOperationalStatusReader(Db);
        public AnalyticalQualityGate Gate => new(Db, Policies, Sources);
        public PostgreSqlDatasets Service(IRawPayloadStore? store = null, IAnalyticalQualityGate? gate = null) => new(Db, store ?? Store, new FootballDataCsvParser(), gate ?? Gate, Policies, Sources);
        public FootballIngestion Ingestion => new(new(Policies, TimeProvider.System, Sources), new FootballIngestionPersistence(Db, Policies, Sources), Store, new FootballDataCsvParser());
        public Task<DatasetBuildResult> Build(DatasetDefinition? definition = null) => Service().BuildAsync(new(definition ?? Definition, "operator:dataset-test", "Build fictional evidence"));
        public Task<DateTime> Now() => Db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        public Task<ImportReport> Import(string csv) => Ingestion.RunAsync(new FootballDataFixtureAdapter(Source, SyntheticFootballDemo.Bytes(csv), TimeProvider.System), SyntheticFootballDemo.Scope,
            new(new(100, 1000, 1, TimeSpan.FromSeconds(30)), TimeProvider.System));
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private async Task<Scenario> Create()
    {
        var s = new Scenario(fixture.CreateContext());
        var today = DateOnly.FromDateTime(await s.Now());
        (s.Source, s.Definition) = await SyntheticDatasetDemo.PrepareAsync(s.Db, s.Ingestion, new(new(100, 1000, 1, TimeSpan.FromSeconds(30)), TimeProvider.System),
            TimeProvider.System, true, "dataset-fiction-" + Guid.NewGuid().ToString("N"), today.AddDays(2));
        return s;
    }
    private static Guid Snapshot(DatasetBuildResult result) { Assert.True(result.Status == DatasetBuildStatus.Succeeded, result.FailureCode); return Assert.IsType<Guid>(result.SnapshotId); }

    [Fact]
    public async Task Frozen_repeat_build_verification_comparison_and_missing_status_are_explicit()
    {
        await using var s = await Create(); var first = await s.Build(); var id = Snapshot(first); var repeat = await s.Build();
        Assert.Equal(first.SnapshotId, repeat.SnapshotId); Assert.Equal(first.ManifestHash, repeat.ManifestHash); Assert.NotEqual(first.AttemptId, repeat.AttemptId);
        var snapshot = await s.Service().InspectAsync(id); var row = Assert.Single(snapshot.Manifest.Rows);
        Assert.All(row.Features.Values, v => { Assert.Null(v.Value); Assert.NotNull(v.MissingReason); });
        Assert.Contains(row.Excluded, e => e.Reason == "future_or_same_day"); Assert.Contains(row.Excluded, e => e.Reason == "target_event");
        Assert.NotEmpty(row.Target.FrozenRecords); Assert.NotEmpty(row.History); Assert.NotEmpty(row.Target.Policies);
        var verification = await s.Service().VerifyAsync(id);
        Assert.True(verification.ArtifactIntegrity); Assert.True(verification.EvidenceComplete); Assert.True(verification.CurrentlyAuthorized); Assert.True(verification.FeaturesReproducible);
        Assert.Equal(0, (await s.Service().CompareAsync(id, id, 0, 20)).Total);
        var terminal = await s.Db.DatasetBuildEvents.Where(e => e.AttemptId == first.AttemptId).OrderBy(e => e.Sequence).ToListAsync();
        Assert.Equal(new[] { DatasetBuildStatus.Requested, DatasetBuildStatus.Running, DatasetBuildStatus.Succeeded }, terminal.Select(e => e.Status));
    }
    [Fact]
    public async Task Backdated_new_observations_raw_and_quality_cannot_change_frozen_history()
    {
        await using var s = await Create(); var first = await s.Build(); var id = Snapshot(first);
        var imported = await s.Import("Div,Date,HomeTeam,AwayTeam,FTR,MatchId\nFICT,01/01/2026,Amber Comets,Cobalt Owls,H,dataset-late\n");
        Assert.Equal(ImportOutcome.Succeeded, imported.Outcome);
        var newObservation = await s.Db.Observations.SingleAsync(o => o.DataSourceId == s.Source && o.Type == ObservationType.EventDate && s.Db.ProviderIdentities.Any(i => i.Id == o.ProviderIdentityId && i.ExternalId == "provider:dataset-late"));
        Assert.True(newObservation.RecordedAtUtc > s.Definition.AsOfUtc);
        var repeated = await s.Build(); Assert.Equal(first.ManifestHash, repeated.ManifestHash);
        var now = await s.Now(); var updated = s.Definition with { AsOfUtc = now, Targets = [s.Definition.Targets[0] with { PredictionCutoffUtc = now }] };
        var later = await s.Build(updated); Assert.NotEqual(id, Snapshot(later)); Assert.NotEqual(first.ManifestHash, later.ManifestHash);
        var differences = await s.Service().CompareAsync(id, later.SnapshotId!.Value, 0, 20); Assert.True(differences.Total > 0);
        Assert.True((await s.Service().VerifyAsync(id)).ArtifactIntegrity);
    }
    [Fact]
    public async Task Later_identity_review_only_changes_explicit_retrospective_interpretation()
    {
        await using var s = await Create(); var first = await s.Build(); var id = Snapshot(first);
        var homeAnchor = await s.Db.ProviderIdentities.SingleAsync(i => i.DataSourceId == s.Source && i.ExternalId == FootballDataCsvParser.TeamReference("FICT", "Amber Comets"));
        var replacementAnchor = await s.Db.ProviderIdentities.SingleAsync(i => i.DataSourceId == s.Source && i.ExternalId == FootballDataCsvParser.TeamReference("FICT", "Violet Herons"));
        var replacement = await s.Db.IdentityResolutions.SingleAsync(d => d.ProviderIdentityId == replacementAnchor.Id);
        var review = await new IdentityReview(s.Db).DecideAsync(new(homeAnchor.Id, s.Source, ReviewAction.Approve,
            new(CanonicalEntityKind.Participant, replacement.CanonicalParticipantId!.Value), 1, "operator:dataset-review", "Explicit retrospective fictional interpretation"));
        Assert.Equal("accepted", review.Result);
        Assert.Equal(first.ManifestHash, (await s.Build()).ManifestHash);
        var reconstructed = await s.Build(s.Definition with { Mode = DatasetMode.RetrospectiveReconstruction, ReconstructionAtUtc = await s.Now() });
        var retro = await s.Service().InspectAsync(Snapshot(reconstructed));
        Assert.Equal(DatasetMode.RetrospectiveReconstruction, retro.Manifest.Definition.Mode);
        Assert.NotEqual((await s.Service().InspectAsync(id)).Manifest.Rows[0].Target.HomeId, retro.Manifest.Rows[0].Target.HomeId);
        Assert.Contains(review.DecisionId!.Value, retro.Manifest.Rows[0].Target.DecisionIds);
        Assert.True((await s.Service().VerifyAsync(id)).FeaturesReproducible);
    }
    [Fact]
    public async Task Rescheduling_creates_new_snapshot_without_mutating_old_artifact()
    {
        await using var s = await Create(); var first = await s.Build(); var id = Snapshot(first);
        var target = (await s.Service().InspectAsync(id)).Manifest.Rows[0].Target;
        var csv = "Div,Date,HomeTeam,AwayTeam,FTR,MatchId\nFICT," + target.EventDate.AddDays(1).ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture) + ",Amber Comets,Cobalt Owls,,dataset-target\n";
        Assert.Equal(ImportOutcome.Succeeded, (await s.Import(csv)).Outcome);
        var latest = await s.Db.Observations.Where(o => o.ProviderIdentityId == target.ProviderIdentityId && o.Type == ObservationType.EventDate).OrderByDescending(o => o.Version).FirstAsync();
        var now = await s.Now(); var next = await s.Build(s.Definition with { AsOfUtc = now, Targets = [new(latest.Id, now)] });
        var newer = await s.Service().InspectAsync(Snapshot(next));
        Assert.NotEqual(first.ManifestHash, next.ManifestHash); Assert.Equal(target.EventDate.AddDays(1), newer.Manifest.Rows[0].Target.EventDate);
        Assert.Equal(target.EventDate, (await s.Service().InspectAsync(id)).Manifest.Rows[0].Target.EventDate);
    }
    [Theory]
    [InlineData("update")][InlineData("delete")][InlineData("truncate")][InlineData("bulk")]
    public async Task Finalized_artifacts_vectors_and_attempts_reject_sql_and_ef_mutation(string operation)
    {
        await using var s = await Create(); Snapshot(await s.Build());
        foreach (var table in new[] { "Snapshots", "Features", "BuildEvents" })
        {
            await using var db = fixture.CreateContext();
            if (operation == "bulk")
            {
                if (table == "Snapshots") await Assert.ThrowsAsync<PostgresException>(() => db.DatasetArtifacts.ExecuteUpdateAsync(p => p.SetProperty(a => a.RowCount, 1)));
                else if (table == "Features") await Assert.ThrowsAsync<PostgresException>(() => db.DatasetFeatures.ExecuteDeleteAsync());
                else await Assert.ThrowsAsync<PostgresException>(() => db.DatasetBuildEvents.ExecuteDeleteAsync());
            }
            else
            {
                var sql = operation == "update" ? $"UPDATE datasets.\"{table}\" SET \"Id\" = \"Id\"" : operation == "delete" ? $"DELETE FROM datasets.\"{table}\"" : $"TRUNCATE datasets.\"{table}\" CASCADE";
                var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql)); Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            }
        }
        var artifact = await s.Db.DatasetArtifacts.SingleAsync(a => a.DefinitionFingerprint == CanonicalDatasetJson.Fingerprint(s.Definition));
        s.Db.Entry(artifact).Property(a => a.RowCount).CurrentValue = 2;
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Db.SaveChangesAsync());
    }
    [Fact]
    public async Task Concurrent_identical_builds_share_exact_content_and_keep_separate_audit()
    {
        await using var s = await Create(); await using var other = fixture.CreateContext();
        var policies = new SourcePolicyEvaluator(new SourcePolicyHistory(other)); var sources = new SourceOperationalStatusReader(other);
        var service = new PostgreSqlDatasets(other, s.Store, new FootballDataCsvParser(), new AnalyticalQualityGate(other, policies, sources), policies, sources);
        var results = await Task.WhenAll(s.Build(), service.BuildAsync(new(s.Definition, "operator:parallel", "Concurrent frozen build")));
        Assert.All(results, r => Snapshot(r)); Assert.Equal(results[0].SnapshotId, results[1].SnapshotId);
        Assert.Equal(1, await s.Db.DatasetArtifacts.CountAsync(a => a.DefinitionFingerprint == CanonicalDatasetJson.Fingerprint(s.Definition)));
        Assert.NotEqual(results[0].AttemptId, results[1].AttemptId);
    }
    private sealed class CallbackStore(IRawPayloadStore inner, Func<Task> beforeRead) : IRawPayloadStore
    {
        private bool called;
        public async Task<ReadOnlyMemory<byte>> ReadAsync(StoredPayload payload, CancellationToken token = default) { if (!called) { called = true; await beforeRead(); } return await inner.ReadAsync(payload, token); }
        public Task<StoredPayload> StageAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default) => inner.StageAsync(bytes, token);
        public Task FinalizeAsync(StoredPayload payload, CancellationToken token = default) => inner.FinalizeAsync(payload, token);
        public IReadOnlyList<string> InventoryStaged() => inner.InventoryStaged();
    }
    private async Task Revoke(Guid source)
    {
        await using var other = fixture.CreateContext();
        var policy = await other.SourcePolicies.Include(p => p.Audit).SingleAsync(p => p.DataSourceId == source);
        var now = await other.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        policy.Revoke(Guid.NewGuid(), "operator:dataset-revocation", "Revoke fictional use", now); await other.SaveChangesAsync();
    }
    [Fact]
    public async Task Revocation_during_repeatable_read_is_rechecked_before_publication()
    {
        await using var s = await Create();
        var result = await s.Service(new CallbackStore(s.Store, () => Revoke(s.Source))).BuildAsync(new(s.Definition, "operator:revocation-test", "Revoke while assembling"));
        Assert.Equal(DatasetBuildStatus.Failed, result.Status); Assert.Equal("current_policy_denied", result.FailureCode);
        Assert.Null(result.SnapshotId); Assert.False(await s.Db.DatasetArtifacts.AnyAsync(a => a.DefinitionFingerprint == CanonicalDatasetJson.Fingerprint(s.Definition)));
    }
    [Fact]
    public async Task Valid_hash_does_not_grant_current_use_after_revocation()
    {
        await using var s = await Create(); var id = Snapshot(await s.Build()); await Revoke(s.Source);
        var verification = await s.Service().VerifyAsync(id);
        Assert.True(verification.ArtifactIntegrity); Assert.True(verification.FeaturesReproducible); Assert.False(verification.CurrentlyAuthorized); Assert.False(verification.EvidenceComplete);
        await Assert.ThrowsAnyAsync<Exception>(() => s.Service().InspectAsync(id));
    }
    [Fact]
    public async Task Cancellation_and_confirmed_interruption_leave_no_partial_success()
    {
        await using var s = await Create(); using var cancellation = new CancellationTokenSource();
        var result = await s.Service(new CallbackStore(s.Store, () => { cancellation.Cancel(); return Task.CompletedTask; })).BuildAsync(new(s.Definition, "operator:cancel", "Cancel synthetic build"), cancellation.Token);
        Assert.Equal(DatasetBuildStatus.Cancelled, result.Status); Assert.Null(result.SnapshotId);
        var attempt = Guid.NewGuid(); var fingerprint = CanonicalDatasetJson.Fingerprint(s.Definition);
        s.Db.DatasetBuildEvents.Add(new() { Id = Guid.NewGuid(), AttemptId = attempt, Sequence = 1, Status = DatasetBuildStatus.Requested, DefinitionFingerprint = fingerprint, OperatorId = "operator:stopped", Reason = "Fictional interrupted owner" });
        await s.Db.SaveChangesAsync();
        await s.Service().MarkInterruptedAsync(attempt, "operator:recovery", "Owner confirmed stopped");
        await s.Service().MarkInterruptedAsync(attempt, "operator:recovery", "Idempotent closure");
        Assert.Equal(2, await s.Db.DatasetBuildEvents.CountAsync(e => e.AttemptId == attempt));
        Assert.Equal("owner_confirmed_interrupted", (await s.Db.DatasetBuildEvents.SingleAsync(e => e.AttemptId == attempt && e.Sequence == 3)).FailureCode);
    }
    [Fact]
    public async Task Corrupted_artifact_fails_verification_without_altering_original()
    {
        await using var s = await Create(); var id = Snapshot(await s.Build()); var original = await s.Db.DatasetArtifacts.SingleAsync(a => a.Id == id);
        var manifest = CanonicalDatasetJson.Deserialize<DatasetManifest>(original.Content);
        var corrupt = new DatasetArtifact { Id = Guid.NewGuid(), DefinitionFingerprint = original.DefinitionFingerprint, ManifestHash = new string('f', 64),
            Content = CanonicalDatasetJson.Serialize(manifest with { SerializerVersion = 999 }), RowCount = original.RowCount, FeatureSchemaVersion = 1, BuiltAtUtc = await s.Now() };
        s.Db.DatasetArtifacts.Add(corrupt); await s.Db.SaveChangesAsync();
        var verification = await s.Service().VerifyAsync(corrupt.Id); Assert.False(verification.ArtifactIntegrity); Assert.False(verification.FeaturesReproducible);
        Assert.True((await s.Service().VerifyAsync(id)).ArtifactIntegrity);
    }
    [Fact]
    public async Task Tampered_raw_prevents_a_successful_build_and_records_failure()
    {
        await using var s = await Create(); var raw = await s.Db.RawPayloads.SingleAsync(r => r.DataSourceId == s.Source);
        await File.WriteAllTextAsync(Path.Combine(s.Root, raw.StorageKey + ".raw"), "fictional corruption");
        var result = await s.Build(); Assert.Equal(DatasetBuildStatus.Failed, result.Status); Assert.Equal("raw_integrity_or_storage", result.FailureCode);
        Assert.Null(result.SnapshotId);
    }
    [Theory]
    [InlineData("future")][InlineData("target-day")][InlineData("missing")]
    public async Task Unsupported_target_cutoffs_or_missing_evidence_fail_with_terminal_audit(string problem)
    {
        await using var s = await Create(); var d = s.Definition;
        d = problem switch { "future" => d with { AsOfUtc = d.AsOfUtc.AddHours(1) },
            "target-day" => d with { AsOfUtc = d.AsOfUtc.AddDays(3), Targets = [new(d.Targets[0].DateObservationId, d.AsOfUtc.AddDays(3))] },
            _ => d with { Targets = [new(Guid.NewGuid(), d.Targets[0].PredictionCutoffUtc)] } };
        var result = await s.Build(d); Assert.Equal(DatasetBuildStatus.Failed, result.Status); Assert.Null(result.SnapshotId);
        Assert.True(await s.Db.DatasetBuildEvents.AnyAsync(e => e.AttemptId == result.AttemptId && e.Sequence == 3));
    }
}
