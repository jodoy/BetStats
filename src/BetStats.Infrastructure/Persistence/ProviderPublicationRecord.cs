using BetStats.Domain.Governance;
using BetStats.Infrastructure.Persistence.Entities;
using BetStats.Infrastructure.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BetStats.Infrastructure.Persistence;

public sealed class ProviderPublicationRecord
{
    public Guid Id { get; init; }
    public Guid SourceId { get; init; }
    public Guid RawId { get; init; }
    public Guid PolicyId { get; init; }
    public Guid ExecutionId { get; init; }
    public required string Key { get; init; }
    public required byte[] Content { get; init; }
    public required string Hash { get; init; }
    public DateTime RecordedUtc { get; private set; }
}
internal static class ProviderPublicationConfiguration
{
    internal static void Configure(ModelBuilder model)
    {
        var r = model.Entity<ProviderPublicationRecord>();
        r.ToTable("ProviderPublications", "ingestion", t => {
            t.HasCheckConstraint("CK_ProviderPublications_Hashes", "\"Key\" ~ '^[0-9a-f]{64}$' AND \"Hash\" ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("CK_ProviderPublications_Size", "octet_length(\"Content\") BETWEEN 1 AND 16777216");
        });
        r.HasKey(x => x.Id); r.Property(x => x.Id).ValueGeneratedNever();
        r.HasIndex(x => new { x.SourceId, x.Key }).IsUnique();
        r.Property(x => x.Key).HasMaxLength(64); r.Property(x => x.Hash).HasMaxLength(64);
        r.HasOne<RawPayload>().WithMany().HasForeignKey(x => new { x.RawId, x.SourceId }).HasPrincipalKey(x => new { x.Id, x.DataSourceId }).OnDelete(DeleteBehavior.Restrict);
        r.HasOne<SourcePolicy>().WithMany().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Restrict);
        r.HasOne<PipelineExecution>().WithMany().HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Restrict);
        r.Property(x => x.RecordedUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
    }
}
