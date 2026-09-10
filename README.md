# Qavren.Edge

On-device **SQLite + sqlite-vec + ONNX Runtime** for .NET MAUI and .NET 10, with
Shiny-style hosting ergonomics and none of the framework lock-in.

Free and open source (MIT) from [Qavren Solutions LLC](https://qavrensolutions.com).

## Status

Design phase. The suite is built as ordered sub-projects, each a top-level
folder in this repository:

| # | Folder | Scope | State |
|---|---|---|---|
| 1 | `foundation/` | Hosting core, MAUI lifecycle bridge, SQLite provider with sqlite-vec compiled in (plain + SQLCipher) | spec approved |
| 2 | `embeddings/` | ONNX session hosting, MEAI `IEmbeddingGenerator`, MEVD `VectorStore` over vec0 + FTS5 | not started |
| 3 | `ingestion/` | Chunkers, PDF/DOCX extraction, incremental re-index pipeline | not started |
| 4 | `chat/` | MEAI `IChatClient` over ONNX Runtime GenAI, RAG recipe | not started |
| 5 | `docs/` | Docs site, benchmarks, 1.0 | not started |

Specs live at `<sub-project>/docs/superpowers/specs/`.
