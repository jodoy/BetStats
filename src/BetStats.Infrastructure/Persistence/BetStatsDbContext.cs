using BetStats.Domain.Identity;
using BetStats.Domain.Governance;
using BetStats.Domain.Observations;
using BetStats.Domain.Sports;
using BetStats.Domain.Quality;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BetStats.Infrastructure.Persistence;

public sealed class BetStatsDbContext(DbContextOptions<BetStatsDbContext> options) : DbContext(options)
{
    public DbSet<DataSource> DataSources => Set<DataSource>();
    public DbSet<QualityAssessment> QualityAssessments => Set<QualityAssessment>();
    public DbSet<MaintenanceEvent> MaintenanceEvents => Set<MaintenanceEvent>();
    public DbSet<IngestionRun> IngestionRuns => Set<IngestionRun>();
    public DbSet<RawPayload> RawPayloads => Set<RawPayload>();
    public DbSet<IngestionAuditEvent> IngestionAuditEvents => Set<IngestionAuditEvent>();
    public DbSet<IngestionPublication> IngestionPublications => Set<IngestionPublication>();
    public DbSet<Sport> Sports => Set<Sport>();
    public DbSet<Competition> Competitions => Set<Competition>();
    public DbSet<Season> Seasons => Set<Season>();
    public DbSet<Participant> Participants => Set<Participant>();
    public DbSet<SportingEvent> SportingEvents => Set<SportingEvent>();
    public DbSet<EventParticipant> EventParticipants => Set<EventParticipant>();
    public DbSet<ProviderIdentity> ProviderIdentities => Set<ProviderIdentity>();
    public DbSet<IdentityResolution> IdentityResolutions => Set<IdentityResolution>();
    public DbSet<Observation> Observations => Set<Observation>();
    public DbSet<SourcePolicy> SourcePolicies => Set<SourcePolicy>();
    public DbSet<PolicyAudit> PolicyAudits => Set<PolicyAudit>();
    public DbSet<PurposePermission> PurposePermissions => Set<PurposePermission>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("ingestion");

        var sources = modelBuilder.Entity<DataSource>();
        sources.ToTable("DataSources", table =>
        {
            table.HasCheckConstraint("CK_DataSources_Code_NotBlank", "length(btrim(\"Code\")) > 0");
            table.HasCheckConstraint("CK_DataSources_DisplayName_NotBlank", "length(btrim(\"DisplayName\")) > 0");
        });
        sources.HasKey(source => source.Id);
        sources.Property(source => source.Id).ValueGeneratedNever();
        sources.Property(source => source.Code).HasMaxLength(100).IsRequired();
        sources.Property(source => source.DisplayName).HasMaxLength(200).IsRequired();
        sources.Property(source => source.CreatedAtUtc).HasColumnType("timestamp with time zone");
        sources.HasIndex(source => source.Code).IsUnique();

        var runs = modelBuilder.Entity<IngestionRun>();
        runs.ToTable("IngestionRuns", table =>
        {
            table.HasCheckConstraint("CK_IngestionRuns_Status", "\"Status\" IN ('Pending', 'Running', 'Succeeded', 'Failed')");
            table.HasCheckConstraint("CK_IngestionRuns_Times",
                "\"CompletedAtUtc\" IS NULL OR (\"StartedAtUtc\" IS NOT NULL AND \"CompletedAtUtc\" >= \"StartedAtUtc\")");
        });
        runs.HasKey(run => run.Id);
        runs.Property(run => run.Id).ValueGeneratedNever();
        runs.HasAlternateKey(run => new { run.Id, run.DataSourceId });
        runs.Property(run => run.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        runs.Property(run => run.ErrorCode).HasMaxLength(100);
        runs.Property(run => run.StartedAtUtc).HasColumnType("timestamp with time zone");
        runs.Property(run => run.CompletedAtUtc).HasColumnType("timestamp with time zone");
        runs.Property(run => run.CreatedAtUtc).HasColumnType("timestamp with time zone");
        runs.HasOne<DataSource>().WithMany().HasForeignKey(run => run.DataSourceId).OnDelete(DeleteBehavior.Restrict);
        runs.HasIndex(run => new { run.DataSourceId, run.CreatedAtUtc });

        var payloads = modelBuilder.Entity<RawPayload>();
        payloads.ToTable("RawPayloads", table =>
        {
            table.HasCheckConstraint("CK_RawPayloads_Hash", "\"ContentHashSha256\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_RawPayloads_ContentType_NotBlank", "length(btrim(\"ContentType\")) > 0");
            table.HasCheckConstraint("CK_RawPayloads_StorageKey_NotBlank", "length(btrim(\"StorageKey\")) > 0");
        });
        payloads.HasKey(payload => payload.Id);
        payloads.Property(payload => payload.Id).ValueGeneratedNever();
        payloads.Property(payload => payload.ExternalReference).HasMaxLength(500);
        payloads.Property(payload => payload.ContentHashSha256).HasMaxLength(64).IsRequired();
        payloads.Property(payload => payload.ContentType).HasMaxLength(200).IsRequired();
        payloads.Property(payload => payload.StorageKey).HasMaxLength(1000).IsRequired();
        payloads.Property(payload => payload.RetrievedAtUtc).HasColumnType("timestamp with time zone");
        payloads.Property(payload => payload.CreatedAtUtc).HasColumnType("timestamp with time zone");
        payloads.HasOne<DataSource>().WithMany().HasForeignKey(payload => payload.DataSourceId).OnDelete(DeleteBehavior.Restrict);
        payloads.HasOne<IngestionRun>().WithMany()
            .HasForeignKey(payload => new { payload.IngestionRunId, payload.DataSourceId })
            .HasPrincipalKey(run => new { run.Id, run.DataSourceId })
            .OnDelete(DeleteBehavior.Restrict);
        payloads.HasIndex(payload => new { payload.DataSourceId, payload.RetrievedAtUtc });
        payloads.HasIndex(payload => payload.ContentHashSha256);
        CanonicalModelConfiguration.Configure(modelBuilder);
        ProvenanceModelConfiguration.Configure(modelBuilder);
        GovernanceModelConfiguration.Configure(modelBuilder);
        IngestionModelConfiguration.Configure(modelBuilder);
        QualityModelConfiguration.Configure(modelBuilder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidateChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ValidateChanges()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is QualityAssessment or MaintenanceEvent && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Quality assessments and maintenance audit are append-only.");
            if (entry.Entity is RawPayload && entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException("RAW capture metadata is append-only; updates and deletes require an explicit retention process.");
            }
            if (entry.Entity is IngestionAuditEvent or IngestionPublication && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Ingestion audit and publication receipts are append-only.");
            if (entry.Entity is ProviderIdentity or IdentityResolution or Observation && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Identity and observation history is append-only.");
            if (entry.Entity is SourcePolicy or PolicyAudit or PurposePermission && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Source policy versions, permissions and audit are append-only.");
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }
            foreach (var property in entry.Properties)
            {
                // Database-generated availability is not a caller-supplied timestamp.
                if (entry.State == EntityState.Added && property.Metadata.GetBeforeSaveBehavior() == PropertySaveBehavior.Ignore)
                    continue;
                if (property.CurrentValue is DateTime timestamp && timestamp.Kind != DateTimeKind.Utc)
                {
                    throw new InvalidOperationException($"{entry.Metadata.ClrType.Name}.{property.Metadata.Name} must be UTC.");
                }
            }
        }
    }
}
