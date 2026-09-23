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
dotnet run --project benchmarks/Qavren.Edge.Benchmarks -c Release
dotnet run --project benchmarks/Qavren.Edge.Benchmarks -c Release -- --list flat
dotnet run --project benchmarks/Qavren.Edge.Benchmarks -c Release -- --filter *VecKnn*
dotnet run --project benchmarks/Qavren.Edge.Benchmarks -c Release -- --full
```

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

Measured on AMD Ryzen 7 5825U, Zen 3, AVX2 without VNNI, Windows 11, .NET
10.0.401, 2026-09-23, ShortRun. The numbers come from one local run of the
command above with both models staged. The encode row is from a re-run of that
class alone on the same day, after a fix to the throughput column. Treat them as
rough. ShortRun keeps three iterations, and the error column (half the 99.9%
confidence interval) is often as large as the mean. Other builds were running on
the machine during the run. Rows marked "not yet measured" have not run
anywhere yet.

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
| All | `ubuntu-24.04` leg of `benchmarks.yml` | | | | not yet measured; run `benchmarks.yml` |
| All | `macos-15` (arm64, NEON) leg of `benchmarks.yml` | | | | not yet measured; run `benchmarks.yml` |

What this run shows, and what it does not:

- **vec0 chunk size (SP2 item 11).** The shipped 256 and sqlite-vec's 1 024
  are within each other's error bars at 10 000 and 100 000 rows, and 256 is
  ahead at 1 000 rows and on insert. Nothing here argues for changing the
  default. A `--full` run on the NEON class should settle it before 1.0.
- **KNN cost is linear.** Past 10 000 rows a query scans about 0.8 million
  384-dimension rows per second on this CPU, so 100 000 rows cost about 0.12 s
  per query. The 1 000-row figure is dominated by per-query overhead.
- **The filter makes hybrid search slower.** A LINQ filter makes `SearchAsync`
  no slower, but it makes `HybridSearchAsync` about 3.5 times slower (104 ms
  against 29 ms). That is a finding to investigate, not yet a claim.
- **The token-window chunker costs about six times as much** as the plain and
  heading chunkers on the same documents.
- **Batching the encoder pays off up to 8.** Batch 1 runs at 150 sentences per
  second and batch 8 at about 270. Batch 32 is no faster than batch 8 on this
  CPU.
- **Chat.** The decode rate is `63 / (2 402 - 939)` ms per token, about 43
  tokens per second. The client's own `ChatTurnStatus` for the same turn
  reported 43.9 tokens per second and a 929 ms time to first token. The
  prompt-processing rate is 120 tokens over roughly 0.9 s.
- **One read per file** held: the unchanged re-run opened each of the 200 files
  exactly once and embedded nothing.
