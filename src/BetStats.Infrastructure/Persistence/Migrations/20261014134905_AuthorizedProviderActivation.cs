using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BetStats.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthorizedProviderActivation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProviderGrants",
                schema: "ingestion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderGrants", x => x.Id);
                    table.UniqueConstraint("AK_ProviderGrants_Id_SourceId", x => new { x.Id, x.SourceId });
                    table.CheckConstraint("CK_ProviderGrants_Content", "octet_length(\"Content\") BETWEEN 1 AND 16384 AND \"Hash\" ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_ProviderGrants_DataSources_SourceId",
                        column: x => x.SourceId,
                        principalSchema: "ingestion",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProviderGrants_SourcePolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalSchema: "governance",
                        principalTable: "SourcePolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProviderActivationEvents",
                schema: "ingestion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    GrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderActivationEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderActivationEvents_ProviderGrants_GrantId_SourceId",
                        columns: x => new { x.GrantId, x.SourceId },
                        principalSchema: "ingestion",
                        principalTable: "ProviderGrants",
                        principalColumns: new[] { "Id", "SourceId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProviderRequestEvents",
                schema: "ingestion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Owner = table.Column<Guid>(type: "uuid", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stage = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RawId = table.Column<Guid>(type: "uuid", nullable: true),
                    PublicationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    RetryUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RecordedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderRequestEvents", x => x.Id);
                    table.CheckConstraint("CK_ProviderRequestEvents_Attempt", "\"Attempt\" BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_ProviderRequestEvents_Stage", "\"Stage\" IN ('Reserved','Failed','Captured','Prepared','Reconciled')");
                    table.ForeignKey(
                        name: "FK_ProviderRequestEvents_Executions_ExecutionId",
                        column: x => x.ExecutionId,
                        principalSchema: "pipeline",
                        principalTable: "Executions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProviderRequestEvents_IngestionRuns_RunId",
                        column: x => x.RunId,
                        principalSchema: "ingestion",
                        principalTable: "IngestionRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProviderRequestEvents_ProviderGrants_GrantId_SourceId",
                        columns: x => new { x.GrantId, x.SourceId },
                        principalSchema: "ingestion",
                        principalTable: "ProviderGrants",
                        principalColumns: new[] { "Id", "SourceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProviderRequestEvents_RawPayloads_RawId_SourceId",
                        columns: x => new { x.RawId, x.SourceId },
                        principalSchema: "ingestion",
                        principalTable: "RawPayloads",
                        principalColumns: new[] { "Id", "DataSourceId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderActivationEvents_GrantId_SourceId",
                schema: "ingestion",
                table: "ProviderActivationEvents",
                columns: new[] { "GrantId", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderActivationEvents_SourceId_Sequence",
                schema: "ingestion",
                table: "ProviderActivationEvents",
                columns: new[] { "SourceId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProviderGrants_PolicyId",
                schema: "ingestion",
                table: "ProviderGrants",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderGrants_SourceId",
                schema: "ingestion",
                table: "ProviderGrants",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderRequestEvents_ExecutionId_Stage",
                schema: "ingestion",
                table: "ProviderRequestEvents",
                columns: new[] { "ExecutionId", "Stage" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProviderRequestEvents_GrantId_SourceId",
                schema: "ingestion",
                table: "ProviderRequestEvents",
                columns: new[] { "GrantId", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderRequestEvents_RawId_SourceId",
                schema: "ingestion",
                table: "ProviderRequestEvents",
                columns: new[] { "RawId", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderRequestEvents_RunId",
                schema: "ingestion",
                table: "ProviderRequestEvents",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderRequestEvents_SourceId_Sequence",
                schema: "ingestion",
                table: "ProviderRequestEvents",
                columns: new[] { "SourceId", "Sequence" });
            migrationBuilder.Sql("""
                CREATE FUNCTION ingestion.provider_activation_clock() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  PERFORM 1 FROM ingestion."DataSources" WHERE "Id"=NEW."SourceId" FOR UPDATE;
                  NEW."RecordedUtc" := clock_timestamp();
                  RETURN NEW;
                END $$;
                CREATE TRIGGER provider_grant_clock BEFORE INSERT ON ingestion."ProviderGrants"
                  FOR EACH ROW EXECUTE FUNCTION ingestion.provider_activation_clock();
                CREATE TRIGGER provider_activation_clock BEFORE INSERT ON ingestion."ProviderActivationEvents"
                  FOR EACH ROW EXECUTE FUNCTION ingestion.provider_activation_clock();
                CREATE TRIGGER provider_request_clock BEFORE INSERT ON ingestion."ProviderRequestEvents"
                  FOR EACH ROW EXECUTE FUNCTION ingestion.provider_activation_clock();
                CREATE TRIGGER provider_grants_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON ingestion."ProviderGrants"
                  FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE TRIGGER provider_activation_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON ingestion."ProviderActivationEvents"
                  FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE TRIGGER provider_requests_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON ingestion."ProviderRequestEvents"
                  FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE FUNCTION ingestion.provider_request_fence() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE grant_json jsonb; policy_id uuid;
                BEGIN
                  IF NEW."Stage"='Reconciled' THEN RETURN NEW; END IF;
                  IF NOT EXISTS (SELECT 1 FROM pipeline."Executions" e WHERE e."Id"=NEW."ExecutionId"
                    AND e."Owner"=NEW."Owner" AND e."Attempt"=NEW."Attempt" AND e."State"=2
                    AND NOT e."CancelRequested" AND e."LeaseUntilUtc">clock_timestamp()) THEN
                    RAISE EXCEPTION 'Provider request owner fenced at commit' USING ERRCODE='23514';
                  END IF;
                  IF NEW."Stage"<>'Reserved' THEN RETURN NEW; END IF;
                  SELECT convert_from(g."Content",'UTF8')::jsonb, g."PolicyId" INTO grant_json, policy_id
                    FROM ingestion."ProviderGrants" g WHERE g."Id"=NEW."GrantId" AND g."SourceId"=NEW."SourceId";
                  IF (grant_json->>'ExpiresUtc')::timestamptz<=clock_timestamp()
                    OR (grant_json->>'FixturePayloadApproved')::boolean IS DISTINCT FROM true
                    OR NOT EXISTS (SELECT 1 FROM ingestion."ProviderActivationEvents" a
                      WHERE a."Id"=(SELECT b."Id" FROM ingestion."ProviderActivationEvents" b WHERE b."SourceId"=NEW."SourceId" ORDER BY b."Sequence" DESC LIMIT 1)
                        AND a."Enabled" AND a."GrantId"=NEW."GrantId")
                    OR NOT EXISTS (SELECT 1 FROM governance."SourcePolicies" p JOIN ingestion."DataSources" s ON s."Id"=p."DataSourceId"
                      WHERE p."Id"=policy_id AND s."Id"=NEW."SourceId" AND s."IsEnabled"
                        AND p."Version"=(grant_json->>'PolicyVersion')::integer
                        AND p."EffectiveFromUtc"<=clock_timestamp() AND (p."EffectiveToUtc" IS NULL OR p."EffectiveToUtc">clock_timestamp())
                        AND (SELECT a."Status" FROM governance."PolicyAudits" a WHERE a."SourcePolicyId"=p."Id" ORDER BY a."Sequence" DESC LIMIT 1)='Approved'
                        AND (SELECT count(*) FROM governance."PurposePermissions" pp WHERE pp."SourcePolicyId"=p."Id"
                          AND pp."Purpose" IN ('DataRetrieval','RawPayloadStorage','HistoricalRetention','InternalAnalytics')
                          AND pp."Decision"='Allowed' AND pp."Attribution" IS NULL AND pp."MaximumRetentionDays" IS NULL)=4)
                  THEN RAISE EXCEPTION 'Provider activation rights unavailable at commit' USING ERRCODE='23514'; END IF;
                  IF (SELECT count(*) FROM ingestion."ProviderRequestEvents" r WHERE r."SourceId"=NEW."SourceId" AND r."Stage"='Reserved')>(grant_json->>'MaximumRequests')::integer
                    OR EXISTS (SELECT 1 FROM ingestion."ProviderRequestEvents" r WHERE r."SourceId"=NEW."SourceId" AND r."Stage"='Reserved'
                      AND r."RequestId"<>NEW."RequestId" AND NOT EXISTS (SELECT 1 FROM ingestion."ProviderRequestEvents" done
                        WHERE done."RequestId"=r."RequestId" AND done."Stage" IN ('Failed','Captured','Reconciled')))
                    OR EXISTS (SELECT 1 FROM ingestion."ProviderRequestEvents" r WHERE r."SourceId"=NEW."SourceId" AND r."RequestId"<>NEW."RequestId" AND r."RetryUntilUtc">NEW."RecordedUtc")
                  THEN RAISE EXCEPTION 'Provider durable quota unavailable' USING ERRCODE='23514'; END IF;
                  RETURN NEW;
                END $$;
                CREATE CONSTRAINT TRIGGER provider_request_commit_fence AFTER INSERT ON ingestion."ProviderRequestEvents"
                  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION ingestion.provider_request_fence();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderActivationEvents",
                schema: "ingestion");

            migrationBuilder.DropTable(
                name: "ProviderRequestEvents",
                schema: "ingestion");

            migrationBuilder.DropTable(
                name: "ProviderGrants",
                schema: "ingestion");
            migrationBuilder.Sql("DROP FUNCTION ingestion.provider_request_fence(); DROP FUNCTION ingestion.provider_activation_clock();");
        }
    }
}
