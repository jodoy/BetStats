using BetStats.Application.Football;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BetStats.Infrastructure.Persistence;

public sealed class FootballImportOperation
{
    public Guid Id { get; init; }
    public Guid OperationId { get; init; }
    public Guid SourceId { get; init; }
    public int Sequence { get; init; }
    public ResultOperationStatus Status { get; init; }
    public required string Fingerprint { get; init; }
    public Guid OwnerToken { get; init; }
    public DateTime? LeaseUntilUtc { get; init; }
    public Guid? AttemptId { get; init; }
    public required string OperatorId { get; init; }
    public required string Reason { get; init; }
    public string? FailureCode { get; init; }
    public DateTime RecordedAtUtc { get; private set; }
}

internal static class FootballImportOperationConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var op = model.Entity<FootballImportOperation>();
        op.ToTable("FootballImportOperations", "ingestion", t => t.HasCheckConstraint("CK_FootballImportOperations_State",
            "\"Sequence\" > 0 AND \"Fingerprint\" ~ '^[0-9a-f]{64}$' AND \"Status\" IN ('Running','Failed','Cancelled','Succeeded') AND (\"Status\" <> 'Running' OR \"LeaseUntilUtc\" > \"RecordedAtUtc\")"));
        op.HasKey(x => x.Id); op.Property(x => x.Id).ValueGeneratedNever();
        op.HasIndex(x => new { x.OperationId, x.Sequence }).IsUnique();
        op.Property(x => x.Status).HasConversion<string>(); op.Property(x => x.Fingerprint).HasMaxLength(64);
        op.Property(x => x.OperatorId).HasMaxLength(200); op.Property(x => x.Reason).HasMaxLength(500);
        op.Property(x => x.FailureCode).HasMaxLength(100);
        op.HasOne<DataSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
        op.Property(x => x.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
    }
}
