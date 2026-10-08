using BetStats.Application.Ingestion;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Ingestion;

public sealed record RecoveryItem(string StorageKey, string Outcome);
public sealed class IngestionRecovery(BetStatsDbContext context, IRawPayloadStore storage, IFootballIngestionPersistence persistence)
{
    // Explicit operator operation, never a background cleanup or unauthorized purge.
    public async Task<IReadOnlyList<RecoveryItem>> ReconcileStagedAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<RecoveryItem>();
        foreach (var key in storage.InventoryStaged())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = await context.RawPayloads.AsNoTracking().Where(r => r.StorageKey == key).Take(2).ToListAsync(cancellationToken);
            if (rows.Count != 1 || rows[0].ByteLength is null) { results.Add(new(key, rows.Count == 0 ? "orphan_requires_authorized_maintenance" : "ambiguous_manifest")); continue; }
            var raw = rows[0];
            try
            {
                await persistence.EnsureCaptureAllowedAsync(raw.DataSourceId, cancellationToken);
                await storage.FinalizeAsync(new(key, raw.ContentHashSha256, raw.ByteLength!.Value), cancellationToken);
                results.Add(new(key, "finalized_verified"));
            }
            catch (IngestionDeniedException) { results.Add(new(key, "authorization_denied")); }
            catch (IOException) { results.Add(new(key, "integrity_or_storage_failure")); }
        }
        return results;
    }
    public async Task MarkInterruptedAsync(Guid attemptId, CancellationToken cancellationToken = default)
    {
        // Operator must establish that the owner is stopped; no lease/automatic timeout guess.
        var started = await context.IngestionAuditEvents.AsNoTracking().SingleAsync(a => a.AttemptId == attemptId && a.Sequence == 1, cancellationToken);
        var rawCount = started.RunId is { } runId ? await context.RawPayloads.CountAsync(r => r.IngestionRunId == runId, cancellationToken) : 0;
        var publication = started.RunId is { } id ? await context.IngestionPublications.AsNoTracking().SingleOrDefaultAsync(p => p.RunId == id && p.IsBatch, cancellationToken) : null;
        await persistence.CompleteAsync(new(attemptId, started.RunId, started.DataSourceId,
            publication is null ? ImportOutcome.Interrupted : ImportOutcome.Succeeded,
            RetrievedPayloads: rawCount, AcceptedRecords: publication?.AcceptedRecords ?? 0,
            ErrorCode: publication is null ? "operator_confirmed_interruption" : "recovered_publication_receipt"), cancellationToken);
    }
}
