namespace IoT.Contracts;

// Shared between the configuration service (which stores it) and data acquisition (which polls by it).
public sealed record DeviceDefinition(
    string Id, bool Enabled, string Protocol, string Host, int Port, byte UnitId,
    int PollIntervalSeconds, int? GpioPin, string Description, MetricDefinition[] Metrics)
{
    public const string ModbusTcp = "ModbusTcp";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.IndexOfAny(['/', '+', '#', ' ']) >= 0)
            throw new ArgumentException("Device ID must be nonempty and must not contain /, +, # or spaces.");
        if (Protocol != ModbusTcp) throw new ArgumentException($"Protocol must be {ModbusTcp}; Modbus RTU comes later.");
        if (string.IsNullOrWhiteSpace(Host)) throw new ArgumentException("Host is required.");
        if (Port is < 1 or > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
        if (PollIntervalSeconds is < 1 or > 3600) throw new ArgumentException("Poll interval must be 1–3600 seconds.");
        if (GpioPin is < 0) throw new ArgumentException("GPIO pin must not be negative.");
        if (Metrics is null || Metrics.Length == 0) throw new ArgumentException("At least one metric is required.");
        if (Metrics.Select(metric => metric.Name).Distinct(StringComparer.Ordinal).Count() != Metrics.Length)
            throw new ArgumentException("Metric names must be unique.");
        foreach (var metric in Metrics) metric.Validate();
    }
}

// Where a value lives on the device and how to turn the raw register(s) into an engineering value:
// value = decoded(DataType, WordOrder) × Scale. 32-bit types span two consecutive registers.
public sealed record MetricDefinition(string Name, string Register, ushort Address, double Scale, string Unit,
    string? DataType = null, string? WordOrder = null)
{
    public static readonly string[] Registers = ["holding", "input", "coil", "discrete"];
    public static readonly string[] RegisterTypes = ["int16", "uint16", "int32", "uint32", "float32"];
    public static readonly string[] WordOrders = ["big", "little"];

    public bool IsBit => Register is "coil" or "discrete";
    // Defaults keep older definitions working: a register is a signed 16-bit value, a coil or input is a bit.
    public string Type => DataType ?? (IsBit ? "bool" : "int16");
    // "big": the first register holds the high word (Modbus convention). "little": word-swapped devices.
    public string Order => WordOrder ?? "big";
    public int Width => Type is "int32" or "uint32" or "float32" ? 2 : 1;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name is "bdSeq" or "DeviceSeq" or "GpioPin")
            throw new ArgumentException($"Invalid metric name '{Name}'.");
        if (!Registers.Contains(Register))
            throw new ArgumentException($"Metric {Name}: register must be one of {string.Join(", ", Registers)}.");
        if (IsBit ? Type != "bool" : !RegisterTypes.Contains(Type))
            throw new ArgumentException(IsBit
                ? $"Metric {Name}: a {Register} holds a single bit, so its data type must be bool."
                : $"Metric {Name}: data type must be one of {string.Join(", ", RegisterTypes)}.");
        if (!WordOrders.Contains(Order)) throw new ArgumentException($"Metric {Name}: word order must be big or little.");
        if (Address + Width - 1 > ushort.MaxValue) throw new ArgumentException($"Metric {Name}: address out of range.");
        if (!double.IsFinite(Scale) || Scale == 0) throw new ArgumentException($"Metric {Name}: scale must be a nonzero number.");
    }
}

// What data acquisition fetches: every enabled device, with a version that changes on every edit.
public sealed record GatewayConfiguration(long Version, DeviceDefinition[] Devices);
