using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BetStats.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HistoricalBacktesting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "evaluation");

            migrationBuilder.CreateTable(
                name: "Backtests",
                schema: "evaluation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DatasetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Backtests", x => x.Id);
                    table.CheckConstraint("CK_Backtests_Content", "\"Hash\" ~ '^[0-9a-f]{64}$' AND octet_length(\"Content\") BETWEEN 1 AND 16777216");
                    table.ForeignKey(
                        name: "FK_Backtests_ResultArtifacts_DatasetId",
                        column: x => x.DatasetId,
                        principalSchema: "datasets",
                        principalTable: "ResultArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BacktestOperations",
                schema: "evaluation",
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
                    table.PrimaryKey("PK_BacktestOperations", x => x.Id);
                    table.CheckConstraint("CK_BacktestOperations_State", "\"Sequence\" > 0 AND \"Status\" IN ('Requested','Running','Failed','Cancelled','Succeeded') AND \"Fingerprint\" ~ '^[0-9a-f]{64}$' AND octet_length(\"Request\") BETWEEN 1 AND 1048576 AND ((\"Status\" = 'Succeeded') = (\"SnapshotId\" IS NOT NULL)) AND (\"Status\" <> 'Running' OR \"LeaseUntilUtc\" > \"RecordedAtUtc\")");
                    table.ForeignKey(
                        name: "FK_BacktestOperations_Backtests_SnapshotId",
                        column: x => x.SnapshotId,
                        principalSchema: "evaluation",
                        principalTable: "Backtests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BacktestOperations_OperationId_Sequence",
                schema: "evaluation",
                table: "BacktestOperations",
                columns: new[] { "OperationId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BacktestOperations_SnapshotId",
                schema: "evaluation",
                table: "BacktestOperations",
                column: "SnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_Backtests_DatasetId",
                schema: "evaluation",
                table: "Backtests",
                column: "DatasetId");

            migrationBuilder.CreateIndex(
                name: "IX_Backtests_Hash",
                schema: "evaluation",
                table: "Backtests",
                column: "Hash",
                unique: true);
            migrationBuilder.Sql("""
                CREATE FUNCTION evaluation.record_backtest_operation() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE prior evaluation."BacktestOperations";
                BEGIN
                  PERFORM pg_advisory_xact_lock(hashtextextended(NEW."OperationId"::text,9011));
                  NEW."RecordedAtUtc" := clock_timestamp();
                  IF encode(sha256(NEW."Request"),'hex') <> NEW."Fingerprint" THEN RAISE EXCEPTION 'Invalid request fingerprint'; END IF;
                  IF NEW."Status"='Succeeded' AND NOT EXISTS (
                    SELECT 1 FROM evaluation."Backtests" WHERE "Id"=NEW."SnapshotId"
                    AND convert_from("Content",'UTF8')::jsonb->>'Fingerprint'=NEW."Fingerprint"
                    AND convert_from("Content",'UTF8')::jsonb->'Definition'=convert_from(NEW."Request",'UTF8')::jsonb)
                    THEN RAISE EXCEPTION 'Success must reference the exact immutable request artifact'; END IF;
                  IF NEW."Status"='Running' AND (NEW."LeaseUntilUtc" IS NULL OR NEW."LeaseUntilUtc" <= NEW."RecordedAtUtc" OR NEW."LeaseUntilUtc" > NEW."RecordedAtUtc" + interval '30 minutes')
                    THEN RAISE EXCEPTION 'Bounded explicit DB-clock lease required'; END IF;
                  SELECT * INTO prior FROM evaluation."BacktestOperations" WHERE "OperationId"=NEW."OperationId" ORDER BY "Sequence" DESC LIMIT 1;
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
                CREATE TRIGGER trusted_backtest_operation BEFORE INSERT ON evaluation."BacktestOperations" FOR EACH ROW EXECUTE FUNCTION evaluation.record_backtest_operation();
                CREATE TRIGGER immutable_backtest_operations BEFORE UPDATE OR DELETE OR TRUNCATE ON evaluation."BacktestOperations" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();

                CREATE FUNCTION evaluation.record_backtest() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  NEW."RecordedAtUtc" := clock_timestamp();
                  IF encode(sha256(NEW."Content"),'hex') <> NEW."Hash" OR NOT EXISTS (
                    SELECT 1 FROM datasets."ResultArtifacts" WHERE "Id"=NEW."DatasetId"
                    AND "Hash"=convert_from(NEW."Content",'UTF8')::jsonb->'Definition'->>'ExpectedDatasetHash')
                    OR (convert_from(NEW."Content",'UTF8')::jsonb->'Definition'->>'DatasetId')::uuid <> NEW."DatasetId"
                    THEN RAISE EXCEPTION 'Invalid backtest dataset or content binding'; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER trusted_backtest BEFORE INSERT ON evaluation."Backtests" FOR EACH ROW EXECUTE FUNCTION evaluation.record_backtest();
                CREATE TRIGGER immutable_backtests BEFORE UPDATE OR DELETE OR TRUNCATE ON evaluation."Backtests" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BacktestOperations",
                schema: "evaluation");

            migrationBuilder.DropTable(
                name: "Backtests",
                schema: "evaluation");
            migrationBuilder.Sql("DROP FUNCTION evaluation.record_backtest(); DROP FUNCTION evaluation.record_backtest_operation();");
        }
    }
}
