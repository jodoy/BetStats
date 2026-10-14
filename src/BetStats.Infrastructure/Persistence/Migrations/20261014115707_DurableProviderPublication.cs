using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BetStats.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableProviderPublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProviderPublications",
                schema: "ingestion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderPublications", x => x.Id);
                    table.CheckConstraint("CK_ProviderPublications_Hashes", "\"Key\" ~ '^[0-9a-f]{64}$' AND \"Hash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_ProviderPublications_Size", "octet_length(\"Content\") BETWEEN 1 AND 16777216");
                    table.ForeignKey(
                        name: "FK_ProviderPublications_Executions_ExecutionId",
                        column: x => x.ExecutionId,
                        principalSchema: "pipeline",
                        principalTable: "Executions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProviderPublications_RawPayloads_RawId_SourceId",
                        columns: x => new { x.RawId, x.SourceId },
                        principalSchema: "ingestion",
                        principalTable: "RawPayloads",
                        principalColumns: new[] { "Id", "DataSourceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProviderPublications_SourcePolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalSchema: "governance",
                        principalTable: "SourcePolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderPublications_ExecutionId",
                schema: "ingestion",
                table: "ProviderPublications",
                column: "ExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderPublications_PolicyId",
                schema: "ingestion",
                table: "ProviderPublications",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderPublications_RawId_SourceId",
                schema: "ingestion",
                table: "ProviderPublications",
                columns: new[] { "RawId", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderPublications_SourceId_Key",
                schema: "ingestion",
                table: "ProviderPublications",
                columns: new[] { "SourceId", "Key" },
                unique: true);
            migrationBuilder.Sql("""
                CREATE FUNCTION ingestion.provider_publication_clock() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  NEW."RecordedUtc" := clock_timestamp();
                  IF NOT EXISTS (SELECT 1 FROM governance."SourcePolicies" WHERE "Id"=NEW."PolicyId" AND "DataSourceId"=NEW."SourceId") THEN
                    RAISE EXCEPTION 'Publication policy source mismatch' USING ERRCODE='23514';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER provider_publication_clock BEFORE INSERT ON ingestion."ProviderPublications"
                  FOR EACH ROW EXECUTE FUNCTION ingestion.provider_publication_clock();
                CREATE TRIGGER provider_publications_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON ingestion."ProviderPublications"
                  FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                CREATE FUNCTION ingestion.provider_publication_fence() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE manifest jsonb := convert_from(NEW."Content", 'UTF8')::jsonb;
                BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pipeline."Executions" e WHERE e."Id"=NEW."ExecutionId"
                    AND e."JobId"=(manifest->>'JobId')::uuid AND e."Owner"=(manifest->>'Owner')::uuid
                    AND e."Attempt"=(manifest->>'Attempt')::integer AND e."State"=2 AND NOT e."CancelRequested"
                    AND e."LeaseUntilUtc">clock_timestamp()) THEN
                    RAISE EXCEPTION 'Publication owner fenced at commit' USING ERRCODE='23514';
                  END IF;
                  IF NOT EXISTS (
                    SELECT 1 FROM governance."SourcePolicies" p
                    JOIN ingestion."DataSources" s ON s."Id"=p."DataSourceId"
                    JOIN ingestion."RawPayloads" raw ON raw."Id"=NEW."RawId" AND raw."DataSourceId"=s."Id"
                    WHERE p."Id"=NEW."PolicyId" AND s."Id"=NEW."SourceId" AND s."IsEnabled"
                      AND p."Version"=(manifest->'Input'->>'PolicyVersion')::integer
                      AND p."EffectiveFromUtc"<=clock_timestamp() AND (p."EffectiveToUtc" IS NULL OR p."EffectiveToUtc">clock_timestamp())
                      AND (SELECT a."Status" FROM governance."PolicyAudits" a WHERE a."SourcePolicyId"=p."Id" ORDER BY a."Sequence" DESC LIMIT 1)='Approved'
                      AND (SELECT count(*) FROM governance."PurposePermissions" permission
                        WHERE permission."SourcePolicyId"=p."Id" AND permission."Purpose" IN ('DataRetrieval','RawPayloadStorage','HistoricalRetention','InternalAnalytics')
                          AND permission."Decision"='Allowed' AND permission."Attribution" IS NULL
                          AND permission."MaximumRetentionDays" IS NULL)=4
                  ) THEN
                    RAISE EXCEPTION 'Publication rights unavailable at commit' USING ERRCODE='23514';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE CONSTRAINT TRIGGER provider_publication_commit_fence AFTER INSERT ON ingestion."ProviderPublications"
                  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION ingestion.provider_publication_fence();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderPublications",
                schema: "ingestion");
            migrationBuilder.Sql("DROP FUNCTION ingestion.provider_publication_clock(); DROP FUNCTION ingestion.provider_publication_fence();");
        }
    }
}
