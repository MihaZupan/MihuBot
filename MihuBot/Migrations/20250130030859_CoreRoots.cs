using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MihuBot.Migrations
{
    /// <inheritdoc />
    public partial class CoreRoots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "coreRoot",
                columns: table => new
                {
                    Sha = table.Column<string>(type: "TEXT", nullable: false),
                    Arch = table.Column<string>(type: "TEXT", nullable: true),
                    Os = table.Column<string>(type: "TEXT", nullable: true),
                    Type = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BlobName = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coreRoot", x => x.Sha);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "coreRoot");
        }
    }
}
