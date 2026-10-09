using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BetStats.IntegrationTests;

public sealed class PersistenceTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTime CapturedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly Guid SourceId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid RunId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid PayloadId = Guid.Parse("00000000-0000-0000-0000-000000000003");

    [Fact]
    public async Task Fresh_database_is_migrated_and_has_no_pending_model_changes()
    {
        await using var context = fixture.CreateContext();
        Assert.Equal(new[] { "20261007234734_InitialPersistence", "20261008001440_CanonicalSportsAndTemporalObservations", "20261008005300_SourceGovernanceAndTrustedHistory", "20261008014627_AuditRemediation", "20261008083514_FirstFootballIngestion", "20261008094144_DataQualityIdentityReview", "20261008123611_DatasetSnapshotsFeatures", "20261008134149_CoverageEventTimeEvaluation", "20261008150019_HistoricalIntegrityContext", "20261008171605_FootballResultsOutcomeProvenance", "20261008221019_ResultCoverageOperationsEventEnd", "20261008231727_HistoricalBacktesting", "20261009102627_RealFootballImportOperations" },
            await context.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
        // Reapplying migrations must be a no-op, not recreate existing tables.
        await context.Database.MigrateAsync();
    }

    [Fact]
    public async Task Expected_tables_constraints_and_indexes_exist()
    {
        await using var context = fixture.CreateContext();
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = 'ingestion' ORDER BY table_name";
        var tables = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }
        Assert.Equal(new[] { "DataSources", "FootballImportOperations", "FootballRawContexts", "IngestionAuditEvents", "IngestionPublications", "IngestionRuns", "RawPayloads" }, tables);

        command.CommandText = """
            SELECT c.contype::text, c.confdeltype::text
            FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace
            WHERE n.nspname = 'ingestion'
            """;
        var constraints = new List<(string Kind, string DeleteAction)>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) constraints.Add((reader.GetString(0), reader.GetString(1)));
        }
        Assert.Equal(7, constraints.Count(constraint => constraint.Kind == "p"));
        Assert.Equal(10, constraints.Count(constraint => constraint.Kind == "f"));
        Assert.All(constraints.Where(constraint => constraint.Kind == "f"), constraint => Assert.Equal("r", constraint.DeleteAction));
        Assert.True(constraints.Count(constraint => constraint.Kind == "c") >= 7);
        Assert.Contains(constraints, constraint => constraint.Kind == "u");

        command.CommandText = "SELECT indexname FROM pg_indexes WHERE schemaname = 'ingestion'";
        var indexes = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) indexes.Add(reader.GetString(0));
        }
        Assert.Contains("IX_DataSources_Code", indexes);
        Assert.Contains("IX_IngestionRuns_DataSourceId_CreatedAtUtc", indexes);
        Assert.Contains("IX_RawPayloads_DataSourceId_RetrievedAtUtc", indexes);
        Assert.Contains("IX_RawPayloads_ContentHashSha256", indexes);
    }

    [Fact]
    public Task Data_source_round_trips() => InTransaction(async context =>
    {
        context.DataSources.Add(Source());
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var source = await context.DataSources.SingleAsync();
        Assert.Equal(SourceId, source.Id);
        Assert.Equal("synthetic-fixture", source.Code);
        Assert.Equal("Synthetic fixture", source.DisplayName);
        Assert.True(source.IsEnabled);
        AssertUtc(CapturedAt, source.CreatedAtUtc);
    });

    [Fact]
    public Task Duplicate_source_code_is_rejected() => InTransaction(async context =>
    {
        context.DataSources.Add(Source());
        await context.SaveChangesAsync();
        context.DataSources.Add(Source(Guid.Parse("00000000-0000-0000-0000-000000000004")));
        await AssertDatabaseError(PostgresErrorCodes.UniqueViolation, () => context.SaveChangesAsync());
    });

    [Theory]
    [InlineData("code")]
    [InlineData("name")]
    public Task Blank_source_fields_are_rejected(string field) => InTransaction(async context =>
    {
        var source = Source();
        if (field == "code") source.Code = " ";
        else source.DisplayName = " ";
        context.DataSources.Add(source);
        await AssertDatabaseError(PostgresErrorCodes.CheckViolation, () => context.SaveChangesAsync());
    });

    [Theory]
    [InlineData(IngestionRunStatus.Pending)]
    [InlineData(IngestionRunStatus.Running)]
    [InlineData(IngestionRunStatus.Succeeded)]
    [InlineData(IngestionRunStatus.Failed)]
    public Task Run_status_and_utc_times_round_trip(IngestionRunStatus status) => InTransaction(async context =>
    {
        context.DataSources.Add(Source());
        var run = Run();
        run.Status = status;
        run.StartedAtUtc = status == IngestionRunStatus.Pending ? null : CapturedAt.AddSeconds(1);
        run.CompletedAtUtc = status is IngestionRunStatus.Succeeded or IngestionRunStatus.Failed ? CapturedAt.AddSeconds(2) : null;
        run.ErrorCode = status == IngestionRunStatus.Failed ? "SYNTHETIC_ERROR" : null;
        context.IngestionRuns.Add(run);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var stored = await context.IngestionRuns.SingleAsync();
        Assert.Equal(SourceId, stored.DataSourceId);
        Assert.Equal(status, stored.Status);
        Assert.Equal(run.ErrorCode, stored.ErrorCode);
        AssertUtc(CapturedAt, stored.CreatedAtUtc);
        if (run.StartedAtUtc is { } start) AssertUtc(start, stored.StartedAtUtc!.Value);
        else Assert.Null(stored.StartedAtUtc);
        if (run.CompletedAtUtc is { } end) AssertUtc(end, stored.CompletedAtUtc!.Value);
        else Assert.Null(stored.CompletedAtUtc);
    });

    [Fact]
    public Task Run_requires_existing_source() => InTransaction(async context =>
    {
        context.IngestionRuns.Add(Run());
        await AssertDatabaseError(PostgresErrorCodes.ForeignKeyViolation, () => context.SaveChangesAsync());
    });

    [Fact]
    public Task Unknown_run_status_is_rejected() => InTransaction(async context =>
    {
        context.DataSources.Add(Source());
        var run = Run();
        run.Status = (IngestionRunStatus)99;
        context.IngestionRuns.Add(run);
        await AssertDatabaseError(PostgresErrorCodes.CheckViolation, () => context.SaveChangesAsync());
    });

    [Fact]
    public Task Run_cannot_complete_before_start() => InTransaction(async context =>
    {
        context.DataSources.Add(Source());
        var run = Run();
        run.StartedAtUtc = CapturedAt.AddSeconds(2);
        run.CompletedAtUtc = CapturedAt.AddSeconds(1);
        context.IngestionRuns.Add(run);
        await AssertDatabaseError(PostgresErrorCodes.CheckViolation, () => context.SaveChangesAsync());
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Raw_metadata_round_trips_with_optional_run(bool withRun) => InTransaction(async context =>
    {
        context.DataSources.Add(Source());
        if (withRun) context.IngestionRuns.Add(Run());
        var payload = Payload(withRun ? RunId : null);
        context.RawPayloads.Add(payload);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var stored = await context.RawPayloads.SingleAsync();
        Assert.Equal(PayloadId, stored.Id);
        Assert.Equal(SourceId, stored.DataSourceId);
        Assert.Equal(payload.IngestionRunId, stored.IngestionRunId);
        Assert.Equal(payload.ExternalReference, stored.ExternalReference);
        Assert.Equal(payload.ContentHashSha256, stored.ContentHashSha256);
        Assert.Equal("application/json", stored.ContentType);
        Assert.Equal("synthetic/capture.json", stored.StorageKey);
        AssertUtc(payload.RetrievedAtUtc, stored.RetrievedAtUtc);
        AssertUtc(payload.CreatedAtUtc, stored.CreatedAtUtc);
    });

    [Theory]
    [InlineData("source")]
    [InlineData("run")]
    [InlineData("source-mismatch")]
    public Task Raw_metadata_rejects_invalid_relationships(string relationship) => InTransaction(async context =>
    {
        if (relationship != "source") context.DataSources.Add(Source());
        if (relationship == "source-mismatch")
        {
            var otherId = Guid.Parse("00000000-0000-0000-0000-000000000004");
            var other = Source(otherId);
            other.Code = "other-synthetic-fixture";
            context.DataSources.Add(other);
            context.IngestionRuns.Add(new IngestionRun { Id = RunId, DataSourceId = otherId, CreatedAtUtc = CapturedAt });
        }
        context.RawPayloads.Add(Payload(relationship == "source" ? null : RunId));
        await AssertDatabaseError(PostgresErrorCodes.ForeignKeyViolation, () => context.SaveChangesAsync());
    });

    [Theory]
    [InlineData("hash")]
    [InlineData("content-type")]
    [InlineData("storage-key")]
    public Task Raw_metadata_rejects_invalid_required_fields(string field) => InTransaction(async context =>
    {
        context.DataSources.Add(Source());
        context.RawPayloads.Add(new RawPayload
        {
            Id = PayloadId, DataSourceId = SourceId, RetrievedAtUtc = CapturedAt, CreatedAtUtc = CapturedAt,
            ContentHashSha256 = field == "hash" ? "invalid" : new string('a', 64),
            ContentType = field == "content-type" ? " " : "application/json",
            StorageKey = field == "storage-key" ? " " : "synthetic/capture.json"
        });
        await AssertDatabaseError(PostgresErrorCodes.CheckViolation, () => context.SaveChangesAsync());
    });

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public Task Non_utc_timestamps_are_rejected(DateTimeKind kind) => InTransaction(async context =>
    {
        context.DataSources.Add(new DataSource
        {
            Id = SourceId, Code = "synthetic-fixture", DisplayName = "Synthetic fixture",
            CreatedAtUtc = DateTime.SpecifyKind(CapturedAt, kind)
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains("must be UTC", error.Message);
    });

    [Theory]
    [InlineData("source-with-run")]
    [InlineData("source-with-raw")]
    [InlineData("run-with-raw")]
    public Task Historical_records_prevent_cascade_deletion(string relationship) => InTransaction(async context =>
    {
        var source = Source();
        var run = Run();
        context.DataSources.Add(source);
        if (relationship != "source-with-raw") context.IngestionRuns.Add(run);
        if (relationship != "source-with-run") context.RawPayloads.Add(Payload(relationship == "run-with-raw" ? RunId : null));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        // Read/delete principals without tracked dependents so the DB foreign
        // keys, rather than EF relationship fixup, enforce retention safety.
        if (relationship == "run-with-raw") context.IngestionRuns.Remove(await context.IngestionRuns.SingleAsync());
        else context.DataSources.Remove(await context.DataSources.SingleAsync());
        await AssertDatabaseError(PostgresErrorCodes.ForeignKeyViolation, () => context.SaveChangesAsync());
        context.ChangeTracker.Clear();
        Assert.Equal(1, await context.DataSources.CountAsync());
        if (relationship != "source-with-raw") Assert.Equal(1, await context.IngestionRuns.CountAsync());
        if (relationship != "source-with-run") Assert.Equal(1, await context.RawPayloads.CountAsync());
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Raw_metadata_is_append_only(bool delete) => InTransaction(async context =>
    {
        context.DataSources.Add(Source());
        var payload = Payload(null);
        context.RawPayloads.Add(payload);
        await context.SaveChangesAsync();
        if (delete) context.RawPayloads.Remove(payload);
        else context.Entry(payload).Property(record => record.StorageKey).CurrentValue = "synthetic/changed.json";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains("append-only", error.Message);
        context.ChangeTracker.Clear();
        Assert.Equal("synthetic/capture.json", (await context.RawPayloads.SingleAsync()).StorageKey);
    });

    [Fact]
    public Task Repeated_content_capture_preserves_each_retrieval() => InTransaction(async context =>
    {
        context.DataSources.Add(Source());
        context.RawPayloads.Add(Payload(null));
        context.RawPayloads.Add(new RawPayload
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000004"), DataSourceId = SourceId,
            RetrievedAtUtc = CapturedAt.AddSeconds(1), CreatedAtUtc = CapturedAt.AddSeconds(1),
            ContentHashSha256 = new string('a', 64), ContentType = "application/json", StorageKey = "synthetic/capture-again.json"
        });
        await context.SaveChangesAsync();
        Assert.Equal(2, await context.RawPayloads.CountAsync());
    });

    private async Task InTransaction(Func<BetStatsDbContext, Task> test)
    {
        await using var context = fixture.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await test(context);
        // Disposal rolls back all test data, including when an assertion fails.
    }

    private static DataSource Source(Guid? id = null) => new()
    {
        Id = id ?? SourceId, Code = "synthetic-fixture", DisplayName = "Synthetic fixture",
        IsEnabled = true, CreatedAtUtc = CapturedAt
    };

    private static IngestionRun Run() => new() { Id = RunId, DataSourceId = SourceId, CreatedAtUtc = CapturedAt };

    private static RawPayload Payload(Guid? runId) => new()
    {
        Id = PayloadId, DataSourceId = SourceId, IngestionRunId = runId,
        ExternalReference = "synthetic-reference", RetrievedAtUtc = CapturedAt.AddTicks(1234560),
        ContentHashSha256 = new string('a', 64), ContentType = "application/json",
        StorageKey = "synthetic/capture.json", CreatedAtUtc = CapturedAt.AddSeconds(1)
    };

    private static void AssertUtc(DateTime expected, DateTime actual)
    {
        Assert.Equal(expected, actual);
        Assert.Equal(DateTimeKind.Utc, actual.Kind);
    }

    private static async Task AssertDatabaseError(string state, Func<Task> save)
    {
        var error = await Assert.ThrowsAsync<DbUpdateException>(save);
        Assert.Equal(state, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }
}
