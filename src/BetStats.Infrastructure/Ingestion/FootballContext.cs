using BetStats.Application.Ingestion;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Ingestion;

internal static class FootballContext
{
    public static async Task<FootballImportScope> ReadAsync(BetStatsDbContext db, RawPayload raw, FootballImportScope requested, CancellationToken token, DateTime? cutoff = null)
    {
        var binding = await db.FootballRawContexts.AsNoTracking().SingleOrDefaultAsync(x => x.RawId == raw.Id, token);
        if (binding is not null)
        {
            if (binding.SourceId != raw.DataSourceId || binding.Version != 1 || cutoff is { } t && binding.RecordedAtUtc > t) throw new InvalidDataException("Original context unavailable at cutoff.");
            return new(binding.CompetitionReference, binding.SeasonReference);
        }
        // Legacy complete publication receipts independently commit the file scope.
        // Never infer context from current canonical event projections.
        var key = FootballPublicationKeys.Batch(requested, raw.ContentHashSha256);
        if (await db.IngestionPublications.AsNoTracking().AnyAsync(x => x.RawPayloadId == raw.Id && x.DataSourceId == raw.DataSourceId && x.IsBatch && x.Key == key && (cutoff == null || x.RecordedAtUtc <= cutoff), token))
            return requested;
        throw new InvalidDataException("Original ingestion context is unavailable.");
    }
}
