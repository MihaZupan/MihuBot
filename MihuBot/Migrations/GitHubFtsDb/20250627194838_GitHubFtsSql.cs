using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MihuBot.Migrations.GitHubFtsDb
{
    /// <inheritdoc />
    public partial class GitHubFtsSql : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "text_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepositoryId = table.Column<long>(type: "bigint", nullable: false),
                    IssueId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    SubIdentifier = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
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

            migrationBuilder.Sql(
                """
                CREATE FULLTEXT CATALOG FTCText AS DEFAULT;
                CREATE FULLTEXT INDEX ON dbo.text_entries(Text) KEY INDEX PK_text_entries ON FTCText WITH STOPLIST = OFF, CHANGE_TRACKING AUTO;
                """,
                true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "text_entries");

            migrationBuilder.Sql(
                """
                DROP FULLTEXT INDEX on dbo.text_entries;
                DROP FULLTEXT CATALOG FTCText;
                """,
                true);
        }
    }
}
