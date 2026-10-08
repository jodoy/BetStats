using BetStats.Domain.Identity;
using BetStats.Domain.Observations;
using BetStats.Domain.Sports;
using BetStats.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BetStats.Infrastructure.Persistence;

internal static class ProvenanceModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<RawPayload>().HasAlternateKey(item => new { item.Id, item.DataSourceId });
        var identities = model.Entity<ProviderIdentity>();
        identities.ToTable("ProviderIdentities", "provenance", table =>
        {
            table.HasCheckConstraint("CK_ProviderIdentities_Kind", KindConstraint);
            table.HasCheckConstraint("CK_ProviderIdentities_ExternalId", "length(btrim(\"ExternalId\")) > 0");
        });
        identities.HasKey(item => item.Id); identities.Property(item => item.Id).ValueGeneratedNever();
        identities.HasAlternateKey(item => new { item.Id, item.DataSourceId, item.EntityKind });
        identities.HasIndex(item => new { item.DataSourceId, item.EntityKind, item.ExternalId }).IsUnique();
        identities.Property(item => item.EntityKind).HasConversion<string>().HasMaxLength(20);
        identities.Property(item => item.ExternalId).HasMaxLength(500);
        identities.HasOne<DataSource>().WithMany().HasForeignKey(item => item.DataSourceId).OnDelete(DeleteBehavior.Restrict);

        var decisions = model.Entity<IdentityResolution>();
        decisions.ToTable("IdentityResolutions", "provenance", table =>
        {
            table.HasCheckConstraint("CK_IdentityResolutions_Target", $"(\"Status\" = 'Resolved' AND ({TargetConstraint(false)})) OR (\"Status\" IN ('Unresolved','Ambiguous') AND ({NullTargets}))");
            table.HasCheckConstraint("CK_IdentityResolutions_Audit", "length(btrim(\"DecidedBy\")) > 0 AND length(btrim(\"Reason\")) > 0");
            table.HasCheckConstraint("CK_IdentityResolutions_Previous", "(\"Version\" = 1 AND \"PreviousDecisionId\" IS NULL AND \"PreviousVersion\" IS NULL AND \"PreviousDecidedAtUtc\" IS NULL) OR (\"Version\" > 1 AND \"PreviousDecisionId\" IS NOT NULL AND \"PreviousVersion\" IS NOT NULL AND \"PreviousDecidedAtUtc\" IS NOT NULL AND \"PreviousVersion\" = \"Version\" - 1 AND \"DecidedAtUtc\" >= \"PreviousDecidedAtUtc\")");
        });
        ConfigureTargets(decisions);
        decisions.HasKey(item => item.Id); decisions.Property(item => item.Id).ValueGeneratedNever();
        decisions.HasAlternateKey(item => new { item.Id, item.ProviderIdentityId, item.Version, item.DecidedAtUtc });
        decisions.HasIndex(item => new { item.ProviderIdentityId, item.Version }).IsUnique();
        decisions.HasIndex(item => new { item.ProviderIdentityId, item.DecidedAtUtc, item.Version });
        decisions.Property(item => item.Status).HasConversion<string>().HasMaxLength(20);
        decisions.Property(item => item.DecidedBy).HasMaxLength(200); decisions.Property(item => item.Reason).HasMaxLength(500);
        decisions.HasOne<ProviderIdentity>().WithMany().HasForeignKey(item => new { item.ProviderIdentityId, item.DataSourceId, item.EntityKind })
            .HasPrincipalKey(item => new { item.Id, item.DataSourceId, item.EntityKind }).OnDelete(DeleteBehavior.Restrict);
        decisions.HasOne<RawPayload>().WithMany().HasForeignKey(item => new { item.RawPayloadId, item.DataSourceId })
            .HasPrincipalKey(item => new { item.Id, item.DataSourceId }).OnDelete(DeleteBehavior.Restrict);
        decisions.HasOne<IdentityResolution>().WithMany()
            .HasForeignKey(item => new { item.PreviousDecisionId, item.ProviderIdentityId, item.PreviousVersion, item.PreviousDecidedAtUtc })
            .HasPrincipalKey(item => new { item.Id, item.ProviderIdentityId, item.Version, item.DecidedAtUtc }).OnDelete(DeleteBehavior.Restrict);

        var observations = model.Entity<Observation>();
        observations.ToTable("Observations", "provenance", table =>
        {
            table.HasCheckConstraint("CK_Observations_Target", TargetConstraint(true));
            table.HasCheckConstraint("CK_Observations_Availability", "\"AvailableAtUtc\" >= \"RetrievedAtUtc\" AND \"CreatedAtUtc\" >= \"RetrievedAtUtc\"");
            table.HasCheckConstraint("CK_Observations_Value", "(\"Type\" = 'DisplayName' AND \"EntityKind\" <> 'SportingEvent' AND \"TextValue\" IS NOT NULL AND length(btrim(\"TextValue\")) > 0 AND \"TimestampValueUtc\" IS NULL AND \"StatusValue\" IS NULL) OR (\"Type\" = 'ScheduledStart' AND \"EntityKind\" = 'SportingEvent' AND \"TextValue\" IS NULL AND \"TimestampValueUtc\" IS NOT NULL AND \"StatusValue\" IS NULL) OR (\"Type\" = 'EventStatus' AND \"EntityKind\" = 'SportingEvent' AND \"TextValue\" IS NULL AND \"TimestampValueUtc\" IS NULL AND \"StatusValue\" IS NOT NULL AND \"StatusValue\" IN ('Scheduled','InProgress','Completed','Postponed','Cancelled'))");
            table.HasCheckConstraint("CK_Observations_Correction", "(\"Version\" = 1 AND \"CorrectsObservationId\" IS NULL AND \"CorrectedVersion\" IS NULL AND \"CorrectedAvailableAtUtc\" IS NULL) OR (\"Version\" > 1 AND \"CorrectsObservationId\" IS NOT NULL AND \"CorrectedVersion\" IS NOT NULL AND \"CorrectedAvailableAtUtc\" IS NOT NULL AND \"CorrectedVersion\" = \"Version\" - 1 AND \"AvailableAtUtc\" >= \"CorrectedAvailableAtUtc\")");
        });
        ConfigureTargets(observations);
        observations.HasKey(item => item.Id); observations.Property(item => item.Id).ValueGeneratedNever();
        observations.Property(item => item.RecordedAtUtc).HasDefaultValueSql("clock_timestamp()").Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        observations.HasAlternateKey(item => new { item.Id, item.ProviderIdentityId, item.Type, item.Version, item.AvailableAtUtc });
        observations.Property(item => item.Type).HasConversion<string>().HasMaxLength(20);
        observations.Property(item => item.StatusValue).HasConversion<string>().HasMaxLength(20);
        observations.Property(item => item.TextValue).HasMaxLength(200);
        observations.HasOne<ProviderIdentity>().WithMany().HasForeignKey(item => new { item.ProviderIdentityId, item.DataSourceId, item.EntityKind })
            .HasPrincipalKey(item => new { item.Id, item.DataSourceId, item.EntityKind }).OnDelete(DeleteBehavior.Restrict);
        observations.HasOne<RawPayload>().WithMany().HasForeignKey(item => new { item.RawPayloadId, item.DataSourceId })
            .HasPrincipalKey(item => new { item.Id, item.DataSourceId }).OnDelete(DeleteBehavior.Restrict);
        observations.HasOne<Observation>().WithMany()
            .HasForeignKey(item => new { item.CorrectsObservationId, item.ProviderIdentityId, item.Type, item.CorrectedVersion, item.CorrectedAvailableAtUtc })
            .HasPrincipalKey(item => new { item.Id, item.ProviderIdentityId, item.Type, item.Version, item.AvailableAtUtc }).OnDelete(DeleteBehavior.Restrict);
        observations.HasIndex(item => new { item.EntityKind, item.AvailableAtUtc, item.CreatedAtUtc, item.Id });
        observations.HasIndex(item => new { item.ProviderIdentityId, item.AvailableAtUtc, item.CreatedAtUtc, item.Id });
        foreach (var target in Targets)
            observations.HasIndex(target.Column, nameof(Observation.AvailableAtUtc), nameof(Observation.CreatedAtUtc), nameof(Observation.Id));
    }

    private const string KindConstraint = "\"EntityKind\" IN ('Sport','Competition','Season','Participant','SportingEvent')";
    private static readonly (string Kind, string Column, Type Type)[] Targets =
    [
        ("Sport", "CanonicalSportId", typeof(Sport)), ("Competition", "CanonicalCompetitionId", typeof(Competition)),
        ("Season", "CanonicalSeasonId", typeof(Season)), ("Participant", "CanonicalParticipantId", typeof(Participant)),
        ("SportingEvent", "CanonicalSportingEventId", typeof(SportingEvent))
    ];
    private static string NullTargets => string.Join(" AND ", Targets.Select(target => $"\"{target.Column}\" IS NULL"));
    private static string TargetConstraint(bool nullable) =>
        (nullable ? $"({NullTargets}) OR " : "") + string.Join(" OR ", Targets.Select(target =>
            $"(\"EntityKind\" = '{target.Kind}' AND \"{target.Column}\" IS NOT NULL AND " +
            string.Join(" AND ", Targets.Where(other => other.Kind != target.Kind).Select(other => $"\"{other.Column}\" IS NULL")) + ")"));

    private static void ConfigureTargets<T>(EntityTypeBuilder<T> builder) where T : class
    {
        builder.Ignore("CanonicalId");
        builder.Property<CanonicalEntityKind>("EntityKind").HasConversion<string>().HasMaxLength(20);
        foreach (var target in Targets)
            builder.HasOne(target.Type, null).WithMany().HasForeignKey(target.Column).OnDelete(DeleteBehavior.Restrict);
    }
}
