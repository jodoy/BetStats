using System.Security.Cryptography;
using BetStats.Application.Datasets;
using BetStats.Application.Football;
using BetStats.Application.Ingestion;
using BetStats.Application.Providers;
using BetStats.Infrastructure.Persistence;
using BetStats.Infrastructure.Quality;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Ingestion;

public sealed record FootballImportPlan(int Version, Guid SourceId, FootballImportScope Scope, string Profile, string Hash, long Length, string Fingerprint);

public sealed class FootballImportOperations(BetStatsDbContext db, FootballIngestion ingestion,
    IFootballIngestionPersistence persistence, AuthorizedProviderExecutor executor, RequestBudget budget, TimeProvider clock, FootballImportOwnership ownership)
{
    public async Task<FootballImportPlan> PlanAsync(Guid source, string path, FootballImportScope scope, CancellationToken token = default)
    {
        if (!scope.IsValid) throw new ArgumentException("Explicit competition/season required.");
        var adapter = new AuthorizedLocalFootballAdapter(source, path, clock, persistence);
        var fetched = await executor.ExecuteAsync(adapter, new(BetStats.Application.Quality.FootballQualityRules.Football,
            ProviderCapability.HistoricalObservations, new()), budget, token);
        if (!fetched.Success || adapter.Content is null) throw new IngestionDeniedException(fetched.Error?.Code ?? "local_read_failed");
        var hash = Convert.ToHexStringLower(SHA256.HashData(adapter.Content.Bytes.Span));
        var fingerprint = CanonicalDatasetJson.Fingerprint(new { Version = 1, SourceId = source, Scope = scope, Profile = HistoricalFootballCsvParser.Version, Hash = hash });
        return new(1, source, scope, HistoricalFootballCsvParser.Version, hash, adapter.Content.Bytes.Length, fingerprint);
    }

    public Task<FootballImportOperation?> InspectAsync(Guid operation, CancellationToken token = default) =>
        db.FootballImportOperations.AsNoTracking().Where(x => x.OperationId == operation).OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(token);

    public async Task<ImportReport> RunAsync(Guid operation, FootballImportPlan expected, string path, string actor, string reason,
        bool recover = false, CancellationToken token = default)
    {
        QualityPersistence.Operator(actor, reason);
        if (operation == Guid.Empty) throw new ArgumentException("Operation UUID required.");
        if (expected.Version != 1 || expected.Profile != HistoricalFootballCsvParser.Version || !expected.Scope.IsValid || expected.Length is < 1 or > 1_048_576)
            throw new ArgumentException("Supported bounded import plan required.");
        await persistence.EnsureParsingAllowedAsync(expected.SourceId, token);
        // Session lock spans capture and publication transactions. A recovery cannot
        // overtake a live process, even if the recorded lease expires during a pause.
        await db.Database.OpenConnectionAsync(token);
        var locked = false;
        try
        {
            locked = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock(hashtextextended({expected.SourceId.ToString("D")}, 9013)) AS \"Value\"").SingleAsync(token);
            if (!locked) throw new InvalidOperationException("Source import owner is active.");
            // A separate operation lock prevents the same operation UUID racing across sources.
            await using var claim = await db.Database.BeginTransactionAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({operation.ToString("D")}, 9014))", token);
            var previous = await InspectAsync(operation, token); var now = await QualityPersistence.Now(db, token);
            if (previous is not null && (previous.Fingerprint != expected.Fingerprint || previous.SourceId != expected.SourceId)) throw new InvalidOperationException("Operation fingerprint mismatch.");
            if (previous?.Status == ResultOperationStatus.Succeeded)
            {
                await claim.CommitAsync(token);
                return new(previous.AttemptId ?? operation, null, expected.SourceId, ImportOutcome.Reused);
            }
            if (previous is not null && (!recover || previous.Status == ResultOperationStatus.Running && previous.LeaseUntilUtc > now))
                throw new InvalidOperationException("Explicit recovery of a failed or expired owner required.");
            var owner = Guid.NewGuid(); var sequence = (previous?.Sequence ?? 0) + 1;
            db.FootballImportOperations.Add(new() { Id = Guid.NewGuid(), OperationId = operation, SourceId = expected.SourceId, Sequence = sequence,
                Status = ResultOperationStatus.Running, Fingerprint = expected.Fingerprint, OwnerToken = owner, LeaseUntilUtc = now.AddMinutes(10), OperatorId = actor, Reason = reason });
            await db.SaveChangesAsync(token); await claim.CommitAsync(token); await claim.DisposeAsync();
            ownership.OperationId = operation; ownership.OwnerToken = owner;
            ImportReport report;
            try
            {
                // Pin exactly the bytes authorized and fingerprinted for this attempt.
                var adapter = new AuthorizedLocalFootballAdapter(expected.SourceId, path, clock, persistence);
                var fetched = await executor.ExecuteAsync(adapter, new(BetStats.Application.Quality.FootballQualityRules.Football,
                    ProviderCapability.HistoricalObservations, new()), budget, token);
                if (!fetched.Success || adapter.Content is null) throw new IngestionDeniedException(fetched.Error?.Code ?? "local_read_failed");
                if (Convert.ToHexStringLower(SHA256.HashData(adapter.Content.Bytes.Span)) != expected.Hash || adapter.Content.Bytes.Length != expected.Length ||
                    CanonicalDatasetJson.Fingerprint(new { Version = 1, SourceId = expected.SourceId, Scope = expected.Scope, Profile = HistoricalFootballCsvParser.Version, Hash = expected.Hash }) != expected.Fingerprint)
                    throw new InvalidDataException("Local content differs from approved plan.");
                report = await ingestion.RunAsync(new PinnedAdapter(adapter.Descriptor, adapter.Content), expected.Scope, budget, token);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or IngestionDeniedException or OperationCanceledException)
            {
                report = new(ownership.AttemptId ?? operation, null, expected.SourceId, error is OperationCanceledException ? ImportOutcome.Interrupted : ImportOutcome.Failed,
                    ErrorCode: error is IngestionDeniedException denied ? denied.Code : "import_interrupted_or_content_changed");
            }
            db.ChangeTracker.Clear();
            await using var terminal = await db.Database.BeginTransactionAsync(CancellationToken.None);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({operation.ToString("D")}, 9014))", CancellationToken.None);
            var current = await InspectAsync(operation, CancellationToken.None); var end = await QualityPersistence.Now(db, CancellationToken.None);
            if (current is null || !OperationFencing.Owns(current.Status, current.OwnerToken, owner, current.LeaseUntilUtc, end))
                throw new InvalidOperationException("Import owner fenced.");
            db.FootballImportOperations.Add(new() { Id = Guid.NewGuid(), OperationId = operation, SourceId = expected.SourceId, Sequence = sequence + 1,
                Status = report.Outcome is ImportOutcome.Succeeded or ImportOutcome.Reused ? ResultOperationStatus.Succeeded : report.Outcome == ImportOutcome.Interrupted ? ResultOperationStatus.Cancelled : ResultOperationStatus.Failed,
                Fingerprint = expected.Fingerprint, OwnerToken = owner, AttemptId = report.AttemptId, OperatorId = actor, Reason = reason, FailureCode = report.ErrorCode });
            await db.SaveChangesAsync(CancellationToken.None); await terminal.CommitAsync(CancellationToken.None);
            return report;
        }
        finally
        {
            ownership.OperationId = null; ownership.OwnerToken = Guid.Empty; ownership.AttemptId = null;
            if (locked) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock(hashtextextended({expected.SourceId.ToString("D")}, 9013))", CancellationToken.None);
            await db.Database.CloseConnectionAsync();
        }
    }

    private sealed class PinnedAdapter(ProviderDescriptor descriptor, RetrievedContent content) : IContentProviderAdapter
    {
        public ProviderDescriptor Descriptor => descriptor;
        public RetrievedContent? Content { get; private set; }
        public ConfigurationValidation ValidateConfiguration() => new(true, []);
        public Task<ProviderResult> ExecuteAsync(ProviderRequest request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Content = content; return Task.FromResult(new ProviderResult(true)); }
    }
}
