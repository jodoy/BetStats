using BetStats.Domain.Governance;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BetStats.Infrastructure.Persistence;

internal static class IngestionModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<RawPayload>().ToTable("RawPayloads", table => table.HasCheckConstraint("CK_RawPayloads_Length", "\"ByteLength\" IS NULL OR \"ByteLength\" BETWEEN 1 AND 1048576"));
        model.Entity<RawPayload>().Property(p => p.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        var binding = model.Entity<FootballRawContext>();
        binding.ToTable("FootballRawContexts", table => table.HasCheckConstraint("CK_FootballRawContexts_Scope", "length(btrim(\"CompetitionReference\")) > 0 AND length(btrim(\"SeasonReference\")) > 0 AND \"Version\" = 1"));
        binding.HasKey(x => x.RawId);
        binding.Property(x => x.CompetitionReference).HasMaxLength(50);
        binding.Property(x => x.SeasonReference).HasMaxLength(50);
        binding.Property(x => x.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        model.Entity<RawPayload>().HasAlternateKey(x => new { x.Id, x.DataSourceId });
        binding.HasOne<RawPayload>().WithMany().HasForeignKey(x => new { x.RawId, x.SourceId }).HasPrincipalKey(x => new { x.Id, x.DataSourceId }).OnDelete(DeleteBehavior.Restrict);
        var audits = model.Entity<IngestionAuditEvent>();
        audits.ToTable("IngestionAuditEvents", table =>
        {
            table.HasCheckConstraint("CK_IngestionAuditEvents_Counts", "\"Sequence\" IN (1,2) AND \"RetrievedPayloads\" >= 0 AND \"ParsedRecords\" >= 0 AND \"AcceptedRecords\" >= 0 AND \"RejectedRecords\" >= 0 AND \"UnresolvedIdentities\" >= 0");
            table.HasCheckConstraint("CK_IngestionAuditEvents_Outcome", "(\"Sequence\" = 1 AND \"Outcome\" = 'Running') OR (\"Sequence\" = 2 AND \"Outcome\" IN ('Succeeded','Partial','Denied','Failed','Interrupted','Reused'))");
        });
        audits.HasKey(a => a.Id); audits.Property(a => a.Id).ValueGeneratedNever();
        audits.HasIndex(a => new { a.AttemptId, a.Sequence }).IsUnique();
        audits.Property(a => a.Outcome).HasConversion<string>().HasMaxLength(20);
        audits.Property(a => a.ErrorCode).HasMaxLength(100);
        audits.Property(a => a.ErrorCategory).HasMaxLength(40);
        audits.Property(a => a.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        audits.HasOne<IngestionRun>().WithMany().HasForeignKey(a => new { a.RunId, a.DataSourceId }).HasPrincipalKey(r => new { r.Id, r.DataSourceId }).OnDelete(DeleteBehavior.Restrict);
        audits.HasOne<SourcePolicy>().WithMany().HasForeignKey(a => a.PolicyId).OnDelete(DeleteBehavior.Restrict);
        audits.HasOne<PolicyAudit>().WithMany().HasForeignKey(a => a.ApprovalAuditId).OnDelete(DeleteBehavior.Restrict);
        var receipts = model.Entity<IngestionPublication>();
        receipts.ToTable("IngestionPublications"); receipts.HasKey(r => r.Id); receipts.Property(r => r.Id).ValueGeneratedNever();
        receipts.Property(r => r.Key).HasMaxLength(64); receipts.HasIndex(r => new { r.DataSourceId, r.Key }).IsUnique();
        receipts.Property(r => r.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        receipts.HasOne<RawPayload>().WithMany().HasForeignKey(r => new { r.RawPayloadId, r.DataSourceId }).HasPrincipalKey(p => new { p.Id, p.DataSourceId }).OnDelete(DeleteBehavior.Restrict);
        receipts.HasOne<IngestionRun>().WithMany().HasForeignKey(r => new { r.RunId, r.DataSourceId }).HasPrincipalKey(p => new { p.Id, p.DataSourceId }).OnDelete(DeleteBehavior.Restrict);
    }
}
