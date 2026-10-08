using BetStats.Application.Ingestion;

namespace BetStats.Infrastructure.Persistence.Entities;

public sealed class IngestionAuditEvent
{
    public Guid Id { get; init; }
    public Guid AttemptId { get; init; }
    public int Sequence { get; init; }
    public Guid? RunId { get; init; }
    // Requested UUID is retained even when no source exists; RunId has a real FK.
    public Guid DataSourceId { get; init; }
    public ImportOutcome Outcome { get; init; }
    public DateTime AtUtc { get; init; }
    public DateTime RecordedAtUtc { get; private set; }
    public int RetrievedPayloads { get; init; }
    public int ParsedRecords { get; init; }
    public int AcceptedRecords { get; init; }
    public int RejectedRecords { get; init; }
    public int UnresolvedIdentities { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorCategory { get; init; }
    public Guid? PolicyId { get; init; }
    public Guid? ApprovalAuditId { get; init; }
    public string[] Issues { get; init; } = [];
}
public sealed class IngestionPublication
{
    public Guid Id { get; init; }
    public Guid DataSourceId { get; init; }
    public required string Key { get; init; }
    public Guid RawPayloadId { get; init; }
    public Guid RunId { get; init; }
    public int AcceptedRecords { get; init; }
    public bool IsBatch { get; init; }
    public DateTime RecordedAtUtc { get; private set; }
}
