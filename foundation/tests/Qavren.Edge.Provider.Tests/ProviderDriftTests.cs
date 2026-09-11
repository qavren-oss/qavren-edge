using System.Reflection;
using System.Text.Json;
using Qavren.Edge.Sqlite.Provider;
using SQLitePCL;
using Xunit;

namespace Qavren.Edge.Provider.Tests;

public class ProviderDriftTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "QavrenEdge.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void GeneratedProvider_ImplementsEveryManifestMemberAndNoOthers()
    {
        var manifestPath = Path.Combine(RepoRoot(), "foundation", "tools", "ProviderGen", "provider.manifest.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var declared = document.RootElement.GetProperty("members")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        var actual = typeof(ISQLite3Provider)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(Format)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(declared.Count, actual.Count);
        Assert.Empty(declared.Except(actual, StringComparer.Ordinal));
        Assert.Empty(actual.Except(declared, StringComparer.Ordinal));
    }

    [Fact]
    public void GeneratedProviderTypeExistsAndIsAnISQLite3Provider()
    {
        var type = typeof(SQLite3Provider_qedge);

        Assert.True(typeof(ISQLite3Provider).IsAssignableFrom(type));
        Assert.True(type.IsSealed);
    }

    [Fact]
    public void ReportedLibraryName_IsNotOneMicrosoftDataSqliteRefusesToEncrypt()
    {
        // MDS maps GetNativeLibraryName() through { e_sqlcipher: true, e_sqlite3: false,
        // e_sqlite3mc: true, sqlcipher: true, sqlite3mc: true, winsqlite3: false } and throws on the
        // Password path when the answer is false. Unknown names are accepted.
        Assert.NotEqual("e_sqlite3", QedgeNativeLibrary.ReportedName);
        Assert.NotEqual("winsqlite3", QedgeNativeLibrary.ReportedName);
    }

    [Fact]
    public void NoSqlitePclRawBundlePackageIsInTheGraph()
    {
        // 2.x bundle packages are incompatible with SQLitePCLRaw.core 3.x and would call
        // raw.SetProvider behind our back through SqliteConnection's static constructor.
        var loadable = Directory.GetFiles(AppContext.BaseDirectory, "SQLitePCLRaw.batteries*.dll");

        Assert.Empty(loadable);
    }

    // Kept byte-identical to ManifestBuilder.FormatType/FormatMember (Task 2.2): a plain
    // Type.FullName renders a constructed generic's arguments assembly-qualified, so all six
    // ReadOnlySpan<byte> members would mismatch the checked-in manifest.
    private static string FormatType(Type t)
    {
        if (t.IsByRef)
        {
            return FormatType(t.GetElementType()!) + "&";
        }

        if (t.IsPointer)
        {
            return FormatType(t.GetElementType()!) + "*";
        }

        if (t.IsArray)
        {
            return FormatType(t.GetElementType()!) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
        }

        if (t.IsConstructedGenericType)
        {
            var args = string.Join(",", t.GetGenericArguments().Select(FormatType));
            return t.GetGenericTypeDefinition().FullName + "[" + args + "]";
        }

        return t.FullName ?? t.Name;
    }

    private static string Format(MethodInfo m)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(FormatType(m.ReturnType)).Append(' ').Append(m.Name).Append('(');
        var ps = m.GetParameters();
        for (var i = 0; i < ps.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(FormatType(ps[i].ParameterType)).Append(' ').Append(ps[i].Name);
        }

        return sb.Append(')').ToString();
    }
}
