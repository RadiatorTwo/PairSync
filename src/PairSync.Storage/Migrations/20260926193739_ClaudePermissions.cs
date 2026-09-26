using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PairSync.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ClaudePermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanApplyClaudeConfig",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanInstallPrograms",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CanApplyClaudeConfig",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "CanInstallPrograms",
                table: "Devices");
        }
    }
}
