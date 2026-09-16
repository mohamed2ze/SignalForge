using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionConcurrencyTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "WorkflowStepExecutions",
                type: "datetime2",
                nullable: false,
                // Required backfill for rows created before this migration; the domain sets
                // UpdatedAt explicitly on every insert/update thereafter, so the default is
                // only a safety net for raw writes.
                defaultValueSql: "SYSUTCDATETIME()");

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "WorkflowExecutions",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "SYSUTCDATETIME()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "WorkflowStepExecutions");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "WorkflowExecutions");
        }
    }
}
