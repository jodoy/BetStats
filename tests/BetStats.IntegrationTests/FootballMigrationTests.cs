using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

namespace BetStats.IntegrationTests;

public sealed class FootballMigrationTests
{
    [Fact]
    public async Task Bs0041_upgrade_preserves_legacy_raw_and_history_with_conservative_receipt_time()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("bs005_upgrade").WithUsername("bs005_upgrade").WithPassword(Guid.NewGuid().ToString("N")).Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); await container.StartAsync(timeout.Token);
        await using var context = new BetStatsDbContext(new DbContextOptionsBuilder<BetStatsDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await context.GetService<IMigrator>().MigrateAsync("20261008014627_AuditRemediation", timeout.Token);
        var time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var source = new DataSource { Id = Guid.NewGuid(), Code = "synthetic-upgrade", DisplayName = "Synthetic", IsEnabled = true, CreatedAtUtc = time };
        var identity = new ProviderIdentity(Guid.NewGuid(), source.Id, CanonicalEntityKind.Participant, "synthetic", time);
        context.AddRange(source, identity); await context.SaveChangesAsync(timeout.Token);
        var rawId = Guid.NewGuid(); var observationId = Guid.NewGuid(); var hash = new string('a', 64);
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ingestion.\"RawPayloads\" (\"Id\", \"DataSourceId\", \"RetrievedAtUtc\", \"CreatedAtUtc\", \"ContentHashSha256\", \"ContentType\", \"StorageKey\") VALUES ({rawId}, {source.Id}, {time}, {time}, {hash}, 'text/csv', 'synthetic/legacy')", timeout.Token);
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO provenance.\"Observations\" (\"Id\", \"ProviderIdentityId\", \"DataSourceId\", \"EntityKind\", \"Type\", \"TextValue\", \"Version\", \"RetrievedAtUtc\", \"AvailableAtUtc\", \"CreatedAtUtc\", \"RawPayloadId\") VALUES ({observationId}, {identity.Id}, {source.Id}, 'Participant', 'DisplayName', 'Legacy fictional', 1, {time}, {time}, {time}, {rawId})", timeout.Token);
        var before = await context.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(timeout.Token);
        await context.Database.MigrateAsync(timeout.Token);
        var raw = await context.RawPayloads.SingleAsync(timeout.Token); var observation = await context.Observations.SingleAsync(timeout.Token);
        Assert.Equal(rawId, raw.Id); Assert.Equal(hash, raw.ContentHashSha256); Assert.Equal(time, raw.RetrievedAtUtc); Assert.Null(raw.ByteLength);
        Assert.True(raw.RecordedAtUtc >= before); Assert.Equal(DateTimeKind.Utc, raw.RecordedAtUtc.Kind); Assert.Equal(0, raw.RecordedAtUtc.Ticks % 10);
        Assert.Equal(observationId, observation.Id); Assert.Equal(raw.Id, observation.RawPayloadId); Assert.Null(observation.DateValue); Assert.Equal(ObservationType.DisplayName, observation.Type);
        var injectedId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ingestion.\"RawPayloads\" (\"Id\", \"DataSourceId\", \"RetrievedAtUtc\", \"CreatedAtUtc\", \"ContentHashSha256\", \"ContentType\", \"StorageKey\", \"RecordedAtUtc\") VALUES ({injectedId}, {source.Id}, {time}, {time}, {hash}, 'text/csv', 'synthetic/new', {time})", timeout.Token);
        Assert.True((await context.RawPayloads.SingleAsync(r => r.Id == injectedId, timeout.Token)).RecordedAtUtc > before);
        Assert.False(context.Database.HasPendingModelChanges()); Assert.Empty(await context.Database.GetPendingMigrationsAsync(timeout.Token));
        Assert.Equal(9, (await context.Database.GetAppliedMigrationsAsync(timeout.Token)).Count());
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => context.RawPayloads.ExecuteUpdateAsync(s => s.SetProperty(r => r.StorageKey, "mutation"), timeout.Token));
    }
}
