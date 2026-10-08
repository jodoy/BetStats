using BetStats.Application.Providers;
using BetStats.Domain.Sports;

namespace BetStats.Application.Ingestion;

public sealed class FootballIngestion(AuthorizedProviderExecutor executor, IFootballIngestionPersistence persistence,
    IRawPayloadStore storage, IFootballMetadataParser parser)
{
    public async Task<ImportReport> RunAsync(IContentProviderAdapter adapter, FootballImportScope scope, RequestBudget sharedBudget, CancellationToken cancellationToken = default)
    {
        var report = await persistence.BeginAsync(Guid.NewGuid(), adapter.Descriptor.DataSourceId, cancellationToken);
        try
        {
            if (!scope.IsValid) return await Finish(report with { Outcome = ImportOutcome.Failed, ErrorCode = "invalid_scope", ErrorCategory = "Configuration" });
            var result = await executor.ExecuteAsync(adapter, new(ReferenceSports.All.Single(s => s.Code == "football").Id,
                ProviderCapability.HistoricalObservations, new()), sharedBudget, cancellationToken);
            if (!result.Success) return await Finish(report with { Outcome = result.Error!.Category == ProviderErrorCategory.PermissionDenied ? ImportOutcome.Denied : ImportOutcome.Failed,
                ErrorCode = result.Error.Code, ErrorCategory = result.Error.Category.ToString() });
            var content = adapter.Content;
            if (content is null || content.Bytes.Length is 0 or > 1_048_576)
                return await Finish(report with { Outcome = ImportOutcome.Failed, ErrorCode = "invalid_content", ErrorCategory = "InvalidResponse" });
            report = report with { RetrievedPayloads = 1 };
            await persistence.EnsureCaptureAllowedAsync(report.DataSourceId, cancellationToken);
            var staged = await storage.StageAsync(content.Bytes, cancellationToken);
            var raw = await persistence.CaptureAsync(report, content, staged, cancellationToken);
            await storage.FinalizeAsync(staged, cancellationToken);
            var bytes = await storage.ReadAsync(staged, cancellationToken);
            var parsed = parser.Parse(bytes, scope, cancellationToken);
            report = report with { ParsedRecords = parsed.ParsedCount, RejectedRecords = parsed.Issues.Count };
            report = await persistence.PublishAsync(report, raw, scope, parsed, cancellationToken);
            return await Finish(report);
        }
        catch (IngestionDeniedException denied) { return await Finish(report with { Outcome = ImportOutcome.Denied, ErrorCode = denied.Code, ErrorCategory = denied.Category }); }
        catch (IngestionPersistenceException) { return await Finish(report with { Outcome = ImportOutcome.Failed, ErrorCode = "persistence_failed", ErrorCategory = "Persistence" }); }
        catch (IOException) { return await Finish(report with { Outcome = ImportOutcome.Failed, ErrorCode = "storage_failed", ErrorCategory = "Storage" }); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            using var auditTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await persistence.CompleteAsync(report with { Outcome = ImportOutcome.Interrupted, ErrorCode = "caller_cancelled", ErrorCategory = "Cancellation" }, auditTimeout.Token);
            throw;
        }
        catch (Exception)
        {
            return await Finish(report with { Outcome = ImportOutcome.Failed, ErrorCode = "ingestion_failed", ErrorCategory = "Execution" });
        }
        async Task<ImportReport> Finish(ImportReport final)
        {
            await persistence.CompleteAsync(final, cancellationToken); return final;
        }
    }
}
