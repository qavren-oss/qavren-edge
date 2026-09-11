using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite.Provider;

namespace Qavren.Edge.Sqlite.Native;

/// <summary>
/// Installs the generated provider into <c>SQLitePCL.raw</c> and proves the native library actually
/// loaded by running one query on an in-memory connection.
/// </summary>
public class QedgeSqliteNativeProvider : ISqliteNativeProvider
{
    private static readonly Lock InstallGate = new();
    private static bool _resolverInstalled;

    private SqliteNativeInfo? _info;

    public virtual string Name => "Qavren.Edge.Sqlite.Native";

    public virtual string LibraryName => QedgeNativeLibrary.DllImportNameForCurrentTarget;

    public virtual bool SupportsEncryption => false;

    /// <summary>What the provider reports to Microsoft.Data.Sqlite. See <see cref="QedgeNativeLibrary.ReportedName"/>.</summary>
    protected virtual string ReportedLibraryName => "qedge_sqlite3";

    /// <summary>The file base name actually loaded. The Cipher package overrides this to redirect.</summary>
    protected virtual string PhysicalLibraryName => "qedge_sqlite3";

    /// <summary>
    /// Whether <see cref="Install"/> calls <c>SQLitePCL.raw.FreezeProvider()</c>.
    /// <see langword="null"/> (the default) means "follow <see cref="EdgeOptions.FreezeSqliteProvider"/>",
    /// which <see cref="SqliteNativeInstallStartupTask"/> resolves from <c>IOptions&lt;EdgeOptions&gt;</c>
    /// before calling <see cref="Install"/>; that option defaults to <see langword="true"/> and tests set
    /// it <see langword="false"/> so they can swap providers. Setting this property explicitly (from the
    /// <c>configure</c> callback of <c>UseSqliteNative()</c>) wins over the option.
    /// If <see cref="Install"/> is called without the startup task, an unset value means frozen.
    /// </summary>
    public bool? FreezeProvider { get; set; }

    public string? ResolvedPath { get; private set; }

    public void Install()
    {
        lock (InstallGate)
        {
            InstallResolver();

            QedgeNativeLibrary.ReportedName = ReportedLibraryName;
            SQLitePCL.raw.SetProvider(new SQLite3Provider_qedge());

            if (FreezeProvider ?? true)
            {
                // SqliteConnection's static ctor reflectively calls SQLitePCL.Batteries_V2.Init(),
                // which would silently replace this provider if any transitive package ever brings
                // a batteries assembly into the app. FreezeProvider makes that a no-op.
                SQLitePCL.raw.FreezeProvider();
            }

            _info = Probe();
        }
    }

    public SqliteNativeInfo Describe()
        => _info ?? throw new InvalidOperationException("Install() has not run yet.");

    private void InstallResolver()
    {
        if (_resolverInstalled)
        {
            return;
        }

        var providerAssembly = typeof(SQLite3Provider_qedge).Assembly;
        NativeLibrary.SetDllImportResolver(providerAssembly, Resolve);
        _resolverInstalled = true;
    }

    private IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, QedgeNativeLibrary.DllImportNameForCurrentTarget, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        if (string.Equals(libraryName, "__Internal", StringComparison.Ordinal))
        {
            // iOS: the symbols are in the main executable; let the runtime handle it.
            return IntPtr.Zero;
        }

        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                ResolvedPath = candidate;
                return handle;
            }
        }

        if (NativeLibrary.TryLoad(PhysicalLibraryName, assembly, searchPath, out var fallback))
        {
            ResolvedPath = "(default probing)";
            return fallback;
        }

        throw new EdgeNativeException(
            RuntimeInformation.RuntimeIdentifier,
            PhysicalLibraryName,
            [.. Candidates()],
            $"Reference {Name} (or Qavren.Edge.Sqlite.Native.Cipher) and make sure the runtimes/ asset for " +
            $"{RuntimeInformation.RuntimeIdentifier} shipped. On iOS, confirm the xcframework NativeReference " +
            "appears in the build log.");
    }

    private IEnumerable<string> Candidates()
    {
        var fileName = OperatingSystem.IsWindows()
            ? PhysicalLibraryName + ".dll"
            : OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
                ? "lib" + PhysicalLibraryName + ".dylib"
                : "lib" + PhysicalLibraryName + ".so";

        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, fileName);
        yield return Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", fileName);
    }

    private SqliteNativeInfo Probe()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Mode=Memory");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version(), vec_version(), qedge_version()";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new EdgeNativeException(
                RuntimeInformation.RuntimeIdentifier,
                PhysicalLibraryName,
                [.. Candidates()],
                "The native library loaded but returned no version row; the build is malformed.");
        }

        var qedge = reader.GetString(2);
        var cipher = ParseSegment(qedge, "cipher ");

        return new SqliteNativeInfo(
            Name,
            LibraryName,
            ResolvedPath,
            reader.GetString(0),
            reader.GetString(1),
            cipher,
            ParseSegment(qedge, "build "));
    }

    /// <summary>qedge_version() is "sqlite &lt;v&gt; | vec &lt;v&gt; | cipher &lt;v|none&gt; | build &lt;sha&gt;".</summary>
    private static string ParseSegment(string version, string prefix)
    {
        foreach (var part in version.Split('|', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith(prefix, StringComparison.Ordinal))
            {
                return part[prefix.Length..];
            }
        }

        return "unknown";
    }
}
