using BetStats.Application.Football;
using BetStats.Application.Ingestion;
using BetStats.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Ingestion;

// Scoped to one operator execution. Publication checks the owner inside its transaction.
public sealed class FootballImportOwnership
{
    public Guid? OperationId { get; set; }
    public Guid OwnerToken { get; set; }
    public Guid? AttemptId { get; set; }
    public async Task EnsureAsync(BetStatsDbContext db, CancellationToken token)
    {
        if (OperationId is not { } operation) return; // Reconciliation has its own audit and source lock.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({operation.ToString("D")}, 9014))", token);
        var current = await db.FootballImportOperations.AsNoTracking().Where(x => x.OperationId == operation).OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(token);
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);
        if (current is null || !OperationFencing.Owns(current.Status, current.OwnerToken, OwnerToken, current.LeaseUntilUtc, now))
            throw new IngestionDeniedException("import_owner_fenced", "Concurrency");
    }
}
