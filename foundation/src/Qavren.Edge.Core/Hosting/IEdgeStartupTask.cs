namespace Qavren.Edge.Hosting;

/// <summary>A unit of work run once, sequentially, during host startup.</summary>
public interface IEdgeStartupTask
{
    /// <summary>Ascending run order. Ties resolve in DI registration order. See <see cref="EdgeStartupOrder"/>.</summary>
    int Order { get; }

    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>Reserved startup orders. Consumer tasks should use <see cref="ConsumerDefault"/> or higher.</summary>
public static class EdgeStartupOrder
{
    public const int NativeProviderInstall = 0;
    public const int DatabaseOpen = 10;
    public const int Migrations = 100;
    public const int ConsumerDefault = 1000;
}
