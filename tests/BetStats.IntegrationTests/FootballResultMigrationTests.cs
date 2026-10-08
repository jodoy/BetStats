using BetStats.Application.Datasets;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

namespace BetStats.IntegrationTests;

public sealed class FootballResultMigrationTests
{
    [Fact]
    public async Task Upgrade_from_bs008_1_preserves_finalized_v1_v2_bytes_hashes_and_recording_clocks()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("bs009_upgrade").WithUsername("bs009_upgrade").WithPassword(Guid.NewGuid().ToString("N")).Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); await container.StartAsync(timeout.Token);
        await using var db = new BetStatsDbContext(new DbContextOptionsBuilder<BetStatsDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261008150019_HistoricalIntegrityContext", timeout.Token);
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(timeout.Token);
        var snapshots = new List<DatasetArtifact>();
        // Opaque finalized artifacts test the migration's byte-preservation contract.
        // Existing dataset workflow fixtures independently test executable v1/v2 compatibility.
        foreach (var version in new[] { 1, 2 })
        {
            var bytes = CanonicalDatasetJson.Serialize(new { ManifestVersion = version, FictionalMigrationFixture = "unchanged" });
            snapshots.Add(new() { Id = Guid.NewGuid(), DefinitionFingerprint = new string('a', 64), ManifestHash = CanonicalDatasetJson.Hash(bytes),
                Content = bytes, RowCount = 1, FeatureSchemaVersion = version, BuiltAtUtc = now });
        }
        db.AddRange(snapshots); await db.SaveChangesAsync(timeout.Token);
        var original = await db.DatasetArtifacts.AsNoTracking().OrderBy(a => a.Id).ToListAsync(timeout.Token);
        await db.Database.MigrateAsync(timeout.Token); db.ChangeTracker.Clear();
        var upgraded = await db.DatasetArtifacts.AsNoTracking().OrderBy(a => a.Id).ToListAsync(timeout.Token);
        for (var i = 0; i < original.Count; i++)
        {
            Assert.Equal(original[i].Content, upgraded[i].Content); Assert.Equal(original[i].ManifestHash, upgraded[i].ManifestHash);
            Assert.Equal(original[i].RecordedAtUtc, upgraded[i].RecordedAtUtc); Assert.Equal(original[i].BuiltAtUtc, upgraded[i].BuiltAtUtc);
        }
        Assert.Empty(await db.FootballResults.ToListAsync(timeout.Token)); Assert.Empty(await db.FootballResultArtifacts.ToListAsync(timeout.Token));
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Empty(await db.Database.GetPendingMigrationsAsync(timeout.Token));
        Assert.Equal(11, (await db.Database.GetAppliedMigrationsAsync(timeout.Token)).Count());
    }
}
