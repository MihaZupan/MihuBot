using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MihuBot.Migrations
{
    /// <inheritdoc />
    public partial class ShortUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "urlShortener",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreationSource = table.Column<string>(type: "TEXT", nullable: true),
                    OriginalUrl = table.Column<string>(type: "TEXT", nullable: true),
                    FetchCount = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_urlShortener", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "urlShortener");
        }
    }
}
