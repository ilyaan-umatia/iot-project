using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeGateway.Gateway.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddBrokerHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BrokerHistoryId",
                table: "Outbox",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BrokerHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DisconnectedAt = table.Column<string>(type: "TEXT", nullable: false),
                    ReconnectedAt = table.Column<string>(type: "TEXT", nullable: true),
                    DowntimeSeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    MessagesBuffered = table.Column<long>(type: "INTEGER", nullable: true),
                    BufferedMessagesSent = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrokerHistory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_BrokerHistoryId",
                table: "Outbox",
                column: "BrokerHistoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_Outbox_BrokerHistory_BrokerHistoryId",
                table: "Outbox",
                column: "BrokerHistoryId",
                principalTable: "BrokerHistory",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite's generated rollback drops the parent before rebuilding its child.
            // Rebuild the child first so populated foreign keys remain valid throughout rollback.
            migrationBuilder.Sql("""
                CREATE TABLE OutboxWithoutHistory (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Kind TEXT NOT NULL, DeviceId TEXT NOT NULL, DeviceSeq INTEGER,
                    Reading TEXT, CreatedAt INTEGER NOT NULL, SentAt INTEGER
                );
                INSERT INTO OutboxWithoutHistory (Id, Kind, DeviceId, DeviceSeq, Reading, CreatedAt, SentAt)
                    SELECT Id, Kind, DeviceId, DeviceSeq, Reading, CreatedAt, SentAt FROM Outbox;
                DROP TABLE Outbox;
                ALTER TABLE OutboxWithoutHistory RENAME TO Outbox;
                CREATE UNIQUE INDEX IX_Outbox_DeviceId_DeviceSeq ON Outbox (DeviceId, DeviceSeq);
                CREATE INDEX IX_Outbox_Unsent ON Outbox (Id) WHERE SentAt IS NULL;
                DROP TABLE BrokerHistory;
                """);
        }
    }
}
