using BetStats.Application.Ingestion;
using BetStats.Application.Governance;
using BetStats.Application.Providers;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

namespace BetStats.IntegrationTests;

public sealed class QualityMigrationTests
{
    [Fact]
    public async Task Upgrade_from_bs005_preserves_raw_decisions_observations_and_receipts()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("bs006_upgrade").WithUsername("bs006_upgrade").WithPassword(Guid.NewGuid().ToString("N")).Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); await container.StartAsync(timeout.Token);
        await using var db = new BetStatsDbContext(new DbContextOptionsBuilder<BetStatsDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261008083514_FirstFootballIngestion", timeout.Token);
        var source = await SyntheticFootballDemo.PrepareAsync(db, true, cancellationToken: timeout.Token);
        // Published BS-005 schema: insert immutable history through its original SQL shape.
        var time = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(timeout.Token);
        var raw = Guid.NewGuid(); var run = Guid.NewGuid(); var observation = Guid.NewGuid(); var receipt = Guid.NewGuid();
        var identity = await db.ProviderIdentities.FirstAsync(i => i.DataSourceId == source && i.EntityKind == BetStats.Domain.Identity.CanonicalEntityKind.Participant, timeout.Token);
        var decision = await db.IdentityResolutions.SingleAsync(d => d.ProviderIdentityId == identity.Id, timeout.Token);
        var hash = new string('a', 64); var key = new string('b', 64);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ingestion.\"IngestionRuns\" (\"Id\",\"DataSourceId\",\"Status\",\"CreatedAtUtc\") VALUES ({run},{source},'Pending',{time})", timeout.Token);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ingestion.\"RawPayloads\" (\"Id\",\"DataSourceId\",\"IngestionRunId\",\"RetrievedAtUtc\",\"CreatedAtUtc\",\"ContentType\",\"ContentHashSha256\",\"StorageKey\",\"ByteLength\") VALUES ({raw},{source},{run},{time},{time},'text/csv',{hash},'synthetic-upgrade',1)", timeout.Token);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO provenance.\"Observations\" (\"Id\",\"ProviderIdentityId\",\"DataSourceId\",\"EntityKind\",\"Type\",\"TextValue\",\"Version\",\"RetrievedAtUtc\",\"AvailableAtUtc\",\"CreatedAtUtc\",\"RawPayloadId\") VALUES ({observation},{identity.Id},{source},'Participant','DisplayName','Fictional preserved',1,{time},{time},{time},{raw})", timeout.Token);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ingestion.\"IngestionPublications\" (\"Id\",\"DataSourceId\",\"Key\",\"RawPayloadId\",\"RunId\",\"AcceptedRecords\",\"IsBatch\") VALUES ({receipt},{source},{key},{raw},{run},1,true)", timeout.Token);
        var rawBefore = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == raw, timeout.Token);
        var observationBefore = await db.Observations.AsNoTracking().SingleAsync(o => o.Id == observation, timeout.Token);
        await db.Database.MigrateAsync(timeout.Token); db.ChangeTracker.Clear();
        Assert.Equal(rawBefore.RecordedAtUtc, (await db.RawPayloads.SingleAsync(r => r.Id == raw, timeout.Token)).RecordedAtUtc);
        Assert.Equal(observationBefore.RecordedAtUtc, (await db.Observations.SingleAsync(o => o.Id == observation, timeout.Token)).RecordedAtUtc);
        Assert.Equal(decision.Id, (await db.IdentityResolutions.SingleAsync(d => d.ProviderIdentityId == identity.Id, timeout.Token)).Id);
        Assert.Equal(receipt, (await db.IngestionPublications.SingleAsync(timeout.Token)).Id);
        Assert.Empty(await db.QualityAssessments.ToListAsync(timeout.Token)); Assert.Empty(await db.MaintenanceEvents.ToListAsync(timeout.Token));
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Equal(12, (await db.Database.GetAppliedMigrationsAsync(timeout.Token)).Count());
        var indexCount = await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM pg_indexes WHERE schemaname = 'quality'").SingleAsync(timeout.Token);
        Assert.True(indexCount >= 10);
    }
}
