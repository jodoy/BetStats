using BetStats.Domain.Sports;
using Microsoft.EntityFrameworkCore;

namespace BetStats.Infrastructure.Persistence;

internal static class CanonicalModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var sports = model.Entity<Sport>();
        sports.ToTable("Sports", "canonical", table =>
        {
            table.HasCheckConstraint("CK_Sports_Code", "\"Code\" ~ '^[a-z][a-z0-9-]*$'");
            table.HasCheckConstraint("CK_Sports_Name", "length(btrim(\"DisplayName\")) > 0");
        });
        sports.HasKey(item => item.Id); sports.Property(item => item.Id).ValueGeneratedNever();
        sports.Property(item => item.Code).HasMaxLength(50); sports.Property(item => item.DisplayName).HasMaxLength(200);
        sports.HasIndex(item => item.Code).IsUnique(); sports.HasData(ReferenceSports.All);

        var competitions = model.Entity<Competition>();
        competitions.ToTable("Competitions", "canonical", table =>
        {
            table.HasCheckConstraint("CK_Competitions_Name", "length(btrim(\"Name\")) > 0");
            table.HasCheckConstraint("CK_Competitions_Country", "\"CountryCode\" IS NULL OR \"CountryCode\" ~ '^[A-Z]{2}$'");
            table.HasCheckConstraint("CK_Competitions_Type", "\"CompetitionType\" IN ('League','Tournament','Other')");
        });
        competitions.HasKey(item => item.Id); competitions.Property(item => item.Id).ValueGeneratedNever();
        competitions.HasAlternateKey(item => new { item.Id, item.SportId });
        competitions.Property(item => item.Name).HasMaxLength(200); competitions.Property(item => item.CountryCode).HasMaxLength(2);
        competitions.Property(item => item.CompetitionType).HasConversion<string>().HasMaxLength(20);
        competitions.HasOne<Sport>().WithMany().HasForeignKey(item => item.SportId).OnDelete(DeleteBehavior.Restrict);

        var seasons = model.Entity<Season>();
        seasons.ToTable("Seasons", "canonical", table =>
        {
            table.HasCheckConstraint("CK_Seasons_Name", "length(btrim(\"Name\")) > 0");
            table.HasCheckConstraint("CK_Seasons_Dates", "\"StartDate\" IS NULL OR \"EndDate\" IS NULL OR \"EndDate\" >= \"StartDate\"");
        });
        seasons.HasKey(item => item.Id); seasons.Property(item => item.Id).ValueGeneratedNever();
        seasons.HasAlternateKey(item => new { item.Id, item.CompetitionId });
        seasons.Property(item => item.Name).HasMaxLength(100);
        seasons.HasOne<Competition>().WithMany().HasForeignKey(item => item.CompetitionId).OnDelete(DeleteBehavior.Restrict);

        var participants = model.Entity<Participant>();
        participants.ToTable("Participants", "canonical", table =>
        {
            table.HasCheckConstraint("CK_Participants_Name", "length(btrim(\"Name\")) > 0");
            table.HasCheckConstraint("CK_Participants_Type", "\"ParticipantType\" IN ('Team','Individual')");
        });
        participants.HasKey(item => item.Id); participants.Property(item => item.Id).ValueGeneratedNever();
        participants.HasAlternateKey(item => new { item.Id, item.SportId });
        participants.Property(item => item.Name).HasMaxLength(200);
        participants.Property(item => item.ParticipantType).HasConversion<string>().HasMaxLength(20);
        participants.HasOne<Sport>().WithMany().HasForeignKey(item => item.SportId).OnDelete(DeleteBehavior.Restrict);

        var events = model.Entity<SportingEvent>();
        events.ToTable("SportingEvents", "canonical", table =>
            table.HasCheckConstraint("CK_SportingEvents_Status", "\"Status\" IN ('Scheduled','InProgress','Completed','Postponed','Cancelled')"));
        events.HasKey(item => item.Id); events.Property(item => item.Id).ValueGeneratedNever();
        events.HasAlternateKey(item => new { item.Id, item.SportId });
        events.Property(item => item.Status).HasConversion<string>().HasMaxLength(20);
        events.HasOne<Sport>().WithMany().HasForeignKey(item => item.SportId).OnDelete(DeleteBehavior.Restrict);
        events.HasOne<Competition>().WithMany().HasForeignKey(item => new { item.CompetitionId, item.SportId })
            .HasPrincipalKey(item => new { item.Id, item.SportId }).OnDelete(DeleteBehavior.Restrict);
        events.HasOne<Season>().WithMany().HasForeignKey(item => new { item.SeasonId, item.CompetitionId })
            .HasPrincipalKey(item => new { item.Id, item.CompetitionId }).OnDelete(DeleteBehavior.Restrict);
        events.HasIndex(item => new { item.SportId, item.ScheduledStartUtc });

        var memberships = model.Entity<EventParticipant>();
        memberships.ToTable("EventParticipants", "canonical", table =>
            table.HasCheckConstraint("CK_EventParticipants_Side", "(\"Position\" = 1 AND \"Role\" IN ('Home','Side1')) OR (\"Position\" = 2 AND \"Role\" IN ('Away','Side2'))"));
        memberships.HasKey(item => new { item.EventId, item.ParticipantId });
        memberships.HasIndex(item => new { item.EventId, item.Position }).IsUnique();
        memberships.Property(item => item.Role).HasConversion<string>().HasMaxLength(10);
        events.HasMany(item => item.Participants).WithOne().HasForeignKey(item => new { item.EventId, item.SportId })
            .HasPrincipalKey(item => new { item.Id, item.SportId }).OnDelete(DeleteBehavior.Restrict);
        events.Navigation(item => item.Participants).HasField("participants").UsePropertyAccessMode(PropertyAccessMode.Field);
        memberships.HasOne<Participant>().WithMany().HasForeignKey(item => new { item.ParticipantId, item.SportId })
            .HasPrincipalKey(item => new { item.Id, item.SportId }).OnDelete(DeleteBehavior.Restrict);
    }
}
