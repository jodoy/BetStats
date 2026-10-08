using System.Security.Cryptography;
using BetStats.Application.Ingestion;

namespace BetStats.Infrastructure.Ingestion;

public sealed class FileSystemRawPayloadStore : IRawPayloadStore
{
    private readonly string root;
    public FileSystemRawPayloadStore(string rootPath)
    {
        if (!Path.IsPathFullyQualified(rootPath)) throw new ArgumentException("RAW storage requires an absolute path.");
        root = Path.GetFullPath(rootPath);
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BetStats.slnx")) || Directory.Exists(Path.Combine(directory.FullName, ".git")))
                throw new ArgumentException("RAW storage must be outside a repository.");
        CheckDirectories(); Directory.CreateDirectory(root); CheckDirectories();
    }
    public async Task<StoredPayload> StageAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (bytes.Length is 0 or > 1_048_576) throw new ArgumentException("Payload size is outside bounds.");
        var payload = new StoredPayload(Guid.NewGuid().ToString("N"), Convert.ToHexStringLower(SHA256.HashData(bytes.Span)), bytes.Length);
        await using var stream = new FileStream(FilePath(payload.StorageKey, ".pending"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, cancellationToken); await stream.FlushAsync(cancellationToken); stream.Flush(flushToDisk: true);
        return payload;
    }
    public async Task FinalizeAsync(StoredPayload payload, CancellationToken cancellationToken = default)
    {
        _ = await ReadAsync(payload, cancellationToken);
        var staged = FilePath(payload.StorageKey, ".pending"); var final = FilePath(payload.StorageKey, ".raw");
        if (File.Exists(final)) return;
        File.Move(staged, final, overwrite: false);
    }
    public async Task<ReadOnlyMemory<byte>> ReadAsync(StoredPayload payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length is <= 0 or > 1_048_576 || payload.Hash.Length != 64 || !payload.Hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid payload manifest.");
        var final = FilePath(payload.StorageKey, ".raw");
        var path = File.Exists(final) ? final : FilePath(payload.StorageKey, ".pending");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length != payload.Length) throw new InvalidDataException("Payload integrity failure.");
        var bytes = new byte[checked((int)payload.Length)]; await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(payload.Hash))) throw new InvalidDataException("Payload integrity failure.");
        return bytes;
    }
    public IReadOnlyList<string> InventoryStaged()
    {
        CheckDirectories(); var files = Directory.EnumerateFiles(root, "*.pending").Take(1001).ToArray();
        if (files.Length > 1000) throw new InvalidOperationException("Inventory exceeds bounded maintenance batch.");
        return files.Select(file => Path.GetFileNameWithoutExtension(file)).ToArray();
    }
    private string FilePath(string key, string extension)
    {
        if (key.Length != 32 || !key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw new ArgumentException("Invalid opaque storage key.");
        CheckDirectories(); var path = Path.Combine(root, key + extension);
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("Storage links are not supported.");
        return path;
    }
    private void CheckDirectories()
    {
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null) throw new IOException("Storage directory links are not supported.");
    }
}
