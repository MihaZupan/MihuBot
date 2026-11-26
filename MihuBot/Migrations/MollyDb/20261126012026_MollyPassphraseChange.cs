using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MihuBot.Migrations.MollyDb
{
    /// <inheritdoc />
    public partial class MollyPassphraseChange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "PreviousDerivedHash",
                table: "mollyEntries",
                type: "BLOB",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_mollyEntries_PreviousDerivedHash",
                table: "mollyEntries",
                column: "PreviousDerivedHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_mollyEntries_PreviousDerivedHash",
                table: "mollyEntries");

            migrationBuilder.DropColumn(
                name: "PreviousDerivedHash",
                table: "mollyEntries");
        }
    }
}
