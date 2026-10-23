using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paparr.API.Migrations
{
    /// <inheritdoc />
    public partial class RetainedSourceUndo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CurrentUndoId",
                table: "ImportJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ImportUndos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportJobId = table.Column<long>(type: "bigint", nullable: false),
                    BookId = table.Column<long>(type: "bigint", nullable: false),
                    SourcePath = table.Column<string>(type: "text", nullable: false),
                    DestinationPath = table.Column<string>(type: "text", nullable: false),
                    FileHash = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    RetryAfter = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportUndos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportUndos_ImportJobs_ImportJobId",
                        column: x => x.ImportJobId,
                        principalTable: "ImportJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportUndos_ImportJobId_CreatedAt",
                table: "ImportUndos",
                columns: new[] { "ImportJobId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportUndos_Status_RetryAfter",
                table: "ImportUndos",
                columns: new[] { "Status", "RetryAfter" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportUndos");

            migrationBuilder.DropColumn(
                name: "CurrentUndoId",
                table: "ImportJobs");
        }
    }
}
