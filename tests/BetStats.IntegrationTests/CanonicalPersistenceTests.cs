using BetStats.Application.Observations;
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

public sealed class CanonicalPersistenceTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTime Time = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SportId = ReferenceSports.All[0].Id;
    private static DataSource Source() => new() { Id = Guid.NewGuid(), Code = "synthetic-" + Guid.NewGuid().ToString("N"), DisplayName = "Synthetic fixture", CreatedAtUtc = Time };
    private static ProviderIdentity Identity(DataSource source, CanonicalEntityKind kind = CanonicalEntityKind.Participant, string externalId = "synthetic") => new(Guid.NewGuid(), source.Id, kind, externalId, Time);
    private static Participant Participant() => new(Guid.NewGuid(), SportId, "Synthetic participant", ParticipantType.Team);
    private async Task InTransaction(Func<BetStatsDbContext, Task> test)
    {
        await using var context = fixture.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await test(context);
        await transaction.RollbackAsync();
    }
    private static async Task Reject(Func<Task> write, string sqlState)
    {
        var exception = await Assert.ThrowsAsync<DbUpdateException>(write);
        Assert.Equal(sqlState, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Fact]
    public Task Canonical_entities_round_trip_and_support_reusable_individuals() => InTransaction(async context =>
    {
        Assert.Equal(4, await context.Sports.CountAsync());
        var competition = new Competition(Guid.NewGuid(), ReferenceSports.All[1].Id, "Synthetic tournament", null, CompetitionType.Tournament);
        var season = new Season(Guid.NewGuid(), competition.Id, "Synthetic season", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var participant = new Participant(Guid.NewGuid(), competition.SportId, "Synthetic individual", ParticipantType.Individual);
        var first = new SportingEvent(Guid.NewGuid(), competition, season, Time.AddDays(1), SportingEventStatus.Scheduled, Time);
        var second = new SportingEvent(Guid.NewGuid(), competition, null, null, SportingEventStatus.Postponed, Time);
        first.AddParticipant(participant, ParticipantRole.Side1, 1);
        second.AddParticipant(participant, ParticipantRole.Side2, 2);
        context.AddRange(competition, season, participant, first, second);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var events = await context.SportingEvents.Include(e => e.Participants).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(participant.Id, Assert.Single(e.Participants).ParticipantId));
        Assert.Equal(DateTimeKind.Utc, events[0].CreatedAtUtc.Kind);
        Assert.Equal(ParticipantType.Individual, (await context.Participants.SingleAsync()).ParticipantType);
    });

    [Theory]
    [InlineData(CanonicalEntityKind.Sport)]
    [InlineData(CanonicalEntityKind.Competition)]
    [InlineData(CanonicalEntityKind.Season)]
    [InlineData(CanonicalEntityKind.Participant)]
    [InlineData(CanonicalEntityKind.SportingEvent)]
    public Task Each_identity_kind_has_a_real_canonical_foreign_key(CanonicalEntityKind kind) => InTransaction(async context =>
    {
        var source = Source();
        var competition = new Competition(Guid.NewGuid(), SportId, "Synthetic", null, CompetitionType.League);
        var season = new Season(Guid.NewGuid(), competition.Id, "Synthetic", null, null);
        var participant = Participant();
        var sportingEvent = new SportingEvent(Guid.NewGuid(), competition, season, null, SportingEventStatus.Scheduled, Time);
        var targetId = kind switch { CanonicalEntityKind.Sport => SportId, CanonicalEntityKind.Competition => competition.Id,
            CanonicalEntityKind.Season => season.Id, CanonicalEntityKind.Participant => participant.Id, _ => sportingEvent.Id };
        var identity = Identity(source, kind);
        var target = new CanonicalReference(kind, targetId);
        var decision = new IdentityResolution(Guid.NewGuid(), identity, ResolutionStatus.Resolved, target, "synthetic-reviewer", "Verified synthetic fixture", Time);
        var observation = kind == CanonicalEntityKind.SportingEvent
            ? new Observation(Guid.NewGuid(), identity, target, ObservationType.EventStatus, Time, Time, Time, statusValue: SportingEventStatus.Scheduled)
            : new Observation(Guid.NewGuid(), identity, target, ObservationType.DisplayName, Time, Time, Time, textValue: "Synthetic");
        context.AddRange(source, competition, season, participant, sportingEvent, identity, decision, observation);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        Assert.Equal(targetId, (await context.IdentityResolutions.SingleAsync()).CanonicalId);
        Assert.Equal(targetId, Assert.Single(await new ObservationHistory(context).ReadAsOfAsync(new(kind, Time, targetId))).CanonicalId);
    });

    [Fact]
    public Task Provider_identity_uniqueness_is_source_and_kind_scoped() => InTransaction(async context =>
    {
        var first = Source(); var second = Source();
        context.AddRange(first, second, Identity(first), Identity(second), Identity(first, CanonicalEntityKind.Competition));
        await context.SaveChangesAsync();
        context.Add(Identity(first));
        await Reject(() => context.SaveChangesAsync(), PostgresErrorCodes.UniqueViolation);
    });

    [Fact]
    public Task Decisions_are_audited_as_of_and_concurrent_branches_are_rejected() => InTransaction(async context =>
    {
        var source = Source(); var identity = Identity(source); var target = Participant();
        context.AddRange(source, identity, target);
        await context.SaveChangesAsync();
        var history = new IdentityResolutionHistory(context);
        var unresolved = new IdentityResolution(Guid.NewGuid(), identity, ResolutionStatus.Unresolved, null, "reviewer", "Missing evidence", Time);
        var ambiguous = new IdentityResolution(Guid.NewGuid(), identity, ResolutionStatus.Ambiguous, null, "reviewer", "Conflicting evidence", Time.AddHours(1), unresolved);
        var resolved = new IdentityResolution(Guid.NewGuid(), identity, ResolutionStatus.Resolved, new(CanonicalEntityKind.Participant, target.Id), "reviewer", "Explicit verified evidence", Time.AddHours(2), ambiguous);
        await history.AppendAsync(unresolved); await history.AppendAsync(ambiguous); await history.AppendAsync(resolved);
        Assert.Null(await history.ReadLatestAsOfAsync(identity.Id, Time.AddSeconds(-1)));
        Assert.Equal(ResolutionStatus.Unresolved, (await history.ReadLatestAsOfAsync(identity.Id, Time))!.Status);
        Assert.Null((await history.ReadLatestAsOfAsync(identity.Id, Time.AddHours(1)))!.CanonicalId);
        Assert.Equal(target.Id, (await history.ReadLatestAsOfAsync(identity.Id, Time.AddHours(2)))!.CanonicalId);
        var staleBranch = new IdentityResolution(Guid.NewGuid(), identity, ResolutionStatus.Unresolved, null, "reviewer", "Stale concurrent decision", Time.AddHours(3), ambiguous);
        await Reject(() => history.AppendAsync(staleBranch), PostgresErrorCodes.UniqueViolation);
    });

    [Fact]
    public Task Future_retrieval_and_corrections_cannot_leak_despite_earlier_source_times() => InTransaction(async context =>
    {
        var source = Source(); var identity = Identity(source); var target = Participant();
        var reference = new CanonicalReference(CanonicalEntityKind.Participant, target.Id);
        var original = new Observation(Guid.NewGuid(), identity, reference, ObservationType.DisplayName, Time, Time, Time, textValue: "Original", sourceEventTimeUtc: Time.AddDays(-10));
        var corrected = new Observation(Guid.NewGuid(), identity, reference, ObservationType.DisplayName, Time.AddDays(2), Time.AddDays(2), Time.AddDays(2), textValue: "Correction", sourceEventTimeUtc: Time.AddDays(-10), sourcePublishedAtUtc: Time.AddDays(-1), corrects: original);
        context.AddRange(source, identity, target, original, corrected);
        await context.SaveChangesAsync();
        var history = new ObservationHistory(context);
        var earlier = await history.ReadAsOfAsync(new(CanonicalEntityKind.Participant, Time.AddDays(1), target.Id));
        Assert.Equal("Original", Assert.Single(earlier).TextValue);
        Assert.DoesNotContain(earlier, o => o.Id == corrected.Id);
        var later = await history.ReadAsOfAsync(new(CanonicalEntityKind.Participant, Time.AddDays(2), target.Id));
        Assert.Equal(new[] { original.Id, corrected.Id }, later.Select(o => o.Id));
        Assert.Equal(original.Id, later[1].CorrectsObservationId);
        Assert.Empty(await history.ReadAsOfAsync(new(CanonicalEntityKind.Participant, Time.AddSeconds(-1))));
    });

    [Fact]
    public Task Equal_timestamps_are_ordered_by_uuid_and_unresolved_history_is_not_remapped() => InTransaction(async context =>
    {
        var source = Source(); var identity = Identity(source); var target = Participant();
        var low = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var high = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var first = new Observation(low, identity, null, ObservationType.DisplayName, Time, Time, Time, textValue: "Unresolved first");
        var second = new Observation(high, identity, null, ObservationType.DisplayName, Time, Time, Time, textValue: "Unresolved second");
        var decision = new IdentityResolution(Guid.NewGuid(), identity, ResolutionStatus.Resolved, new(CanonicalEntityKind.Participant, target.Id), "reviewer", "Later resolution", Time.AddDays(1));
        context.AddRange(source, identity, target, second, first, decision);
        await context.SaveChangesAsync();
        var history = new ObservationHistory(context);
        var query = new ObservationQuery(CanonicalEntityKind.Participant, Time.AddDays(2), dataSourceId: source.Id, providerIdentityId: identity.Id);
        Assert.Equal(new[] { low, high }, (await history.ReadAsOfAsync(query)).Select(o => o.Id));
        Assert.Equal(new[] { low, high }, (await history.ReadAsOfAsync(query)).Select(o => o.Id));
        Assert.All(await history.ReadAsOfAsync(query), o => Assert.Null(o.CanonicalId));
        Assert.Empty(await history.ReadAsOfAsync(new(CanonicalEntityKind.Participant, Time.AddDays(2), target.Id)));
        Assert.Empty(await history.ReadAsOfAsync(new(CanonicalEntityKind.Participant, Time.AddDays(2), dataSourceId: Guid.NewGuid())));
        Assert.Empty(await history.ReadAsOfAsync(new(CanonicalEntityKind.Participant, Time.AddDays(2), providerIdentityId: Guid.NewGuid())));
        // AsNoTracking must not return a modified tracked object as historical evidence.
        context.Entry(first).Property(o => o.TextValue).CurrentValue = "Unsaved mutation";
        Assert.Equal("Unresolved first", (await history.ReadAsOfAsync(query))[0].TextValue);
    });

    [Fact]
    public Task Observation_foreign_keys_require_existing_canonical_targets() => InTransaction(async context =>
    {
        var source = Source(); var identity = Identity(source);
        context.AddRange(source, identity);
        await context.SaveChangesAsync();
        context.Add(new Observation(Guid.NewGuid(), identity, new(CanonicalEntityKind.Participant, Guid.NewGuid()), ObservationType.DisplayName, Time, Time, Time, textValue: "Synthetic"));
        await Reject(() => context.SaveChangesAsync(), PostgresErrorCodes.ForeignKeyViolation);
    });

    [Fact]
    public Task Cross_sport_membership_is_rejected_even_when_domain_is_bypassed() => InTransaction(async context =>
    {
        var competition = new Competition(Guid.NewGuid(), SportId, "Synthetic", null, CompetitionType.League);
        var sportingEvent = new SportingEvent(Guid.NewGuid(), competition, null, null, SportingEventStatus.Scheduled, Time);
        var foreignParticipant = new Participant(Guid.NewGuid(), ReferenceSports.All[1].Id, "Synthetic", ParticipantType.Individual);
        context.AddRange(competition, sportingEvent, foreignParticipant);
        await context.SaveChangesAsync();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO canonical.\"EventParticipants\" (\"EventId\", \"ParticipantId\", \"SportId\", \"Role\", \"Position\") VALUES ({sportingEvent.Id}, {foreignParticipant.Id}, {SportId}, 'Side1', 1)"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Canonical_targets_and_sources_cannot_be_deleted_under_history(bool deleteSource) => InTransaction(async context =>
    {
        var source = Source(); var identity = Identity(source); var target = Participant();
        var observation = new Observation(Guid.NewGuid(), identity, new(CanonicalEntityKind.Participant, target.Id), ObservationType.DisplayName, Time, Time, Time, textValue: "Synthetic");
        context.AddRange(source, identity, target, observation);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        if (deleteSource) context.Remove(await context.DataSources.SingleAsync());
        else context.Remove(await context.Participants.SingleAsync());
        await Reject(() => context.SaveChangesAsync(), PostgresErrorCodes.ForeignKeyViolation);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public Task Provenance_sql_delete_and_truncate_are_blocked(int operation) => InTransaction(async context =>
    {
        var sql = operation switch
        {
            0 => "DELETE FROM provenance.\"ProviderIdentities\"",
            1 => "DELETE FROM provenance.\"IdentityResolutions\"",
            2 => "DELETE FROM provenance.\"Observations\"",
            _ => "TRUNCATE provenance.\"Observations\""
        };
        var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    });

    [Fact]
    public Task Observation_bulk_update_is_blocked_by_database_trigger() => InTransaction(async context =>
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Observations.ExecuteUpdateAsync(setters => setters.SetProperty(o => o.TextValue, "Mutation")));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Ef_observation_update_and_delete_are_blocked(bool delete) => InTransaction(async context =>
    {
        var source = Source(); var identity = Identity(source);
        var observation = new Observation(Guid.NewGuid(), identity, null, ObservationType.DisplayName, Time, Time, Time, textValue: "Synthetic");
        context.AddRange(source, identity, observation);
        await context.SaveChangesAsync();
        if (delete) context.Remove(observation);
        else context.Entry(observation).Property(o => o.TextValue).CurrentValue = "Mutation";
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    });

    [Fact]
    public Task Sql_cannot_backdate_availability() => InTransaction(async context =>
    {
        var source = Source(); var identity = Identity(source);
        var observation = new Observation(Guid.NewGuid(), identity, null, ObservationType.DisplayName, Time, Time, Time, textValue: "Synthetic");
        context.AddRange(source, identity);
        await context.SaveChangesAsync();
        context.Add(observation);
        context.Entry(observation).Property(o => o.AvailableAtUtc).CurrentValue = Time.AddDays(-1);
        await Reject(() => context.SaveChangesAsync(), PostgresErrorCodes.CheckViolation);
    });

    [Fact]
    public Task Raw_provenance_cannot_cross_data_sources() => InTransaction(async context =>
    {
        var source = Source(); var other = Source(); var identity = Identity(source);
        var raw = new RawPayload { Id = Guid.NewGuid(), DataSourceId = other.Id, RetrievedAtUtc = Time, CreatedAtUtc = Time,
            ContentHashSha256 = new string('a', 64), ContentType = "application/json", StorageKey = "synthetic/raw" };
        context.AddRange(source, other, identity, raw);
        await context.SaveChangesAsync();
        context.Add(new Observation(Guid.NewGuid(), identity, null, ObservationType.DisplayName, Time, Time, Time, textValue: "Synthetic", rawPayloadId: raw.Id));
        await Reject(() => context.SaveChangesAsync(), PostgresErrorCodes.ForeignKeyViolation);
    });

    [Fact]
    public Task Raw_metadata_and_observation_source_times_round_trip() => InTransaction(async context =>
    {
        var source = Source(); var identity = Identity(source);
        var raw = new RawPayload { Id = Guid.NewGuid(), DataSourceId = source.Id, RetrievedAtUtc = Time, CreatedAtUtc = Time,
            ContentHashSha256 = new string('b', 64), ContentType = "application/json", StorageKey = "synthetic/raw" };
        var observation = new Observation(Guid.NewGuid(), identity, null, ObservationType.DisplayName, Time, Time.AddHours(1), Time,
            textValue: "Synthetic", sourceEventTimeUtc: Time.AddDays(-10), sourcePublishedAtUtc: Time.AddDays(-1), rawPayloadId: raw.Id);
        context.AddRange(source, identity, raw, observation);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var stored = await context.Observations.SingleAsync();
        Assert.Equal(raw.Id, stored.RawPayloadId);
        Assert.Equal(source.Id, stored.DataSourceId);
        Assert.Equal(Time.AddDays(-10), stored.SourceEventTimeUtc);
        Assert.Equal(Time.AddDays(-1), stored.SourcePublishedAtUtc);
        Assert.Equal(DateTimeKind.Utc, stored.RetrievedAtUtc.Kind);
        Assert.Equal(Time.AddHours(1), stored.AvailableAtUtc);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Database_enforces_season_competition_and_event_slot_uniqueness(bool duplicateSlot) => InTransaction(async context =>
    {
        var competition = new Competition(Guid.NewGuid(), SportId, "Synthetic", null, CompetitionType.League);
        var other = new Competition(Guid.NewGuid(), SportId, "Synthetic other", null, CompetitionType.League);
        var season = new Season(Guid.NewGuid(), other.Id, "Synthetic", null, null);
        var sportingEvent = new SportingEvent(Guid.NewGuid(), competition, null, null, SportingEventStatus.Scheduled, Time);
        var first = Participant(); var second = Participant();
        sportingEvent.AddParticipant(first, ParticipantRole.Home, 1);
        context.AddRange(competition, other, season, sportingEvent, first, second);
        await context.SaveChangesAsync();
        if (duplicateSlot)
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO canonical.\"EventParticipants\" (\"EventId\", \"ParticipantId\", \"SportId\", \"Role\", \"Position\") VALUES ({sportingEvent.Id}, {second.Id}, {SportId}, 'Side1', 1)"));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        }
        else
        {
            context.Entry(sportingEvent).Property(e => e.SeasonId).CurrentValue = season.Id;
            await Reject(() => context.SaveChangesAsync(), PostgresErrorCodes.ForeignKeyViolation);
        }
    });

    [Fact]
    public Task Database_rejects_wrong_kind_resolution_even_when_domain_is_bypassed() => InTransaction(async context =>
    {
        var source = Source(); var identity = Identity(source);
        var decision = new IdentityResolution(Guid.NewGuid(), identity, ResolutionStatus.Resolved, new(CanonicalEntityKind.Participant, Guid.NewGuid()), "reviewer", "Synthetic", Time);
        context.AddRange(source, identity);
        await context.SaveChangesAsync();
        context.Add(decision);
        context.Entry(decision).Property(d => d.CanonicalParticipantId).CurrentValue = null;
        context.Entry(decision).Property(d => d.CanonicalSportId).CurrentValue = SportId;
        await Reject(() => context.SaveChangesAsync(), PostgresErrorCodes.CheckViolation);
    });

    [Fact]
    public async Task Canonical_and_provenance_foreign_keys_never_cascade()
    {
        await using var context = fixture.CreateContext();
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT c.confdeltype::text FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace WHERE n.nspname IN ('canonical','provenance') AND c.contype = 'f'";
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync()) { Assert.Equal("r", reader.GetString(0)); count++; }
        Assert.True(count >= 20);
    }

    [Fact]
    public async Task Bs002_database_upgrades_without_losing_existing_ingestion_rows()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("upgrade_fixture")
            .WithUsername("upgrade_fixture").WithPassword(Guid.NewGuid().ToString("N")).Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await container.StartAsync(timeout.Token);
        await using var context = new BetStatsDbContext(new DbContextOptionsBuilder<BetStatsDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await context.GetService<IMigrator>().MigrateAsync("20261007234734_InitialPersistence", timeout.Token);
        var source = Source();
        var run = new IngestionRun { Id = Guid.NewGuid(), DataSourceId = source.Id, CreatedAtUtc = Time };
        var raw = new RawPayload { Id = Guid.NewGuid(), DataSourceId = source.Id, IngestionRunId = run.Id, RetrievedAtUtc = Time, CreatedAtUtc = Time,
            ContentHashSha256 = new string('c', 64), ContentType = "application/json", StorageKey = "synthetic/upgrade" };
        context.AddRange(source, run, raw);
        await context.SaveChangesAsync(timeout.Token);
        await context.Database.MigrateAsync(timeout.Token);
        context.ChangeTracker.Clear();
        Assert.Equal(source.DisplayName, (await context.DataSources.SingleAsync(timeout.Token)).DisplayName);
        Assert.Equal(run.Id, (await context.IngestionRuns.SingleAsync(timeout.Token)).Id);
        Assert.Equal(raw.StorageKey, (await context.RawPayloads.SingleAsync(timeout.Token)).StorageKey);
        Assert.Equal(4, await context.Sports.CountAsync(timeout.Token));
        Assert.Equal(2, (await context.Database.GetAppliedMigrationsAsync(timeout.Token)).Count());
        Assert.False(context.Database.HasPendingModelChanges());
    }
}
