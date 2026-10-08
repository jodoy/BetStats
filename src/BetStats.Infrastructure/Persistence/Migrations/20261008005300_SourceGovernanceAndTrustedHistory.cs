using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BetStats.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SourceGovernanceAndTrustedHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "governance");

            migrationBuilder.AddColumn<DateTime>(
                name: "RecordedAtUtc",
                schema: "provenance",
                table: "IdentityResolutions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "clock_timestamp()");

            migrationBuilder.CreateTable(
                name: "SourcePolicies",
                schema: "governance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DataSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    EffectiveFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectiveToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TermsReference = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    EvidenceReference = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcePolicies", x => x.Id);
                    table.CheckConstraint("CK_SourcePolicies_Evidence", "length(btrim(\"TermsReference\")) > 0 AND length(btrim(\"EvidenceReference\")) > 0");
                    table.CheckConstraint("CK_SourcePolicies_Interval", "\"EffectiveToUtc\" IS NULL OR \"EffectiveToUtc\" > \"EffectiveFromUtc\"");
                    table.CheckConstraint("CK_SourcePolicies_Version", "\"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_SourcePolicies_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "ingestion",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PolicyAudits",
                schema: "governance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourcePolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Reviewer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    PreviousAuditId = table.Column<Guid>(type: "uuid", nullable: true),
                    PreviousSequence = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyAudits", x => x.Id);
                    table.UniqueConstraint("AK_PolicyAudits_Id_SourcePolicyId_Sequence", x => new { x.Id, x.SourcePolicyId, x.Sequence });
                    table.CheckConstraint("CK_PolicyAudits_Audit", "length(btrim(\"Reviewer\")) > 0 AND length(btrim(\"Reason\")) > 0");
                    table.CheckConstraint("CK_PolicyAudits_Previous", "(\"Sequence\" = 1 AND \"PreviousAuditId\" IS NULL AND \"PreviousSequence\" IS NULL) OR (\"Sequence\" > 1 AND \"PreviousAuditId\" IS NOT NULL AND \"PreviousSequence\" IS NOT NULL AND \"PreviousSequence\" = \"Sequence\" - 1)");
                    table.CheckConstraint("CK_PolicyAudits_Status", "(\"Status\" = 'Approved' AND \"ApprovedAtUtc\" IS NOT NULL AND \"ApprovedAtUtc\" >= \"ReviewedAtUtc\") OR (\"Status\" = 'Revoked' AND \"ApprovedAtUtc\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_PolicyAudits_PolicyAudits_PreviousAuditId_SourcePolicyId_Pr~",
                        columns: x => new { x.PreviousAuditId, x.SourcePolicyId, x.PreviousSequence },
                        principalSchema: "governance",
                        principalTable: "PolicyAudits",
                        principalColumns: new[] { "Id", "SourcePolicyId", "Sequence" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PolicyAudits_SourcePolicies_SourcePolicyId",
                        column: x => x.SourcePolicyId,
                        principalSchema: "governance",
                        principalTable: "SourcePolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurposePermissions",
                schema: "governance",
                columns: table => new
                {
                    SourcePolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Decision = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Attribution = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MaximumRetentionDays = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurposePermissions", x => new { x.SourcePolicyId, x.Purpose });
                    table.CheckConstraint("CK_PurposePermissions_Decision", "\"Decision\" IN ('Allowed','Denied','Unknown')");
                    table.CheckConstraint("CK_PurposePermissions_Purpose", "\"Purpose\" IN ('MetadataDiscovery','DataRetrieval','RawPayloadStorage','HistoricalRetention','InternalAnalytics','PublicDisplay','ModelTraining','CommercialUse','Redistribution')");
                    table.CheckConstraint("CK_PurposePermissions_Restrictions", "(\"MaximumRetentionDays\" IS NULL OR \"MaximumRetentionDays\" > 0) AND (\"Attribution\" IS NULL OR length(btrim(\"Attribution\")) > 0)");
                    table.ForeignKey(
                        name: "FK_PurposePermissions_SourcePolicies_SourcePolicyId",
                        column: x => x.SourcePolicyId,
                        principalSchema: "governance",
                        principalTable: "SourcePolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityResolutions_ProviderIdentityId_RecordedAtUtc_Version",
                schema: "provenance",
                table: "IdentityResolutions",
                columns: new[] { "ProviderIdentityId", "RecordedAtUtc", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyAudits_PreviousAuditId_SourcePolicyId_PreviousSequence",
                schema: "governance",
                table: "PolicyAudits",
                columns: new[] { "PreviousAuditId", "SourcePolicyId", "PreviousSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyAudits_SourcePolicyId_RecordedAtUtc",
                schema: "governance",
                table: "PolicyAudits",
                columns: new[] { "SourcePolicyId", "RecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyAudits_SourcePolicyId_Sequence",
                schema: "governance",
                table: "PolicyAudits",
                columns: new[] { "SourcePolicyId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourcePolicies_DataSourceId_EffectiveFromUtc_EffectiveToUtc",
                schema: "governance",
                table: "SourcePolicies",
                columns: new[] { "DataSourceId", "EffectiveFromUtc", "EffectiveToUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SourcePolicies_DataSourceId_Version",
                schema: "governance",
                table: "SourcePolicies",
                columns: new[] { "DataSourceId", "Version" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE FUNCTION provenance.record_identity_availability() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    NEW."RecordedAtUtc" := clock_timestamp();
                    RETURN NEW;
                END; $$;
                CREATE TRIGGER trusted_identity_availability BEFORE INSERT ON provenance."IdentityResolutions"
                    FOR EACH ROW EXECUTE FUNCTION provenance.record_identity_availability();

                CREATE FUNCTION governance.record_policy_availability() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    NEW."RecordedAtUtc" := clock_timestamp();
                    RETURN NEW;
                END; $$;
                CREATE TRIGGER trusted_policy_availability BEFORE INSERT ON governance."SourcePolicies"
                    FOR EACH ROW EXECUTE FUNCTION governance.record_policy_availability();

                CREATE FUNCTION governance.protect_permission_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM 1 FROM governance."SourcePolicies" WHERE "Id" = NEW."SourcePolicyId" FOR UPDATE;
                    IF EXISTS (SELECT 1 FROM governance."PolicyAudits" WHERE "SourcePolicyId" = NEW."SourcePolicyId") THEN
                        RAISE EXCEPTION 'Audited permissions are immutable' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END; $$;
                CREATE TRIGGER protect_permission_insert BEFORE INSERT ON governance."PurposePermissions"
                    FOR EACH ROW EXECUTE FUNCTION governance.protect_permission_insert();

                CREATE FUNCTION governance.audit_policy() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE policy governance."SourcePolicies"%ROWTYPE;
                        previous governance."PolicyAudits"%ROWTYPE;
                BEGIN
                    SELECT * INTO policy FROM governance."SourcePolicies" WHERE "Id" = NEW."SourcePolicyId";
                    IF current_setting('transaction_isolation') NOT IN ('read committed', 'serializable') THEN
                        RAISE EXCEPTION 'Policy audit requires READ COMMITTED or SERIALIZABLE' USING ERRCODE = '23514';
                    END IF;
                    PERFORM 1 FROM ingestion."DataSources" WHERE "Id" = policy."DataSourceId" FOR UPDATE;
                    PERFORM 1 FROM governance."SourcePolicies" WHERE "Id" = NEW."SourcePolicyId" FOR UPDATE;
                    SELECT * INTO previous FROM governance."PolicyAudits" WHERE "SourcePolicyId" = NEW."SourcePolicyId" ORDER BY "Sequence" DESC LIMIT 1;
                    IF NEW."Status" = 'Approved' THEN
                        IF previous."Id" IS NOT NULL OR (SELECT count(*) FROM governance."PurposePermissions" WHERE "SourcePolicyId" = NEW."SourcePolicyId") <> 9 THEN
                            RAISE EXCEPTION 'Approval requires a complete draft' USING ERRCODE = '23514';
                        END IF;
                        IF EXISTS (
                            SELECT 1 FROM governance."SourcePolicies" other
                            JOIN LATERAL (SELECT "Status" FROM governance."PolicyAudits" WHERE "SourcePolicyId" = other."Id" ORDER BY "Sequence" DESC LIMIT 1) current ON true
                            WHERE other."DataSourceId" = policy."DataSourceId" AND other."Id" <> policy."Id" AND current."Status" = 'Approved'
                              AND tstzrange(other."EffectiveFromUtc", other."EffectiveToUtc", '[)') && tstzrange(policy."EffectiveFromUtc", policy."EffectiveToUtc", '[)')
                        ) THEN
                            RAISE EXCEPTION 'Conflicting approved intervals' USING ERRCODE = '23514';
                        END IF;
                    ELSIF NEW."Status" = 'Revoked' THEN
                        IF previous."Status" IS DISTINCT FROM 'Approved' OR NEW."PreviousAuditId" IS DISTINCT FROM previous."Id" THEN
                            RAISE EXCEPTION 'Revocation requires the current approval' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    NEW."RecordedAtUtc" := greatest(clock_timestamp(), policy."RecordedAtUtc", previous."RecordedAtUtc");
                    RETURN NEW;
                END; $$;
                CREATE TRIGGER audit_policy BEFORE INSERT ON governance."PolicyAudits"
                    FOR EACH ROW EXECUTE FUNCTION governance.audit_policy();
                CREATE TRIGGER prevent_history_mutation BEFORE UPDATE OR DELETE OR TRUNCATE ON governance."SourcePolicies"
                    FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE TRIGGER prevent_history_mutation BEFORE UPDATE OR DELETE OR TRUNCATE ON governance."PurposePermissions"
                    FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE TRIGGER prevent_history_mutation BEFORE UPDATE OR DELETE OR TRUNCATE ON governance."PolicyAudits"
                    FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER trusted_identity_availability ON provenance."IdentityResolutions";
                DROP FUNCTION provenance.record_identity_availability();
                """);
            migrationBuilder.DropTable(
                name: "PolicyAudits",
                schema: "governance");

            migrationBuilder.DropTable(
                name: "PurposePermissions",
                schema: "governance");

            migrationBuilder.DropTable(
                name: "SourcePolicies",
                schema: "governance");
            migrationBuilder.Sql("""
                DROP FUNCTION governance.audit_policy();
                DROP FUNCTION governance.protect_permission_insert();
                DROP FUNCTION governance.record_policy_availability();
                """);

            migrationBuilder.DropIndex(
                name: "IX_IdentityResolutions_ProviderIdentityId_RecordedAtUtc_Version",
                schema: "provenance",
                table: "IdentityResolutions");

            migrationBuilder.DropColumn(
                name: "RecordedAtUtc",
                schema: "provenance",
                table: "IdentityResolutions");
        }
    }
}
