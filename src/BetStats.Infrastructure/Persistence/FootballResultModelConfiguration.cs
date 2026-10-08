using BetStats.Application.Datasets;
using BetStats.Domain.Football;
using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Domain.Sports;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Text;

namespace BetStats.Infrastructure.Persistence;

internal static class FootballResultModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var r = model.Entity<FootballResultObservation>();
        r.ToTable("Results", "football", t => {
            t.HasCheckConstraint("CK_Results_Version", "\"SchemaVersion\" = 1 AND \"Version\" > 0 AND ((\"Version\" = 1 AND \"CorrectsId\" IS NULL) OR (\"Version\" > 1 AND \"CorrectsId\" IS NOT NULL))");
            t.HasCheckConstraint("CK_Results_Times", "\"AvailableAtUtc\" >= \"RetrievedAtUtc\" AND \"AvailableAtUtc\" <= \"RecordedAtUtc\" AND (\"PublishedAtUtc\" IS NULL OR \"PublishedAtUtc\" <= \"RetrievedAtUtc\")");
            t.HasCheckConstraint("CK_Results_Context", "\"HomeId\" <> \"AwayId\" AND length(btrim(\"SourceEventReference\")) > 0 AND length(btrim(\"CompetitionReference\")) > 0 AND length(btrim(\"SeasonReference\")) > 0");
        });
        r.HasKey(x => x.Id); r.Property(x => x.Id).ValueGeneratedNever();
        r.Property(x => x.Value).HasConversion(v => Encoding.UTF8.GetString(CanonicalDatasetJson.Serialize(v)), v => CanonicalDatasetJson.Deserialize<FootballResultValue>(Encoding.UTF8.GetBytes(v))).HasColumnType("jsonb");
        r.HasIndex(x => new { x.ProviderIdentityId, x.Version }).IsUnique(); r.HasIndex(x => x.CorrectsId).IsUnique();
        r.HasIndex(x => new { x.EventId, x.AvailableAtUtc, x.RecordedAtUtc });
        r.HasOne<FootballResultObservation>().WithMany().HasForeignKey(x => x.CorrectsId).OnDelete(DeleteBehavior.Restrict);
        r.HasOne<RawPayload>().WithMany().HasForeignKey(x => new { x.RawId, x.SourceId }).HasPrincipalKey(x => new { x.Id, x.DataSourceId }).OnDelete(DeleteBehavior.Restrict);
        r.HasOne<ProviderIdentity>().WithMany().HasForeignKey(x => x.ProviderIdentityId).OnDelete(DeleteBehavior.Restrict);
        r.HasOne<Observation>().WithMany().HasForeignKey(x => x.DateObservationId).OnDelete(DeleteBehavior.Restrict);
        r.HasOne<SportingEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
        r.HasOne<Competition>().WithMany().HasForeignKey(x => x.CompetitionId).OnDelete(DeleteBehavior.Restrict);
        r.HasOne<Season>().WithMany().HasForeignKey(x => x.SeasonId).OnDelete(DeleteBehavior.Restrict);
        r.HasOne<Participant>().WithMany().HasForeignKey(x => x.HomeId).OnDelete(DeleteBehavior.Restrict);
        r.HasOne<Participant>().WithMany().HasForeignKey(x => x.AwayId).OnDelete(DeleteBehavior.Restrict);
        r.Property(x => x.SourceEventReference).HasMaxLength(500); r.Property(x => x.CompetitionReference).HasMaxLength(50); r.Property(x => x.SeasonReference).HasMaxLength(50);
        r.Property(x => x.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        var a = model.Entity<FootballResultArtifact>();
        a.ToTable("ResultArtifacts", "datasets", t => t.HasCheckConstraint("CK_ResultArtifacts_Hash", "\"Hash\" ~ '^[0-9a-f]{64}$' AND octet_length(\"Content\") <= 16777216 AND \"SchemaVersion\" = 3"));
        a.HasKey(x => x.Id); a.Property(x => x.Id).ValueGeneratedNever(); a.HasIndex(x => x.Hash).IsUnique(); a.Property(x => x.Hash).HasMaxLength(64);
        a.HasOne<DatasetArtifact>().WithMany().HasForeignKey(x => x.MetadataSnapshotId).OnDelete(DeleteBehavior.Restrict);
        a.Property(x => x.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
    }
}

public sealed class FootballResultArtifact
{
    public Guid Id { get; init; }
    public Guid MetadataSnapshotId { get; init; }
    public string Hash { get; init; } = "";
    public byte[] Content { get; init; } = [];
    public int SchemaVersion { get; init; } = 3;
    public DateTime RecordedAtUtc { get; private set; }
}
