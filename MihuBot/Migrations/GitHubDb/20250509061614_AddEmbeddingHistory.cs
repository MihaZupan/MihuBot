using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MihuBot.Migrations.GitHubDb
{
    /// <inheritdoc />
    public partial class AddEmbeddingHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ingested_embedding_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourceIdentifier = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingested_embedding_history", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_issues_UpdatedAt",
                table: "issues",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ingested_embedding_history_ResourceIdentifier",
                table: "ingested_embedding_history",
                column: "ResourceIdentifier");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ingested_embedding_history");

            migrationBuilder.DropIndex(
                name: "IX_issues_UpdatedAt",
                table: "issues");
        }
    }
}
