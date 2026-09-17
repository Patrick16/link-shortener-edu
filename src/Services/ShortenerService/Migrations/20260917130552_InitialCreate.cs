using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShortenerService.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "shortener-service");

            migrationBuilder.CreateTable(
                name: "links",
                schema: "shortener-service",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "text", nullable: false),
                    OriginalLink = table.Column<string>(type: "text", nullable: false),
                    ShortenLink = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_links", x => x.Hash);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "links",
                schema: "shortener-service");
        }
    }
}
