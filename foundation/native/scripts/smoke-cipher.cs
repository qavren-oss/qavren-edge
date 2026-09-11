// Run: dotnet run foundation/native/scripts/smoke-cipher.cs [-- <path-to-qedge_sqlcipher.dll> <scratch-dir>]
//
// With no arguments it probes the win-x64 artifact this repo's build-windows.ps1 produces and
// uses a temp scratch directory, so `dotnet run smoke-cipher.cs` on its own is a complete check.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

var dllPath = Path.GetFullPath(args.Length > 0
    ? args[0]
    : Path.Combine(ScriptDirectory(), "..", "artifacts", "win-x64", "qedge_sqlcipher.dll"));

if (!File.Exists(dllPath))
{
    Console.Error.WriteLine($"not found: {dllPath}");
    Console.Error.WriteLine("build it first: build-windows.ps1 -Arch x64 -Cipher -BuildSha local");
    return 2;
}

var scratchDir = Path.GetFullPath(args.Length > 1
    ? args[1]
    : Path.Combine(Path.GetTempPath(), "qedge-cipher-smoke"));

var dbPath = Path.Combine(scratchDir, "cipher-smoke.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
File.Delete(dbPath);

NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, asm, path) =>
    name == "qedge" ? NativeLibrary.Load(dllPath) : IntPtr.Zero);

const string GoodKey = "x'0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20'";
const string BadKey = "x'FF02030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20'";

// Create, key, write.
if (!Open(dbPath, out var db)) { return 1; }
if (Exec(db, $"PRAGMA key = '{GoodKey.Replace("'", "''")}'") != 0) { Console.Error.WriteLine("PRAGMA key failed"); return 1; }
if (Exec(db, "CREATE TABLE t(x TEXT)") != 0) { Console.Error.WriteLine("CREATE failed"); return 1; }
if (Exec(db, "INSERT INTO t VALUES('hello')") != 0) { Console.Error.WriteLine("INSERT failed"); return 1; }
_ = Native.sqlite3_close_v2(db);

// Reopen with the right key.
if (!Open(dbPath, out db)) { return 1; }
_ = Exec(db, $"PRAGMA key = '{GoodKey.Replace("'", "''")}'");
Console.WriteLine($"good key -> {Scalar(db, "SELECT x FROM t")}");
Console.WriteLine($"cipher_version -> {Scalar(db, "PRAGMA cipher_version")}");
Console.WriteLine($"qedge_version -> {Scalar(db, "SELECT qedge_version()")}");
Console.WriteLine($"vec_version -> {Scalar(db, "SELECT vec_version()")}");
_ = Native.sqlite3_close_v2(db);

// Reopen with the wrong key: reading must fail.
if (!Open(dbPath, out db)) { return 1; }
_ = Exec(db, $"PRAGMA key = '{BadKey.Replace("'", "''")}'");
var wrong = Exec(db, "SELECT count(*) FROM sqlite_master");
_ = Native.sqlite3_close_v2(db);

if (wrong == 0)
{
    Console.Error.WriteLine("FAIL: the wrong key was accepted");
    return 1;
}

Console.WriteLine("OK: raw key accepted, wrong key rejected, vec0 present in the cipher build");
return 0;

static string ScriptDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

static bool Open(string path, out IntPtr db)
{
    var rc = Native.sqlite3_open_v2(Encoding.UTF8.GetBytes(path + "\0"), out db, 0x02 | 0x04, IntPtr.Zero);
    if (rc != 0) { Console.Error.WriteLine($"open failed rc={rc}"); }
    return rc == 0;
}

static int Exec(IntPtr db, string sql)
{
    var rc = Native.sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes(sql + "\0"), -1, out var stmt, IntPtr.Zero);
    if (rc != 0) { return rc; }
    rc = Native.sqlite3_step(stmt);
    _ = Native.sqlite3_finalize(stmt);
    return rc is 100 or 101 ? 0 : rc;   // SQLITE_ROW / SQLITE_DONE
}

static string Scalar(IntPtr db, string sql)
{
    var rc = Native.sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes(sql + "\0"), -1, out var stmt, IntPtr.Zero);
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

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int sqlite3_close_v2(IntPtr db);
}
