using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paparr.API.Migrations
{
    /// <inheritdoc />
    public partial class FolderProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CollectionSegment",
                table: "ImportJobs",
                type: "character varying(70)",
                maxLength: 70,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilenameOrder",
                table: "ImportJobs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "TitleAuthor");

            migrationBuilder.AddColumn<string>(
                name: "ProfileIngestSubfolder",
                table: "ImportJobs",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProfileName",
                table: "ImportJobs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CollectionSegment",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "FilenameOrder",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "ProfileIngestSubfolder",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "ProfileName",
                table: "ImportJobs");
        }
    }
}
