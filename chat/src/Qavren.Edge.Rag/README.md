# Qavren.Edge.Rag

The retrieval-augmented generation recipe of the Qavren.Edge suite, over any
`Microsoft.Extensions.AI` chat client and any `Microsoft.Extensions.VectorData` store:
`IEdgeRetriever`, `VectorStoreRetriever` (keyed collections, hybrid search opt-in), the
`UseRag()` pipeline step that puts numbered context under a token budget, `[n]` citations
returned as `CitationAnnotation` with text-span regions, and `ExtractiveChatClient` as the
no-LLM floor. No ONNX dependency; pairs with `Qavren.Edge.Chat.Onnx` or any other client.

```
dotnet add package Qavren.Edge.Rag
```

```csharp
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddOnnxEmbeddings(o => o.Preset = EmbeddingPresets.MiniLmL6V2Int8)
    .AddVectorStore()
    .AddVectorCollectionMigration<string, Note>(version: 1, "notes")
    .AddOnnxChat(ChatPresets.Llama32_1BInstructInt4, pipeline: chat => chat.UseRag())
    .AddVectorStoreRetriever<string, Note>("notes",
        n => new RagSource(n.Key, n.Body) { Title = n.Title, Uri = n.Url }));

var response = await chat.GetResponseAsync("what does the warranty cover?");
foreach (var citation in response.Messages[^1].Contents
                                 .SelectMany(c => c.Annotations ?? [])
                                 .OfType<CitationAnnotation>())
    Console.WriteLine($"[{citation.Title}] {citation.Url}");
```

When streaming, citations arrive once on the final metadata update and their spans index the
accumulated answer.

Sub-project README: [chat/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/chat/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
