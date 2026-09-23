# Getting started

Two packages get you a database; the rest of the chain is added as you need
it.

```
dotnet add package Qavren.Edge
dotnet add package Qavren.Edge.Maui
```

Add the packages the chain below needs as you go: `Qavren.Edge.Embeddings.Onnx`,
`Qavren.Edge.VectorData`, `Qavren.Edge.Ingestion` (with `.Onnx`, `.Pdf`,
`.OpenXml`, `.DataIngestion`), `Qavren.Edge.Chat.Onnx`, `Qavren.Edge.Rag`.
`Qavren.Edge.Ingestion.DataIngestion` is prerelease only regardless of the
suite's own version — add it with `--prerelease`.

The whole stack, composed once in `MauiProgram.cs`. Outside MAUI the same
chain hangs off `services.AddQavrenEdge(edge => ...)`.

```csharp
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddOnnxEmbeddings(o => o.Preset = EmbeddingPresets.MiniLmL6V2Int8)
    .AddVectorStore()
    .AddIngestion(migrationVersion: 10)          // claims versions 10 and 11
    .AddOnnxIngestion()                          // tokenizer + throttle
    .AddPdfExtractor()                           // optional
    .AddOnnxChat(ChatPresets.Qwen3_600MInt4,     // there is no default preset
                 pipeline: chat => chat.UseRag())
    .AddRetriever(ChunkRetriever));
```

`AddIngestion` claims two consecutive migration versions, `N` for the
collection and `N + 1` for the state tables it owns.

`Qavren.Edge.Rag` retrieves through any `Microsoft.Extensions.VectorData`
collection. `AddVectorStoreRetriever<TKey, TRecord>(collectionName, project)`
is the one-liner for a typed record; the ingestion collection is dynamic, so it
goes through `DelegateRetriever`:

```csharp
static IEdgeRetriever ChunkRetriever(IServiceProvider services)
{
    var chunks = services.GetRequiredService<EdgeVectorStore>().GetDynamicCollection(
        "chunks",
        IngestionSchema.BuildDefinition(
            dimensions: 384, DistanceFunction.CosineDistance, fullTextIndexed: true));

    return new DelegateRetriever("ingestion-chunks", async (query, request, ct) =>
    {
        var sources = new List<RagSource>();

        await foreach (var hit in chunks.HybridSearchAsync(
            query, request.Keywords?.ToArray() ?? [], request.Top, cancellationToken: ct))
        {
            var chunk = IngestedChunk.FromRecord(hit.Record);
            sources.Add(new RagSource(chunk.Key, chunk.Text)
            {
                Title = chunk.Breadcrumb,
                Score = hit.Score ?? 0,
                ScoreKind = RetrievalScoreKind.Relevance,   // HybridSearchAsync fuses by RRF
            });
        }

        return sources;
    });
}
```

Index the corpus once, then again whenever it changes. A re-run over an
unchanged corpus costs one sequential read per document and nothing else:

```csharp
var pipeline = services.GetRequiredService<IIngestionPipeline>();

var run = await pipeline.RunAsync(
    IngestionSource.Folder(@"C:\docs", "*.md", recursive: true),
    options: new IngestionRunOptions { Budget = IngestionBudget.Background });

Console.WriteLine($"{run.Outcome}: +{run.ChunksAdded} -{run.ChunksRemoved} ({run.DocumentsSkipped} skipped)");
```

Then ask, streamed, with citations. Citations arrive once, on the final
metadata update, and their spans index the accumulated answer rather than any
single update, so accumulate first and slice your own buffer:

```csharp
var chat = services.GetRequiredService<IChatClient>();
var answer = new StringBuilder();

await foreach (var update in chat.GetStreamingResponseAsync("what does the warranty cover?"))
{
    Console.Write(update.Text);
    answer.Append(update.Text);

    foreach (var citation in update.Contents.SelectMany(c => c.Annotations ?? [])
                                            .OfType<CitationAnnotation>())
    foreach (var region in (citation.AnnotatedRegions ?? []).OfType<TextSpanAnnotatedRegion>())
    {
        // StartIndex/EndIndex are int? on the shipped MEAI 10.10.0 surface.
        if (region.StartIndex is not { } start || region.EndIndex is not { } end) continue;
        Console.WriteLine($"\n[{citation.Title}] {citation.Url} -> {answer.ToString(start, end - start)}");
    }
}
```

Search with no model in the loop is the vector store on its own:
`GetCollection<TKey, TRecord>` or `GetDynamicCollection`, then `SearchAsync`
(a `vec0` distance, lower is better) or `HybridSearchAsync` (an RRF score,
higher is better).
