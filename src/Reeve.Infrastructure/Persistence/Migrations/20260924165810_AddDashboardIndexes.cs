using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reeve.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDashboardIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_jobs_completed_at",
                table: "jobs",
                column: "completed_at",
                filter: "completed_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_created_at",
                table: "jobs",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_jobs_completed_at",
                table: "jobs");

            migrationBuilder.DropIndex(
                name: "ix_jobs_created_at",
                table: "jobs");
        }
    }
}
