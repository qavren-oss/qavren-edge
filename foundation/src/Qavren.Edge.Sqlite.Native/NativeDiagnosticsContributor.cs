using Qavren.Edge.Diagnostics;

namespace Qavren.Edge.Sqlite.Native;

/// <summary>
/// The <c>Native</c> block of <see cref="EdgeDiagnosticsReport"/>: which library loaded, from where,
/// and what it reports about itself.
/// </summary>
/// <remarks>
/// <b>This file is compiled twice.</b> <c>Qavren.Edge.Sqlite.Native</c> owns it and
/// <c>Qavren.Edge.Sqlite.Native.Cipher</c> links it, so <c>UseSqliteNative()</c> and
/// <c>UseSqliteNativeCipher()</c> register the same contributor. The cipher package registered none
/// before this was shared, which left an encrypted app's report with an empty native block - no
/// SQLite, sqlite-vec or SQLCipher version, and no resolved path. The component is named after the
/// assembly it is compiled into, which is also what tells the two apart in a report.
/// </remarks>
internal sealed class NativeDiagnosticsContributor(ISqliteNativeProvider provider) : IEdgeDiagnosticsContributor
{
    public string ComponentName =>
        typeof(NativeDiagnosticsContributor).Assembly.GetName().Name ?? "Qavren.Edge.Sqlite.Native";

    public string? ComponentVersion => typeof(NativeDiagnosticsContributor).Assembly.GetName().Version?.ToString();

    public IReadOnlyDictionary<string, string?> Describe()
    {
        try
        {
            var info = provider.Describe();
            return new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["providerName"] = info.ProviderName,
                ["libraryName"] = info.LibraryName,
                ["resolvedPath"] = info.ResolvedPath,
                ["sqliteVersion"] = info.SqliteVersion,
                ["vecVersion"] = info.VecVersion,
                ["cipherVersion"] = info.CipherVersion,
                ["buildSha"] = info.BuildSha,
            };
        }
        catch (InvalidOperationException)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal) { ["state"] = "not installed" };
        }
    }
}
