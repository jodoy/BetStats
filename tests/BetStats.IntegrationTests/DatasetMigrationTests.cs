using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

namespace BetStats.IntegrationTests;

public sealed class DatasetMigrationTests
{
    [Fact]
    public async Task Upgrade_from_bs006_preserves_recorded_history_and_adds_empty_dataset_tables()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("bs007_upgrade").WithUsername("bs007_upgrade").WithPassword(Guid.NewGuid().ToString("N")).Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); await container.StartAsync(timeout.Token);
        await using var db = new BetStatsDbContext(new DbContextOptionsBuilder<BetStatsDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261008094144_DataQualityIdentityReview", timeout.Token);
        var source = await SyntheticFootballDemo.PrepareAsync(db, true, cancellationToken: timeout.Token);
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(timeout.Token);
        var raw = new BetStats.Infrastructure.Persistence.Entities.RawPayload { Id = Guid.NewGuid(), DataSourceId = source, RetrievedAtUtc = now, CreatedAtUtc = now,
            ContentHashSha256 = new string('a', 64), ContentType = "text/csv", StorageKey = "fictional-upgrade-metadata", ByteLength = 1 };
        db.Add(raw); await db.SaveChangesAsync(timeout.Token);
        var quality = new BetStats.Domain.Quality.QualityAssessment { Id = Guid.NewGuid(), ExecutionId = Guid.NewGuid(), DataSourceId = source, RawPayloadId = raw.Id,
            Row = 1, RecordReference = "fictional-upgrade", SportId = BetStats.Application.Quality.FootballQualityRules.Football, RuleId = "football.sport", RuleVersion = 1,
            Passed = true, Severity = BetStats.Domain.Quality.QualitySeverity.Info, ReasonCode = "passed", Classification = BetStats.Domain.Quality.QualityClassification.Accepted, AssessedAtUtc = now };
        db.Add(quality); await db.SaveChangesAsync(timeout.Token);
        var qualityRecorded = quality.RecordedAtUtc; var rawRecorded = raw.RecordedAtUtc;
        var before = await db.IdentityResolutions.AsNoTracking().Where(d => db.ProviderIdentities.Any(i => i.Id == d.ProviderIdentityId && i.DataSourceId == source))
            .OrderBy(d => d.Id).Select(d => new { d.Id, d.RecordedAtUtc, d.Version }).ToListAsync(timeout.Token);
        await db.Database.MigrateAsync(timeout.Token);
        var after = await db.IdentityResolutions.AsNoTracking().Where(d => db.ProviderIdentities.Any(i => i.Id == d.ProviderIdentityId && i.DataSourceId == source))
            .OrderBy(d => d.Id).Select(d => new { d.Id, d.RecordedAtUtc, d.Version }).ToListAsync(timeout.Token);
        Assert.Equal(before, after); Assert.Empty(await db.DatasetArtifacts.ToListAsync(timeout.Token)); Assert.Empty(await db.DatasetFeatures.ToListAsync(timeout.Token)); Assert.Empty(await db.DatasetBuildEvents.ToListAsync(timeout.Token));
        db.ChangeTracker.Clear();
        Assert.Equal(qualityRecorded, (await db.QualityAssessments.SingleAsync(timeout.Token)).RecordedAtUtc);
        Assert.Equal(rawRecorded, (await db.RawPayloads.SingleAsync(timeout.Token)).RecordedAtUtc);
        Assert.Equal(8, (await db.Database.GetAppliedMigrationsAsync(timeout.Token)).Count()); Assert.False(db.Database.HasPendingModelChanges());
        var count = await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='datasets' AND NOT t.tgisinternal").SingleAsync(timeout.Token);
        Assert.Equal(6, count);
    }
}
