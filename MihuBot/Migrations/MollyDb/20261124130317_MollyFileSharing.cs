using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MihuBot.Migrations.MollyDb
{
    /// <inheritdoc />
    public partial class MollyFileSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ShareActiveAt",
                table: "mollyEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ShareIdentityBoundAt",
                table: "mollyEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "SharePartyHash",
                table: "mollyEntries",
                type: "BLOB",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "mollyShareEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipientKeyHash = table.Column<byte[]>(type: "BLOB", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReleasedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Threshold = table.Column<int>(type: "INTEGER", nullable: true),
                    RequestDigest = table.Column<byte[]>(type: "BLOB", nullable: true),
                    EncryptedHeader = table.Column<byte[]>(type: "BLOB", nullable: true),
                    EncryptedObjects = table.Column<byte[]>(type: "BLOB", nullable: true),
                    EncryptedFile = table.Column<byte[]>(type: "BLOB", nullable: true),
                    EncryptedSkey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    EncryptedDataKeySealed = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mollyShareEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mollyShareUpdates",
                columns: table => new
                {
                    PartyHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mollyShareUpdates", x => x.PartyHash);
                });

            migrationBuilder.CreateTable(
                name: "mollyShareTrustees",
                columns: table => new
                {
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Slot = table.Column<int>(type: "INTEGER", nullable: false),
                    TrusteeKeyHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EncryptedSlot = table.Column<byte[]>(type: "BLOB", nullable: false),
                    EncryptedShare = table.Column<byte[]>(type: "BLOB", nullable: true),
                    EncryptedApproval = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mollyShareTrustees", x => new { x.EntryId, x.Slot });
                    table.ForeignKey(
                        name: "FK_mollyShareTrustees_mollyShareEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "mollyShareEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mollyShareEntries_OwnerEntryId",
                table: "mollyShareEntries",
                column: "OwnerEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_mollyShareEntries_RecipientKeyHash",
                table: "mollyShareEntries",
                column: "RecipientKeyHash");

            migrationBuilder.CreateIndex(
                name: "IX_mollyShareTrustees_TrusteeKeyHash",
                table: "mollyShareTrustees",
                column: "TrusteeKeyHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mollyShareTrustees");

            migrationBuilder.DropTable(
                name: "mollyShareUpdates");

            migrationBuilder.DropTable(
                name: "mollyShareEntries");

            migrationBuilder.DropColumn(
                name: "ShareActiveAt",
                table: "mollyEntries");

            migrationBuilder.DropColumn(
                name: "ShareIdentityBoundAt",
                table: "mollyEntries");

            migrationBuilder.DropColumn(
                name: "SharePartyHash",
                table: "mollyEntries");
        }
    }
}
