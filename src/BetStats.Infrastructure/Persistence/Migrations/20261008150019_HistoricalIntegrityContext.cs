using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BetStats.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HistoricalIntegrityContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FootballRawContexts",
                schema: "ingestion",
                columns: table => new
                {
                    RawId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompetitionReference = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SeasonReference = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FootballRawContexts", x => x.RawId);
                    table.CheckConstraint("CK_FootballRawContexts_Scope", "length(btrim(\"CompetitionReference\")) > 0 AND length(btrim(\"SeasonReference\")) > 0 AND \"Version\" = 1");
                    table.ForeignKey(
                        name: "FK_FootballRawContexts_RawPayloads_RawId_SourceId",
                        columns: x => new { x.RawId, x.SourceId },
                        principalSchema: "ingestion",
                        principalTable: "RawPayloads",
                        principalColumns: new[] { "Id", "DataSourceId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FootballRawContexts_RawId_SourceId",
                schema: "ingestion",
                table: "FootballRawContexts",
                columns: new[] { "RawId", "SourceId" });
            migrationBuilder.Sql("""
                CREATE FUNCTION ingestion.record_football_context() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN NEW."RecordedAtUtc" := clock_timestamp(); RETURN NEW; END $$;
                CREATE TRIGGER trusted_context_clock BEFORE INSERT ON ingestion."FootballRawContexts" FOR EACH ROW EXECUTE FUNCTION ingestion.record_football_context();
                CREATE TRIGGER immutable_context BEFORE UPDATE OR DELETE OR TRUNCATE ON ingestion."FootballRawContexts" FOR EACH STATEMENT EXECUTE FUNCTION provenance.reject_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FootballRawContexts",
                schema: "ingestion");
            migrationBuilder.Sql("DROP FUNCTION ingestion.record_football_context();");
        }
    }
}
