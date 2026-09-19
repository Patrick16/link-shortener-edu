using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShortenerService.Migrations
{
    /// <inheritdoc />
    public partial class AddLinksUserIdCreatedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_links_UserId_CreatedAt",
                table: "links",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_links_UserId_CreatedAt",
                table: "links");
        }
    }
}
