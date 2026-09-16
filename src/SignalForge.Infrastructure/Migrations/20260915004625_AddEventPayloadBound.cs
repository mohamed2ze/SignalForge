using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignalForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEventPayloadBound : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The payload column stays nvarchar(max) (SQL Server cannot dimension nvarchar beyond
            // 4000), so the bound declared on EventConfiguration is enforced here at the database
            // as a CHECK constraint, mirroring the request-level and domain-level validation.
            // DATALENGTH counts bytes (2 per nvarchar char) and, unlike LEN, keeps trailing spaces.
            migrationBuilder.Sql(
                "ALTER TABLE [Events] ADD CONSTRAINT [CK_Events_Payload_Chars]" +
                " CHECK (DATALENGTH([Payload]) / 2 <= 1048576)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE [Events] DROP CONSTRAINT [CK_Events_Payload_Chars]");
        }
    }
}
