using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeadLetterReplayTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReplaySourceDeadLetterId",
                table: "OutboxMessages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastReplayedAt",
                table: "DeadLetterMessages",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplayCount",
                table: "DeadLetterMessages",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ReplayedFromDeadLetterId",
                table: "DeadLetterMessages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ReplaySourceDeadLetterId",
                table: "OutboxMessages",
                column: "ReplaySourceDeadLetterId");

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetterMessages_ReplayedFromDeadLetterId",
                table: "DeadLetterMessages",
                column: "ReplayedFromDeadLetterId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_ReplaySourceDeadLetterId",
                table: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_DeadLetterMessages_ReplayedFromDeadLetterId",
                table: "DeadLetterMessages");

            migrationBuilder.DropColumn(
                name: "ReplaySourceDeadLetterId",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "LastReplayedAt",
                table: "DeadLetterMessages");

            migrationBuilder.DropColumn(
                name: "ReplayCount",
                table: "DeadLetterMessages");

            migrationBuilder.DropColumn(
                name: "ReplayedFromDeadLetterId",
                table: "DeadLetterMessages");
        }
    }
}
