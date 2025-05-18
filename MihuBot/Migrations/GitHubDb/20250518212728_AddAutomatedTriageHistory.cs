using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MihuBot.Migrations.GitHubDb
{
    /// <inheritdoc />
    public partial class AddAutomatedTriageHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "triaged_issues",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: true),
                    TriageReportIssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    IssueId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_triaged_issues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_triaged_issues_issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_triaged_issues_IssueId",
                table: "triaged_issues",
                column: "IssueId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "triaged_issues");
        }
    }
}
