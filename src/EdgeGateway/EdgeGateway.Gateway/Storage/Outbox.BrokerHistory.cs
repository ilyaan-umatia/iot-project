using System.Globalization;

namespace EdgeGateway.Gateway.Storage;

public sealed partial class Outbox
{
    // Only this running gateway's observed outage is active. Never resume an old NULL reconnect on startup.
    private long? _activeBrokerHistoryId;

    public void RecordBrokerDisconnected(DateTimeOffset observedAt)
    {
        lock (_gate)
        {
            if (_activeBrokerHistoryId.HasValue) return;
            _activeBrokerHistoryId = (long)Scalar("""
                INSERT INTO BrokerHistory (DisconnectedAt) VALUES ($time) RETURNING Id
                """, null, ("$time", observedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)))!;
        }
    }

    public void RecordBrokerConnected(DateTimeOffset observedAt)
    {
        lock (_gate)
        {
            if (_activeBrokerHistoryId is not { } id) return;
            // Even if recording fails, new live messages must not be linked to the old outage.
            _activeBrokerHistoryId = null;
            using var transaction = _connection.BeginTransaction();
            var disconnectedAt = DateTimeOffset.Parse((string)Scalar(
                "SELECT DisconnectedAt FROM BrokerHistory WHERE Id = $id", transaction, ("$id", id))!,
                CultureInfo.InvariantCulture);
            Execute("""
                UPDATE BrokerHistory SET ReconnectedAt = $time, DowntimeSeconds = $seconds,
                    MessagesBuffered = (SELECT COUNT(*) FROM Outbox WHERE BrokerHistoryId = $id)
                WHERE Id = $id
                """, transaction,
                ("$time", observedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
                ("$seconds", Math.Max(0L, (long)(observedAt - disconnectedAt).TotalSeconds)), ("$id", id));
            transaction.Commit();
        }
    }

    public void EndBrokerObservation()
    {
        // NULL means "reconnection not recorded", not necessarily "broker still down".
        lock (_gate) _activeBrokerHistoryId = null;
    }
}
