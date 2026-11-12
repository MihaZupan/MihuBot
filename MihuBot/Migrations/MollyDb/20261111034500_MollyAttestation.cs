using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MihuBot.Migrations.MollyDb
{
    /// <inheritdoc />
    public partial class MollyAttestation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_mollyEntries_HashPrefix",
                table: "mollyEntries");

            migrationBuilder.DropColumn(
                name: "HashPrefix",
                table: "mollyEntries");

            migrationBuilder.AddColumn<byte[]>(
                name: "EncryptedAttestationState",
                table: "mollyEntries",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastLoginAt",
                table: "mollyEntries",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "ActiveSessionIssuedAt",
                table: "mollyEntries",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Fails with "UNIQUE constraint failed: mollyEntries.DerivedHash" if racing registrations ever
            // created duplicate entries. Those have to be resolved manually - they hold different secrets.
            migrationBuilder.CreateIndex(
                name: "IX_mollyEntries_DerivedHash",
                table: "mollyEntries",
                column: "DerivedHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_mollyEntries_DerivedHash",
                table: "mollyEntries");

            migrationBuilder.DropColumn(
                name: "ActiveSessionIssuedAt",
                table: "mollyEntries");

            migrationBuilder.DropColumn(
                name: "LastLoginAt",
                table: "mollyEntries");

            migrationBuilder.DropColumn(
                name: "EncryptedAttestationState",
                table: "mollyEntries");

            migrationBuilder.AddColumn<int>(
                name: "HashPrefix",
                table: "mollyEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // The first byte of DerivedHash, from its first two hex digits.
            migrationBuilder.Sql("""
                UPDATE mollyEntries SET HashPrefix =
                    (instr('0123456789ABCDEF', substr(hex(DerivedHash), 1, 1)) - 1) * 16 +
                    (instr('0123456789ABCDEF', substr(hex(DerivedHash), 2, 1)) - 1);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_mollyEntries_HashPrefix",
                table: "mollyEntries",
                column: "HashPrefix");
        }
    }
}