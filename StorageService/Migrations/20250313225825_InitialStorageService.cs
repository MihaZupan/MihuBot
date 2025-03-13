using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StorageService.Migrations
{
    /// <inheritdoc />
    public partial class InitialStorageService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "containers",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Owner = table.Column<string>(type: "TEXT", nullable: true),
                    SasKey = table.Column<string>(type: "TEXT", nullable: true),
                    IsPublic = table.Column<bool>(type: "INTEGER", nullable: false),
                    RetentionPeriodSeconds = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_containers", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "files",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: true),
                    ContentLength = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ContainerId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_files_containers_ContainerId",
                        column: x => x.ContainerId,
                        principalTable: "containers",
                        principalColumn: "Name");
                });

            migrationBuilder.CreateIndex(
                name: "IX_files_ContainerId",
                table: "files",
                column: "ContainerId");

            migrationBuilder.CreateIndex(
                name: "IX_files_ExpiresAt",
                table: "files",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "files");

            migrationBuilder.DropTable(
                name: "containers");
        }
    }
}
