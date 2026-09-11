namespace Qavren.Edge.ProviderGen;

/// <summary>
/// Signatures transcribed verbatim from ericsink/SQLitePCL.raw
/// src/SQLitePCLRaw.core/isqlite3.cs at tag v3.0.5 (byte-identical to v2.1.12).
/// Format matches <see cref="ManifestBuilder.FormatMember"/>: "ReturnType Name(ParamType name, ...)".
/// </summary>
internal static class SpotCheck
{
    internal static readonly string[] ExpectedSignatures =
    [
        "System.String GetNativeLibraryName()",
        "System.Int32 sqlite3_open(SQLitePCL.utf8z filename, System.IntPtr& db)",
        "System.Int32 sqlite3_open_v2(SQLitePCL.utf8z filename, System.IntPtr& db, System.Int32 flags, SQLitePCL.utf8z vfs)",
        "System.Int32 sqlite3_close_v2(System.IntPtr db)",
        "System.Int32 sqlite3_prepare_v2(SQLitePCL.sqlite3 db, System.ReadOnlySpan`1[System.Byte] sql, System.IntPtr& stmt, System.ReadOnlySpan`1[System.Byte]& remain)",
        "System.Int32 sqlite3_step(SQLitePCL.sqlite3_stmt stmt)",
        "System.Int32 sqlite3_finalize(System.IntPtr stmt)",
        "System.Int32 sqlite3_bind_blob(SQLitePCL.sqlite3_stmt stmt, System.Int32 index, System.ReadOnlySpan`1[System.Byte] blob)",
        "System.ReadOnlySpan`1[System.Byte] sqlite3_column_blob(SQLitePCL.sqlite3_stmt stmt, System.Int32 index)",
        "SQLitePCL.utf8z sqlite3_column_text(SQLitePCL.sqlite3_stmt stmt, System.Int32 index)",
        "SQLitePCL.utf8z sqlite3_errmsg(SQLitePCL.sqlite3 db)",
        "System.Int32 sqlite3_libversion_number()",
        "SQLitePCL.utf8z sqlite3_libversion()",
        "System.Int32 sqlite3_key(SQLitePCL.sqlite3 db, System.ReadOnlySpan`1[System.Byte] key)",
        "System.Int32 sqlite3_key_v2(SQLitePCL.sqlite3 db, SQLitePCL.utf8z dbname, System.ReadOnlySpan`1[System.Byte] key)",
        "System.Int32 sqlite3_rekey_v2(SQLitePCL.sqlite3 db, SQLitePCL.utf8z dbname, System.ReadOnlySpan`1[System.Byte] key)",
        "System.Int32 sqlite3_enable_load_extension(SQLitePCL.sqlite3 db, System.Int32 enable)",
        "System.Int32 sqlite3_win32_set_directory(System.Int32 typ, SQLitePCL.utf8z path)",
    ];
}
