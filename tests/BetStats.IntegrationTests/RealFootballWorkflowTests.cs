using System.Text;
using BetStats.Application.Football;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Application.Quality;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Sports;
using BetStats.Infrastructure;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BetStats.IntegrationTests;

public sealed class RealFootballWorkflowTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    // Project-authored fictional bytes only. No third-party data or permissions are implied.
    private const string Csv = "Div,Date,HomeTeam,AwayTeam,FTHG,FTAG,HTHG,HTAG,MatchId\nFICT,02/01/2026,Amber Comets,Cobalt Owls,2,1,1,0,reviewed-history\n";
    private ServiceProvider Provider(string root) => new ServiceCollection().AddPersistence(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["ConnectionStrings:BetStats"] = fixture.GetConnectionString(), ["Ingestion:RawStoragePath"] = root }).Build()).BuildServiceProvider();

    [Fact]
    public async Task Local_import_requires_review_preserves_raw_and_supports_reconciliation_corrections_and_revocation()
    {
        var root = Path.Combine(Path.GetTempPath(), "bs013-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var provider = Provider(root); using var scope = provider.CreateScope(); var services = scope.ServiceProvider;
            var db = services.GetRequiredService<BetStatsDbContext>();
            var source = await SyntheticFootballDemo.PrepareAsync(db, true, "bs013-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(root, "operator.csv"); await File.WriteAllTextAsync(path, Csv, new UTF8Encoding(false));
            var ops = services.GetRequiredService<FootballImportOperations>(); var plan = await ops.PlanAsync(source, path, SyntheticFootballDemo.Scope);
            var operation = Guid.NewGuid();
            var first = await ops.RunAsync(operation, plan, path, "test:operator", "Explicit fictional test import");
            Assert.Equal(ImportOutcome.Partial, first.Outcome); Assert.Empty(await db.FootballResults.Where(r => r.SourceId == source).ToListAsync());
            var raw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.DataSourceId == source);
            Assert.Equal("fixture:" + HistoricalFootballCsvParser.Version, raw.ExternalReference);
            Assert.Equal(Encoding.UTF8.GetBytes(Csv), (await services.GetRequiredService<IRawPayloadStore>().ReadAsync(new(raw.StorageKey, raw.ContentHashSha256, raw.ByteLength!.Value))).ToArray());
            var eventIdentity = await db.ProviderIdentities.SingleAsync(i => i.DataSourceId == source && i.EntityKind == CanonicalEntityKind.SportingEvent);
            var decisions = await db.IdentityResolutions.Where(i => i.DataSourceId == source).ToListAsync();
            var competitionId = decisions.Single(d => d.CanonicalCompetitionId != null).CanonicalCompetitionId;
            var seasonId = decisions.Single(d => d.CanonicalSeasonId != null).CanonicalSeasonId;
            var participantIds = decisions.Where(d => d.CanonicalParticipantId != null).Select(d => d.CanonicalParticipantId!.Value).ToArray();
            var competition = await db.Competitions.SingleAsync(c => c.Id == competitionId);
            var season = await db.Seasons.SingleAsync(s => s.Id == seasonId);
            var teams = await db.Participants.Where(p => participantIds.Contains(p.Id)).ToListAsync();
            var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
            var canonical = new SportingEvent(Guid.NewGuid(), competition, season, null, SportingEventStatus.Completed, now);
            canonical.AddParticipant(teams.Single(t => t.Name == "Amber Comets"), ParticipantRole.Home, 1);
            canonical.AddParticipant(teams.Single(t => t.Name == "Cobalt Owls"), ParticipantRole.Away, 2);
            db.Add(canonical); await db.SaveChangesAsync();
            var latest = await db.IdentityResolutions.Where(d => d.ProviderIdentityId == eventIdentity.Id).OrderByDescending(d => d.Version).FirstAsync();
            var reviewed = await services.GetRequiredService<IIdentityReview>().DecideAsync(new(eventIdentity.Id, source, ReviewAction.Approve,
                new(CanonicalEntityKind.SportingEvent, canonical.Id), latest.Version, "test:reviewer", "Explicit fixture identity review"));
            Assert.Equal("accepted", reviewed.Result);
            var reconciled = await services.GetRequiredService<IDataReconciliation>().RunAsync([new(raw.Id, SyntheticFootballDemo.Scope)], "test:operator", "Reconcile reviewed identity");
            Assert.Equal("Completed", reconciled.Result);
            var original = await db.FootballResults.AsNoTracking().SingleAsync(r => r.SourceId == source);
            Assert.Null(original.PublishedAtUtc); Assert.True(original.RecordedAtUtc > original.EventDate.ToDateTime(TimeOnly.MinValue));
            var recovered = await ops.RunAsync(operation, plan, path, "test:operator", "Explicit retry after review", true);
            Assert.Equal(ImportOutcome.Succeeded, recovered.Outcome);
            Assert.Equal(ImportOutcome.Reused, (await ops.RunAsync(operation, plan, path, "test:operator", "Repeat receipt")).Outcome);
            await File.WriteAllTextAsync(path, Csv.Replace(",2,1,1,0,", ",2,2,1,0,", StringComparison.Ordinal), new UTF8Encoding(false));
            var correction = await ops.PlanAsync(source, path, SyntheticFootballDemo.Scope);
            Assert.Equal(ImportOutcome.Succeeded, (await ops.RunAsync(Guid.NewGuid(), correction, path, "test:operator", "Explicit late correction")).Outcome);
            var results = await db.FootballResults.AsNoTracking().Where(r => r.SourceId == source).OrderBy(r => r.Version).ToListAsync();
            Assert.Equal(2, results.Count); Assert.Equal(original.Id, results[1].CorrectsId); Assert.Equal(1, results[0].Value.FullTime.Away);
            now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
            var query = new FootballResultQuery(competition.Id, season.Id, now, DataPurpose.InternalAnalytics, new(), SourceId: source);
            var evidence = await services.GetRequiredService<IFootballResults>().ReadAsync(query);
            Assert.Equal("Draw", Assert.Single(evidence.Results).Labels!.Winner);
            var correctedRaw = await db.RawPayloads.AsNoTracking().SingleAsync(r => r.Id == results[1].RawId);
            var rawPath = Path.Combine(root, correctedRaw.StorageKey + ".raw"); var retained = await File.ReadAllBytesAsync(rawPath);
            await File.WriteAllBytesAsync(rawPath, Encoding.UTF8.GetBytes("corrupted"));
            var corrupted = await services.GetRequiredService<IFootballResults>().ReadAsync(query);
            Assert.Contains("result_raw_integrity_or_context", Assert.Single(corrupted.Results).Reasons);
            await File.WriteAllBytesAsync(rawPath, retained);
            var readiness = await FootballHistoryOperatorCommand.ReadinessAsync(services, source); Assert.NotNull(readiness);
            db.ChangeTracker.Clear(); var policy = await db.SourcePolicies.Include(p => p.Audit).SingleAsync(p => p.DataSourceId == source);
            now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
            policy.Revoke(Guid.NewGuid(), "test:reviewer", "Permission withdrawn", now); await db.SaveChangesAsync();
            await Assert.ThrowsAsync<IngestionDeniedException>(() => ops.PlanAsync(source, Path.Combine(root, "does-not-exist.csv"), SyntheticFootballDemo.Scope));
            await Assert.ThrowsAsync<IngestionDeniedException>(() => FootballHistoryOperatorCommand.ReadinessAsync(services, source));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Source_owner_lock_and_changed_file_are_rejected_before_publication()
    {
        var root = Path.Combine(Path.GetTempPath(), "bs013-owner-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var provider = Provider(root); using var scope = provider.CreateScope(); var services = scope.ServiceProvider;
            var db = services.GetRequiredService<BetStatsDbContext>(); var source = await SyntheticFootballDemo.PrepareAsync(db, true, "bs013-lock-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(root, "file.csv"); await File.WriteAllTextAsync(path, Csv);
            var ops = services.GetRequiredService<FootballImportOperations>(); var plan = await ops.PlanAsync(source, path, SyntheticFootballDemo.Scope);
            await using var owner = fixture.CreateContext(); await owner.Database.OpenConnectionAsync();
            await owner.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock(hashtextextended({source.ToString("D")}, 9013))");
            await Assert.ThrowsAsync<InvalidOperationException>(() => ops.RunAsync(Guid.NewGuid(), plan, path, "test:operator", "Concurrent attempt"));
            await owner.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock(hashtextextended({source.ToString("D")}, 9013))");
            await File.AppendAllTextAsync(path, "truncated");
            var failed = await ops.RunAsync(Guid.NewGuid(), plan, path, "test:operator", "Changed file attempt");
            Assert.Equal(ImportOutcome.Failed, failed.Outcome); Assert.Empty(await db.RawPayloads.Where(r => r.DataSourceId == source).ToListAsync());
            await File.WriteAllTextAsync(path, Csv);
            var interrupted = Guid.NewGuid(); var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
            db.FootballImportOperations.Add(new() { Id = Guid.NewGuid(), OperationId = interrupted, SourceId = source, Sequence = 1,
                Status = ResultOperationStatus.Running, Fingerprint = plan.Fingerprint, OwnerToken = Guid.NewGuid(), LeaseUntilUtc = now.AddSeconds(1),
                OperatorId = "test:stopped-owner", Reason = "Interrupted process fixture" }); await db.SaveChangesAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => ops.RunAsync(interrupted, plan, path, "test:operator", "Live owner recovery denied", true));
            var ownership = services.GetRequiredService<FootballImportOwnership>(); ownership.OperationId = interrupted; ownership.OwnerToken = Guid.NewGuid();
            await using (var transaction = await db.Database.BeginTransactionAsync())
                await Assert.ThrowsAsync<IngestionDeniedException>(() => ownership.EnsureAsync(db, default));
            ownership.OperationId = null;
            await Task.Delay(1100);
            Assert.Equal(ImportOutcome.Partial, (await ops.RunAsync(interrupted, plan, path, "test:operator", "Recover expired stopped owner", true)).Outcome);
            Assert.Equal(3, (await ops.InspectAsync(interrupted))!.Sequence);
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ingestion.\"FootballImportOperations\" SET \"Reason\" = 'rewrite' WHERE \"OperationId\" = {interrupted}"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Previously_unknown_team_alias_can_be_reviewed_before_any_canonical_event_is_published()
    {
        var root = Path.Combine(Path.GetTempPath(), "bs013-alias-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var provider = Provider(root); using var scope = provider.CreateScope(); var services = scope.ServiceProvider;
            var db = services.GetRequiredService<BetStatsDbContext>(); var source = await SyntheticFootballDemo.PrepareAsync(db, true, "bs013-alias-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(root, "alias.csv"); await File.WriteAllTextAsync(path, Csv.Replace("Amber Comets", "Amber Alias", StringComparison.Ordinal));
            var ops = services.GetRequiredService<FootballImportOperations>(); var plan = await ops.PlanAsync(source, path, SyntheticFootballDemo.Scope);
            Assert.Equal(ImportOutcome.Partial, (await ops.RunAsync(Guid.NewGuid(), plan, path, "test:operator", "Import unreviewed alias")).Outcome);
            var aliasReference = FootballDataCsvParser.TeamReference("FICT", "Amber Alias");
            var originalReference = FootballDataCsvParser.TeamReference("FICT", "Amber Comets");
            var alias = await db.ProviderIdentities.SingleAsync(i => i.DataSourceId == source && i.ExternalId == aliasReference);
            var original = await db.ProviderIdentities.SingleAsync(i => i.DataSourceId == source && i.ExternalId == originalReference);
            var target = await db.IdentityResolutions.Where(d => d.ProviderIdentityId == original.Id).Select(d => d.CanonicalParticipantId).SingleAsync();
            var version = await db.IdentityResolutions.Where(d => d.ProviderIdentityId == alias.Id).MaxAsync(d => d.Version);
            var reviewed = await services.GetRequiredService<IIdentityReview>().DecideAsync(new(alias.Id, source, ReviewAction.Approve,
                new(CanonicalEntityKind.Participant, target!.Value), version, "test:reviewer", "Explicit alias mapping"));
            Assert.Equal("accepted", reviewed.Result); Assert.Empty(await db.FootballResults.Where(r => r.SourceId == source).ToArrayAsync());
        }
        finally { Directory.Delete(root, true); }
    }
}
