# Qavren.Edge.Ingestion.OpenXml

DOCX text extraction for `Qavren.Edge.Ingestion`, over `DocumentFormat.OpenXml`. MIT like the
core; it is a separate package for size (the dependency is about 14 MB), so a Markdown-only app
never carries it.

```
dotnet add package Qavren.Edge.Ingestion.OpenXml
```

```csharp
builder.UseQavrenEdge(edge => edge
    // ... AddSqlite, AddOnnxEmbeddings, AddVectorStore, AddIngestion ...
    .AddDocxExtractor());
```

Headings become the chunk breadcrumb the same way Markdown headings do.

Sub-project README: [ingestion/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/ingestion/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
