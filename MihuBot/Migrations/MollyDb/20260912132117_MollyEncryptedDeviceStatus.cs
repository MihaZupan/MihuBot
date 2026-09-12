using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MihuBot.Migrations.MollyDb
{
    /// <inheritdoc />
    public partial class MollyEncryptedDeviceStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppVersion",
                table: "mollyEntries");

            migrationBuilder.DropColumn(
                name: "BatteryLevel",
                table: "mollyEntries");

            migrationBuilder.DropColumn(
                name: "LocationEnabled",
                table: "mollyEntries");

            migrationBuilder.AddColumn<byte[]>(
                name: "EncryptedDeviceStatus",
                table: "mollyEntries",
                type: "BLOB",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncryptedDeviceStatus",
                table: "mollyEntries");

            migrationBuilder.AddColumn<string>(
                name: "AppVersion",
                table: "mollyEntries",
                type: "TEXT",
                nullable: true);

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
    }
}
