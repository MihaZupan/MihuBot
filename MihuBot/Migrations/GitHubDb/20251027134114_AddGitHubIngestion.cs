using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#pragma warning disable CA1861 // Avoid constant arrays as arguments

#nullable disable

namespace MihuBot.Migrations.GitHubDb
{
    /// <inheritdoc />
    public partial class AddGitHubIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "body_edit_history",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ResourceIdentifier = table.Column<string>(type: "text", nullable: true),
                    IsComment = table.Column<bool>(type: "boolean", nullable: false),
                    PreviousBody = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_body_edit_history", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ingested_embedding_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<long>(type: "bigint", nullable: false),
                    IssueId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingested_embedding_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "semantic_ingestion_backlog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IssueId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_semantic_ingestion_backlog", x => x.Id);
                });

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

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NodeIdentifier = table.Column<string>(type: "text", nullable: true),
                    Login = table.Column<string>(type: "text", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: true),
                    HtmlUrl = table.Column<string>(type: "text", nullable: true),
                    Followers = table.Column<int>(type: "integer", nullable: false),
                    Following = table.Column<int>(type: "integer", nullable: false),
                    Company = table.Column<string>(type: "text", nullable: true),
                    Location = table.Column<string>(type: "text", nullable: true),
                    Bio = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EntryUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "repositories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NodeIdentifier = table.Column<string>(type: "text", nullable: true),
                    HtmlUrl = table.Column<string>(type: "text", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: true),
                    FullName = table.Column<string>(type: "text", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Private = table.Column<bool>(type: "boolean", nullable: false),
                    Archived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OwnerId = table.Column<long>(type: "bigint", nullable: false),
                    InitialIngestionInProgress = table.Column<bool>(type: "boolean", nullable: false),
                    IssueRescanCursor = table.Column<string>(type: "text", nullable: true),
                    PullRequestRescanCursor = table.Column<string>(type: "text", nullable: true),
                    DiscussionRescanCursor = table.Column<string>(type: "text", nullable: true),
                    LastFullRescanTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastFullRescanStartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastForceRescanStartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRepositoryMetadataUpdate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastIssuesUpdate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastIssueCommentsUpdate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastPullRequestReviewCommentsUpdate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "labels",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: true),
                    Color = table.Column<string>(type: "text", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    RepositoryId = table.Column<long>(type: "bigint", nullable: false)
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
                name: "milestones",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: true),
                    Title = table.Column<string>(type: "text", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    OpenIssueCount = table.Column<int>(type: "integer", nullable: false),
                    ClosedIssueCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DueOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RepositoryId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_milestones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_milestones_repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "issues",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    HtmlUrl = table.Column<string>(type: "text", nullable: true),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: true),
                    Body = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Locked = table.Column<bool>(type: "boolean", nullable: false),
                    ActiveLockReason = table.Column<int>(type: "integer", nullable: true),
                    AuthorAssociation = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    RepositoryId = table.Column<long>(type: "bigint", nullable: false),
                    MilestoneId = table.Column<string>(type: "text", nullable: true),
                    IssueType = table.Column<int>(type: "integer", nullable: false),
                    Plus1 = table.Column<int>(type: "integer", nullable: false),
                    Minus1 = table.Column<int>(type: "integer", nullable: false),
                    Laugh = table.Column<int>(type: "integer", nullable: false),
                    Confused = table.Column<int>(type: "integer", nullable: false),
                    Heart = table.Column<int>(type: "integer", nullable: false),
                    Hooray = table.Column<int>(type: "integer", nullable: false),
                    Eyes = table.Column<int>(type: "integer", nullable: false),
                    Rocket = table.Column<int>(type: "integer", nullable: false),
                    LastSemanticIngestionTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastObservedDuringFullRescanTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_issues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_issues_milestones_MilestoneId",
                        column: x => x.MilestoneId,
                        principalTable: "milestones",
                        principalColumn: "Id");
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
                name: "comments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    HtmlUrl = table.Column<string>(type: "text", nullable: true),
                    Body = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AuthorAssociation = table.Column<int>(type: "integer", nullable: false),
                    IsMinimized = table.Column<bool>(type: "boolean", nullable: false),
                    MinimizedReason = table.Column<string>(type: "text", nullable: true),
                    IsPrReviewComment = table.Column<bool>(type: "boolean", nullable: false),
                    GitHubIdentifier = table.Column<long>(type: "bigint", nullable: false),
                    IssueId = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Plus1 = table.Column<int>(type: "integer", nullable: false),
                    Minus1 = table.Column<int>(type: "integer", nullable: false),
                    Laugh = table.Column<int>(type: "integer", nullable: false),
                    Confused = table.Column<int>(type: "integer", nullable: false),
                    Heart = table.Column<int>(type: "integer", nullable: false),
                    Hooray = table.Column<int>(type: "integer", nullable: false),
                    Eyes = table.Column<int>(type: "integer", nullable: false),
                    Rocket = table.Column<int>(type: "integer", nullable: false),
                    LastObservedDuringFullRescanTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "IssueInfoLabelInfo",
                columns: table => new
                {
                    IssuesId = table.Column<string>(type: "text", nullable: false),
                    LabelsId = table.Column<string>(type: "text", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "IssueInfoUserInfo",
                columns: table => new
                {
                    AssignedIssuesId = table.Column<string>(type: "text", nullable: false),
                    AssigneesId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueInfoUserInfo", x => new { x.AssignedIssuesId, x.AssigneesId });
                    table.ForeignKey(
                        name: "FK_IssueInfoUserInfo_issues_AssignedIssuesId",
                        column: x => x.AssignedIssuesId,
                        principalTable: "issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IssueInfoUserInfo_users_AssigneesId",
                        column: x => x.AssigneesId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pullrequests",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    MergedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDraft = table.Column<bool>(type: "boolean", nullable: false),
                    Mergeable = table.Column<int>(type: "integer", nullable: true),
                    Additions = table.Column<int>(type: "integer", nullable: false),
                    Deletions = table.Column<int>(type: "integer", nullable: false),
                    ChangedFiles = table.Column<int>(type: "integer", nullable: false),
                    MaintainerCanModify = table.Column<bool>(type: "boolean", nullable: false),
                    IssueId = table.Column<string>(type: "text", nullable: true),
                    MergedById = table.Column<long>(type: "bigint", nullable: true)
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
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: true),
                    TriageReportIssueNumber = table.Column<int>(type: "integer", nullable: false),
                    IssueId = table.Column<string>(type: "text", nullable: true)
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
                name: "IX_ingested_embedding_records_IssueId",
                table: "ingested_embedding_records",
                column: "IssueId");

            migrationBuilder.CreateIndex(
                name: "IX_ingested_embedding_records_RepositoryId",
                table: "ingested_embedding_records",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueInfoLabelInfo_LabelsId",
                table: "IssueInfoLabelInfo",
                column: "LabelsId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueInfoUserInfo_AssigneesId",
                table: "IssueInfoUserInfo",
                column: "AssigneesId");

            migrationBuilder.CreateIndex(
                name: "IX_issues_CreatedAt",
                table: "issues",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_issues_MilestoneId",
                table: "issues",
                column: "MilestoneId");

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
                name: "IX_milestones_RepositoryId",
                table: "milestones",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_milestones_Title",
                table: "milestones",
                column: "Title");

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

            migrationBuilder.CreateIndex(
                name: "IX_triaged_issues_IssueId",
                table: "triaged_issues",
                column: "IssueId");

            migrationBuilder.CreateIndex(
                name: "IX_users_NodeIdentifier",
                table: "users",
                column: "NodeIdentifier");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "body_edit_history");

            migrationBuilder.DropTable(
                name: "comments");

            migrationBuilder.DropTable(
                name: "ingested_embedding_records");

            migrationBuilder.DropTable(
                name: "IssueInfoLabelInfo");

            migrationBuilder.DropTable(
                name: "IssueInfoUserInfo");

            migrationBuilder.DropTable(
                name: "pullrequests");

            migrationBuilder.DropTable(
                name: "semantic_ingestion_backlog");

            migrationBuilder.DropTable(
                name: "text_entries");

            migrationBuilder.DropTable(
                name: "triaged_issues");

            migrationBuilder.DropTable(
                name: "labels");

            migrationBuilder.DropTable(
                name: "issues");

            migrationBuilder.DropTable(
                name: "milestones");

            migrationBuilder.DropTable(
                name: "repositories");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
