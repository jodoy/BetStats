using System.Text;
using BetStats.Application.Datasets;
using BetStats.Domain.Coverage;
using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Domain.Governance;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BetStats.Infrastructure.Persistence;

internal static class CoverageModelConfiguration
{
    private static string Encode<T>(T value) => Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(value));
    private static T Decode<T>(string value) => CanonicalDatasetJson.Deserialize<T>(Encoding.UTF8.GetBytes(value));
    public static void Configure(ModelBuilder model)
    {
        var evidence = model.Entity<CoverageEvidence>();
        evidence.ToTable("Evidence", "coverage", t => {
            t.HasCheckConstraint("CK_CoverageEvidence_Version", "\"Version\" > 0 AND \"AvailableAtUtc\" >= \"RetrievedAtUtc\" AND \"AvailableAtUtc\" <= \"RecordedAtUtc\" AND \"ValidUntilUtc\" > \"AvailableAtUtc\"");
            t.HasCheckConstraint("CK_CoverageEvidence_Hash", "\"RawHash\" ~ '^[0-9a-f]{64}$' AND length(btrim(\"EvidenceReference\")) > 0 AND cardinality(\"SupportingObservationIds\") <= 1000");
            t.HasCheckConstraint("CK_CoverageEvidence_Claim", "\"Claim\" IN ('Unknown','Partial','VerifiedComplete','VerifiedEmpty') AND \"Basis\" IN ('ManualStatement','OwnedFixtureInventory')");
        });
        evidence.HasKey(e => e.Id); evidence.Property(e => e.Id).ValueGeneratedNever();
        evidence.Property(e => e.Scope).HasConversion(s => Encode(s), s => Decode<CoverageScope>(s)).HasColumnType("jsonb");
        evidence.Property(e => e.Claim).HasConversion<string>(); evidence.Property(e => e.Basis).HasConversion<string>();
        evidence.HasIndex(e => new { e.SourceId, e.RecordedAtUtc, e.Id });
        evidence.HasOne<RawPayload>().WithMany().HasForeignKey(e => new { e.RawId, e.SourceId }).HasPrincipalKey(r => new { r.Id, r.DataSourceId }).OnDelete(DeleteBehavior.Restrict);
        evidence.HasOne<SourcePolicy>().WithMany().HasForeignKey(e => e.PolicyId).OnDelete(DeleteBehavior.Restrict);
        evidence.Property(e => e.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        evidence.Property(e => e.OperatorId).HasMaxLength(200); evidence.Property(e => e.Reason).HasMaxLength(500); evidence.Property(e => e.EvidenceReference).HasMaxLength(1000);
        var reviews = model.Entity<CoverageReview>();
        reviews.ToTable("Reviews", "coverage", t => t.HasCheckConstraint("CK_CoverageReview_State", "\"Sequence\" > 0 AND \"Status\" IN ('Approved','Rejected') AND \"ReviewedAtUtc\" <= \"RecordedAtUtc\" AND length(btrim(\"BasisReference\")) > 0"));
        reviews.HasKey(r => r.Id); reviews.Property(r => r.Id).ValueGeneratedNever(); reviews.Property(r => r.Status).HasConversion<string>();
        reviews.HasIndex(r => new { r.EvidenceId, r.Sequence }).IsUnique(); reviews.HasOne<CoverageEvidence>().WithMany().HasForeignKey(r => r.EvidenceId).OnDelete(DeleteBehavior.Restrict);
        reviews.Property(r => r.OperatorId).HasMaxLength(200); reviews.Property(r => r.Reason).HasMaxLength(500); reviews.Property(r => r.BasisReference).HasMaxLength(1000);
        reviews.Property(r => r.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        var times = model.Entity<EventTimeEvidence>();
        times.ToTable("EventTimes", "coverage", t => t.HasCheckConstraint("CK_EventTime_Version", "\"Version\" > 0 AND \"AvailableAtUtc\" >= \"RetrievedAtUtc\" AND \"AvailableAtUtc\" <= \"RecordedAtUtc\" AND \"RawHash\" ~ '^[0-9a-f]{64}$'"));
        times.HasKey(e => e.Id); times.Property(e => e.Id).ValueGeneratedNever(); times.Property(e => e.Value).HasConversion(v => Encode(v), v => Decode<EventTimeValue>(v)).HasColumnType("jsonb");
        times.HasIndex(e => new { e.ProviderIdentityId, e.RecordedAtUtc, e.Id }); times.HasIndex(e => e.CorrectsId).IsUnique();
        times.HasOne<EventTimeEvidence>().WithMany().HasForeignKey(e => e.CorrectsId).OnDelete(DeleteBehavior.Restrict);
        times.HasOne<Observation>().WithMany().HasForeignKey(e => e.DateObservationId).OnDelete(DeleteBehavior.Restrict);
        times.HasOne<ProviderIdentity>().WithMany().HasForeignKey(e => e.ProviderIdentityId).OnDelete(DeleteBehavior.Restrict);
        times.HasOne<RawPayload>().WithMany().HasForeignKey(e => new { e.RawId, e.SourceId }).HasPrincipalKey(r => new { r.Id, r.DataSourceId }).OnDelete(DeleteBehavior.Restrict);
        times.HasOne<SourcePolicy>().WithMany().HasForeignKey(e => e.PolicyId).OnDelete(DeleteBehavior.Restrict);
        times.Property(e => e.OperatorId).HasMaxLength(200); times.Property(e => e.Reason).HasMaxLength(500); times.Property(e => e.EvidenceReference).HasMaxLength(1000);
        times.Property(e => e.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
    }
}
