using IoT.Contracts;
using Application.Api.Models;
using Microsoft.Data.Sqlite;

namespace Application.Api.Services;

public sealed class GpioMessageService(TelemetryStore store)
{
    private readonly object _gate = new();

    public bool Process(LifecycleEvent item)
    {
        Validate(item);
        lock (_gate)
        {
            using var transaction = store.BeginTransaction();
            if (store.ContainsEvent(item.EventId, transaction)) return true;
            var applied = Apply(item, transaction);
            store.SaveEvent(item, applied, transaction);
            transaction.Commit();
            return applied;
        }
    }

    private bool Apply(LifecycleEvent item, SqliteTransaction transaction)
    {
        var gatewayKey = item.GroupId + "/" + item.GatewayId;
        var gateway = store.GetGateway(gatewayKey, transaction);
        if (item.MessageType.StartsWith('N'))
        {
            if (gateway is not null && item.ObservedAt < gateway.UpdatedAt) return false;
            if (item.MessageType == "NBIRTH")
            {
                store.Save(new GatewayStatus(item.GroupId, item.GatewayId, true, item.BdSeq,
                    item.Metrics.GetValueOrDefault("UptimeSeconds")?.Number, item.ObservedAt), transaction);
                MarkDevicesOffline(item, transaction);
            }
            else if (item.MessageType == "NDEATH")
            {
                if (gateway is null || gateway.BdSeq != item.BdSeq) return false;
                store.Save(gateway with { Online = false, UpdatedAt = item.ObservedAt }, transaction);
                MarkDevicesOffline(item, transaction);
            }
            else
            {
                if (gateway is null || !gateway.Online) return false;
                store.Save(gateway with
                {
                    UptimeSeconds = item.Metrics.GetValueOrDefault("UptimeSeconds")?.Number ?? gateway.UptimeSeconds,
                    UpdatedAt = item.ObservedAt
                }, transaction);
            }
            return true;
        }

        if (gateway is null || !gateway.Online) return false;
        var device = store.GetDevice(gatewayKey + "/" + item.DeviceId, transaction);
        if (device is not null && item.ObservedAt < device.UpdatedAt) return false;
        if (item.MessageType == "DDEATH")
        {
            if (device is null) return false;
            store.Save(device with { Online = false, UpdatedAt = item.ObservedAt }, transaction);
            return true;
        }
        if (item.MessageType == "DDATA" && (device is null || !device.Online)) return false;
        if (item.MessageType == "DDATA" && item.RecordedAt < device!.RecordedAt) return false;
        var temperature = item.Metrics.GetValueOrDefault("Temperature")?.Number ?? device?.Temperature;
        var humidity = item.Metrics.GetValueOrDefault("Humidity")?.Number ?? device?.Humidity;
        var gpioPin = item.Metrics.GetValueOrDefault("GpioPin")?.Number ?? device?.GpioPin;
        var gpioValue = item.Metrics.GetValueOrDefault("GpioValue")?.Number ?? device?.GpioValue;
        if (temperature is null || humidity is null || gpioPin is null || gpioValue is null)
            throw new ArgumentException("Missing fridge metrics.");
        // This simulated door sensor is active-high: 1 means open, 0 means closed.
        store.Save(new DeviceStatus(item.GroupId, item.GatewayId, item.DeviceId!, true,
            temperature.Value, humidity.Value, (int)gpioPin.Value, (int)gpioValue.Value,
            gpioValue == 1, temperature > 8, item.RecordedAt, item.ObservedAt), transaction);
        return true;
    }

