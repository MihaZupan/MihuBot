using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MihuBot.Migrations.GitHubDb
{
    /// <inheritdoc />
    public partial class AddGitHubFts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ingested_fts_record_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourceIdentifier = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingested_fts_record_history", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ingested_fts_record_history_ResourceIdentifier",
                table: "ingested_fts_record_history",
                column: "ResourceIdentifier");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ingested_fts_record_history");
        }
    }
}
