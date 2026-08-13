using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenImageReviewer.Migrations
{
    /// <inheritdoc />
    public partial class RemoveStepsAddQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Steps",
                table: "ComparisonRuns");

            migrationBuilder.AddColumn<string>(
                name: "Quality",
                table: "ComparisonRuns",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Quality",
                table: "ComparisonRuns");

            migrationBuilder.AddColumn<int>(
                name: "Steps",
                table: "ComparisonRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }
    }
}
