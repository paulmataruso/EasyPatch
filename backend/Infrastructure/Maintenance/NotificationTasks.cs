using EasyPatch.Infrastructure.Notifications;

namespace EasyPatch.Infrastructure.Maintenance;

/// <summary>Opens, refreshes and resolves alerts and queues their notifications.</summary>
public sealed class AlertEvaluationTask(AlertEngine engine) : IMaintenanceTask
{
    public string Name => "alerts";
    public long LockKey => 0x4550_0009;
    public TimeSpan Interval => TimeSpan.FromMinutes(2);

    public Task RunOnceAsync(CancellationToken ct) => engine.EvaluateAsync(ct);
}

/// <summary>Sends queued email and webhook notifications (time-boxed to 25 seconds per run).</summary>
public sealed class NotificationDeliveryTask(NotificationDispatcher dispatcher) : IMaintenanceTask
{
    public string Name => "notifications";
    public long LockKey => 0x4550_000A;
    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    public Task RunOnceAsync(CancellationToken ct) => dispatcher.DeliverPendingAsync(ct);
}
