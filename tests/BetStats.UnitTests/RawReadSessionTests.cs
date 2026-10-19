using System.Security.Cryptography;
using BetStats.Application.Ingestion;

namespace BetStats.UnitTests;

public sealed class RawReadSessionTests
{
    private sealed class Store : IRawPayloadStore
    {
        public byte[] Bytes = [1, 2, 3];
        public int Reads;
        public Task<ReadOnlyMemory<byte>> ReadAsync(StoredPayload payload, CancellationToken token = default)
        { Reads++; return Task.FromResult<ReadOnlyMemory<byte>>(Bytes); }
        public Task<StoredPayload> StageAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default) => throw new NotSupportedException();
        public Task FinalizeAsync(StoredPayload payload, CancellationToken token = default) => throw new NotSupportedException();
        public IReadOnlyList<string> InventoryStaged() => [];
    }
    private static StoredPayload Manifest(Store store, string key = "synthetic") => new(key, Convert.ToHexStringLower(SHA256.HashData(store.Bytes)), store.Bytes.Length);
    [Fact]
    public async Task One_read_is_deduplicated_but_next_session_verifies_again()
    {
        var store = new Store(); var payload = Manifest(store); var session = new RawReadSession();
        _ = await session.ReadAsync(store, payload); _ = await session.ReadAsync(store, payload); Assert.Equal(1, store.Reads);
        store.Bytes = [3, 2, 1];
        await Assert.ThrowsAsync<InvalidDataException>(() => new RawReadSession().ReadAsync(store, payload));
        Assert.Equal(2, store.Reads);
    }
    [Fact]
    public async Task Failed_integrity_is_not_cached_and_descriptor_binding_is_complete()
    {
        var store = new Store(); var payload = Manifest(store); var session = new RawReadSession();
        await Assert.ThrowsAsync<InvalidDataException>(() => session.ReadAsync(store, payload with { Length = 2 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => session.ReadAsync(store, payload with { Hash = new string('0', 64) }));
        _ = await session.ReadAsync(store, payload); Assert.Equal(3, store.Reads);
        _ = await session.ReadAsync(store, payload with { StorageKey = "another" }); Assert.Equal(4, store.Reads);
    }
    [Fact]
    public async Task Retention_is_bounded_and_cancellation_is_observed_on_reuse()
    {
        var store = new Store { Bytes = new byte[1_048_576] }; var session = new RawReadSession();
        for (var i = 0; i < 17; i++) _ = await session.ReadAsync(store, Manifest(store, i.ToString()));
        _ = await session.ReadAsync(store, Manifest(store, "16")); Assert.Equal(18, store.Reads);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadAsync(store, Manifest(store, "0"), cancelled.Token));
        Assert.Equal(18, store.Reads);
    }
}
