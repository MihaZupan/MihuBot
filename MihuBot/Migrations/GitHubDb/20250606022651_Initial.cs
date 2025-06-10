using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MihuBot.Migrations.GitHubDb
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "body_edit_history",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ResourceIdentifier = table.Column<string>(type: "TEXT", nullable: true),
                    IsComment = table.Column<bool>(type: "INTEGER", nullable: false),
                    PreviousBody = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_body_edit_history", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ingested_embedding_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourceIdentifier = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingested_embedding_history", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeIdentifier = table.Column<string>(type: "TEXT", nullable: true),
                    Login = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    HtmlUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Followers = table.Column<int>(type: "INTEGER", nullable: false),
                    Following = table.Column<int>(type: "INTEGER", nullable: false),
                    Company = table.Column<string>(type: "TEXT", nullable: true),
                    Location = table.Column<string>(type: "TEXT", nullable: true),
                    Bio = table.Column<string>(type: "TEXT", nullable: true),
                    Type = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EntryUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "repositories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeIdentifier = table.Column<string>(type: "TEXT", nullable: true),
                    HtmlUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    FullName = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Private = table.Column<bool>(type: "INTEGER", nullable: false),
                    Archived = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<long>(type: "INTEGER", nullable: false),
                    LastRepositoryMetadataUpdate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastIssuesUpdate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastIssueCommentsUpdate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastPullRequestReviewCommentsUpdate = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repositories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_repositories_users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "issues",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    GitHubIdentifier = table.Column<long>(type: "INTEGER", nullable: false),
                    NodeIdentifier = table.Column<string>(type: "TEXT", nullable: true),
                    HtmlUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Body = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Locked = table.Column<bool>(type: "INTEGER", nullable: false),
                    ActiveLockReason = table.Column<int>(type: "INTEGER", nullable: true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    RepositoryId = table.Column<long>(type: "INTEGER", nullable: false),
                    Plus1 = table.Column<int>(type: "INTEGER", nullable: false),
                    Minus1 = table.Column<int>(type: "INTEGER", nullable: false),
                    Laugh = table.Column<int>(type: "INTEGER", nullable: false),
                    Confused = table.Column<int>(type: "INTEGER", nullable: false),
                    Heart = table.Column<int>(type: "INTEGER", nullable: false),
                    Hooray = table.Column<int>(type: "INTEGER", nullable: false),
                    Eyes = table.Column<int>(type: "INTEGER", nullable: false),
                    Rocket = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_issues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_issues_repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_issues_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "labels",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    GitHubIdentifier = table.Column<long>(type: "INTEGER", nullable: false),
                    NodeIdentifier = table.Column<string>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Color = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    RepositoryId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_labels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_labels_repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "comments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    GitHubIdentifier = table.Column<long>(type: "INTEGER", nullable: false),
                    NodeIdentifier = table.Column<string>(type: "TEXT", nullable: true),
                    HtmlUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Body = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AuthorAssociation = table.Column<int>(type: "INTEGER", nullable: false),
                    IsPrReviewComment = table.Column<bool>(type: "INTEGER", nullable: false),
                    IssueId = table.Column<string>(type: "TEXT", nullable: true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Plus1 = table.Column<int>(type: "INTEGER", nullable: false),
                    Minus1 = table.Column<int>(type: "INTEGER", nullable: false),
                    Laugh = table.Column<int>(type: "INTEGER", nullable: false),
                    Confused = table.Column<int>(type: "INTEGER", nullable: false),
                    Heart = table.Column<int>(type: "INTEGER", nullable: false),
                    Hooray = table.Column<int>(type: "INTEGER", nullable: false),
                    Eyes = table.Column<int>(type: "INTEGER", nullable: false),
                    Rocket = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_comments_issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "issues",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_comments_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pullrequests",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    GitHubIdentifier = table.Column<long>(type: "INTEGER", nullable: false),
                    NodeIdentifier = table.Column<string>(type: "TEXT", nullable: true),
                    MergedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Draft = table.Column<bool>(type: "INTEGER", nullable: false),
                    Mergeable = table.Column<bool>(type: "INTEGER", nullable: true),
                    MergeableState = table.Column<int>(type: "INTEGER", nullable: true),
                    MergeCommitSha = table.Column<string>(type: "TEXT", nullable: true),
                    Commits = table.Column<int>(type: "INTEGER", nullable: false),
                    Additions = table.Column<int>(type: "INTEGER", nullable: false),
                    Deletions = table.Column<int>(type: "INTEGER", nullable: false),
                    ChangedFiles = table.Column<int>(type: "INTEGER", nullable: false),
                    MaintainerCanModify = table.Column<bool>(type: "INTEGER", nullable: true),
                    IssueId = table.Column<string>(type: "TEXT", nullable: true),
                    MergedById = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pullrequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pullrequests_issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "issues",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "triaged_issues",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: true),
                    TriageReportIssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    IssueId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_triaged_issues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_triaged_issues_issues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "issues",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "IssueInfoLabelInfo",
                columns: table => new
                {
                    IssuesId = table.Column<string>(type: "TEXT", nullable: false),
                    LabelsId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueInfoLabelInfo", x => new { x.IssuesId, x.LabelsId });
                    table.ForeignKey(
                        name: "FK_IssueInfoLabelInfo_issues_IssuesId",
                        column: x => x.IssuesId,
                        principalTable: "issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IssueInfoLabelInfo_labels_LabelsId",
                        column: x => x.LabelsId,
                        principalTable: "labels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_comments_IssueId",
                table: "comments",
                column: "IssueId");

            migrationBuilder.CreateIndex(
                name: "IX_comments_UpdatedAt",
                table: "comments",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_comments_UserId",
                table: "comments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ingested_embedding_history_ResourceIdentifier",
                table: "ingested_embedding_history",
                column: "ResourceIdentifier");

            migrationBuilder.CreateIndex(
                name: "IX_IssueInfoLabelInfo_LabelsId",
                table: "IssueInfoLabelInfo",
                column: "LabelsId");

            migrationBuilder.CreateIndex(
                name: "IX_issues_CreatedAt",
                table: "issues",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_issues_Number",
                table: "issues",
                column: "Number");

            migrationBuilder.CreateIndex(
                name: "IX_issues_RepositoryId",
                table: "issues",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_issues_UpdatedAt",
                table: "issues",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_issues_UserId",
                table: "issues",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_labels_RepositoryId",
                table: "labels",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_pullrequests_IssueId",
                table: "pullrequests",
                column: "IssueId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_repositories_OwnerId",
                table: "repositories",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_triaged_issues_IssueId",
                table: "triaged_issues",
                column: "IssueId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "body_edit_history");

            migrationBuilder.DropTable(
                name: "comments");

            migrationBuilder.DropTable(
                name: "ingested_embedding_history");

            migrationBuilder.DropTable(
                name: "IssueInfoLabelInfo");

            migrationBuilder.DropTable(
                name: "pullrequests");

            migrationBuilder.DropTable(
                name: "triaged_issues");

            migrationBuilder.DropTable(
                name: "labels");

            migrationBuilder.DropTable(
                name: "issues");

            migrationBuilder.DropTable(
                name: "repositories");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
