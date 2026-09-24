# Benchmarks

The suite ships one BenchmarkDotNet project,
[`benchmarks/Qavren.Edge.Benchmarks`](https://github.com/qavren-oss/qavren-edge/tree/main/benchmarks/Qavren.Edge.Benchmarks),
that measures what a consumer of the packages gets. Every benchmark composes the
container the way the guides do (`services.AddQavrenEdge(...)` with
`UseSqliteNative()`, `AddSqlite`, `AddVectorStore`, `AddIngestion`,
`AddOnnxEmbeddings`, `AddOnnxChat`) and calls only public API. The inputs are
synthetic and generated from one fixed seed, so two runs measure the same bytes.

## What is measured

| Area | Benchmark | What it measures, and why |
|---|---|---|
| SQLite | `VecKnnBenchmarks.Knn10` | A `vec0` k-nearest-neighbour query (`k = 10`, 384 dimensions, cosine) over 1 000, 10 000 and 100 000 rows. `vec0` scans every row, so this is the cost of one vector query as the corpus grows. It runs at two `chunk_size` values: 256, the vector store's default, and 1 024, sqlite-vec's own default. The shipped default had not been benchmarked before this suite. |
| SQLite | `FtsMatchBenchmarks.MatchTop10` | An FTS5 `MATCH` for one term, top 10 by `bm25`, over the same row counts. About 0.6% of rows match. This is the keyword half of hybrid search. |
| SQLite | `VecInsertBenchmarks.InsertBatch` | 1 000 and 10 000 vectors inserted into an empty `vec0` table inside one transaction, at both chunk sizes. This is what a bulk load costs. |
| Vector store | `VectorSearchBenchmarks` | `SearchAsync` and `HybridSearchAsync` (reciprocal rank fusion of the vector and keyword candidates) over 10 000 records, top 10, each with and without a LINQ filter on an indexed property that the store pushes into SQL. |
| Ingestion | `IngestionBenchmarks` | Over a 200-document Markdown corpus: the plain-text and Markdown extractors, the plain, heading and token-window chunkers, content hashing, and the whole pipeline re-run over the unchanged corpus. |
| Embeddings | `TokenizerBenchmarks.Encode32`, `EmbeddingBenchmarks.Generate` | WordPiece encoding of 32 short sentences, and `GenerateAsync` with the int8 MiniLM preset for batches of 1, 8 and 32 sentences. Reported as sentences and tokens per second. Needs the model; see below. |
| Chat | `ChatBenchmarks` | One turn with the Qwen3 0.6B int4 preset on a 120-token prompt, answering with 1 token (prompt processing) and with 64 tokens. Decode speed is `63 / (T64 - T1)` tokens per second. Needs the model. |

Two details affect how to read the ingestion numbers. The chunkers count tokens
with the real WordPiece code over a vocabulary generated from the corpus, so each
corpus word is one token and no model download is needed. The pipeline's
embedding generator is a deterministic hash rather than the ONNX model, so the
ingestion rows measure extraction, chunking, hashing and state; the Embeddings
rows measure the encoder on its own. The re-run embeds nothing in either case.
During setup the suite counts how often the source is opened in the re-run:
200 opens for 200 unchanged files. That measurement backs the "one read per
file" claim in the root README.

## Why the numbers depend on the CPU class

The embedding and chat models are int8 and int4 graphs, and ONNX Runtime runs
their integer arithmetic on whichever kernels the CPU offers. AVX2's u8s8 path
is one class, AVX-512 VNNI another, and ARM NEON (every phone, every Apple
Silicon Mac) a third. They differ in speed and in numerics: AVX2 saturates
where VNNI and NEON do not, which is why the tier-3 tests calibrate their
tolerance per class. A number on this page is a number for one kernel class on
one machine. It says nothing about "x64" or "arm64" in general, and nothing
about a phone. Hosted CI runners are a mix of classes, so the workflow prints
the CPU and its int8 flags beside every run.

## Running it

Locally, from the repository root (the SQLite benchmarks need the native library
in `foundation/native/artifacts/<rid>`; see
[building the native library](../../foundation/native/README.md)):

```
dotnet run --project benchmarks/Qavren.Edge.Benchmarks -c Release -p:TargetFrameworks=net10.0
dotnet run --project benchmarks/Qavren.Edge.Benchmarks -c Release -p:TargetFrameworks=net10.0 -- --list flat
dotnet run --project benchmarks/Qavren.Edge.Benchmarks -c Release -p:TargetFrameworks=net10.0 -- --filter *VecKnn*
dotnet run --project benchmarks/Qavren.Edge.Benchmarks -c Release -p:TargetFrameworks=net10.0 -- --full
```

`-p:TargetFrameworks=net10.0` keeps the restore to the host framework: the
packages the suite references also target Android, iOS and Mac Catalyst, and
without it `dotnet run` asks for those workloads. The suite passes the same
property to the child build BenchmarkDotNet generates for every benchmark.

The default job is `ShortRun`: 3 warm-ups, 3 iterations, 1 launch. It is fast
and noisy. `--full` uses BenchmarkDotNet's default job, which runs until the
statistics settle. Any other argument goes to BenchmarkDotNet. Reports land in
`artifacts/benchmarks/results/` as JSON and GitHub Markdown.

The Embeddings and Chat classes run only when their model is staged, with the
same variables the tier-3 tests read. Without a model the run prints one line
per skipped area, for example
`skipped: Embeddings (QAVREN_EDGE_MODEL_DIR unset ...)`.

- `QAVREN_EDGE_MODEL_DIR`: a folder holding `onnx/model_qint8_arm64.onnx` and
  `vocab.txt` from `sentence-transformers/all-MiniLM-L6-v2` at revision
  `1110a243fdf4706b3f48f1d95db1a4f5529b4d41`.
- `QAVREN_EDGE_CHAT_MODEL_DIR`: the six files of
  `Arm/qwen3-0-6b-onnx-genai-int4-kquantlast-emb-int4` at revision
  `c1d7bbbbb20630eef24c00d8ad18250bd57b232c`.

The pinned SHA-256 of each file is in
[`benchmarks.yml`](https://github.com/qavren-oss/qavren-edge/blob/main/.github/workflows/benchmarks.yml).

In CI, the
[`benchmarks.yml`](https://github.com/qavren-oss/qavren-edge/blob/main/.github/workflows/benchmarks.yml)
workflow runs the suite on `ubuntu-24.04` and on `macos-15` (arm64, the NEON
class). It runs on manual dispatch only, never on a pull request or a schedule:

```
gh workflow run benchmarks.yml --ref main -f full=false -f models=true
```

Each leg writes its tables to the job summary and uploads
`artifacts/benchmarks/` as the `benchmarks-<os>` artifact.

## Results

Two ShortRun measurements taken on 2026-09-23, one per int8 kernel class the
suite runs on locally. Treat both as rough. ShortRun keeps three iterations,
and the error column BenchmarkDotNet reports (half the 99.9% confidence
interval) is often as large as the mean. Every row says whether it was
measured. The hosted-runner legs of `benchmarks.yml` have not run yet.

### AMD Ryzen 7 5825U (x64, AVX2)

AMD Ryzen 7 5825U, Zen 3, AVX2 without VNNI, Windows 11, .NET 10.0.401,
2026-09-23, ShortRun. One run of the command above with both models staged.
The encode row comes from a re-run of that one class the same day, after a fix
to the throughput column. Other builds were running on the machine during the
run, so this table is noisier than the Apple one.

| Area | Benchmark | Parameters | Mean | Throughput | Measured |
|---|---|---|---:|---:|---|
| SQLite | vec0 KNN, k = 10 | 1 000 rows, chunk 256 | 0.41 ms | 2.4 M rows/s scanned | yes |
| SQLite | vec0 KNN, k = 10 | 1 000 rows, chunk 1 024 | 0.74 ms | 1.4 M rows/s scanned | yes |
| SQLite | vec0 KNN, k = 10 | 10 000 rows, chunk 256 | 14.9 ms | 0.67 M rows/s scanned | yes |
| SQLite | vec0 KNN, k = 10 | 10 000 rows, chunk 1 024 | 12.7 ms | 0.79 M rows/s scanned | yes |
| SQLite | vec0 KNN, k = 10 | 100 000 rows, chunk 256 | 121.7 ms | 0.82 M rows/s scanned | yes |
| SQLite | vec0 KNN, k = 10 | 100 000 rows, chunk 1 024 | 115.6 ms | 0.86 M rows/s scanned | yes |
| SQLite | FTS5 MATCH, top 10 | 1 000 rows | 20 µs | | yes |
| SQLite | FTS5 MATCH, top 10 | 10 000 rows | 110 µs | | yes |
| SQLite | FTS5 MATCH, top 10 | 100 000 rows | 365 µs | | yes |
| SQLite | vec0 insert, one transaction | 1 000 rows, chunk 256 | 15.5 ms | 64 000 rows/s | yes |
| SQLite | vec0 insert, one transaction | 1 000 rows, chunk 1 024 | 21.4 ms | 47 000 rows/s | yes |
| SQLite | vec0 insert, one transaction | 10 000 rows, chunk 256 | 179.7 ms | 56 000 rows/s | yes |
| SQLite | vec0 insert, one transaction | 10 000 rows, chunk 1 024 | 198.5 ms | 50 000 rows/s | yes |
| Vector store | `SearchAsync` | 10 000 records | 11.5 ms | | yes |
| Vector store | `SearchAsync` + filter | 10 000 records | 10.6 ms | | yes |
| Vector store | `HybridSearchAsync` | 10 000 records | 29.3 ms | | yes |
| Vector store | `HybridSearchAsync` + filter | 10 000 records | 104.1 ms | | yes |
| Ingestion | plain-text extraction | 200 documents | 37.7 ms | 5 300 docs/s | yes |
| Ingestion | Markdown extraction | 200 documents | 42.1 ms | 4 700 docs/s | yes |
| Ingestion | plain chunker | 200 documents | 137 ms | 1 460 docs/s | yes |
| Ingestion | Markdown heading chunker | 200 documents | 156 ms | 1 280 docs/s | yes |
| Ingestion | token-window chunker | 200 documents | 919 ms | 218 docs/s | yes |
| Ingestion | content hash (xxHash128) | 200 documents | 43 µs | 4.7 M docs/s | yes |
| Ingestion | pipeline re-run, unchanged corpus | 200 documents | 59.7 ms | 3 350 docs/s | yes |
| Embeddings | WordPiece encode | 32 sentences | 81 µs | 395 000 sentences/s, 4.9 M tokens/s | yes |
| Embeddings | `GenerateAsync`, int8 MiniLM | batch 1 | 6.7 ms | 150 sentences/s, 1 950 tokens/s | yes |
| Embeddings | `GenerateAsync`, int8 MiniLM | batch 8 | 29.2 ms | 274 sentences/s, 3 420 tokens/s | yes |
| Embeddings | `GenerateAsync`, int8 MiniLM | batch 32 | 117.1 ms | 273 sentences/s, 3 370 tokens/s | yes |
| Chat | Qwen3 0.6B int4, 120-token prompt, 1-token answer | | 939 ms | about 130 prompt tokens/s | yes |
| Chat | Qwen3 0.6B int4, 120-token prompt, 64-token answer | | 2 402 ms | 43 decode tokens/s | yes |

### Apple M4 (arm64, NEON)

Apple M4 (Mac Mini, 16 GB), macOS 27.0, .NET 10.0.401, 2026-09-23, ShortRun.
This is the NEON int8 class the product ships on. One run of the same command.

| Area | Benchmark | Parameters | Mean | Throughput | Measured |
|---|---|---|---:|---:|---|
| SQLite | vec0 KNN, k = 10 | 1 000 rows, chunk 256 | 0.25 ms | 4.0 M rows/s scanned | yes |
| SQLite | vec0 KNN, k = 10 | 1 000 rows, chunk 1 024 | 0.30 ms | 3.3 M rows/s scanned | yes |
| SQLite | vec0 KNN, k = 10 | 10 000 rows, chunk 256 | 3.79 ms | 2.6 M rows/s scanned | yes |
| SQLite | vec0 KNN, k = 10 | 10 000 rows, chunk 1 024 | 3.68 ms | 2.7 M rows/s scanned | yes |
| SQLite | vec0 KNN, k = 10 | 100 000 rows, chunk 256 | 38.9 ms | 2.6 M rows/s scanned | yes |
| SQLite | vec0 KNN, k = 10 | 100 000 rows, chunk 1 024 | 38.6 ms | 2.6 M rows/s scanned | yes |
| SQLite | FTS5 MATCH, top 10 | 1 000 rows | 7.8 µs | | yes |
| SQLite | FTS5 MATCH, top 10 | 10 000 rows | 21 µs | | yes |
| SQLite | FTS5 MATCH, top 10 | 100 000 rows | 170 µs | | yes |
| SQLite | vec0 insert, one transaction | 1 000 rows, chunk 256 | 7.4 ms | 136 000 rows/s | yes |
| SQLite | vec0 insert, one transaction | 1 000 rows, chunk 1 024 | 10.5 ms | 95 000 rows/s | yes |
| SQLite | vec0 insert, one transaction | 10 000 rows, chunk 256 | 74.8 ms | 134 000 rows/s | yes |
| SQLite | vec0 insert, one transaction | 10 000 rows, chunk 1 024 | 106.5 ms | 94 000 rows/s | yes |
| Vector store | `SearchAsync` | 10 000 records | 3.68 ms | | yes |
| Vector store | `SearchAsync` + filter | 10 000 records | 2.14 ms | | yes |
| Vector store | `HybridSearchAsync` | 10 000 records | 4.29 ms | | yes |
| Vector store | `HybridSearchAsync` + filter | 10 000 records | 14.1 ms | | yes |
| Ingestion | plain-text extraction | 200 documents | 16.3 ms | 12 300 docs/s | yes |
| Ingestion | Markdown extraction | 200 documents | 23.3 ms | 8 600 docs/s | yes |
| Ingestion | plain chunker | 200 documents | 65.3 ms | 3 060 docs/s | yes |
| Ingestion | Markdown heading chunker | 200 documents | 77.2 ms | 2 590 docs/s | yes |
| Ingestion | token-window chunker | 200 documents | 285 ms | 702 docs/s | yes |
| Ingestion | content hash (xxHash128) | 200 documents | 35 µs | 5.7 M docs/s | yes |
| Ingestion | pipeline re-run, unchanged corpus | 200 documents | 13.1 ms | 15 200 docs/s | yes |
| Embeddings | WordPiece encode | 32 sentences | 24 µs | 1.35 M sentences/s, 16.6 M tokens/s | yes |
| Embeddings | `GenerateAsync`, int8 MiniLM | batch 1 | 3.7 ms | 274 sentences/s, 3 560 tokens/s | yes |
| Embeddings | `GenerateAsync`, int8 MiniLM | batch 8 | 18.9 ms | 424 sentences/s, 5 300 tokens/s | yes |
| Embeddings | `GenerateAsync`, int8 MiniLM | batch 32 | 92.7 ms | 345 sentences/s, 4 250 tokens/s | yes |
| Chat | Qwen3 0.6B int4, 120-token prompt, 1-token answer | | 303 ms | about 400 prompt tokens/s | yes |
| Chat | Qwen3 0.6B int4, 120-token prompt, 64-token answer | | 791 ms | 129 decode tokens/s | yes |

The chat rows come from a second run of the chat class alone, a few minutes after the rest. The model finished staging on the Mini only after the main run had started.

### What the two runs show

- **vec0 chunk size (SP2 item 11).** On both machines the shipped 256 and
  sqlite-vec's 1 024 give the same query cost within noise at 10 000 and
  100 000 rows. 256 is ahead at 1 000 rows. On insert 256 is ahead on both
  machines, and clearly so on the M4 (about 135 000 rows/s against 94 000). The
  measurements back the shipped default of 256.
- **KNN cost is linear.** Past 10 000 rows a query scans about 2.6 million
  384-dimension rows per second on the M4 and about 0.8 million on the Ryzen.
  At 100 000 rows that is 39 ms and 116 to 122 ms per query.
- **The filter makes hybrid search slower.** On both machines a LINQ filter
  leaves `SearchAsync` no slower (on the M4 it is faster), but it makes
  `HybridSearchAsync` 3.3 to 3.6 times slower: 14.1 ms against 4.3 ms on the
  M4, and 104 ms against 29 ms on the Ryzen. This needs investigating before
  anything is claimed about it.
- **The token-window chunker costs 3.5 to 6 times** as much as the plain and
  heading chunkers on the same documents.
- **Encoder batching.** Throughput peaks at batch 8 on both machines: 424
  sentences/s on the M4 and 274 on the Ryzen. Batch 32 is no faster than
  batch 8, and on the M4 it is slower.
- **Chat.** The decode rate is `63 / (T64 - T1)`. That is about 129 tokens per
  second on the M4 (`63 / (791 - 303)` ms) and 43 on the Ryzen
  (`63 / (2 402 - 939)` ms). The client's own `ChatTurnStatus` agreed: 127.9
  and 43.9 tokens per second, with a time to first token of 370 ms and 929 ms.
  The M4 decodes about three times as fast as the Ryzen.
- **One read per file** held on both machines. The unchanged re-run opened each
  of the 200 files exactly once and embedded nothing.