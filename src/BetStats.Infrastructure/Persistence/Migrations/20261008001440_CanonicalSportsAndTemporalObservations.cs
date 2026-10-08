using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace BetStats.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CanonicalSportsAndTemporalObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "canonical");

            migrationBuilder.EnsureSchema(
                name: "provenance");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_RawPayloads_Id_DataSourceId",
                schema: "ingestion",
                table: "RawPayloads",
                columns: new[] { "Id", "DataSourceId" });

            migrationBuilder.CreateTable(
                name: "ProviderIdentities",
                schema: "provenance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DataSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderIdentities", x => x.Id);
                    table.UniqueConstraint("AK_ProviderIdentities_Id_DataSourceId_EntityKind", x => new { x.Id, x.DataSourceId, x.EntityKind });
                    table.CheckConstraint("CK_ProviderIdentities_ExternalId", "length(btrim(\"ExternalId\")) > 0");
                    table.CheckConstraint("CK_ProviderIdentities_Kind", "\"EntityKind\" IN ('Sport','Competition','Season','Participant','SportingEvent')");
                    table.ForeignKey(
                        name: "FK_ProviderIdentities_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "ingestion",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Sports",
                schema: "canonical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sports", x => x.Id);
                    table.CheckConstraint("CK_Sports_Code", "\"Code\" ~ '^[a-z][a-z0-9-]*$'");
                    table.CheckConstraint("CK_Sports_Name", "length(btrim(\"DisplayName\")) > 0");
                });

            migrationBuilder.CreateTable(
                name: "Competitions",
                schema: "canonical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    CompetitionType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Competitions", x => x.Id);
                    table.UniqueConstraint("AK_Competitions_Id_SportId", x => new { x.Id, x.SportId });
                    table.CheckConstraint("CK_Competitions_Country", "\"CountryCode\" IS NULL OR \"CountryCode\" ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("CK_Competitions_Name", "length(btrim(\"Name\")) > 0");
                    table.CheckConstraint("CK_Competitions_Type", "\"CompetitionType\" IN ('League','Tournament','Other')");
                    table.ForeignKey(
                        name: "FK_Competitions_Sports_SportId",
                        column: x => x.SportId,
                        principalSchema: "canonical",
                        principalTable: "Sports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Participants",
                schema: "canonical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ParticipantType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Participants", x => x.Id);
                    table.UniqueConstraint("AK_Participants_Id_SportId", x => new { x.Id, x.SportId });
                    table.CheckConstraint("CK_Participants_Name", "length(btrim(\"Name\")) > 0");
                    table.CheckConstraint("CK_Participants_Type", "\"ParticipantType\" IN ('Team','Individual')");
                    table.ForeignKey(
                        name: "FK_Participants_Sports_SportId",
                        column: x => x.SportId,
                        principalSchema: "canonical",
                        principalTable: "Sports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Seasons",
                schema: "canonical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompetitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Seasons", x => x.Id);
                    table.UniqueConstraint("AK_Seasons_Id_CompetitionId", x => new { x.Id, x.CompetitionId });
                    table.CheckConstraint("CK_Seasons_Dates", "\"StartDate\" IS NULL OR \"EndDate\" IS NULL OR \"EndDate\" >= \"StartDate\"");
                    table.CheckConstraint("CK_Seasons_Name", "length(btrim(\"Name\")) > 0");
                    table.ForeignKey(
                        name: "FK_Seasons_Competitions_CompetitionId",
                        column: x => x.CompetitionId,
                        principalSchema: "canonical",
                        principalTable: "Competitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SportingEvents",
                schema: "canonical",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SportId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompetitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScheduledStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SportingEvents", x => x.Id);
                    table.UniqueConstraint("AK_SportingEvents_Id_SportId", x => new { x.Id, x.SportId });
                    table.CheckConstraint("CK_SportingEvents_Status", "\"Status\" IN ('Scheduled','InProgress','Completed','Postponed','Cancelled')");
                    table.ForeignKey(
                        name: "FK_SportingEvents_Competitions_CompetitionId_SportId",
                        columns: x => new { x.CompetitionId, x.SportId },
                        principalSchema: "canonical",
                        principalTable: "Competitions",
                        principalColumns: new[] { "Id", "SportId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SportingEvents_Seasons_SeasonId_CompetitionId",
                        columns: x => new { x.SeasonId, x.CompetitionId },
                        principalSchema: "canonical",
                        principalTable: "Seasons",
                        principalColumns: new[] { "Id", "CompetitionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SportingEvents_Sports_SportId",
                        column: x => x.SportId,
                        principalSchema: "canonical",
                        principalTable: "Sports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EventParticipants",
                schema: "canonical",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventParticipants", x => new { x.EventId, x.ParticipantId });
                    table.CheckConstraint("CK_EventParticipants_Side", "(\"Position\" = 1 AND \"Role\" IN ('Home','Side1')) OR (\"Position\" = 2 AND \"Role\" IN ('Away','Side2'))");
                    table.ForeignKey(
                        name: "FK_EventParticipants_Participants_ParticipantId_SportId",
                        columns: x => new { x.ParticipantId, x.SportId },
                        principalSchema: "canonical",
                        principalTable: "Participants",
                        principalColumns: new[] { "Id", "SportId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventParticipants_SportingEvents_EventId_SportId",
                        columns: x => new { x.EventId, x.SportId },
                        principalSchema: "canonical",
                        principalTable: "SportingEvents",
                        principalColumns: new[] { "Id", "SportId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IdentityResolutions",
                schema: "provenance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderIdentityId = table.Column<Guid>(type: "uuid", nullable: false),
                    DataSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    PreviousDecisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    PreviousVersion = table.Column<int>(type: "integer", nullable: true),
                    PreviousDecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RawPayloadId = table.Column<Guid>(type: "uuid", nullable: true),
                    CanonicalSportId = table.Column<Guid>(type: "uuid", nullable: true),
                    CanonicalCompetitionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CanonicalSeasonId = table.Column<Guid>(type: "uuid", nullable: true),
                    CanonicalParticipantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CanonicalSportingEventId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityResolutions", x => x.Id);
                    table.UniqueConstraint("AK_IdentityResolutions_Id_ProviderIdentityId_Version_DecidedAt~", x => new { x.Id, x.ProviderIdentityId, x.Version, x.DecidedAtUtc });
                    table.CheckConstraint("CK_IdentityResolutions_Audit", "length(btrim(\"DecidedBy\")) > 0 AND length(btrim(\"Reason\")) > 0");
                    table.CheckConstraint("CK_IdentityResolutions_Previous", "(\"Version\" = 1 AND \"PreviousDecisionId\" IS NULL AND \"PreviousVersion\" IS NULL AND \"PreviousDecidedAtUtc\" IS NULL) OR (\"Version\" > 1 AND \"PreviousDecisionId\" IS NOT NULL AND \"PreviousVersion\" IS NOT NULL AND \"PreviousDecidedAtUtc\" IS NOT NULL AND \"PreviousVersion\" = \"Version\" - 1 AND \"DecidedAtUtc\" >= \"PreviousDecidedAtUtc\")");
                    table.CheckConstraint("CK_IdentityResolutions_Target", "(\"Status\" = 'Resolved' AND ((\"EntityKind\" = 'Sport' AND \"CanonicalSportId\" IS NOT NULL AND \"CanonicalCompetitionId\" IS NULL AND \"CanonicalSeasonId\" IS NULL AND \"CanonicalParticipantId\" IS NULL AND \"CanonicalSportingEventId\" IS NULL) OR (\"EntityKind\" = 'Competition' AND \"CanonicalCompetitionId\" IS NOT NULL AND \"CanonicalSportId\" IS NULL AND \"CanonicalSeasonId\" IS NULL AND \"CanonicalParticipantId\" IS NULL AND \"CanonicalSportingEventId\" IS NULL) OR (\"EntityKind\" = 'Season' AND \"CanonicalSeasonId\" IS NOT NULL AND \"CanonicalSportId\" IS NULL AND \"CanonicalCompetitionId\" IS NULL AND \"CanonicalParticipantId\" IS NULL AND \"CanonicalSportingEventId\" IS NULL) OR (\"EntityKind\" = 'Participant' AND \"CanonicalParticipantId\" IS NOT NULL AND \"CanonicalSportId\" IS NULL AND \"CanonicalCompetitionId\" IS NULL AND \"CanonicalSeasonId\" IS NULL AND \"CanonicalSportingEventId\" IS NULL) OR (\"EntityKind\" = 'SportingEvent' AND \"CanonicalSportingEventId\" IS NOT NULL AND \"CanonicalSportId\" IS NULL AND \"CanonicalCompetitionId\" IS NULL AND \"CanonicalSeasonId\" IS NULL AND \"CanonicalParticipantId\" IS NULL))) OR (\"Status\" IN ('Unresolved','Ambiguous') AND (\"CanonicalSportId\" IS NULL AND \"CanonicalCompetitionId\" IS NULL AND \"CanonicalSeasonId\" IS NULL AND \"CanonicalParticipantId\" IS NULL AND \"CanonicalSportingEventId\" IS NULL))");
                    table.ForeignKey(
                        name: "FK_IdentityResolutions_Competitions_CanonicalCompetitionId",
                        column: x => x.CanonicalCompetitionId,
                        principalSchema: "canonical",
                        principalTable: "Competitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IdentityResolutions_IdentityResolutions_PreviousDecisionId_~",
                        columns: x => new { x.PreviousDecisionId, x.ProviderIdentityId, x.PreviousVersion, x.PreviousDecidedAtUtc },
                        principalSchema: "provenance",
                        principalTable: "IdentityResolutions",
                        principalColumns: new[] { "Id", "ProviderIdentityId", "Version", "DecidedAtUtc" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IdentityResolutions_Participants_CanonicalParticipantId",
                        column: x => x.CanonicalParticipantId,
                        principalSchema: "canonical",
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IdentityResolutions_ProviderIdentities_ProviderIdentityId_D~",
                        columns: x => new { x.ProviderIdentityId, x.DataSourceId, x.EntityKind },
                        principalSchema: "provenance",
                        principalTable: "ProviderIdentities",
                        principalColumns: new[] { "Id", "DataSourceId", "EntityKind" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IdentityResolutions_RawPayloads_RawPayloadId_DataSourceId",
                        columns: x => new { x.RawPayloadId, x.DataSourceId },
                        principalSchema: "ingestion",
                        principalTable: "RawPayloads",
                        principalColumns: new[] { "Id", "DataSourceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IdentityResolutions_Seasons_CanonicalSeasonId",
                        column: x => x.CanonicalSeasonId,
                        principalSchema: "canonical",
                        principalTable: "Seasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IdentityResolutions_SportingEvents_CanonicalSportingEventId",
                        column: x => x.CanonicalSportingEventId,
                        principalSchema: "canonical",
                        principalTable: "SportingEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IdentityResolutions_Sports_CanonicalSportId",
                        column: x => x.CanonicalSportId,
                        principalSchema: "canonical",
                        principalTable: "Sports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Observations",
                schema: "provenance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderIdentityId = table.Column<Guid>(type: "uuid", nullable: false),
                    DataSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TextValue = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TimestampValueUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StatusValue = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    SourceEventTimeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SourcePublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetrievedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AvailableAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RawPayloadId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CorrectsObservationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrectedVersion = table.Column<int>(type: "integer", nullable: true),
                    CorrectedAvailableAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CanonicalSportId = table.Column<Guid>(type: "uuid", nullable: true),
                    CanonicalCompetitionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CanonicalSeasonId = table.Column<Guid>(type: "uuid", nullable: true),
                    CanonicalParticipantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CanonicalSportingEventId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Observations", x => x.Id);
                    table.UniqueConstraint("AK_Observations_Id_ProviderIdentityId_Type_Version_AvailableAt~", x => new { x.Id, x.ProviderIdentityId, x.Type, x.Version, x.AvailableAtUtc });
                    table.CheckConstraint("CK_Observations_Availability", "\"AvailableAtUtc\" >= \"RetrievedAtUtc\" AND \"CreatedAtUtc\" >= \"RetrievedAtUtc\"");
                    table.CheckConstraint("CK_Observations_Correction", "(\"Version\" = 1 AND \"CorrectsObservationId\" IS NULL AND \"CorrectedVersion\" IS NULL AND \"CorrectedAvailableAtUtc\" IS NULL) OR (\"Version\" > 1 AND \"CorrectsObservationId\" IS NOT NULL AND \"CorrectedVersion\" IS NOT NULL AND \"CorrectedAvailableAtUtc\" IS NOT NULL AND \"CorrectedVersion\" = \"Version\" - 1 AND \"AvailableAtUtc\" >= \"CorrectedAvailableAtUtc\")");
                    table.CheckConstraint("CK_Observations_Target", "(\"CanonicalSportId\" IS NULL AND \"CanonicalCompetitionId\" IS NULL AND \"CanonicalSeasonId\" IS NULL AND \"CanonicalParticipantId\" IS NULL AND \"CanonicalSportingEventId\" IS NULL) OR (\"EntityKind\" = 'Sport' AND \"CanonicalSportId\" IS NOT NULL AND \"CanonicalCompetitionId\" IS NULL AND \"CanonicalSeasonId\" IS NULL AND \"CanonicalParticipantId\" IS NULL AND \"CanonicalSportingEventId\" IS NULL) OR (\"EntityKind\" = 'Competition' AND \"CanonicalCompetitionId\" IS NOT NULL AND \"CanonicalSportId\" IS NULL AND \"CanonicalSeasonId\" IS NULL AND \"CanonicalParticipantId\" IS NULL AND \"CanonicalSportingEventId\" IS NULL) OR (\"EntityKind\" = 'Season' AND \"CanonicalSeasonId\" IS NOT NULL AND \"CanonicalSportId\" IS NULL AND \"CanonicalCompetitionId\" IS NULL AND \"CanonicalParticipantId\" IS NULL AND \"CanonicalSportingEventId\" IS NULL) OR (\"EntityKind\" = 'Participant' AND \"CanonicalParticipantId\" IS NOT NULL AND \"CanonicalSportId\" IS NULL AND \"CanonicalCompetitionId\" IS NULL AND \"CanonicalSeasonId\" IS NULL AND \"CanonicalSportingEventId\" IS NULL) OR (\"EntityKind\" = 'SportingEvent' AND \"CanonicalSportingEventId\" IS NOT NULL AND \"CanonicalSportId\" IS NULL AND \"CanonicalCompetitionId\" IS NULL AND \"CanonicalSeasonId\" IS NULL AND \"CanonicalParticipantId\" IS NULL)");
                    table.CheckConstraint("CK_Observations_Value", "(\"Type\" = 'DisplayName' AND \"EntityKind\" <> 'SportingEvent' AND \"TextValue\" IS NOT NULL AND length(btrim(\"TextValue\")) > 0 AND \"TimestampValueUtc\" IS NULL AND \"StatusValue\" IS NULL) OR (\"Type\" = 'ScheduledStart' AND \"EntityKind\" = 'SportingEvent' AND \"TextValue\" IS NULL AND \"TimestampValueUtc\" IS NOT NULL AND \"StatusValue\" IS NULL) OR (\"Type\" = 'EventStatus' AND \"EntityKind\" = 'SportingEvent' AND \"TextValue\" IS NULL AND \"TimestampValueUtc\" IS NULL AND \"StatusValue\" IS NOT NULL AND \"StatusValue\" IN ('Scheduled','InProgress','Completed','Postponed','Cancelled'))");
                    table.ForeignKey(
                        name: "FK_Observations_Competitions_CanonicalCompetitionId",
                        column: x => x.CanonicalCompetitionId,
                        principalSchema: "canonical",
                        principalTable: "Competitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Observations_Observations_CorrectsObservationId_ProviderIde~",
                        columns: x => new { x.CorrectsObservationId, x.ProviderIdentityId, x.Type, x.CorrectedVersion, x.CorrectedAvailableAtUtc },
                        principalSchema: "provenance",
                        principalTable: "Observations",
                        principalColumns: new[] { "Id", "ProviderIdentityId", "Type", "Version", "AvailableAtUtc" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Observations_Participants_CanonicalParticipantId",
                        column: x => x.CanonicalParticipantId,
                        principalSchema: "canonical",
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Observations_ProviderIdentities_ProviderIdentityId_DataSour~",
                        columns: x => new { x.ProviderIdentityId, x.DataSourceId, x.EntityKind },
                        principalSchema: "provenance",
                        principalTable: "ProviderIdentities",
                        principalColumns: new[] { "Id", "DataSourceId", "EntityKind" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Observations_RawPayloads_RawPayloadId_DataSourceId",
                        columns: x => new { x.RawPayloadId, x.DataSourceId },
                        principalSchema: "ingestion",
                        principalTable: "RawPayloads",
                        principalColumns: new[] { "Id", "DataSourceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Observations_Seasons_CanonicalSeasonId",
                        column: x => x.CanonicalSeasonId,
                        principalSchema: "canonical",
                        principalTable: "Seasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Observations_SportingEvents_CanonicalSportingEventId",
                        column: x => x.CanonicalSportingEventId,
                        principalSchema: "canonical",
                        principalTable: "SportingEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Observations_Sports_CanonicalSportId",
                        column: x => x.CanonicalSportId,
                        principalSchema: "canonical",
                        principalTable: "Sports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "canonical",
                table: "Sports",
                columns: new[] { "Id", "Code", "DisplayName" },
                values: new object[,]
                {
                    { new Guid("10000000-0000-0000-0000-000000000001"), "football", "Football" },
                    { new Guid("10000000-0000-0000-0000-000000000002"), "tennis", "Tennis" },
                    { new Guid("10000000-0000-0000-0000-000000000003"), "basketball", "Basketball" },
                    { new Guid("10000000-0000-0000-0000-000000000004"), "ice-hockey", "IceHockey" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Competitions_SportId",
                schema: "canonical",
                table: "Competitions",
                column: "SportId");

            migrationBuilder.CreateIndex(
                name: "IX_EventParticipants_EventId_Position",
                schema: "canonical",
                table: "EventParticipants",
                columns: new[] { "EventId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventParticipants_EventId_SportId",
                schema: "canonical",
                table: "EventParticipants",
                columns: new[] { "EventId", "SportId" });

            migrationBuilder.CreateIndex(
                name: "IX_EventParticipants_ParticipantId_SportId",
                schema: "canonical",
                table: "EventParticipants",
                columns: new[] { "ParticipantId", "SportId" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityResolutions_CanonicalCompetitionId",
                schema: "provenance",
                table: "IdentityResolutions",
                column: "CanonicalCompetitionId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityResolutions_CanonicalParticipantId",
                schema: "provenance",
                table: "IdentityResolutions",
                column: "CanonicalParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityResolutions_CanonicalSeasonId",
                schema: "provenance",
                table: "IdentityResolutions",
                column: "CanonicalSeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityResolutions_CanonicalSportId",
                schema: "provenance",
                table: "IdentityResolutions",
                column: "CanonicalSportId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityResolutions_CanonicalSportingEventId",
                schema: "provenance",
                table: "IdentityResolutions",
                column: "CanonicalSportingEventId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityResolutions_PreviousDecisionId_ProviderIdentityId_P~",
                schema: "provenance",
                table: "IdentityResolutions",
                columns: new[] { "PreviousDecisionId", "ProviderIdentityId", "PreviousVersion", "PreviousDecidedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityResolutions_ProviderIdentityId_DataSourceId_EntityK~",
                schema: "provenance",
                table: "IdentityResolutions",
                columns: new[] { "ProviderIdentityId", "DataSourceId", "EntityKind" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityResolutions_ProviderIdentityId_DecidedAtUtc_Version",
                schema: "provenance",
                table: "IdentityResolutions",
                columns: new[] { "ProviderIdentityId", "DecidedAtUtc", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityResolutions_ProviderIdentityId_Version",
                schema: "provenance",
                table: "IdentityResolutions",
                columns: new[] { "ProviderIdentityId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityResolutions_RawPayloadId_DataSourceId",
                schema: "provenance",
                table: "IdentityResolutions",
                columns: new[] { "RawPayloadId", "DataSourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Observations_CanonicalCompetitionId_AvailableAtUtc_CreatedA~",
                schema: "provenance",
                table: "Observations",
                columns: new[] { "CanonicalCompetitionId", "AvailableAtUtc", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Observations_CanonicalParticipantId_AvailableAtUtc_CreatedA~",
                schema: "provenance",
                table: "Observations",
                columns: new[] { "CanonicalParticipantId", "AvailableAtUtc", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Observations_CanonicalSeasonId_AvailableAtUtc_CreatedAtUtc_~",
                schema: "provenance",
                table: "Observations",
                columns: new[] { "CanonicalSeasonId", "AvailableAtUtc", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Observations_CanonicalSportId_AvailableAtUtc_CreatedAtUtc_Id",
                schema: "provenance",
                table: "Observations",
                columns: new[] { "CanonicalSportId", "AvailableAtUtc", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Observations_CanonicalSportingEventId_AvailableAtUtc_Create~",
                schema: "provenance",
                table: "Observations",
                columns: new[] { "CanonicalSportingEventId", "AvailableAtUtc", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Observations_CorrectsObservationId_ProviderIdentityId_Type_~",
                schema: "provenance",
                table: "Observations",
                columns: new[] { "CorrectsObservationId", "ProviderIdentityId", "Type", "CorrectedVersion", "CorrectedAvailableAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Observations_EntityKind_AvailableAtUtc_CreatedAtUtc_Id",
                schema: "provenance",
                table: "Observations",
                columns: new[] { "EntityKind", "AvailableAtUtc", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Observations_ProviderIdentityId_AvailableAtUtc_CreatedAtUtc~",
                schema: "provenance",
                table: "Observations",
                columns: new[] { "ProviderIdentityId", "AvailableAtUtc", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Observations_ProviderIdentityId_DataSourceId_EntityKind",
                schema: "provenance",
                table: "Observations",
                columns: new[] { "ProviderIdentityId", "DataSourceId", "EntityKind" });

            migrationBuilder.CreateIndex(
                name: "IX_Observations_RawPayloadId_DataSourceId",
                schema: "provenance",
                table: "Observations",
                columns: new[] { "RawPayloadId", "DataSourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Participants_SportId",
                schema: "canonical",
                table: "Participants",
                column: "SportId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderIdentities_DataSourceId_EntityKind_ExternalId",
                schema: "provenance",
                table: "ProviderIdentities",
                columns: new[] { "DataSourceId", "EntityKind", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Seasons_CompetitionId",
                schema: "canonical",
                table: "Seasons",
                column: "CompetitionId");

            migrationBuilder.CreateIndex(
                name: "IX_SportingEvents_CompetitionId_SportId",
                schema: "canonical",
                table: "SportingEvents",
                columns: new[] { "CompetitionId", "SportId" });

            migrationBuilder.CreateIndex(
                name: "IX_SportingEvents_SeasonId_CompetitionId",
                schema: "canonical",
                table: "SportingEvents",
                columns: new[] { "SeasonId", "CompetitionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SportingEvents_SportId_ScheduledStartUtc",
                schema: "canonical",
                table: "SportingEvents",
                columns: new[] { "SportId", "ScheduledStartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Sports_Code",
                schema: "canonical",
                table: "Sports",
                column: "Code",
                unique: true);

            // Covers bulk SQL as well as EF writes. Corrections are INSERTs.
            migrationBuilder.Sql("""
                CREATE FUNCTION provenance.reject_history_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$ BEGIN
                    RAISE EXCEPTION 'Provenance history is append-only' USING ERRCODE = '23514';
                END; $$;
                CREATE TRIGGER prevent_history_mutation BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON provenance."ProviderIdentities" FOR EACH STATEMENT
                    EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE TRIGGER prevent_history_mutation BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON provenance."IdentityResolutions" FOR EACH STATEMENT
                    EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE TRIGGER prevent_history_mutation BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON provenance."Observations" FOR EACH STATEMENT
                    EXECUTE FUNCTION provenance.reject_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventParticipants",
                schema: "canonical");

            migrationBuilder.DropTable(
                name: "IdentityResolutions",
                schema: "provenance");

            migrationBuilder.DropTable(
                name: "Observations",
                schema: "provenance");

            migrationBuilder.DropTable(
                name: "Participants",
                schema: "canonical");

            migrationBuilder.DropTable(
                name: "ProviderIdentities",
                schema: "provenance");

            migrationBuilder.DropTable(
                name: "SportingEvents",
                schema: "canonical");

            migrationBuilder.DropTable(
                name: "Seasons",
                schema: "canonical");

            migrationBuilder.DropTable(
                name: "Competitions",
                schema: "canonical");

            migrationBuilder.DropTable(
                name: "Sports",
                schema: "canonical");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_RawPayloads_Id_DataSourceId",
                schema: "ingestion",
                table: "RawPayloads");
            migrationBuilder.Sql("DROP FUNCTION provenance.reject_history_mutation();");
        }
    }
}
