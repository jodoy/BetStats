using System.Security.Cryptography;

namespace BetStats.Application.Ingestion;

// Explicit ownership: construct once for a single read operation and discard it.
// This contains bytes, not source authorization; callers must authorize each use.
public sealed class RawReadSession
{
    private readonly Dictionary<StoredPayload, ReadOnlyMemory<byte>> verified = [];
    public async Task<ReadOnlyMemory<byte>> ReadAsync(IRawPayloadStore store, StoredPayload payload, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (payload.Length is <= 0 or > 1_048_576 || payload.Hash.Length != 64 || !payload.Hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid RAW read manifest.");
        if (verified.TryGetValue(payload, out var bytes)) return bytes;
        bytes = await store.ReadAsync(payload, token);
        if (bytes.Length != payload.Length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes.Span), Convert.FromHexString(payload.Hash)))
            throw new InvalidDataException("RAW integrity failure.");
        // At most 16 MiB of verified payload bytes retained by the operation.
        if (verified.Count < 16) verified.Add(payload, bytes);
        return bytes;
    }
}
