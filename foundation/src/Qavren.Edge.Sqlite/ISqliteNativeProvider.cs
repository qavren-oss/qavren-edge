namespace Qavren.Edge.Sqlite;

/// <summary>What the native library reports about itself once installed.</summary>
public sealed record SqliteNativeInfo(
    string ProviderName,
    string LibraryName,
    string? ResolvedPath,
    string SqliteVersion,
    string VecVersion,
    string CipherVersion,
    string BuildSha);

/// <summary>
/// Implemented by <c>Qavren.Edge.Sqlite.Native</c> and <c>Qavren.Edge.Sqlite.Native.Cipher</c>.
/// Exactly one must be registered; zero or two is <see cref="Qavren.Edge.EdgeConfigurationException"/>.
/// </summary>
public interface ISqliteNativeProvider
{
    string Name { get; }

    /// <summary>The base name passed to <c>DllImport</c>, e.g. <c>qedge_sqlite3</c> or <c>__Internal</c>.</summary>
    string LibraryName { get; }

    bool SupportsEncryption { get; }

    /// <summary>Installs the provider into <c>SQLitePCL.raw</c> and verifies it. Startup task order 0.</summary>
    void Install();

    /// <summary>Valid only after <see cref="Install"/>.</summary>
    SqliteNativeInfo Describe();
}
