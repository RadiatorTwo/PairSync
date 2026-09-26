using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PairSync.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SyncProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SyncProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PeerDeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocalPath = table.Column<string>(type: "TEXT", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    Paused = table.Column<bool>(type: "INTEGER", nullable: false),
                    Excludes = table.Column<string>(type: "TEXT", nullable: false),
                    AllowRead = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowWrite = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowDelete = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    IndexId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LastSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    RemoteIndexId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RemoteSequenceSeen = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSyncUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Problem = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncActivities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    Files = table.Column<int>(type: "INTEGER", nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncActivities_SyncProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "SyncProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncConflicts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    CopyPath = table.Column<string>(type: "TEXT", nullable: false),
                    CopyIsLocal = table.Column<bool>(type: "INTEGER", nullable: false),
                    LocalSize = table.Column<long>(type: "INTEGER", nullable: false),
                    RemoteSize = table.Column<long>(type: "INTEGER", nullable: false),
                    LocalSha256 = table.Column<byte[]>(type: "BLOB", nullable: true),
                    RemoteSha256 = table.Column<byte[]>(type: "BLOB", nullable: true),
                    LocalMTimeUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    RemoteMTimeUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DetectedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Resolved = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncConflicts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncConflicts_SyncProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "SyncProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncFiles",
                columns: table => new
                {
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Side = table.Column<int>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    IsDirectory = table.Column<bool>(type: "INTEGER", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ChunkHashes = table.Column<byte[]>(type: "BLOB", nullable: true),
                    MTimeUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    DeletedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncFiles", x => new { x.ProfileId, x.Side, x.Path });
                    table.ForeignKey(
                        name: "FK_SyncFiles_SyncProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "SyncProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncActivities_ProfileId_Id",
                table: "SyncActivities",
                columns: new[] { "ProfileId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflicts_ProfileId_Resolved",
                table: "SyncConflicts",
                columns: new[] { "ProfileId", "Resolved" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncFiles_ProfileId_Side_Sequence",
                table: "SyncFiles",
                columns: new[] { "ProfileId", "Side", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncProfiles_PeerDeviceId",
                table: "SyncProfiles",
                column: "PeerDeviceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncActivities");

            migrationBuilder.DropTable(
                name: "SyncConflicts");

            migrationBuilder.DropTable(
                name: "SyncFiles");

            migrationBuilder.DropTable(
                name: "SyncProfiles");
        }
    }
}
