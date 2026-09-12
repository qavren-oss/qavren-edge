# Qavren.Edge.Sqlite.Native.Cipher

The encrypted variant of `Qavren.Edge.Sqlite.Native`: SQLCipher with libtomcrypt and `sqlite-vec`
compiled in, for the same RIDs and the same xcframework. Use it instead of the plain native
package, never alongside it.

```
dotnet add package Qavren.Edge.Sqlite.Native.Cipher
```

```csharp
services.AddQavrenEdge(edge => edge
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .UseSqliteNativeCipher());
```

The key is supplied through the SQLite options at registration; the pragmas the suite applies on
every open connection include the SQLCipher ones. Referencing both native packages in one app is
a configuration error caught at startup.

Sub-project README: [foundation/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/foundation/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
