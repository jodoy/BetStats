namespace BetStats.Application.Football;

public enum ResultOperationStatus { Requested, Running, Failed, Cancelled, Succeeded }
public sealed record ResultOperationRequest(Guid OperationId, FootballResultDatasetRequest Dataset, string OperatorId, string Reason, bool Approved);
public sealed record ResultOperationResult(Guid OperationId, ResultOperationStatus Status, int Sequence, Guid? SnapshotId, string? Hash, string? FailureCode, string Fingerprint);
public sealed record ResultRecoveryRequest(Guid OperationId, string ExpectedFingerprint, string OperatorId, string Reason, bool Approved);
public interface IResultDatasetOperations
{
    Task<ResultOperationResult> BuildAsync(ResultOperationRequest request, CancellationToken token = default);
    Task<ResultOperationResult> RecoverAsync(ResultRecoveryRequest request, CancellationToken token = default);
    Task<ResultOperationResult> InspectAsync(Guid operationId, CancellationToken token = default);
    Task<FootballResultVerification> VerifyAsync(Guid snapshotId, bool deep = false, CancellationToken token = default);
}
