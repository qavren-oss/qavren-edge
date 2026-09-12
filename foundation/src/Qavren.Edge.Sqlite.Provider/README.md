# Qavren.Edge.Sqlite.Provider

The generated SQLitePCLRaw provider `SQLite3Provider_qedge : ISQLite3Provider` that binds the
suite's own `qedge_sqlite3` / `qedge_sqlcipher` native library. A drift test keeps it in step
with the SQLitePCLRaw version the suite pins.

```
dotnet add package Qavren.Edge.Sqlite.Native   # pulls this package in
```

You do not reference this package directly and you do not call it: `Qavren.Edge.Sqlite.Native`
and `Qavren.Edge.Sqlite.Native.Cipher` depend on it and `UseSqliteNative()` /
`UseSqliteNativeCipher()` install it as the SQLitePCLRaw provider at startup.

Sub-project README: [foundation/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/foundation/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
