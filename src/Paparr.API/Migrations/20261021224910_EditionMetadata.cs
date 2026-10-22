using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paparr.API.Migrations
{
    /// <inheritdoc />
    public partial class EditionMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Isbn13",
                table: "MetadataCandidates",
                type: "character varying(13)",
                maxLength: 13,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IsbnStatus",
                table: "MetadataCandidates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NotProvided");

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "MetadataCandidates",
                type: "character varying(35)",
                maxLength: 35,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PublicationYear",
                table: "MetadataCandidates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Publisher",
                table: "MetadataCandidates",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Isbn13",
                table: "Books",
                type: "character varying(13)",
                maxLength: 13,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IsbnStatus",
                table: "Books",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NotProvided");

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "Books",
                type: "character varying(35)",
                maxLength: 35,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PublicationYear",
                table: "Books",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Publisher",
                table: "Books",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Isbn13",
                table: "MetadataCandidates");

            migrationBuilder.DropColumn(
                name: "IsbnStatus",
                table: "MetadataCandidates");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "MetadataCandidates");

            migrationBuilder.DropColumn(
                name: "PublicationYear",
                table: "MetadataCandidates");

            migrationBuilder.DropColumn(
                name: "Publisher",
                table: "MetadataCandidates");

            migrationBuilder.DropColumn(
                name: "Isbn13",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "IsbnStatus",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "PublicationYear",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "Publisher",
                table: "Books");
        }
    }
}
