using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowExecutionClaimLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowExecutions_Status_StartedAt",
                table: "WorkflowExecutions");

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedAt",
                table: "WorkflowExecutions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_Status_ClaimedAt_StartedAt",
                table: "WorkflowExecutions",
                columns: new[] { "Status", "ClaimedAt", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowExecutions_Status_ClaimedAt_StartedAt",
                table: "WorkflowExecutions");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "WorkflowExecutions");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_Status_StartedAt",
                table: "WorkflowExecutions",
                columns: new[] { "Status", "StartedAt" });
        }
    }
}
