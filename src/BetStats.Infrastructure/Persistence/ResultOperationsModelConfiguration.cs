using System.Text;
using BetStats.Application.Datasets;
using BetStats.Application.Football;
using BetStats.Domain.Coverage;
using BetStats.Domain.Football;
using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BetStats.Infrastructure.Persistence;

public sealed class ResultOperationEvent
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
internal static class ResultOperationsModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var inventory = model.Entity<ResultInventoryEvidence>();
        inventory.ToTable("ResultInventory", "coverage", t => {
            t.HasCheckConstraint("CK_ResultInventory_Claim", "\"Claim\" IN ('Unknown','Partial','Complete','Empty') AND \"Version\" > 0");
            t.HasCheckConstraint("CK_ResultInventory_Time", "\"AvailableAtUtc\" >= \"RetrievedAtUtc\" AND \"AvailableAtUtc\" <= \"RecordedAtUtc\" AND \"ValidUntilUtc\" > \"AvailableAtUtc\" AND (\"PublishedAtUtc\" IS NULL OR \"PublishedAtUtc\" <= \"RetrievedAtUtc\")");
        });
        inventory.HasKey(x => x.Id); inventory.Property(x => x.Id).ValueGeneratedNever();
        inventory.Property(x => x.Scope).HasConversion(v => Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(v)), v => CanonicalDatasetJson.Deserialize<ResultCoverageScope>(Encoding.UTF8.GetBytes(v))).HasColumnType("jsonb");
        inventory.Property(x => x.Claim).HasConversion<string>(); inventory.HasIndex(x => x.CorrectsId).IsUnique();
        inventory.HasOne<ResultInventoryEvidence>().WithMany().HasForeignKey(x => x.CorrectsId).OnDelete(DeleteBehavior.Restrict);
        inventory.HasOne<RawPayload>().WithMany().HasForeignKey(x => new { x.RawId, x.SourceId }).HasPrincipalKey(x => new { x.Id, x.DataSourceId }).OnDelete(DeleteBehavior.Restrict);
        inventory.HasOne<SourcePolicy>().WithMany().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Restrict);
        inventory.Property(x => x.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        var review = model.Entity<ResultInventoryReview>();
        review.ToTable("ResultInventoryReviews", "coverage", t => t.HasCheckConstraint("CK_ResultInventoryReviews_Sequence", "\"Sequence\" > 0"));
        review.HasKey(x => x.Id); review.Property(x => x.Id).ValueGeneratedNever(); review.HasIndex(x => new { x.EvidenceId, x.Sequence }).IsUnique();
        review.HasOne<ResultInventoryEvidence>().WithMany().HasForeignKey(x => x.EvidenceId).OnDelete(DeleteBehavior.Restrict);
        review.Property(x => x.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        var end = model.Entity<EventEndEvidence>();
        end.ToTable("EventEnds", "football", t => {
            t.HasCheckConstraint("CK_EventEnds_Version", "\"Version\" > 0 AND ((\"Version\" = 1 AND \"CorrectsId\" IS NULL) OR (\"Version\" > 1 AND \"CorrectsId\" IS NOT NULL))");
            t.HasCheckConstraint("CK_EventEnds_Time", "\"AvailableAtUtc\" >= \"RetrievedAtUtc\" AND \"AvailableAtUtc\" <= \"RecordedAtUtc\" AND (\"PublishedAtUtc\" IS NULL OR \"PublishedAtUtc\" <= \"RetrievedAtUtc\")");
        });
        end.HasKey(x => x.Id); end.Property(x => x.Id).ValueGeneratedNever(); end.HasIndex(x => x.CorrectsId).IsUnique();
        end.HasOne<EventEndEvidence>().WithMany().HasForeignKey(x => x.CorrectsId).OnDelete(DeleteBehavior.Restrict);
        end.HasOne<FootballResultObservation>().WithMany().HasForeignKey(x => x.ResultObservationId).OnDelete(DeleteBehavior.Restrict);
        end.HasOne<RawPayload>().WithMany().HasForeignKey(x => new { x.RawId, x.SourceId }).HasPrincipalKey(x => new { x.Id, x.DataSourceId }).OnDelete(DeleteBehavior.Restrict);
        end.HasOne<SourcePolicy>().WithMany().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Restrict);
        end.HasOne<IdentityResolution>().WithMany().HasForeignKey(x => x.IdentityDecisionId).OnDelete(DeleteBehavior.Restrict);
        end.Property(x => x.Value).HasConversion(v => Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(v)), v => CanonicalDatasetJson.Deserialize<EventTimeValue>(Encoding.UTF8.GetBytes(v))).HasColumnType("jsonb");
        end.Property(x => x.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        var op = model.Entity<ResultOperationEvent>();
        op.ToTable("ResultOperations", "datasets", t => {
            t.HasCheckConstraint("CK_ResultOperations_State", "\"Sequence\" > 0 AND \"Status\" IN ('Requested','Running','Failed','Cancelled','Succeeded') AND \"Fingerprint\" ~ '^[0-9a-f]{64}$' AND octet_length(\"Request\") <= 1048576 AND ((\"Status\" = 'Succeeded') = (\"SnapshotId\" IS NOT NULL)) AND (\"Status\" <> 'Running' OR \"LeaseUntilUtc\" > \"RecordedAtUtc\")");
        });
        op.HasKey(x => x.Id); op.Property(x => x.Id).ValueGeneratedNever(); op.HasIndex(x => new { x.OperationId, x.Sequence }).IsUnique();
        op.Property(x => x.Status).HasConversion<string>(); op.Property(x => x.Fingerprint).HasMaxLength(64);
        op.HasOne<FootballResultArtifact>().WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Restrict);
        op.Property(x => x.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
    }
}
