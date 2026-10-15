using Microsoft.EntityFrameworkCore;
using BetStats.Application.Ingestion;
using BetStats.Infrastructure.Ingestion;

namespace BetStats.Infrastructure.Persistence;

public sealed record InfrastructureReadinessResult(bool PostgreSql, bool RawStorage, string Scope = "InfrastructureOnly");
public static class InfrastructureReadiness
{
    public static async Task<InfrastructureReadinessResult> CheckAsync(BetStatsDbContext db, IRawPayloadStore store, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var database = false; var raw = false;
        try { database = await db.Database.CanConnectAsync(timeout.Token); }
        catch (Exception e) when (e is System.Data.Common.DbException or OperationCanceledException or InvalidOperationException) { }
        try { raw = store is FileSystemRawPayloadStore file && await Task.Run(file.IsReadable, CancellationToken.None).WaitAsync(timeout.Token); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException) { }
        return new(database, raw);
    }
}
