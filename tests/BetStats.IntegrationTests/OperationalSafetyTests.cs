using System.Diagnostics;
using BetStats.Application.Datasets;
using BetStats.Application.Ingestion;
using BetStats.Application.Pipeline;
using BetStats.Application.Providers;
using BetStats.Infrastructure;
using BetStats.Infrastructure.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BetStats.IntegrationTests;

[Trait("Category", "Security")]
[Trait("Category", "Migration")]
public sealed class OperationalSafetyTests : IAsyncLifetime
{
    // Backup covers the whole database, unlike source-scoped workflow tests.
    // Each case therefore needs its own DB/RAW lifetime, not a shared class DB
    // containing manifests whose previous test already removed its temp files.
    private readonly PostgreSqlFixture fixture = new();
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();
    private static async Task Docker(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token); _ = await output; _ = await error;
        Assert.Equal(0, process.ExitCode);
    }
    [Fact]
    public async Task Sealed_restore_preserves_original_raw_receipt_and_runtime_replay_without_http()
    {
        var f = await new ProviderPublicationWorkflowTests(fixture).Create(); await using var s = f.Scenario;
        var raw = await ProviderPublicationWorkflowTests.Capture(f, complete: false);
        var published = await ProviderPublicationWorkflowTests.Publish(f, raw);
        var backup = Path.Combine(Path.GetTempPath(), "betstats-backup-" + Guid.NewGuid().ToString("N"));
        var database = "restore_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(fixture.GetConnectionString()); await admin.OpenAsync();
        try
        {
            await DevelopmentBackup.CreateAsync(s.Db, s.Get<IRawPayloadStore>(), fixture.ContainerName, "betstats_tests", backup, true, default);
            await using (var create = new NpgsqlCommand("CREATE DATABASE " + database, admin)) await create.ExecuteNonQueryAsync();
            var containerDump = "/tmp/" + database + ".dump";
            await Docker("cp", Path.Combine(backup, "database.dump"), fixture.ContainerName + ":" + containerDump);
            await Docker("exec", fixture.ContainerName, "pg_restore", "-U", "betstats_tests", "-d", database, "--exit-on-error", "--no-owner", "--no-acl", containerDump);
            var connection = new NpgsqlConnectionStringBuilder(fixture.GetConnectionString()) { Database = database };
            await using var restored = new BetStatsDbContext(new DbContextOptionsBuilder<BetStatsDbContext>().UseNpgsql(connection.ConnectionString).Options);
            await DevelopmentBackup.VerifyRestoredAsync(restored, backup, default);
            Assert.Equal((await s.Db.RawPayloads.OrderBy(r => r.Id).Select(r => r.ContentHashSha256).ToArrayAsync()),
                await restored.RawPayloads.OrderBy(r => r.Id).Select(r => r.ContentHashSha256).ToArrayAsync());
            Assert.Equal(await s.Db.Observations.CountAsync(), await restored.Observations.CountAsync());
            Assert.Equal(await s.Db.Set<ProviderPublicationRecord>().CountAsync(), await restored.Set<ProviderPublicationRecord>().CountAsync());
            var frozen = await restored.Backtests.OrderBy(r => r.Id).Select(r => r.Hash).ToArrayAsync();
            await using var roleAdmin = new NpgsqlConnection(connection.ConnectionString); await roleAdmin.OpenAsync();
            var appPassword = Guid.NewGuid().ToString("N"); var adminPassword = Guid.NewGuid().ToString("N");
            await RuntimeDatabaseRole.ConfigureAsync(roleAdmin, adminPassword, appPassword, default);
            connection.Username = "betstats_app"; connection.Password = appPassword;
            await using var app = new NpgsqlConnection(connection.ConnectionString); await app.OpenAsync();
            foreach (var sql in new[] { "CREATE TABLE canonical.unauthorized(id int)", "ALTER TABLE ingestion.\"RawPayloads\" DISABLE TRIGGER ALL", "SET session_replication_role=replica", "TRUNCATE ingestion.\"RawPayloads\"", "CREATE ROLE unauthorized" })
            {
                await using var command = new NpgsqlCommand(sql, app);
                var denied = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync()); Assert.Equal("42501", denied.SqlState);
            }
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["ConnectionStrings:BetStats"] = connection.ConnectionString, ["Ingestion:RawStoragePath"] = Path.Combine(backup, "raw") }).Build();
            await using var services = new ServiceCollection().AddLogging().AddPersistence(configuration).BuildServiceProvider();
            using var scope = services.CreateScope(); var publisher = scope.ServiceProvider.GetRequiredService<ProviderPublication>();
            Assert.Equal(published.Hash, (await publisher.VerifyAsync(published.Report.ReceiptId, false)).Hash);
            var jobs = scope.ServiceProvider.GetRequiredService<PostgreSqlPipeline>();
            var approval = new PipelineApproval("test:restore", "Replay original restored RAW without provider transport", true);
            var now = await restored.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
            var job = Guid.NewGuid(); await jobs.PlanAsync(job, new(1, PipelineKind.ProviderPublication, new(now), System.Text.Json.JsonSerializer.Serialize(published.Report.Input)), approval);
            await jobs.SetEnabledAsync(job, true, approval); var claim = (await jobs.AcquireAsync(approval))!;
            var replay = await publisher.PublishAsync(claim, published.Report.Input, approval);
            Assert.Equal(published.Report.ReceiptId, replay.Report.ReceiptId); Assert.Equal(published.Hash, replay.Hash);
            Assert.Empty(await restored.Set<ProviderRequestEvent>().ToArrayAsync());
            Assert.Equal(frozen, await restored.Backtests.OrderBy(r => r.Id).Select(r => r.Hash).ToArrayAsync());
            // Ordinary runtime capture still works under the restricted identity.
            var persistence = scope.ServiceProvider.GetRequiredService<IFootballIngestionPersistence>();
            var runtimeStore = scope.ServiceProvider.GetRequiredService<IRawPayloadStore>();
            var attempt = await persistence.BeginAsync(Guid.NewGuid(), s.Source, default);
            var bytes = System.Text.Encoding.UTF8.GetBytes(ProviderPublicationWorkflowTests.Payload().ToJsonString());
            var staged = await runtimeStore.StageAsync(bytes);
            var captured = await persistence.CaptureAsync(attempt, new(bytes, "application/json", now, ApiFootballFixtureAdapter.Profile), staged, new("999999", "2026"), default);
            await runtimeStore.FinalizeAsync(staged);
            await persistence.CompleteAsync(attempt with { Outcome = ImportOutcome.Succeeded }, default);
            Assert.NotEqual(raw, captured.Id);
            // Backup binding is checked against the original restored state before
            // this intentionally new capture; do not claim the modified DB is identical.
            var marker = Path.Combine(backup, "COMPLETE"); var seal = await File.ReadAllTextAsync(marker);
            await File.WriteAllTextAsync(marker, "unsealed");
            await Assert.ThrowsAsync<InvalidDataException>(() => DevelopmentBackup.VerifySealedAsync(backup, default));
            await File.WriteAllTextAsync(marker, seal);
            var storage = new FileSystemRawPayloadStore(Path.Combine(backup, "raw"));
            var first = (await DevelopmentBackup.VerifyFilesAsync(backup, default)).Raw[0];
            await File.WriteAllTextAsync(Path.Combine(backup, "raw", first.Key + ".raw"), "corrupted synthetic bytes");
            await Assert.ThrowsAsync<InvalidDataException>(() => DevelopmentBackup.VerifySealedAsync(backup, default));
        }
        finally
        {
            // Only the uniquely named test database/directory are removed.
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand("DROP DATABASE IF EXISTS " + database + " WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync();
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
        }
    }
    [Fact]
    public async Task Windows_development_scripts_restore_into_a_new_disposable_container()
    {
        var f = await new ProviderPublicationWorkflowTests(fixture).Create(); await using var s = f.Scenario;
        _ = await ProviderPublicationWorkflowTests.Capture(f);
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "BetStats.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var backup = Path.Combine(Path.GetTempPath(), "betstats-script-backup-" + Guid.NewGuid().ToString("N"));
        var restored = Path.Combine(Path.GetTempPath(), "betstats-script-restore-" + Guid.NewGuid().ToString("N"));
        string? container = null;
        async Task<(int Code, string Output)> Script(string file, params string[] arguments)
        {
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-File", Path.Combine(root.FullName, "eng", file) }.Concat(arguments)) start.ArgumentList.Add(arg);
            start.Environment["ConnectionStrings__BetStats"] = fixture.GetConnectionString(); start.Environment["Ingestion__RawStoragePath"] = s.Root;
            start.Environment["BETSTATS_API_FOOTBALL_ENABLED"] = "false";
            using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); await process.WaitForExitAsync(timeout.Token); _ = await error;
            return (process.ExitCode, await output);
        }
        try
        {
            var saved = await Script("backup.ps1", "-Container", fixture.ContainerName, "-User", "betstats_tests", "-Destination", backup, "-OfflineConfirmed");
            Assert.Equal(0, saved.Code);
            var restoredResult = await Script("restore.ps1", "-Backup", backup, "-Destination", restored);
            var match = System.Text.RegularExpressions.Regex.Match(restoredResult.Output, @"Restore container: (betstats-restore-[a-f0-9]{32})");
            if (match.Success) container = match.Groups[1].Value;
            Assert.Equal(0, restoredResult.Code); Assert.NotNull(container);
            Assert.Contains("Verified isolated restore", restoredResult.Output);
            _ = await DevelopmentBackup.VerifySealedAsync(restored, default);
        }
        finally
        {
            if (container is not null) await Docker("rm", "--force", container);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            if (Directory.Exists(restored)) Directory.Delete(restored, true);
        }
    }
    [Fact]
    public async Task Offline_confirmation_is_required_and_readiness_does_not_authorize_provider()
    {
        await using var db = fixture.CreateContext(); var root = Path.Combine(Path.GetTempPath(), "betstats-readiness-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSystemRawPayloadStore(root);
            await Assert.ThrowsAsync<ArgumentException>(() => DevelopmentBackup.CreateAsync(db, store, fixture.ContainerName, "betstats_tests", root + "-backup", false, default));
            var result = await InfrastructureReadiness.CheckAsync(db, store, default);
            Assert.True(result.PostgreSql); Assert.True(result.RawStorage); Assert.Equal("InfrastructureOnly", result.Scope);
            Assert.Empty(await db.Set<ProviderRequestEvent>().ToArrayAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
