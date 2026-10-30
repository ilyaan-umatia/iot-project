using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeGateway.Gateway.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitializeGatewayRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    IntakeOnline = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    PublishedOnline = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    PublishedReading = table.Column<string>(type: "TEXT", nullable: true),
                    MetricSignature = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.DeviceId);
                });

            migrationBuilder.CreateTable(
                name: "Outbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceSeq = table.Column<long>(type: "INTEGER", nullable: true),
                    Reading = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SentAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_DeviceId_DeviceSeq",
                table: "Outbox",
                columns: new[] { "DeviceId", "DeviceSeq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Unsent",
                table: "Outbox",
                column: "Id",
                filter: "SentAt IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "Outbox");
        }
    }
}
