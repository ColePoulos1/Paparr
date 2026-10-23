using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Paparr.API.Migrations
{
    /// <inheritdoc />
    public partial class RecoverableImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Evidence",
                table: "MetadataCandidates",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "ImportJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DestinationPath",
                table: "ImportJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "ImportJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseExpiresAt",
                table: "ImportJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "Revision",
                table: "ImportJobs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<long>(
                name: "SelectedCandidateId",
                table: "ImportJobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ImportEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ImportJobId = table.Column<long>(type: "bigint", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportEvents_ImportJobs_ImportJobId",
                        column: x => x.ImportJobId,
                        principalTable: "ImportJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportJobs_FileHash",
                table: "ImportJobs",
                column: "FileHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportJobs_Status_CreatedAt",
                table: "ImportJobs",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportEvents_ImportJobId",
                table: "ImportEvents",
                column: "ImportJobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportEvents");

            migrationBuilder.DropIndex(
                name: "IX_ImportJobs_FileHash",
                table: "ImportJobs");

            migrationBuilder.DropIndex(
                name: "IX_ImportJobs_Status_CreatedAt",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "Evidence",
                table: "MetadataCandidates");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "DestinationPath",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "SelectedCandidateId",
                table: "ImportJobs");
        }
    }
}
