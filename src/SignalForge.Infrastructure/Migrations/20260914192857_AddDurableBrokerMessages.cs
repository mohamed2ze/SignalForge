using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableBrokerMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BrokerMessages",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrokerMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BrokerMessages_PublishedAt",
                table: "BrokerMessages",
                column: "PublishedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BrokerMessages");
        }
    }
}
