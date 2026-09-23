using Qavren.Edge.Onnx;

namespace Qavren.Edge.Benchmarks.Infrastructure;

/// <summary>
/// The app and model roots of one benchmark process, pointed at one fresh directory under the
/// system temp folder. <see cref="Delete"/> removes it; a locked file is left for the OS.
/// </summary>
internal sealed class ScratchPaths : IEdgePaths, IEdgeModelPaths
{
    private ScratchPaths(string root) => Root = root;

    /// <summary>The scratch directory.</summary>
    public string Root { get; }

    public string Data => Root;

    public string Cache => Ensure("cache");

    public string Models => Ensure("models");

    public string OrtCache => Ensure("ort-cache");

    /// <summary>A new, empty directory under <c>%TEMP%/qedge-bench</c>.</summary>
    public static ScratchPaths Create(string area)
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-bench", area, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new ScratchPaths(root);
    }

    /// <summary>Best-effort delete. A still-mapped WAL file or ORT cache entry must not fail a run.</summary>
    public void Delete()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // The directory is under TEMP and the OS reclaims it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    private string Ensure(string leaf)
    {
        var directory = Path.Combine(Root, leaf);
        Directory.CreateDirectory(directory);
        return directory;
    }
}
