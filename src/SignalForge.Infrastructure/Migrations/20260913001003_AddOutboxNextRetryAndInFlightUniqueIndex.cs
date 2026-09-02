using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxNextRetryAndInFlightUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_ReplaySourceDeadLetterId",
                table: "OutboxMessages");

            migrationBuilder.AddColumn<DateTime>(
                name: "NextRetryAt",
                table: "OutboxMessages",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_OutboxMessages_ReplaySourceDeadLetterId_Unprocessed",
                table: "OutboxMessages",
                column: "ReplaySourceDeadLetterId",
                unique: true,
                filter: "[ReplaySourceDeadLetterId] IS NOT NULL AND [IsProcessed] = CAST(0 AS bit)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_OutboxMessages_ReplaySourceDeadLetterId_Unprocessed",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "NextRetryAt",
                table: "OutboxMessages");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ReplaySourceDeadLetterId",
                table: "OutboxMessages",
                column: "ReplaySourceDeadLetterId");
        }
    }
}
