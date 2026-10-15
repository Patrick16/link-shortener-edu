using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShortenerService.Migrations
{
    /// <inheritdoc />
    public partial class AddLinksHasCustomAlias : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasCustomAlias",
                table: "links",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasCustomAlias",
                table: "links");
        }
    }
}
