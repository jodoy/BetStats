using BetStats.Application.Governance;
using BetStats.Application.Observations;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BetStats.IntegrationTests;

public sealed class GovernancePersistenceTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTime Time = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DataSource Source() => new() { Id = Guid.NewGuid(), Code = "synthetic-" + Guid.NewGuid().ToString("N"), DisplayName = "Synthetic source", CreatedAtUtc = Time };
    private static SourcePolicy Policy(DataSource source, PermissionDecision permission = PermissionDecision.Allowed, int version = 1, DateTime? end = null) =>
        new(Guid.NewGuid(), source.Id, version, Time, end, "synthetic:terms", "synthetic:evidence", Time, [new(DataPurpose.DataRetrieval, permission)]);
    private static SourcePolicyEvaluator Evaluator(BetStatsDbContext context) => new(new SourcePolicyHistory(context));
    private static async Task<DateTime> DatabaseNow(BetStatsDbContext context) =>
        await context.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
    private async Task InTransaction(Func<BetStatsDbContext, Task> test)
    {
        await using var context = fixture.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await test(context); await transaction.RollbackAsync();
    }
    private static async Task Approve(BetStatsDbContext context, SourcePolicy policy)
    {
        policy.Approve(Guid.NewGuid(), "synthetic-reviewer", "Explicit synthetic evidence", Time, Time);
        await context.SaveChangesAsync();
    }
    private static async Task Reject(Func<Task> action, string sqlState)
    {
        var exception = await Assert.ThrowsAsync<DbUpdateException>(action);
        Assert.Equal(sqlState, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }
    [Fact]
    public Task Missing_draft_approved_and_revoked_policy_states_round_trip() => InTransaction(async context =>
    {
        var source = Source(); context.Add(source); await context.SaveChangesAsync();
        var evaluator = Evaluator(context);
        Assert.Equal(PolicyReason.MissingPolicy, (await evaluator.EvaluateAsync(source.Id, DataPurpose.DataRetrieval, await DatabaseNow(context), new())).Reason);
        var policy = Policy(source); context.Add(policy); await context.SaveChangesAsync();
        Assert.Equal(PolicyReason.Draft, (await evaluator.EvaluateAsync(source.Id, DataPurpose.DataRetrieval, await DatabaseNow(context), new())).Reason);
        await Approve(context, policy);
        var approval = policy.Audit.Single();
        Assert.True((await evaluator.EvaluateAsync(source.Id, DataPurpose.DataRetrieval, approval.RecordedAtUtc, new())).Allowed);
        Assert.Equal(PolicyReason.Draft, (await evaluator.EvaluateAsync(source.Id, DataPurpose.DataRetrieval, approval.RecordedAtUtc.AddTicks(-10), new())).Reason);
        policy.Revoke(Guid.NewGuid(), "synthetic-reviewer", "Synthetic revocation", Time.AddDays(1));
        await context.SaveChangesAsync();
        var revokedAt = policy.Audit.Max(a => a.RecordedAtUtc);
        Assert.Equal(PolicyReason.Revoked, (await evaluator.EvaluateAsync(source.Id, DataPurpose.DataRetrieval, revokedAt, new())).Reason);
        Assert.True((await evaluator.EvaluateAsync(source.Id, DataPurpose.DataRetrieval, approval.RecordedAtUtc, new())).Allowed);
        context.ChangeTracker.Clear();
        var stored = await context.SourcePolicies.Include(p => p.Permissions).Include(p => p.Audit).SingleAsync();
        Assert.Equal(9, stored.Permissions.Count); Assert.Equal(2, stored.Audit.Count);
        Assert.Equal(PolicyStatus.Revoked, stored.Status); Assert.Equal("synthetic-reviewer", stored.Reviewer);
        Assert.Equal(Time, stored.ApprovedAtUtc); Assert.Equal("synthetic:evidence", stored.EvidenceReference);
    });
    [Theory]
    [InlineData(PermissionDecision.Unknown, PolicyReason.UnknownPermission)]
    [InlineData(PermissionDecision.Denied, PolicyReason.ExplicitDenial)]
    [InlineData(PermissionDecision.Allowed, PolicyReason.Authorized)]
    public Task Internal_approval_does_not_override_purpose_rights(PermissionDecision permission, PolicyReason expected) => InTransaction(async context =>
    {
        var source = Source(); var policy = Policy(source, permission);
        context.AddRange(source, policy); await context.SaveChangesAsync(); await Approve(context, policy);
        var result = await Evaluator(context).EvaluateAsync(source.Id, DataPurpose.DataRetrieval, await DatabaseNow(context), new());
        Assert.Equal(expected, result.Reason); Assert.Equal(policy.Id, result.PolicyId);
        Assert.False((await Evaluator(context).EvaluateAsync(source.Id, DataPurpose.DataRetrieval, await DatabaseNow(context), new(Commercial: true))).Allowed);
    });
    [Fact]
    public Task Expired_policy_never_authorizes() => InTransaction(async context =>
    {
        var source = Source(); var policy = Policy(source, end: Time.AddDays(1));
        context.AddRange(source, policy); await context.SaveChangesAsync(); await Approve(context, policy);
        Assert.Equal(PolicyReason.NotEffective, (await Evaluator(context).EvaluateAsync(source.Id, DataPurpose.DataRetrieval, await DatabaseNow(context), new())).Reason);
    });

    [Fact]
    public Task Future_approval_event_time_does_not_authorize_present_usage() => InTransaction(async context =>
    {
        var source = Source(); var policy = Policy(source); context.AddRange(source, policy); await context.SaveChangesAsync();
        var future = new DateTime(2050, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        policy.Approve(Guid.NewGuid(), "reviewer", "Synthetic future event", Time, future); await context.SaveChangesAsync();
        Assert.False((await Evaluator(context).EvaluateAsync(source.Id, DataPurpose.DataRetrieval, await DatabaseNow(context), new())).Allowed);
    });
    [Fact]
    public Task Source_policy_versions_are_unique() => InTransaction(async context =>
    {
        var source = Source(); context.AddRange(source, Policy(source)); await context.SaveChangesAsync();
        context.Add(Policy(source)); await Reject(() => context.SaveChangesAsync(), PostgresErrorCodes.UniqueViolation);
    });
    [Fact]
    public Task Overlapping_approvals_fail_at_database_boundary() => InTransaction(async context =>
    {
        var source = Source(); var first = Policy(source); var second = Policy(source, version: 2);
        context.AddRange(source, first, second); await context.SaveChangesAsync(); await Approve(context, first);
        second.Approve(Guid.NewGuid(), "reviewer", "Conflicting synthetic evidence", Time, Time);
        await Reject(() => context.SaveChangesAsync(), PostgresErrorCodes.CheckViolation);
    });
    [Fact]
    public Task Revocation_permits_a_new_version_without_rewriting_history() => InTransaction(async context =>
    {
        var source = Source(); var first = Policy(source); var second = Policy(source, version: 2);
        context.AddRange(source, first, second); await context.SaveChangesAsync(); await Approve(context, first);
        first.Revoke(Guid.NewGuid(), "reviewer", "New terms require review", Time); await context.SaveChangesAsync();
        await Approve(context, second);
        var result = await Evaluator(context).EvaluateAsync(source.Id, DataPurpose.DataRetrieval, await DatabaseNow(context), new());
        Assert.True(result.Allowed); Assert.Equal(second.Id, result.PolicyId);
        Assert.Equal(3, await context.PolicyAudits.CountAsync());
    });
    [Fact]
    public Task Adjacent_half_open_approval_intervals_do_not_conflict() => InTransaction(async context =>
    {
        var source = Source(); var first = Policy(source, end: Time.AddDays(1));
        var second = new SourcePolicy(Guid.NewGuid(), source.Id, 2, Time.AddDays(1), null, "synthetic:terms", "synthetic:evidence", Time,
            [new(DataPurpose.DataRetrieval, PermissionDecision.Allowed)]);
        context.AddRange(source, first, second); await context.SaveChangesAsync(); await Approve(context, first); await Approve(context, second);
        Assert.Equal(2, (await Evaluator(context).EvaluateAsync(source.Id, DataPurpose.DataRetrieval, await DatabaseNow(context), new())).Version);
    });
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public Task Direct_sql_cannot_rewrite_policy_history(int table) => InTransaction(async context =>
    {
        var sql = table switch { 0 => "DELETE FROM governance.\"SourcePolicies\"", 1 => "DELETE FROM governance.\"PurposePermissions\"", _ => "DELETE FROM governance.\"PolicyAudits\"" };
        var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    });
    [Fact]
    public Task Ef_permission_mutation_is_rejected() => InTransaction(async context =>
    {
        var source = Source(); var policy = Policy(source);
        context.AddRange(source, policy); await context.SaveChangesAsync();
        context.Entry(policy.Permissions.First()).Property(p => p.Decision).CurrentValue = PermissionDecision.Allowed;
        context.Entry(policy.Permissions.First()).State = EntityState.Modified;
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    });
    [Fact]
    public Task Policy_foreign_keys_restrict_source_deletion() => InTransaction(async context =>
    {
        var source = Source(); context.AddRange(source, Policy(source)); await context.SaveChangesAsync();
        context.ChangeTracker.Clear(); context.Remove(await context.DataSources.SingleAsync());
        await Reject(() => context.SaveChangesAsync(), PostgresErrorCodes.ForeignKeyViolation);
    });
    [Fact]
    public Task Caller_cannot_backdate_identity_availability_through_ef_or_sql() => InTransaction(async context =>
    {
        var source = Source(); var identity = new ProviderIdentity(Guid.NewGuid(), source.Id, CanonicalEntityKind.Participant, "synthetic", Time);
        var decision = new IdentityResolution(Guid.NewGuid(), identity, ResolutionStatus.Unresolved, null, "reviewer", "Backdated event time", Time);
        context.AddRange(source, identity, decision);
        context.Entry(decision).Property(d => d.RecordedAtUtc).CurrentValue = Time;
        await context.SaveChangesAsync();
        Assert.True(decision.RecordedAtUtc > Time); Assert.Equal(DateTimeKind.Utc, decision.RecordedAtUtc.Kind);
        var history = new IdentityResolutionHistory(context);
        Assert.Null(await history.ReadLatestAsOfAsync(identity.Id, decision.RecordedAtUtc.AddTicks(-10)));
        Assert.Equal(decision.Id, (await history.ReadLatestAsOfAsync(identity.Id, decision.RecordedAtUtc))!.Id);
        var nextId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO provenance.\"IdentityResolutions\" (\"Id\", \"ProviderIdentityId\", \"DataSourceId\", \"EntityKind\", \"Status\", \"Version\", \"DecidedBy\", \"Reason\", \"DecidedAtUtc\", \"RecordedAtUtc\", \"PreviousDecisionId\", \"PreviousVersion\", \"PreviousDecidedAtUtc\") VALUES ({nextId}, {identity.Id}, {source.Id}, 'Participant', 'Ambiguous', 2, 'reviewer', 'Synthetic SQL attempt', {Time}, {Time}, {decision.Id}, 1, {Time})");
        var next = await context.IdentityResolutions.AsNoTracking().SingleAsync(d => d.Id == nextId);
        Assert.True(next.RecordedAtUtc > decision.RecordedAtUtc);
        Assert.Equal(decision.Id, (await history.ReadLatestAsOfAsync(identity.Id, decision.RecordedAtUtc))!.Id);
    });
    [Fact]
    public Task Keyset_pages_preserve_ties_cutoff_filters_and_legacy_bounds() => InTransaction(async context =>
    {
        var source = Source(); var identity = new ProviderIdentity(Guid.NewGuid(), source.Id, CanonicalEntityKind.Participant, "synthetic", Time);
        context.AddRange(source, identity);
        var ids = Enumerable.Range(1, 7).Select(i => Guid.Parse($"30000000-0000-0000-0000-{i:000000000000}")).ToArray();
        foreach (var id in ids.Reverse()) context.Add(new Observation(id, identity, null, ObservationType.DisplayName, Time, Time, Time, textValue: "Synthetic"));
        var future = new Observation(Guid.NewGuid(), identity, null, ObservationType.DisplayName, Time.AddDays(2), Time.AddDays(2), Time.AddDays(2), textValue: "Future");
        context.Add(future); await context.SaveChangesAsync();
        var history = new ObservationHistory(context, new(3));
        var query = new ObservationQuery(CanonicalEntityKind.Participant, Time.AddDays(1), dataSourceId: source.Id, providerIdentityId: identity.Id);
        var seen = new List<Guid>(); ObservationCursor? cursor = null;
        do
        {
            var page = await history.ReadPageAsOfAsync(query, 3, cursor); Assert.InRange(page.Items.Count, 1, 3);
            seen.AddRange(page.Items.Select(o => o.Id)); cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(ids, seen); Assert.Equal(7, seen.Distinct().Count()); Assert.DoesNotContain(future.Id, seen);
        await Assert.ThrowsAsync<InvalidOperationException>(() => history.ReadAsOfAsync(query));
        await Assert.ThrowsAsync<ArgumentException>(() => history.ReadPageAsOfAsync(query, 4));
        var firstPage = await history.ReadPageAsOfAsync(query, 3);
        await Assert.ThrowsAsync<ArgumentException>(() => history.ReadPageAsOfAsync(new(CanonicalEntityKind.Participant, Time.AddDays(2)), 3, firstPage.NextCursor));
        var empty = await history.ReadPageAsOfAsync(new(CanonicalEntityKind.Participant, Time.AddSeconds(-1)), 3);
        Assert.Empty(empty.Items); Assert.Null(empty.NextCursor);
    });
    [Fact]
    public async Task Bs003_upgrade_preserves_evidence_and_conservatively_records_old_availability()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("bs004_upgrade")
            .WithUsername("bs004_upgrade").WithPassword(Guid.NewGuid().ToString("N")).Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await container.StartAsync(timeout.Token);
        await using var context = new BetStatsDbContext(new DbContextOptionsBuilder<BetStatsDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        await context.GetService<IMigrator>().MigrateAsync("20261008001440_CanonicalSportsAndTemporalObservations", timeout.Token);
        var source = Source(); var identity = new ProviderIdentity(Guid.NewGuid(), source.Id, CanonicalEntityKind.Participant, "synthetic", Time);
        context.AddRange(source, identity, new Observation(Guid.NewGuid(), identity, null, ObservationType.DisplayName, Time, Time, Time, textValue: "Preserved"));
        await context.SaveChangesAsync(timeout.Token);
        var decisionId = Guid.NewGuid();
        // Insert with the actual BS-003 columns; the current EF model contains the new column.
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO provenance.\"IdentityResolutions\" (\"Id\", \"ProviderIdentityId\", \"DataSourceId\", \"EntityKind\", \"Status\", \"Version\", \"DecidedBy\", \"Reason\", \"DecidedAtUtc\") VALUES ({decisionId}, {identity.Id}, {source.Id}, 'Participant', 'Unresolved', 1, 'reviewer', 'Preserved decision', {Time})", timeout.Token);
        var beforeMigration = await DatabaseNow(context);
        await context.Database.MigrateAsync(timeout.Token);
        var decision = await context.IdentityResolutions.AsNoTracking().SingleAsync(timeout.Token);
        Assert.Equal(decisionId, decision.Id); Assert.Equal(Time, decision.DecidedAtUtc); Assert.True(decision.RecordedAtUtc >= beforeMigration);
        Assert.Null(await new IdentityResolutionHistory(context).ReadLatestAsOfAsync(identity.Id, Time));
        Assert.Equal("Preserved", (await context.Observations.SingleAsync(timeout.Token)).TextValue);
        Assert.Equal(source.Id, (await context.DataSources.SingleAsync(timeout.Token)).Id);
        Assert.Equal(4, await context.Sports.CountAsync(timeout.Token));
        Assert.False(context.Database.HasPendingModelChanges()); Assert.Empty(await context.Database.GetPendingMigrationsAsync(timeout.Token));
    }

    [Fact]
    public async Task Concurrent_overlapping_approvals_cannot_both_commit()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("approval_race")
            .WithUsername("approval_race").WithPassword(Guid.NewGuid().ToString("N")).Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await container.StartAsync(timeout.Token);
        var options = new DbContextOptionsBuilder<BetStatsDbContext>().UseNpgsql(container.GetConnectionString()).Options;
        var source = Source(); var first = Policy(source); var second = Policy(source, version: 2);
        await using (var setup = new BetStatsDbContext(options))
        {
            await setup.Database.MigrateAsync(timeout.Token);
            setup.AddRange(source, first, second); await setup.SaveChangesAsync(timeout.Token);
        }
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Attempt(Guid id)
        {
            await using var writer = new BetStatsDbContext(options);
            var policy = await writer.SourcePolicies.Include(p => p.Permissions).Include(p => p.Audit).SingleAsync(p => p.Id == id, timeout.Token);
            policy.Approve(Guid.NewGuid(), "reviewer", "Concurrent synthetic approval", Time, Time);
            await start.Task;
            try { await writer.SaveChangesAsync(timeout.Token); return true; }
            catch (DbUpdateException exception)
            {
                Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(exception.InnerException).SqlState); return false;
            }
        }
        var a = Attempt(first.Id); var b = Attempt(second.Id); start.SetResult();
        var results = await Task.WhenAll(a, b);
        Assert.Equal(1, results.Count(r => r));
        await using var check = new BetStatsDbContext(options);
        Assert.Single(await check.PolicyAudits.ToListAsync(timeout.Token));
    }

    [Fact]
    public Task Caller_cannot_backdate_policy_recorded_availability() => InTransaction(async context =>
    {
        var source = Source(); var policy = Policy(source);
        context.AddRange(source, policy); context.Entry(policy).Property(p => p.RecordedAtUtc).CurrentValue = Time;
        await context.SaveChangesAsync(); await Approve(context, policy);
        Assert.True(policy.RecordedAtUtc > Time);
        Assert.Equal(PolicyReason.MissingPolicy, (await Evaluator(context).EvaluateAsync(source.Id, DataPurpose.DataRetrieval, Time, new())).Reason);
    });

    [Fact]
    public Task Policy_audit_rejects_isolation_that_could_hide_concurrent_approvals() => InTransaction(async context =>
    {
        await context.Database.ExecuteSqlRawAsync("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ");
        var source = Source(); var policy = Policy(source); context.AddRange(source, policy); await context.SaveChangesAsync();
        policy.Approve(Guid.NewGuid(), "reviewer", "Synthetic", Time, Time);
        await Reject(() => context.SaveChangesAsync(), PostgresErrorCodes.CheckViolation);
    });

    [Fact]
    public Task Keyset_order_uses_creation_time_before_uuid() => InTransaction(async context =>
    {
        var source = Source(); var identity = new ProviderIdentity(Guid.NewGuid(), source.Id, CanonicalEntityKind.Participant, "synthetic", Time);
        var low = Guid.Parse("40000000-0000-0000-0000-000000000001"); var high = Guid.Parse("40000000-0000-0000-0000-000000000002");
        context.AddRange(source, identity,
            new Observation(low, identity, null, ObservationType.DisplayName, Time, Time, Time.AddHours(1), textValue: "Created later"),
            new Observation(high, identity, null, ObservationType.DisplayName, Time, Time, Time, textValue: "Created first"));
        await context.SaveChangesAsync();
        var history = new ObservationHistory(context);
        var query = new ObservationQuery(CanonicalEntityKind.Participant, Time.AddDays(1));
        var first = await history.ReadPageAsOfAsync(query, 1);
        Assert.Equal(high, Assert.Single(first.Items).Id);
        var second = await history.ReadPageAsOfAsync(query, 1, first.NextCursor);
        Assert.Equal(low, Assert.Single(second.Items).Id); Assert.Null(second.NextCursor);
    });
}
