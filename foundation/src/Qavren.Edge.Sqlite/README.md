# Qavren.Edge.Sqlite

SQLite for Qavren.Edge: `AddSqlite()`, `IEdgeDatabase`, pragma-tuned connections, versioned
migrations, and helpers for `sqlite-vec` KNN queries and FTS5. Microsoft.Data.Sqlite, EF Core
Sqlite, sqlite-net-pcl and Dapper all work unchanged on the connection this package opens.
Pair it with one native package: `Qavren.Edge.Sqlite.Native` or `Qavren.Edge.Sqlite.Native.Cipher`.

```
dotnet add package Qavren.Edge.Sqlite
dotnet add package Qavren.Edge.Sqlite.Native
```

```csharp
services.AddQavrenEdge(edge => edge
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddMigration<M001_CreateNotes>()
    .UseSqliteNative());

await using var conn = await db.OpenConnectionAsync(ct);   // db is the injected IEdgeDatabase
var hits = await Knn.QueryAsync(conn, "notes_vec", query, k: 10, cancellationToken: ct);
```

Migrations are registered explicitly and run at startup; a failed migration is the app's single
startup fault and every later `OpenConnectionAsync` rethrows it with its remediation. No
down-migrations.

Sub-project README: [foundation/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/foundation/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
