using BetStats.Application.Providers;
using BetStats.Domain.Sports;

namespace BetStats.Application.Ingestion;

public sealed record FootballImportScope(string CompetitionReference, string SeasonReference)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(CompetitionReference) && CompetitionReference.Length <= 50 &&
        !string.IsNullOrWhiteSpace(SeasonReference) && SeasonReference.Length <= 50;
}
public sealed record FootballMatchRecord(int Row, string CompetitionReference, string SeasonReference,
    DateOnly MatchDate, string HomeReference, string AwayReference, string HomeName, string AwayName,
    string MatchReference, bool CompositeMatchReference, SportingEventStatus? Status,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] FootballResultInput? Result = null);
public sealed record FootballResultInput(BetStats.Domain.Football.FootballResultValue Value, DateTime? PublishedAtUtc);
public sealed record ImportIssue(int Row, string Code);
public sealed record FootballParseResult(int ParsedCount, IReadOnlyList<FootballMatchRecord> Records, IReadOnlyList<ImportIssue> Issues, bool CompletePayload = true,
    string ParserVersion = "metadata-v1");
public interface IFootballMetadataParser
{
    FootballParseResult Parse(ReadOnlyMemory<byte> bytes, FootballImportScope scope, CancellationToken cancellationToken = default);
}
public sealed record RetrievedContent(ReadOnlyMemory<byte> Bytes, string ContentType, DateTime RetrievedAtUtc);
public interface IContentProviderAdapter : IProviderAdapter
{
    RetrievedContent? Content { get; }
}
public sealed record StoredPayload(string StorageKey, string Hash, long Length);
public interface IRawPayloadStore
{
    Task<StoredPayload> StageAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);
    Task FinalizeAsync(StoredPayload payload, CancellationToken cancellationToken = default);
    Task<ReadOnlyMemory<byte>> ReadAsync(StoredPayload payload, CancellationToken cancellationToken = default);
    IReadOnlyList<string> InventoryStaged();
}
public enum ImportOutcome { Running, Succeeded, Partial, Denied, Failed, Interrupted, Reused }
public sealed record ImportReport(Guid AttemptId, Guid? RunId, Guid DataSourceId, ImportOutcome Outcome,
    int RetrievedPayloads = 0, int ParsedRecords = 0, int AcceptedRecords = 0, int RejectedRecords = 0,
    int UnresolvedIdentities = 0, string? ErrorCode = null, Guid? PolicyId = null, string? ErrorCategory = null);
public sealed record RawCapture(Guid Id, StoredPayload Object, DateTime RetrievedAtUtc, DateTime CreatedAtUtc, DateTime RecordedAtUtc);
public sealed class IngestionDeniedException(string code, string category = "Authorization") : Exception(code)
{
    public string Code { get; } = code;
    public string Category { get; } = category;
}
public sealed class IngestionPersistenceException : Exception
{
    public IngestionPersistenceException() : base("Ingestion persistence failed.") { }
}
public interface IFootballIngestionPersistence
{
    Task<ImportReport> BeginAsync(Guid attemptId, Guid sourceId, CancellationToken cancellationToken);
    Task EnsureCaptureAllowedAsync(Guid sourceId, CancellationToken cancellationToken);
    Task<RawCapture> CaptureAsync(ImportReport attempt, RetrievedContent content, StoredPayload payload, FootballImportScope scope, CancellationToken cancellationToken);
    Task<ImportReport> PublishAsync(ImportReport attempt, RawCapture raw, FootballImportScope scope, FootballParseResult parsed, CancellationToken cancellationToken);
    Task CompleteAsync(ImportReport report, CancellationToken cancellationToken);
}
