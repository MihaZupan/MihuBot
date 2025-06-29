using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable
#pragma warning disable CA1861 // Avoid constant arrays as arguments

namespace MihuBot.Migrations.GitHubFtsDb
{
    /// <inheritdoc />
    public partial class AddGitHubFts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "text_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<long>(type: "bigint", nullable: false),
                    IssueId = table.Column<string>(type: "text", nullable: true),
                    SubIdentifier = table.Column<string>(type: "text", nullable: true),
                    Text = table.Column<string>(type: "text", nullable: true),
                    TextVector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true)
                        .Annotation("Npgsql:TsVectorConfig", "english")
                        .Annotation("Npgsql:TsVectorProperties", new[] { "Text" })
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_text_entries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_text_entries_IssueId",
                table: "text_entries",
                column: "IssueId");

            migrationBuilder.CreateIndex(
                name: "IX_text_entries_RepositoryId",
                table: "text_entries",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_text_entries_TextVector",
                table: "text_entries",
                column: "TextVector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "text_entries");
        }
    }
}
