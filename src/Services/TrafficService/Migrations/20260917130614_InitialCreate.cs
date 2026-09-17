using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrafficService.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "traffic-service");

            migrationBuilder.CreateTable(
                name: "clicks",
                schema: "traffic-service",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClickedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InboundLink = table.Column<string>(type: "text", nullable: false),
                    OutboundLink = table.Column<string>(type: "text", nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_clicks", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clicks",
                schema: "traffic-service");
        }
    }
}
