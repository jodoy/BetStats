using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BetStats.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DatasetSnapshotsFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "datasets");

            migrationBuilder.CreateTable(
                name: "Snapshots",
                schema: "datasets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DefinitionFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    RowCount = table.Column<int>(type: "integer", nullable: false),
                    FeatureSchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    BuiltAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Snapshots", x => x.Id);
                    table.CheckConstraint("CK_Snapshots_Content", "\"RowCount\" BETWEEN 1 AND 100 AND \"FeatureSchemaVersion\" = 1 AND octet_length(\"Content\") BETWEEN 1 AND 16777216");
                    table.CheckConstraint("CK_Snapshots_Hash", "\"ManifestHash\" ~ '^[0-9a-f]{64}$' AND \"DefinitionFingerprint\" ~ '^[0-9a-f]{64}$'");
                });

            migrationBuilder.CreateTable(
                name: "BuildEvents",
                schema: "datasets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DefinitionFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OperatorId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuildEvents", x => x.Id);
                    table.CheckConstraint("CK_BuildEvents_Result", "(\"Status\" = 'Succeeded') = (\"SnapshotId\" IS NOT NULL) AND length(btrim(\"OperatorId\")) > 0 AND length(btrim(\"Reason\")) > 0");
                    table.CheckConstraint("CK_BuildEvents_State", "(\"Sequence\" = 1 AND \"Status\" = 'Requested') OR (\"Sequence\" = 2 AND \"Status\" = 'Running') OR (\"Sequence\" = 3 AND \"Status\" IN ('Succeeded','Failed','Cancelled'))");
                    table.ForeignKey(
                        name: "FK_BuildEvents_Snapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalSchema: "datasets",
                        principalTable: "Snapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Features",
                schema: "datasets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DatasetId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    PredictionCutoffUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Features", x => x.Id);
                    table.CheckConstraint("CK_Features_Hash", "\"Fingerprint\" ~ '^[0-9a-f]{64}$' AND octet_length(\"Content\") BETWEEN 1 AND 16777216");
                    table.ForeignKey(
                        name: "FK_Features_Snapshots_DatasetId",
                        column: x => x.DatasetId,
                        principalSchema: "datasets",
                        principalTable: "Snapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BuildEvents_AttemptId_Sequence",
                schema: "datasets",
                table: "BuildEvents",
                columns: new[] { "AttemptId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildEvents_DefinitionFingerprint",
                schema: "datasets",
                table: "BuildEvents",
                column: "DefinitionFingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_BuildEvents_SnapshotId",
                schema: "datasets",
                table: "BuildEvents",
                column: "SnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_Features_DatasetId_EventId_PredictionCutoffUtc",
                schema: "datasets",
                table: "Features",
                columns: new[] { "DatasetId", "EventId", "PredictionCutoffUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Snapshots_DefinitionFingerprint",
                schema: "datasets",
                table: "Snapshots",
                column: "DefinitionFingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_Snapshots_ManifestHash",
                schema: "datasets",
                table: "Snapshots",
                column: "ManifestHash",
                unique: true);
            migrationBuilder.Sql("""
                CREATE FUNCTION datasets.record_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  NEW."RecordedAtUtc" := clock_timestamp();
                  IF TG_TABLE_NAME = 'BuildEvents' THEN
                    PERFORM pg_advisory_xact_lock(hashtextextended(NEW."AttemptId"::text,7007));
                    IF NEW."Sequence" > 1 AND NOT EXISTS (
                      SELECT 1 FROM datasets."BuildEvents" WHERE "AttemptId" = NEW."AttemptId" AND "Sequence" = NEW."Sequence" - 1
                    ) AND NOT (NEW."Sequence" = 3 AND NEW."Status" = 'Failed' AND EXISTS (
                      SELECT 1 FROM datasets."BuildEvents" WHERE "AttemptId" = NEW."AttemptId" AND "Sequence" = 1
                    )) THEN RAISE EXCEPTION 'Dataset lifecycle predecessor missing' USING ERRCODE = '23514'; END IF;
                    IF EXISTS (SELECT 1 FROM datasets."BuildEvents" WHERE "AttemptId" = NEW."AttemptId" AND "Sequence" = 3)
                    THEN RAISE EXCEPTION 'Dataset attempt already closed' USING ERRCODE = '23514'; END IF;
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER record_snapshot BEFORE INSERT ON datasets."Snapshots" FOR EACH ROW EXECUTE FUNCTION datasets.record_insert();
                CREATE TRIGGER record_feature BEFORE INSERT ON datasets."Features" FOR EACH ROW EXECUTE FUNCTION datasets.record_insert();
                CREATE TRIGGER record_build BEFORE INSERT ON datasets."BuildEvents" FOR EACH ROW EXECUTE FUNCTION datasets.record_insert();
                CREATE TRIGGER immutable_snapshot BEFORE UPDATE OR DELETE OR TRUNCATE ON datasets."Snapshots" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE TRIGGER immutable_feature BEFORE UPDATE OR DELETE OR TRUNCATE ON datasets."Features" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE TRIGGER immutable_build BEFORE UPDATE OR DELETE OR TRUNCATE ON datasets."BuildEvents" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BuildEvents",
                schema: "datasets");

            migrationBuilder.DropTable(
                name: "Features",
                schema: "datasets");

            migrationBuilder.DropTable(
                name: "Snapshots",
                schema: "datasets");
            migrationBuilder.Sql("DROP FUNCTION datasets.record_insert();");
        }
    }
}
