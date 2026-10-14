using BetStats.Application.Datasets;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BetStats.IntegrationTests;

public sealed class ProviderActivationMigrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact] public async Task Additive_upgrade_preserves_raw_canonical_receipts_and_frozen_artifacts()
    {
        var f = await new ProviderPublicationWorkflowTests(fixture).Create(); await using var s = f.Scenario;
        await s.Db.GetService<IMigrator>().MigrateAsync("20261014115707_DurableProviderPublication");
        var raw = await ProviderPublicationWorkflowTests.Capture(f);
        await ProviderPublicationWorkflowTests.Publish(f,raw);
        async Task<string> Fingerprint() => CanonicalDatasetJson.Fingerprint(new {
            Raw=await s.Db.RawPayloads.AsNoTracking().OrderBy(x=>x.Id).ToArrayAsync(),
            Observations=await s.Db.Observations.AsNoTracking().OrderBy(x=>x.Id).ToArrayAsync(),
            Receipts=await s.Db.Set<ProviderPublicationRecord>().AsNoTracking().OrderBy(x=>x.Id).ToArrayAsync(),
            Predictions=await s.Db.Backtests.AsNoTracking().OrderBy(x=>x.Id).ToArrayAsync(),
            Results=await s.Db.FootballResultArtifacts.AsNoTracking().OrderBy(x=>x.Id).ToArrayAsync()
        });
        var before=await Fingerprint(); await s.Db.Database.MigrateAsync(); s.Db.ChangeTracker.Clear();
        Assert.Equal(before,await Fingerprint()); Assert.False(s.Db.Database.HasPendingModelChanges());
        Assert.Empty(await s.Db.Set<ProviderGrantRecord>().ToArrayAsync()); Assert.Empty(await s.Db.Set<ProviderRequestEvent>().ToArrayAsync());
        Assert.Equal(16,(await s.Db.Database.GetAppliedMigrationsAsync()).Count());
    }
}
