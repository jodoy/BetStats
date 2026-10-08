using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BetStats.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FootballResultsOutcomeProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "football");

            migrationBuilder.CreateTable(
                name: "ResultArtifacts",
                schema: "datasets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MetadataSnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResultArtifacts", x => x.Id);
                    table.CheckConstraint("CK_ResultArtifacts_Hash", "\"Hash\" ~ '^[0-9a-f]{64}$' AND octet_length(\"Content\") <= 16777216 AND \"SchemaVersion\" = 3");
                    table.ForeignKey(
                        name: "FK_ResultArtifacts_Snapshots_MetadataSnapshotId",
                        column: x => x.MetadataSnapshotId,
                        principalSchema: "datasets",
                        principalTable: "Snapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Results",
                schema: "football",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderIdentityId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompetitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonId = table.Column<Guid>(type: "uuid", nullable: false),
                    HomeId = table.Column<Guid>(type: "uuid", nullable: false),
                    AwayId = table.Column<Guid>(type: "uuid", nullable: false),
                    DateObservationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceEventReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CompetitionReference = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SeasonReference = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EventDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Value = table.Column<string>(type: "jsonb", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CorrectsId = table.Column<Guid>(type: "uuid", nullable: true),
                    PublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetrievedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AvailableAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Results", x => x.Id);
                    table.CheckConstraint("CK_Results_Context", "\"HomeId\" <> \"AwayId\" AND length(btrim(\"SourceEventReference\")) > 0 AND length(btrim(\"CompetitionReference\")) > 0 AND length(btrim(\"SeasonReference\")) > 0");
                    table.CheckConstraint("CK_Results_Times", "\"AvailableAtUtc\" >= \"RetrievedAtUtc\" AND \"AvailableAtUtc\" <= \"RecordedAtUtc\" AND (\"PublishedAtUtc\" IS NULL OR \"PublishedAtUtc\" <= \"RetrievedAtUtc\")");
                    table.CheckConstraint("CK_Results_Version", "\"SchemaVersion\" = 1 AND \"Version\" > 0 AND ((\"Version\" = 1 AND \"CorrectsId\" IS NULL) OR (\"Version\" > 1 AND \"CorrectsId\" IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_Results_Competitions_CompetitionId",
                        column: x => x.CompetitionId,
                        principalSchema: "canonical",
                        principalTable: "Competitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Results_Observations_DateObservationId",
                        column: x => x.DateObservationId,
                        principalSchema: "provenance",
                        principalTable: "Observations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Results_Participants_AwayId",
                        column: x => x.AwayId,
                        principalSchema: "canonical",
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Results_Participants_HomeId",
                        column: x => x.HomeId,
                        principalSchema: "canonical",
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Results_ProviderIdentities_ProviderIdentityId",
                        column: x => x.ProviderIdentityId,
                        principalSchema: "provenance",
                        principalTable: "ProviderIdentities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Results_RawPayloads_RawId_SourceId",
                        columns: x => new { x.RawId, x.SourceId },
                        principalSchema: "ingestion",
                        principalTable: "RawPayloads",
                        principalColumns: new[] { "Id", "DataSourceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Results_Results_CorrectsId",
                        column: x => x.CorrectsId,
                        principalSchema: "football",
                        principalTable: "Results",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Results_Seasons_SeasonId",
                        column: x => x.SeasonId,
                        principalSchema: "canonical",
                        principalTable: "Seasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Results_SportingEvents_EventId",
                        column: x => x.EventId,
                        principalSchema: "canonical",
                        principalTable: "SportingEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResultArtifacts_Hash",
                schema: "datasets",
                table: "ResultArtifacts",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResultArtifacts_MetadataSnapshotId",
                schema: "datasets",
                table: "ResultArtifacts",
                column: "MetadataSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_Results_AwayId",
                schema: "football",
                table: "Results",
                column: "AwayId");

            migrationBuilder.CreateIndex(
                name: "IX_Results_CompetitionId",
                schema: "football",
                table: "Results",
                column: "CompetitionId");

            migrationBuilder.CreateIndex(
                name: "IX_Results_CorrectsId",
                schema: "football",
                table: "Results",
                column: "CorrectsId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Results_DateObservationId",
                schema: "football",
                table: "Results",
                column: "DateObservationId");

            migrationBuilder.CreateIndex(
                name: "IX_Results_EventId_AvailableAtUtc_RecordedAtUtc",
                schema: "football",
                table: "Results",
                columns: new[] { "EventId", "AvailableAtUtc", "RecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Results_HomeId",
                schema: "football",
                table: "Results",
                column: "HomeId");

            migrationBuilder.CreateIndex(
                name: "IX_Results_ProviderIdentityId_Version",
                schema: "football",
                table: "Results",
                columns: new[] { "ProviderIdentityId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Results_RawId_SourceId",
                schema: "football",
                table: "Results",
                columns: new[] { "RawId", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Results_SeasonId",
                schema: "football",
                table: "Results",
                column: "SeasonId");
            migrationBuilder.Sql("""
                CREATE FUNCTION football.record_result() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE prior football."Results"; value jsonb; score jsonb;
                BEGIN
                  NEW."RecordedAtUtc" := clock_timestamp(); value := NEW."Value";
                  IF NOT EXISTS (SELECT 1 FROM ingestion."FootballRawContexts" c JOIN ingestion."RawPayloads" raw ON raw."Id" = c."RawId"
                    WHERE c."RawId" = NEW."RawId" AND c."SourceId" = NEW."SourceId"
                    AND c."CompetitionReference" = NEW."CompetitionReference" AND c."SeasonReference" = NEW."SeasonReference"
                    AND raw."RetrievedAtUtc" = NEW."RetrievedAtUtc" AND raw."RecordedAtUtc" <= NEW."AvailableAtUtc") THEN
                    RAISE EXCEPTION 'Result original context mismatch'; END IF;
                  IF NOT EXISTS (SELECT 1 FROM provenance."ProviderIdentities" i WHERE i."Id" = NEW."ProviderIdentityId"
                    AND i."DataSourceId" = NEW."SourceId" AND i."ExternalId" = NEW."SourceEventReference") THEN
                    RAISE EXCEPTION 'Result source identity mismatch'; END IF;
                  IF NOT EXISTS (SELECT 1 FROM provenance."Observations" o WHERE o."Id" = NEW."DateObservationId"
                    AND o."ProviderIdentityId" = NEW."ProviderIdentityId" AND o."CanonicalSportingEventId" = NEW."EventId"
                    AND o."DateValue" = NEW."EventDate" AND o."AvailableAtUtc" <= NEW."AvailableAtUtc") THEN
                    RAISE EXCEPTION 'Result date identity mismatch'; END IF;
                  IF jsonb_typeof(value) <> 'object' OR NOT value ?& ARRAY['Status','Basis','FullTime','HalfTime']
                    OR jsonb_typeof(value->'Status') <> 'string' OR jsonb_typeof(value->'Basis') <> 'string'
                    OR value->>'Status' NOT IN ('Scheduled','Live','HalfTime','Finished','Postponed','Cancelled','Abandoned')
                    OR value->>'Basis' NOT IN ('Unknown','RegulationTime','IncludesExtraTime','PenaltyShootout') THEN RAISE EXCEPTION 'Invalid result vocabulary'; END IF;
                  FOREACH score IN ARRAY ARRAY[value->'FullTime',value->'HalfTime'] LOOP
                    IF score IS NULL OR NOT score ? 'Home' OR NOT score ? 'Away' OR
                      ((score->>'Home') IS NULL) <> ((score->>'Away') IS NULL) THEN RAISE EXCEPTION 'Invalid result score pair'; END IF;
                    IF score->>'Home' IS NOT NULL AND (jsonb_typeof(score->'Home') <> 'number' OR jsonb_typeof(score->'Away') <> 'number'
                      OR score->>'Home' !~ '^[0-9]+$' OR score->>'Away' !~ '^[0-9]+$'
                      OR (score->>'Home')::bigint + (score->>'Away')::bigint > 2147483647) THEN RAISE EXCEPTION 'Invalid result goals'; END IF;
                  END LOOP;
                  IF value->>'Basis' = 'RegulationTime' AND ((value->'HalfTime'->>'Home')::int > (value->'FullTime'->>'Home')::int
                    OR (value->'HalfTime'->>'Away')::int > (value->'FullTime'->>'Away')::int) THEN RAISE EXCEPTION 'Half time exceeds full time'; END IF;
                  IF NEW."CorrectsId" IS NOT NULL THEN
                    SELECT * INTO prior FROM football."Results" WHERE "Id" = NEW."CorrectsId";
                    IF prior."ProviderIdentityId" <> NEW."ProviderIdentityId" OR prior."SourceId" <> NEW."SourceId" OR prior."EventId" <> NEW."EventId"
                      OR prior."CompetitionId" <> NEW."CompetitionId" OR prior."SeasonId" <> NEW."SeasonId" OR prior."HomeId" <> NEW."HomeId" OR prior."AwayId" <> NEW."AwayId"
                      OR prior."Version" + 1 <> NEW."Version" OR prior."AvailableAtUtc" > NEW."AvailableAtUtc" OR prior."RetrievedAtUtc" >= NEW."RetrievedAtUtc"
                      OR prior."Value" <> NEW."Value" AND prior."PublishedAtUtc" IS NOT NULL AND NEW."PublishedAtUtc" IS NOT NULL AND prior."PublishedAtUtc" >= NEW."PublishedAtUtc"
                      THEN RAISE EXCEPTION 'Invalid result correction chain'; END IF;
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER trusted_result BEFORE INSERT ON football."Results" FOR EACH ROW EXECUTE FUNCTION football.record_result();
                CREATE TRIGGER immutable_results BEFORE UPDATE OR DELETE OR TRUNCATE ON football."Results" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE FUNCTION datasets.record_result_artifact() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN NEW."RecordedAtUtc" := clock_timestamp(); RETURN NEW; END $$;
                CREATE TRIGGER trusted_result_artifact BEFORE INSERT ON datasets."ResultArtifacts" FOR EACH ROW EXECUTE FUNCTION datasets.record_result_artifact();
                CREATE TRIGGER immutable_result_artifacts BEFORE UPDATE OR DELETE OR TRUNCATE ON datasets."ResultArtifacts" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResultArtifacts",
                schema: "datasets");

            migrationBuilder.DropTable(
                name: "Results",
                schema: "football");
            migrationBuilder.Sql("DROP FUNCTION football.record_result(); DROP FUNCTION datasets.record_result_artifact();");
        }
    }
}
