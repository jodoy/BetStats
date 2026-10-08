using BetStats.Application.Football;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BetStats.Infrastructure.Persistence;

public sealed class BacktestArtifact
{
    public Guid Id { get; init; }
    public Guid DatasetId { get; init; }
    public required string Hash { get; init; }
    public required byte[] Content { get; init; }
    public DateTime RecordedAtUtc { get; private set; }
}
public sealed class BacktestOperationEvent
{
    public Guid Id { get; init; }
    public Guid OperationId { get; init; }
    public int Sequence { get; init; }
    public ResultOperationStatus Status { get; init; }
    public required string Fingerprint { get; init; }
    public required byte[] Request { get; init; }
    public Guid OwnerToken { get; init; }
    public DateTime? LeaseUntilUtc { get; init; }
    public Guid? SnapshotId { get; init; }
    public string? FailureCode { get; init; }
    public required string OperatorId { get; init; }
    public required string Reason { get; init; }
    public DateTime RecordedAtUtc { get; private set; }
}
internal static class BacktestModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var a = model.Entity<BacktestArtifact>();
        a.ToTable("Backtests", "evaluation", t => t.HasCheckConstraint("CK_Backtests_Content", "\"Hash\" ~ '^[0-9a-f]{64}$' AND octet_length(\"Content\") BETWEEN 1 AND 16777216"));
        a.HasKey(x => x.Id); a.Property(x => x.Id).ValueGeneratedNever(); a.HasIndex(x => x.Hash).IsUnique(); a.Property(x => x.Hash).HasMaxLength(64);
        a.HasOne<FootballResultArtifact>().WithMany().HasForeignKey(x => x.DatasetId).OnDelete(DeleteBehavior.Restrict);
        a.Property(x => x.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        var op = model.Entity<BacktestOperationEvent>();
        op.ToTable("BacktestOperations", "evaluation", t => t.HasCheckConstraint("CK_BacktestOperations_State", "\"Sequence\" > 0 AND \"Status\" IN ('Requested','Running','Failed','Cancelled','Succeeded') AND \"Fingerprint\" ~ '^[0-9a-f]{64}$' AND octet_length(\"Request\") BETWEEN 1 AND 1048576 AND ((\"Status\" = 'Succeeded') = (\"SnapshotId\" IS NOT NULL)) AND (\"Status\" <> 'Running' OR \"LeaseUntilUtc\" > \"RecordedAtUtc\")"));
        op.HasKey(x => x.Id); op.Property(x => x.Id).ValueGeneratedNever(); op.HasIndex(x => new { x.OperationId, x.Sequence }).IsUnique();
        op.Property(x => x.Status).HasConversion<string>(); op.Property(x => x.Fingerprint).HasMaxLength(64);
        op.HasOne<BacktestArtifact>().WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Restrict);
        op.Property(x => x.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
    }
}
