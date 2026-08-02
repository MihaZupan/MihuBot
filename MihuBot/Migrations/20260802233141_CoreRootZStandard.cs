using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MihuBot.Migrations
{
    /// <inheritdoc />
    public partial class CoreRootZStandard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CommitTime",
                table: "coreRoot",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "PrefixBlobName",
                table: "coreRoot",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommitTime",
                table: "coreRoot");

            migrationBuilder.DropColumn(
                name: "PrefixBlobName",
                table: "coreRoot");
        }
    }
}
