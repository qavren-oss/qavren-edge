using Xunit;

namespace Qavren.Edge.Core.Tests;

public class PathsTests
{
    [Fact]
    public void DefaultPaths_AreUnderLocalApplicationDataAndCreated()
    {
        var appName = "QavrenEdgeTest_" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            appName,
            "qavren-edge");
        try
        {
            IEdgePaths paths = new DefaultEdgePaths(appName);

            Assert.Equal(root, paths.Data);
            Assert.Equal(Path.Combine(root, "cache"), paths.Cache);
            Assert.True(Directory.Exists(paths.Data));
            Assert.True(Directory.Exists(paths.Cache));
        }
        finally
        {
            var appRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName);
            if (Directory.Exists(appRoot))
            {
                Directory.Delete(appRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void EdgeOptions_HaveDocumentedDefaults()
    {
        var options = new EdgeOptions();

        Assert.True(options.FreezeSqliteProvider);
        Assert.Equal(64, options.LifecycleHistoryCapacity);
        Assert.False(string.IsNullOrWhiteSpace(options.AppName));
    }
}
