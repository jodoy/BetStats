using BetStats.Application.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Pipeline;

public sealed class PipelineJob
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public PipelineState State { get; set; }
    public bool Enabled { get; set; }
    public DateTime? NextDueUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
}
public sealed class PipelineJobVersion
{
    public Guid JobId { get; set; }
    public int Version { get; set; }
    public byte[] Content { get; set; } = [];
    public string Hash { get; set; } = "";
    public string Actor { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime RecordedUtc { get; set; }
}
public sealed class PipelineExecution
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public int DefinitionVersion { get; set; }
    public string Fingerprint { get; set; } = "";
    public PipelineState State { get; set; }
    public Guid Owner { get; set; }
    public DateTime PlannedUtc { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime LeaseUntilUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public int Attempt { get; set; }
    public bool CancelRequested { get; set; }
}
public sealed class PipelineReceipt
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid? ExecutionId { get; set; }
    public Guid Owner { get; set; }
    public PipelineState State { get; set; }
    public string Category { get; set; } = "";
    public string Actor { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime RecordedUtc { get; set; }
    public Guid? ArtifactId { get; set; }
    public string? ArtifactHash { get; set; }
}
internal static class PipelineConfiguration
{
    internal static void Configure(ModelBuilder model)
    {
        var jobs = model.Entity<PipelineJob>(); jobs.ToTable("Jobs", "pipeline"); jobs.HasKey(x => x.Id);
        var versions = model.Entity<PipelineJobVersion>(); versions.ToTable("JobVersions", "pipeline"); versions.HasKey(x => new { x.JobId, x.Version });
        versions.HasOne<PipelineJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        versions.Property(x => x.Hash).HasMaxLength(64); versions.Property(x => x.Actor).HasMaxLength(200); versions.Property(x => x.Reason).HasMaxLength(1000);
        var executions = model.Entity<PipelineExecution>(); executions.ToTable("Executions", "pipeline"); executions.HasKey(x => x.Id);
        executions.HasIndex(x => new { x.JobId, x.DefinitionVersion, x.PlannedUtc }).IsUnique();
        executions.HasOne<PipelineJobVersion>().WithMany().HasForeignKey(x => new { x.JobId, x.DefinitionVersion }).OnDelete(DeleteBehavior.Restrict);
        executions.Property(x => x.Fingerprint).HasMaxLength(64);
        var receipts = model.Entity<PipelineReceipt>(); receipts.ToTable("Receipts", "pipeline"); receipts.HasKey(x => x.Id);
        receipts.HasIndex(x => new { x.JobId, x.RecordedUtc }); receipts.HasOne<PipelineJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        receipts.HasOne<PipelineExecution>().WithMany().HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Restrict);
        receipts.Property(x => x.Actor).HasMaxLength(200); receipts.Property(x => x.Reason).HasMaxLength(1000); receipts.Property(x => x.Category).HasMaxLength(100);
        receipts.Property(x => x.ArtifactHash).HasMaxLength(64);
        var outputs = model.Entity<PipelineArtifact>(); outputs.ToTable("Artifacts", "pipeline"); outputs.HasKey(x => x.ExecutionId);
        outputs.HasOne<PipelineExecution>().WithOne().HasForeignKey<PipelineArtifact>(x => x.ExecutionId).OnDelete(DeleteBehavior.Restrict);
        outputs.Property(x => x.Hash).HasMaxLength(64);
    }
}
public sealed class PipelineArtifact
{
    public Guid ExecutionId { get; set; }
    public byte[] Content { get; set; } = [];
    public string Hash { get; set; } = "";
    public DateTime RecordedUtc { get; set; }
}
