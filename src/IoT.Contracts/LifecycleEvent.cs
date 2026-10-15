namespace IoT.Contracts;

public sealed record LifecycleEvent(
    string EventId, string MessageType, string GroupId, string GatewayId,
    string? DeviceId, string SourceTopic, DateTimeOffset ObservedAt,
    DateTimeOffset? RecordedAt, ulong? Seq, ulong? BdSeq,
    Dictionary<string, MetricValue> Metrics, bool Historical = false);

public sealed record MetricValue(double? Number = null, bool? Boolean = null);
