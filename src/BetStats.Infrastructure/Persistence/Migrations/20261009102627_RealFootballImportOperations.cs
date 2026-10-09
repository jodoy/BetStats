using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BetStats.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RealFootballImportOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FootballImportOperations",
                schema: "ingestion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OwnerToken = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: true),
                    OperatorId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FootballImportOperations", x => x.Id);
                    table.CheckConstraint("CK_FootballImportOperations_State", "\"Sequence\" > 0 AND \"Fingerprint\" ~ '^[0-9a-f]{64}$' AND \"Status\" IN ('Running','Failed','Cancelled','Succeeded') AND (\"Status\" <> 'Running' OR \"LeaseUntilUtc\" > \"RecordedAtUtc\")");
                    table.ForeignKey(
                        name: "FK_FootballImportOperations_DataSources_SourceId",
                        column: x => x.SourceId,
                        principalSchema: "ingestion",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FootballImportOperations_OperationId_Sequence",
                schema: "ingestion",
                table: "FootballImportOperations",
                columns: new[] { "OperationId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FootballImportOperations_SourceId",
                schema: "ingestion",
                table: "FootballImportOperations",
                column: "SourceId");
            migrationBuilder.Sql("""
                CREATE FUNCTION ingestion.record_football_import_operation() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE prior ingestion."FootballImportOperations";
                BEGIN
                  PERFORM pg_advisory_xact_lock(hashtextextended(NEW."OperationId"::text,9014));
                  NEW."RecordedAtUtc" := clock_timestamp();
                  IF NEW."OwnerToken"='00000000-0000-0000-0000-000000000000'::uuid OR length(btrim(NEW."OperatorId"))=0 OR length(btrim(NEW."Reason"))=0
                    THEN RAISE EXCEPTION 'Explicit owner and operator required'; END IF;
                  IF NEW."Status"='Running' AND (NEW."LeaseUntilUtc" IS NULL OR NEW."LeaseUntilUtc" <= NEW."RecordedAtUtc" OR NEW."LeaseUntilUtc" > NEW."RecordedAtUtc" + interval '30 minutes')
                    THEN RAISE EXCEPTION 'Bounded DB-clock lease required'; END IF;
                  SELECT * INTO prior FROM ingestion."FootballImportOperations" WHERE "OperationId"=NEW."OperationId" ORDER BY "Sequence" DESC LIMIT 1;
                  IF NOT FOUND THEN
                    IF NEW."Sequence" <> 1 OR NEW."Status" <> 'Running' THEN RAISE EXCEPTION 'Initial running owner required'; END IF;
                  ELSE
                    IF NEW."Sequence" <> prior."Sequence"+1 OR NEW."Fingerprint" <> prior."Fingerprint" OR NEW."SourceId" <> prior."SourceId" OR prior."Status"='Succeeded'
                      THEN RAISE EXCEPTION 'Immutable operation identity and sequence'; END IF;
                    IF NEW."Status"='Running' THEN
                      IF prior."Status"='Running' AND prior."LeaseUntilUtc" > NEW."RecordedAtUtc" OR NEW."OwnerToken"=prior."OwnerToken"
                        THEN RAISE EXCEPTION 'Live owner cannot be replaced'; END IF;
                    ELSIF prior."Status" <> 'Running' OR NEW."OwnerToken" <> prior."OwnerToken" OR prior."LeaseUntilUtc" <= NEW."RecordedAtUtc"
                      THEN RAISE EXCEPTION 'Import terminal owner fenced'; END IF;
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER trusted_football_import_operation BEFORE INSERT ON ingestion."FootballImportOperations" FOR EACH ROW EXECUTE FUNCTION ingestion.record_football_import_operation();
                CREATE TRIGGER immutable_football_import_operations BEFORE UPDATE OR DELETE OR TRUNCATE ON ingestion."FootballImportOperations" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FootballImportOperations",
                schema: "ingestion");
            migrationBuilder.Sql("DROP FUNCTION ingestion.record_football_import_operation();");
        }
    }
}
