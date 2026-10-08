using BetStats.Application.Governance;
using BetStats.Application.Observations;
using BetStats.Application.Providers;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Domain.Sports;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BetStats.IntegrationTests;

public sealed class AuditRemediationPersistenceTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTime Time = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DataSource Source(bool enabled = true) => new() { Id = Guid.NewGuid(), Code = "synthetic-" + Guid.NewGuid().ToString("N"), DisplayName = "Synthetic", IsEnabled = enabled, CreatedAtUtc = Time };
    private static ProviderIdentity Identity(DataSource source) => new(Guid.NewGuid(), source.Id, CanonicalEntityKind.Participant, "synthetic", Time);
    private static RawPayload Raw(DataSource source) => new() { Id = Guid.NewGuid(), DataSourceId = source.Id, RetrievedAtUtc = Time, CreatedAtUtc = Time.AddHours(1), ContentHashSha256 = new string('a', 64), ContentType = "application/json", StorageKey = "synthetic/raw" };
    private static Task<DateTime> DatabaseNow(BetStatsDbContext context) => context.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
    private async Task InTransaction(Func<BetStatsDbContext, Task> test)
    {
        await using var context = fixture.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await test(context); await transaction.RollbackAsync();
    }

    [Fact]
    public Task Late_backdated_insert_does_not_change_historical_query_or_pages() => InTransaction(async context =>
    {
        var source = Source(); var identity = Identity(source);
        context.AddRange(source, identity); await context.SaveChangesAsync();
        var cutoff = await DatabaseNow(context);
        var history = new ObservationHistory(context);
        var query = new ObservationQuery(CanonicalEntityKind.Participant, cutoff);
        Assert.Empty(await history.ReadAsOfAsync(query));
        var observation = new Observation(Guid.NewGuid(), identity, null, ObservationType.DisplayName, Time, Time, Time, textValue: "Backdated");
        context.Add(observation); context.Entry(observation).Property(o => o.RecordedAtUtc).CurrentValue = Time;
        await context.SaveChangesAsync();
        Assert.True(observation.RecordedAtUtc > cutoff); Assert.Equal(DateTimeKind.Utc, observation.RecordedAtUtc.Kind);
        Assert.Equal(0, observation.RecordedAtUtc.Ticks % 10);
        Assert.Empty(await history.ReadAsOfAsync(query));
        Assert.Empty((await history.ReadPageAsOfAsync(query, 1)).Items);
        Assert.Single(await history.ReadAsOfAsync(new(CanonicalEntityKind.Participant, observation.RecordedAtUtc)));
        var sqlId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO provenance.\"Observations\" (\"Id\", \"ProviderIdentityId\", \"DataSourceId\", \"EntityKind\", \"Type\", \"TextValue\", \"Version\", \"RetrievedAtUtc\", \"AvailableAtUtc\", \"CreatedAtUtc\", \"RecordedAtUtc\") VALUES ({sqlId}, {identity.Id}, {source.Id}, 'Participant', 'DisplayName', 'SQL backdate', 1, {Time}, {Time}, {Time}, {Time})");
        var sqlRow = await context.Observations.AsNoTracking().SingleAsync(o => o.Id == sqlId);
        Assert.True(sqlRow.RecordedAtUtc > cutoff); Assert.Empty(await history.ReadAsOfAsync(query));
    });

    [Fact]
    public Task Delayed_raw_reprocessing_and_corrections_preserve_provenance_without_leakage() => InTransaction(async context =>
    {
        var source = Source(); var identity = Identity(source); var raw = Raw(source);
        context.AddRange(source, identity, raw); await context.SaveChangesAsync();
        var cutoff = await DatabaseNow(context);
        var original = new Observation(Guid.NewGuid(), identity, null, ObservationType.DisplayName, Time, raw.CreatedAtUtc, raw.CreatedAtUtc, textValue: "Derived", rawPayloadId: raw.Id, sourceEventTimeUtc: Time.AddDays(-10), sourcePublishedAtUtc: Time.AddDays(-1));
        context.Add(original); await context.SaveChangesAsync();
        var correctionCutoff = original.RecordedAtUtc;
        var correction = new Observation(Guid.NewGuid(), identity, null, ObservationType.DisplayName, Time, raw.CreatedAtUtc, raw.CreatedAtUtc, textValue: "Corrected", rawPayloadId: raw.Id, corrects: original);
        context.Add(correction); await context.SaveChangesAsync();
        var history = new ObservationHistory(context);
        Assert.Empty(await history.ReadAsOfAsync(new(CanonicalEntityKind.Participant, cutoff)));
        Assert.Equal(original.Id, Assert.Single(await history.ReadAsOfAsync(new(CanonicalEntityKind.Participant, correctionCutoff))).Id);
        var query = new ObservationQuery(CanonicalEntityKind.Participant, correction.RecordedAtUtc);
        var first = await history.ReadPageAsOfAsync(query, 1);
        var second = await history.ReadPageAsOfAsync(query, 1, first.NextCursor);
        var ordered = new[] { original, correction }.OrderBy(o => o.AvailableAtUtc).ThenBy(o => o.CreatedAtUtc).ThenBy(o => o.Id).Select(o => o.Id);
        Assert.Equal(ordered, first.Items.Concat(second.Items).Select(o => o.Id));
        Assert.Null(second.NextCursor); Assert.Equal(original.Id, correction.CorrectsObservationId);
        Assert.Equal(2, correction.Version); Assert.Equal(raw.Id, correction.RawPayloadId);
        Assert.Equal(Time, original.RetrievedAtUtc); Assert.Equal(Time.AddDays(-10), original.SourceEventTimeUtc);
        Assert.Equal(Time.AddDays(-1), original.SourcePublishedAtUtc); Assert.Equal(raw.CreatedAtUtc, original.CreatedAtUtc);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public Task Observation_cannot_precede_raw_retrieval_creation_or_availability(int field) => InTransaction(async context =>
    {
        var source = Source(); var identity = Identity(source); var raw = Raw(source);
        context.AddRange(source, identity, raw); await context.SaveChangesAsync();
        var observation = new Observation(Guid.NewGuid(), identity, null, ObservationType.DisplayName,
            field == 0 ? Time.AddTicks(-10) : Time,
            field == 1 ? Time : raw.CreatedAtUtc,
            field == 2 ? Time : raw.CreatedAtUtc, textValue: "Invalid temporal link", rawPayloadId: raw.Id);
        context.Add(observation);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public Task Raw_bulk_and_direct_sql_mutation_are_rejected(int operation) => InTransaction(async context =>
    {
        var source = Source(); var raw = Raw(source); context.AddRange(source, raw); await context.SaveChangesAsync();
        Func<Task> write = operation switch
        {
            0 => async () => { await context.RawPayloads.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.StorageKey, "changed")); },
            1 => async () => { await context.RawPayloads.ExecuteDeleteAsync(); },
            2 => async () => { await context.Database.ExecuteSqlRawAsync("UPDATE ingestion.\"RawPayloads\" SET \"StorageKey\" = 'changed'"); },
            3 => async () => { await context.Database.ExecuteSqlRawAsync("DELETE FROM ingestion.\"RawPayloads\""); },
            _ => async () => { await context.Database.ExecuteSqlRawAsync("TRUNCATE ingestion.\"RawPayloads\" CASCADE"); }
        };
        var exception = await Assert.ThrowsAsync<PostgresException>(write);
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    });

    private sealed class Adapter(Guid source) : IProviderAdapter
    {
        public ProviderDescriptor Descriptor { get; } = new(source, "synthetic", [ReferenceSports.All[0].Id], [ProviderCapability.EventMetadata]);
        public int Calls { get; private set; }
        public ConfigurationValidation ValidateConfiguration() => new(true, []);
        public Task<ProviderResult> ExecuteAsync(ProviderRequest request, CancellationToken cancellationToken) { Calls++; return Task.FromResult(new ProviderResult(true)); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Current_source_status_controls_execution_independently_of_approved_policy(bool enabled) => InTransaction(async context =>
    {
        var source = Source(enabled);
        var policy = new SourcePolicy(Guid.NewGuid(), source.Id, 1, Time, null, "synthetic:terms", "synthetic:evidence", Time, [new(DataPurpose.DataRetrieval, PermissionDecision.Allowed)]);
        context.AddRange(source, policy); await context.SaveChangesAsync();
        policy.Approve(Guid.NewGuid(), "reviewer", "Synthetic approval", Time, Time); await context.SaveChangesAsync();
        var evaluator = new SourcePolicyEvaluator(new SourcePolicyHistory(context));
        Assert.True((await evaluator.EvaluateAsync(source.Id, DataPurpose.DataRetrieval, await DatabaseNow(context), new())).Allowed);
        var adapter = new Adapter(source.Id); var budget = new RequestBudget(new(1, 1, 1, TimeSpan.FromSeconds(10)), TimeProvider.System);
        var result = await new AuthorizedProviderExecutor(evaluator, TimeProvider.System, new SourceOperationalStatusReader(context)).ExecuteAsync(adapter, new(ReferenceSports.All[0].Id, ProviderCapability.EventMetadata, new()), budget);
        Assert.Equal(enabled, result.Success); Assert.Equal(enabled ? 1 : 0, adapter.Calls);
        if (!enabled) { Assert.Equal("source_disabled", result.Error!.Code); using var lease = budget.TryAcquire(); Assert.NotNull(lease); }
        source.IsEnabled = !enabled; await context.SaveChangesAsync();
        Assert.Equal(!enabled ? SourceOperationalStatus.Enabled : SourceOperationalStatus.Disabled, await new SourceOperationalStatusReader(context).ReadAsync(source.Id));
        Assert.Equal(SourceOperationalStatus.Missing, await new SourceOperationalStatusReader(context).ReadAsync(Guid.NewGuid()));
    });

    [Fact]
    public async Task Bs004_upgrade_preserves_raw_and_observation_and_assigns_conservative_availability()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("bs0041_upgrade").WithUsername("bs0041_upgrade").WithPassword(Guid.NewGuid().ToString("N")).Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); await container.StartAsync(timeout.Token);
        await using var context = new BetStatsDbContext(new DbContextOptionsBuilder<BetStatsDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await context.GetService<IMigrator>().MigrateAsync("20261008005300_SourceGovernanceAndTrustedHistory", timeout.Token);
        var source = Source(); var identity = Identity(source); var raw = Raw(source);
        context.AddRange(source, identity); await context.SaveChangesAsync(timeout.Token);
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ingestion.\"RawPayloads\" (\"Id\", \"DataSourceId\", \"RetrievedAtUtc\", \"CreatedAtUtc\", \"ContentHashSha256\", \"ContentType\", \"StorageKey\") VALUES ({raw.Id}, {source.Id}, {raw.RetrievedAtUtc}, {raw.CreatedAtUtc}, {raw.ContentHashSha256}, {raw.ContentType}, {raw.StorageKey})", timeout.Token);
        var id = Guid.NewGuid(); var derived = raw.CreatedAtUtc;
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO provenance.\"Observations\" (\"Id\", \"ProviderIdentityId\", \"DataSourceId\", \"EntityKind\", \"Type\", \"TextValue\", \"Version\", \"RetrievedAtUtc\", \"AvailableAtUtc\", \"CreatedAtUtc\", \"RawPayloadId\") VALUES ({id}, {identity.Id}, {source.Id}, 'Participant', 'DisplayName', 'Legacy', 1, {Time}, {derived}, {derived}, {raw.Id})", timeout.Token);
        var correctionId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO provenance.\"Observations\" (\"Id\", \"ProviderIdentityId\", \"DataSourceId\", \"EntityKind\", \"Type\", \"TextValue\", \"Version\", \"RetrievedAtUtc\", \"AvailableAtUtc\", \"CreatedAtUtc\", \"RawPayloadId\", \"CorrectsObservationId\", \"CorrectedVersion\", \"CorrectedAvailableAtUtc\") VALUES ({correctionId}, {identity.Id}, {source.Id}, 'Participant', 'DisplayName', 'Legacy correction', 2, {Time}, {derived}, {derived}, {raw.Id}, {id}, 1, {derived})", timeout.Token);
        var before = await DatabaseNow(context); await context.Database.MigrateAsync(timeout.Token);
        var observation = await context.Observations.AsNoTracking().SingleAsync(o => o.Id == id, timeout.Token);
        var correction = await context.Observations.AsNoTracking().SingleAsync(o => o.Id == correctionId, timeout.Token);
        Assert.Equal(id, correction.CorrectsObservationId); Assert.Equal(2, correction.Version); Assert.Equal(raw.Id, correction.RawPayloadId);
        Assert.Equal(id, observation.Id); Assert.Equal("Legacy", observation.TextValue); Assert.Equal(raw.Id, observation.RawPayloadId);
        Assert.Equal(derived, observation.AvailableAtUtc); Assert.True(observation.RecordedAtUtc >= before);
        Assert.Empty(await new ObservationHistory(context).ReadAsOfAsync(new(CanonicalEntityKind.Participant, before.AddTicks(-10))));
        Assert.Equal(raw.StorageKey, (await context.RawPayloads.AsNoTracking().SingleAsync(timeout.Token)).StorageKey);
        Assert.False(context.Database.HasPendingModelChanges()); Assert.Empty(await context.Database.GetPendingMigrationsAsync(timeout.Token));
        Assert.Equal(5, (await context.Database.GetAppliedMigrationsAsync(timeout.Token)).Count());
    }
}
