using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BetStats.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableGovernedPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "pipeline");

            migrationBuilder.CreateTable(
                name: "Jobs",
                schema: "pipeline",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    NextDueUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobVersions",
                schema: "pipeline",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobVersions", x => new { x.JobId, x.Version });
                    table.ForeignKey(
                        name: "FK_JobVersions_Jobs_JobId",
                        column: x => x.JobId,
                        principalSchema: "pipeline",
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Executions",
                schema: "pipeline",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    DefinitionVersion = table.Column<int>(type: "integer", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Owner = table.Column<Guid>(type: "uuid", nullable: false),
                    PlannedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LeaseUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    CancelRequested = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Executions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Executions_JobVersions_JobId_DefinitionVersion",
                        columns: x => new { x.JobId, x.DefinitionVersion },
                        principalSchema: "pipeline",
                        principalTable: "JobVersions",
                        principalColumns: new[] { "JobId", "Version" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Artifacts",
                schema: "pipeline",
                columns: table => new
                {
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Artifacts", x => x.ExecutionId);
                    table.ForeignKey(
                        name: "FK_Artifacts_Executions_ExecutionId",
                        column: x => x.ExecutionId,
                        principalSchema: "pipeline",
                        principalTable: "Executions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Receipts",
                schema: "pipeline",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Owner = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArtifactHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Receipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Receipts_Executions_ExecutionId",
                        column: x => x.ExecutionId,
                        principalSchema: "pipeline",
                        principalTable: "Executions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Receipts_Jobs_JobId",
                        column: x => x.JobId,
                        principalSchema: "pipeline",
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Executions_JobId_DefinitionVersion_PlannedUtc",
                schema: "pipeline",
                table: "Executions",
                columns: new[] { "JobId", "DefinitionVersion", "PlannedUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_ExecutionId",
                schema: "pipeline",
                table: "Receipts",
                column: "ExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_JobId_RecordedUtc",
                schema: "pipeline",
                table: "Receipts",
                columns: new[] { "JobId", "RecordedUtc" });
            migrationBuilder.Sql("""
                ALTER TABLE pipeline."Jobs" ADD CONSTRAINT "CK_PipelineJobs_State" CHECK ("State" BETWEEN 0 AND 6 AND "Version">0);
                ALTER TABLE pipeline."JobVersions" ADD CONSTRAINT "CK_PipelineVersions_Content" CHECK
                  ("Version">0 AND octet_length("Content") BETWEEN 1 AND 2097152 AND "Hash"=encode(sha256("Content"),'hex') AND length(btrim("Actor"))>0 AND length(btrim("Reason"))>0);
                ALTER TABLE pipeline."Executions" ADD CONSTRAINT "CK_PipelineExecutions_Bounds" CHECK
                  ("State" BETWEEN 0 AND 6 AND "Attempt" BETWEEN 1 AND 5 AND "Owner"<>'00000000-0000-0000-0000-000000000000'::uuid AND
                   "LeaseUntilUtc">"StartedUtc" AND "CompletedUtc">="StartedUtc" AND "Fingerprint" ~ '^[0-9a-f]{64}$');
                ALTER TABLE pipeline."Receipts" ADD CONSTRAINT "CK_PipelineReceipts_Bounds" CHECK
                  ("State" BETWEEN 0 AND 6 AND "Category" ~ '^[a-z0-9_]{1,100}$' AND length(btrim("Actor"))>0 AND length(btrim("Reason"))>0);
                ALTER TABLE pipeline."Artifacts" ADD CONSTRAINT "CK_PipelineArtifacts_Content" CHECK
                  (octet_length("Content") BETWEEN 1 AND 16777216 AND "Hash"=encode(sha256("Content"),'hex'));
                CREATE FUNCTION pipeline.clock_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN NEW."RecordedUtc"=clock_timestamp(); RETURN NEW; END $$;
                CREATE TRIGGER pipeline_version_clock BEFORE INSERT ON pipeline."JobVersions" FOR EACH ROW EXECUTE FUNCTION pipeline.clock_receipt();
                CREATE TRIGGER pipeline_receipt_clock BEFORE INSERT ON pipeline."Receipts" FOR EACH ROW EXECUTE FUNCTION pipeline.clock_receipt();
                CREATE TRIGGER pipeline_artifact_clock BEFORE INSERT ON pipeline."Artifacts" FOR EACH ROW EXECUTE FUNCTION pipeline.clock_receipt();
                CREATE TRIGGER pipeline_versions_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON pipeline."JobVersions" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE TRIGGER pipeline_receipts_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON pipeline."Receipts" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE TRIGGER pipeline_artifacts_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON pipeline."Artifacts" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE FUNCTION pipeline.execution_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF TG_OP='UPDATE' AND (NEW."JobId"<>OLD."JobId" OR NEW."DefinitionVersion"<>OLD."DefinitionVersion" OR NEW."Fingerprint"<>OLD."Fingerprint" OR NEW."PlannedUtc"<>OLD."PlannedUtc") THEN
                    RAISE EXCEPTION 'Immutable pipeline request';
                  END IF;
                  IF TG_OP='UPDATE' AND NEW."Owner"<>OLD."Owner" AND (NEW."Attempt"<>OLD."Attempt"+1 OR NEW."State"<>2 OR OLD."State"=3 OR (OLD."State"=2 AND OLD."LeaseUntilUtc">clock_timestamp())) THEN
                    RAISE EXCEPTION 'Invalid pipeline owner transition';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER pipeline_execution_guard BEFORE UPDATE ON pipeline."Executions" FOR EACH ROW EXECUTE FUNCTION pipeline.execution_guard();
                CREATE TABLE pipeline."Diagnostics" (
                  "Id" uuid PRIMARY KEY, "ExecutionId" uuid NOT NULL, "JobId" uuid NOT NULL, "State" integer NOT NULL,
                  "Category" varchar(100) NOT NULL, "DurationMilliseconds" double precision NOT NULL, "RecordedUtc" timestamptz NOT NULL DEFAULT clock_timestamp());
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE pipeline.\"Diagnostics\";");
            migrationBuilder.DropTable(
                name: "Artifacts",
                schema: "pipeline");

            migrationBuilder.DropTable(
                name: "Receipts",
                schema: "pipeline");

            migrationBuilder.DropTable(
                name: "Executions",
                schema: "pipeline");

            migrationBuilder.DropTable(
                name: "JobVersions",
                schema: "pipeline");

            migrationBuilder.DropTable(
                name: "Jobs",
                schema: "pipeline");
            migrationBuilder.Sql("DROP FUNCTION pipeline.clock_receipt(); DROP FUNCTION pipeline.execution_guard();");
        }
    }
}
