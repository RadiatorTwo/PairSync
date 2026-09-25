using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PairSync.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChunkJournal",
                columns: table => new
                {
                    TransferId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TempPath = table.Column<string>(type: "TEXT", nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    ChunkSize = table.Column<int>(type: "INTEGER", nullable: false),
                    ChunkCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastWriteTimeUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Confirmed = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChunkJournal", x => x.TransferId);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PairedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Trust = table.Column<int>(type: "INTEGER", nullable: false),
                    CanSendToMe = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "History",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PeerDeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PeerName = table.Column<string>(type: "TEXT", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    FileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_History", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PeerDeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Policy = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetPath = table.Column<string>(type: "TEXT", nullable: true),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    FileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    SourcePath = table.Column<string>(type: "TEXT", nullable: true),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    LastWriteTimeUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    TransferId = table.Column<Guid>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobItems_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_PublicKey",
                table: "Devices",
                column: "PublicKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_History_FinishedAtUtc",
                table: "History",
                column: "FinishedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JobItems_JobId_RelativePath",
                table: "JobItems",
                columns: new[] { "JobId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobItems_TransferId",
                table: "JobItems",
                column: "TransferId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_State_PeerDeviceId",
                table: "Jobs",
                columns: new[] { "State", "PeerDeviceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChunkJournal");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "History");

            migrationBuilder.DropTable(
                name: "JobItems");

            migrationBuilder.DropTable(
                name: "Jobs");
        }
    }
}
