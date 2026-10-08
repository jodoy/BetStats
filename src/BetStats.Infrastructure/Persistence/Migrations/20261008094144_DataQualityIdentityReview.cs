using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BetStats.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DataQualityIdentityReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "quality");

            migrationBuilder.CreateTable(
                name: "MaintenanceEvents",
                schema: "quality",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OperatorId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousReferenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Result = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExecutedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceEvents", x => x.Id);
                    table.CheckConstraint("CK_MaintenanceEvents_Audit", "\"Sequence\" IN (1,2) AND length(btrim(\"OperatorId\")) > 0 AND length(btrim(\"Reason\")) > 0 AND length(btrim(\"Action\")) > 0 AND length(btrim(\"Result\")) > 0");
                    table.CheckConstraint("CK_MaintenanceEvents_Time", "\"ExecutedAtUtc\" <= \"RecordedAtUtc\"");
                    table.ForeignKey(
                        name: "FK_MaintenanceEvents_IdentityResolutions_DecisionId",
                        column: x => x.DecisionId,
                        principalSchema: "provenance",
                        principalTable: "IdentityResolutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "QualityAssessments",
                schema: "quality",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    DataSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawPayloadId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProviderIdentityId = table.Column<Guid>(type: "uuid", nullable: true),
                    ObservationId = table.Column<Guid>(type: "uuid", nullable: true),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Row = table.Column<int>(type: "integer", nullable: false),
                    RecordReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ContextKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SportId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RuleVersion = table.Column<int>(type: "integer", nullable: false),
                    Passed = table.Column<bool>(type: "boolean", nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BlocksEligibility = table.Column<bool>(type: "boolean", nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Classification = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AssessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityAssessments", x => x.Id);
                    table.CheckConstraint("CK_QualityAssessments_Rule", "\"RuleVersion\" > 0 AND \"Row\" >= 0 AND length(btrim(\"RuleId\")) > 0 AND length(btrim(\"ReasonCode\")) > 0");
                    table.CheckConstraint("CK_QualityAssessments_Severity", "\"Severity\" IN ('Info','Warning','Error','Critical')");
                    table.CheckConstraint("CK_QualityAssessments_Time", "\"AssessedAtUtc\" <= \"RecordedAtUtc\"");
                    table.ForeignKey(
                        name: "FK_QualityAssessments_IngestionRuns_RunId_DataSourceId",
                        columns: x => new { x.RunId, x.DataSourceId },
                        principalSchema: "ingestion",
                        principalTable: "IngestionRuns",
                        principalColumns: new[] { "Id", "DataSourceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityAssessments_Observations_ObservationId",
                        column: x => x.ObservationId,
                        principalSchema: "provenance",
                        principalTable: "Observations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityAssessments_ProviderIdentities_ProviderIdentityId",
                        column: x => x.ProviderIdentityId,
                        principalSchema: "provenance",
                        principalTable: "ProviderIdentities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityAssessments_RawPayloads_RawPayloadId_DataSourceId",
                        columns: x => new { x.RawPayloadId, x.DataSourceId },
                        principalSchema: "ingestion",
                        principalTable: "RawPayloads",
                        principalColumns: new[] { "Id", "DataSourceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityAssessments_SourcePolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalSchema: "governance",
                        principalTable: "SourcePolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceEvents_DecisionId",
                schema: "quality",
                table: "MaintenanceEvents",
                column: "DecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceEvents_ExecutionId_Sequence",
                schema: "quality",
                table: "MaintenanceEvents",
                columns: new[] { "ExecutionId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceEvents_TargetId_RecordedAtUtc_Id",
                schema: "quality",
                table: "MaintenanceEvents",
                columns: new[] { "TargetId", "RecordedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityAssessments_ExecutionId_RawPayloadId_Row_RuleId_Rule~",
                schema: "quality",
                table: "QualityAssessments",
                columns: new[] { "ExecutionId", "RawPayloadId", "Row", "RuleId", "RuleVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QualityAssessments_ObservationId",
                schema: "quality",
                table: "QualityAssessments",
                column: "ObservationId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityAssessments_PolicyId",
                schema: "quality",
                table: "QualityAssessments",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityAssessments_ProviderIdentityId_RecordedAtUtc_Id",
                schema: "quality",
                table: "QualityAssessments",
                columns: new[] { "ProviderIdentityId", "RecordedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityAssessments_RawPayloadId_DataSourceId",
                schema: "quality",
                table: "QualityAssessments",
                columns: new[] { "RawPayloadId", "DataSourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityAssessments_RunId_DataSourceId",
                schema: "quality",
                table: "QualityAssessments",
                columns: new[] { "RunId", "DataSourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityAssessments_RunId_Row",
                schema: "quality",
                table: "QualityAssessments",
                columns: new[] { "RunId", "Row" });
            migrationBuilder.Sql("""
                CREATE FUNCTION quality.record_quality_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  NEW."RecordedAtUtc" := clock_timestamp();
                  IF TG_TABLE_NAME = 'QualityAssessments' THEN
                    IF NOT EXISTS (SELECT 1 FROM canonical."Sports" s WHERE s."Id" = NEW."SportId") THEN
                      RAISE EXCEPTION 'Quality sport is missing' USING ERRCODE = '23514';
                    END IF;
                    IF NEW."PolicyId" IS NOT NULL AND NOT EXISTS
                       (SELECT 1 FROM governance."SourcePolicies" p WHERE p."Id" = NEW."PolicyId" AND p."DataSourceId" = NEW."DataSourceId") THEN
                      RAISE EXCEPTION 'Quality policy source mismatch' USING ERRCODE = '23514';
                    END IF;
                    IF NEW."ProviderIdentityId" IS NOT NULL AND NOT EXISTS
                       (SELECT 1 FROM provenance."ProviderIdentities" i WHERE i."Id" = NEW."ProviderIdentityId" AND i."DataSourceId" = NEW."DataSourceId") THEN
                      RAISE EXCEPTION 'Quality identity source mismatch' USING ERRCODE = '23514';
                    END IF;
                    IF NEW."ObservationId" IS NOT NULL AND NOT EXISTS
                       (SELECT 1 FROM provenance."Observations" o WHERE o."Id" = NEW."ObservationId" AND o."DataSourceId" = NEW."DataSourceId" AND o."RawPayloadId" = NEW."RawPayloadId") THEN
                      RAISE EXCEPTION 'Quality observation provenance mismatch' USING ERRCODE = '23514';
                    END IF;
                  END IF;
                  RETURN NEW;
                END; $$;
                CREATE TRIGGER quality_recorded BEFORE INSERT ON quality."QualityAssessments" FOR EACH ROW EXECUTE FUNCTION quality.record_quality_insert();
                CREATE TRIGGER maintenance_recorded BEFORE INSERT ON quality."MaintenanceEvents" FOR EACH ROW EXECUTE FUNCTION quality.record_quality_insert();
                CREATE TRIGGER quality_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON quality."QualityAssessments" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE TRIGGER maintenance_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON quality."MaintenanceEvents" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaintenanceEvents",
                schema: "quality");

            migrationBuilder.DropTable(
                name: "QualityAssessments",
                schema: "quality");
            migrationBuilder.Sql("DROP FUNCTION quality.record_quality_insert();");
        }
    }
}
