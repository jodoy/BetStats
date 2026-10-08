using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BetStats.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CoverageEventTimeEvaluation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Snapshots_Content",
                schema: "datasets",
                table: "Snapshots");

            migrationBuilder.EnsureSchema(
                name: "coverage");

            migrationBuilder.CreateTable(
                name: "EventTimes",
                schema: "coverage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderIdentityId = table.Column<Guid>(type: "uuid", nullable: false),
                    DateObservationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawHash = table.Column<string>(type: "text", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<string>(type: "jsonb", nullable: false),
                    EvidenceReference = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CorrectsId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    SourcePublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetrievedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AvailableAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OperatorId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventTimes", x => x.Id);
                    table.CheckConstraint("CK_EventTime_Version", "\"Version\" > 0 AND \"AvailableAtUtc\" >= \"RetrievedAtUtc\" AND \"AvailableAtUtc\" <= \"RecordedAtUtc\" AND \"RawHash\" ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_EventTimes_EventTimes_CorrectsId",
                        column: x => x.CorrectsId,
                        principalSchema: "coverage",
                        principalTable: "EventTimes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventTimes_Observations_DateObservationId",
                        column: x => x.DateObservationId,
                        principalSchema: "provenance",
                        principalTable: "Observations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventTimes_ProviderIdentities_ProviderIdentityId",
                        column: x => x.ProviderIdentityId,
                        principalSchema: "provenance",
                        principalTable: "ProviderIdentities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventTimes_RawPayloads_RawId_SourceId",
                        columns: x => new { x.RawId, x.SourceId },
                        principalSchema: "ingestion",
                        principalTable: "RawPayloads",
                        principalColumns: new[] { "Id", "DataSourceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventTimes_SourcePolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalSchema: "governance",
                        principalTable: "SourcePolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Evidence",
                schema: "coverage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<string>(type: "jsonb", nullable: false),
                    Claim = table.Column<string>(type: "text", nullable: false),
                    Basis = table.Column<string>(type: "text", nullable: false),
                    EvidenceReference = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    RawId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawHash = table.Column<string>(type: "text", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupportingObservationIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    SourcePublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetrievedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AvailableAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OperatorId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Evidence", x => x.Id);
                    table.CheckConstraint("CK_CoverageEvidence_Claim", "\"Claim\" IN ('Unknown','Partial','VerifiedComplete','VerifiedEmpty') AND \"Basis\" IN ('ManualStatement','OwnedFixtureInventory')");
                    table.CheckConstraint("CK_CoverageEvidence_Hash", "\"RawHash\" ~ '^[0-9a-f]{64}$' AND length(btrim(\"EvidenceReference\")) > 0 AND cardinality(\"SupportingObservationIds\") <= 1000");
                    table.CheckConstraint("CK_CoverageEvidence_Version", "\"Version\" > 0 AND \"AvailableAtUtc\" >= \"RetrievedAtUtc\" AND \"AvailableAtUtc\" <= \"RecordedAtUtc\" AND \"ValidUntilUtc\" > \"AvailableAtUtc\"");
                    table.ForeignKey(
                        name: "FK_Evidence_RawPayloads_RawId_SourceId",
                        columns: x => new { x.RawId, x.SourceId },
                        principalSchema: "ingestion",
                        principalTable: "RawPayloads",
                        principalColumns: new[] { "Id", "DataSourceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Evidence_SourcePolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalSchema: "governance",
                        principalTable: "SourcePolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Reviews",
                schema: "coverage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EvidenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    BasisReference = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    OperatorId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reviews", x => x.Id);
                    table.CheckConstraint("CK_CoverageReview_State", "\"Sequence\" > 0 AND \"Status\" IN ('Approved','Rejected') AND \"ReviewedAtUtc\" <= \"RecordedAtUtc\" AND length(btrim(\"BasisReference\")) > 0");
                    table.ForeignKey(
                        name: "FK_Reviews_Evidence_EvidenceId",
                        column: x => x.EvidenceId,
                        principalSchema: "coverage",
                        principalTable: "Evidence",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Snapshots_Content",
                schema: "datasets",
                table: "Snapshots",
                sql: "\"RowCount\" BETWEEN 1 AND 100 AND \"FeatureSchemaVersion\" IN (1,2) AND octet_length(\"Content\") BETWEEN 1 AND 16777216");

            migrationBuilder.CreateIndex(
                name: "IX_EventTimes_CorrectsId",
                schema: "coverage",
                table: "EventTimes",
                column: "CorrectsId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventTimes_DateObservationId",
                schema: "coverage",
                table: "EventTimes",
                column: "DateObservationId");

            migrationBuilder.CreateIndex(
                name: "IX_EventTimes_PolicyId",
                schema: "coverage",
                table: "EventTimes",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_EventTimes_ProviderIdentityId_RecordedAtUtc_Id",
                schema: "coverage",
                table: "EventTimes",
                columns: new[] { "ProviderIdentityId", "RecordedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_EventTimes_RawId_SourceId",
                schema: "coverage",
                table: "EventTimes",
                columns: new[] { "RawId", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Evidence_PolicyId",
                schema: "coverage",
                table: "Evidence",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_Evidence_RawId_SourceId",
                schema: "coverage",
                table: "Evidence",
                columns: new[] { "RawId", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Evidence_SourceId_RecordedAtUtc_Id",
                schema: "coverage",
                table: "Evidence",
                columns: new[] { "SourceId", "RecordedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_EvidenceId_Sequence",
                schema: "coverage",
                table: "Reviews",
                columns: new[] { "EvidenceId", "Sequence" },
                unique: true);
            migrationBuilder.Sql("""
                CREATE FUNCTION coverage.record_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE evidence coverage."Evidence"%ROWTYPE;
                        previous coverage."EventTimes"%ROWTYPE;
                BEGIN
                  IF current_setting('transaction_isolation') NOT IN ('read committed','serializable')
                  THEN RAISE EXCEPTION 'Coverage write requires READ COMMITTED or SERIALIZABLE' USING ERRCODE='23514'; END IF;
                  PERFORM 1 FROM ingestion."DataSources" WHERE "Id"=NEW."SourceId" FOR UPDATE;
                  NEW."RecordedAtUtc" := clock_timestamp();
                  IF length(btrim(NEW."OperatorId"))=0 OR length(btrim(NEW."Reason"))=0
                  THEN RAISE EXCEPTION 'Operator and reason required' USING ERRCODE='23514'; END IF;
                  IF TG_TABLE_NAME='Reviews' THEN
                    SELECT * INTO evidence FROM coverage."Evidence" WHERE "Id"=NEW."EvidenceId";
                    IF evidence."SourceId" IS DISTINCT FROM NEW."SourceId" OR NEW."Sequence" <> COALESCE((SELECT max("Sequence") FROM coverage."Reviews" WHERE "EvidenceId"=NEW."EvidenceId"),0)+1
                       OR NEW."ReviewedAtUtc" < evidence."RecordedAtUtc"
                    THEN RAISE EXCEPTION 'Invalid review stream' USING ERRCODE='23514'; END IF;
                    IF NEW."Status"='Approved' AND evidence."Claim" IN ('VerifiedComplete','VerifiedEmpty') AND
                       (evidence."Basis" <> 'OwnedFixtureInventory' OR NEW."BasisReference" <> 'project-owned-fixture-inventory-v1' OR
                        evidence."ValidUntilUtc" <= NEW."RecordedAtUtc" OR NOT EXISTS (SELECT 1 FROM governance."SourcePolicies" WHERE "Id"=evidence."PolicyId" AND "TermsReference"='synthetic:owned-fixture'))
                    THEN RAISE EXCEPTION 'Unsupported completeness approval' USING ERRCODE='23514'; END IF;
                  ELSE
                    IF NEW."SourcePublishedAtUtc" > NEW."AvailableAtUtc" OR NOT EXISTS
                       (SELECT 1 FROM ingestion."RawPayloads" WHERE "Id"=NEW."RawId" AND "DataSourceId"=NEW."SourceId" AND
                        "ContentHashSha256"=NEW."RawHash" AND "RetrievedAtUtc"=NEW."RetrievedAtUtc") OR NOT EXISTS
                       (SELECT 1 FROM governance."SourcePolicies" WHERE "Id"=NEW."PolicyId" AND "DataSourceId"=NEW."SourceId")
                    THEN RAISE EXCEPTION 'Coverage provenance mismatch' USING ERRCODE='23514'; END IF;
                    IF TG_TABLE_NAME='Evidence' THEN
                      IF (NEW."Scope"->>'SourceId')::uuid IS DISTINCT FROM NEW."SourceId" OR
                         NEW."Scope"->>'EventType' IS DISTINCT FROM 'football-match' OR
                         NEW."Scope"->>'ObservationType' NOT IN ('EventDate','EventStatus') OR
                         NOT EXISTS (SELECT 1 FROM canonical."Seasons" s JOIN canonical."Competitions" c ON c."Id"=s."CompetitionId"
                           WHERE s."Id"=(NEW."Scope"->>'SeasonId')::uuid AND c."Id"=(NEW."Scope"->>'CompetitionId')::uuid AND c."SportId"=(NEW."Scope"->>'SportId')::uuid) OR
                         EXISTS (SELECT 1 FROM unnest(NEW."SupportingObservationIds") id WHERE NOT EXISTS
                           (SELECT 1 FROM provenance."Observations" o WHERE o."Id"=id AND o."DataSourceId"=NEW."SourceId" AND o."Type"=NEW."Scope"->>'ObservationType'))
                      THEN RAISE EXCEPTION 'Coverage scope mismatch' USING ERRCODE='23514'; END IF;
                    ELSE
                      IF NOT EXISTS (SELECT 1 FROM provenance."Observations" WHERE "Id"=NEW."DateObservationId" AND "Type"='EventDate' AND
                          "DataSourceId"=NEW."SourceId" AND "ProviderIdentityId"=NEW."ProviderIdentityId")
                      THEN RAISE EXCEPTION 'Event-time observation mismatch' USING ERRCODE='23514'; END IF;
                      IF NEW."CorrectsId" IS NOT NULL THEN
                        SELECT * INTO previous FROM coverage."EventTimes" WHERE "Id"=NEW."CorrectsId";
                        IF previous."ProviderIdentityId" IS DISTINCT FROM NEW."ProviderIdentityId" OR previous."SourceId" IS DISTINCT FROM NEW."SourceId" OR
                           NEW."Version" <> previous."Version"+1 OR NEW."AvailableAtUtc" < previous."AvailableAtUtc"
                        THEN RAISE EXCEPTION 'Invalid time correction' USING ERRCODE='23514'; END IF;
                      ELSIF NEW."Version" <> 1 THEN RAISE EXCEPTION 'Initial time version must be one' USING ERRCODE='23514'; END IF;
                    END IF;
                  END IF;
                  RETURN NEW;
                END; $$;
                CREATE TRIGGER trusted_evidence BEFORE INSERT ON coverage."Evidence" FOR EACH ROW EXECUTE FUNCTION coverage.record_insert();
                CREATE TRIGGER trusted_review BEFORE INSERT ON coverage."Reviews" FOR EACH ROW EXECUTE FUNCTION coverage.record_insert();
                CREATE TRIGGER trusted_time BEFORE INSERT ON coverage."EventTimes" FOR EACH ROW EXECUTE FUNCTION coverage.record_insert();
                CREATE TRIGGER immutable_evidence BEFORE UPDATE OR DELETE OR TRUNCATE ON coverage."Evidence" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE TRIGGER immutable_review BEFORE UPDATE OR DELETE OR TRUNCATE ON coverage."Reviews" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE TRIGGER immutable_time BEFORE UPDATE OR DELETE OR TRUNCATE ON coverage."EventTimes" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventTimes",
                schema: "coverage");

            migrationBuilder.DropTable(
                name: "Reviews",
                schema: "coverage");

            migrationBuilder.DropTable(
                name: "Evidence",
                schema: "coverage");
            migrationBuilder.Sql("DROP FUNCTION coverage.record_insert();");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Snapshots_Content",
                schema: "datasets",
                table: "Snapshots");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Snapshots_Content",
                schema: "datasets",
                table: "Snapshots",
                sql: "\"RowCount\" BETWEEN 1 AND 100 AND \"FeatureSchemaVersion\" = 1 AND octet_length(\"Content\") BETWEEN 1 AND 16777216");
        }
    }
}
