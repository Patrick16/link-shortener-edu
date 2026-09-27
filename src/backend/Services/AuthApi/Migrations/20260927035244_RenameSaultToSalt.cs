using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthApi.Migrations
{
    /// <inheritdoc />
    public partial class RenameSaultToSalt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Sault",
                table: "users",
                newName: "Salt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Salt",
                table: "users",
                newName: "Sault");
        }
    }
}
