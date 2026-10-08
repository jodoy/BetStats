using BetStats.Domain.Governance;
using BetStats.Domain.Identity;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BetStats.Infrastructure.Persistence;

internal static class GovernanceModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var policies = model.Entity<SourcePolicy>();
        policies.ToTable("SourcePolicies", "governance", table =>
        {
            table.HasCheckConstraint("CK_SourcePolicies_Version", "\"Version\" > 0");
            table.HasCheckConstraint("CK_SourcePolicies_Interval", "\"EffectiveToUtc\" IS NULL OR \"EffectiveToUtc\" > \"EffectiveFromUtc\"");
            table.HasCheckConstraint("CK_SourcePolicies_Evidence", "length(btrim(\"TermsReference\")) > 0 AND length(btrim(\"EvidenceReference\")) > 0");
        });
        policies.HasKey(p => p.Id); policies.Property(p => p.Id).ValueGeneratedNever();
        policies.HasIndex(p => new { p.DataSourceId, p.Version }).IsUnique();
        policies.HasIndex(p => new { p.DataSourceId, p.EffectiveFromUtc, p.EffectiveToUtc });
        policies.HasOne<DataSource>().WithMany().HasForeignKey(p => p.DataSourceId).OnDelete(DeleteBehavior.Restrict);
        policies.Property(p => p.TermsReference).HasMaxLength(1000); policies.Property(p => p.EvidenceReference).HasMaxLength(1000);
        policies.Ignore(p => p.Status); policies.Ignore(p => p.ReviewedAtUtc); policies.Ignore(p => p.ApprovedAtUtc); policies.Ignore(p => p.Reviewer);
        policies.Property(p => p.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);

        var permissions = model.Entity<PurposePermission>();
        permissions.ToTable("PurposePermissions", "governance", table =>
        {
            table.HasCheckConstraint("CK_PurposePermissions_Purpose", "\"Purpose\" IN ('MetadataDiscovery','DataRetrieval','RawPayloadStorage','HistoricalRetention','InternalAnalytics','PublicDisplay','ModelTraining','CommercialUse','Redistribution')");
            table.HasCheckConstraint("CK_PurposePermissions_Decision", "\"Decision\" IN ('Allowed','Denied','Unknown')");
            table.HasCheckConstraint("CK_PurposePermissions_Restrictions", "(\"MaximumRetentionDays\" IS NULL OR \"MaximumRetentionDays\" > 0) AND (\"Attribution\" IS NULL OR length(btrim(\"Attribution\")) > 0)");
        });
        permissions.HasKey(p => new { p.SourcePolicyId, p.Purpose });
        permissions.Property(p => p.Purpose).HasConversion<string>().HasMaxLength(30);
        permissions.Property(p => p.Decision).HasConversion<string>().HasMaxLength(10);
        permissions.Property(p => p.Attribution).HasMaxLength(500);
        policies.HasMany(p => p.Permissions).WithOne().HasForeignKey(p => p.SourcePolicyId).OnDelete(DeleteBehavior.Restrict);
        policies.Navigation(p => p.Permissions).HasField("permissions").UsePropertyAccessMode(PropertyAccessMode.Field);

        var audits = model.Entity<PolicyAudit>();
        audits.ToTable("PolicyAudits", "governance", table =>
        {
            table.HasCheckConstraint("CK_PolicyAudits_Status", "(\"Status\" = 'Approved' AND \"ApprovedAtUtc\" IS NOT NULL AND \"ApprovedAtUtc\" >= \"ReviewedAtUtc\") OR (\"Status\" = 'Revoked' AND \"ApprovedAtUtc\" IS NULL)");
            table.HasCheckConstraint("CK_PolicyAudits_Audit", "length(btrim(\"Reviewer\")) > 0 AND length(btrim(\"Reason\")) > 0");
            table.HasCheckConstraint("CK_PolicyAudits_Previous", "(\"Sequence\" = 1 AND \"PreviousAuditId\" IS NULL AND \"PreviousSequence\" IS NULL) OR (\"Sequence\" > 1 AND \"PreviousAuditId\" IS NOT NULL AND \"PreviousSequence\" IS NOT NULL AND \"PreviousSequence\" = \"Sequence\" - 1)");
        });
        audits.HasKey(a => a.Id); audits.Property(a => a.Id).ValueGeneratedNever();
        audits.HasAlternateKey(a => new { a.Id, a.SourcePolicyId, a.Sequence });
        audits.HasIndex(a => new { a.SourcePolicyId, a.Sequence }).IsUnique();
        audits.HasIndex(a => new { a.SourcePolicyId, a.RecordedAtUtc });
        audits.Property(a => a.Status).HasConversion<string>().HasMaxLength(10);
        audits.Property(a => a.Reviewer).HasMaxLength(200); audits.Property(a => a.Reason).HasMaxLength(500);
        audits.Property(a => a.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        policies.HasMany(p => p.Audit).WithOne().HasForeignKey(a => a.SourcePolicyId).OnDelete(DeleteBehavior.Restrict);
        policies.Navigation(p => p.Audit).HasField("audit").UsePropertyAccessMode(PropertyAccessMode.Field);
        audits.HasOne<PolicyAudit>().WithMany().HasForeignKey(a => new { a.PreviousAuditId, a.SourcePolicyId, a.PreviousSequence })
            .HasPrincipalKey(a => new { a.Id, a.SourcePolicyId, a.Sequence }).OnDelete(DeleteBehavior.Restrict);

        var identities = model.Entity<IdentityResolution>();
        identities.Property(i => i.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        identities.HasIndex(i => new { i.ProviderIdentityId, i.RecordedAtUtc, i.Version });
    }
}
