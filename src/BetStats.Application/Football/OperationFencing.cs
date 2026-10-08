namespace BetStats.Application.Football;

public static class OperationFencing
{
    public static bool Owns(ResultOperationStatus status, Guid currentOwner, Guid claimant, DateTime? leaseUntil, DateTime databaseNow) =>
        status == ResultOperationStatus.Running && claimant != Guid.Empty && currentOwner == claimant && leaseUntil is { } until && until > databaseNow;
    public static bool ValidLease(TimeSpan lease) => lease >= TimeSpan.FromSeconds(1) && lease <= TimeSpan.FromMinutes(30);
}
