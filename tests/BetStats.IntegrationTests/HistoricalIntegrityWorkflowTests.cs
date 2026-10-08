using BetStats.Application.Coverage;
using BetStats.Application.Datasets;
using BetStats.Application.Governance;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Domain.Coverage;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Domain.Quality;
using BetStats.Domain.Sports;
using BetStats.Infrastructure.Coverage;
using BetStats.Infrastructure.Datasets;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;

namespace BetStats.IntegrationTests;

public sealed class HistoricalIntegrityWorkflowTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed class Scenario : IAsyncDisposable
    {
        public BetStatsDbContext Db { get; }
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "betstats-logic-audit-" + Guid.NewGuid().ToString("N"));
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
        public async Task<DatasetDefinition> Current() { var now = await Now(); return Definition with { AsOfUtc = now, Targets = [Definition.Targets[0] with { PredictionCutoffUtc = now }] }; }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private async Task<Scenario> Create()
    {
        var s = new Scenario(fixture.CreateContext()); var now = await s.Now();
        (s.Source, s.Definition) = await SyntheticDatasetDemo.PrepareAsync(s.Db, s.Ingestion, new(new(100, 1000, 1, TimeSpan.FromSeconds(30)), TimeProvider.System), TimeProvider.System,
            true, "audit-fiction-" + Guid.NewGuid().ToString("N"), DateOnly.FromDateTime(now).AddDays(2)); return s;
    }
    [Fact]
    public async Task Prevents_dataset_relabeling_existing_events_into_an_unrelated_season()
    {
        await using var s = await Create(); var now = await s.Now(); var season = new Season(Guid.NewGuid(), s.Definition.CompetitionId, "Other fictional season");
        var alias = new ProviderIdentity(Guid.NewGuid(), s.Source, CanonicalEntityKind.Season, FootballDataCsvParser.SeasonReference("FICT", "2026-other"), now);
        s.Db.AddRange(season, alias, new IdentityResolution(Guid.NewGuid(), alias, ResolutionStatus.Resolved, new(CanonicalEntityKind.Season, season.Id), "audit:operator", "Fictional separate season", now)); await s.Db.SaveChangesAsync();
        var d = (await s.Current()) with { SeasonId = season.Id, SeasonReference = "2026-other", Version = 2, FeatureSchemaVersion = 2 };
        var result = await s.Datasets.BuildAsync(new(d, "audit:operator", "Test altered scope")); Assert.Equal(DatasetBuildStatus.Failed, result.Status);
    }
    [Fact]
    public async Task Accepts_consistent_overlap_for_two_independently_approved_overlapping_claims()
    {
        await using var s = await Create();
        async Task<CoverageScope> Approve(int start, int end)
        {
            var scope = new CoverageScope(s.Source, s.Definition.SportId, s.Definition.CompetitionId, s.Definition.SeasonId, null, "football-match", ObservationType.EventDate,
                "FICT", "2026-fiction", new(IntervalKind.Calendar, null, null, new(2026, 1, start), new(2026, 1, end), "UTC-calendar"));
            var ids = await s.Db.Observations.Where(o => o.DataSourceId == s.Source && o.Type == ObservationType.EventDate && o.DateValue >= scope.Interval.StartDate && o.DateValue < scope.Interval.EndDate).OrderBy(o => o.Id).Select(o => o.Id).ToArrayAsync();
            var bytes = CanonicalDatasetJson.Serialize(new CoverageInventory("project-owned-fixture-inventory-v1", scope, CoverageStatus.VerifiedComplete, ids));
            var stored = await s.Store.StageAsync(bytes); await s.Store.FinalizeAsync(stored); var now = await s.Now();
            var raw = new RawPayload { Id = Guid.NewGuid(), DataSourceId = s.Source, RetrievedAtUtc = now, CreatedAtUtc = now, StorageKey = stored.StorageKey, ContentType = "application/json", ContentHashSha256 = stored.Hash, ByteLength = stored.Length }; s.Db.Add(raw); await s.Db.SaveChangesAsync();
            var evidence = await s.Coverage.RecordAsync(new(scope, CoverageStatus.VerifiedComplete, CoverageBasis.OwnedFixtureInventory, "audit:owned-inventory", raw.Id, 1, null, now, now.AddDays(30), "audit:operator", "Exact fictional scope"));
            var review = await s.Coverage.ReviewAsync(new(evidence.Id, 0, CoverageReviewStatus.Approved, "project-owned-fixture-inventory-v1", "audit:review", "Verify known complete fixture"));
            Assert.Equal(CoverageReviewStatus.Approved, review.Status); return scope;
        }
        var a = await Approve(1, 6); await Approve(4, 7);
        var report = await s.Coverage.ReportAsync(new(a, await s.Now(), DatasetMode.HistoricalAsKnown, null, DataPurpose.InternalAnalytics, new()));
        Assert.All(report.Items, i => Assert.Equal(CoverageStatus.VerifiedComplete, i.Status)); Assert.Equal(CoverageStatus.VerifiedComplete, report.Status); Assert.Empty(report.ConflictingIntervals);
    }
    [Fact]
    public async Task Reports_raw_missing_separately_from_frozen_metadata()
    {
        await using var s = await Create(); var result = await s.Datasets.BuildAsync(new(s.Definition, "audit:operator", "Freeze fixture")); Assert.Equal(DatasetBuildStatus.Succeeded, result.Status);
        foreach (var path in Directory.GetFiles(s.Root, "*.raw")) File.Delete(path);
        var verification = await s.Datasets.VerifyDeepAsync(result.SnapshotId!.Value);
        Assert.True(verification.ArtifactIntegrity); Assert.True(verification.EvidenceComplete); Assert.True(verification.FeaturesReproducible); Assert.False(verification.RawAvailable); Assert.False(verification.RawHashVerified);
    }
    [Fact]
    public async Task Prevents_arbitrary_event_time_raw_not_bound_to_an_event()
    {
        await using var s = await Create(); var o = await s.Db.Observations.SingleAsync(o => o.Id == s.Definition.Targets[0].DateObservationId);
        var time = new EventTimeValue(o.DateValue, new(12, 0), "Europe/Warsaw", null, null, EventTimePrecision.Minute);
        // This RAW contains a clock claim only: no event/source identity or match reference.
        var stored = await s.Store.StageAsync(CanonicalDatasetJson.Serialize(time)); await s.Store.FinalizeAsync(stored); var now = await s.Now();
        var raw = new RawPayload
        {
            Id = Guid.NewGuid(),
            DataSourceId = s.Source,
            RetrievedAtUtc = now,
            CreatedAtUtc = now,
            StorageKey = stored.StorageKey,
            ContentType = "application/json",
            ContentHashSha256 = stored.Hash,
            ByteLength = stored.Length
        }; s.Db.Add(raw); await s.Db.SaveChangesAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => s.Coverage.RecordTimeAsync(new(o.Id, raw.Id, time, "unbound-manual-time", null, null, now, "audit:operator", "Source has no match identity")));
        Assert.Empty(await s.Coverage.TimesAsync(o.ProviderIdentityId, await s.Now(), DatasetMode.HistoricalAsKnown, null, DataPurpose.InternalAnalytics, new()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Standard_and_deep_verification_have_independent_guarantees(int version)
    {
        await using var s = await Create();
        var definition = (await s.Current()) with { Version = version, FeatureSchemaVersion = version };
        var built = await s.Datasets.BuildAsync(new(definition, "audit:operator", "Verify both schemas"));
        Assert.Equal(DatasetBuildStatus.Succeeded, built.Status);
        var id = built.SnapshotId!.Value;
        var before = (await s.Db.DatasetArtifacts.AsNoTracking().SingleAsync(a => a.Id == id)).Content.ToArray();
        var standard = await s.Datasets.VerifyAsync(id);
        Assert.True(standard.FrozenMetadataComplete); Assert.Null(standard.RawAvailable); Assert.Null(standard.RawHashVerified);
        var deep = await s.Datasets.VerifyDeepAsync(id);
        Assert.True(deep.RawAvailable); Assert.True(deep.RawHashVerified); Assert.True(deep.ArtifactIntegrity); Assert.Equal(2, deep.ContractVersion);
        Assert.Equal(before, (await s.Db.DatasetArtifacts.AsNoTracking().SingleAsync(a => a.Id == id)).Content);
        var again = await s.Datasets.BuildAsync(new(definition, "audit:operator", "Identical request"));
        Assert.Equal(built.ManifestHash, again.ManifestHash);
    }
    [Fact]
    public async Task Corrupt_raw_fails_deep_hash_verification_without_invalidating_artifact()
    {
        await using var s = await Create();
        var built = await s.Datasets.BuildAsync(new(s.Definition, "audit:operator", "Freeze fixture")); Assert.Equal(DatasetBuildStatus.Succeeded, built.Status);
        var raw = await s.Db.RawPayloads.FirstAsync(r => r.DataSourceId == s.Source);
        await File.WriteAllTextAsync(Path.Combine(s.Root, raw.StorageKey + ".raw"), "corrupt fictional fixture");
        var result = await s.Datasets.VerifyDeepAsync(built.SnapshotId!.Value);
        Assert.True(result.ArtifactIntegrity); Assert.True(result.FrozenMetadataComplete); Assert.True(result.FeaturesReproducible);
        Assert.True(result.RawAvailable); Assert.False(result.RawHashVerified); Assert.Contains("raw_corrupt", result.Reasons);
    }
    private sealed class NoReadStore(IRawPayloadStore inner) : IRawPayloadStore
    {
        public int Reads { get; private set; }
        public Task<StoredPayload> StageAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default) => inner.StageAsync(bytes, token);
        public Task FinalizeAsync(StoredPayload payload, CancellationToken token = default) => inner.FinalizeAsync(payload, token);
        public Task<ReadOnlyMemory<byte>> ReadAsync(StoredPayload payload, CancellationToken token = default) { Reads++; throw new InvalidOperationException("Unauthorized RAW read"); }
        public IReadOnlyList<string> InventoryStaged() => inner.InventoryStaged();
    }
    [Fact]
    public async Task Revoked_deep_verification_never_reads_raw()
    {
        await using var s = await Create(); var built = await s.Datasets.BuildAsync(new(s.Definition, "audit:operator", "Freeze fixture"));
        Assert.Equal(DatasetBuildStatus.Succeeded, built.Status);
        var policy = await s.Db.SourcePolicies.Include(p => p.Audit).SingleAsync(p => p.DataSourceId == s.Source);
        policy.Revoke(Guid.NewGuid(), "audit:review", "Revoke fictional use", await s.Now()); await s.Db.SaveChangesAsync();
        var store = new NoReadStore(s.Store);
        var result = await new PostgreSqlDatasets(s.Db, store, new FootballDataCsvParser(), s.Gate, s.Policies, s.Sources, s.Coverage).VerifyDeepAsync(built.SnapshotId!.Value);
        Assert.False(result.CurrentUseAuthorized); Assert.True(result.ArtifactIntegrity); Assert.Null(result.RawAvailable); Assert.Null(result.RawHashVerified); Assert.Equal(0, store.Reads);
    }
    [Fact]
    public async Task Frozen_scope_inconsistency_is_detected_without_changing_original_artifact()
    {
        await using var s = await Create(); var built = await s.Datasets.BuildAsync(new(s.Definition, "audit:operator", "Freeze fixture"));
        Assert.Equal(DatasetBuildStatus.Succeeded, built.Status);
        var original = await s.Db.DatasetArtifacts.AsNoTracking().SingleAsync(a => a.Id == built.SnapshotId);
        var manifest = CanonicalDatasetJson.Deserialize<DatasetManifest>(original.Content);
        var definition = manifest.Definition with { SeasonId = Guid.NewGuid(), SeasonReference = "2026-unrelated" };
        var bytes = CanonicalDatasetJson.Serialize(manifest with { Definition = definition, DefinitionFingerprint = CanonicalDatasetJson.Fingerprint(definition) });
        var altered = new DatasetArtifact
        {
            Id = Guid.NewGuid(),
            Content = bytes,
            ManifestHash = CanonicalDatasetJson.Hash(bytes),
            DefinitionFingerprint = CanonicalDatasetJson.Fingerprint(definition),
            RowCount = original.RowCount,
            FeatureSchemaVersion = original.FeatureSchemaVersion,
            BuiltAtUtc = await s.Now()
        };
        s.Db.DatasetArtifacts.Add(altered);
        foreach (var feature in await s.Db.DatasetFeatures.AsNoTracking().Where(f => f.DatasetId == original.Id).ToListAsync())
            s.Db.DatasetFeatures.Add(new() { Id = Guid.NewGuid(), DatasetId = altered.Id, EventId = feature.EventId, PredictionCutoffUtc = feature.PredictionCutoffUtc, Fingerprint = feature.Fingerprint, Content = feature.Content });
        await s.Db.SaveChangesAsync();
        var result = await s.Datasets.VerifyAsync(altered.Id); Assert.True(result.ArtifactIntegrity); Assert.False(result.FrozenMetadataComplete);
        Assert.True((await s.Datasets.VerifyAsync(original.Id)).FrozenMetadataComplete);
    }
    [Theory]
    [InlineData("UPDATE")]
    [InlineData("DELETE")]
    [InlineData("TRUNCATE")]
    public async Task Original_context_is_append_only_under_sql(string operation)
    {
        await using var s = await Create();
        var sql = operation == "UPDATE" ? "UPDATE ingestion.\"FootballRawContexts\" SET \"Version\"=\"Version\"" : operation == "DELETE" ? "DELETE FROM ingestion.\"FootballRawContexts\"" : "TRUNCATE ingestion.\"FootballRawContexts\"";
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => s.Db.Database.ExecuteSqlRawAsync(sql));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Precise_time_rejects_wrong_reference_or_ambiguous_mapping(bool ambiguous)
    {
        await using var s = await Create(); var o = await s.Db.Observations.SingleAsync(o => o.Id == s.Definition.Targets[0].DateObservationId);
        var identity = await s.Db.ProviderIdentities.SingleAsync(i => i.Id == o.ProviderIdentityId);
        var value = new EventTimeValue(o.DateValue, new(12, 0), "Europe/Warsaw", null, null, EventTimePrecision.Minute);
        if (ambiguous)
        {
            var previous = await s.Db.IdentityResolutions.Where(d => d.ProviderIdentityId == identity.Id).OrderByDescending(d => d.Version).FirstAsync();
            s.Db.IdentityResolutions.Add(new(Guid.NewGuid(), identity, ResolutionStatus.Ambiguous, null, "audit:review", "Ambiguous fictional event", await s.Now(), previous)); await s.Db.SaveChangesAsync();
        }
        var claim = new EventTimeSourceClaim(1, o.RawPayloadId!.Value, ambiguous ? identity.ExternalId : "provider:unrelated-match", new(s.Definition.CompetitionReference, s.Definition.SeasonReference), value);
        var stored = await s.Store.StageAsync(CanonicalDatasetJson.Serialize(claim)); await s.Store.FinalizeAsync(stored); var now = await s.Now();
        var raw = new RawPayload { Id = Guid.NewGuid(), DataSourceId = s.Source, RetrievedAtUtc = now, CreatedAtUtc = now, ContentHashSha256 = stored.Hash, StorageKey = stored.StorageKey, ByteLength = stored.Length, ContentType = "application/json" };
        s.Db.RawPayloads.Add(raw); await s.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => s.Coverage.RecordTimeAsync(new(o.Id, raw.Id, value, "audit:bound-time", null, null, now, "audit:operator", "Must reject invalid binding")));
        Assert.Empty(await s.Db.EventTimeEvidence.Where(e => e.SourceId == s.Source).ToListAsync());
    }
    [Fact]
    public async Task Legacy_unbound_time_claim_is_an_explicit_unverified_operator_assertion()
    {
        await using var s = await Create(); var o = await s.Db.Observations.SingleAsync(o => o.Id == s.Definition.Targets[0].DateObservationId);
        var value = new EventTimeValue(o.DateValue, new(12, 0), "Europe/Warsaw", null, null, EventTimePrecision.Minute);
        var stored = await s.Store.StageAsync(CanonicalDatasetJson.Serialize(value)); await s.Store.FinalizeAsync(stored); var now = await s.Now();
        var raw = new RawPayload { Id = Guid.NewGuid(), DataSourceId = s.Source, RetrievedAtUtc = now, CreatedAtUtc = now, ContentHashSha256 = stored.Hash, StorageKey = stored.StorageKey, ByteLength = stored.Length, ContentType = "application/json" };
        s.Db.RawPayloads.Add(raw); await s.Db.SaveChangesAsync();
        s.Db.EventTimeEvidence.Add(new()
        {
            Id = Guid.NewGuid(),
            SourceId = s.Source,
            ProviderIdentityId = o.ProviderIdentityId,
            DateObservationId = o.Id,
            RawId = raw.Id,
            RawHash = raw.ContentHashSha256,
            PolicyId = (await s.Db.SourcePolicies.SingleAsync(p => p.DataSourceId == s.Source)).Id,
            Value = value,
            EvidenceReference = "legacy-operator-assertion",
            Version = 1,
            RetrievedAtUtc = now,
            AvailableAtUtc = now,
            OperatorId = "audit:legacy",
            Reason = "Simulate old stored assertion"
        }); await s.Db.SaveChangesAsync();
        var result = Assert.Single(await s.Coverage.TimesAsync(o.ProviderIdentityId, await s.Now(), DatasetMode.HistoricalAsKnown, null, DataPurpose.InternalAnalytics, new()));
        Assert.Null(result.Resolution.UtcInstant); Assert.Equal("unverified_operator_time_assertion", result.Resolution.Reason);
    }
    [Fact]
    public async Task Unknown_legacy_context_fails_closed_without_creating_a_binding()
    {
        await using var s = await Create(); var observation = await s.Db.Observations.SingleAsync(o => o.Id == s.Definition.Targets[0].DateObservationId);
        // An independently captured unscoped CSV is not proof of the original file season.
        var old = await s.Db.RawPayloads.SingleAsync(r => r.Id == observation.RawPayloadId);
        var now = await s.Now(); var raw = new RawPayload
        {
            Id = Guid.NewGuid(),
            DataSourceId = old.DataSourceId,
            RetrievedAtUtc = old.RetrievedAtUtc,
            CreatedAtUtc = now,
            StorageKey = old.StorageKey,
            ByteLength = old.ByteLength,
            ContentHashSha256 = old.ContentHashSha256,
            ContentType = old.ContentType
        };
        s.Db.RawPayloads.Add(raw); await s.Db.SaveChangesAsync();
        var report = await new DataReconciliation(s.Db, s.Store, new FootballDataCsvParser(), new FootballIngestionPersistence(s.Db, s.Policies, s.Sources), s.Policies, s.Sources)
            .RunAsync([new(raw.Id, new(s.Definition.CompetitionReference, s.Definition.SeasonReference))], "audit:operator", "Unknown original file scope");
        Assert.NotEqual("Completed", report.Result);
        Assert.False(await s.Db.FootballRawContexts.AnyAsync(c => c.RawId == raw.Id));
    }
}
