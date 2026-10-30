using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EdgeGateway.Configuration.Storage;

// Owns Configuration schema only. DeviceStore keeps its existing transactional SQL.
public sealed class ConfigurationDbContext(DbContextOptions<ConfigurationDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<StoredInterface>(entity =>
        {
            entity.ToTable("Interfaces", table => table.HasCheckConstraint("CK_Interfaces_Settings_Json", "json_valid(Settings)"));
            entity.HasKey(x => x.Id);
        });
        model.Entity<StoredConfigurationDevice>(entity =>
        {
            entity.ToTable("Devices", table => table.HasCheckConstraint("CK_Devices_Data_Json", "json_valid(Data)"));
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.InterfaceId);
            entity.HasOne<StoredInterface>().WithMany().HasForeignKey(x => x.InterfaceId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ConfigurationMetadata>(entity =>
        {
            entity.ToTable("Meta");
            entity.HasKey(x => x.Key);
            entity.HasData(new ConfigurationMetadata { Key = "Version", Value = 0 });
        });
    }
}

public sealed class StoredInterface
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Settings { get; set; } = "";
}

public sealed class StoredConfigurationDevice
{
    public string Id { get; set; } = "";
    public string InterfaceId { get; set; } = "";
    public string Data { get; set; } = "";
}

public sealed class ConfigurationMetadata
{
    public string Key { get; set; } = "";
    public long Value { get; set; }
}

// Tooling uses an explicit DB path after -- and never starts the Configuration API.
public sealed class ConfigurationDbContextFactory : IDesignTimeDbContextFactory<ConfigurationDbContext>
{
    public ConfigurationDbContext CreateDbContext(string[] args)
    {
        var path = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "state", "configuration.db");
        var connection = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString();
        return new(new DbContextOptionsBuilder<ConfigurationDbContext>().UseSqlite(connection).Options);
    }
}
