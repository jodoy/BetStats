using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Domain.Quality;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BetStats.Infrastructure.Persistence;

internal static class QualityModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var assessments = model.Entity<QualityAssessment>();
        assessments.ToTable("QualityAssessments", "quality", table =>
        {
            table.HasCheckConstraint("CK_QualityAssessments_Rule", "\"RuleVersion\" > 0 AND \"Row\" >= 0 AND length(btrim(\"RuleId\")) > 0 AND length(btrim(\"ReasonCode\")) > 0");
            table.HasCheckConstraint("CK_QualityAssessments_Severity", "\"Severity\" IN ('Info','Warning','Error','Critical')");
            table.HasCheckConstraint("CK_QualityAssessments_Time", "\"AssessedAtUtc\" <= \"RecordedAtUtc\"");
        });
        assessments.HasKey(a => a.Id); assessments.Property(a => a.Id).ValueGeneratedNever();
        assessments.HasIndex(a => new { a.ExecutionId, a.RawPayloadId, a.Row, a.RuleId, a.RuleVersion }).IsUnique();
        assessments.HasIndex(a => new { a.ProviderIdentityId, a.RecordedAtUtc, a.Id });
        assessments.HasIndex(a => new { a.RunId, a.Row });
        assessments.Property(a => a.RecordReference).HasMaxLength(500); assessments.Property(a => a.RuleId).HasMaxLength(100);
        assessments.Property(a => a.ReasonCode).HasMaxLength(100);
        assessments.Property(a => a.ContextKey).HasMaxLength(64);
        assessments.Property(a => a.Severity).HasConversion<string>().HasMaxLength(20);
        assessments.Property(a => a.Classification).HasConversion<string>().HasMaxLength(40);
        assessments.Property(a => a.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        assessments.HasOne<RawPayload>().WithMany().HasForeignKey(a => new { a.RawPayloadId, a.DataSourceId }).HasPrincipalKey(r => new { r.Id, r.DataSourceId }).OnDelete(DeleteBehavior.Restrict);
        assessments.HasOne<ProviderIdentity>().WithMany().HasForeignKey(a => a.ProviderIdentityId).OnDelete(DeleteBehavior.Restrict);
        assessments.HasOne<Observation>().WithMany().HasForeignKey(a => a.ObservationId).OnDelete(DeleteBehavior.Restrict);
        assessments.HasOne<IngestionRun>().WithMany().HasForeignKey(a => new { a.RunId, a.DataSourceId }).HasPrincipalKey(r => new { r.Id, r.DataSourceId }).OnDelete(DeleteBehavior.Restrict);
        assessments.HasOne<BetStats.Domain.Governance.SourcePolicy>().WithMany().HasForeignKey(a => a.PolicyId).OnDelete(DeleteBehavior.Restrict);
        var audits = model.Entity<MaintenanceEvent>();
        audits.ToTable("MaintenanceEvents", "quality", table =>
        {
            table.HasCheckConstraint("CK_MaintenanceEvents_Audit", "\"Sequence\" IN (1,2) AND length(btrim(\"OperatorId\")) > 0 AND length(btrim(\"Reason\")) > 0 AND length(btrim(\"Action\")) > 0 AND length(btrim(\"Result\")) > 0");
            table.HasCheckConstraint("CK_MaintenanceEvents_Time", "\"ExecutedAtUtc\" <= \"RecordedAtUtc\"");
        });
        audits.HasKey(a => a.Id); audits.Property(a => a.Id).ValueGeneratedNever();
        audits.HasIndex(a => new { a.ExecutionId, a.Sequence }).IsUnique();
        audits.HasIndex(a => new { a.TargetId, a.RecordedAtUtc, a.Id });
        audits.Property(a => a.Action).HasMaxLength(50); audits.Property(a => a.Result).HasMaxLength(50);
        audits.Property(a => a.OperatorId).HasMaxLength(200); audits.Property(a => a.Reason).HasMaxLength(500);
        audits.Property(a => a.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        audits.HasOne<IdentityResolution>().WithMany().HasForeignKey(a => a.DecisionId).OnDelete(DeleteBehavior.Restrict);
    }
}
