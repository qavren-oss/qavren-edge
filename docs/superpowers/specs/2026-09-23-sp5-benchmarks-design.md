# Sub-project 5, part 3: the benchmark suite

Date: 2026-09-23. Status: approved design.

Part 1 (the release path) shipped `v0.1.0-preview.1`; part 2 shipped the
documentation site. The root README's versioning section says 1.0 follows the
documentation site and the benchmark suite. This part is the benchmark suite.

## 1. Goal

One BenchmarkDotNet project that measures what a consumer of the packages gets,
through the same DI composition the READMEs document, and one manual workflow
that runs it on the two hardware classes that matter. Every number the
documentation quotes comes out of it, with the machine and the date beside it.
Before this part the suite quotes no throughput number anywhere, and the SP2
spec forbids one until it has been measured.

## 2. What exists

- SP2 spec `embeddings/docs/superpowers/specs/2026-09-11-sp2-embeddings-vectorstore-design.md`
  section 19, item 11: `ChunkSize = 256` for vec0 was reasoned, not benchmarked
  (upstream's performance guide is a stub), and "Benchmark before any
  rows-per-second number goes in the README." Item 13 (`PinnedSequenceLength`
  under CoreML) is a device measurement on the Mac Mini with the CoreML
  execution provider; it is out of scope here (section 5).
- The root README claims an unchanged corpus "costs one read per file". Nothing
  measures that today.
- Tier-3 tests already gate on staged models: `QAVREN_EDGE_MODEL_DIR` (layout
  `onnx/model_qint8_arm64.onnx` + `vocab.txt`, preset
  `EmbeddingPresets.MiniLmL6V2Int8`, `Tier3Host` builds the container over a
  `FileOnnxModelSource` so nothing downloads) and `QAVREN_EDGE_CHAT_MODEL_DIR`
  (the Qwen3 0.6B int4 GenAI folder, preset `ChatPresets.Qwen3_600MInt4`,
  `EdgeChatOptions.ModelDirectoryOverride`).
- The Actions spend rule: model lanes never run on `pull_request` or on a new
  schedule. `ci.yml`'s `model-tests` and `chat-model-tests` are guarded to
  `schedule`/`workflow_dispatch`, stay out of `ci-gate`, fetch a pinned model,
  verify its SHA-256 on every run, and cache it on the content hash.
