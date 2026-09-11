namespace Qavren.Edge;

/// <summary>Where Qavren.Edge stores durable data and disposable caches.</summary>
public interface IEdgePaths
{
    /// <summary>Durable storage. Backed up by the platform where the platform backs anything up.</summary>
    string Data { get; }

    /// <summary>Disposable storage. The OS may delete this at any time.</summary>
    string Cache { get; }
}

/// <summary>
/// Non-MAUI default: <c>LocalApplicationData/&lt;AppName&gt;/qavren-edge</c> with a
/// <c>cache</c> subdirectory. Both directories are created on construction.
/// </summary>
public sealed class DefaultEdgePaths : IEdgePaths
{
    public DefaultEdgePaths(string appName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);

        Data = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            appName,
            "qavren-edge");
        Cache = Path.Combine(Data, "cache");

        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Cache);
    }

    public string Data { get; }

    public string Cache { get; }
}
