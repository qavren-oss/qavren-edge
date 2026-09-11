namespace Qavren.Edge.Sqlite.Provider;

/// <summary>Names the native library for the generated provider.</summary>
public static class QedgeNativeLibrary
{
#if IOS
    /// <summary>iOS links the xcframework statically, so entry points live in the main executable.</summary>
    internal const string DllImportName = "__Internal";
#else
    /// <summary>
    /// Every non-iOS target (including Mac Catalyst) loads a shared library by name. See plan
    /// adjustment 2: upstream routes <c>net10.0-maccatalyst</c> to the ordinary named-DllImport
    /// provider, not to <c>__Internal</c>.
    /// </summary>
    internal const string DllImportName = "qedge_sqlite3";
#endif

    /// <summary>
    /// What <c>ISQLite3Provider.GetNativeLibraryName()</c> returns. Microsoft.Data.Sqlite looks this
    /// up in a fixed dictionary { e_sqlcipher: true, e_sqlite3: false, e_sqlite3mc: true,
    /// sqlcipher: true, sqlite3mc: true, winsqlite3: false } and throws on the Password path when the
    /// answer is <see langword="false"/>. Unknown names are accepted. The plain Native package leaves
    /// this at "qedge_sqlite3"; the Cipher package sets it to "sqlcipher" before installing.
    /// </summary>
    public static string ReportedName { get; set; } = "qedge_sqlite3";

    /// <summary>The exact string passed to <c>DllImport</c> on this target framework.</summary>
    public static string DllImportNameForCurrentTarget => DllImportName;
}
