using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenImageReviewer.Migrations
{
    /// <inheritdoc />
    public partial class AddRoundAndReferenceImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "ReferenceImageData",
                table: "ComparisonRuns",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferenceImageMimeType",
                table: "ComparisonRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReferenceAccuracyScore",
                table: "ComparisonResults",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Round",
                table: "ComparisonResults",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReferenceImageData",
                table: "ComparisonRuns");

            migrationBuilder.DropColumn(
                name: "ReferenceImageMimeType",
                table: "ComparisonRuns");

            migrationBuilder.DropColumn(
                name: "ReferenceAccuracyScore",
                table: "ComparisonResults");

            migrationBuilder.DropColumn(
                name: "Round",
                table: "ComparisonResults");
        }
    }
}
