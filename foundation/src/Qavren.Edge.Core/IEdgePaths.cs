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
    /// <summary>Resolves and creates <see cref="Data"/> and <see cref="Cache"/> under the platform's local application data folder.</summary>
    /// <param name="appName">The application's name; used as the directory segment under local application data.</param>
    /// <exception cref="ArgumentException"><paramref name="appName"/> is null, empty, or whitespace.</exception>
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

    /// <inheritdoc/>
    public string Data { get; }

    /// <inheritdoc/>
    public string Cache { get; }
}
