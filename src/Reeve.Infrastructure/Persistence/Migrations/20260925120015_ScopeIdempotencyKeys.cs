using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reeve.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopeIdempotencyKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_jobs_idempotency_key",
                table: "jobs");

            migrationBuilder.AddColumn<string>(
                name: "idempotency_scope",
                table: "jobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // Existing keyed jobs: schedule-generated keys belong to the platform; API keys to whoever
            // created the job, as recorded by its job.created audit event. Anything else (created
            // before auditing existed) keeps the shared empty scope.
            migrationBuilder.Sql("""
                UPDATE jobs
                SET idempotency_scope = CASE WHEN idempotency_key LIKE 'schedule:%' THEN 'system' ELSE '' END
                WHERE idempotency_key IS NOT NULL;

                UPDATE jobs j
                SET idempotency_scope = a.actor
                FROM audit_events a
                WHERE a.action = 'job.created'
                  AND a.entity_id = j.id::text
                  AND j.idempotency_key IS NOT NULL
                  AND j.idempotency_key NOT LIKE 'schedule:%';
                """);

            migrationBuilder.CreateIndex(
                name: "ix_jobs_idempotency_key",
                table: "jobs",
                columns: new[] { "idempotency_scope", "idempotency_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Fails if two callers have since used the same key: the old index was global.
            migrationBuilder.DropIndex(
                name: "ix_jobs_idempotency_key",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "idempotency_scope",
                table: "jobs");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_idempotency_key",
                table: "jobs",
                column: "idempotency_key",
                unique: true);
        }
    }
}
