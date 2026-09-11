namespace Qavren.Edge.Onnx;

/// <summary>
/// Where a 23-137 MB re-derivable blob belongs. Deliberately NOT <see cref="IEdgePaths.Data"/>
/// (backed up on all three platforms; Android's Auto Backup quota is 25 MB per app, so one model
/// there consumes the whole quota and stops the user's real database being backed up) and
/// deliberately NOT <see cref="IEdgePaths.Cache"/> (the OS may purge it mid-session, converting a
/// purge into a re-download).
/// </summary>
public interface IEdgeModelPaths
{
    /// <summary>Durable, excluded from cloud backup, never OS-purged. Created on first access.</summary>
    string Models { get; }

    /// <summary>Sibling of <see cref="Models"/>. Owned and purged by this library, never by the OS.</summary>
    string OrtCache { get; }
}

/// <summary>
/// net10.0 and Windows: <c>&lt;IEdgePaths.Data&gt;/models</c> and <c>&lt;IEdgePaths.Data&gt;/ort-cache</c>.
/// </summary>
/// <remarks>
/// Neither path is cached across accesses. The iOS sandbox path carries an app GUID that changes
/// on every clean install, which is the same reason SP1's <c>MauiEdgePaths</c> reads
/// <c>FileSystem.Current</c> each time; the desktop implementation keeps the same discipline so
/// the two behave identically when a test swaps <see cref="IEdgePaths"/> underneath it.
/// </remarks>
public sealed class DefaultEdgeModelPaths : IEdgeModelPaths
{
    private readonly IEdgePaths _paths;

    /// <summary>Creates the desktop model paths over SP1's <see cref="IEdgePaths"/>.</summary>
    /// <param name="paths">SP1's path provider.</param>
    public DefaultEdgeModelPaths(IEdgePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <inheritdoc />
    public string Models => Ensure(Path.Combine(_paths.Data, "models"));

    /// <inheritdoc />
    public string OrtCache => Ensure(Path.Combine(_paths.Data, "ort-cache"));

    private static string Ensure(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }
}
