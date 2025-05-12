using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MihuBot.Migrations.GitHubDb
{
    /// <inheritdoc />
    public partial class AddMorePrInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Additions",
                table: "pullrequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ChangedFiles",
                table: "pullrequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Commits",
                table: "pullrequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Deletions",
                table: "pullrequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "Draft",
                table: "pullrequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "MaintainerCanModify",
                table: "pullrequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeCommitSha",
                table: "pullrequests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Mergeable",
                table: "pullrequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MergeableState",
                table: "pullrequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MergedAt",
                table: "pullrequests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MergedById",
                table: "pullrequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NodeId",
                table: "pullrequests",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Additions",
                table: "pullrequests");

            migrationBuilder.DropColumn(
                name: "ChangedFiles",
                table: "pullrequests");

            migrationBuilder.DropColumn(
                name: "Commits",
                table: "pullrequests");

            migrationBuilder.DropColumn(
                name: "Deletions",
                table: "pullrequests");

            migrationBuilder.DropColumn(
                name: "Draft",
                table: "pullrequests");

            migrationBuilder.DropColumn(
                name: "MaintainerCanModify",
                table: "pullrequests");

            migrationBuilder.DropColumn(
                name: "MergeCommitSha",
                table: "pullrequests");

            migrationBuilder.DropColumn(
                name: "Mergeable",
                table: "pullrequests");

            migrationBuilder.DropColumn(
                name: "MergeableState",
                table: "pullrequests");

            migrationBuilder.DropColumn(
                name: "MergedAt",
                table: "pullrequests");

            migrationBuilder.DropColumn(
                name: "MergedById",
                table: "pullrequests");

            migrationBuilder.DropColumn(
                name: "NodeId",
                table: "pullrequests");
        }
    }
}
