using System.Text.Json;
using System.Text.Json.Serialization;

namespace IoT.Contracts;

public sealed record DeviceWithInterface(DeviceRegistration Device, ModbusInterface NewInterface);

// Settings are JSON at the API/storage boundary; polling receives a validated TCP definition.
public sealed record ModbusInterface(string Id, string Type, JsonElement Settings)
{
    public const string Tcp = "ModbusTcp", Rtu = "ModbusRtu";

    public void Validate()
    {
        DeviceRegistration.ValidateId(Id, "Interface ID");
        if (Settings.ValueKind != JsonValueKind.Object) throw new ArgumentException("Interface settings must be an object.");
        if (Type == Tcp) TcpSettings().Validate();
        else if (Type == Rtu) ReadSettings<SerialInterfaceSettings>().Validate();
        else throw new ArgumentException("Interface type must be ModbusTcp or ModbusRtu.");
    }

    public TcpInterfaceSettings TcpSettings() => ReadSettings<TcpInterfaceSettings>();

    private T ReadSettings<T>() where T : class
    {
        try { return Settings.Deserialize<T>(EdgeTopics.Json) ?? throw new ArgumentException("Interface settings are required."); }
        catch (JsonException error) { throw new ArgumentException($"Invalid {Type} settings: {error.Message}", error); }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TcpInterfaceSettings(string Host, int Port)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host)) throw new ArgumentException("Host is required.");
        if (Port is < 1 or > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SerialInterfaceSettings(string PortName, int BaudRate, int DataBits, string Parity, int StopBits)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PortName)) throw new ArgumentException("Serial port name is required.");
        if (BaudRate <= 0) throw new ArgumentException("Baud rate must be positive.");
        if (DataBits != 8) throw new ArgumentException("Modbus RTU requires 8 data bits.");
        if (Parity is not ("Even" or "Odd" or "None")) throw new ArgumentException("Parity must be Even, Odd or None.");
        if (StopBits is not (1 or 2)) throw new ArgumentException("Stop bits must be 1 or 2.");
    }
}
