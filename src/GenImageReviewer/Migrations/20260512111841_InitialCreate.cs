using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenImageReviewer.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ComparisonRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    NegativePrompt = table.Column<string>(type: "TEXT", nullable: true),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    Steps = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComparisonRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ComparisonResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderName = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    ImageData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    MimeType = table.Column<string>(type: "TEXT", nullable: false),
                    GenerationSuccess = table.Column<bool>(type: "INTEGER", nullable: false),
                    GenerationError = table.Column<string>(type: "TEXT", nullable: true),
                    GenerationDurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ReviewCompleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    AccuracyScore = table.Column<int>(type: "INTEGER", nullable: true),
                    QualityScore = table.Column<int>(type: "INTEGER", nullable: true),
                    OverallScore = table.Column<int>(type: "INTEGER", nullable: true),
                    DefectsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Commentary = table.Column<string>(type: "TEXT", nullable: true),
                    ReviewError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComparisonResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComparisonResults_ComparisonRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "ComparisonRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComparisonResults_RunId",
                table: "ComparisonResults",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComparisonResults");

            migrationBuilder.DropTable(
                name: "ComparisonRuns");
        }
    }
}
