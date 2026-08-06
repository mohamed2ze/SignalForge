using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeadLetterMessagesAndEventUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Events_TenantId",
                table: "Events");

            migrationBuilder.CreateTable(
                name: "DeadLetterMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalMessageType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OriginalPayload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FailedStepType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FailedStepNumber = table.Column<int>(type: "int", nullable: false),
                    WorkflowExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowStepExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    FinalAttemptCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsProcessed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeadLetterMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeadLetterMessages_WorkflowExecutions_WorkflowExecutionId",
                        column: x => x.WorkflowExecutionId,
                        principalTable: "WorkflowExecutions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DeadLetterMessages_WorkflowStepExecutions_WorkflowStepExecutionId",
                        column: x => x.WorkflowStepExecutionId,
                        principalTable: "WorkflowStepExecutions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_TenantId_ExternalEventId",
                table: "Events",
                columns: new[] { "TenantId", "ExternalEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetterMessages_CreatedAt",
                table: "DeadLetterMessages",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetterMessages_TenantId",
                table: "DeadLetterMessages",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetterMessages_WorkflowExecutionId",
                table: "DeadLetterMessages",
                column: "WorkflowExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetterMessages_WorkflowStepExecutionId",
                table: "DeadLetterMessages",
                column: "WorkflowStepExecutionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeadLetterMessages");

            migrationBuilder.DropIndex(
                name: "IX_Events_TenantId_ExternalEventId",
                table: "Events");

            migrationBuilder.CreateIndex(
                name: "IX_Events_TenantId",
                table: "Events",
                column: "TenantId");
        }
    }
}
