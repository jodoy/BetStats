using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BetStats.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ResultCoverageOperationsEventEnd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventEnds",
                schema: "football",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResultObservationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawHash = table.Column<string>(type: "text", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdentityDecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<string>(type: "jsonb", nullable: false),
                    CorrectsId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetrievedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AvailableAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OperatorId = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventEnds", x => x.Id);
                    table.CheckConstraint("CK_EventEnds_Time", "\"AvailableAtUtc\" >= \"RetrievedAtUtc\" AND \"AvailableAtUtc\" <= \"RecordedAtUtc\" AND (\"PublishedAtUtc\" IS NULL OR \"PublishedAtUtc\" <= \"RetrievedAtUtc\")");
                    table.CheckConstraint("CK_EventEnds_Version", "\"Version\" > 0 AND ((\"Version\" = 1 AND \"CorrectsId\" IS NULL) OR (\"Version\" > 1 AND \"CorrectsId\" IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_EventEnds_EventEnds_CorrectsId",
                        column: x => x.CorrectsId,
                        principalSchema: "football",
                        principalTable: "EventEnds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventEnds_IdentityResolutions_IdentityDecisionId",
                        column: x => x.IdentityDecisionId,
                        principalSchema: "provenance",
                        principalTable: "IdentityResolutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventEnds_RawPayloads_RawId_SourceId",
                        columns: x => new { x.RawId, x.SourceId },
                        principalSchema: "ingestion",
                        principalTable: "RawPayloads",
                        principalColumns: new[] { "Id", "DataSourceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventEnds_Results_ResultObservationId",
                        column: x => x.ResultObservationId,
                        principalSchema: "football",
                        principalTable: "Results",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EventEnds_SourcePolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalSchema: "governance",
                        principalTable: "SourcePolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ResultInventory",
                schema: "coverage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<string>(type: "jsonb", nullable: false),
                    Claim = table.Column<string>(type: "text", nullable: false),
                    RawId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawHash = table.Column<string>(type: "text", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CorrectsId = table.Column<Guid>(type: "uuid", nullable: true),
                    PublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetrievedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AvailableAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OperatorId = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResultInventory", x => x.Id);
                    table.CheckConstraint("CK_ResultInventory_Claim", "\"Claim\" IN ('Unknown','Partial','Complete','Empty') AND \"Version\" > 0");
                    table.CheckConstraint("CK_ResultInventory_Time", "\"AvailableAtUtc\" >= \"RetrievedAtUtc\" AND \"AvailableAtUtc\" <= \"RecordedAtUtc\" AND \"ValidUntilUtc\" > \"AvailableAtUtc\" AND (\"PublishedAtUtc\" IS NULL OR \"PublishedAtUtc\" <= \"RetrievedAtUtc\")");
                    table.ForeignKey(
                        name: "FK_ResultInventory_RawPayloads_RawId_SourceId",
                        columns: x => new { x.RawId, x.SourceId },
                        principalSchema: "ingestion",
                        principalTable: "RawPayloads",
                        principalColumns: new[] { "Id", "DataSourceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResultInventory_ResultInventory_CorrectsId",
                        column: x => x.CorrectsId,
                        principalSchema: "coverage",
                        principalTable: "ResultInventory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResultInventory_SourcePolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalSchema: "governance",
                        principalTable: "SourcePolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ResultOperations",
                schema: "datasets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Request = table.Column<byte[]>(type: "bytea", nullable: false),
                    OwnerToken = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    FailureCode = table.Column<string>(type: "text", nullable: true),
                    OperatorId = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResultOperations", x => x.Id);
                    table.CheckConstraint("CK_ResultOperations_State", "\"Sequence\" > 0 AND \"Status\" IN ('Requested','Running','Failed','Cancelled','Succeeded') AND \"Fingerprint\" ~ '^[0-9a-f]{64}$' AND octet_length(\"Request\") <= 1048576 AND ((\"Status\" = 'Succeeded') = (\"SnapshotId\" IS NOT NULL)) AND (\"Status\" <> 'Running' OR \"LeaseUntilUtc\" > \"RecordedAtUtc\")");
                    table.ForeignKey(
                        name: "FK_ResultOperations_ResultArtifacts_SnapshotId",
                        column: x => x.SnapshotId,
                        principalSchema: "datasets",
                        principalTable: "ResultArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ResultInventoryReviews",
                schema: "coverage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EvidenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Approved = table.Column<bool>(type: "boolean", nullable: false),
                    OperatorId = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResultInventoryReviews", x => x.Id);
                    table.CheckConstraint("CK_ResultInventoryReviews_Sequence", "\"Sequence\" > 0");
                    table.ForeignKey(
                        name: "FK_ResultInventoryReviews_ResultInventory_EvidenceId",
                        column: x => x.EvidenceId,
                        principalSchema: "coverage",
                        principalTable: "ResultInventory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventEnds_CorrectsId",
                schema: "football",
                table: "EventEnds",
                column: "CorrectsId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventEnds_IdentityDecisionId",
                schema: "football",
                table: "EventEnds",
                column: "IdentityDecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_EventEnds_PolicyId",
                schema: "football",
                table: "EventEnds",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_EventEnds_RawId_SourceId",
                schema: "football",
                table: "EventEnds",
                columns: new[] { "RawId", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_EventEnds_ResultObservationId",
                schema: "football",
                table: "EventEnds",
                column: "ResultObservationId");

            migrationBuilder.CreateIndex(
                name: "IX_ResultInventory_CorrectsId",
                schema: "coverage",
                table: "ResultInventory",
                column: "CorrectsId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResultInventory_PolicyId",
                schema: "coverage",
                table: "ResultInventory",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_ResultInventory_RawId_SourceId",
                schema: "coverage",
                table: "ResultInventory",
                columns: new[] { "RawId", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_ResultInventoryReviews_EvidenceId_Sequence",
                schema: "coverage",
                table: "ResultInventoryReviews",
                columns: new[] { "EvidenceId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResultOperations_OperationId_Sequence",
                schema: "datasets",
                table: "ResultOperations",
                columns: new[] { "OperationId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResultOperations_SnapshotId",
                schema: "datasets",
                table: "ResultOperations",
                column: "SnapshotId");
            migrationBuilder.Sql("""
                CREATE FUNCTION coverage.record_result_inventory() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE raw ingestion."RawPayloads"; prior coverage."ResultInventory";
                BEGIN
                  NEW."RecordedAtUtc" := clock_timestamp();
                  SELECT * INTO raw FROM ingestion."RawPayloads" WHERE "Id"=NEW."RawId" AND "DataSourceId"=NEW."SourceId";
                  IF NOT FOUND OR raw."ContentHashSha256" <> NEW."RawHash" OR raw."RetrievedAtUtc" <> NEW."RetrievedAtUtc"
                    OR NEW."Scope"->>'SourceId' IS NULL OR (NEW."Scope"->>'SourceId')::uuid <> NEW."SourceId"
                    OR NOT EXISTS (SELECT 1 FROM governance."SourcePolicies" WHERE "Id"=NEW."PolicyId" AND "DataSourceId"=NEW."SourceId")
                    THEN RAISE EXCEPTION 'Invalid result inventory source/RAW binding'; END IF;
                  IF NEW."CorrectsId" IS NULL THEN
                    IF NEW."Version" <> 1 THEN RAISE EXCEPTION 'Initial inventory version required'; END IF;
                  ELSE
                    SELECT * INTO prior FROM coverage."ResultInventory" WHERE "Id"=NEW."CorrectsId";
                    IF prior."Scope" <> NEW."Scope" OR prior."SourceId" <> NEW."SourceId" OR prior."Version"+1 <> NEW."Version"
                      OR prior."AvailableAtUtc" >= NEW."AvailableAtUtc" OR prior."RetrievedAtUtc" >= NEW."RetrievedAtUtc"
                      THEN RAISE EXCEPTION 'Invalid inventory correction'; END IF;
                  END IF;
                  IF length(btrim(NEW."OperatorId"))=0 OR length(btrim(NEW."Reason"))=0 THEN RAISE EXCEPTION 'Actor and reason required'; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER trusted_result_inventory BEFORE INSERT ON coverage."ResultInventory" FOR EACH ROW EXECUTE FUNCTION coverage.record_result_inventory();
                CREATE TRIGGER immutable_result_inventory BEFORE UPDATE OR DELETE OR TRUNCATE ON coverage."ResultInventory" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE FUNCTION coverage.record_result_review() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE source uuid; sequence integer;
                BEGIN
                  SELECT "SourceId" INTO source FROM coverage."ResultInventory" WHERE "Id"=NEW."EvidenceId";
                  PERFORM 1 FROM ingestion."DataSources" WHERE "Id"=source FOR UPDATE;
                  SELECT COALESCE(max("Sequence"),0) INTO sequence FROM coverage."ResultInventoryReviews" WHERE "EvidenceId"=NEW."EvidenceId";
                  IF NEW."Sequence" <> sequence+1 THEN RAISE EXCEPTION 'Invalid review sequence'; END IF;
                  NEW."RecordedAtUtc" := clock_timestamp();
                  IF length(btrim(NEW."OperatorId"))=0 OR length(btrim(NEW."Reason"))=0 THEN RAISE EXCEPTION 'Actor and reason required'; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER trusted_result_review BEFORE INSERT ON coverage."ResultInventoryReviews" FOR EACH ROW EXECUTE FUNCTION coverage.record_result_review();
                CREATE TRIGGER immutable_result_reviews BEFORE UPDATE OR DELETE OR TRUNCATE ON coverage."ResultInventoryReviews" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE FUNCTION football.record_event_end() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE raw ingestion."RawPayloads"; result football."Results"; prior football."EventEnds"; original football."Results";
                BEGIN
                  NEW."RecordedAtUtc" := clock_timestamp();
                  SELECT * INTO raw FROM ingestion."RawPayloads" WHERE "Id"=NEW."RawId" AND "DataSourceId"=NEW."SourceId";
                  IF NOT FOUND OR raw."ContentHashSha256" <> NEW."RawHash" OR raw."RetrievedAtUtc" <> NEW."RetrievedAtUtc" THEN RAISE EXCEPTION 'Invalid event-end RAW'; END IF;
                  SELECT * INTO result FROM football."Results" WHERE "Id"=NEW."ResultObservationId" AND "SourceId"=NEW."SourceId";
                  IF NOT FOUND OR result."Value"->>'Status' <> 'Finished' OR result."Value"->>'Basis' <> 'RegulationTime'
                    OR result."RecordedAtUtc" > NEW."RecordedAtUtc" OR result."AvailableAtUtc" > NEW."AvailableAtUtc"
                    OR NOT EXISTS (SELECT 1 FROM provenance."IdentityResolutions" WHERE "Id"=NEW."IdentityDecisionId" AND "ProviderIdentityId"=result."ProviderIdentityId"
                      AND "Status"='Resolved' AND "CanonicalSportingEventId"=result."EventId" AND "RecordedAtUtc" <= NEW."RecordedAtUtc")
                    OR NOT EXISTS (SELECT 1 FROM governance."SourcePolicies" WHERE "Id"=NEW."PolicyId" AND "DataSourceId"=NEW."SourceId")
                    THEN RAISE EXCEPTION 'Invalid event-end result/identity/policy'; END IF;
                  IF jsonb_typeof(NEW."Value") <> 'object' OR NEW."Value"->>'Precision' IS NULL OR NEW."Value"->>'Precision' NOT IN ('Unknown','DateOnly','Minute','Second') THEN RAISE EXCEPTION 'Invalid end precision'; END IF;
                  IF NEW."CorrectsId" IS NOT NULL THEN
                    SELECT * INTO prior FROM football."EventEnds" WHERE "Id"=NEW."CorrectsId";
                    SELECT * INTO original FROM football."Results" WHERE "Id"=prior."ResultObservationId";
                    IF prior."SourceId" <> NEW."SourceId" OR original."ProviderIdentityId" <> result."ProviderIdentityId" OR prior."Version"+1 <> NEW."Version"
                      OR prior."AvailableAtUtc" >= NEW."AvailableAtUtc" OR prior."RetrievedAtUtc" >= NEW."RetrievedAtUtc"
                      OR prior."PublishedAtUtc" IS NOT NULL AND NEW."PublishedAtUtc" IS NOT NULL AND prior."PublishedAtUtc" >= NEW."PublishedAtUtc"
                      THEN RAISE EXCEPTION 'Invalid event-end correction'; END IF;
                  END IF;
                  IF length(btrim(NEW."OperatorId"))=0 OR length(btrim(NEW."Reason"))=0 THEN RAISE EXCEPTION 'Actor and reason required'; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER trusted_event_end BEFORE INSERT ON football."EventEnds" FOR EACH ROW EXECUTE FUNCTION football.record_event_end();
                CREATE TRIGGER immutable_event_ends BEFORE UPDATE OR DELETE OR TRUNCATE ON football."EventEnds" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE FUNCTION datasets.record_result_operation() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE prior datasets."ResultOperations";
                BEGIN
                  PERFORM pg_advisory_xact_lock(hashtextextended(NEW."OperationId"::text,9010));
                  NEW."RecordedAtUtc" := clock_timestamp();
                  IF NEW."Status"='Running' AND (NEW."LeaseUntilUtc" IS NULL OR NEW."LeaseUntilUtc" <= NEW."RecordedAtUtc" OR NEW."LeaseUntilUtc" > NEW."RecordedAtUtc" + interval '30 minutes')
                    THEN RAISE EXCEPTION 'Bounded explicit DB-clock lease required'; END IF;
                  SELECT * INTO prior FROM datasets."ResultOperations" WHERE "OperationId"=NEW."OperationId" ORDER BY "Sequence" DESC LIMIT 1;
                  IF NOT FOUND THEN
                    IF NEW."Sequence" <> 1 OR NEW."Status" <> 'Requested' OR NEW."OwnerToken" <> '00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'Initial Requested required'; END IF;
                  ELSE
                    IF NEW."Sequence" <> prior."Sequence"+1 OR NEW."Fingerprint" <> prior."Fingerprint" OR NEW."Request" <> prior."Request" OR prior."Status"='Succeeded'
                      THEN RAISE EXCEPTION 'Operation identity/sequence immutable'; END IF;
                    IF NEW."Status"='Running' THEN
                      IF prior."Status" NOT IN ('Requested','Failed','Cancelled','Running') OR prior."Status"='Running' AND prior."LeaseUntilUtc" > NEW."RecordedAtUtc"
                        OR NEW."OwnerToken"=prior."OwnerToken" OR NEW."OwnerToken"='00000000-0000-0000-0000-000000000000'::uuid
                        THEN RAISE EXCEPTION 'Live owner cannot be replaced'; END IF;
                    ELSIF NEW."Status" IN ('Succeeded','Failed','Cancelled') THEN
                      IF prior."Status" <> 'Running' OR NEW."OwnerToken" <> prior."OwnerToken" OR prior."LeaseUntilUtc" <= NEW."RecordedAtUtc"
                        THEN RAISE EXCEPTION 'Lease lost; terminal publication fenced'; END IF;
                    ELSE RAISE EXCEPTION 'Invalid operation transition'; END IF;
                  END IF;
                  IF length(btrim(NEW."OperatorId"))=0 OR length(btrim(NEW."Reason"))=0 THEN RAISE EXCEPTION 'Actor and reason required'; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER trusted_result_operation BEFORE INSERT ON datasets."ResultOperations" FOR EACH ROW EXECUTE FUNCTION datasets.record_result_operation();
                CREATE TRIGGER immutable_result_operations BEFORE UPDATE OR DELETE OR TRUNCATE ON datasets."ResultOperations" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventEnds",
                schema: "football");

            migrationBuilder.DropTable(
                name: "ResultInventoryReviews",
                schema: "coverage");

            migrationBuilder.DropTable(
                name: "ResultOperations",
                schema: "datasets");

            migrationBuilder.DropTable(
                name: "ResultInventory",
                schema: "coverage");
            migrationBuilder.Sql("DROP FUNCTION coverage.record_result_inventory(); DROP FUNCTION coverage.record_result_review(); DROP FUNCTION football.record_event_end(); DROP FUNCTION datasets.record_result_operation();");
        }
    }
}
