# Architecture decision records

Each area keeps its decisions as numbered records beside the code, in the format ADR 0001
establishes. A record states the context, the decision and its consequences at the time it
was taken; later records supersede rather than edit. The pages below are the records as they
live in the repository.

## Hosting core and SQLite

- [1. Record architecture decisions](../../../foundation/docs/adr/0001-record-architecture-decisions.md)
- [2. Own the SQLite build and the SQLitePCLRaw provider](../../../foundation/docs/adr/0002-own-sqlite-build-and-provider.md)

## Embeddings and the vector store

- [3. Pin `Microsoft.ML.OnnxRuntime` at 1.30.0](../../../embeddings/docs/adr/0003-pin-onnxruntime-1-30-0.md)
- [4. Cut NNAPI from `EdgeExecutionProvider`](../../../embeddings/docs/adr/0004-cut-nnapi.md)
- [5. `IEdgeModelPaths` lives in `Qavren.Edge.Onnx` for now, absorbed into SP1 at 1.0](../../../embeddings/docs/adr/0005-absorb-iedgemodelpaths-into-sp1-at-1-0.md)
- [6. Defer multilingual presets to `Microsoft.ML.Tokenizers` 3.x](../../../embeddings/docs/adr/0006-defer-multilingual-to-tokenizers-3x.md)
- [7. Accept the `MEVD9001` experimental surface in `Qavren.Edge.VectorData`](../../../embeddings/docs/adr/0007-mevd9001-experimental-surface.md)
- [8. Raise SP2's platform floors above SP1's — android 24.0, Apple 15.1](../../../embeddings/docs/adr/0008-raised-platform-floors.md)

## Ingestion

- [9. PdfPig and the Apache-2.0 split](../../../ingestion/docs/adr/0009-pdfpig-and-the-apache-2-0-split.md)
- [10. Content-addressed chunk keys](../../../ingestion/docs/adr/0010-content-addressed-chunk-keys.md)
- [11. Mirror MEDI's shape rather than depend on it](../../../ingestion/docs/adr/0011-mirror-medi-rather-than-depend-on-it.md)
- [12. Two migration versions, and what SP2's registry buys](../../../ingestion/docs/adr/0012-two-migration-versions-and-sp2s-registry.md)
- [13. One token-index search for both tokenizers](../../../ingestion/docs/adr/0013-one-token-index-search-for-both-tokenizers.md)

## Chat and RAG

- [9. Execution providers cut](../../../chat/docs/adr/0009-execution-providers-cut.md)
- [10. The upstream `IChatClient` is not shipped](../../../chat/docs/adr/0010-upstream-ichatclient-not-shipped.md)
- [11. Guidance is unverified, not unavailable](../../../chat/docs/adr/0011-guidance-unverified-not-unavailable.md)
- [12. No tool calling in v1](../../../chat/docs/adr/0012-no-tool-calling-in-v1.md)
- [13. Apple platform floor rises to 15.4](../../../chat/docs/adr/0013-apple-platform-floor-15-4.md)
