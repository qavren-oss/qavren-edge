# Qavren.Edge.Embeddings.Onnx

On-device text embeddings as a `Microsoft.Extensions.AI` `IEmbeddingGenerator<string, Embedding<float>>`,
over ONNX Runtime and `Microsoft.ML.Tokenizers`. Presets carry pinned model hashes;
`EmbeddingPresets.MiniLmL6V2Int8` is the 384-dimension default.

```
dotnet add package Qavren.Edge.Embeddings.Onnx
```

```csharp
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddOnnxEmbeddings(o => o.Preset = EmbeddingPresets.MiniLmL6V2Int8));

var embed = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
var vector = (await embed.GenerateAsync(["roof leak above the kitchen"]))[0].Vector;
```

`Qavren.Edge.VectorData` resolves this generator from the container and fills any record
property whose vector source is a `string`, so most apps never call it directly.

Sub-project README: [embeddings/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/embeddings/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
