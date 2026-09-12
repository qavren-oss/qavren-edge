# Qavren.Edge.VectorData

A `Microsoft.Extensions.VectorData` `VectorStore` over the suite's SQLite: `sqlite-vec` (`vec0`)
for vectors, FTS5 for keywords, hybrid search fused with reciprocal rank fusion, and LINQ filters
pushed into SQL. Collections are declared as versioned migrations, so the schema is explicit.

```
dotnet add package Qavren.Edge.VectorData
```

```csharp
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddOnnxEmbeddings(o => o.Preset = EmbeddingPresets.MiniLmL6V2Int8)
    .AddVectorStore()
    .AddVectorCollectionMigration<string, Note>(version: 1, "notes"));

public sealed class Note
{
    [VectorStoreKey] public string Key { get; set; } = "";
    [VectorStoreData(IsFullTextIndexed = true)] public string Title { get; set; } = "";
    [VectorStoreData(IsFullTextIndexed = true)] public string Body { get; set; } = "";
    [VectorStoreVector(384, DistanceFunction = DistanceFunction.CosineDistance)]
    public string? Embedding => Body;          // string source: the registered generator fills it
}

var notes = store.GetCollection<string, Note>("notes");
await notes.UpsertAsync(new Note { Key = "n1", Title = "Roof leak", Body = "…" });
await foreach (var hit in notes.HybridSearchAsync("water damage", ["roof", "leak"], top: 10))
    Console.WriteLine($"{hit.Record.Title} {hit.Score:F4}");
```

Distance functions: cosine, Euclidean and Manhattan (what `vec0` exposes). Collection-valued
data properties and bit/int8 vectors are out of scope for v1.

Sub-project README: [embeddings/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/embeddings/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
