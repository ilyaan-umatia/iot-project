using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeGateway.Configuration.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Baseline also adopts the current pre-EF shared-interface schema in place.
            // IF NOT EXISTS / OR IGNORE retain registered devices and the current config version.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS Interfaces (
                    Id TEXT NOT NULL PRIMARY KEY, Type TEXT NOT NULL,
                    Settings TEXT NOT NULL CONSTRAINT CK_Interfaces_Settings_Json CHECK(json_valid(Settings))
                );
                CREATE TABLE IF NOT EXISTS Devices (
                    Id TEXT NOT NULL PRIMARY KEY,
                    InterfaceId TEXT NOT NULL REFERENCES Interfaces(Id) ON DELETE RESTRICT,
                    Data TEXT NOT NULL CONSTRAINT CK_Devices_Data_Json CHECK(json_valid(Data))
                );
                CREATE INDEX IF NOT EXISTS IX_Devices_InterfaceId ON Devices(InterfaceId);
                CREATE TABLE IF NOT EXISTS Meta (Key TEXT NOT NULL PRIMARY KEY, Value INTEGER NOT NULL);
                INSERT OR IGNORE INTO Meta (Key, Value) VALUES ('Version', 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "Meta");

            migrationBuilder.DropTable(
                name: "Interfaces");
        }
    }
}
