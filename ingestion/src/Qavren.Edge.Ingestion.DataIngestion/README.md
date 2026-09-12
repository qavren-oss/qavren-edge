# Qavren.Edge.Ingestion.DataIngestion

The `Microsoft.Extensions.DataIngestion` (MEDI) shim for `Qavren.Edge.Ingestion`, in both
directions: `EdgeDocumentConverter.ToMedi` / `FromMedi` between the suite's `ExtractedDocument`
and MEDI's `IngestionDocument`, `EdgeChunkerMediAdapter` (a MEDI `IngestionChunker<string>` over
the suite's chunkers), `EdgeVectorStoreMediWriter` (a MEDI chunk writer into the suite's vector
store) and `MediReaderAdapter` (a MEDI reader as an `IDocumentExtractor`). Built against the
zero-dependency Abstractions package only. **Prerelease only** while MEDI is.

```
dotnet add package Qavren.Edge.Ingestion.DataIngestion --prerelease
```

```csharp
IngestionDocument medi = EdgeDocumentConverter.ToMedi(extracted);
ExtractedDocument back  = EdgeDocumentConverter.FromMedi(medi);
```

Why a shim rather than a dependency on MEDI's implementation package is ADR 0011.

Sub-project README: [ingestion/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/ingestion/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
