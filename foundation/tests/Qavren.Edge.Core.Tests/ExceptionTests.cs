using Xunit;

namespace Qavren.Edge.Core.Tests;

public class ExceptionTests
{
    [Fact]
    public void ConfigurationException_CarriesCodeAndHelpLink()
    {
        var ex = new EdgeConfigurationException(EdgeErrorCode.DuplicateDatabaseName, "dupe");

        Assert.Equal(EdgeErrorCode.DuplicateDatabaseName, ex.Code);
        Assert.Equal("dupe", ex.Message);
        Assert.EndsWith("#1001", ex.HelpLink, StringComparison.Ordinal);
        Assert.IsAssignableFrom<EdgeException>(ex);
    }

    [Fact]
    public void NativeException_MessageNamesRidLibraryAndProbedPaths()
    {
        var ex = new EdgeNativeException("win-x64", "qedge_sqlite3", ["C:\\a", "C:\\b"], "reference Qavren.Edge.Sqlite.Native");

        Assert.Equal(EdgeErrorCode.NativeLoadFailed, ex.Code);
        Assert.Contains("win-x64", ex.Message, StringComparison.Ordinal);
        Assert.Contains("qedge_sqlite3", ex.Message, StringComparison.Ordinal);
        Assert.Contains("C:\\b", ex.Message, StringComparison.Ordinal);
        Assert.Contains("reference Qavren.Edge.Sqlite.Native", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["C:\\a", "C:\\b"], ex.ProbedPaths);
    }

    [Fact]
    public void MigrationException_CarriesVersionNameAndInner()
    {
        var inner = new InvalidOperationException("no such table");
        var ex = new EdgeMigrationException(3, "create notes", inner);

        Assert.Equal(EdgeErrorCode.MigrationFailed, ex.Code);
        Assert.Equal(3, ex.Version);
        Assert.Equal("create notes", ex.Name);
        Assert.Same(inner, ex.InnerException);
        Assert.Contains("3", ex.Message, StringComparison.Ordinal);
        Assert.Contains("create notes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DatabaseKeyException_UsesKeyRejectedCode()
    {
        var ex = new EdgeDatabaseKeyException("notes.db", null);

        Assert.Equal(EdgeErrorCode.DatabaseKeyRejected, ex.Code);
        Assert.Contains("notes.db", ex.Message, StringComparison.Ordinal);
    }
}
