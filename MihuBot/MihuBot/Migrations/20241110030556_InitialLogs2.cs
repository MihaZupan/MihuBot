using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MihuBot.Migrations
{
    /// <inheritdoc />
    public partial class InitialLogs2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Timestamp",
                table: "logs",
                newName: "Snowflake");

            migrationBuilder.RenameIndex(
                name: "IX_logs_Timestamp",
                table: "logs",
                newName: "IX_logs_Snowflake");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Snowflake",
                table: "logs",
                newName: "Timestamp");

            migrationBuilder.RenameIndex(
                name: "IX_logs_Snowflake",
                table: "logs",
                newName: "IX_logs_Timestamp");
        }
    }
}
