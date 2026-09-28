using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PairSync.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ContentDefinedChunks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ChunkHashes",
                table: "SyncFiles",
                newName: "Chunks");

            // Fixed 4 MiB chunk hashes do not fit the new format; the next scan hashes these files once more.
            migrationBuilder.Sql("UPDATE SyncFiles SET Chunks = NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE SyncFiles SET Chunks = NULL");

            migrationBuilder.RenameColumn(
                name: "Chunks",
                table: "SyncFiles",
                newName: "ChunkHashes");
        }
    }
}
