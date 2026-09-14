using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxClaimLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_Poll",
                table: "OutboxMessages");

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedAt",
                table: "OutboxMessages",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Poll",
                table: "OutboxMessages",
                columns: new[] { "IsProcessed", "FailedAt", "NextRetryAt", "ClaimedAt", "CreatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_Poll",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "OutboxMessages");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Poll",
                table: "OutboxMessages",
                columns: new[] { "IsProcessed", "FailedAt", "NextRetryAt", "CreatedAt", "Id" });
        }
    }
}
