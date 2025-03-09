using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MihuBot.Migrations
{
    /// <inheritdoc />
    public partial class AddIdToCoreRoots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_coreRoot",
                table: "coreRoot");

            migrationBuilder.AlterColumn<string>(
                name: "Sha",
                table: "coreRoot",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<long>(
                name: "Id",
                table: "coreRoot",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_coreRoot",
                table: "coreRoot",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_coreRoot_Sha",
                table: "coreRoot",
                column: "Sha");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_coreRoot",
                table: "coreRoot");

            migrationBuilder.DropIndex(
                name: "IX_coreRoot_Sha",
                table: "coreRoot");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "coreRoot");

            migrationBuilder.AlterColumn<string>(
                name: "Sha",
                table: "coreRoot",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_coreRoot",
                table: "coreRoot",
                column: "Sha");
        }
    }
}
