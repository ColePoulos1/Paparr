using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paparr.API.Migrations
{
    /// <inheritdoc />
    public partial class DurableSourceCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CleanupAfter",
                table: "ImportJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CleanupAttemptCount",
                table: "ImportJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CleanupError",
                table: "ImportJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CleanupStatus",
                table: "ImportJobs",
                type: "text",
                nullable: false,
                defaultValue: "NotRequested");

            migrationBuilder.CreateIndex(
                name: "IX_ImportJobs_CleanupStatus_CleanupAfter",
                table: "ImportJobs",
                columns: new[] { "CleanupStatus", "CleanupAfter" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ImportJobs_CleanupStatus_CleanupAfter",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "CleanupAfter",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "CleanupAttemptCount",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "CleanupError",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "CleanupStatus",
                table: "ImportJobs");
        }
    }
}
