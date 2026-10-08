using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BetStats.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditRemediation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RecordedAtUtc",
                schema: "provenance",
                table: "Observations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "clock_timestamp()");
            migrationBuilder.Sql("""
                CREATE FUNCTION provenance.record_observation_availability() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE raw ingestion."RawPayloads"%ROWTYPE;
                BEGIN
                    NEW."RecordedAtUtc" := clock_timestamp();
                    IF NEW."RawPayloadId" IS NOT NULL THEN
                        SELECT * INTO raw FROM ingestion."RawPayloads" WHERE "Id" = NEW."RawPayloadId";
                        IF NOT FOUND OR raw."DataSourceId" <> NEW."DataSourceId" THEN
                            RAISE EXCEPTION 'RAW source reference is invalid' USING ERRCODE = '23503';
                        END IF;
                        IF NEW."RetrievedAtUtc" < raw."RetrievedAtUtc" OR NEW."AvailableAtUtc" < raw."CreatedAtUtc" OR NEW."CreatedAtUtc" < raw."CreatedAtUtc" THEN
                            RAISE EXCEPTION 'Observation precedes RAW capture metadata' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END; $$;
                CREATE TRIGGER trusted_observation_availability BEFORE INSERT ON provenance."Observations"
                    FOR EACH ROW EXECUTE FUNCTION provenance.record_observation_availability();
                CREATE TRIGGER prevent_raw_mutation BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON ingestion."RawPayloads" FOR EACH STATEMENT
                    EXECUTE FUNCTION provenance.reject_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER prevent_raw_mutation ON ingestion."RawPayloads";
                DROP TRIGGER trusted_observation_availability ON provenance."Observations";
                DROP FUNCTION provenance.record_observation_availability();
                """);
            migrationBuilder.DropColumn(
                name: "RecordedAtUtc",
                schema: "provenance",
                table: "Observations");
        }
    }
}
