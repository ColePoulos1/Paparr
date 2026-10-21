using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Paparr.API.Migrations
{
    /// <inheritdoc />
    public partial class DuplicateSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DuplicateSources",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ImportJobId = table.Column<long>(type: "bigint", nullable: false),
                    FilePath = table.Column<string>(type: "text", nullable: false),
                    PathKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DiscoveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DuplicateSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DuplicateSources_ImportJobs_ImportJobId",
                        column: x => x.ImportJobId,
                        principalTable: "ImportJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateSources_ImportJobId_DiscoveredAt_Id",
                table: "DuplicateSources",
                columns: new[] { "ImportJobId", "DiscoveredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateSources_ImportJobId_PathKey",
                table: "DuplicateSources",
                columns: new[] { "ImportJobId", "PathKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DuplicateSources");
        }
    }
}
