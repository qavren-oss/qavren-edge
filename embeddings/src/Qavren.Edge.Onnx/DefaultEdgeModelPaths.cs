namespace Qavren.Edge.Onnx;

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
