using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Paparr.API.Migrations
{
    /// <inheritdoc />
    public partial class LibraryIntegrityScans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "IntegrityCheckedAt",
                table: "Books",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntegrityMessage",
                table: "Books",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntegrityStatus",
                table: "Books",
                type: "text",
                nullable: false,
                defaultValue: "Unchecked");

            migrationBuilder.CreateTable(
                name: "LibraryAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Status = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<Guid>(type: "uuid", nullable: false),
                    LastBookId = table.Column<long>(type: "bigint", nullable: false),
                    MaximumBookId = table.Column<long>(type: "bigint", nullable: false),
                    TotalBooks = table.Column<int>(type: "integer", nullable: false),
                    ProcessedBooks = table.Column<int>(type: "integer", nullable: false),
                    VerifiedBooks = table.Column<int>(type: "integer", nullable: false),
                    MissingBooks = table.Column<int>(type: "integer", nullable: false),
                    ChangedBooks = table.Column<int>(type: "integer", nullable: false),
                    UnavailableBooks = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryAudits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Books_IntegrityStatus",
                table: "Books",
                column: "IntegrityStatus");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryAudits_IsActive",
                table: "LibraryAudits",
                column: "IsActive",
                unique: true,
                filter: "\"IsActive\" = TRUE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LibraryAudits");

            migrationBuilder.DropIndex(
                name: "IX_Books_IntegrityStatus",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "IntegrityCheckedAt",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "IntegrityMessage",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "IntegrityStatus",
                table: "Books");
        }
    }
}
