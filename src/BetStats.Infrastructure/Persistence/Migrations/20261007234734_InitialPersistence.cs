using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BetStats.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ingestion");

            migrationBuilder.CreateTable(
                name: "DataSources",
                schema: "ingestion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataSources", x => x.Id);
                    table.CheckConstraint("CK_DataSources_Code_NotBlank", "length(btrim(\"Code\")) > 0");
                    table.CheckConstraint("CK_DataSources_DisplayName_NotBlank", "length(btrim(\"DisplayName\")) > 0");
                });

            migrationBuilder.CreateTable(
                name: "IngestionRuns",
                schema: "ingestion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DataSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestionRuns", x => x.Id);
                    table.UniqueConstraint("AK_IngestionRuns_Id_DataSourceId", x => new { x.Id, x.DataSourceId });
                    table.CheckConstraint("CK_IngestionRuns_Status", "\"Status\" IN ('Pending', 'Running', 'Succeeded', 'Failed')");
                    table.CheckConstraint("CK_IngestionRuns_Times", "\"CompletedAtUtc\" IS NULL OR (\"StartedAtUtc\" IS NOT NULL AND \"CompletedAtUtc\" >= \"StartedAtUtc\")");
                    table.ForeignKey(
                        name: "FK_IngestionRuns_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "ingestion",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RawPayloads",
                schema: "ingestion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DataSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    IngestionRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExternalReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RetrievedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ContentHashSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RawPayloads", x => x.Id);
                    table.CheckConstraint("CK_RawPayloads_ContentType_NotBlank", "length(btrim(\"ContentType\")) > 0");
                    table.CheckConstraint("CK_RawPayloads_Hash", "\"ContentHashSha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_RawPayloads_StorageKey_NotBlank", "length(btrim(\"StorageKey\")) > 0");
                    table.ForeignKey(
                        name: "FK_RawPayloads_DataSources_DataSourceId",
                        column: x => x.DataSourceId,
                        principalSchema: "ingestion",
                        principalTable: "DataSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RawPayloads_IngestionRuns_IngestionRunId_DataSourceId",
                        columns: x => new { x.IngestionRunId, x.DataSourceId },
                        principalSchema: "ingestion",
                        principalTable: "IngestionRuns",
                        principalColumns: new[] { "Id", "DataSourceId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataSources_Code",
                schema: "ingestion",
                table: "DataSources",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IngestionRuns_DataSourceId_CreatedAtUtc",
                schema: "ingestion",
                table: "IngestionRuns",
                columns: new[] { "DataSourceId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RawPayloads_ContentHashSha256",
                schema: "ingestion",
                table: "RawPayloads",
                column: "ContentHashSha256");

            migrationBuilder.CreateIndex(
                name: "IX_RawPayloads_DataSourceId_RetrievedAtUtc",
                schema: "ingestion",
                table: "RawPayloads",
                columns: new[] { "DataSourceId", "RetrievedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RawPayloads_IngestionRunId_DataSourceId",
                schema: "ingestion",
                table: "RawPayloads",
                columns: new[] { "IngestionRunId", "DataSourceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RawPayloads",
                schema: "ingestion");

            migrationBuilder.DropTable(
                name: "IngestionRuns",
                schema: "ingestion");

            migrationBuilder.DropTable(
                name: "DataSources",
                schema: "ingestion");
        }
    }
}