    public static void Validate(LifecycleEvent item)
    {
        if (string.IsNullOrWhiteSpace(item.EventId) || string.IsNullOrWhiteSpace(item.GroupId) ||
            string.IsNullOrWhiteSpace(item.GatewayId) || item.Metrics is null || item.ObservedAt == default ||
            item.MessageType is not ("NBIRTH" or "NDATA" or "NDEATH" or "DBIRTH" or "DDATA" or "DDEATH"))
            throw new ArgumentException("Invalid lifecycle event.");
        if (item.MessageType.StartsWith('D') && string.IsNullOrWhiteSpace(item.DeviceId))
            throw new ArgumentException("Device ID required.");
        if (item.MessageType.StartsWith('N') && item.DeviceId is not null)
            throw new ArgumentException("Node messages must not contain a device ID.");
        if (item.MessageType != "NDEATH" && (item.Seq is null or > 255 || item.RecordedAt is null))
            throw new ArgumentException("Timestamp and sequence required.");
        if (item.MessageType == "NDEATH" && item.Seq is not null)
            throw new ArgumentException("NDEATH must not contain seq.");
        if ((item.MessageType is "NBIRTH" or "NDEATH") && item.BdSeq is null or > 255)
            throw new ArgumentException("bdSeq required.");
        var expectedTopic = $"spBv1.0/{item.GroupId}/{item.MessageType}/{item.GatewayId}" +
            (item.MessageType.StartsWith('D') ? $"/{item.DeviceId}" : "");
        if (item.SourceTopic != expectedTopic || item.GroupId.IndexOfAny(['/', '+', '#']) >= 0 ||
            item.GatewayId.IndexOfAny(['/', '+', '#']) >= 0 || item.DeviceId?.IndexOfAny(['/', '+', '#']) >= 0)
            throw new ArgumentException("Topic identity mismatch.");
        if (item.MessageType == "NBIRTH" && item.Seq != 0)
            throw new ArgumentException("NBIRTH must start at seq 0.");
        foreach (var (name, value) in item.Metrics)
        {
            if (string.IsNullOrWhiteSpace(name) || value is null ||
                value.Number.HasValue == value.Boolean.HasValue ||
                (value.Number is { } number && !double.IsFinite(number)))
                throw new ArgumentException("Invalid metric.");
            if ((name is "Temperature" or "Humidity" or "GpioPin" or "GpioValue" or "UptimeSeconds") && value.Number is null)
                throw new ArgumentException("Numeric metric required.");
            if (name == "UptimeSeconds" && value.Number < 0)
                throw new ArgumentException("Uptime must not be negative.");
            if (name == "Humidity" && value.Number is < 0 or > 100)
                throw new ArgumentException("Humidity must be between 0 and 100.");
            if (name == "GpioPin" && (value.Number is null || value.Number < 0 ||
                value.Number > int.MaxValue || value.Number != Math.Truncate(value.Number.Value)))
                throw new ArgumentException("GPIO pin must be a non-negative integer.");
            if (name == "GpioValue" && value.Number is not (0 or 1))
                throw new ArgumentException("GPIO value must be 0 or 1.");
        }
        if (item.MessageType == "DBIRTH" &&
            new[] { "Temperature", "Humidity", "GpioPin", "GpioValue" }.Any(name => !item.Metrics.ContainsKey(name)))
            throw new ArgumentException("DBIRTH must include all fridge metrics.");
    }

    private void MarkDevicesOffline(LifecycleEvent item, SqliteTransaction transaction)
    {
        foreach (var device in store.GetDevices(transaction))
            if (device.GroupId == item.GroupId && device.GatewayId == item.GatewayId)
                store.Save(device with { Online = false, UpdatedAt = item.ObservedAt }, transaction);
    }

    public GatewayStatus[] GetGateways() { lock (_gate) return store.GetGateways(); }
    public DeviceStatus[] GetDevices() { lock (_gate) return store.GetDevices(); }
    public DeviceStatus? GetDevice(string deviceId)
    {
        lock (_gate) return store.FindDevice(deviceId);
    }

    public HistoryEntry[] GetRecentEvents(int limit = 100)
    {
        if (limit is < 1 or > 500) throw new ArgumentException("Invalid event page.");
        lock (_gate) return store.GetRecentEvents(limit);
    }

    public DeviceSummary[] GetSummaries() { lock (_gate) return store.GetSummaries(); }

    public HistoryEntry[] GetHistory(string deviceId, int limit = 100, long? before = null)
    {
        if (limit is < 1 or > 500 || before is <= 0) throw new ArgumentException("Invalid history page.");
        lock (_gate) return store.GetHistory(deviceId, limit, before);
    }
}
