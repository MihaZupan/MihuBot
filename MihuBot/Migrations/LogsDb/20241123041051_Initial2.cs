using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MihuBot.Migrations.LogsDb
{
    /// <inheritdoc />
    public partial class Initial2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "logs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Snowflake = table.Column<long>(type: "INTEGER", nullable: false),
                    GuildId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: true),
                    ExtraContentJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_logs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_logs_Snowflake",
                table: "logs",
                column: "Snowflake");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "logs");
        }
    }
}
