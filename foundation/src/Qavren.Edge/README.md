# Qavren.Edge

SQLite with `sqlite-vec` compiled in, wired for `Microsoft.Extensions.*` dependency injection.

```csharp
services.AddQavrenEdge(edge => edge
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddMigration<M001_CreateNotes>()
    .UseSqliteNative());
```

Then inject `IEdgeDatabase`:

```csharp
await using var conn = await db.OpenConnectionAsync(ct);   // awaits startup, applies pragmas
var hits = await Knn.QueryAsync(conn, "notes_vec", query, k: 10, cancellationToken: ct);
```

Microsoft.Data.Sqlite, EF Core Sqlite (the `.Core` package), sqlite-net-pcl and Dapper all work
unchanged on top of this provider.

- MAUI lifecycle and `FileSystem`-backed paths: add `Qavren.Edge.Maui`.
- Encryption: replace `Qavren.Edge.Sqlite.Native` with `Qavren.Edge.Sqlite.Native.Cipher` and call
  `UseSqliteNativeCipher()`. Referencing both native packages is a configuration error.

MIT licensed. https://github.com/qavren-oss/qavren-edge
