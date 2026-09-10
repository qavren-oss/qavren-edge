using Microsoft.Extensions.Logging;

namespace Qavren.Edge;

/// <summary>Stable logging event ids. The numeric ranges mirror <see cref="EdgeErrorCode"/>.</summary>
public static class EdgeEventIds
{
    public static readonly EventId StartupBegan = new(100, nameof(StartupBegan));
    public static readonly EventId StartupTaskCompleted = new(101, nameof(StartupTaskCompleted));
    public static readonly EventId StartupFailed = new(102, nameof(StartupFailed));
    public static readonly EventId StartupCompleted = new(103, nameof(StartupCompleted));

    public static readonly EventId LifecycleRaised = new(200, nameof(LifecycleRaised));
    public static readonly EventId LifecycleObserverFailed = new(201, nameof(LifecycleObserverFailed));

    public static readonly EventId NativeProviderInstalled = new(300, nameof(NativeProviderInstalled));
    public static readonly EventId NativeProviderFailed = new(301, nameof(NativeProviderFailed));

    public static readonly EventId MigrationApplied = new(400, nameof(MigrationApplied));
    public static readonly EventId MigrationFailed = new(401, nameof(MigrationFailed));

    public static readonly EventId CheckpointFailed = new(500, nameof(CheckpointFailed));
}
