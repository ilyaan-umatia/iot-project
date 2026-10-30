# Configuration database migrations

Configuration now owns a separate EF Core 8.0.31 context and migration set:

- `src/EdgeGateway/EdgeGateway.Configuration/Storage/ConfigurationDbContext.cs`
- `src/EdgeGateway/EdgeGateway.Configuration/Storage/Migrations/`
- Baseline: `InitialConfiguration`.

The context maps the existing `Interfaces`, `Devices` and `Meta` tables. Connection settings and device/metric definitions remain JSON/TEXT. The Devices-to-Interfaces foreign key, deletion restriction, JSON checks and InterfaceId index remain. API requests/responses, serial configuration-only rules, snapshot retrieval, atomic writes and configuration version increments still use the existing DeviceStore logic.

`DeviceStore` opens its own SQLite connection and calls `Database.Migrate()` on startup. New databases are created from the baseline; future pending migrations are applied once and recorded in that database's `__EFMigrationsHistory`. Gateway retains its separate context, migrations and database.

The baseline's Up SQL intentionally uses `CREATE TABLE IF NOT EXISTS` and `INSERT OR IGNORE` to adopt the current shared-interface pre-EF schema without rewriting data or resetting `Meta.Version`. This does not migrate the older device-only schema that has no InterfaceId; its existing explicit error remains. Do not assume arbitrary incompatible database schemas can be baselined this way.

The initial version row is 0 for fresh databases. A repeated service start or baseline adoption does not reset an existing version or trigger poller reconfiguration by itself. A future change inside the existing JSON structure generally does not need a database migration; changing a column/table/index/constraint does.

From the repository root:

```powershell
dotnet tool restore
dotnet ef migrations list --project src/EdgeGateway/EdgeGateway.Configuration --context ConfigurationDbContext
dotnet ef migrations has-pending-model-changes --project src/EdgeGateway/EdgeGateway.Configuration --context ConfigurationDbContext
# After editing the Configuration model, create and review the migration:
dotnet ef migrations add DescribeTheChange --project src/EdgeGateway/EdgeGateway.Configuration --context ConfigurationDbContext --output-dir Storage/Migrations
# Optional manual application instead of normal service startup; specify the actual DB:
dotnet ef database update --project src/EdgeGateway/EdgeGateway.Configuration --context ConfigurationDbContext -- "C:\path\to\configuration.db"
```

The design-time factory does not start the API or load app credentials. Always pass an explicit actual DB path after `--` when manually updating a database; otherwise the factory uses its design-time default. Do not point Configuration migrations at `outbox.db`, or Gateway migrations at `configuration.db`. Rolling the baseline back to zero intentionally drops configuration tables and data; it is not a backup.

For the existing local demo, open `logs/demo/state/configuration.db` in DB Browser. Interfaces, Devices and Meta should retain their existing records; `__EFMigrationsHistory` should contain InitialConfiguration once. No reset is needed for the supported shared-interface schema.

Checks:

```powershell
dotnet run --project tests/IoT.Checks -- --configuration-migrations-only
dotnet run --project tests/IoT.Checks
```

Tests cover fresh creation, in-place baseline adoption, exact JSON/config-version preservation, restart, FK/index/JSON constraints, rollback/reapply, model snapshot, failure rollback/retry and rejection of the unsupported older schema. Existing interface/polling regressions and real Configuration HTTP API checks also verify behaviour with the new baseline.
