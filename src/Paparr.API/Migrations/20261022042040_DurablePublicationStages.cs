using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paparr.API.Migrations
{
    /// <inheritdoc />
    public partial class DurablePublicationStages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PublicationFence",
                table: "ImportJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PublicationStages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportJobId = table.Column<long>(type: "bigint", nullable: false),
                    PublicationFence = table.Column<Guid>(type: "uuid", nullable: false),
                    FilePath = table.Column<string>(type: "text", nullable: false),
                    DestinationPath = table.Column<string>(type: "text", nullable: false),
                    FileHash = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CleanupAttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CleanupAfter = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CleanupError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationStages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationStages_ImportJobs_ImportJobId",
                        column: x => x.ImportJobId,
                        principalTable: "ImportJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationStages_FilePath",
                table: "PublicationStages",
                column: "FilePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationStages_ImportJobId",
                table: "PublicationStages",
                column: "ImportJobId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationStages_Status_CleanupAfter",
                table: "PublicationStages",
                columns: new[] { "Status", "CleanupAfter" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicationStages");

            migrationBuilder.DropColumn(
                name: "PublicationFence",
                table: "ImportJobs");
        }
    }
}
