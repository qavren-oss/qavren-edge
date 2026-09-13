---
_layout: landing
title: On-device data and AI for .NET
---

# On-device data and AI for .NET MAUI and .NET 10

On-device data and AI for .NET MAUI and .NET 10: SQLite with `sqlite-vec`,
ONNX Runtime embeddings, a `Microsoft.Extensions.VectorData` store, document
ingestion, and local chat with retrieval-augmented generation. Everything runs
in-process on the device. There is no service to call, nothing leaves the
phone, and nothing is downloaded without the user's consent.

## Install

```
dotnet add package Qavren.Edge --prerelease          # hosting core + SQLite + the native library
dotnet add package Qavren.Edge.Maui --prerelease     # MAUI lifecycle bridge and app paths
```

Then add what you use: `Qavren.Edge.Embeddings.Onnx`, `Qavren.Edge.VectorData`,
`Qavren.Edge.Ingestion` (with `.Onnx`, `.Pdf`, `.OpenXml`), `Qavren.Edge.Chat.Onnx`,
`Qavren.Edge.Rag`. Every package is MIT except that `Qavren.Edge.Ingestion.Pdf`
depends on PdfPig (Apache-2.0), which is why PDF support is a separate package.

## Where to go

- [Getting started](getting-started.md): the whole stack composed once, then
  index, search and ask.
- Guides: [hosting core and SQLite](../../foundation/README.md),
  [embeddings and the vector store](../../embeddings/README.md),
  [ingestion](../../ingestion/README.md), [chat and RAG](../../chat/README.md).
- [Error codes](../../foundation/docs/errors.md): every `EdgeException` carries
  a numbered code and a remediation.
- [API reference](api/index.md) for every package, and the
  [MAUI package](api-maui/index.md).
- Decisions: the architecture decision records for each area, in the sidebar.

## Supported platforms

| Adopting | Android | iOS / Mac Catalyst |
|---|---|---|
| SQLite and hosting only | 21 | 15.0 |
| + embeddings, vector store, ingestion | 24 | 15.1 |
| + chat | 24 | 15.4 |

Windows x64 and arm64, Linux x64 and arm64 and macOS arm64 are supported for
plain .NET 10. Source, issues and releases:
[github.com/qavren-oss/qavren-edge](https://github.com/qavren-oss/qavren-edge).
