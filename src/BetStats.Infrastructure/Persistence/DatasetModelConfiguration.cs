using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BetStats.Infrastructure.Persistence;

internal static class DatasetModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var artifact = model.Entity<DatasetArtifact>();
        artifact.ToTable("Snapshots", "datasets", t => {
            t.HasCheckConstraint("CK_Snapshots_Content", "\"RowCount\" BETWEEN 1 AND 100 AND \"FeatureSchemaVersion\" IN (1,2) AND octet_length(\"Content\") BETWEEN 1 AND 16777216");
            t.HasCheckConstraint("CK_Snapshots_Hash", "\"ManifestHash\" ~ '^[0-9a-f]{64}$' AND \"DefinitionFingerprint\" ~ '^[0-9a-f]{64}$'");
        });
        artifact.HasKey(a => a.Id); artifact.Property(a => a.Id).ValueGeneratedNever();
        artifact.HasIndex(a => a.ManifestHash).IsUnique(); artifact.HasIndex(a => a.DefinitionFingerprint);
        artifact.Property(a => a.ManifestHash).HasMaxLength(64); artifact.Property(a => a.DefinitionFingerprint).HasMaxLength(64);
        artifact.Property(a => a.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        var features = model.Entity<DatasetFeatureRecord>();
        features.ToTable("Features", "datasets", t => t.HasCheckConstraint("CK_Features_Hash", "\"Fingerprint\" ~ '^[0-9a-f]{64}$' AND octet_length(\"Content\") BETWEEN 1 AND 16777216"));
        features.HasKey(f => f.Id); features.Property(f => f.Id).ValueGeneratedNever(); features.Property(f => f.Fingerprint).HasMaxLength(64);
        features.HasIndex(f => new { f.DatasetId, f.EventId, f.PredictionCutoffUtc }).IsUnique();
        features.HasOne<DatasetArtifact>().WithMany().HasForeignKey(f => f.DatasetId).OnDelete(DeleteBehavior.Restrict);
        features.Property(f => f.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        var events = model.Entity<DatasetBuildEvent>();
        events.ToTable("BuildEvents", "datasets", t => {
            t.HasCheckConstraint("CK_BuildEvents_State", "(\"Sequence\" = 1 AND \"Status\" = 'Requested') OR (\"Sequence\" = 2 AND \"Status\" = 'Running') OR (\"Sequence\" = 3 AND \"Status\" IN ('Succeeded','Failed','Cancelled'))");
            t.HasCheckConstraint("CK_BuildEvents_Result", "(\"Status\" = 'Succeeded') = (\"SnapshotId\" IS NOT NULL) AND length(btrim(\"OperatorId\")) > 0 AND length(btrim(\"Reason\")) > 0");
        });
        events.HasKey(e => e.Id); events.Property(e => e.Id).ValueGeneratedNever();
        events.HasIndex(e => new { e.AttemptId, e.Sequence }).IsUnique(); events.HasIndex(e => e.DefinitionFingerprint);
        events.Property(e => e.Status).HasConversion<string>().HasMaxLength(20); events.Property(e => e.OperatorId).HasMaxLength(200);
        events.Property(e => e.Reason).HasMaxLength(500); events.Property(e => e.FailureCode).HasMaxLength(100); events.Property(e => e.DefinitionFingerprint).HasMaxLength(64);
        events.HasOne<DatasetArtifact>().WithMany().HasForeignKey(e => e.SnapshotId).OnDelete(DeleteBehavior.Restrict);
        events.Property(e => e.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
    }
}
