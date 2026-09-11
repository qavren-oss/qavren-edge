// Run: dotnet run foundation/native/scripts/smoke.cs -- <path-to-dll>
using System.Runtime.InteropServices;
using System.Text;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: smoke.cs <path-to-qedge dll>");
    return 2;
}

var dllPath = Path.GetFullPath(args[0]);
if (!File.Exists(dllPath))
{
    Console.Error.WriteLine($"not found: {dllPath}");
    return 2;
}

NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, asm, path) =>
    name == "qedge" ? NativeLibrary.Load(dllPath) : IntPtr.Zero);

var rc = Native.sqlite3_open_v2(Utf8(":memory:"), out var db, 0x00000002 /*READWRITE*/ | 0x00000004 /*CREATE*/, IntPtr.Zero);
if (rc != 0)
{
    Console.Error.WriteLine($"sqlite3_open_v2 failed rc={rc}");
    return 1;
}

foreach (var sql in new[] { "select sqlite_version()", "select vec_version()", "select qedge_version()" })
{
    Console.WriteLine($"{sql} -> {Scalar(db, sql)}");
}

rc = Exec(db, "create virtual table t using vec0(embedding float[4] distance_metric=cosine)");
if (rc != 0)
{
    Console.Error.WriteLine($"vec0 CREATE failed rc={rc}");
    return 1;
}

Console.WriteLine("OK: vec0 virtual table created, auto-extension survived SQLITE_OMIT_LOAD_EXTENSION");
return 0;

static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

static int Exec(IntPtr db, string sql)
{
    var rc = Native.sqlite3_prepare_v2(db, Utf8(sql), -1, out var stmt, IntPtr.Zero);
    if (rc != 0) { return rc; }
    rc = Native.sqlite3_step(stmt);
    _ = Native.sqlite3_finalize(stmt);
    return rc is 100 or 101 ? 0 : rc;   // SQLITE_ROW / SQLITE_DONE
}

static string Scalar(IntPtr db, string sql)
{
    var rc = Native.sqlite3_prepare_v2(db, Utf8(sql), -1, out var stmt, IntPtr.Zero);
    if (rc != 0) { return $"<prepare rc={rc}>"; }
    try
    {
        return Native.sqlite3_step(stmt) == 100
            ? Marshal.PtrToStringUTF8(Native.sqlite3_column_text(stmt, 0)) ?? "<null>"
            : "<no row>";
    }
    finally
    {
        _ = Native.sqlite3_finalize(stmt);
    }
}

internal static class Native
{
    private const string Dll = "qedge";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int sqlite3_step(IntPtr stmt);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int sqlite3_finalize(IntPtr stmt);
}
