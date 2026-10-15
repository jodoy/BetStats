using System.Security.AccessControl;
using System.Security.Principal;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using BetStats.Application.Ingestion;
using BetStats.Infrastructure.Ingestion;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Persistence;

public sealed record BackupRaw(Guid Id, Guid? RunId, Guid SourceId, string Key, string Hash, long Length);
public sealed record DevelopmentBackupManifest(int Version, DateTime SnapshotUtc, string Snapshot, string DumpHash, BackupRaw[] Raw);

// Development offline procedure. The caller must stop all application processes
// and filesystem maintenance first. SHARE locks block database DML for the entire
// exported-snapshot/dump/copy interval; pg_dump uses precisely that snapshot.
public static class DevelopmentBackup
{
    public static async Task CreateAsync(BetStatsDbContext db, IRawPayloadStore store, string container, string user,
        string destination, bool offlineConfirmed, CancellationToken token)
    {
        if (!offlineConfirmed || !Path.IsPathFullyQualified(destination) || Directory.Exists(destination))
            throw new ArgumentException("Offline confirmation and a new absolute backup directory required.");
        ValidateName(container); ValidateName(user);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token);
        // Acquired before exporting/reading: no application table writes can commit
        // until the sealed backup is complete. Lock timeout fails closed.
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout='10s'; SET LOCAL statement_timeout='10min'", token);
        var tables = await db.Database.SqlQueryRaw<string>("SELECT quote_ident(schemaname)||'.'||quote_ident(tablename) AS \"Value\" FROM pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema') ORDER BY schemaname,tablename").ToArrayAsync(token);
        foreach (var table in tables)
        {
            // Identifiers originate exclusively from pg_catalog quote_ident.
            var sql = "LOCK TABLE " + table + " IN SHARE MODE";
            await db.Database.ExecuteSqlRawAsync(sql, token);
        }
        var snapshot = await db.Database.SqlQueryRaw<string>("SELECT pg_export_snapshot() AS \"Value\"").SingleAsync(token);
        var now = await db.Database.SqlQueryRaw<DateTime>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);
        var rows = await db.RawPayloads.AsNoTracking().OrderBy(r => r.Id).Select(r => new BackupRaw(r.Id, r.IngestionRunId, r.DataSourceId, r.StorageKey, r.ContentHashSha256, r.ByteLength ?? 0)).ToArrayAsync(token);
        Directory.CreateDirectory(destination);
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Backup owner unavailable.");
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(owner);
            acl.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(destination).SetAccessControl(acl);
        }
        else File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Path.Combine(destination, "raw"));
        _ = new FileSystemRawPayloadStore(Path.Combine(destination, "raw"));
        // Preserve referenced originals even if finalization had not happened yet.
        foreach (var row in rows)
        {
            var bytes = await store.ReadAsync(new(row.Key, row.Hash, row.Length), token);
            await File.WriteAllBytesAsync(Path.Combine(destination, "raw", row.Key + ".raw"), bytes.ToArray(), token);
        }
        var database = db.Database.GetDbConnection().Database;
        var dump = Path.Combine(destination, "database.dump");
        await DockerDump(container, user, database, snapshot, dump, token);
        var manifest = new DevelopmentBackupManifest(1, now, snapshot, await Hash(dump, token), rows);
        await File.WriteAllTextAsync(Path.Combine(destination, "manifest.json"), JsonSerializer.Serialize(manifest), token);
        await VerifyFilesAsync(destination, token);
        // Sealing is last; an interrupted directory without this marker is invalid.
        await File.WriteAllTextAsync(Path.Combine(destination, "COMPLETE"), await Hash(Path.Combine(destination, "manifest.json"), token), token);
        await tx.CommitAsync(token);
    }

    public static async Task<DevelopmentBackupManifest> VerifyFilesAsync(string directory, CancellationToken token)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (new FileInfo(manifestPath).Length > 16_777_216) throw new InvalidDataException("Backup manifest exceeds bound.");
        var m = JsonSerializer.Deserialize<DevelopmentBackupManifest>(await File.ReadAllTextAsync(manifestPath, token)) ?? throw new InvalidDataException("Missing manifest.");
        if (!Directory.Exists(Path.Combine(directory, "raw")) || m.Version != 1 || m.SnapshotUtc.Kind != DateTimeKind.Utc ||
            m.Raw.Length > 100000 || m.Raw.Any(r => r.Id == Guid.Empty || r.SourceId == Guid.Empty) || m.Raw.Select(r => r.Id).Distinct().Count() != m.Raw.Length ||
            m.Raw.Select(r => r.Key).Distinct().Count() != m.Raw.Length || await Hash(Path.Combine(directory, "database.dump"), token) != m.DumpHash)
            throw new InvalidDataException("Backup integrity failure.");
        var store = new FileSystemRawPayloadStore(Path.Combine(directory, "raw"));
        foreach (var row in m.Raw) _ = await store.ReadAsync(new(row.Key, row.Hash, row.Length), token);
        return m;
    }

    public static async Task VerifyRestoredAsync(BetStatsDbContext db, string directory, CancellationToken token)
    {
        var m = await VerifySealedAsync(directory, token);
        var actual = await db.RawPayloads.AsNoTracking().OrderBy(r => r.Id).Select(r => new BackupRaw(r.Id, r.IngestionRunId, r.DataSourceId, r.StorageKey, r.ContentHashSha256, r.ByteLength ?? 0)).ToArrayAsync(token);
        if (!m.Raw.SequenceEqual(actual)) throw new InvalidDataException("Restored DB/RAW manifest binding differs.");
        if (db.Database.HasPendingModelChanges()) throw new InvalidDataException("Restored model differs.");
    }

    public static async Task<DevelopmentBackupManifest> VerifySealedAsync(string directory, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(directory) || new DirectoryInfo(directory).LinkTarget is not null)
            throw new InvalidDataException("Absolute backup directory without links required.");
        if (await File.ReadAllTextAsync(Path.Combine(directory, "COMPLETE"), token) != await Hash(Path.Combine(directory, "manifest.json"), token))
            throw new InvalidDataException("Unsealed backup.");
        return await VerifyFilesAsync(directory, token);
    }

    private static async Task<string> Hash(string file, CancellationToken token)
    {
        if (new FileInfo(file).LinkTarget is not null) throw new IOException("Backup links rejected.");
        await using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }
    private static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 100 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) throw new ArgumentException("Invalid container/user name.");
    }
    private static async Task DockerDump(string container, string user, string database, string snapshot, string path, CancellationToken token)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "exec", container, "pg_dump", "-U", user, "-d", database, "--format=custom", "--no-owner", "--no-acl", "--snapshot=" + snapshot }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Backup tool unavailable.");
        using var cancellation = token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var errors = process.StandardError.ReadToEndAsync(token);
        await using (var output = File.Create(path)) await process.StandardOutput.BaseStream.CopyToAsync(output, token);
        await process.WaitForExitAsync(token); _ = await errors;
        if (process.ExitCode != 0) throw new IOException("Database dump failed; unsealed backup must not be used.");
    }
}
