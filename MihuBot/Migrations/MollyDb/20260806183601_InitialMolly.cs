using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MihuBot.Migrations.MollyDb
{
    /// <inheritdoc />
    public partial class InitialMolly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mollyEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HashPrefix = table.Column<int>(type: "INTEGER", nullable: false),
                    DerivedHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    EncryptedServerHmac = table.Column<byte[]>(type: "BLOB", nullable: true),
                    EncryptedNickname = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CreatedDay = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    LastSeenDay = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    LockRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    WipeRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    AlertsMuted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mollyEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mollyAlerts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EncryptedPayload = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mollyAlerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mollyAlerts_mollyEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "mollyEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mollyAlerts_EntryId",
                table: "mollyAlerts",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_mollyEntries_HashPrefix",
                table: "mollyEntries",
                column: "HashPrefix");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mollyAlerts");

            migrationBuilder.DropTable(
                name: "mollyEntries");
        }
    }
}
