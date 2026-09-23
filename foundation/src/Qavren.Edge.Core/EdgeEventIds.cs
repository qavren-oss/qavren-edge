using Microsoft.Extensions.Logging;

namespace Qavren.Edge;

/// <summary>Stable logging event ids. The numeric ranges mirror <see cref="EdgeErrorCode"/>.</summary>
public static class EdgeEventIds
{
    /// <summary>Startup began: the host is about to run every registered <see cref="Hosting.IEdgeStartupTask"/>.</summary>
    public static readonly EventId StartupBegan = new(100, nameof(StartupBegan));

    /// <summary>One startup task finished successfully.</summary>
    public static readonly EventId StartupTaskCompleted = new(101, nameof(StartupTaskCompleted));

    /// <summary>A startup task threw and aborted startup.</summary>
    public static readonly EventId StartupFailed = new(102, nameof(StartupFailed));

    /// <summary>Every startup task finished successfully.</summary>
    public static readonly EventId StartupCompleted = new(103, nameof(StartupCompleted));

    /// <summary>A <see cref="Lifecycle.IEdgeLifecycle"/> event was raised to its observers.</summary>
    public static readonly EventId LifecycleRaised = new(200, nameof(LifecycleRaised));

    /// <summary>A <see cref="Lifecycle.IEdgeLifecycleObserver"/> threw while handling a raised event; caught and logged, never rethrown.</summary>
    public static readonly EventId LifecycleObserverFailed = new(201, nameof(LifecycleObserverFailed));

    /// <summary>A SQLite native provider was installed and verified.</summary>
    public static readonly EventId NativeProviderInstalled = new(300, nameof(NativeProviderInstalled));

    /// <summary>A SQLite native provider failed to load or verify.</summary>
    public static readonly EventId NativeProviderFailed = new(301, nameof(NativeProviderFailed));

    /// <summary>A migration applied successfully.</summary>
    public static readonly EventId MigrationApplied = new(400, nameof(MigrationApplied));

    /// <summary>A migration threw and left the database at its prior <c>user_version</c>.</summary>
    public static readonly EventId MigrationFailed = new(401, nameof(MigrationFailed));

    /// <summary>A WAL checkpoint failed during a lifecycle event.</summary>
    public static readonly EventId CheckpointFailed = new(500, nameof(CheckpointFailed));
}
