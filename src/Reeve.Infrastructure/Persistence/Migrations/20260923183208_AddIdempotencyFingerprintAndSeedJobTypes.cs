using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Reeve.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIdempotencyFingerprintAndSeedJobTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "idempotency_fingerprint",
                table: "jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.InsertData(
                table: "job_types",
                columns: new[] { "type", "enabled", "max_retries", "timeout_seconds" },
                values: new object[,]
                {
                    { "GENERATE_REPORT", true, 3, 300 },
                    { "PROCESS_IMAGE", true, 3, 120 },
                    { "SEND_NOTIFICATION", true, 5, 30 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "job_types",
                keyColumn: "type",
                keyValue: "GENERATE_REPORT");

            migrationBuilder.DeleteData(
                table: "job_types",
                keyColumn: "type",
                keyValue: "PROCESS_IMAGE");

            migrationBuilder.DeleteData(
                table: "job_types",
                keyColumn: "type",
                keyValue: "SEND_NOTIFICATION");

            migrationBuilder.DropColumn(
                name: "idempotency_fingerprint",
                table: "jobs");
        }
    }
}
