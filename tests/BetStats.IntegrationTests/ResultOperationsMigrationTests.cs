using BetStats.Application.Datasets;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

namespace BetStats.IntegrationTests;

public sealed class ResultOperationsMigrationTests
{
    [Fact]
    public async Task Upgrade_from_bs009_preserves_v1_v2_v3_bytes_hashes_and_database_clocks()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("bs010_upgrade").WithUsername("bs010_upgrade").WithPassword(Guid.NewGuid().ToString("N")).Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); await pg.StartAsync(timeout.Token);
        await using var db = new BetStatsDbContext(new DbContextOptionsBuilder<BetStatsDbContext>().UseNpgsql(pg.GetConnectionString()).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261008171605_FootballResultsOutcomeProvenance", timeout.Token);
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(timeout.Token);
        // Opaque bytes establish migration preservation; workflow tests establish executable compatibility.
        foreach (var version in new[] { 1, 2 })
        {
            var content = CanonicalDatasetJson.Serialize(new { ManifestVersion = version, Fixture = "unchanged" });
            db.Add(new DatasetArtifact { Id = Guid.NewGuid(), DefinitionFingerprint = new('a', 64), ManifestHash = CanonicalDatasetJson.Hash(content), Content = content,
                RowCount = 1, FeatureSchemaVersion = version, BuiltAtUtc = now });
        }
        await db.SaveChangesAsync(timeout.Token);
        var metadata = await db.DatasetArtifacts.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync(timeout.Token);
        var resultContent = CanonicalDatasetJson.Serialize(new { ManifestVersion = 3, Fixture = "legacy-v3-unchanged" });
        db.Add(new FootballResultArtifact { Id = Guid.NewGuid(), MetadataSnapshotId = metadata[0].Id, Hash = CanonicalDatasetJson.Hash(resultContent), Content = resultContent }); await db.SaveChangesAsync(timeout.Token);
        var results = await db.FootballResultArtifacts.AsNoTracking().SingleAsync(timeout.Token);
        await db.Database.MigrateAsync(timeout.Token); db.ChangeTracker.Clear();
        var migrated = await db.DatasetArtifacts.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync(timeout.Token);
        for (var i = 0; i < metadata.Length; i++)
        { Assert.Equal(metadata[i].Content, migrated[i].Content); Assert.Equal(metadata[i].ManifestHash, migrated[i].ManifestHash); Assert.Equal(metadata[i].RecordedAtUtc, migrated[i].RecordedAtUtc); }
        var migratedResult = await db.FootballResultArtifacts.AsNoTracking().SingleAsync(timeout.Token);
        Assert.Equal(results.Content, migratedResult.Content); Assert.Equal(results.Hash, migratedResult.Hash); Assert.Equal(results.RecordedAtUtc, migratedResult.RecordedAtUtc);
        Assert.Empty(await db.ResultInventory.ToArrayAsync(timeout.Token)); Assert.Empty(await db.EventEnds.ToArrayAsync(timeout.Token)); Assert.Empty(await db.ResultOperations.ToArrayAsync(timeout.Token));
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.Database.GetPendingMigrationsAsync(timeout.Token));
        Assert.Equal(13, (await db.Database.GetAppliedMigrationsAsync(timeout.Token)).Count());
    }
}
