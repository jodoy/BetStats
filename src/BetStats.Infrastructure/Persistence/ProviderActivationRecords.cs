using BetStats.Domain.Governance;
using BetStats.Infrastructure.Pipeline;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BetStats.Infrastructure.Persistence;

public sealed class ProviderGrantRecord
{
    public Guid Id { get; init; }
    public Guid SourceId { get; init; }
    public Guid PolicyId { get; init; }
    public required byte[] Content { get; init; }
    public required string Hash { get; init; }
    public DateTime RecordedUtc { get; private set; }
}
public sealed class ProviderActivationEvent
{
    public Guid Id { get; init; }
    public long Sequence { get; private set; }
    public Guid GrantId { get; init; }
    public Guid SourceId { get; init; }
    public bool Enabled { get; init; }
    public required string Actor { get; init; }
    public required string Reason { get; init; }
    public DateTime RecordedUtc { get; private set; }
}
public sealed class ProviderRequestEvent
{
    public Guid Id { get; init; }
    public long Sequence { get; private set; }
    public Guid RequestId { get; init; }
    public Guid SourceId { get; init; }
    public Guid GrantId { get; init; }
    public Guid ExecutionId { get; init; }
    public Guid Owner { get; init; }
    public int Attempt { get; init; }
    public Guid RunId { get; init; }
    public required string Stage { get; init; }
    public required string Category { get; init; }
    public Guid? RawId { get; init; }
    public Guid? PublicationJobId { get; init; }
    public DateTime? RetryUntilUtc { get; init; }
    public DateTime RecordedUtc { get; private set; }
}
internal static class ProviderActivationConfiguration
{
    internal static void Configure(ModelBuilder model)
    {
        var g = model.Entity<ProviderGrantRecord>();
        g.ToTable("ProviderGrants", "ingestion", t => t.HasCheckConstraint("CK_ProviderGrants_Content", "octet_length(\"Content\") BETWEEN 1 AND 16384 AND \"Hash\" ~ '^[0-9a-f]{64}$'"));
        g.HasKey(x => x.Id); g.Property(x => x.Id).ValueGeneratedNever();
        g.HasAlternateKey(x => new { x.Id, x.SourceId });
        g.HasOne<DataSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
        g.HasOne<SourcePolicy>().WithMany().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Restrict);
        g.Property(x => x.Hash).HasMaxLength(64);
        g.Property(x => x.RecordedUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        var a = model.Entity<ProviderActivationEvent>(); a.ToTable("ProviderActivationEvents", "ingestion");
        a.HasKey(x => x.Id); a.Property(x => x.Id).ValueGeneratedNever();
        a.Property(x => x.Sequence).UseIdentityAlwaysColumn(); a.HasIndex(x => new { x.SourceId, x.Sequence }).IsUnique();
        a.HasOne<ProviderGrantRecord>().WithMany().HasForeignKey(x => new { x.GrantId, x.SourceId }).HasPrincipalKey(x => new { x.Id, x.SourceId }).OnDelete(DeleteBehavior.Restrict);
        a.Property(x => x.Actor).HasMaxLength(200); a.Property(x => x.Reason).HasMaxLength(1000);
        a.Property(x => x.RecordedUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        var r = model.Entity<ProviderRequestEvent>();
        r.ToTable("ProviderRequestEvents", "ingestion", t => {
            t.HasCheckConstraint("CK_ProviderRequestEvents_Stage", "\"Stage\" IN ('Reserved','Failed','Captured','Prepared','Reconciled')");
            t.HasCheckConstraint("CK_ProviderRequestEvents_Attempt", "\"Attempt\" BETWEEN 1 AND 5");
        });
        r.HasKey(x => x.Id); r.Property(x => x.Id).ValueGeneratedNever();
        r.Property(x => x.Sequence).UseIdentityAlwaysColumn(); r.HasIndex(x => new { x.SourceId, x.Sequence });
        r.HasIndex(x => new { x.ExecutionId, x.Stage }).IsUnique();
        r.HasOne<ProviderGrantRecord>().WithMany().HasForeignKey(x => new { x.GrantId, x.SourceId }).HasPrincipalKey(x => new { x.Id, x.SourceId }).OnDelete(DeleteBehavior.Restrict);
        r.HasOne<PipelineExecution>().WithMany().HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Restrict);
        r.HasOne<IngestionRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
        r.HasOne<RawPayload>().WithMany().HasForeignKey(x => new { x.RawId, x.SourceId }).HasPrincipalKey(x => new { x.Id, x.DataSourceId }).OnDelete(DeleteBehavior.Restrict);
        r.Property(x => x.Stage).HasMaxLength(30); r.Property(x => x.Category).HasMaxLength(100);
        r.Property(x => x.RecordedUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
    }
}
