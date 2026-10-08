using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BetStats.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FirstFootballIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Observations_Value",
                schema: "provenance",
                table: "Observations");

            migrationBuilder.AddColumn<long>(
                name: "ByteLength",
                schema: "ingestion",
                table: "RawPayloads",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecordedAtUtc",
                schema: "ingestion",
                table: "RawPayloads",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "clock_timestamp()");

            migrationBuilder.AddColumn<DateOnly>(
                name: "DateValue",
                schema: "provenance",
                table: "Observations",
                type: "date",
                nullable: true);
            migrationBuilder.Sql("""
                CREATE FUNCTION ingestion.record_raw_availability() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN NEW."RecordedAtUtc" := clock_timestamp(); RETURN NEW; END; $$;
                CREATE TRIGGER trusted_raw_availability BEFORE INSERT ON ingestion."RawPayloads"
                    FOR EACH ROW EXECUTE FUNCTION ingestion.record_raw_availability();
                """);

            migrationBuilder.CreateTable(
                name: "IngestionAuditEvents",
                schema: "ingestion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    DataSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    RetrievedPayloads = table.Column<int>(type: "integer", nullable: false),
                    ParsedRecords = table.Column<int>(type: "integer", nullable: false),
                    AcceptedRecords = table.Column<int>(type: "integer", nullable: false),
                    RejectedRecords = table.Column<int>(type: "integer", nullable: false),
                    UnresolvedIdentities = table.Column<int>(type: "integer", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ErrorCategory = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovalAuditId = table.Column<Guid>(type: "uuid", nullable: true),
                    Issues = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestionAuditEvents", x => x.Id);
                    table.CheckConstraint("CK_IngestionAuditEvents_Counts", "\"Sequence\" IN (1,2) AND \"RetrievedPayloads\" >= 0 AND \"ParsedRecords\" >= 0 AND \"AcceptedRecords\" >= 0 AND \"RejectedRecords\" >= 0 AND \"UnresolvedIdentities\" >= 0");
                    table.CheckConstraint("CK_IngestionAuditEvents_Outcome", "(\"Sequence\" = 1 AND \"Outcome\" = 'Running') OR (\"Sequence\" = 2 AND \"Outcome\" IN ('Succeeded','Partial','Denied','Failed','Interrupted','Reused'))");
                    table.ForeignKey(
                        name: "FK_IngestionAuditEvents_IngestionRuns_RunId_DataSourceId",
                        columns: x => new { x.RunId, x.DataSourceId },
                        principalSchema: "ingestion",
                        principalTable: "IngestionRuns",
                        principalColumns: new[] { "Id", "DataSourceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IngestionAuditEvents_PolicyAudits_ApprovalAuditId",
                        column: x => x.ApprovalAuditId,
                        principalSchema: "governance",
                        principalTable: "PolicyAudits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IngestionAuditEvents_SourcePolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalSchema: "governance",
                        principalTable: "SourcePolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IngestionPublications",
                schema: "ingestion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DataSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsBatch = table.Column<bool>(type: "boolean", nullable: false),
                    RawPayloadId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    AcceptedRecords = table.Column<int>(type: "integer", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestionPublications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestionPublications_IngestionRuns_RunId_DataSourceId",
                        columns: x => new { x.RunId, x.DataSourceId },
                        principalSchema: "ingestion",
                        principalTable: "IngestionRuns",
                        principalColumns: new[] { "Id", "DataSourceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IngestionPublications_RawPayloads_RawPayloadId_DataSourceId",
                        columns: x => new { x.RawPayloadId, x.DataSourceId },
                        principalSchema: "ingestion",
                        principalTable: "RawPayloads",
                        principalColumns: new[] { "Id", "DataSourceId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_RawPayloads_Length",
                schema: "ingestion",
                table: "RawPayloads",
                sql: "\"ByteLength\" IS NULL OR \"ByteLength\" BETWEEN 1 AND 1048576");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Observations_Value",
                schema: "provenance",
                table: "Observations",
                sql: "(\"DateValue\" IS NULL AND ((\"Type\" = 'DisplayName' AND \"EntityKind\" <> 'SportingEvent' AND \"TextValue\" IS NOT NULL AND length(btrim(\"TextValue\")) > 0 AND \"TimestampValueUtc\" IS NULL AND \"StatusValue\" IS NULL) OR (\"Type\" = 'ScheduledStart' AND \"EntityKind\" = 'SportingEvent' AND \"TextValue\" IS NULL AND \"TimestampValueUtc\" IS NOT NULL AND \"StatusValue\" IS NULL) OR (\"Type\" = 'EventStatus' AND \"EntityKind\" = 'SportingEvent' AND \"TextValue\" IS NULL AND \"TimestampValueUtc\" IS NULL AND \"StatusValue\" IS NOT NULL AND \"StatusValue\" IN ('Scheduled','InProgress','Completed','Postponed','Cancelled')))) OR (\"Type\" = 'EventDate' AND \"EntityKind\" = 'SportingEvent' AND \"DateValue\" IS NOT NULL AND \"TextValue\" IS NULL AND \"TimestampValueUtc\" IS NULL AND \"StatusValue\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionAuditEvents_ApprovalAuditId",
                schema: "ingestion",
                table: "IngestionAuditEvents",
                column: "ApprovalAuditId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionAuditEvents_AttemptId_Sequence",
                schema: "ingestion",
                table: "IngestionAuditEvents",
                columns: new[] { "AttemptId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IngestionAuditEvents_PolicyId",
                schema: "ingestion",
                table: "IngestionAuditEvents",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionAuditEvents_RunId_DataSourceId",
                schema: "ingestion",
                table: "IngestionAuditEvents",
                columns: new[] { "RunId", "DataSourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_IngestionPublications_DataSourceId_Key",
                schema: "ingestion",
                table: "IngestionPublications",
                columns: new[] { "DataSourceId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IngestionPublications_RawPayloadId_DataSourceId",
                schema: "ingestion",
                table: "IngestionPublications",
                columns: new[] { "RawPayloadId", "DataSourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_IngestionPublications_RunId_DataSourceId",
                schema: "ingestion",
                table: "IngestionPublications",
                columns: new[] { "RunId", "DataSourceId" });
            migrationBuilder.Sql("""
                CREATE FUNCTION ingestion.record_execution_availability() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN NEW."RecordedAtUtc" := clock_timestamp(); RETURN NEW; END; $$;
                CREATE TRIGGER trusted_ingestion_audit BEFORE INSERT ON ingestion."IngestionAuditEvents"
                    FOR EACH ROW EXECUTE FUNCTION ingestion.record_execution_availability();
                CREATE TRIGGER trusted_ingestion_publication BEFORE INSERT ON ingestion."IngestionPublications"
                    FOR EACH ROW EXECUTE FUNCTION ingestion.record_execution_availability();
                CREATE TRIGGER prevent_ingestion_audit_mutation BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON ingestion."IngestionAuditEvents" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE TRIGGER prevent_ingestion_publication_mutation BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON ingestion."IngestionPublications" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER prevent_ingestion_audit_mutation ON ingestion."IngestionAuditEvents";
                DROP TRIGGER prevent_ingestion_publication_mutation ON ingestion."IngestionPublications";
                DROP TRIGGER trusted_ingestion_audit ON ingestion."IngestionAuditEvents";
                DROP TRIGGER trusted_ingestion_publication ON ingestion."IngestionPublications";
                DROP FUNCTION ingestion.record_execution_availability();
                DROP TRIGGER trusted_raw_availability ON ingestion."RawPayloads";
                DROP FUNCTION ingestion.record_raw_availability();
                """);
            migrationBuilder.DropTable(
                name: "IngestionAuditEvents",
                schema: "ingestion");

            migrationBuilder.DropTable(
                name: "IngestionPublications",
                schema: "ingestion");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RawPayloads_Length",
                schema: "ingestion",
                table: "RawPayloads");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Observations_Value",
                schema: "provenance",
                table: "Observations");

            migrationBuilder.DropColumn(
                name: "ByteLength",
                schema: "ingestion",
                table: "RawPayloads");

            migrationBuilder.DropColumn(
                name: "RecordedAtUtc",
                schema: "ingestion",
                table: "RawPayloads");

            migrationBuilder.DropColumn(
                name: "DateValue",
                schema: "provenance",
                table: "Observations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Observations_Value",
                schema: "provenance",
                table: "Observations",
                sql: "(\"Type\" = 'DisplayName' AND \"EntityKind\" <> 'SportingEvent' AND \"TextValue\" IS NOT NULL AND length(btrim(\"TextValue\")) > 0 AND \"TimestampValueUtc\" IS NULL AND \"StatusValue\" IS NULL) OR (\"Type\" = 'ScheduledStart' AND \"EntityKind\" = 'SportingEvent' AND \"TextValue\" IS NULL AND \"TimestampValueUtc\" IS NOT NULL AND \"StatusValue\" IS NULL) OR (\"Type\" = 'EventStatus' AND \"EntityKind\" = 'SportingEvent' AND \"TextValue\" IS NULL AND \"TimestampValueUtc\" IS NULL AND \"StatusValue\" IS NOT NULL AND \"StatusValue\" IN ('Scheduled','InProgress','Completed','Postponed','Cancelled'))");
        }
    }
}
