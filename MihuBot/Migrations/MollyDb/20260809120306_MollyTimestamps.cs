using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MihuBot.Migrations.MollyDb
{
    /// <inheritdoc />
    public partial class MollyTimestamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "LastSeenDay",
                table: "mollyEntries",
                newName: "LastSeenAt");

            migrationBuilder.RenameColumn(
                name: "CreatedDay",
                table: "mollyEntries",
                newName: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "LastSeenAt",
                table: "mollyEntries",
                newName: "LastSeenDay");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "mollyEntries",
                newName: "CreatedDay");
        }
    }
}
