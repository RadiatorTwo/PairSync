using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PairSync.Storage.Migrations
{
    /// <inheritdoc />
    public partial class JobTitles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "Jobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "History",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Title",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "History");
        }
    }
}
