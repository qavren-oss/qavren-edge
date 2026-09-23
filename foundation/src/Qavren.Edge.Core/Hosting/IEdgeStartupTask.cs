namespace Qavren.Edge.Hosting;

/// <summary>A unit of work run once, sequentially, during host startup.</summary>
public interface IEdgeStartupTask
{
    /// <summary>Ascending run order. Ties resolve in DI registration order. See <see cref="EdgeStartupOrder"/>.</summary>
    int Order { get; }

    /// <summary>Runs the task's work once, during host startup.</summary>
    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>Reserved startup orders. Consumer tasks should use <see cref="ConsumerDefault"/> or higher.</summary>
public static class EdgeStartupOrder
{
    /// <summary>Installs and verifies the SQLite native provider.</summary>
    public const int NativeProviderInstall = 0;

    /// <summary>Opens every configured database.</summary>
    public const int DatabaseOpen = 10;

    /// <summary>Runs pending migrations.</summary>
    public const int Migrations = 100;

    /// <summary>The default order for a consumer-registered <see cref="IEdgeStartupTask"/>.</summary>
    public const int ConsumerDefault = 1000;
}
