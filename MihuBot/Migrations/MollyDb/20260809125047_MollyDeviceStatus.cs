using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MihuBot.Migrations.MollyDb
{
    /// <inheritdoc />
    public partial class MollyDeviceStatus : Migration
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

            migrationBuilder.AddColumn<int>(
                name: "BatteryLevel",
                table: "mollyEntries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LocationEnabled",
                table: "mollyEntries",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BatteryLevel",
                table: "mollyEntries");

            migrationBuilder.DropColumn(
                name: "LocationEnabled",
                table: "mollyEntries");

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