- The int8 kernel classes: `ci.yml`'s "Runner CPU" step exists because the int8
  MiniLM graph's numerics and speed depend on the CPU's integer kernels. The
  AVX2 u8s8 path saturates where AVX-512 VNNI and ARM NEON do not, and hosted
  ubuntu runners are a mix of both. `embeddings/tests/.../Tier3` calibrates its
  reference-vector tolerance to those classes (PR #31). A benchmark number is
  therefore a number for one kernel class, never for "x64".
- `artifacts/` is gitignored at the repository root.

## 3. Design

### 3.1 Project

`benchmarks/Qavren.Edge.Benchmarks/Qavren.Edge.Benchmarks.csproj`: `net10.0`,
`OutputType Exe`, `IsPackable false`, in `QavrenEdge.slnx` under
`/benchmarks/`. `BenchmarkDotNet` 0.15.8 is pinned in `Directory.Packages.props`
under its own `Benchmarks` item group. It references the packages' projects,
not their internals: every benchmark composes the container with
`services.AddQavrenEdge(...)`, `UseSqliteNative()`, `AddSqlite`,
`AddVectorStore`, `AddIngestion`, `AddOnnxEmbeddings` and `AddOnnxChat`, and
calls only public API. Scratch databases live under `Path.GetTempPath()` and
are deleted in `GlobalCleanup`.

### 3.2 Benchmarks

All inputs are synthetic and generated from a fixed seed, so two runs measure
the same bytes. Vectors are 384-dimensional (MiniLM's width), unit length,
cosine distance.

| Area | Class | What it measures | Parameters |
|---|---|---|---|
| Sqlite | `VecKnnBenchmarks` | vec0 KNN, `k = 10`, through `Knn.QueryAsync` | rows 1 000 / 10 000 / 100 000; vec0 `chunk_size` 256 / 1 024 |
| Sqlite | `FtsMatchBenchmarks` | FTS5 `MATCH` for one term, top 10 by `bm25` | rows 1 000 / 10 000 / 100 000 |
| Sqlite | `VecInsertBenchmarks` | batched vec0 insert inside one transaction (`ExecuteInTransactionAsync`) | rows 1 000 / 10 000; `chunk_size` 256 / 1 024 |
| VectorData | `VectorSearchBenchmarks` | `SearchAsync` and `HybridSearchAsync` (RRF), each with and without a LINQ filter on an indexed property | 10 000 records, top 10 |
| Ingestion | `IngestionBenchmarks` | plain-text and Markdown extraction, the three chunkers, `ContentHash` over the corpus, and an incremental re-run of the pipeline over an unchanged folder | 200 documents |
| Embeddings (gated) | `EmbeddingBenchmarks` | WordPiece encode; `GenerateAsync` for batches of 1, 8 and 32 short sentences | batch 1 / 8 / 32 |
| Chat (gated) | `ChatBenchmarks` | one turn with a one-token answer (prompt processing) and one with a 64-token answer (decode) | none |

The `chunk_size` parameter is SP2 item 11: 256 is the shipped default, 1 024
is sqlite-vec's own default, and the pair is the comparison the item asks for.

The ingestion corpus is Markdown (headings, paragraphs, a list, a table, a code
fence). Extraction and chunking run on it directly; the chunkers run over the
Markdown extractor's output against `ChunkModelProfile.MiniLmL6V2Int8`'s budget.
The chunk tokenizer is `EdgeTokenCounter.CreateWordPiece` over a vocabulary
generated from the corpus's own word list, so the chunkers exercise the real
`Microsoft.ML.Tokenizers` WordPiece path without a model download. The pipeline
needs an `IEmbeddingGenerator`; the ingestion benchmarks register a
deterministic hashing generator so the numbers isolate extraction, chunking,
hashing and state, and the re-run embeds nothing by construction. `GlobalSetup`
checks that the re-run skips all 200 documents and counts how many times the
source is opened (the "one read per file" claim), and prints both.

Embeddings build the container exactly as `Tier3Host` does, over a
`FileOnnxModelSource` on the staged directory. Chat builds it as
`Tier3ChatHost` does, with `ModelDirectoryOverride`, greedy decoding and
`/no_think` in the system prompt, and a fresh conversation per turn so the
conversation cache never shortens a prompt. Decode tokens per second is
`63 / (T64 - T1)`; `GlobalSetup` also prints the client's own
`ChatTurnStatus` (time to first token, tokens per second) for one turn.

A custom summary column reports items per second where the item count is known:
rows for the SQLite and insert rows, documents for ingestion, sentences and
tokens for embeddings. The count comes from a benchmark parameter or from a
public static property on the benchmark class, computed deterministically on the
host from the same generator the benchmark uses.

### 3.3 Program and gating

BenchmarkDotNet has no skip. `Program.cs` selects the classes to run: the
Sqlite, VectorData and Ingestion classes always, Embeddings only when
`QAVREN_EDGE_MODEL_DIR` holds the MiniLM layout, Chat only when
`QAVREN_EDGE_CHAT_MODEL_DIR` holds `genai_config.json`. Each skipped area prints
one line, `skipped: Embeddings (QAVREN_EDGE_MODEL_DIR unset)`. `--list` sees
every class, gated or not, because listing runs nothing.

Defaults: the `ShortRun` job (3 warm-ups, 3 iterations, 1 launch) and a
`--filter *` when none is given, so a bare `dotnet run` does the CI run. `--full`
drops `ShortRun` for BenchmarkDotNet's default job; any other argument passes
through to `BenchmarkSwitcher`. Exporters: JSON (full) and GitHub Markdown, into
`artifacts/benchmarks/` at the repository root (found by walking up to
`QavrenEdge.slnx`). `MemoryDiagnoser` is on.

### 3.4 Workflow

`.github/workflows/benchmarks.yml`, `workflow_dispatch` only, inputs `full`
(boolean, default false) and `models` (boolean, default true). Never
`schedule`, never `pull_request`: a benchmark on a PR costs hosted minutes and
measures a runner drawn at random.

- `natives`: calls `native.yml` exactly as `ci.yml` does, with `contents: read`
  and `actions: read` (the reuse job's cache repair needs it).
- `bench`: `needs: natives`, matrix `ubuntu-24.04` and `macos-15` (arm64, the
  NEON class the product ships on), `fail-fast: false`, `timeout-minutes: 60`.
  Checkout, setup-dotnet from `global.json`, download `native-*` into
  `foundation/native/artifacts`, then, guarded by `inputs.models`, the MiniLM
  and Qwen3 cache, fetch and verify steps copied from `model-tests` and
  `chat-model-tests` (same variable names, revisions and hashes, same
  content-hash cache keys). A "Runner CPU" step prints the model name and the
  int8 kernel flags on Linux and `machdep.cpu.brand_string` on macOS. Then the
  suite runs (`--full` when asked), `artifacts/benchmarks/**` uploads as
  `benchmarks-<os>`, and every `*-report-github.md` is appended to
  `$GITHUB_STEP_SUMMARY`.
- The model variables are set only when `inputs.models` is true, so an
  unchecked box skips the gated classes rather than failing.

`assert-workflows.py` gains a "Sub-project 5 (benchmarks)" section: the file
exists; its only trigger is `workflow_dispatch`; its model cache keys contain
the content-hash variables; `ci.yml` does not reference it; `docs/site/toc.yml`
links the benchmarks page.

### 3.5 Documentation

`docs/site/benchmarks.md`, linked from `toc.yml` as "Benchmarks" between
"Errors" and "API reference" and listed in `docfx.json`'s first content group.
It says what each benchmark measures and why, why the numbers belong to a
kernel class, how to run the suite locally and dispatch the workflow, and gives
one results table from a real ShortRun on the maintainer's machine (AMD Ryzen 7
5825U, Zen 3, AVX2 without VNNI, Windows 11). Every row states whether it was
measured. `docs/maintainers.md` gains the `benchmarks/` layout row, the
workflow in the `.github/` row, and a CI-shape bullet.

## 4. Verification

- `dotnet build QavrenEdge.slnx -c Release` is green.
- `dotnet run --project benchmarks/Qavren.Edge.Benchmarks -c Release -- --list flat`
  lists every benchmark, the gated ones included.
- A full local ShortRun completes with both models staged, and its summary is
  the table on the docs page.
- `python foundation/tools/ci-checks/assert-workflows.py` passes.
- `dotnet format QavrenEdge.slnx --verify-no-changes --no-restore` is clean
  after a solution restore.
- `dotnet docfx docs/site/docfx.json --warningsAsErrors` is green.
- After merge: one dispatch of `benchmarks.yml` on both legs, its summaries
  linked from the docs page.

## 5. Out of scope

- SP2 item 13 (`PinnedSequenceLength` under CoreML): a device measurement with
  the CoreML execution provider, not a hosted-runner benchmark.
- Device benchmarks (Android, iOS, Mac Catalyst), SQLCipher, PDF and DOCX
  extraction, and memory-pressure behaviour.
- Regression gating. There is no baseline to gate against, and hosted runners
  are too noisy to gate a PR on; results are artifacts and a page.
- Any number in the root README. The docs page carries the numbers; the README
  is owned by another stream.

## 6. Risks

- Hosted runners are shared and heterogeneous: two dispatches can draw
  different CPUs. The "Runner CPU" step makes every run attributable; the page
  says the numbers are per class and per run.
- `100 000` rows at 384 dimensions is a 150 MB setup per benchmark process.
  If the ShortRun exceeds the job timeout, the top parameter drops to 50 000
  and the page says so.
- The ingestion chunk tokenizer's vocabulary is synthetic (every corpus word is
  one token). Chunk counts differ from MiniLM's real vocabulary; relative
  chunker cost does not depend on it, and the page says which vocabulary ran.
- The chat decode estimate assumes the 64-token turn runs to its cap. The
  prompt asks for a long enumeration so it does, and `GlobalSetup` prints the
  generated count so a short turn is visible.
