# Qavren.Edge.Ingestion

The document ingestion pipeline of the Qavren.Edge suite (sub-project 3): `AddIngestion()`, the
document model, plain-text and Markdown extractors, three chunkers (plain, heading-scoped,
token window), a token budget, xxHash128 content hashing for incremental re-indexing, a
cancellable and checkpointed runner, and the chunk writer into `Qavren.Edge.VectorData`.

```
dotnet add package Qavren.Edge.Ingestion
```

```csharp
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddOnnxEmbeddings()
    .AddVectorStore()
    .AddIngestion(migrationVersion: 10));   // claims versions 10 AND 11

var pipeline = services.GetRequiredService<IIngestionPipeline>();
var result = await pipeline.RunAsync(
    IngestionSource.Folder(@"C:\docs", "*.md", recursive: true),
    options: new IngestionRunOptions { Budget = IngestionBudget.Background });
Console.WriteLine($"{result.Outcome}: +{result.ChunksAdded} -{result.ChunksRemoved}");
```

Satellites: `.Pdf` (PdfPig, Apache-2.0, opt-in), `.OpenXml` (DOCX), `.Onnx` (the real tokenizer
and a resource-aware throttle), `.DataIngestion` (the Microsoft.Extensions.DataIngestion shim).
Error codes 6000-6299.

Sub-project README: [ingestion/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/ingestion/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
