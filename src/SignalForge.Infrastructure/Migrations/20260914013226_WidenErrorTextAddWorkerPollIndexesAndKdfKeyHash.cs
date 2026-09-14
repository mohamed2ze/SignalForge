using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WidenErrorTextAddWorkerPollIndexesAndKdfKeyHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowStepExecutions_WorkflowExecutionId",
                table: "WorkflowStepExecutions");

            migrationBuilder.AlterColumn<string>(
                name: "Output",
                table: "WorkflowStepExecutions",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ErrorMessage",
                table: "WorkflowStepExecutions",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ErrorMessage",
                table: "OutboxMessages",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ErrorMessage",
                table: "DeadLetterMessages",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(2000)",
                oldMaxLength: 2000);

            migrationBuilder.AlterColumn<string>(
                name: "KeyHash",
                table: "ApiKeys",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(445)",
                oldMaxLength: 445);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowStepExecutions_Execution_Step_Status",
                table: "WorkflowStepExecutions",
                columns: new[] { "WorkflowExecutionId", "StepNumber", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_Status_StartedAt",
                table: "WorkflowExecutions",
                columns: new[] { "Status", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Poll",
                table: "OutboxMessages",
                columns: new[] { "IsProcessed", "FailedAt", "NextRetryAt", "CreatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowStepExecutions_Execution_Step_Status",
                table: "WorkflowStepExecutions");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowExecutions_Status_StartedAt",
                table: "WorkflowExecutions");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_Poll",
                table: "OutboxMessages");

            migrationBuilder.AlterColumn<string>(
                name: "Output",
                table: "WorkflowStepExecutions",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ErrorMessage",
                table: "WorkflowStepExecutions",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ErrorMessage",
                table: "OutboxMessages",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ErrorMessage",
                table: "DeadLetterMessages",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "KeyHash",
                table: "ApiKeys",
                type: "nvarchar(445)",
                maxLength: 445,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(512)",
                oldMaxLength: 512);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowStepExecutions_WorkflowExecutionId",
                table: "WorkflowStepExecutions",
                column: "WorkflowExecutionId");
        }
    }
}
