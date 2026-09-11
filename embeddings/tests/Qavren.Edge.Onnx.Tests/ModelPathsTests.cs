using Xunit;

namespace Qavren.Edge.Onnx.Tests;

/// <summary>Spec 6.2's desktop model root.</summary>
public class ModelPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qedge-modelpaths-" + Guid.NewGuid().ToString("N"));
    private readonly MutablePaths _paths;

    public ModelPathsTests()
    {
        _paths = new MutablePaths { Data = Path.Combine(_root, "a"), Cache = Path.Combine(_root, "a", "cache") };
    }

    private sealed class MutablePaths : IEdgePaths
    {
        public required string Data { get; set; }

        public required string Cache { get; set; }
    }

    [Fact]
    public void ModelsAndOrtCacheSitUnderDataAndAreCreatedOnFirstAccess()
    {
        var paths = new DefaultEdgeModelPaths(_paths);

        Assert.Equal(Path.Combine(_paths.Data, "models"), paths.Models);
        Assert.Equal(Path.Combine(_paths.Data, "ort-cache"), paths.OrtCache);
        Assert.True(Directory.Exists(paths.Models));
        Assert.True(Directory.Exists(paths.OrtCache));
    }

    [Fact]
    public void ThePathIsRecomputedFromEdgePathsOnEveryAccess()
    {
        var paths = new DefaultEdgeModelPaths(_paths);
        var before = paths.Models;

        // The iOS sandbox path carries an app GUID that changes on every clean install, which is why
        // nothing here may be cached across accesses.
        _paths.Data = Path.Combine(_root, "b");

        var after = paths.Models;

        Assert.NotEqual(before, after);
        Assert.Equal(Path.Combine(_root, "b", "models"), after);
        Assert.True(Directory.Exists(after));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
