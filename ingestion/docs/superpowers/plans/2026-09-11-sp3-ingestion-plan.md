# Qavren.Edge Sub-project 3 — Ingestion: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `Qavren.Edge.Ingestion` plus four satellites — `.Pdf`, `.OpenXml`, `.Onnx`, `.DataIngestion` — an L2 recipe that turns a folder, a picker result or a single stream into a searchable SP2 collection, with content-hash incremental re-index, a budget that suspends on a committed boundary, and durable document-level state.

**Architecture:** One core package on top of SP2's `EdgeVectorStore` + `IEmbeddingGenerator` and SP1's `IEdgeDatabase`, lifecycle hub and diagnostics. The core owns the document model, the plain-text and Markdown extractors, the three chunkers, the token budget, xxHash128 content addressing, the recipe, the incremental diff, the runner, the chunk writer, the state schema, the lifecycle observer, the diagnostics contributor and the exceptions. `.Pdf` (Apache-2.0 PdfPig) and `.OpenXml` (14.66 MB DocumentFormat.OpenXml) are opt-in extractors. `.Onnx` is the ~200-line bridge to SP2's `IEdgeTokenizer` and `IEdgeResourceMonitor`. `.DataIngestion` is the prerelease MEDI shim. No satellite references another.

**Tech stack (verified on this box — see Environment ground truth):** .NET SDK 10.0.401, C# 14, `Markdig` 1.3.2, `System.IO.Hashing` 10.0.12, `PdfPig` 0.1.16, `DocumentFormat.OpenXml` 3.5.1, `Microsoft.ML.Tokenizers` 2.0.0 (SP2's pin, reused), `Microsoft.Extensions.DataIngestion(.Abstractions)` 10.10.0-preview.1.26459.2, `CsCheck` 4.8.0, xunit.v3 3.2.2 on Microsoft.Testing.Platform, SP1 natives for vec0 + FTS5.

**Worktree:** `C:\Users\steve\projects\qavren-edge-sp3`, branch **`feat/sp3-ingestion`** (based on `feat/sp2-embeddings`). `C:\Users\steve\projects\qavren-edge` and the `-sp2` / `-sp4` worktrees are in use by other agents and **must never be touched**.

**Branch and commit model.** Implementers **never run git**. One **integrator** per wave owns every root file — `QavrenEdge.slnx`, `Directory.Packages.props`, `Directory.Build.props`, `.gitignore`, `.gitattributes`, `.github/**`, `THIRD-PARTY-NOTICES.md` — and commits the wave onto `feat/sp3-ingestion`. Tasks marked **(integrator)** are the integrator's; an implementer that finds itself wanting to edit a root file has found a plan bug, not a licence.

**Every wave has a parallel phase and a close, and wave 1 also has a prologue.** Inherited verbatim from SP2, for the same two measured reasons:

1. **A task that mutates a file every project imports cannot share a wave with a task that builds anything.** `Directory.Packages.props` is imported by `Qavren.Edge.Core`, so Task 1.1 is a **serial prologue**. Nothing else in this plan edits a root file a `dotnet build` reads; Task 2.2 edits `.github/**` and `foundation/tools/ci-checks/**` only.
2. **A task's verify writes `obj/` and `bin/` for every project in its graph, and MSBuild holds no cross-process lock on either.** Every verify in a parallel phase therefore carries **`-p:ArtifactsPath=D:\Local\Temp\qedge-sp3\w<wave>\t<task>`**, which redirects both `obj/` and `bin/` under a directory no other task touches. The close task deletes the scratch roots and re-runs **every** verify **sequentially, without `ArtifactsPath`**, in the real tree, and only then commits. A wave that passes only under isolation has not passed.

**One more rule this plan adds, and it is the shape of the whole wave map: exactly one task per wave may write `ingestion/src/Qavren.Edge.Ingestion`, and no other task in that wave may compile it.** Folder-disjointness does not help here — a sibling task that merely *references* the core compiles whatever half-written state the core is in. The core is written across four waves (2, 3, 4, 5), each of which is therefore thin by design; every task that consumes the core lives in wave 6 or later, where the core is frozen. Waves 3, 4 and 5 have a single implementer and say so. This costs wall-clock and buys the only thing that matters here: a wave close that means something.

**Scratch roots.** `D:\Local\Temp\qedge-sp3\w<wave>\t<task>`. On a box where `D:` is unavailable, `C:\Users\steve\AppData\Local\Temp\qedge-sp3\…` substitutes; the path is arbitrary, the disjointness is not.

**Convention for "literal code".** Every artifact this plan *originates* is literal and complete: the csproj files, the `Directory.Packages.props` block, the `.slnx` entries, the workflow YAML, the `assert-workflows.py` additions, **the whole fixture corpus — the twelve text and Markdown files character for character with their measured byte lengths and SHA-256s, and all forty-seven DOCX part-XML files with the expected extraction output of each tree** — the fixture generator, the state DDL, the three `IngestionBudget` presets, the shared token-index search, the `EdgeVectorStoreCollectionOptions` projection, and every expected value that was measured rather than guessed.

A fixture is the sharpest case of this rule and the one an earlier draft got wrong twice. A test fixture described in prose — "the obvious minimal document", "a tree proving `w:txbxContent` is reached" — is an artifact the implementer invents, and a test over an invented input measures the invention. Worse, when the plan also pins that fixture's SHA-256, the task's own verify becomes **unsatisfiable**: nobody can author bytes that hash to a given digest from a description. So a fixture is either written out in full here with a measured digest, or it is not digest-pinned at all. There is no third option and no fixture in this plan takes one.

**Public API surfaces are adopted BY REFERENCE.** Where the spec declares a type as literal C# — §7.1, §7.4, §7.5, §8.1, §9.2, §10.2, §11 — the plan says "exactly as §N declares it" and the implementer transcribes that block **verbatim, XML docs included**. The spec is 3,084 lines of normative prose and retyping its declarations here would create a second source of truth that can drift. Two obligations come with the rule and both are load-bearing:

1. **Every public type the spec declares is NAMED by exactly one task.** A type nobody names is a type nobody writes, and that failure presents as a compile error three waves later.
2. **Every default that changes behaviour is restated in the owning task**, under a **Defaults that change behaviour** heading, and is either asserted by a test in the same task or named in the README. Those headings appear in Tasks 2.1 (`ChunkOptions`, `IngestionOptions`, `ExtractionOptions`), 3.1 (`PlainTextExtractorOptions`, `MarkdownExtractorOptions`), 5.1 (`IngestionRunOptions`, the budget presets) and 6.1 (`PdfExtractorOptions`, `DocxExtractorOptions`).

Method **bodies** of the larger internal implementations are specified by their declared signature, their stated behaviour and the spec section that defines them, rather than transcribed. Where a body has a trap in it — the `_au` trigger's cost, the `WITHOUT ROWID` state tables, the two-call `OpenAsync`, the counted-read ceiling, the state-row-last ordering, the FTS5 merge spelling — the trap is restated inside the task, because that is exactly what a reader of the task alone would otherwise get wrong.

---

## Environment ground truth (this box)

Everything in this table was measured on 2026-09-11 in the `feat/sp3-ingestion` worktree, not assumed.

| Fact | Value |
|---|---|
| Worktree / branch | `C:\Users\steve\projects\qavren-edge-sp3` on `feat/sp3-ingestion`, clean, `ingestion/` untracked, head `4fe723a feat(sp2): sample app Embeddings and Search pages plus Diagnostics toggles` |
| .NET SDK | 10.0.401 (`global.json`, `rollForward: latestFeature`), MTP runner selected repo-wide (`TestingPlatformDotnetTestSupport=true` in `Directory.Build.props`) |
| Workloads | android, ios, maccatalyst, maui-windows installed; restore for the Apple TFMs succeeds on this Windows host |
| Repo-wide compile settings | `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`, `GenerateDocumentationFile=true`, `NoWarn=NU5127;CS1591`, `InvariantGlobalization=true`. `IsPackable=true` pulls in `IsAotCompatible`, `EnablePackageValidation` and MinVer from `Directory.Build.targets` |
| Build isolation | `Directory.Build.props` sets neither `ArtifactsPath` nor `UseArtifactsOutput` (verified), so `-p:ArtifactsPath=…` is free for the parallel phases |
| Existing migration versions in the sample | `M001_CreateNotes` is 1 and `AddVectorCollectionMigration<string, Note>` is 2 — so the sample's `AddIngestion` claims **3 and 4** (plan adjustment 3) |
| SP1 natives on disk | `foundation\native\artifacts\win-x64\{qedge_sqlite3.dll, qedge_sqlcipher.dll}` |
| ORT CPU EP | runs locally |
| NuGet global packages | `D:\packages\nuget` |
| **Python** | **3.14.5 at `C:\Python314\python.exe`** (also first on `PATH`), and **`PyYAML` 6.0.3 imports** — measured 2026-09-11 with `python -V` and `python -c "import yaml; print(yaml.__version__)"`. Both verify commands that shell out to Python (Task 2.2 Step 6, Task 9.1 Step 4) and `make_pdf_fixtures.py` (Task 1.3, stdlib only) are therefore runnable here. `make_pdf_fixtures.py` needs CPython **3.11 or newer** for `bytes.__mod__` and `pathlib`; 3.14.5 satisfies it |
| **`bert-base-uncased` `vocab.txt`** | **PRESENT** at `C:\Users\steve\AppData\Local\Temp\qedge-model\vocab.txt` — **231,508 bytes**, SHA-256 `07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3`, matching SP2's `QAVREN_EDGE_VOCAB_SHA256` exactly (`certutil -hashfile`, 2026-09-11). It sits beside `onnx/` in SP2's model cache. This is the vocabulary Task 2.1 Step 1's probe and **every golden file Task 4.1 writes** are generated against, and it is the one Environment fact that was previously left open — see the re-provisioning command below |
| NOT available | macOS / Xcode; Android emulator; any workflow run (implementers never push) |

### The real vocabulary — §14.1's precondition, CLOSED here

Every committed golden is generated against `MlChunkTokenizer` over the real 30,522-entry
`bert-base-uncased` vocabulary. That file is **on this box** (row above), so the precondition is
met rather than hoped for — but it lives under `%LOCALAPPDATA%\Temp`, which a disk cleanup can
empty, so the plan pins both the path and the way to get it back. Tasks 2.1 and 4.1 resolve it as:

1. `%QAVREN_EDGE_VOCAB%` when set (an explicit override), else
2. `%QAVREN_EDGE_MODEL_DIR%\vocab.txt` when that variable is set (SP2's own convention), else
3. `C:\Users\steve\AppData\Local\Temp\qedge-model\vocab.txt` (where it is today).

If none of the three exists, **re-provision it** — one command, 231 KB, no build step, the same URL
and the same digest `ci.yml`'s `model-tests` job uses:

```powershell
$dir = "C:\Users\steve\AppData\Local\Temp\qedge-model"
New-Item -ItemType Directory -Force $dir | Out-Null
$rev = '1110a243fdf4706b3f48f1d95db1a4f5529b4d41'
Invoke-WebRequest -UseBasicParsing `
  "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/$rev/vocab.txt" `
  -OutFile "$dir\vocab.txt"
$sha = (Get-FileHash "$dir\vocab.txt" -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sha -ne '07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3') {
  throw "vocab.txt sha256 $sha - refusing to generate goldens against an unverified vocabulary"
}
Write-Host "OK vocab.txt 231508 bytes, sha256 verified"
```

**There is no toy-vocabulary fallback for the goldens, and that is deliberate** (plan adjustment 23).
A golden generated against a 23-entry probe vocabulary pins boundaries no shipped configuration
produces — it would be a test that ratifies a fiction. If the command above cannot run (no network),
**wave 4 stops and the plan is blocked at Task 4.1 Step 0**; it does not proceed with twenty absent
goldens and a verify that cannot pass. The file is committed to nothing and downloaded once.

### SP3's five runtime packages — restore and asset resolution, CLOSED here

A throwaway library targeting `net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst;net10.0-windows10.0.19041.0` referencing all five, restored on this box:

```
                                net10.0 / android / ios / maccatalyst / windows  — all five TFMs identical
Markdig/1.3.2                   -> lib/net10.0/Markdig.dll
System.IO.Hashing/10.0.12       -> lib/net10.0/System.IO.Hashing.dll
DocumentFormat.OpenXml/3.5.1    -> lib/net10.0/DocumentFormat.OpenXml.dll
PdfPig/0.1.16                   -> lib/net9.0/{UglyToad.PdfPig, .Core, .Fonts, .Tokens, .Tokenization,
                                              .DocumentLayoutAnalysis, .Package}.dll
Microsoft.ML.Tokenizers/2.0.0   -> lib/net8.0/Microsoft.ML.Tokenizers.dll
```

**PdfPig resolves `lib/net9.0` on every one of the five TFMs, never `lib/netstandard2.0`** — §15's asset assertion is a standing guard for a future SDK bump, not a present fault, and unlike ORT's native package `PdfPig` itself carries the `compile` assets, so the assertion can be written against that id directly.

Declared dependencies on `net10.0`: `Markdig` **none**, `System.IO.Hashing` **none**, `PdfPig` **none**, `DocumentFormat.OpenXml` → `DocumentFormat.OpenXml.Framework` 3.5.1 → **`System.IO.Packaging` 10.0.2**. The full new-to-this-repo closure the five packages add is eight entries: the four above plus `DocumentFormat.OpenXml.Framework`, `System.IO.Packaging` 10.0.2, and — already present through SP2 — `Google.Protobuf` 3.30.2 (a dependency of `Microsoft.ML.Tokenizers` 2.0.0, not of OnnxRuntime as SP2's plan supposed). Central transitive pinning stays off, so none of these is promoted into a produced nuspec.

### `SpecialTokenOverhead` — §17 item 2, CLOSED here: **2**

Measured against the shipped `Microsoft.ML.Tokenizers` 2.0.0 assembly with a `BertTokenizer` over a `bert-base-uncased`-shaped vocab:

```
"hello world"          CountTokens=2  EncodeToIds(default)=4  addSpecialTokens:true=4  addSpecialTokens:false=2  -> overhead 2
"the quick brown fox"  CountTokens=4  EncodeToIds(default)=6  addSpecialTokens:true=6  addSpecialTokens:false=4  -> overhead 2
"a"                    CountTokens=1  EncodeToIds(default)=3  addSpecialTokens:true=3  addSpecialTokens:false=1  -> overhead 2
```

`BertTokenizer` declares `EncodeToIds(string, bool addSpecialTokens, bool considerPreTokenization = true, bool considerNormalization = true)` **and** overrides the base three-argument `EncodeToIds(string, bool considerPreTokenization, bool considerNormalization)` so that the default path adds specials; `CountTokens` is not overridden and reports the WordPiece count only. The asymmetry §8.1 predicts is real, the overhead is exactly 2, and **the two pinned triples stand: 222 / 32 / 27 on MiniLM-int8 and 478 / 64 / 59 on bge-small.** Recomputed from §8.1's truncating rules: `256 − 2 − 32 − 0 = 222`, `222*15/100 = 33 → 33/8*8 = 32`, `222/8 = 27`; `512 − 2 − 32 − 0 = 478`, `478*15/100 = 71 → 71/8*8 = 64`, `478/8 = 59`.

### `GetIndexByTokenCount` — §17 item 11, and the one place the spec is wrong

Measured on the same assembly, over `"the quick brown fox jumps over the lazy dog hello world"` (55 chars, 11 tokens):

| maxTokenCount | default (`considerNormalization: true`) | `considerNormalization: false` |
|---|---|---|
| 1 | index 3, tokenCount 1 | index **55**, tokenCount **1** |
| 2 | index 10, tokenCount 2 | index **55**, tokenCount **1** |
| 3 | index 16, tokenCount 3 | index **55**, tokenCount **1** |
| 5 | index 26, tokenCount 5 | index **55**, tokenCount **1** |
| 8 | index 40, tokenCount 8 | index **55**, tokenCount **1** |
| 11 | index 55, tokenCount 11 | index **55**, tokenCount **1** |

Two facts fall out, and they point in opposite directions.

- **The spec's diagnosis is correct.** At the default, the returned index is into the *normalised* copy. For the NFD input `"cafe\u0301 cafe\u0301 cafe\u0301 hello world"` (29 chars) the call returned `normalizedText = "café café café hello world"` (26 chars) and `index = 15`; `original[..15]` is `"café café caf"` — a boundary landing mid-word. Applying that index to the original silently mislocates every offset after the first difference, which is exactly what §8.1 says.
- **The spec's remedy does not work.** `considerNormalization: false` does not mean "the same tokenization, indexed into the original". It returned the whole string for every budget from 1 to 11. A `MlChunkTokenizer` built on that one call would emit the entire document as a single chunk for any budget, which `ChunkExceedsTokenBudget` (6151) would then throw on — i.e. the ONNX-free path would not work at all.

The substitute is measured, not assumed: a **prefix scan over `CountTokens` at the tokenizer's own defaults reproduces the default call's answers exactly** — largest prefix at max 3 is 16 chars, at max 5 is 26, at max 8 is 40, matching the table above column for column — and its index is into the string as passed *by construction*, because it only ever measures prefixes of the original. That is plan adjustment 1.

Caveat recorded honestly: the probe used a 23-entry toy vocabulary, so the `considerNormalization: false` column may be vocabulary-dependent (a real 30,522-entry vocab would WordPiece an un-pre-tokenized run into many pieces rather than one). Task 2.1 Step 1 re-runs the identical probe against the real `bert-base-uncased` `vocab.txt` SP2 already pins (231,508 bytes, SHA-256 `07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3`) and records the result in the task. **The shared prefix search is correct either way**, which is why the adjustment does not wait on that re-run.

### MEDI — §17 item 8, CLOSED here: **yes, zero dependencies**

`Microsoft.Extensions.DataIngestion.Abstractions` 10.10.0-preview.1.26459.2 on `net10.0` resolves `lib/net10.0/…Abstractions.dll` and its target entry carries **no `dependencies` key at all**. The shim's isolation claim holds: one prerelease reference, nothing behind it.

The *implementation* package's measured `net10.0` closure is **twelve** packages, not the eight §14.4 lists:

```
Microsoft.Extensions.DataIngestion/10.10.0-preview.1.26459.2
  -> Microsoft.Extensions.AI 10.10.0, Microsoft.Extensions.DataIngestion.Abstractions,
     Microsoft.Extensions.Logging.Abstractions 10.0.12,
     Microsoft.Extensions.VectorData.Abstractions 9.7.0,   <-- BELOW this repo's 10.10.0
     Microsoft.ML.Tokenizers 1.0.1,                        <-- BELOW this repo's 2.0.0
     System.Numerics.Tensors 10.0.12
transitively also: Microsoft.Extensions.AI.Abstractions, Microsoft.Extensions.Caching.Abstractions,
     Microsoft.Extensions.DependencyInjection.Abstractions, Microsoft.Extensions.Primitives,
     Google.Protobuf
```

`System.Diagnostics.DiagnosticSource` and `System.Linq.AsyncEnumerable` — the two §14.4 calls out as "new surface" — do **not** appear. Two packages MEDI demands are *older* than this repo's pins, which is the safe direction: a project holding higher direct or project-carried references unifies upward with no `NU1605`. Proven by building a `net10.0` project with `TreatWarningsAsErrors=true` referencing `Microsoft.Extensions.DataIngestion` + `Microsoft.ML.Tokenizers` 2.0.0 + `Microsoft.Extensions.VectorData.Abstractions` 10.10.0 + `Markdig` 1.3.2 together: **build succeeded, zero errors, no `MSB3277`, no downgrade warning.** `Markdig.Signed` never enters the graph because `Microsoft.Extensions.DataIngestion.Markdig` is never referenced.

`CsCheck` 4.8.0 restores on `net10.0` and resolves `lib/net8.0/CsCheck.dll` with no dependencies.

### Third-party API surface — spot-checked against the shipped assemblies

- **PdfPig 0.1.16.** `PdfDocument.Open(Stream, ParsingOptions)` exists alongside `Open(string, …)`, `Open(byte[], …)` and `Open(ReadOnlyMemory<byte>, …)` — §7.4's "always the `Stream` overload" is expressible. `ParsingOptions` carries `SkipMissingFonts`, `UseActualText`, `UseLenientParsing`, `Passwords` (`List<string>`), `MaxStackDepth`, `ClipPaths`, `FilterProvider`, `Logger` — every member `PdfExtractorOptions` forwards. `Page` carries `Number`, `Letters`, `Text`, `NumberOfImages`, `Operations`, `Paths`, `CropBox`, `MediaBox`, `Rotation` — so §7.4's scanned-page test (`Letters.Count == 0 && NumberOfImages > 0`) is exactly writable.
- **DocumentFormat.OpenXml 3.5.1.** `DocumentFormat.OpenXml.OpenXmlPartReader` and `DocumentFormat.OpenXml.Wordprocessing.OutlineLevel` both present.
- **Markdig 1.3.2.** `UsePipeTables(MarkdownPipelineBuilder, PipeTableOptions)`, `UseYamlFrontMatter`, `UsePreciseSourceLocation` all present as extension methods.

### SP1 and SP2 facts SP3 composes against — verified in source

| Fact | Where | Status |
|---|---|---|
| `EdgeBuilder` is a thin `IServiceCollection` wrapper | `foundation/src/Qavren.Edge.Core/Hosting/EdgeBuilder.cs` | as spec'd |
| `EdgeStartupOrder` = 0 / 10 / 100 / 1000 — order 400 is free and unowned | `Hosting/IEdgeStartupTask.cs` | as spec'd |
| `EdgeEventIds` occupies 100–500; SP2's `EdgeAiEventIds` 600–899 | `EdgeEventIds.cs` | 900–999 is free |
| `EdgeErrorCode` ends at `ReservedColumnName = 5213` | `EdgeErrorCode.cs` | append-only, as spec'd |
| `EdgeException.HelpLink` → `foundation/docs/errors.md#<code>`, and that file exists with anchors through `## 5213` | `EdgeExceptions.cs`, `foundation/docs/errors.md` | SP3 appends 6000–6299 |
| `AddMigrations(IEnumerable<IEdgeMigration>, databaseName)` is public and AOT-safe | `SqliteEdgeBuilderExtensions.cs` | SP3's state migration uses it |
| `IEdgeDatabase.ExecuteInTransactionAsync` and `OpenConnectionAsync` | `IEdgeDatabase.cs` | as spec'd |
| `FtsTable`'s `_au` trigger is `AFTER UPDATE ON "<contentTable>"`, unqualified, doing an FTS5 delete-then-insert | `Fts/FtsTable.cs` | §9.5's repair cost is real |
| `EdgeCollectionModelBuilder` is **public**; `BuildDynamic(definition, generator)` is the AOT-safe model build SP2 itself uses | `Internal/EdgeCollectionModelBuilder.cs`, `EdgeVectorDataBuilderExtensions.cs` | §17 item 1 half one: **resolves** |
| `EdgeVectorSchema(CollectionModel, string, EdgeVectorStoreOptions?, bool alwaysCreateFullTextIndex)` is public, opens nothing, emits `\n` line endings | `EdgeVectorSchema.cs` | as spec'd |
| `collection.GetService(typeof(EdgeVectorSchema))` returns the schema, built in the constructor with **no connection** | `EdgeVectorStoreCollection.cs` | §17 item 1 half two: **resolves** |
| `EdgeVectorCollectionSchemaFactory.ToStoreOptions` is `internal`; `EdgeVectorCollectionRegistry` is `internal`; `SchemaOnlyEmbeddingGenerator` is `internal` | `Internal/EdgeVectorCollectionMigration.cs` | SP3 reimplements one, cannot reach the other two |
| **`EdgeVectorStoreCollectionOptions.RemoveDiacritics` is `internal`**, and `Qavren.Edge.VectorData` grants **no** `InternalsVisibleTo` | `EdgeVectorStoreOptions.cs`, no `AssemblyInfo.cs` | plan adjustment 4 |
| `AddVectorCollectionMigration(version, name, definition, databaseName, configure)` — public, definition-based, registers the collection in SP2's registry | `EdgeVectorDataBuilderExtensions.cs` | plan adjustment 2 takes it |
| SP2's FTS5 merge is `INSERT INTO "<t>"("<t>", rank) VALUES ('merge', 500)` — **two columns**, bounded to 4 iterations / 2 s, registered at index 0 | `Internal/VectorDataLifecycleObserver.cs` | §12's one-column spelling is not valid SQL |
| `BertEdgeTokenizer.CountTokens` forwards `_tokenizer.CountTokens(text)` verbatim; `IndexByTokenCount` forwards `GetIndexByTokenCount(text, maxTokens, out _, out tokenCount)` with normalisation left on | `Internal/BertEdgeTokenizer.cs:137` | §8.1's citation is exact |
| `OnnxEmbeddingGenerator.GetService(typeof(EmbeddingPreset))` returns the preset; `results.Usage = new UsageDetails { InputTokenCount = tokens }` is always set; `ApplyPrefix` is verbatim as §8.3 quotes it | `OnnxEmbeddingGenerator.cs` | §17 item 13's third part: **`Usage` is non-null** |
| `EmbeddingPreset.Pooling` is the `EmbeddingPooling` enum `{ Mean, Cls }`; ids are lower-case; nomic carries both prefixes; bge carries the query instruction only | `EmbeddingPresets.cs` | §11's `ChunkModelProfile` constants are correct as written |
| `SqliteTypeMap.SupportedDataTypes` — `byte[]` is the only collection type | `Internal/SqliteTypeMap.cs` | §8.3's breadcrumb-as-TEXT decision stands |

**Consequences.** Every host-lane verify in this plan runs here. Apple compilation and execution, Android device execution, every workflow run, the tier-3 nightly lanes and §17's device measurements are CI-only (see **CI-only work**). Implementers **must not run git**. **Never use `cd`** in a shell command — it wipes PATH in this environment. PowerShell is the primary shell and every path is absolute.

---

## Spec adjustments

Every place this plan departs from the approved spec is enumerated here — whether because verified research contradicted the spec (**the facts win**) or because the spec's wording is unimplementable as written and the plan does the nearest correct thing. Each adjustment names the task that implements it. A difference between spec and plan that is **not** in this list is a bug in the plan.

1. **`IChunkTokenizer.IndexByTokenCount` has ONE implementation, a bounded prefix search over `CountTokens`, shared by both tokenizers. `GetIndexByTokenCount` is not called at all.** §8.1 designs two derivations: `MlChunkTokenizer` calls `GetIndexByTokenCount(…, considerNormalization: false)` directly, and `EdgeChunkTokenizer` binary-searches `CountTokens`. The first does not work — measured in **Environment ground truth**, `considerNormalization: false` returns `index = text.Length, tokenCount = 1` for every budget, so every chunk would be the whole document. The default is worse than useless for SP3's purpose in a different way: its index is into the normalised copy, which is the bug §8.1 exists to avoid. So the plan ships `internal static class TokenIndexSearch` — estimate a cut from a running chars-per-token ratio, snap candidates to grapheme boundaries with `StringInfo`, search within ±25% of the estimate, widen once if the window fails, and **verify the chosen cut** with a final `CountTokens` — and both `MlChunkTokenizer` and `EdgeChunkTokenizer` delegate to it with their own `CountTokens` delegate. The offset contract then holds by construction on both paths, and both count with exactly the settings the encoder encodes with. §17 item 11's fallback (an additive `IEdgeTokenizer.IndexByTokenCount` overload filed against SP2) is **withdrawn**: there is no overload of the underlying member that would help. (Tasks 2.1, 6.2.)

2. **The collection DDL goes through SP2's `AddVectorCollectionMigration`, and `AddIngestion` claims TWO consecutive migration versions.** §17 item 1 asks the plan to weigh the one-migration design against the two-version fallback and to record the reasoning rather than only the outcome. Both halves of the "does it resolve" question came back **yes** (see Environment ground truth), so this is a free choice — and the fallback wins on four counts:
   - It puts SP3's collection in `EdgeVectorCollectionRegistry`, which is the **only** writer SP2's lifecycle observer and diagnostics contributor read. SP2 therefore merges SP3's FTS5 sidecar and reports its tables, and §12's SP3-owned merge — `FtsMergeBudget`, `FtsMergePages`, `FtsMergeMaxIterations`, event 926, the ≤ 3 s sum validation and the whole second term of §12's budget table — is **deleted**. Four options and one event id fewer, on the observer that runs on a platform callback thread.
   - It deletes `IngestionSchemaOnlyGenerator` from the shipped surface of the migration path.
   - The core then names no `Microsoft.Extensions.VectorData.ProviderServices` type on the registration path, so **`Qavren.Edge.Ingestion` needs no `MEVD9001` suppression** and carries none of ADR 0007's churn exposure. (The 6011 check in Task 5.1 still builds a model, so the suppression lives there alone — see adjustment 5.)
   - Version bookkeeping gets stricter rather than looser: SP1's `SqliteRegistry.AddMigrationVersion` raises `MigrationVersionConflict` (3002) for a clash, which is the same diagnostic every other migration in the suite gets.

   The cost is exactly §11.1's one-version claim. `AddIngestion(int migrationVersion, …)` keeps its signature and its XML doc states that it claims **`migrationVersion` and `migrationVersion + 1`** — `N` for the collection, `N + 1` for the three state tables. `IngestionMigrationVersionConflict` (6007) keeps its meaning: a second `AddIngestion` for the same collection. (Tasks 5.1, 1.2.)

3. **The sample app's `AddIngestion` version is 3**, because `M001_CreateNotes` already claims 1 and `AddVectorCollectionMigration<string, Note>` claims 2 in `sample.db`; with adjustment 2 that means SP3 occupies 3 and 4. (Task 8.2.)

4. **`IngestionOptions` gains `FullTextRemoveDiacritics`, because `ConfigureCollection` physically cannot carry it.** §11.1 tells a consumer whose DDL and runtime collection disagree to "pass the same shaping values to `AddIngestion`'s `ConfigureCollection` that you passed to `AddVectorStore`". That remediation is impossible for one of the five shaping values: `EdgeVectorStoreCollectionOptions.RemoveDiacritics` is `internal` and `Qavren.Edge.VectorData` grants no `InternalsVisibleTo`, so nothing outside that assembly can set it. A consumer who writes `AddVectorStore(o => o.FullTextRemoveDiacritics = 1)` would hit `IngestionCollectionSchemaMismatch` (6011) at every start with no way to fix it. So `IngestionOptions` declares `public int FullTextRemoveDiacritics { get; set; } = 2;`, SP3's projection writes it onto the `EdgeVectorStoreOptions` it builds for the 6011 check, and the 6011 remediation names it specifically. (Tasks 2.1, 5.1.)

5. **SP3's five-line projection is six lines and must match SP2's `ToStoreOptions` exactly, brace-escaping included.** `EdgeVectorCollectionSchemaFactory.ToStoreOptions` escapes `{` and `}` in a table-name override before assigning it to a `*NameFormat` property, because those properties are composite format strings. A projection that forgets this turns a collection named `a{b}` into a `FormatException` at DDL time. The literal projection is in Task 5.1, and a test asserts SP3's emitted DDL is byte-identical to what `AddVectorCollectionMigration` registers for the same definition and options — which, under adjustment 2, is the same code path, making the test a guard on the projection rather than on two migrations. (Task 5.1.)

6. **SP3's FTS5 merge statement is deleted, not corrected — but the spelling is recorded because the README quotes it.** §12 writes `INSERT INTO "<fts>"("<fts>") VALUES('merge', FtsMergePages)`, which names one column and supplies two values and is a SQLite syntax error. SP2's working spelling is `INSERT INTO "<t>"("<t>", rank) VALUES ('merge', 500)`. Under adjustment 2 SP3 runs no merge of its own, so the statement disappears from the code; the README's "how index maintenance happens" paragraph quotes SP2's spelling. (Tasks 1.2, 5.1.)

7. **`IngestionLifecycleObserver` keeps its index-0 registration, and the reason is now stronger, not weaker.** §12 justifies inserting at index 0 partly by SP3's own merge, which adjustment 2 removes. The remaining reason is the one that actually mattered: `EdgeLifecycleHub` awaits observers in registration order, and SP2's merge — which under adjustment 2 now runs against **SP3's own table** — must not run while SP3's runner is still writing to it. `[Ingestion, VectorData, Sqlite]` sets SP3's stop flag and waits for its checkpoint, then merges, then checkpoints WAL. The descriptor-scan idempotence guard is kept verbatim. (Task 5.1.)

8. **The two `ingestion/samples/` background wirings are single-platform class libraries and they ARE in the `.slnx`.** §4.2 calls them "documentation projects, not shipped packages … not in `ci-gate`'s path", which as written means nothing ever compiles them and they rot on the first API rename. They are instead `net10.0-android` and `net10.0-ios` class libraries with `IsPackable=false`, listed in `QavrenEdge.slnx` with the same `IsOSPlatform` TFM guards SP1 uses, so `dotnet restore QavrenEdge.slnx`, `dotnet format` and `dotnet pack` on the `windows-2025` leg all compile them. They add no test step, no device lane and no package. (Tasks 1.1, 8.3.)

9. **`assert-workflows.py`'s SP2 rule is generalised, not duplicated.** §15 asks for "every test project under `ingestion/tests/` appears as an explicit step". The existing rule globs `embeddings/tests/*/*.csproj`; the edit turns that into a loop over both roots and renames the loop variable off `rel`, which currently shadows the `release.yml` yaml document (harmless today, a trap tomorrow). The `ingestion/tests/fixtures/` directory holds no csproj, so the glob is safe. (Task 2.2.)

10. **Every verify command is `dotnet run --project <test csproj> -c Release -f net10.0 -p:TargetFrameworks=net10.0`, never `dotnet test`** — SP1 adjustment 36 and SP2 adjustment 12, unchanged. The two single-TFM test projects (`…Onnx.Tests`, `…DataIngestion.Tests`) take `-p:TargetFrameworks=net10.0` and **no** `-f`. **Their csprojs must spell the TFM as the plural `<TargetFrameworks>net10.0</TargetFrameworks>`** (as SP2's `VectorData.Conformance.Tests` does): against a singular `<TargetFramework>` the global property makes MTP's `buildMultiTargeting` targets generate a second entry point beside xunit.v3's — CS8892 (measured in wave 6, SDK 10.0.401). **In a parallel phase every one of them also carries `-p:ArtifactsPath=D:\Local\Temp\qedge-sp3\w<wave>\t<task>`**; the wave's close re-runs the identical commands without it. **Exception — a device-TFM build carries `-f <tfm>` and NO `-p:TargetFrameworks`**, because `-p:` is a global property that would force every `net10.0`-only project in the closure to a TFM it does not have (`NETSDK1005`). (Every task.)

11. **`Qavren.Edge.Ingestion.DataIngestion` is in the `.slnx` and packs, but `EnablePackageValidation` is turned off on it.** §15 says it is "excluded from package validation's baseline". There is no baseline to exclude it from — `PackageValidationBaselineVersion` is unset repo-wide, and with no baseline the validator's remaining job is the compatibility suppression file. A prerelease package whose only reference is itself prerelease needs neither, and leaving validation on for a package whose dependency is `10.10.0-preview.1.26459.2` is how a pack fails on a version-range technicality. The csproj sets `<EnablePackageValidation>false</EnablePackageValidation>` with the reason in a comment, and `<VersionSuffix>` is not set in the csproj — MinVer supplies the prerelease suffix from the tag, as it does for every other package here. (Task 1.1.)

12. **The golden files are written for `MaxTokens = 222` only, not for both pinned triples — and the set is exactly twenty, enumerated by name.** §14.1 asks for "one JSON per (fixture × chunker config)". Twelve fixtures × three chunkers × two model profiles is 72 committed JSON files for a 10 KB corpus, and the bge triple exercises no code path the MiniLM triple does not — the arithmetic difference between 222/32/27 and 478/64/59 is already pinned by a unit test that needs no fixture at all. So the committed set is:

    - **twelve `auto` goldens**, one per text/Markdown fixture, at the MiniLM triple. `empty.txt` and `whitespace-only.txt` are included and their goldens assert **zero chunks** — a golden saying "nothing" is the only thing that catches a chunker that starts emitting an empty chunk. Twelve fixtures under one chunker selection is **twelve files**, not seventeen; an earlier draft of this adjustment said seventeen and was simply wrong arithmetic.
    - **five option-variant goldens** on the Markdown fixtures under an explicit `markdown-heading`, covering the two options a consumer is most likely to flip: `IncludePreamble = false` on `headings.md` and `giant-heading-section.md` (2), and `PrependHeadingPath = false` on `headings.md`, `tables-lists.md` and `giant-heading-section.md` (3). These five are **authorised here**, not only in the task — the previous draft invented them in Task 4.1 with no adjustment behind them, which is exactly the spec/plan divergence this section exists to prevent.
    - **three explicit `token-window` goldens** on `long-token.txt`, `three-paragraphs.txt` and `giant-heading-section.md`, where the cascade itself is what is under test.

    12 + 5 + 3 = **twenty files**, each legible in a diff, each named in Task 4.1 Step 6. (Task 4.1.)

13. **The DOCX fixture count is eleven trees, and the localised-style fixture is one of them.** §14.2 says "ten committed part-XML trees plus one helper" and then lists ten names *plus* "a localised-style fixture". They are eleven: `headings`, `run-split`, `table`, `numbered-list`, `footnotes`, `header-footer`, `hyperlink`, `textbox`, `empty-body`, `unknown-style`, `localised-style`. (Tasks 1.3, 6.1.)

14. **`Qavren.Edge.Ingestion.Extractors.Tests` has one owner per wave, which puts the PDF and DOCX extractors in ONE task.** Two implementers writing two satellites whose tests share a csproj would each compile the other's half-written test files — `ArtifactsPath` separates outputs, not sources. The two extractors are one task (6.1) and the shared `DeterministicOpc` zip helper belongs to it. The same rule keeps Task 7.1 (integration, owns `Ingestion.Tests`) and Task 7.3 (device assertions, owns `Extractors.Tests`) in separate projects. (Tasks 6.1, 7.1, 7.3.)

15. **`trim-smoke` publishes twice from one project, switched by an MSBuild property.** §17 item 4 wants the core alone under the trimmer and then the core plus both satellites. Rather than a second console, `Qavren.Edge.TrimSmoke` gains a `ProjectReference` to `Qavren.Edge.Ingestion` unconditionally and to the two extractor satellites under `Condition="'$(QedgeTrimSatellites)' == 'true'"`. The `ci.yml` job publishes once without the property — that is the PR gate, and it runs the Markdown one-document path that puts Markdig under the trimmer — and once with it, `continue-on-error: true` for one release cycle, exactly as the existing AOT leg already is. (Tasks 1.1, 2.2, 7.2.)

16. **`ChunkModelProfile.MiniLmL6V2Fp32` is in the core's catalogue and in the drift test, even though §11 lists three named profiles plus nomic.** §11 declares all four; the point of restating it is that §17 item 12 says "every core profile" and the fp32 profile is the one a reader skips. All four are asserted field-for-field against `EmbeddingPresets` of the same name. (Task 6.2.)

17. **`foundation/docs/errors.md` gains 6000–6299 in Task 1.2, and the cross-check that every code has an anchor runs in the wave-1 close**, not inside either task — Task 1.2 (docs) and Task 1.4 (`EdgeErrorCode`) are parallel and neither may read the other's output. (Tasks 1.2, 1.4, 1.5.)

18. **Nothing in `ingestion/` is generated at build time and CI never runs a generator.** `make_pdf_fixtures.py` is the documented regeneration path for the seven committed ASCII PDFs, exactly as `make_tiny_model.py` and `fetch_preset_hashes.py` are for SP2's fixtures. Its venv is gitignored; its output is committed. (Tasks 1.1, 1.3.)

19. **All three state tables are `WITHOUT ROWID`, not only the composite-key one.** §6 says "all `WITHOUT ROWID` **where the key is composite**", and only `<p>_document` has a composite key. Task 2.1 Step 6's DDL nevertheless applies `WITHOUT ROWID` to `<p>_run` (`"run_id" TEXT PRIMARY KEY`) and `<p>_meta` (`"key" TEXT PRIMARY KEY`) as well, and this adjustment is where that choice is recorded rather than left as an unexplained difference between the spec's prose and the plan's literal SQL. The reason: a `WITHOUT ROWID` table with a `TEXT` primary key stores the row **in** the key's b-tree instead of a rowid table plus a separate unique index, which is strictly the right shape for two tables that are read by primary key, never scanned by rowid, and hold twenty rows and two rows respectively. Applying it only to `_document` would leave the other two paying for an index nothing uses. The consequence to watch: no `AUTOINCREMENT`, and every row must supply its primary key — both already true, because `run_id` is generated by the runner and `key` is a literal. **Task 9.1 Step 5 checks the plan's wording, not §6's**, and the golden-SQL test in Task 2.1 Step 9 is the authority. (Tasks 2.1, 9.1.)

20. **`IngestionBudget.Quick` and `Background` are pinned to literal values.** §10.2 documents them as "~20 s" and "~5 min" and §17 asks nothing about them, so two tildes were the only specification either the spec or the previous draft of this plan carried — and a preset nobody pins is a preset that quietly drifts between a doc comment and an implementation. The plan pins all three presets as literal initialisers in Task 2.1 Step 7, asserts them in `OptionsDefaultsTests`, and restates them under Task 5.1's **Defaults that change behaviour** heading because the runner is what meters them:

    ```csharp
    public static IngestionBudget Unlimited  { get; } = new();
    public static IngestionBudget Quick      { get; } = new() { MaxDuration = TimeSpan.FromSeconds(20) };
    public static IngestionBudget Background { get; } = new() { MaxDuration = TimeSpan.FromMinutes(5) };
    ```

    **Duration only, and the other three members stay null on purpose.** A `Quick` that also capped documents or chunks would suspend on a count that has nothing to do with the twenty-second slot it exists to fit, and a caller who wants a count cap can set one — `IngestionBudget` is an `init`-only record, so `IngestionBudget.Quick with { MaxDocuments = 50 }` is the composition path and it needs no second preset. (Tasks 2.1, 5.1.)

21. **§13.3's failure catalogue is missing rows for 6002 and 6102, and the plan supplies both.** §13.1 declares `IngestionCollectionNotConfigured = 6002` and `ExtractionFailed = 6102`; §13.3's table has a row for neither, so on the spec alone there is no condition, no tier and no behaviour for either — which is how a declared error code ends up with no raise site at all. The plan defines them, and Task 5.1 Step 5 implements both:

    | Code | Condition | Tier | Behaviour |
    |---|---|---|---|
    | 6002 | `RunAsync` / `PruneAsync` / `GetStatusAsync` / `RemoveSourceAsync` / `RemoveDocumentAsync` named a `collectionName` that no `AddIngestion` call configured | run | Throw before any enumeration, naming the requested name and listing the configured ones. The alternative is a run against a collection that does not exist, which surfaces as `IngestionStateMissing` (6201) and blames the schema for a typo. |
    | 6102 | `IDocumentExtractor.ExtractAsync` threw something that is **not** already one of 6103 / 6104 / 6106 / 6107 | document | Recorded `Failed`, original preserved as inner, `ExtractorId` set. One bad document fails one document, never a corpus. This is the catch-all that stops an extractor bug from becoming an unhandled run fault. |

    (Tasks 1.2, 5.1, 9.1.)

22. **6009, 6054, 6102 and 6002 each get a named implementation step and a named test, and the plan carries an error-code ownership table so the wave-9 gate is mechanical.** §13.3 specifies 6009 (`StrictRecipe` drift) and 6054 (a `DocumentId` yielded twice in one run) and the previous draft of this plan implemented neither — Task 5.1 had no `StrictRecipe` branch and no duplicate-id detection, while Task 9.1 Step 5 gated on "every `EdgeErrorCode` 6000–6299 is raised from somewhere". A gate whose subject nothing implements fails at the gate, which is the most expensive place to learn it. All four are now Task 5.1 Step 5's, and the **Error-code ownership** table below assigns a raise site and a test to every one of the thirty-six. (Tasks 5.1, 9.1.)

23. **A golden is never generated against a probe vocabulary, and the absence of the real one blocks wave 4 rather than degrading it.** §14.1 assumes the vocabulary is simply there. It is (**Environment ground truth**), and the re-provisioning command is pinned there too — but the previous draft paired "goldens are not written without it" with a Task 2.1 step that said "if the vocabulary is not on the box, say so and proceed", which left Task 4.1's verify ("twenty golden comparisons") and Task 9.1's gate ("twenty golden files") unsatisfiable on exactly that branch. The resolution order, the re-fetch command and the hard stop are all in Environment ground truth; Task 4.1 Step 0 executes them, and Task 2.1 Step 1's probe is no longer optional. (Tasks 2.1, 4.1, 9.1.)

24. **The core grants `InternalsVisibleTo` to BOTH the test assembly and `Qavren.Edge.Ingestion.Onnx`, and both grants are written in wave 2.** Plan adjustment 1 makes `Qavren.Edge.Ingestion.Onnx`'s `EdgeChunkTokenizer` delegate to `Internal.TokenIndexSearch`, which is `internal` — so the satellite needs a grant. A previous draft left Task 6.2 to "pick one and say which" between adding the grant itself and adding a public `EdgeTokenCounter` helper, and **both options are writes to `ingestion/src/Qavren.Edge.Ingestion` in wave 6**, where Tasks 6.1 and 6.3 are compiling that assembly and Task 5.2 Step 4 has already declared it frozen. `ArtifactsPath` separates outputs, not sources, so that is exactly the mid-edit-compile the Wave map's headline rule exists to prevent. The grant moves to **Task 2.1 Step 2**, which writes `AssemblyInfo.cs` in full with the reason on the second line, and Task 6.2 has no branch, no choice and **no file under `ingestion/src/Qavren.Edge.Ingestion` in its Files list**. The public-helper alternative is rejected outright: a public member existing so one first-party satellite can call it is surface with no consumer, and §11's type list does not carry it. (Tasks 2.1, 6.2.)

25. **`build_xref_stream` is written literally, `xref-stream.pdf` ships, and the seven committed PDFs are validated against the real PdfPig before they are committed.** §14.2 requires seven PDFs, one of them a PDF 1.5 cross-reference stream with an object stream — the shape every modern writer emits. A previous draft left the generator function a `NotImplementedError` with "drop the fixture" as the documented fallback, which broke this plan's **Convention for "literal code"**, left the most common real-world PDF shape with no committed coverage and no task obliged to produce it, and made Task 6.1 Step 5 say "if it was committed". The function is now in Task 1.3 Step 3 in full — `/Index` omitted so it defaults to `[0 /Size]`, `/W [1 4 2]` with a big-endian row packer, the stream's own offset knowable before serialisation because it is the last object — and **Task 1.3 Step 3b** opens all seven with PdfPig 0.1.16 in a scratchpad console outside the repo, so a broken fixture fails in the task that wrote it rather than three waves later in a task that does not own the folder. Task 1.3 Step 7 asserts **exactly seven** PDFs by name and checks `xref-stream.pdf` carries `%PDF-1.5`, `/Type /XRef`, `/W [1 4 2]`, `/Type /ObjStm` and no classic `trailer`. (Tasks 1.3, 6.1.)

26. **§14.5's real-world document lane gets a committed manifest, a consumer and a gate — not a comment.** §14.5 wants three or four files produced by actual Word, LibreOffice and Acrobat, fetched by pinned SHA-256 and never committed, and the choice of files is genuinely the owner's. A previous draft discharged that with a `ci.yml` step whose body was a comment saying the URLs would be filled in later, paired with a test that skipped on an empty corpus — a permanently green no-op that no gate could ever catch. Instead: Task 1.3 Step 6b commits `ingestion/tests/fixtures/realworld-corpus.json` with a **schema** and an empty `documents` array; Task 2.2 Step 4's nightly fetch step reads it with `jq`, `curl`s and `sha256sum -c`s each entry, and emits a `::warning::` annotation on every nightly run while it is empty; Task 7.1 Step 10 adds a **tier-1** manifest-shape test that runs on every PR, a coverage test that skips with the printed reason *"spec 14.5 is owed"* and otherwise asserts all three producers are represented, and the tier-3 ingestion test itself; and Task 9.1 Step 5 names it as an **owner-input item that blocks the first publish, not the first commit** — the same standing §17 item 3 already has. The owner's contribution is four lines of JSON. (Tasks 1.3, 2.2, 7.1, 9.1.)

27. **No SP3 test links SP2's tiny ONNX fixture, so §14.3's `distance_metric = L2` rule governs nothing in this plan.** §14.3 states the rule conditionally — "where the SP2 tiny ONNX fixture is used" — and a previous draft carried it verbatim into Task 7.1's Approach while no task anywhere declared the `<Compile Link=…>` of `TinyModels.g.cs` that would make it applicable. Every embedding in every SP3 test comes from `RecordingEmbeddingGenerator`, which is the point: SP3 must prove *how many* embed calls happen and *which* text reached them, not what a model returns. The `.Onnx` reference Task 7.1 adds is for `EdgeChunkTokenizer`, `ResourceMonitorThrottle` and `AddOnnxIngestion`'s registration, none of which runs an ORT session. The rule is kept in Task 7.1's Approach as a **condition on any future task** that wants the fixture: it must declare the link in its own Files list and take the L2 rule with it. (Tasks 1.1, 7.1.)

28. **Everything `Qavren.Edge.TrimSmoke` will ever need is declared in Task 1.1, so its csproj has exactly one writer.** Task 7.2 Step 3 ingests a real PDF and a real DOCX under the satellite publish, and a previous draft said it would use "the committed `minimal-text.pdf` bytes and one `DeterministicOpc`-built DOCX" — naming a type in `Qavren.Edge.Ingestion.Extractors.Tests`, which this console neither references nor may reference, and declaring no mechanism at all for how either file's bytes reach the process. Adding an `EmbeddedResource` item in wave 7 would have made the csproj a **third** cross-wave multi-writer file, contradicting the **Cross-wave file ownership** table's own claim. So Task 1.1 Step 9 declares the two `ProjectReference`s **and** four `EmbeddedResource` items — `minimal-text.pdf` plus the three `run-split` DOCX parts — under one `QedgeTrimSatellites` condition, and Task 7.2 reads them through `Assembly.GetManifestResourceStream` and assembles the zip with fifteen console-local lines. No `.docx` is committed anywhere in this repo, which is why the console builds one rather than reading one. (Tasks 1.1, 7.2.)

---

## Error-code ownership

Thirty-six codes, thirty-six raise sites. `errors.md` gives every code an anchor (Task 1.2) and
`EdgeErrorCode` gives every code a name (Task 1.4), but neither makes a code *reachable* — and Task
9.1 Step 5 gates on "every `EdgeErrorCode` 6000–6299 is raised from somewhere". This table is what
that gate reads, and a code with no row is a plan bug.

| Code | Raised by | Owning task | Asserted by |
|---|---|---|---|
| 6001 `TokenCounterMissing` | order-400 startup task, tokenizer resolve | 5.1 §5 | 5.1 `StartupTaskTests` |
| 6002 `IngestionCollectionNotConfigured` | `IngestionPipeline`, every `collectionName`-taking method (adjustment 21) | 5.1 §5 | 5.1 `PipelineCollectionNameTests` |
| 6003 `IngestionChunkBudgetInvalid` | `ChunkOptions.Resolve` | 2.1 §4 | 2.1 `ChunkBudgetTests`, 5.1 `StartupTaskTests` |
| 6004 `IngestionDuplicateExtractorId` | `DocumentExtractorRegistry` composition | 3.1 §4 | 3.1 `ExtractorRegistryTests` |
| 6005 `IngestionOptionsInvalid` | `AddIngestion` validation; `AddOnnxIngestion` profile guard | 5.1 §6, 6.2 §3 | 5.1 `StartupTaskTests`, 6.2 §4 |
| 6006 `IngestionCollectionDimensionMismatch` | order-400 startup task, generator metadata | 5.1 §5 | 7.1 §6 |
| 6007 `IngestionMigrationVersionConflict` | second `AddIngestion` for one collection | 5.1 §6 | 5.1 `StartupTaskTests` |
| 6008 `IngestionRunAlreadyActive` | `IngestionPipeline.RunAsync` re-entry | 5.1 §5 | 5.1 `Runtime` suite |
| 6009 `IngestionRecipeChanged` | `IngestionRunner` recipe gate under `StrictRecipe` (adjustment 22) | 5.1 §5 | 5.1 `RecipeDriftTests`, 7.1 §2 |
| 6010 `IngestionRunAborted` | `AbortAfterConsecutiveErrors` | 5.1 §5 | 5.1 `Runtime` suite |
| 6011 `IngestionCollectionSchemaMismatch` | order-400 DDL comparison | 5.1 §5 | 5.1 `CollectionShapeTests`, 7.1 §6 |
| 6051 `IngestionSourceUnavailable` | `FolderIngestionSource` enumeration | 3.1 §2 | 3.1 `FolderSourceTests` |
| 6052 `IngestionDocumentTooLarge` | size gate, both enforcement points | 5.1 §5 | 7.1 §6 |
| 6053 `IngestionDocumentUnreadable` | `SourceStream` open / non-seekable ceiling | 3.1 §3 | 3.1 `SeekabilityTests`, 7.1 §6 |
| 6054 `IngestionDuplicateDocumentId` | `IngestionRunner` per-run id set (adjustment 22) | 5.1 §5 | 5.1 `DuplicateDocumentIdTests` |
| 6055 `IngestionSourceIdInvalid` | `IngestionSource` factories | 3.1 §2 | 3.1 `FolderSourceTests` |
| 6101 `ExtractorNotFound` | registry `TryResolve` miss, per document | 3.1 §4 | 3.1 `ExtractorRegistryTests` |
| 6102 `ExtractionFailed` | `IngestionRunner` extractor catch-all (adjustment 21) | 5.1 §5 | 5.1 `ExtractionFaultTests`, 6.1 §5 |
| 6103 `DocumentEncrypted` | `PdfTextExtractor` after `Passwords` | 6.1 §1 | 6.1 §5 |
| 6104 `DocumentMalformed` | `PdfTextExtractor`; `DocxTextExtractor` | 6.1 §1, §2 | 6.1 §5 (`broken-startxref`) |
| 6105 `DocumentHasNoTextLayer` | `PdfTextExtractor` scanned-page path | 6.1 §1 | 6.1 §5 (`no-text-layer`) |
| 6106 `DocumentEncodingUndecodable` | `PlainTextExtractor` under `StrictUtf8` | 3.1 §5 | 3.1 `EncodingFallbackTests` |
| 6107 `DocumentPageBudgetExceeded` | `PdfTextExtractor` page-boundary watchdog | 6.1 §1 | 6.1 §5 |
| 6151 `ChunkExceedsTokenBudget` | every chunker's pre-yield verification | 4.1 §2–§4 | 4.1 §8 (CsCheck) |
| 6152 `ChunkContextTooLong` | `MarkdownHeadingChunker` breadcrumb overflow | 4.1 §4 | 4.1 `Chunking` suite |
| 6153 `ChunkTokenizerCeilingExceeded` | `ChunkOptions.Resolve` ceiling assert | 2.1 §4 | 2.1 `ChunkBudgetTests` |
| 6154 `ChunkerProducedEmptyChunk` | every chunker's pre-yield verification | 4.1 §2–§4 | 4.1 §8 (CsCheck) |
| 6155 `MarkdownParseFailed` | `MarkdownExtractor` | 3.1 §6 | 3.1 `MarkdownExtractorTests` |
| 6201 `IngestionStateMissing` | `IngestionStateStore` open | 5.1 §1 | 5.1 `StateStoreTests` |
| 6202 `IngestionHashAlgorithmMismatch` | `IngestionStateStore` meta read | 5.1 §1 | 5.1 `StateStoreTests` |
| 6203 `IngestionStateSchemaUnsupported` | `IngestionStateStore` meta read | 5.1 §1 | 5.1 `StateStoreTests` |
| 6204 `IngestionStateCorrupt` | `ContentHash.FromBlob`; state row read | 2.1 §2, 5.1 §1 | 2.1 `ContentHashTests`, 5.1 `StateStoreTests` |
| 6205 `IngestionCheckpointWriteFailed` | `ChunkWriter` step d | 5.1 §4 | 7.1 §3 (`SQLITE_BUSY`) |
| 6206 `IngestionEmbeddingFailed` | `ChunkWriter` step a1 | 5.1 §4 | 7.1 §6 |
| 6207 `IngestionWriteFailed` | `ChunkWriter` steps a2 and b | 5.1 §4 | 7.1 §6 |
| 6208 `IngestionEmbeddingGeneratorMissing` | order-400 startup task, generator resolve | 5.1 §5 | 5.1 `StartupTaskTests` |

---

## File structure

```
qavren-edge-sp3/                              # the worktree; branch feat/sp3-ingestion
  QavrenEdge.slnx                             # T1.1 - eleven SP3 projects appended
  Directory.Packages.props                    # T1.1 - six PackageVersion entries, three labels
  .gitignore                                  # T1.1 - fixture venv + parallel-phase artifacts
  THIRD-PARTY-NOTICES.md                      # T1.1 - PdfPig, Markdig, DocumentFormat.OpenXml
  .github/workflows/ci.yml                    # T2.2 - four test steps, PdfPig assert, trim-smoke,
                                              #        tier-3 ingestion + real-world document lane
  foundation/
    docs/errors.md                            # T1.2 - 6000-6299 anchors
    src/Qavren.Edge.Core/EdgeErrorCode.cs      # T1.4 - AMENDED (the one SP1/SP2 library edit)
    tests/Qavren.Edge.Core.Tests/              # T1.4 - range assertion
    tests/Qavren.Edge.Sqlite.Cipher.Tests/     # T7.1 - one ProjectReference + the SQLCipher lifecycle test
    tests/Qavren.Edge.DeviceTests/             # T8.1 - two ProjectReferences
    samples/Qavren.Edge.Sample/                # T8.2 - Ingest page, AppShell entry, MauiProgram wiring,
                                               #        csproj: core + .Onnx + .Pdf + .OpenXml refs
    tools/ci-checks/assert-workflows.py        # T2.2 - generalised test-project rule + PdfPig assert
  embeddings/
    tools/Qavren.Edge.TrimSmoke/               # T1.1 csproj (refs + 4 EmbeddedResource, ONE writer);
                                               # T7.2 Program.cs - AddIngestion + a Markdown run
  ingestion/
    README.md                                  # T1.2, then T7.2 fills '## Trim warnings'
    docs/adr/0009..0013-*.md                   # T1.2
    docs/superpowers/specs/                    # the approved SP3 spec
    docs/superpowers/plans/                    # this file
    src/
      Qavren.Edge.Ingestion/                   # T1.1 csproj; T2.1 surface+budget; T3.1 extraction;
                                               # T4.1 chunkers; T5.1 state+runner+registration
      Qavren.Edge.Ingestion.Pdf/               # T1.1 csproj; T6.1
      Qavren.Edge.Ingestion.OpenXml/           # T1.1 csproj; T6.1
      Qavren.Edge.Ingestion.Onnx/              # T1.1 csproj; T6.2
      Qavren.Edge.Ingestion.DataIngestion/     # T1.1 csproj; T6.3
    tests/
      fixtures/.gitattributes                  # T1.1  - corpus/** -text
      fixtures/manifest.json                   # T1.3  - byte length + SHA-256 of every fixture
      fixtures/corpus/**                       # T1.3  - 12 text/markdown, 7 PDF, 11 DOCX trees (47 parts)
      fixtures/README.md                       # T1.3, then T4.1 fills '## Golden generation'
      fixtures/golden/**                       # T4.1  - 20 generated-once JSON files: 12 auto,
                                               #         5 markdown-heading variants, 3 token-window
      fixtures/make_pdf_fixtures.py            # T1.3  - regeneration path, NEVER a build step
      fixtures/realworld-corpus.json           # T1.3  - spec 14.5 OWNER INPUT; ships with
                                               #         "documents": [] and three visible signals
      Qavren.Edge.Ingestion.Tests/             # T1.1 csproj; T2.1; T3.1; T4.1; T5.1; T7.1
      Qavren.Edge.Ingestion.Extractors.Tests/  # T1.1 csproj; T6.1; T7.3
      Qavren.Edge.Ingestion.Onnx.Tests/        # T1.1 csproj; T6.2
      Qavren.Edge.Ingestion.DataIngestion.Tests/ # T1.1 csproj; T6.3
    samples/
      Ingestion.Background.iOS/                # T1.1 csproj; T8.3
      Ingestion.Background.Android/            # T1.1 csproj; T8.3
```

Scratch, never committed: `ingestion/tests/fixtures/.venv`, `ingestion/tests/fixtures/_scratch`, and any in-repo `artifacts/` a mis-pointed `ArtifactsPath` produces — all gitignored by Task 1.1.

## Wave map

Wave membership obeys SP1 adjustment 26 and SP2's two additions — **disjoint folders AND disjoint build graphs**, plus **output-disjointness** bought by `-p:ArtifactsPath` — and one rule this plan adds:

- **Exactly one task per wave may write `ingestion/src/Qavren.Edge.Ingestion`, and no other task in that wave may compile it.** That is why waves 3, 4 and 5 carry a single implementer. A sibling task that merely references the core would compile it mid-edit; no artifacts path fixes that.
- **One owner per test csproj per wave**, for the same reason at test scope (adjustment 14).

Each wave: **prologue (wave 1 only) → parallel phase, at most three implementers → close**. The next wave starts only after the close has re-verified the whole wave sequentially and committed it.

| Wave | Phase | Tasks | Folders each task owns | Build graph each task compiles (= its write set) | Parallel-safe because |
|---|---|---|---|---|---|
| 1 | prologue | **1.1** skeleton + CPM + slnx + `.gitignore` + `THIRD-PARTY-NOTICES.md` *(integrator)* | **every root file** — `Directory.Packages.props`, `QavrenEdge.slnx`, `.gitignore`, `THIRD-PARTY-NOTICES.md` — plus all eleven SP3 csprojs, the TrimSmoke csproj and `ingestion/tests/fixtures/.gitattributes` | **restore only** — but it rewrites `Directory.Packages.props` and `QavrenEdge.slnx`, which every project reads | it runs **alone**; nothing else in wave 1 may be running |
| 1 | parallel | **1.2** README/ADRs/`errors.md`; **1.3** the fixture corpus + generator; **1.4** SP1 edit — `EdgeErrorCode` | `ingestion/README.md` + `ingestion/docs/adr` + `foundation/docs/errors.md` / `ingestion/tests/fixtures` / `foundation/src/Qavren.Edge.Core` + `…Core.Tests` | none / none in the repo (python, plus a scratchpad PdfPig probe) / Core + Core.Tests | 1.2 and 1.3 compile nothing **in the tree**, so 1.4 is the only writer of any repo `obj/`. 1.3 Step 3b builds a throwaway PdfPig console under `D:\Local\Temp`, outside the repo and outside the solution, exactly as Task 2.1 Step 1's probe does; the CPM file is frozen by the prologue. **1.2 owns no root file** — the notices edit moved to the prologue, so the integrator rule holds at its first application |
| 1 | close | **1.5** *(integrator)* | none | Core.Tests, solution restore, sequential | it is the only thing running |
| 2 | parallel | **2.1** core part 1 — surface, value types, tokenizer seam, budget, recipe, schema; **2.2** `ci.yml` + `assert-workflows.py` *(integrator)* | `ingestion/src/Qavren.Edge.Ingestion` + `…Ingestion.Tests` / `.github/workflows` + `foundation/tools/ci-checks` | Ingestion→VectorData→Sqlite→Core / none (YAML + python) | 2.2 compiles nothing at all, so the core has exactly one writer and one builder |
| 2 | close | **2.3** *(integrator)* | none | Ingestion.Tests, the workflow contract, sequential | — |
| 3 | **single implementer** | **3.1** core part 2 — sources, registry, plain text, Markdown | `ingestion/src/Qavren.Edge.Ingestion` + `…Ingestion.Tests` | Ingestion→VectorData→Sqlite→Core | nothing else runs. Every candidate sibling either writes the core or compiles it |
| 3 | close | **3.2** *(integrator)* | none | Ingestion.Tests, sequential | — |
| 4 | **single implementer** | **4.1** core part 3 — three chunkers, the golden harness, the CsCheck properties | `ingestion/src/Qavren.Edge.Ingestion` + `…Ingestion.Tests` + `ingestion/tests/fixtures/golden` + **`ingestion/tests/fixtures/README.md`** (its `## Golden generation` section — created empty by Task 1.3, filled here; see **Cross-wave file ownership**) | Ingestion→VectorData→Sqlite→Core | same rule |
| 4 | close | **4.2** *(integrator)* | none | Ingestion.Tests, sequential | — |
| 5 | **single implementer** | **5.1** core part 4 — state store, diff, write protocol, runner, `AddIngestion`, observer, diagnostics | `ingestion/src/Qavren.Edge.Ingestion` + `…Ingestion.Tests` | Ingestion→VectorData(+Sqlite.Native)→Sqlite→Core | same rule. This is the wave that closes the core; everything after it reads a frozen assembly |
| 5 | close | **5.2** *(integrator)* | none | Ingestion.Tests, sequential | — |
| 6 | parallel | **6.1** `.Pdf` + `.OpenXml` + `Extractors.Tests`; **6.2** `.Onnx` + `Onnx.Tests`; **6.3** `.DataIngestion` + `DataIngestion.Tests` | `ingestion/src/…Pdf` + `…OpenXml` + `ingestion/tests/…Extractors.Tests` / `ingestion/src/…Onnx` + `…Onnx.Tests` / `ingestion/src/…DataIngestion` + `…DataIngestion.Tests` | each → Ingestion (**frozen**) → VectorData → Sqlite → Core; 6.2 additionally → Embeddings.Onnx → Onnx | frozen core — **no wave-6 task writes a single file under `ingestion/src/Qavren.Edge.Ingestion`**, including 6.2, whose `InternalsVisibleTo("Qavren.Edge.Ingestion.Onnx")` grant was issued by Task 2.1 in wave 2 precisely so it would not have to be (plan adjustment 24). Disjoint source folders, disjoint test projects; all three write the same `Qavren.Edge.Ingestion` `obj/`, which `ArtifactsPath` separates. 6.3 additionally restores a new package graph (MEDI's twelve) while the other two build — the exact hazard SP2's wave 5 hit |
| 7 | parallel | **7.1** tier-2 integration + SP3's device-only assertions in `Ingestion.Tests` **and the one SQLCipher lifecycle test**; **7.2** `trim-smoke`; **7.3** extractor device-lane assertions in `Extractors.Tests` | `ingestion/tests/…Ingestion.Tests` **+ `foundation/tests/Qavren.Edge.Sqlite.Cipher.Tests`** / `embeddings/tools/Qavren.Edge.TrimSmoke` (**`Program.cs` only** — the csproj was written once, in wave 1, and is frozen) **+ `ingestion/README.md`** (doc-only, its `## Trim warnings` section) / `ingestion/tests/…Extractors.Tests` | Ingestion.Tests→Ingestion+Pdf+OpenXml+Onnx+Sqlite.Native **and Cipher.Tests→Ingestion+VectorData+Sqlite+Sqlite.Native** / TrimSmoke→Ingestion+VectorData+Embeddings.Onnx+Sqlite.Native / Extractors.Tests→Ingestion+Pdf+OpenXml | every source they compile was frozen in wave 6; three concurrent writers of the same `obj/`, separated by `ArtifactsPath`. **`foundation/tests/Qavren.Edge.Sqlite.Cipher.Tests` is 7.1's alone** — 7.2 and 7.3 neither write nor compile it, and it is listed here rather than left implicit because an undeclared write set is how a future wave gains a hidden second writer. 7.2's `ingestion/README.md` edit compiles nothing. **No MAUI or platform-head build is in this wave** |
| 7 | close | **7.4** *(integrator)* | none | both suites + the TrimSmoke publish, sequential | — |
| 8 | **serial — one implementer at a time** | **8.1** device-host wiring, then **8.2** sample Ingest page, then **8.3** the two background samples | `foundation/tests/…DeviceTests` → `foundation/samples/…Sample` → `ingestion/samples/**` | DeviceTests→MAUI + two SP3 test libs at platform TFMs / Sample→MAUI + Ingestion(+Pdf,OpenXml,Onnx) at platform TFMs / two platform class libs → Ingestion | **they are not parallel-safe and are not run in parallel.** Two MAUI/platform builds over one shared reference closure, each staging the same natives, is the worst pairing available; 8.1 may conditionally edit its own csproj depending on a build outcome. Serial removes both problems and costs one build |
| 8 | close | **8.4** *(integrator)* | none | both platform heads + both samples, sequential | — |
| 9 | gate | **9.1** full-solution verification *(integrator)* | none (read-only) | the whole `.slnx` | runs alone; it is the gate that closes the plan |

### Cross-wave file ownership

The rule the table above enforces is **one owner per file per wave, declared in that task's Files
list**. Two documentation files legitimately have a writer in two *different* waves. They cannot
race — the waves are strictly serial and each is committed before the next starts — but an
amendment that is not in a Files list is invisible to the map, which is the failure this subsection
closes. Both are handed over by a **placeholder heading**: the wave-1 task creates the section
empty with a one-line "filled by Task N.N" note, and the later task replaces that note. A later
task never appends to a file whose shape it has not been given.

| File | Created by | Amended by | What is handed over |
|---|---|---|---|
| `ingestion/tests/fixtures/README.md` | **1.3** Step 4 (wave 1) | **4.1** Step 6 (wave 4) | The `## Golden generation` section: which vocabulary the twenty goldens were generated against, its SHA-256, the MiniLM triple they pin, and the `QAVREN_EDGE_WRITE_GOLDEN` rule |
| `ingestion/README.md` | **1.2** Step 8 (wave 1) | **7.2** Step 4 (wave 7) | The `## Trim warnings` section: the `IL2xxx`/`IL3xxx` output of the core-only publish and of the satellite publish, recorded **separately**, or the sentence "both publishes were warning-free" |

No other file in this plan has more than one writer across all nine waves, and two near-misses are
named here so the claim is checkable rather than asserted:

- `THIRD-PARTY-NOTICES.md` had two writers in an earlier draft — Task 1.1 and Task 1.2 — and now has
  exactly one, the wave-1 integrator.
- **`embeddings/tools/Qavren.Edge.TrimSmoke/Qavren.Edge.TrimSmoke.csproj` had two in an earlier
  draft** — Task 1.1 Step 9 for the `ProjectReference`s in wave 1, and Task 7.2 for the
  `EmbeddedResource` items its PDF and DOCX run needs in wave 7. It now has exactly one: **Task 1.1
  declares everything the console will ever need, references and resources together** (plan
  adjustment 28), and Task 7.2 writes `Program.cs` alone. The Wave map lists
  `embeddings/tools/Qavren.Edge.TrimSmoke` as 7.2's folder for the wave; within it, 7.2 owns
  `Program.cs` and the csproj is frozen. A third handover would have been one more than the two
  this table can justify — and a plan that documents a rule and breaks it in the same section is
  worse than one that has no rule.

## WAVE 1 — Skeleton, docs, fixtures, and the one SP1 edit

### Task 1.1: SP3 skeleton — CPM, `.slnx`, `.gitignore` and eleven csprojs *(integrator; **wave-1 prologue — runs alone**)*

**Local-verifiable:** yes.

**Files:**
- Edit: `Directory.Packages.props`
- Edit: `QavrenEdge.slnx`
- Edit: `.gitignore`
- Edit: `THIRD-PARTY-NOTICES.md` *(a root file, therefore the integrator's — Step 12)*
- Edit: `embeddings\tools\Qavren.Edge.TrimSmoke\Qavren.Edge.TrimSmoke.csproj`
- Create: `ingestion\src\Qavren.Edge.Ingestion\Qavren.Edge.Ingestion.csproj`
- Create: `ingestion\src\Qavren.Edge.Ingestion.Pdf\Qavren.Edge.Ingestion.Pdf.csproj`
- Create: `ingestion\src\Qavren.Edge.Ingestion.OpenXml\Qavren.Edge.Ingestion.OpenXml.csproj`
- Create: `ingestion\src\Qavren.Edge.Ingestion.Onnx\Qavren.Edge.Ingestion.Onnx.csproj`
- Create: `ingestion\src\Qavren.Edge.Ingestion.DataIngestion\Qavren.Edge.Ingestion.DataIngestion.csproj`
- Create: `ingestion\tests\Qavren.Edge.Ingestion.Tests\Qavren.Edge.Ingestion.Tests.csproj`
- Create: `ingestion\tests\Qavren.Edge.Ingestion.Extractors.Tests\Qavren.Edge.Ingestion.Extractors.Tests.csproj`
- Create: `ingestion\tests\Qavren.Edge.Ingestion.Onnx.Tests\Qavren.Edge.Ingestion.Onnx.Tests.csproj`
- Create: `ingestion\tests\Qavren.Edge.Ingestion.DataIngestion.Tests\Qavren.Edge.Ingestion.DataIngestion.Tests.csproj`
- Create: `ingestion\samples\Ingestion.Background.Android\Ingestion.Background.Android.csproj`
- Create: `ingestion\samples\Ingestion.Background.iOS\Ingestion.Background.iOS.csproj`
- Create: `ingestion\tests\fixtures\.gitattributes`

All paths are relative to `C:\Users\steve\projects\qavren-edge-sp3\`.

**Approach — read this before writing.** This task writes only project files and the fixture `.gitattributes`, and it is the wave-1 **prologue**: 1.2, 1.3 and 1.4 do not start until it has finished. Compiling nothing is not what makes a task safe to run beside another — this one rewrites `Directory.Packages.props`, which `Qavren.Edge.Core` imports and Task 1.4's verify builds, and its own `dotnet restore QavrenEdge.slnx` writes `foundation/src/Qavren.Edge.Core/obj/project.assets.json`, which Task 1.4's verify also writes. Its verify is the restore, which is exactly the check that matters here: CPM resolves, every TFM is legal, and PdfPig's assets land where §4.1 claims. A stub csproj with no `.cs` files restores and later builds to an empty assembly; that is intended. No test csproj yet references a fixture *file* — Task 1.3 is still writing them in this same wave — but the `EmbeddedResource` globs are declared now so no later task has to touch a csproj.

- [ ] **Step 1: Add the six package versions**

Append to `Directory.Packages.props`, immediately before the closing `</Project>`:

```xml
  <!-- Sub-project 3. Versions verified by restore on 2026-09-11 (plan Environment ground truth).
       All five runtime packages resolve identically across net10.0 and the four platform TFMs:
       Markdig, System.IO.Hashing and DocumentFormat.OpenXml land on lib/net10.0, PdfPig on
       lib/net9.0 (never lib/netstandard2.0 - see the ci.yml assertion), Microsoft.ML.Tokenizers
       on lib/net8.0. New transitives: DocumentFormat.OpenXml.Framework 3.5.1 and
       System.IO.Packaging 10.0.2. Central transitive pinning stays off, so neither is promoted
       into a produced nuspec. -->
  <ItemGroup Label="SP3 runtime">
    <PackageVersion Include="Markdig" Version="1.3.2" />
    <PackageVersion Include="System.IO.Hashing" Version="10.0.12" />
    <PackageVersion Include="PdfPig" Version="0.1.16" />
    <PackageVersion Include="DocumentFormat.OpenXml" Version="3.5.1" />
    <PackageVersion Include="Microsoft.Extensions.DataIngestion.Abstractions" Version="10.10.0-preview.1.26459.2" />
  </ItemGroup>

  <ItemGroup Label="SP3 test">
    <PackageVersion Include="CsCheck" Version="4.8.0" />
  </ItemGroup>

  <!-- Referenced by ingestion/tests/Qavren.Edge.Ingestion.DataIngestion.Tests ONLY.
       The MEDI IMPLEMENTATION package ships IngestionPipeline<T>, which the conformance test
       needs and the shim does not. Never referenced by a src/ project. Its net10.0 closure is
       twelve packages and it demands Microsoft.Extensions.VectorData.Abstractions 9.7.0 and
       Microsoft.ML.Tokenizers 1.0.1 - BELOW this repo's pins, which is the safe direction: the
       test project's higher direct and project-carried references unify upward with no NU1605.
       Measured with TreatWarningsAsErrors on, 2026-09-11. -->
  <ItemGroup Label="SP3 test (MEDI conformance)">
    <PackageVersion Include="Microsoft.Extensions.DataIngestion" Version="10.10.0-preview.1.26459.2" />
  </ItemGroup>
```

`Microsoft.ML.Tokenizers` is **not** added: SP2 already pins it at 2.0.0 and the core consumes that pin.

- [ ] **Step 2: `.gitignore`**

Append:

```gitignore
# Sub-project 3 fixture toolchain. The generator's OUTPUT (the committed ASCII PDFs) is in git;
# its venv and any scratch it writes are not. The script is run by hand, never by a build or CI.
ingestion/tests/fixtures/.venv/
ingestion/tests/fixtures/_scratch/
```

`/artifacts/` is already ignored by SP2's entry and covers a mis-pointed `ArtifactsPath`.

- [ ] **Step 3: `ingestion\tests\fixtures\.gitattributes`**

```gitattributes
# LOAD-BEARING. Chunk offsets are character positions. The repo root sets `* text=auto eol=lf`,
# but this machine's editing tooling flips LF to CRLF on multi-line edits, and a CRLF that
# sneaks into a fixture moves every expected boundary - on the Linux CI leg only, which is the
# worst place to discover it. `-text` disables all end-of-line conversion, so what is committed
# is byte-for-byte what every platform checks out.
corpus/** -text
golden/** -text
```

- [ ] **Step 4: `Qavren.Edge.Ingestion.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- net10.0 alone. Nothing in SP3 touches a platform API: Markdig, System.IO.Hashing and
         Microsoft.ML.Tokenizers are pure managed with no runtimes/ folder between them, and a
         platform TFM consumes a net10.0 library unchanged - exactly as Qavren.Edge.VectorData
         already does. It does NOT reference Qavren.Edge.Embeddings.Onnx: IChunkTokenizer and
         ChunkModelProfile are the core-owned seams, and a ProjectReference would drag ~100 MB of
         ORT AARs and xcframeworks into a Markdown-only app using a cloud generator (spec 4.1). -->
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>true</IsPackable>
    <PackageId>Qavren.Edge.Ingestion</PackageId>
    <Description>File-to-collection ingestion for Qavren.Edge: streaming extraction, token-budgeted structure-aware chunking, xxHash128 content addressing, incremental re-index, and a budget-driven runner that suspends on a committed boundary.</Description>
    <RootNamespace>Qavren.Edge.Ingestion</RootNamespace>
    <!-- No MEVD9001 suppression, and that is a deliberate outcome of plan adjustment 2: with the
         collection DDL going through SP2's own AddVectorCollectionMigration, nothing on this
         package's registration path names a Microsoft.Extensions.VectorData.ProviderServices
         type. IngestionSchema.BuildDefinition names VectorStoreCollectionDefinition and friends,
         which are not experimental. Task 5.1's 6011 check is the one exception and carries its
         own file-scoped suppression with the reason on it. -->
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Markdig" />
    <PackageReference Include="System.IO.Hashing" />
    <PackageReference Include="Microsoft.ML.Tokenizers" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <ProjectReference Include="..\..\..\embeddings\src\Qavren.Edge.VectorData\Qavren.Edge.VectorData.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: the four satellite csprojs**

`ingestion\src\Qavren.Edge.Ingestion.Pdf\Qavren.Edge.Ingestion.Pdf.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>true</IsPackable>
    <PackageId>Qavren.Edge.Ingestion.Pdf</PackageId>
    <Description>PDF text extraction for Qavren.Edge.Ingestion, over PdfPig. Page-at-a-time and stream-only, with scanned-page detection. PdfPig is Apache-2.0, which is why this extractor is a separate package.</Description>
    <RootNamespace>Qavren.Edge.Ingestion.Pdf</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="PdfPig" />
    <ProjectReference Include="..\Qavren.Edge.Ingestion\Qavren.Edge.Ingestion.csproj" />
  </ItemGroup>
</Project>
```

`ingestion\src\Qavren.Edge.Ingestion.OpenXml\Qavren.Edge.Ingestion.OpenXml.csproj` is the file above with `PdfPig` replaced by `DocumentFormat.OpenXml`, the id and root namespace `Qavren.Edge.Ingestion.OpenXml`, and:

```xml
    <Description>DOCX text extraction for Qavren.Edge.Ingestion, over DocumentFormat.OpenXml. Outline-level heading detection, run merging, and an OpenXmlPartReader streaming path above 8 MiB. Separate from the core because the dependency is 14.66 MB.</Description>
```

`ingestion\src\Qavren.Edge.Ingestion.Onnx\Qavren.Edge.Ingestion.Onnx.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- net10.0 even though it references a four-TFM project. A net10.0 assembly referencing a
         multi-TFM package is ordinary TFM compatibility, and the consuming app resolves
         Qavren.Edge.Embeddings.Onnx's platform asset for ITS OWN TFM through the package graph,
         so an Android app still links ORT's net9.0-android35.0 asset. Nothing is lost by this
         satellite being unversioned (spec 4.1). -->
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>true</IsPackable>
    <PackageId>Qavren.Edge.Ingestion.Onnx</PackageId>
    <Description>Bridges Qavren.Edge.Ingestion to the ONNX embedding stack: a chunk tokenizer over IEdgeTokenizer, a throttle over IEdgeResourceMonitor, and a chunk budget derived from the resolved EmbeddingPreset.</Description>
    <RootNamespace>Qavren.Edge.Ingestion.Onnx</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Qavren.Edge.Ingestion\Qavren.Edge.Ingestion.csproj" />
    <ProjectReference Include="..\..\..\embeddings\src\Qavren.Edge.Embeddings.Onnx\Qavren.Edge.Embeddings.Onnx.csproj" />
  </ItemGroup>
</Project>
```

`ingestion\src\Qavren.Edge.Ingestion.DataIngestion\Qavren.Edge.Ingestion.DataIngestion.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>true</IsPackable>
    <PackageId>Qavren.Edge.Ingestion.DataIngestion</PackageId>
    <Description>Microsoft.Extensions.DataIngestion interop for Qavren.Edge.Ingestion, in both directions. Prerelease only: it references a prerelease abstractions package and ships on that package's cadence, so the stable core is never blocked by it.</Description>
    <RootNamespace>Qavren.Edge.Ingestion.DataIngestion</RootNamespace>
    <!-- Plan adjustment 11. PackageValidationBaselineVersion is unset repo-wide, so with no
         baseline the validator's remaining job is a compatibility-suppression file this package
         does not have and does not need - and leaving it on is how a pack fails on a version-
         range technicality against a 10.10.0-preview.1 dependency. Turn it back on when MEDI
         ships stable and this package drops its prerelease suffix. VersionSuffix is NOT set
         here: MinVer supplies the prerelease suffix from the tag, as for every other package. -->
    <EnablePackageValidation>false</EnablePackageValidation>
  </PropertyGroup>

  <ItemGroup>
    <!-- The ABSTRACTIONS package only, and it has ZERO dependencies on net8.0+ (measured).
         The IMPLEMENTATION package, Microsoft.Extensions.DataIngestion, is referenced by the
         conformance TEST project alone and must never appear here: it drags eleven more
         packages, two of them below this repo's pins. -->
    <PackageReference Include="Microsoft.Extensions.DataIngestion.Abstractions" />
    <ProjectReference Include="..\Qavren.Edge.Ingestion\Qavren.Edge.Ingestion.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: the two device-hosted test projects**

Both copy `Qavren.Edge.VectorData.Tests`'s shape exactly. Neither carries a raised `SupportedOSPlatformVersion` today, because neither has a transitive ORT reference — `Qavren.Edge.Ingestion.Tests` gains SP2's 24.0 / 15.1 floors **only** in Task 7.1, which is the wave that adds the `.Onnx` reference.

`ingestion\tests\Qavren.Edge.Ingestion.Tests\Qavren.Edge.Ingestion.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- net10.0 is the HOST lane: an MTP test application, run with `dotnet run -f net10.0`.
         The four platform TFMs exist ONLY so this same assembly can be loaded by
         Qavren.Edge.DeviceTests and executed on a device. The windows TFM is present even though
         Qavren.Edge.Ingestion has none: Qavren.Edge.DeviceTests targets
         net10.0-windows10.0.19041.0 and must be able to reference this library, and a
         net10.0-windows test assembly referencing a net10.0 product assembly is ordinary TFM
         compatibility. -->
    <TargetFrameworks>net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst;net10.0-windows10.0.19041.0</TargetFrameworks>
    <TargetFrameworks Condition="$([MSBuild]::IsOSPlatform('Linux'))">net10.0;net10.0-android</TargetFrameworks>
    <TargetFrameworks Condition="$([MSBuild]::IsOSPlatform('OSX'))">net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst</TargetFrameworks>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <PropertyGroup Condition="'$(TargetFramework)' == 'net10.0'">
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
    <PackageReference Include="xunit.v3" />
  </ItemGroup>

  <!-- DEVICE LANES. A plain class library. Referencing xunit.v3 or xunit.v3.core here would
       inject a Main and collide with the MAUI application host (SP1 adjustment 11). -->
  <PropertyGroup Condition="'$(TargetFramework)' != 'net10.0'">
    <OutputType>Library</OutputType>
    <IsTestingPlatformApplication>false</IsTestingPlatformApplication>
    <GenerateTestingPlatformEntryPoint>false</GenerateTestingPlatformEntryPoint>
    <!-- CA2007 self-suppresses for an application output kind, so the net10.0 lane never sees it;
         the device lanes compile the SAME sources as a library and it fires on every await. Its
         fix - ConfigureAwait(false) in a test body - is what xunit's xUnit1030 forbids. -->
    <NoWarn>$(NoWarn);CA2007</NoWarn>
  </PropertyGroup>
  <ItemGroup Condition="'$(TargetFramework)' != 'net10.0'">
    <PackageReference Include="xunit.v3.extensibility.core" />
    <PackageReference Include="xunit.v3.assert" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="CsCheck" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
    <ProjectReference Include="..\..\src\Qavren.Edge.Ingestion\Qavren.Edge.Ingestion.csproj" />
    <!-- vec0 and FTS5 arrive only with the native. Qavren.Edge.Sqlite.Native's QedgeHostRid item
         stages qedge_sqlite3.dll flat beside the test binary, so the host lane runs on a dev box
         with no package installed. Same arrangement Qavren.Edge.VectorData.Tests uses. -->
    <ProjectReference Include="..\..\..\foundation\src\Qavren.Edge.Sqlite.Native\Qavren.Edge.Sqlite.Native.csproj" />
  </ItemGroup>

  <!-- Fixtures ship as EmbeddedResource, not files: device filesystem layout differs from the
       host and SP2's <Compile Link=...> idiom covers base64 text constants only (spec 14.6).
       Declared now so no later task has to edit a csproj; empty until Tasks 1.3 and 4.1 fill the
       directories. -->
  <ItemGroup>
    <EmbeddedResource Include="..\fixtures\corpus\**\*" LogicalName="fixtures/corpus/%(RecursiveDir)%(Filename)%(Extension)" />
    <EmbeddedResource Include="..\fixtures\golden\**\*" LogicalName="fixtures/golden/%(RecursiveDir)%(Filename)%(Extension)" />
    <EmbeddedResource Include="..\fixtures\manifest.json" LogicalName="fixtures/manifest.json" />
  </ItemGroup>
</Project>
```

`ingestion\tests\Qavren.Edge.Ingestion.Extractors.Tests\Qavren.Edge.Ingestion.Extractors.Tests.csproj` is byte-for-byte the file above with `CsCheck` dropped, the `Qavren.Edge.Sqlite.Native` reference dropped (no extractor test touches a database), the `golden` glob dropped, and the single core `ProjectReference` replaced by three:

```xml
    <ProjectReference Include="..\..\src\Qavren.Edge.Ingestion\Qavren.Edge.Ingestion.csproj" />
    <ProjectReference Include="..\..\src\Qavren.Edge.Ingestion.Pdf\Qavren.Edge.Ingestion.Pdf.csproj" />
    <ProjectReference Include="..\..\src\Qavren.Edge.Ingestion.OpenXml\Qavren.Edge.Ingestion.OpenXml.csproj" />
```

- [ ] **Step 7: the two host-only test projects**

`ingestion\tests\Qavren.Edge.Ingestion.Onnx.Tests\Qavren.Edge.Ingestion.Onnx.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- net10.0 ALONE, and that is the authoritative spelling (spec 4.2, 14.6). Host-only: never
         referenced from Qavren.Edge.DeviceTests, never on a device lane. Its whole job is the
         ChunkModelProfile-vs-EmbeddingPreset drift table and two adapters, none of which is
         platform-specific, and four extra TFMs would add four build legs to prove nothing. -->
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
    <ProjectReference Include="..\..\src\Qavren.Edge.Ingestion.Onnx\Qavren.Edge.Ingestion.Onnx.csproj" />
  </ItemGroup>
</Project>
```

`ingestion\tests\Qavren.Edge.Ingestion.DataIngestion.Tests\Qavren.Edge.Ingestion.DataIngestion.Tests.csproj` is the same file with the project reference swapped and three additions:

```xml
    <!-- The MEDI IMPLEMENTATION package. IngestionPipeline<T> lives here and not in the
         Abstractions the shim ships against, so the conformance test - and ONLY the conformance
         test - restores it. It brings eleven transitives, two of them BELOW this repo's pins
         (VectorData.Abstractions 9.7.0, ML.Tokenizers 1.0.1); the higher direct and
         project-carried references unify upward with no NU1605 (measured 2026-09-11).
         Microsoft.Extensions.DataIngestion.Markdig is deliberately ABSENT: it depends on
         Markdig.Signed, a second package id shipping the same Markdig.dll assembly name as the
         core's Markdig 1.3.2, and two ids producing one assembly name in one graph is MSB3277,
         which TreatWarningsAsErrors turns into a build failure (spec 14.4). -->
    <PackageReference Include="Microsoft.Extensions.DataIngestion" />
    <ProjectReference Include="..\..\src\Qavren.Edge.Ingestion.DataIngestion\Qavren.Edge.Ingestion.DataIngestion.csproj" />
    <ProjectReference Include="..\..\..\foundation\src\Qavren.Edge.Sqlite.Native\Qavren.Edge.Sqlite.Native.csproj" />
```

- [ ] **Step 8: the two background sample projects** (plan adjustment 8)

`ingestion\samples\Ingestion.Background.Android\Ingestion.Background.Android.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- A class library, not an app head: it exists to be READ and copied, and an app head would
         need an icon, a splash, a manifest and a signing story for something nobody installs.
         It IS in the .slnx (plan adjustment 8) so restore, format and pack on the windows-2025
         leg compile it - a documentation project nothing builds is one that rots on the first
         rename. -->
    <TargetFramework>net10.0-android</TargetFramework>
    <IsPackable>false</IsPackable>
    <SupportedOSPlatformVersion>24.0</SupportedOSPlatformVersion>
    <RootNamespace>Ingestion.Background.Android</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <ProjectReference Include="..\..\src\Qavren.Edge.Ingestion\Qavren.Edge.Ingestion.csproj" />
  </ItemGroup>
</Project>
```

`ingestion\samples\Ingestion.Background.iOS\Ingestion.Background.iOS.csproj` is the same with `<TargetFramework>net10.0-ios</TargetFramework>`, `<SupportedOSPlatformVersion>15.1</SupportedOSPlatformVersion>` and the matching root namespace. Neither carries an `IsOSPlatform` TFM guard, because a single-TFM project cannot drop its only TFM; they are safe in the `.slnx` because only the `windows-2025` CI leg restores, formats or packs the solution (verified: all three steps are `if: matrix.os == 'windows-2025'`). State that in the README's samples paragraph.

- [ ] **Step 9: `trim-smoke` gains a conditional satellite reference** (plan adjustment 15)

In `embeddings\tools\Qavren.Edge.TrimSmoke\Qavren.Edge.TrimSmoke.csproj`, add to the existing `ItemGroup`:

```xml
    <!-- Spec 17: the trim-smoke job publishes this console twice. The unconditional core
         reference is the PR gate and runs the Markdown one-document path, which puts Markdig -
         the only dependency in the CORE that declares neither IsTrimmable nor IsAotCompatible -
         under the trimmer on every PR. The two satellites arrive only with
         -p:QedgeTrimSatellites=true, which is the second, continue-on-error publish
         (verification item 4's run (b)). -->
    <ProjectReference Include="..\..\..\ingestion\src\Qavren.Edge.Ingestion\Qavren.Edge.Ingestion.csproj" />
    <ProjectReference Include="..\..\..\ingestion\src\Qavren.Edge.Ingestion.Pdf\Qavren.Edge.Ingestion.Pdf.csproj"
                      Condition="'$(QedgeTrimSatellites)' == 'true'" />
    <ProjectReference Include="..\..\..\ingestion\src\Qavren.Edge.Ingestion.OpenXml\Qavren.Edge.Ingestion.OpenXml.csproj"
                      Condition="'$(QedgeTrimSatellites)' == 'true'" />
```

and, in the **same edit**, the four fixture resources the satellite run needs — because this task is the **only** writer of this csproj in the whole plan and Task 7.2 must not have to come back to it (plan adjustment 28):

```xml
    <!-- The satellite trim run (Task 7.2 Step 3) ingests one real PDF and one real DOCX, so it
         needs their bytes. They travel as EmbeddedResource rather than as files: this console is
         published self-contained to an output directory that contains nothing but the publish,
         and a CopyToOutputDirectory item would have to be re-found at runtime on two host RIDs.
         Conditioned with the ProjectReferences above, so the CORE-only publish - the PR gate -
         embeds nothing and links neither satellite.
         The DOCX is the committed run-split PART TREE, not a .docx: no zip is committed anywhere
         in this repo (Task 1.3 Step 5), so the console assembles the package itself from these
         three parts. It does NOT use Extractors.Tests' DeterministicOpc - this console has no
         reference to any test project and must never gain one. -->
    <ItemGroup Condition="'$(QedgeTrimSatellites)' == 'true'">
      <EmbeddedResource Include="..\..\..\ingestion\tests\fixtures\corpus\pdf\minimal-text.pdf"
                        LogicalName="trimsmoke/minimal-text.pdf" />
      <EmbeddedResource Include="..\..\..\ingestion\tests\fixtures\corpus\docx\run-split\[Content_Types].xml"
                        LogicalName="trimsmoke/docx/[Content_Types].xml" />
      <EmbeddedResource Include="..\..\..\ingestion\tests\fixtures\corpus\docx\run-split\_rels\.rels"
                        LogicalName="trimsmoke/docx/_rels/.rels" />
      <EmbeddedResource Include="..\..\..\ingestion\tests\fixtures\corpus\docx\run-split\word\document.xml"
                        LogicalName="trimsmoke/docx/word/document.xml" />
    </ItemGroup>
```

Put that `ItemGroup` after the existing one; it is its own group because the condition is on the whole set.

**Two things about ordering, both fine, both worth stating.** These four `Include` paths name files Task 1.3 has not written yet — it runs after this prologue, in the same wave. Restore does not evaluate `EmbeddedResource` existence, this task's verify is a restore, and the wave-1 close builds `Qavren.Edge.Core.Tests` and restores the solution but never *builds* `Qavren.Edge.TrimSmoke`; the first build of this project is Task 7.2's, six waves later, by which time the files have been committed. And `[Content_Types].xml` contains square brackets, which MSBuild does not treat specially in an `Include` — only `%`, `$`, `@` and `;` need escaping there — so the path is written literally.

**This csproj now has exactly one writer across all nine waves, and that is deliberate.** An earlier draft left Task 7.2 needing an `EmbeddedResource` item here in wave 7, which would have made it a third cross-wave multi-writer file and contradicted the **Cross-wave file ownership** table's claim in the same breath. Everything the console will ever need is declared here, up front; Task 7.2 writes `Program.cs` and nothing else in this directory.

The console's `Program.cs` is not touched here — Task 7.2 owns it.

- [ ] **Step 10: Add every project to `QavrenEdge.slnx`**

Insert three `<Folder>` elements before the closing `</Solution>`:

```xml
  <Folder Name="/ingestion/src/">
    <Project Path="ingestion/src/Qavren.Edge.Ingestion/Qavren.Edge.Ingestion.csproj" />
    <Project Path="ingestion/src/Qavren.Edge.Ingestion.Pdf/Qavren.Edge.Ingestion.Pdf.csproj" />
    <Project Path="ingestion/src/Qavren.Edge.Ingestion.OpenXml/Qavren.Edge.Ingestion.OpenXml.csproj" />
    <Project Path="ingestion/src/Qavren.Edge.Ingestion.Onnx/Qavren.Edge.Ingestion.Onnx.csproj" />
    <Project Path="ingestion/src/Qavren.Edge.Ingestion.DataIngestion/Qavren.Edge.Ingestion.DataIngestion.csproj" />
  </Folder>
  <Folder Name="/ingestion/tests/">
    <Project Path="ingestion/tests/Qavren.Edge.Ingestion.Tests/Qavren.Edge.Ingestion.Tests.csproj" />
    <Project Path="ingestion/tests/Qavren.Edge.Ingestion.Extractors.Tests/Qavren.Edge.Ingestion.Extractors.Tests.csproj" />
    <Project Path="ingestion/tests/Qavren.Edge.Ingestion.Onnx.Tests/Qavren.Edge.Ingestion.Onnx.Tests.csproj" />
    <Project Path="ingestion/tests/Qavren.Edge.Ingestion.DataIngestion.Tests/Qavren.Edge.Ingestion.DataIngestion.Tests.csproj" />
  </Folder>
  <Folder Name="/ingestion/samples/">
    <Project Path="ingestion/samples/Ingestion.Background.Android/Ingestion.Background.Android.csproj" />
    <Project Path="ingestion/samples/Ingestion.Background.iOS/Ingestion.Background.iOS.csproj" />
  </Folder>
```

- [ ] **Step 11: Restore and assert PdfPig's asset resolution**

```powershell
dotnet restore "C:\Users\steve\projects\qavren-edge-sp3\QavrenEdge.slnx"
```

Then assert the five resolutions §4.1 and §15 depend on:

```powershell
$assets = "C:\Users\steve\projects\qavren-edge-sp3\ingestion\tests\Qavren.Edge.Ingestion.Extractors.Tests\obj\project.assets.json"
$a = Get-Content $assets -Raw | ConvertFrom-Json
$tfms = 'net10.0','net10.0-android','net10.0-ios','net10.0-maccatalyst','net10.0-windows10.0.19041.0'
foreach ($tfm in $tfms) {
  $entry = $a.targets.$tfm.'PdfPig/0.1.16'
  if (-not $entry) { throw "$tfm did not resolve PdfPig 0.1.16" }
  $names = $entry.compile.PSObject.Properties.Name
  $bad = $names | Where-Object { $_ -notlike 'lib/net9.0/*' }
  if ($bad) { throw "$tfm resolved PdfPig to '$($bad -join ',')'; expected lib/net9.0 only" }
  Write-Host "OK $tfm -> lib/net9.0 ($($names.Count) assemblies)"
}
```

Expected: five `OK` lines, each reporting seven assemblies. A `lib/netstandard2.0` anywhere is the failure §14.6 names: the `netstandard2.0` copy of `UglyToad.PdfPig.Fonts.dll` contains neither `AndroidSystemFontLister` nor `IOSSystemFontLister` and throws `NotSupportedException` out of a static constructor on a PDF that references a non-embedded font — with no compile error anywhere.

- [ ] **Step 12: `THIRD-PARTY-NOTICES.md`** *(moved here from Task 1.2 — it is a root file, and root files are the integrator's)*

Three `##` sections in the existing file's style: `## PdfPig (Apache-2.0) — Qavren.Edge.Ingestion.Pdf only`, `## Markdig (BSD-2-Clause)`, `## DocumentFormat.OpenXml (MIT) — Qavren.Edge.Ingestion.OpenXml only`. Each names the package id, the version pinned in Step 1, the SPDX identifier and the upstream URL.

This step lives in **this** task rather than in Task 1.2 for one reason, and it is the reason the wave map is auditable at all: the Branch and commit model says one integrator per wave owns every root file, and `THIRD-PARTY-NOTICES.md` is on that list by name. Task 1.2 is an implementer. Nothing would have collided in practice — no other wave-1 task touches the file — but a rule that is broken at its first application is a rule nobody checks at its tenth. Whether a plain `PackageReference` carries a NOTICE obligation beyond this file is **§17 item 3, an owner decision that blocks the first publish and not the first commit**; it is recorded in Open risks, not asserted here.

- [ ] **Step 13: Verify**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp3"
dotnet restore "$root\QavrenEdge.slnx"
if ($LASTEXITCODE -ne 0) { throw "solution restore failed" }
$notices = Get-Content "$root\THIRD-PARTY-NOTICES.md" -Raw
foreach ($n in 'PdfPig','Apache-2.0','Markdig','BSD-2-Clause','DocumentFormat.OpenXml') {
  if ($notices -notmatch [regex]::Escape($n)) { throw "THIRD-PARTY-NOTICES.md missing $n" }
}
Write-Host 'OK: solution restored, three notices present'
```

Expected: `Restored` for every project including the eleven new ones, the `OK:` line, exit code 0, and the Step 11 script prints five `OK` lines.

---

### Task 1.2: `ingestion` README, ADRs 0009–0013, `foundation/docs/errors.md` and the notices

**Local-verifiable:** yes.

**Files:**
- Create: `ingestion\README.md`
- Create: `ingestion\docs\adr\0009-pdfpig-and-the-apache-2-0-split.md`
- Create: `ingestion\docs\adr\0010-content-addressed-chunk-keys.md`
- Create: `ingestion\docs\adr\0011-mirror-medi-rather-than-depend-on-it.md`
- Create: `ingestion\docs\adr\0012-two-migration-versions-and-sp2s-registry.md`
- Create: `ingestion\docs\adr\0013-one-token-index-search-for-both-tokenizers.md`
- Edit: `foundation\docs\errors.md`

`THIRD-PARTY-NOTICES.md` is **not** in this list. It is a root file and therefore the wave-1 integrator's; Task 1.1 Step 12 writes it. `foundation/docs/errors.md` is not on the root-file list and is this task's alone.

**Approach.** Docs only; this task compiles nothing, which is why it can run beside Task 1.4. The ADRs follow `foundation/docs/adr/0002-own-sqlite-build-and-provider.md`'s shape — `## Status`, `## Context`, `## Decision`, `## Consequences` — and continue the global numbering (SP1 holds 0001–0002, SP2 holds 0003–0008).

- [ ] **Step 1: `foundation\docs\errors.md` gains `## 6000–6299 — sub-project 3`**

One `## <number>` heading per code in §13.1, ascending, each with a one-line meaning and a one-line remediation, because `EdgeException.HelpLink` addresses it by number. The thirty-six codes are 6001–6011, 6051–6055, 6101–6107, 6151–6155, 6201–6208. Take each entry's meaning and remediation from §13.3's failure catalogue — **except for the six entries below, where §13.3 is absent, stale, or describes a member this plan deletes.** Copying §13.3 verbatim for these would ship a remediation naming something that does not exist, which is worse than no remediation at all, because a consumer follows it.

| Code | What §13.3 gives | What `errors.md` must say instead, and why |
|---|---|---|
| **6005** | "`SleepGraceBudget` **or `FtsMergeBudget`** over 2 s, **their sum over 3 s**" | **Plan adjustment 2 deletes `FtsMergeBudget` and the ≤ 3 s sum** — SP3 runs no merge of its own, so neither member exists on `IngestionOptions` and Task 5.1 Step 6 validates `SleepGraceBudget` alone. The entry reads: explicitly-set `OverlapTokens >= MaxTokens / 2` or `MinTokens >= MaxTokens`; a non-positive `HeadingPathTokenBudget`, `WriteBatchSize` or `DeleteBatchSize`; a `SleepGraceBudget` over 2 s; a `FullTextRemoveDiacritics` outside 0–2; a `StateTablePrefix` that is not `^[A-Za-z_][A-Za-z0-9_]*$`; and a `Model` disagreeing with the resolved preset (§11.2). **No entry in `errors.md` may name `FtsMergeBudget`, `FtsMergePages` or `FtsMergeMaxIterations`**, and Task 1.5 Step 3 greps for all three. |
| **6002** | *nothing — §13.3 has no row* | Plan adjustment 21 supplies one: a `collectionName` argument that no `AddIngestion` configured. Remediation: call `AddIngestion` for that collection, or pass `null` to use the single configured one. |
| **6102** | *nothing — §13.3 has no row* | Plan adjustment 21 supplies one: the selected extractor threw something that is not already 6103 / 6104 / 6106 / 6107. Remediation: read the inner exception; the document is recorded `Failed` and the run continues. |
| **6009** | "Throw with both hashes; default is to re-index and log 915." | Correct, but **both branches must be in the entry**, because the default branch is the one every consumer hits: with `StrictRecipe = false` (the default) a recipe change silently re-indexes and logs event 915 once with both hashes; only `StrictRecipe = true` throws. An entry describing the throw alone tells a consumer their run will fail when it will not. |
| **6011** | the `ConfigureCollection` remediation | Must **also** name `IngestionOptions.FullTextRemoveDiacritics`, because one of the five shaping values is unreachable through `ConfigureCollection` (plan adjustment 4) and the spec's remediation is impossible to follow for it. |
| **6101** | "remediation names the missing satellite" | Spell the calls out — `AddPdfExtractor()` for `.pdf`, `AddDocxExtractor()` for `.docx` — because that is the mistake a first-time consumer actually makes. |

- [ ] **Step 2: ADR 0009 — PdfPig and the Apache-2.0 split**

Context: suite decision 11 is MIT; PdfPig is Apache-2.0; every alternative fails a hard constraint (Docnet.Core 2.6.0 ships no android/ios/maccatalyst natives and is three years stale, PDFsharp 6.2.4 has no text-extraction member at all, iText 9.7.0 is AGPL, Syncfusion and Telerik are commercial, and the platform fast paths cost two extra code paths and two extra test matrices while leaving Windows on PdfPig anyway). Decision: isolate it in one opt-in package, state the licence in that package's README first paragraph, never reference `UglyToad.PdfPig.DocumentLayoutAnalysis.Export` — its Alto/PageXml exporters use `XmlSerializer` and carry `RequiresUnreferencedCode`, and they are the only trim hazard in the package. Consequences: a Markdown-only app carries neither the licence nor the ~5.7 MB.

- [ ] **Step 3: ADR 0010 — content-addressed chunk keys**

Context: the prior art's `documentId#index` re-embeds the tail of a document on any insertion; pure content addressing breaks on a repeated paragraph. Decision: `xxHash128(sourceId, documentId, chunkContentHash, duplicateOrdinal)`, with ordinal stored but **not** part of identity. Consequences: inserting a paragraph at the top of a 500-chunk document is one embedding and 499 ordinal/offset **repairs** — and the repairs are not free, because SP1's `"<fts>_au"` trigger is `AFTER UPDATE ON <data>` unqualified and fires an FTS5 delete-plus-insert per row. Three to four orders of magnitude cheaper than 499 embeddings, `RepairOrdinals` exists to turn it off, and §17 item 7 measures it. An SP1 amendment emitting `AFTER UPDATE OF <fts columns> ON` would remove the cost; it is filed as an SP1 issue and is **not** an SP3 dependency.

- [ ] **Step 4: ADR 0011 — mirror MEDI rather than depend on it**

Context: fourteen prereleases in eleven months, `<Stage>preview</Stage>`, abstractions that are abstract classes, chunks with no ordinal/offset/hash/token count, `Guid.NewGuid()` keys, and a delete-and-replace "incremental" mode that re-embeds every chunk of an unchanged document. Decision: match MEDI's verbs, ship the shim as a separate prerelease package against the zero-dependency Abstractions, keep the implementation package inside one test project. Consequences: the ecosystem claim is tested rather than aspirational; `Markdig.Signed` never enters any graph in this repository; if a future test genuinely needs MEDI's own Markdig reader it needs a **new project** that does not reference `Qavren.Edge.Ingestion`, not a package addition; revisit folding the shim in when MEDI ships stable.

- [ ] **Step 5: ADR 0012 — two migration versions, and what SP2's registry buys**

This is plan adjustment 2 written up as a decision record, because a later reader will otherwise "fix" it back to one version. Context: §17 item 1's two candidate shapes, **both of which resolve** — `EdgeCollectionModelBuilder` is public, `BuildDynamic(definition, generator)` is the call SP2 itself makes, and a runtime collection's `EdgeVectorSchema` is reachable through `GetService` with no connection. Decision: `AddIngestion(N)` claims `N` for the collection through SP2's `AddVectorCollectionMigration` definition overload and `N + 1` for SP3's three state tables through SP1's `AddMigrations`. Consequences: SP3's collection enters `EdgeVectorCollectionRegistry`, which is the only writer SP2's lifecycle observer and diagnostics contributor read, so SP2 merges SP3's FTS5 sidecar and reports its tables; `FtsMergeBudget`, `FtsMergePages`, `FtsMergeMaxIterations`, event 926 and §12's ≤ 3 s sum validation are deleted; `IngestionSchemaOnlyGenerator` leaves the registration path; the core needs no `MEVD9001` suppression; and a version clash surfaces as SP1's `MigrationVersionConflict` (3002) like every other migration in the suite. The cost is §11.1's one-version claim, and the XML doc on `AddIngestion` states the two versions plainly.

- [ ] **Step 6: ADR 0013 — one token-index search for both tokenizers**

Plan adjustment 1, for the same reason. Context: the measured behaviour of `Tokenizer.GetIndexByTokenCount` at both settings — quote the table from the plan's **Environment ground truth** verbatim, including that `considerNormalization: false` returned `index = text.Length, tokenCount = 1` for every budget, and that at the default an NFD input returned an index into a normalised copy that lands mid-word in the original. Decision: one bounded prefix search over `CountTokens` at the tokenizer's own defaults, shared by `MlChunkTokenizer` and `EdgeChunkTokenizer`, verifying its final cut. Consequences: the offset contract holds by construction because only prefixes of the original are ever measured; the count is exactly what the encoder will produce; no SP2 edit is asked for now or later, and §17 item 11's proposed `considerNormalization` overload is **withdrawn** because no setting of that parameter would help; the cost is 8–12 `CountTokens` calls per cut over a bounded span, which §17 item 11 still measures on an ARM64 device.

- [ ] **Step 7: `ingestion\README.md`**

Covers, in this order:

1. The two calls from §4.3, with the note that `AddIngestion(migrationVersion: N)` claims **N and N+1**.
2. The package table from §4.1, with PdfPig's Apache-2.0 term in the first paragraph of the `.Pdf` row.
3. The chunk budget table from §8.1 with both pinned triples (**222 / 32 / 27** and **478 / 64 / 59**) and the measured `SpecialTokenOverhead = 2`, plus the sentence that a prefixed profile's triple is computed at startup and deliberately not tabulated.
4. What re-running over an unchanged corpus costs — one sequential 64 KiB-buffered read per document, zero embeddings, zero writes — and, immediately after, both halves of §12's "known outstanding" sentence: `recipeStaleDocuments == 0 && staleDocuments == 0 && lastRunOutcome == Completed` means nothing *known* is owed, **not** that the files on disk are unchanged. A consumer who reads the first as the second will stop re-indexing edited files.
5. The `Sleeping` budget arithmetic under plan adjustment 2: SP3's `SleepGraceBudget` (750 ms, validated ≤ 2 s), then SP2's bounded FTS5 merge — which now covers SP3's own sidecar because SP3's collection is registered — then SP1's WAL checkpoint, against iOS's documented ~5 s window. Quote SP2's merge spelling, `INSERT INTO "<t>"("<t>", rank) VALUES ('merge', 500)`, because §12's one-column spelling is not valid SQL and a reader who copies it gets an error.
6. The mobile-source paragraph from §11.3 with the `IngestionSource.Items` sample and its three traps: a stable `DocumentId`, a legitimately unknown `SizeBytes`, and an `OpenAsync` that is called **twice** and must re-acquire the iOS security scope each time.
7. `PageBudget`'s honest limitation from §7.4 — it bounds a slow document, not a wedged page, because PdfPig's per-page surface takes no `CancellationToken`, and the only real defence against a page that never returns is the consumer's own process-level budget.
8. The samples paragraph: `ingestion/samples/` holds two single-TFM class libraries that the solution compiles and nobody installs.

9. **A placeholder section, written empty, for the one amendment another wave makes.** The last heading in the file is exactly:

   ```markdown
   ## Trim warnings

   <!-- Filled by Task 7.2 Step 4 (wave 7): the IL2xxx/IL3xxx output of the core-only trimmed
        publish and of the satellite publish, recorded SEPARATELY, or the sentence "both
        publishes were warning-free". Do not delete this heading; Task 7.2 replaces this comment
        and nothing else in this file. -->
   ```

   This is the handover named under **Cross-wave file ownership**. Task 7.2 is the second and last writer of this file, four waves later, and it replaces the comment rather than appending to a shape it was never given. §17 item 4 says the warnings are recorded in the README rather than suppressed; a README with nowhere to record them is how "recorded" becomes "mentioned in a log nobody reads".

- [ ] **Step 8: Verify**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp3"
$expected = @(6001,6002,6003,6004,6005,6006,6007,6008,6009,6010,6011,
              6051,6052,6053,6054,6055,6101,6102,6103,6104,6105,6106,6107,
              6151,6152,6153,6154,6155,6201,6202,6203,6204,6205,6206,6207,6208)
$errors = Get-Content "$root\foundation\docs\errors.md" -Raw
foreach ($c in $expected) { if ($errors -notmatch "(?m)^## $c\s*$") { throw "errors.md has no anchor for $c" } }
$adrs = 'ingestion\docs\adr\0009-pdfpig-and-the-apache-2-0-split.md',
        'ingestion\docs\adr\0010-content-addressed-chunk-keys.md',
        'ingestion\docs\adr\0011-mirror-medi-rather-than-depend-on-it.md',
        'ingestion\docs\adr\0012-two-migration-versions-and-sp2s-registry.md',
        'ingestion\docs\adr\0013-one-token-index-search-for-both-tokenizers.md'
foreach ($f in $adrs) {
  $t = Get-Content "$root\$f" -Raw
  if ($t -notmatch '## Status' -or $t -notmatch '## Context' -or $t -notmatch '## Decision' -or $t -notmatch '## Consequences') {
    throw "$f is not in ADR shape"
  }
}
$readme = Get-Content "$root\ingestion\README.md" -Raw
foreach ($token in '222','478','SpecialTokenOverhead','IngestionSource.Items','PageBudget','Apache-2.0',"VALUES ('merge', 500)") {
  if ($readme -notmatch [regex]::Escape($token)) { throw "README does not cover: $token" }
}
if ($readme -notmatch '(?m)^## Trim warnings\s*$') { throw "README has no '## Trim warnings' placeholder for Task 7.2" }

# Plan adjustment 2 deleted these three members. An errors.md remediation naming one of them
# would tell a consumer to set a property that does not exist on IngestionOptions.
foreach ($gone in 'FtsMergeBudget','FtsMergePages','FtsMergeMaxIterations') {
  if ($errors -match [regex]::Escape($gone)) { throw "errors.md names $gone, deleted by plan adjustment 2" }
}
# 6009's entry must describe the default (re-index + 915) as well as the StrictRecipe throw.
$e6009 = ($errors -split '(?m)^## ')[($errors -split '(?m)^## ').IndexOf(($errors -split '(?m)^## ' | Where-Object { $_ -like '6009*' } | Select-Object -First 1))]
foreach ($half in 'StrictRecipe','915') {
  if ($e6009 -notmatch [regex]::Escape($half)) { throw "errors.md 6009 does not cover: $half" }
}
Write-Host 'OK: 36 error anchors, 5 ADRs in shape, README covers its eight points plus the placeholder, no deleted members named'
```

Expected: the `OK:` line, exit 0. `THIRD-PARTY-NOTICES.md` is **not** checked here — Task 1.1 Step 13 checks it, because Task 1.1 writes it.

---

### Task 1.3: The committed fixture corpus and `make_pdf_fixtures.py`

**Local-verifiable:** yes.

**Files:**
- Create: `ingestion\tests\fixtures\corpus\text\*` (7 files)
- Create: `ingestion\tests\fixtures\corpus\markdown\*` (5 files)
- Create: `ingestion\tests\fixtures\corpus\pdf\*.pdf` (**7 files**, generated once by the script below and committed) and `flate-content.stream`
- Create: `ingestion\tests\fixtures\corpus\docx\<name>\*.xml` (11 part-XML trees)
- Create: `ingestion\tests\fixtures\make_pdf_fixtures.py`
- Create: `ingestion\tests\fixtures\requirements.txt`
- Create: `ingestion\tests\fixtures\manifest.json`
- Create: `ingestion\tests\fixtures\realworld-corpus.json` *(the §14.5 owner-input manifest — Step 6b)*
- Create: `ingestion\tests\fixtures\README.md`

**Approach — commit the inputs; generate nothing at test time except the zip and deflate containers.** Measured by SP2 and true here: PdfPig's `PdfDocumentBuilder` writes a random trailer `/ID` pair, so identical content produces a different SHA on every build, and `WordprocessingDocument.Create` embeds execution-time zip stamps. A generated fixture also makes the *input* move whenever the generator library upgrades, at which point a golden test measures PdfPig rather than the chunker. So the corpus is committed ASCII, `make_pdf_fixtures.py` documents regeneration and **is never a build step and CI never runs it**, and `manifest.json` turns "somebody regenerated and the bytes moved" into a failing test rather than a surprise.

- [ ] **Step 1: the seven text fixtures**

`corpus/text/`. The byte lengths and SHA-256s below were measured on 2026-09-11 and go into `manifest.json` verbatim.

| File | Bytes | SHA-256 | What it is for |
|---|---|---|---|
| `empty.txt` | 0 | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` | empty input → zero chunks, and a golden that says exactly that |
| `whitespace-only.txt` | 7 | `3018a85eec1be9ff91c1060df01a9c0474bf9faf334bf9f363a7649b82a16fe4` | `"   \n\n\t\n"` — no chunk, no *empty* chunk, no throw |
| `three-paragraphs.txt` | 190 | `9469d9507868eaf0a931eb7fde7aeda5bc34cbb251c92f2c89c03a0b6bed9e99` | paragraph blocks, a sentence-aware cut, and **no trailing newline** |
| `crlf-and-lone-cr.txt` | 71 | `8f452179905c47b2baa6a17f3709e2c008bea9a4f591f24c355362a852e37ec9` | CRLF **and** a lone CR, both normalised to LF; offsets are into the normalised buffer |
| `long-token.txt` | 5000 | `260679791fa8da4dddc6aa3b243c514025e83d3a2f60800b9734b990be5d11a0` | 5,000 `A` with no whitespace — forces the hard split where there is no separator to back up to |
| `unicode.txt` | 136 | `e6b237d83b94fd4add1b7389d3e188436791b27cc351f8ae970ee7460e3498dc` | é as NFC **and** NFD, a ZWJ family emoji, CJK, an RTL run, and `I İ i ı` |
| `bom.txt` | 74 | `c1ea8c926fadbbabcbac1cd47ef9f6baeeca3595a612ff0abe62e04750d029d6` | a UTF-8 BOM, stripped, with offsets starting after it |

Exact content, LF endings throughout:

- `empty.txt` — zero bytes.
- `whitespace-only.txt` — `"   \n\n\t\n"`.
- `three-paragraphs.txt` —

  ```
  The first paragraph is short.

  The second paragraph has two sentences. It exists so a sentence-aware cut has somewhere to land.

  The third paragraph ends the file without a trailing newline.
  ```

  with **no** final newline.
- `crlf-and-lone-cr.txt` — `"line one\r\nline two\rline three\r\n\r\nsecond block after a CRLF blank line\r\n"`. This is the one file `corpus/** -text` exists for; write it with explicit `\r` bytes and never re-save it in an editor.
- `long-token.txt` — `A` repeated 5,000 times, no newline.
- `unicode.txt` — six lines, each ending in LF: `NFC: café résumé`, `NFD: cafe<U+0301> re<U+0301>sume<U+0301>` with real combining acutes, `ZWJ: 👨‍👩‍👧`, `CJK: 漢字テキスト`, `RTL: مرحبا بالعالم`, `TR: I İ i ı`.
- `bom.txt` — the bytes `EF BB BF` then `"A document that opens with a UTF-8 byte order mark.\n\nSecond paragraph.\n"`.

- [ ] **Step 2: the five Markdown fixtures**

`corpus/markdown/`.

| File | Bytes | SHA-256 | What it is for |
|---|---|---|---|
| `headings.md` | 402 | `0bbb67d71f47465d3daaf4053b831736d2c16efa2a59a0887f808f5bdc048bdd` | YAML front matter, a preamble before the first heading, h1/h2/h3/h4, a setext h1, and a `#` line **inside a fence** |
| `fences.md` | 248 | `4a1099e8a352413935074d858ca6b8ca1971495701df344fec74cbe2dff071d2` | a tilde fence, three backticks nested inside a four-tick fence, an indented code block — three ways a `##` line is **not** a heading |
| `tables-lists.md` | 190 | `41724421125458cfb0422f87602881b664368e66c6856bd125f7715ad1e61e70` | a header plus three body rows in a pipe table, a nested bullet list, an ordered list |
| `raw-html.md` | 163 | `a5be2e6cc68aa802d0e99dcab9015538315d10e99e789cde24245c4d69dadaf9` | an `HtmlBlock` and inline HTML, both kept as verbatim source substrings |
| `giant-heading-section.md` | 3866 | `e706d8fcc5701cb7d3a6b0bbf5c5252eab80331fa86429a19b5dd02f93be6a37` | one h2 section of forty sentences, far over the 222-token budget: must split **and** keep the breadcrumb on every piece |

**All five are given below character for character, and the five digests above were measured on 2026-09-11 by writing exactly these bytes and hashing them.** That is not ceremony: Step 7 asserts each digest as a literal, and an implementer cannot author bytes that hash to a given SHA-256 from a prose description. A fixture whose content is described rather than written makes its own verify unsatisfiable — which is what an earlier draft of this task did to `fences.md`, `tables-lists.md` and `raw-html.md`. Write these files with **LF endings, UTF-8, no BOM**, and with the final newline each one shows.

`headings.md` — 402 bytes:

```markdown
---
title: Heading fixture
tags: [alpha, beta]
---

Preamble text that belongs to no heading at all.

# Top level

Body under the h1.

## Second level

Body under the h2.

```text
# not a heading, this line is inside a fence
```

### Third level

Body under the h3.

Setext heading
==============

Body under the setext h1.

#### Fourth level

Body under the h4, which must stay inside its h3 section.
```

(The inner ```` ```text ```` … ```` ``` ```` pair is part of the fixture's content, not of this plan's code fence. When copying, take the 28 lines from `---` to `Body under the h4, …` inclusive, plus a trailing newline.)

`fences.md` — 248 bytes, seventeen lines plus a final newline:

```
# Fences
<blank>
~~~
tilde fenced block
## not a heading, this line is inside a tilde fence
~~~
<blank>
````
```
three backticks inside a four-tick fence
```
````
<blank>
    indented code block
    ## not a heading either
<blank>
Closing paragraph after three kinds of code.
```

Every `<blank>` is an empty line. Lines 14 and 15 begin with exactly **four spaces** — that is the indented code block, and a tab or three spaces changes both the parse and the digest. The four-backtick fence on lines 8 and 12 contains a three-backtick pair on lines 9 and 11; Markdig must produce **one** `FencedCodeBlock` there, not three blocks, and `MarkdownExtractorTests` asserts exactly that.

`tables-lists.md` — 190 bytes:

```markdown
# Tables and lists

| Column A | Column B |
| --- | --- |
| a1 | b1 |
| a2 | b2 |
| a3 | b3 |

- first bullet
  - nested bullet
- second bullet

1. first ordered item
2. second ordered item
```

The nested bullet is indented by exactly **two spaces**. The table is one header row, one delimiter row and three body rows, so `MarkdownHeadingChunker`'s row-wise table split has three rows to split and one header to re-emit.

`raw-html.md` — 163 bytes:

```markdown
# Raw HTML

<div class="note">
  <p>A block of raw HTML that Markdig keeps as one HtmlBlock.</p>
</div>

A paragraph with <em>inline</em> HTML and a <br /> break.
```

The `<div>` … `</div>` run is three lines and must come back as **one** `HtmlBlock`, not three paragraphs; the last line is a `ParagraphBlock` whose source substring still contains its `<em>` and `<br />` verbatim, because the chunk text is a source slice and never a re-render.

`giant-heading-section.md` — 3866 bytes — is `# Giant`, a blank line, `## One oversized section`, a blank line, then **one** paragraph built as `"Sentence number {i} of a section that is far larger than any chunk budget this package resolves."` for `i` in 1..40 joined by a single space, then a final newline. It is generated rather than transcribed for readability, and the digest above was measured from exactly that construction; `manifest.json` and Step 7 are what hold it.

- [ ] **Step 3: `make_pdf_fixtures.py`**

A checked-in regeneration path, never a build step. It writes uncompressed, pure-7-bit-ASCII PDFs with a classic xref table whose byte offsets **it** computes — the one part of a hand-written PDF nobody should compute by hand.

```python
#!/usr/bin/env python3
"""Regenerate ingestion/tests/fixtures/corpus/pdf/*.pdf.

NEVER a build step, and CI never runs this. The OUTPUT is committed; run this by hand only when a
fixture's content must change, and put the reason in the PR body. Requires CPython 3.11 or newer
(bytes.__mod__ formatting); this box has 3.14.5. No third-party module, no venv needed.

Every document here is uncompressed 7-bit ASCII so a reviewer can read the diff. The one Flate
path that cannot be spelled in ASCII is flate-content.stream, which the test project deflates at
a fixed CompressionLevel - DeflateStream is deterministic where PdfPig's trailer /ID is not.
"""
import pathlib
import sys

OUT = pathlib.Path(__file__).parent / "corpus" / "pdf"


def ashex(raw: bytes) -> bytes:
    """ASCIIHexDecode-encode, EOD marker included. This is what keeps a PDF 1.5 file 7-bit."""
    return b"".join(b"%02x" % b for b in raw) + b">"


def xref_row(kind: int, field2: int, field3: int) -> bytes:
    """One /W [1 4 2] cross-reference-stream entry, big-endian as the spec requires."""
    return bytes([kind]) + field2.to_bytes(4, "big") + field3.to_bytes(2, "big")


def build(objects: list[bytes], root_obj: int = 1) -> bytes:
    """Assemble numbered objects into a PDF with a correct classic xref table."""
    out = bytearray(b"%PDF-1.4\n")
    offsets = [0]
    for i, body in enumerate(objects, start=1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % i + body + b"\nendobj\n"
    xref_at = len(out)
    out += b"xref\n0 %d\n" % (len(objects) + 1)
    out += b"0000000000 65535 f \n"
    for off in offsets[1:]:
        out += b"%010d 00000 n \n" % off
    out += b"trailer\n<< /Size %d /Root %d 0 R >>\n" % (len(objects) + 1, root_obj)
    out += b"startxref\n%d\n%%%%EOF\n" % xref_at
    return bytes(out)


def simple(content: bytes, pages: int = 1) -> bytes:
    """One Helvetica Type1 font; `pages` pages sharing one content stream."""
    kids = b" ".join(b"%d 0 R" % (4 + i) for i in range(pages))
    objs = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [%s] /Count %d >>" % (kids, pages),
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ]
    stream_obj = 4 + pages
    for _ in range(pages):
        objs.append(
            b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            b"/Resources << /Font << /F1 3 0 R >> >> /Contents %d 0 R >>" % stream_obj
        )
    objs.append(b"<< /Length %d >>\nstream\n" % len(content) + content + b"\nendstream")
    return build(objs)


def text(lines: list[str], x: int = 72, y: int = 720, leading: int = 14) -> bytes:
    body = ["BT", "/F1 12 Tf", "%d TL" % leading, "%d %d Td" % (x, y)]
    for line in lines:
        escaped = line.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")
        body.append("(%s) Tj T*" % escaped)
    body.append("ET")
    return ("\n".join(body) + "\n").encode("ascii")


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True)

    # 1. minimal-text: the reference shape. Five objects, classic xref, Helvetica Type1.
    (OUT / "minimal-text.pdf").write_bytes(
        simple(text(["Hello from a minimal PDF.", "A second line of the same paragraph."]))
    )

    # 2. two-pages: page numbers must reach DocumentBlock.PageNumber and ChunkDraft.Page.
    (OUT / "two-pages.pdf").write_bytes(
        simple(text(["Page content shared by both pages of this fixture."]), pages=2)
    )

    # 3. two-columns: two text runs at different x in the same y band - reading order.
    left = text(["Left column line one.", "Left column line two."], x=72)
    right = text(["Right column line one.", "Right column line two."], x=330)
    (OUT / "two-columns.pdf").write_bytes(simple(left + right))

    # 4. hyphen-linebreak: JoinHyphenatedLineBreaks must rejoin "extra-" + "ordinary".
    (OUT / "hyphen-linebreak.pdf").write_bytes(
        simple(text(["This paragraph contains an extra-", "ordinary hyphenated line break."]))
    )

    # 5. no-text-layer: a filled rectangle and a tiny inline image, no text operators at all.
    #    Must yield IngestionDocumentStatus.NoTextLayer, never an exception. The four image
    #    bytes are the ONLY non-ASCII bytes in the whole corpus, and Step 7 exempts this file.
    rect = (
        b"0.5 0.5 0.5 rg\n72 600 200 120 re f\n"
        b"q 200 0 0 120 72 400 cm\nBI /W 2 /H 2 /CS /G /BPC 8 ID \x00\x40\x80\xc0 EI Q\n"
    )
    (OUT / "no-text-layer.pdf").write_bytes(simple(rect))

    # 6. xref-stream: a PDF 1.5 cross-reference stream plus an object stream, both
    #    ASCIIHexDecode so the file stays reviewable. See build_xref_stream's docstring.
    (OUT / "xref-stream.pdf").write_bytes(build_xref_stream())

    # 7. broken-startxref: minimal-text with startxref pointing past EOF. The error-path test
    #    asserts DocumentMalformed (6104) and that the run continues.
    broken = bytearray((OUT / "minimal-text.pdf").read_bytes())
    i = broken.rindex(b"startxref\n") + len(b"startxref\n")
    j = broken.index(b"\n", i)
    broken[i:j] = b"999999"
    (OUT / "broken-startxref.pdf").write_bytes(bytes(broken))

    # The Flate content stream, deflated by the TEST at a fixed CompressionLevel.
    (OUT / "flate-content.stream").write_bytes(
        text(["This content stream is stored with FlateDecode.", "Two lines, one paragraph."])
    )

    for p in sorted(OUT.iterdir()):
        print("%-28s %6d" % (p.name, p.stat().st_size))
    return 0


def build_xref_stream() -> bytes:
    """Emit a PDF 1.5 file: a /Type /XRef cross-reference stream plus an object stream, both
    ASCIIHexDecode so the file stays reviewable 7-bit ASCII.

    Written longhand because build() emits a classic xref table. Object layout:

        1  Catalog          plain, type-1 xref entry
        2  Pages            plain, type-1
        3  Page             INSIDE the object stream -> type-2 entry (objstm 6, index 0)
        4  Font             plain, type-1
        5  Content stream   plain, type-1
        6  ObjStm           plain, type-1; holds object 3
        7  XRef stream      plain, type-1, and its own entry points at itself

    There is no trailer dictionary: /Size and /Root live on the XRef stream's own dict, which is
    what a 1.5 file does and what the classic-xref fixtures cannot exercise. /Index is omitted,
    so it defaults to [0 /Size] - every object from 0 to 7, in order, which is the layout below.
    """
    content = text([
        "A PDF 1.5 file whose cross-reference is a stream.",
        "The page dictionary lives in an object stream.",
    ])

    page = (
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        b"/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"
    )
    # An ObjStm's decoded payload is a pair list "objnum offset ..." followed, at byte /First,
    # by the object bodies. One object here, so one pair and an offset of 0.
    pairs = b"3 0\n"
    objstm_plain = pairs + page
    first = len(pairs)

    out = bytearray(b"%PDF-1.5\n")
    offsets: dict[int, int] = {}

    def emit(num: int, body: bytes) -> None:
        offsets[num] = len(out)
        out.extend(b"%d 0 obj\n" % num)
        out.extend(body)
        out.extend(b"\nendobj\n")

    emit(1, b"<< /Type /Catalog /Pages 2 0 R >>")
    emit(2, b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
    emit(4, b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    emit(5, b"<< /Length %d >>\nstream\n" % len(content) + content + b"\nendstream")

    stm = ashex(objstm_plain)
    emit(
        6,
        b"<< /Type /ObjStm /N 1 /First %d /Filter /ASCIIHexDecode /Length %d >>\nstream\n"
        % (first, len(stm))
        + stm
        + b"\nendstream",
    )

    # The XRef stream's own offset is knowable before it is serialised, because it is last - so
    # there is no circularity, only an ordering rule: compute the table, then emit object 7.
    xref_at = len(out)
    table = b"".join(
        [
            xref_row(0, 0, 65535),          # 0: head of the free list
            xref_row(1, offsets[1], 0),
            xref_row(1, offsets[2], 0),
            xref_row(2, 6, 0),              # 3: compressed, in object stream 6 at index 0
            xref_row(1, offsets[4], 0),
            xref_row(1, offsets[5], 0),
            xref_row(1, offsets[6], 0),
            xref_row(1, xref_at, 0),        # 7: this stream, pointing at itself
        ]
    )
    xr = ashex(table)
    emit(
        7,
        b"<< /Type /XRef /Size 8 /W [1 4 2] /Root 1 0 R /Filter /ASCIIHexDecode /Length %d >>\nstream\n"
        % len(xr)
        + xr
        + b"\nendstream",
    )
    out.extend(b"startxref\n%d\n%%%%EOF\n" % xref_at)
    return bytes(out)


if __name__ == "__main__":
    sys.exit(main())
```

**`build_xref_stream` is written above, in full, and `xref-stream.pdf` ships** (plan adjustment 25). An earlier draft of this task left the function a `NotImplementedError` with "drop the fixture" as a documented fallback, which failed the plan's own **Convention for "literal code"** and left the one PDF shape every modern writer emits — a 1.5 cross-reference stream with a compressed object — with no committed coverage and no task obliged to produce it. The three things that make it writable rather than a guess are all in the code: `/Index` is omitted so it defaults to `[0 /Size]` and the entries are simply objects 0 through 7 in order; `/W [1 4 2]` fixes the field widths and `xref_row` packs them big-endian; and the stream's own offset is knowable before it is serialised because it is the last object in the file, so the table has no circular dependency on its own length.

Run the script. Seven `.pdf` files and one `.stream` are written; Step 3b opens all seven with the real PdfPig, Step 6's manifest records their bytes, and Step 7 asserts there are exactly seven.

- [ ] **Step 3b: Open all seven PDFs with the real PdfPig, before they are committed**

A generated fixture that no parser has opened is a fixture that fails three waves later, in Task 6.1, in a task that does not own this folder and cannot fix it without crossing a wave boundary. So the check happens **here**, in the task that writes the bytes — and it happens in a **throwaway console outside the repo**, the same idiom Task 2.1 Step 1 uses, so this task still compiles nothing in the tree and wave 1 stays parallel-safe (Task 1.4 is the only writer of any repo `obj/`).

```powershell
$probe = "D:\Local\Temp\qedge-sp3\fixture-probe"
Remove-Item $probe -Recurse -Force -ErrorAction SilentlyContinue
dotnet new console -o $probe --framework net10.0 | Out-Null
dotnet add $probe package PdfPig --version 0.1.16 | Out-Null
```

`$probe\Program.cs`:

```csharp
using UglyToad.PdfPig;

var dir = args[0];
var expected = new (string File, int Pages, string Contains)[]
{
    ("minimal-text.pdf",     1, "minimal PDF"),
    ("two-pages.pdf",        2, "Page content"),
    ("two-columns.pdf",      1, "Left column"),
    ("hyphen-linebreak.pdf", 1, "ordinary"),
    ("no-text-layer.pdf",    1, ""),          // "" means: assert there is NO text layer
    ("xref-stream.pdf",      1, "object stream"),
};

var bad = 0;
foreach (var (file, pages, contains) in expected)
{
    var path = Path.Combine(dir, file);
    try
    {
        using var stream = File.OpenRead(path);
        using var doc = PdfDocument.Open(stream);
        var all = string.Join("\n", doc.GetPages()
            .Select(p => string.Concat(p.Letters.Select(l => l.Value))));

        if (doc.NumberOfPages != pages)
        {
            Console.WriteLine($"FAIL {file}: {doc.NumberOfPages} pages, expected {pages}");
            bad++;
        }
        else if (contains.Length == 0 && all.Trim().Length != 0)
        {
            Console.WriteLine($"FAIL {file}: expected no text layer, got '{all}'");
            bad++;
        }
        else if (contains.Length != 0 && !all.Contains(contains, StringComparison.Ordinal))
        {
            Console.WriteLine($"FAIL {file}: text does not contain '{contains}' (got '{all}')");
            bad++;
        }
        else
        {
            Console.WriteLine($"OK   {file}: {doc.NumberOfPages} page(s), {all.Length} chars");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {file}: {ex.GetType().Name}: {ex.Message}");
        bad++;
    }
}

// broken-startxref is NOT asserted, it is OBSERVED. PdfPig may recover from a bad startxref by
// brute-force scanning, and which way it goes decides where Task 6.1 raises 6104. Record the
// printed line in this task's output either way.
try
{
    using var s = File.OpenRead(Path.Combine(dir, "broken-startxref.pdf"));
    using var d = PdfDocument.Open(s);
    var n = d.GetPages().Count();
    Console.WriteLine($"NOTE broken-startxref.pdf RECOVERED and yielded {n} page(s)");
}
catch (Exception ex)
{
    Console.WriteLine($"NOTE broken-startxref.pdf REJECTED: {ex.GetType().Name}");
}

return bad == 0 ? 0 : 1;
```

```powershell
dotnet run --project $probe -c Release -- "C:\Users\steve\projects\qavren-edge-sp3\ingestion\tests\fixtures\corpus\pdf"
if ($LASTEXITCODE -ne 0) { throw "a committed PDF fixture does not open in PdfPig 0.1.16" }
```

Expected: six `OK` lines and one `NOTE`. A `FAIL` on `xref-stream.pdf` is a bug in `build_xref_stream` and is fixed **here** — it is twenty lines of offset arithmetic with a parser to check it against, not an open-ended research task. Carry the `NOTE` line into the task's output: if it says **RECOVERED**, Task 6.1 Step 5's 6104 assertion has no raise site on this fixture and the plan owes it one (record it as an Open risk in the wave-1 close, and let Task 6.1 raise 6104 from a truncated-object PDF built at test time instead).

- [ ] **Step 4: `requirements.txt` and the fixtures README**

`requirements.txt` exists with a single comment line saying the generator needs nothing but CPython **3.11 or newer** (`bytes.__mod__`; this box has 3.14.5 — see Environment ground truth), so the directory shape matches `embeddings/tools/model-hashes/`.

`README.md` states: what each fixture is for; that the corpus is committed and the generator is never a build step; that `corpus/** -text` is load-bearing and why; and that `manifest.json` is regenerated only in a PR whose body says why. Its **last** heading is a placeholder, written empty, for the one amendment another wave makes:

```markdown
## Golden generation

<!-- Filled by Task 4.1 Step 6 (wave 4): which vocabulary the twenty goldens were generated
     against and its SHA-256, the MiniLM triple 222/32/27 they pin, and the
     QAVREN_EDGE_WRITE_GOLDEN rule. Do not delete this heading; Task 4.1 replaces this comment
     and nothing else in this file. -->
```

That is the handover named under **Cross-wave file ownership**: this task is the file's first writer, Task 4.1 is its second and last, three waves later, and neither appends to a shape the other did not give it.

- [ ] **Step 5: the eleven DOCX part-XML trees**

`corpus/docx/<name>/` each holding `[Content_Types].xml`, `_rels/.rels`, `word/document.xml` and whatever else that fixture needs. **No zip is committed**: Task 6.1's `DeterministicOpc.Build` assembles them at test time.

Eleven hand-written OPC trees whose `w:outlineLvl`, `w:txbxContent`, rsid-split runs and absent-style `pStyle` exist to drive specific branches of `DocxTextExtractor` are exactly the artifact this plan's **Convention for "literal code"** says it originates — so every part is given below, in full, and every tree states the **expected extraction output** under `DocxExtractorOptions`' defaults. A tree described by its purpose alone leaves the implementer to invent the XML, and then the test measures the invention rather than the extractor. All 47 parts below were written and parsed on 2026-09-11; all eleven `word/document.xml` are well-formed.

Write every part as **UTF-8 with LF endings, no BOM**, and with the XML declaration line shown. `<blank>` never appears — these files have no blank lines.

**Three parts are shared verbatim by several trees.** Write them once and copy.

`_rels/.rels` — identical in **all eleven** trees:

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml" />
</Relationships>
```

`[Content_Types].xml` — the **base** form, used verbatim by `run-split`, `table`, `hyperlink`, `textbox` and `empty-body`:

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
  <Default Extension="xml" ContentType="application/xml" />
  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml" />
</Types>
```

The other six trees are the base form **plus** one or two `<Override>` lines inserted before `</Types>`, taken from this list by the extra parts they carry:

```xml
  <Override PartName="/word/styles.xml"    ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml" />
  <Override PartName="/word/numbering.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml" />
  <Override PartName="/word/footnotes.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml" />
  <Override PartName="/word/header1.xml"   ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml" />
  <Override PartName="/word/footer1.xml"   ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml" />
```

Throughout, `W` stands for `xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"` and `R` for `xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"`; write them out in full where the tree shows them.

| # | Tree | Parts | Content-Types overrides | What it proves |
|---|---|---|---|---|
| 1 | `headings` | 5 | styles | `w:outlineLvl` on the paragraph resolves h1/h2/h3 |
| 2 | `run-split` | 3 | — | one sentence across five `w:r` with rsid noise is **one** paragraph block. The single most common extractor bug |
| 3 | `table` | 3 | — | `TableRow` blocks, pipe-joined, header row first |
| 4 | `numbered-list` | 5 | numbering | `ListItem` blocks |
| 5 | `footnotes` | 5 | footnotes | notes included when `IncludeNotes` |
| 6 | `header-footer` | 6 | header + footer | **excluded** by default — they repeat on every page and poison embeddings |
| 7 | `hyperlink` | 4 | — | link text survives; the target does not become body text |
| 8 | `textbox` | 3 | — | `w:txbxContent` is reached; routinely missed |
| 9 | `empty-body` | 3 | — | zero blocks, no throw |
| 10 | `unknown-style` | 5 | styles | a `pStyle` naming an absent style degrades to a paragraph, never throws |
| 11 | `localised-style` | 5 | styles | the outline-level path, not a `Heading{n}` regex |

**1. `headings`** — `[Content_Types].xml` (+styles), `_rels/.rels`, `word/_rels/document.xml.rels`, `word/styles.xml`, `word/document.xml`.

```xml
<!-- word/_rels/document.xml.rels -->
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml" />
</Relationships>
```

```xml
<!-- word/styles.xml -->
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:styles W>
  <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1" /><w:pPr><w:outlineLvl w:val="0" /></w:pPr></w:style>
</w:styles>
```

```xml
<!-- word/document.xml -->
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document W>
  <w:body>
    <w:p>
      <w:pPr><w:outlineLvl w:val="0" /></w:pPr>
      <w:r><w:t xml:space="preserve">Top level heading</w:t></w:r>
    </w:p>
    <w:p>
      <w:r><w:t xml:space="preserve">Body under the h1.</w:t></w:r>
    </w:p>
    <w:p>
      <w:pPr><w:outlineLvl w:val="1" /></w:pPr>
      <w:r><w:t xml:space="preserve">Second level heading</w:t></w:r>
    </w:p>
    <w:p>
      <w:r><w:t xml:space="preserve">Body under the h2.</w:t></w:r>
    </w:p>
    <w:p>
      <w:pPr><w:outlineLvl w:val="2" /></w:pPr>
      <w:r><w:t xml:space="preserve">Third level heading</w:t></w:r>
    </w:p>
    <w:p>
      <w:r><w:t xml:space="preserve">Body under the h3.</w:t></w:r>
    </w:p>
  </w:body>
</w:document>
```

**Expected output:** six blocks, in order — `Heading`/level 1 `"Top level heading"`, `Paragraph` `"Body under the h1."`, `Heading`/level 2 `"Second level heading"`, `Paragraph` `"Body under the h2."`, `Heading`/level 3 `"Third level heading"`, `Paragraph` `"Body under the h3."`. `styles.xml` is present and names `Heading1`, but **no paragraph references it** — the levels come from `w:outlineLvl` alone, which is the point: a style-name path would resolve nothing here and the test would still pass if the extractor secretly used one, so it must not be given the option.

**2. `run-split`** — `[Content_Types].xml` (base), `_rels/.rels`, `word/document.xml`.

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document W>
  <w:body>
    <w:p w:rsidR="00A1B2C3" w:rsidRDefault="00A1B2C3">
      <w:r w:rsidR="00A1B2C3"><w:t xml:space="preserve">One sentence </w:t></w:r>
      <w:r w:rsidR="00D4E5F6"><w:t xml:space="preserve">split across </w:t></w:r>
      <w:r w:rsidR="00112233"><w:t xml:space="preserve">five separate </w:t></w:r>
      <w:r w:rsidR="00445566"><w:t xml:space="preserve">runs with rsid </w:t></w:r>
      <w:r w:rsidR="00778899"><w:t>noise.</w:t></w:r>
    </w:p>
  </w:body>
</w:document>
```

**Expected output:** **one** `Paragraph` block whose text is exactly `"One sentence split across five separate runs with rsid noise."` — 60 characters, one space between each pair of runs and none doubled. Every `w:t` but the last carries `xml:space="preserve"` and a trailing space; an extractor that trims per-run produces `"One sentencesplit across…"` and an extractor that inserts its own separator produces doubled spaces. Both are failures and the assertion is on the whole string, not on a substring.

**3. `table`** — `[Content_Types].xml` (base), `_rels/.rels`, `word/document.xml`.

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document W>
  <w:body>
    <w:tbl>
      <w:tr>
        <w:tc><w:p><w:r><w:t>Region</w:t></w:r></w:p></w:tc>
        <w:tc><w:p><w:r><w:t>Total</w:t></w:r></w:p></w:tc>
      </w:tr>
      <w:tr>
        <w:tc><w:p><w:r><w:t>North</w:t></w:r></w:p></w:tc>
        <w:tc><w:p><w:r><w:t>12</w:t></w:r></w:p></w:tc>
      </w:tr>
      <w:tr>
        <w:tc><w:p><w:r><w:t>South</w:t></w:r></w:p></w:tc>
        <w:tc><w:p><w:r><w:t>34</w:t></w:r></w:p></w:tc>
      </w:tr>
    </w:tbl>
    <w:p>
      <w:r><w:t xml:space="preserve">A paragraph after the table.</w:t></w:r>
    </w:p>
  </w:body>
</w:document>
```

**Expected output:** four blocks — three `TableRow` (`"Region | Total"`, `"North | 12"`, `"South | 34"`) then one `Paragraph` (`"A paragraph after the table."`). The join is `" | "`, the header row is the first `w:tr` and is emitted first, and the paragraph after the table is **not** swallowed into it.

**4. `numbered-list`** — `[Content_Types].xml` (+numbering), `_rels/.rels`, `word/_rels/document.xml.rels` (one `numbering` relationship to `numbering.xml`), `word/numbering.xml`, `word/document.xml`.

```xml
<!-- word/numbering.xml -->
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:numbering W>
  <w:abstractNum w:abstractNumId="0"><w:lvl w:ilvl="0"><w:numFmt w:val="decimal" /><w:lvlText w:val="%1." /></w:lvl></w:abstractNum>
  <w:num w:numId="1"><w:abstractNumId w:val="0" /></w:num>
</w:numbering>
```

```xml
<!-- word/document.xml -->
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document W>
  <w:body>
    <w:p>
      <w:pPr><w:numPr><w:ilvl w:val="0" /><w:numId w:val="1" /></w:numPr></w:pPr>
      <w:r><w:t xml:space="preserve">First list item.</w:t></w:r>
    </w:p>
    <w:p>
      <w:pPr><w:numPr><w:ilvl w:val="0" /><w:numId w:val="1" /></w:numPr></w:pPr>
      <w:r><w:t xml:space="preserve">Second list item.</w:t></w:r>
    </w:p>
    <w:p>
      <w:r><w:t xml:space="preserve">A plain paragraph that is not a list item.</w:t></w:r>
    </w:p>
  </w:body>
</w:document>
```

**Expected output:** three blocks — `ListItem` `"First list item."`, `ListItem` `"Second list item."`, `Paragraph` `"A plain paragraph that is not a list item."`. The kind comes from the presence of `w:numPr`, and the third paragraph proves the extractor stops treating paragraphs as list items once `w:numPr` is gone. The `%1.` in `w:lvlText` is **not** rendered into the text; a `"1. First list item."` is a failure.

**5. `footnotes`** — `[Content_Types].xml` (+footnotes), `_rels/.rels`, `word/_rels/document.xml.rels` (one `footnotes` relationship to `footnotes.xml`), `word/footnotes.xml`, `word/document.xml`.

```xml
<!-- word/footnotes.xml -->
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:footnotes W>
  <w:footnote w:id="1"><w:p><w:r><w:t>The footnote body text.</w:t></w:r></w:p></w:footnote>
</w:footnotes>
```

```xml
<!-- word/document.xml -->
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document W>
  <w:body>
    <w:p>
      <w:r><w:t xml:space="preserve">Body text with a note reference.</w:t></w:r>
      <w:r><w:footnoteReference w:id="1" /></w:r>
    </w:p>
  </w:body>
</w:document>
```

**Expected output** under `IncludeNotes = true` (the default): two blocks — `Paragraph` `"Body text with a note reference."` then `Footer` `"The footnote body text."`, the note appended **after** the body and never spliced into the referencing paragraph. Under `IncludeNotes = false`: one block, the paragraph alone. Both are asserted; the second is what makes the option mean something.

**6. `header-footer`** — `[Content_Types].xml` (+header +footer), `_rels/.rels`, `word/_rels/document.xml.rels` (a `header` relationship `rId1` → `header1.xml` and a `footer` relationship `rId2` → `footer1.xml`), `word/header1.xml`, `word/footer1.xml`, `word/document.xml`.

```xml
<!-- word/header1.xml -->
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:hdr W>
  <w:p><w:r><w:t>RUNNING HEADER THAT MUST NOT BE INDEXED</w:t></w:r></w:p>
</w:hdr>
```

```xml
<!-- word/footer1.xml -->
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:ftr W>
  <w:p><w:r><w:t>RUNNING FOOTER THAT MUST NOT BE INDEXED</w:t></w:r></w:p>
</w:ftr>
```

```xml
<!-- word/document.xml -->
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document W R>
  <w:body>
    <w:p>
      <w:r><w:t xml:space="preserve">The only body paragraph.</w:t></w:r>
    </w:p>
    <w:sectPr>
      <w:headerReference w:type="default" r:id="rId1" />
      <w:footerReference w:type="default" r:id="rId2" />
    </w:sectPr>
  </w:body>
</w:document>
```

**Expected output** under `ExcludeHeadersAndFooters = true` (the default): **one** block, `Paragraph` `"The only body paragraph."`, and `ExtractedDocument.Text` contains neither `"RUNNING HEADER"` nor `"RUNNING FOOTER"` — asserted as two `DoesNotContain`, because the whole point is an absence. With the option flipped to `false`: three blocks, header first, footer last. The screaming caps are deliberate; a substring assertion against them cannot pass by accident.

**7. `hyperlink`** — `[Content_Types].xml` (base), `_rels/.rels`, `word/_rels/document.xml.rels`, `word/document.xml`.

```xml
<!-- word/_rels/document.xml.rels -->
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://example.invalid/target" TargetMode="External" />
</Relationships>
```

```xml
<!-- word/document.xml -->
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document W R>
  <w:body>
    <w:p>
      <w:r><w:t xml:space="preserve">See </w:t></w:r>
      <w:hyperlink r:id="rId1"><w:r><w:t>the linked phrase</w:t></w:r></w:hyperlink>
      <w:r><w:t xml:space="preserve"> for details.</w:t></w:r>
    </w:p>
  </w:body>
</w:document>
```

**Expected output:** one `Paragraph` block, text exactly `"See the linked phrase for details."` — the link **text** is inline in the paragraph and the **target** appears nowhere in `ExtractedDocument.Text`. Asserted both ways: equality on the string, and `DoesNotContain("example.invalid")`. A URL in the body text is a URL in the embedding, and `example.invalid` is a reserved TLD so nothing resolves it if a test ever does try.

**8. `textbox`** — `[Content_Types].xml` (base), `_rels/.rels`, `word/document.xml`.

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document W xmlns:v="urn:schemas-microsoft-com:vml">
  <w:body>
    <w:p>
      <w:r><w:t xml:space="preserve">Body paragraph outside the text box.</w:t></w:r>
    </w:p>
    <w:p>
      <w:r>
        <w:pict>
          <v:shape id="_x0000_s1026" style="width:200pt;height:50pt">
            <v:textbox>
              <w:txbxContent>
                <w:p><w:r><w:t>Text inside a w:txbxContent box.</w:t></w:r></w:p>
              </w:txbxContent>
            </v:textbox>
          </v:shape>
        </w:pict>
      </w:r>
    </w:p>
  </w:body>
</w:document>
```

**Expected output** under `IncludeTextBoxes = true` (the default): two `Paragraph` blocks — `"Body paragraph outside the text box."` then `"Text inside a w:txbxContent box."`. Under `IncludeTextBoxes = false`: one block, the first. The VML namespace declaration on `w:document` is load-bearing; without it the part is not well-formed and the fixture fails to open rather than failing to extract, which is a different bug wearing the same message.

**9. `empty-body`** — `[Content_Types].xml` (base), `_rels/.rels`, `word/document.xml`.

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document W>
  <w:body />
</w:document>
```

**Expected output:** `ExtractedDocument.Text` is `""`, `Blocks` is empty, `HasTextLayer` is **true** (this is a DOCX, not a scan — there is nothing to recognise, which is not the same as an unrecognised image), no exception, and the document is recorded `Indexed` with zero chunks rather than `Failed`. The self-closing `<w:body />` is deliberate: an extractor that assumes a child element dereferences null here.

**10. `unknown-style`** — `[Content_Types].xml` (+styles), `_rels/.rels`, `word/_rels/document.xml.rels` (one `styles` relationship), `word/styles.xml`, `word/document.xml`.

```xml
<!-- word/styles.xml -->
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:styles W>
  <w:style w:type="paragraph" w:styleId="Normal"><w:name w:val="Normal" /></w:style>
</w:styles>
```

```xml
<!-- word/document.xml -->
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document W>
  <w:body>
    <w:p>
      <w:pPr><w:pStyle w:val="StyleThatDoesNotExist" /></w:pPr>
      <w:r><w:t xml:space="preserve">A paragraph whose pStyle names a style that is not in styles.xml.</w:t></w:r>
    </w:p>
    <w:p>
      <w:r><w:t xml:space="preserve">A second, ordinary paragraph.</w:t></w:r>
    </w:p>
  </w:body>
</w:document>
```

**Expected output:** two blocks, **both `Paragraph`**, neither `Heading`, `HeadingLevel` null on both, and **no exception**. `styles.xml` deliberately contains only `Normal`, so the lookup for `StyleThatDoesNotExist` misses — and a miss must degrade to a paragraph. This is the fixture that fails if the style lookup is written with `Single()` or an indexer instead of a `TryGet`.

**11. `localised-style`** — `[Content_Types].xml` (+styles), `_rels/.rels`, `word/_rels/document.xml.rels` (one `styles` relationship), `word/styles.xml`, `word/document.xml`.

```xml
<!-- word/styles.xml -->
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:styles W>
  <w:style w:type="paragraph" w:styleId="Titre1"><w:name w:val="Titre 1" /><w:pPr><w:outlineLvl w:val="0" /></w:pPr></w:style>
  <w:style w:type="paragraph" w:styleId="berschrift2"><w:name w:val="Überschrift 2" /><w:pPr><w:outlineLvl w:val="1" /></w:pPr></w:style>
</w:styles>
```

```xml
<!-- word/document.xml -->
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document W>
  <w:body>
    <w:p>
      <w:pPr><w:pStyle w:val="Titre1" /></w:pPr>
      <w:r><w:t xml:space="preserve">Titre de premier niveau</w:t></w:r>
    </w:p>
    <w:p>
      <w:r><w:t xml:space="preserve">Corps sous le titre.</w:t></w:r>
    </w:p>
    <w:p>
      <w:pPr><w:pStyle w:val="berschrift2" /></w:pPr>
      <w:r><w:t xml:space="preserve">Zweite Ebene</w:t></w:r>
    </w:p>
    <w:p>
      <w:r><w:t xml:space="preserve">Text unter der zweiten Ebene.</w:t></w:r>
    </w:p>
  </w:body>
</w:document>
```

**Expected output:** four blocks — `Heading`/level 1 `"Titre de premier niveau"`, `Paragraph` `"Corps sous le titre."`, `Heading`/level 2 `"Zweite Ebene"`, `Paragraph` `"Text unter der zweiten Ebene."`. **Neither `w:styleId` nor `w:name` contains the string `Heading`**, and neither paragraph carries a direct `w:outlineLvl` — the levels are reachable *only* through the styles part's `w:outlineLvl`, which is §7.5's second priority. A `Heading{n}` regex produces two paragraphs here and no headings, which is precisely the localised-template bug this fixture exists to catch. `styles.xml` is the one fixture part containing a non-ASCII character (`Ü`, U+00DC), so it is also the check that the parts are read as UTF-8 rather than as the platform's default encoding.

Each tree is minimal, indented and reviewable, and Step 7 asserts the directory count, the per-tree part count from the table above, and that every `.xml` under `corpus/docx/` parses.

- [ ] **Step 6: `manifest.json`**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp3\ingestion\tests\fixtures"
$entries = Get-ChildItem -LiteralPath "$root\corpus" -Recurse -File | Sort-Object FullName | ForEach-Object {
  [pscustomobject]@{
    path   = $_.FullName.Substring("$root\".Length).Replace('\','/')
    bytes  = $_.Length
    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  }
}
$entries | ConvertTo-Json -Depth 3 | Set-Content "$root\manifest.json" -Encoding utf8NoBOM
Write-Host "manifest.json: $($entries.Count) fixtures, $(($entries | Measure-Object bytes -Sum).Sum) bytes total"
```

The twelve text and Markdown entries must match Steps 1 and 2 exactly. A mismatch there means an editor touched a file — most likely flipping LF to CRLF, which is precisely what `corpus/** -text` and this manifest exist to catch. Task 4.1 adds a `FixtureDriftTests` class that re-runs the same check from the embedded resources, so the guard also holds on a device.

- [ ] **Step 6b: `realworld-corpus.json` — the §14.5 owner-input manifest** (plan adjustment 26)

§14.5's real-world lane wants "three or four files produced by actual Word, LibreOffice and Acrobat, fetched by pinned SHA-256 into the existing content-hash cache, never committed". Which files those are is the **owner's** choice and nobody else's — but a fetch step whose URLs live in a YAML comment is a permanently green no-op, and a plan cannot discharge a spec requirement with a comment. So the choice gets a **file with a schema**, a consumer (Task 2.2 Step 4's fetch loop and Task 7.1 Step 10's test), and a gate (Task 9.1 Step 5). Write it exactly as below, with the array empty:

```json
{
  "note": "Spec 14.5. OWNER INPUT. Add one entry per real-world document, then nothing else changes: the nightly fetch step reads this file and the tier-3 test reads whatever it fetched. The files themselves are NEVER committed - only their URL and digest are. Each producer in the spec (Word, LibreOffice, Acrobat) should appear at least once.",
  "schema": {
    "id": "stable, lower-case, used as the DocumentId - never rename one",
    "producer": "word | libreoffice | acrobat",
    "url": "a stable direct-download URL",
    "sha256": "lower-case hex, 64 chars",
    "phrase": "a short phrase the extracted text must contain, chosen by whoever added the entry"
  },
  "documents": []
}
```

`"documents": []` is the **declared unfinished state**, not a silent one: Task 9.1 Step 5 names it, and until it is non-empty the plan ships with §14.5 owed rather than claimed. It is committed empty so the wiring is exercised end to end on this box — the fetch loop over zero entries and the test's skip path both run — and so the owner's contribution is a four-line JSON edit rather than a YAML archaeology exercise.

- [ ] **Step 7: Verify**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp3\ingestion\tests\fixtures"
$expected = [ordered]@{
  'corpus/text/empty.txt'                    = @(0,    'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855')
  'corpus/text/whitespace-only.txt'          = @(7,    '3018a85eec1be9ff91c1060df01a9c0474bf9faf334bf9f363a7649b82a16fe4')
  'corpus/text/three-paragraphs.txt'         = @(190,  '9469d9507868eaf0a931eb7fde7aeda5bc34cbb251c92f2c89c03a0b6bed9e99')
  'corpus/text/crlf-and-lone-cr.txt'         = @(71,   '8f452179905c47b2baa6a17f3709e2c008bea9a4f591f24c355362a852e37ec9')
  'corpus/text/long-token.txt'               = @(5000, '260679791fa8da4dddc6aa3b243c514025e83d3a2f60800b9734b990be5d11a0')
  'corpus/text/unicode.txt'                  = @(136,  'e6b237d83b94fd4add1b7389d3e188436791b27cc351f8ae970ee7460e3498dc')
  'corpus/text/bom.txt'                      = @(74,   'c1ea8c926fadbbabcbac1cd47ef9f6baeeca3595a612ff0abe62e04750d029d6')
  'corpus/markdown/headings.md'              = @(402,  '0bbb67d71f47465d3daaf4053b831736d2c16efa2a59a0887f808f5bdc048bdd')
  'corpus/markdown/fences.md'                = @(248,  '4a1099e8a352413935074d858ca6b8ca1971495701df344fec74cbe2dff071d2')
  'corpus/markdown/tables-lists.md'          = @(190,  '41724421125458cfb0422f87602881b664368e66c6856bd125f7715ad1e61e70')
  'corpus/markdown/raw-html.md'              = @(163,  'a5be2e6cc68aa802d0e99dcab9015538315d10e99e789cde24245c4d69dadaf9')
  'corpus/markdown/giant-heading-section.md' = @(3866, 'e706d8fcc5701cb7d3a6b0bbf5c5252eab80331fa86429a19b5dd02f93be6a37')
}
foreach ($k in $expected.Keys) {
  $p = Join-Path $root ($k -replace '/','\')
  if (-not (Test-Path $p)) { throw "missing $k" }
  $len = (Get-Item $p).Length
  $sha = (Get-FileHash $p -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($len -ne $expected[$k][0]) { throw "$k is $len bytes, expected $($expected[$k][0])" }
  if ($sha -ne $expected[$k][1]) { throw "$k sha256 $sha, expected $($expected[$k][1])" }
}
$pdfs = @(Get-ChildItem "$root\corpus\pdf" -Filter *.pdf)
if ($pdfs.Count -ne 7) { throw "expected exactly seven committed PDFs, found $($pdfs.Count)" }
foreach ($n in 'minimal-text','two-pages','two-columns','hyphen-linebreak','no-text-layer','xref-stream','broken-startxref') {
  if (-not (Test-Path "$root\corpus\pdf\$n.pdf")) { throw "missing corpus/pdf/$n.pdf" }
}
$xs = [IO.File]::ReadAllText("$root\corpus\pdf\xref-stream.pdf")
foreach ($t in '%PDF-1.5', '/Type /XRef', '/W [1 4 2]', '/Type /ObjStm', '/ASCIIHexDecode') {
  if (-not $xs.Contains($t)) { throw "xref-stream.pdf does not carry $t - it is not the 1.5 shape" }
}
if ($xs.Contains("`ntrailer")) { throw "xref-stream.pdf has a classic trailer; /Size and /Root belong on the XRef stream dict" }
foreach ($p in $pdfs) {
  if ($p.Name -eq 'no-text-layer.pdf') { continue }
  $bytes = [IO.File]::ReadAllBytes($p.FullName)
  if (@($bytes | Where-Object { $_ -gt 127 }).Count -gt 0) { throw "$($p.Name) is not 7-bit ASCII" }
}
if (-not (Test-Path "$root\corpus\pdf\flate-content.stream")) { throw "flate-content.stream missing" }
$docxParts = [ordered]@{
  'headings' = 5; 'run-split' = 3; 'table' = 3; 'numbered-list' = 5; 'footnotes' = 5
  'header-footer' = 6; 'hyperlink' = 4; 'textbox' = 3; 'empty-body' = 3
  'unknown-style' = 5; 'localised-style' = 5
}
$docx = @(Get-ChildItem "$root\corpus\docx" -Directory)
if ($docx.Count -ne 11) { throw "expected 11 DOCX trees, found $($docx.Count)" }
$totalParts = 0
foreach ($tree in $docxParts.Keys) {
  $d = Join-Path "$root\corpus\docx" $tree
  if (-not (Test-Path $d)) { throw "missing DOCX tree $tree" }
  $files = @(Get-ChildItem $d -Recurse -File)
  if ($files.Count -ne $docxParts[$tree]) {
    throw "$tree has $($files.Count) parts, expected $($docxParts[$tree])"
  }
  foreach ($req in '[Content_Types].xml', '_rels\.rels', 'word\document.xml') {
    if (-not (Test-Path -LiteralPath (Join-Path $d $req))) { throw "$tree is missing $req" }
  }
  foreach ($f in $files) {
    try { [xml](Get-Content -LiteralPath $f.FullName -Raw) | Out-Null }
    catch { throw "$tree\$($f.Name) is not well-formed XML: $($_.Exception.Message)" }
  }
  $totalParts += $files.Count
}
if ($totalParts -ne 47) { throw "expected 47 DOCX parts in total, found $totalParts" }
if (-not (Test-Path "$root\corpus\docx\localised-style\word\styles.xml")) { throw "localised styles.xml missing" }
$ls = Get-Content "$root\corpus\docx\localised-style\word\styles.xml" -Raw
if ($ls -notmatch 'Überschrift 2') { throw "localised-style styles.xml lost its U+00DC - re-save it as UTF-8 without BOM" }
if ($ls -match 'Heading') { throw "localised-style must contain no style named Heading - that is the whole fixture" }
if (-not (Test-Path "$root\manifest.json")) { throw "manifest.json missing" }
$rw = Get-Content "$root\realworld-corpus.json" -Raw | ConvertFrom-Json
if ($null -eq $rw.documents) { throw "realworld-corpus.json has no 'documents' array" }
foreach ($d in @($rw.documents)) {
  foreach ($f in 'id','producer','url','sha256','phrase') {
    if (-not $d.$f) { throw "realworld-corpus.json entry is missing '$f'" }
  }
  if ($d.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw "realworld-corpus.json: $($d.id) sha256 is not 64 lower-case hex" }
}
Write-Host "realworld-corpus.json: $(@($rw.documents).Count) owner-supplied documents"
$fr = Get-Content "$root\README.md" -Raw
if ($fr -notmatch '(?m)^## Golden generation\s*$') { throw "fixtures README has no '## Golden generation' placeholder for Task 4.1" }
Write-Host "OK: 12 text/markdown fixtures byte-exact, $($pdfs.Count) PDFs, 11 DOCX trees / $totalParts parts all well-formed, manifest and README placeholder present"
```

Expected: the `OK:` line, exit 0. `no-text-layer.pdf` is the one exemption from the ASCII rule — its four inline-image bytes are the point of the fixture. The DOCX half of this check is structural on purpose: it asserts the part **count** per tree, the three parts every tree must have, that every part parses, and the two properties of `localised-style` that make it a test rather than a file. It does **not** assert DOCX digests — the trees are indented XML a reviewer is expected to read and occasionally reformat, and pinning their bytes would turn a whitespace change into a red test with no signal; what pins their *meaning* is the expected-output paragraph under each tree in Step 5, asserted by Task 6.1 Step 5.

---

### Task 1.4: SP1 edit — `EdgeErrorCode` gains 6000–6299

**Local-verifiable:** yes.

**Files:**
- Edit: `foundation\src\Qavren.Edge.Core\EdgeErrorCode.cs`
- Create: `foundation\tests\Qavren.Edge.Core.Tests\EdgeErrorCodeSp3RangeTests.cs`

**Approach.** This is the **one** library edit SP3 makes to SP1 or SP2 (§5.1), and it is a pure append: SP1's 1001–4001 and SP2's 5001–5213 are untouched and nothing is renumbered. `EdgeStartupOrder` is **not** changed — order 400 already sits between SP2's `VectorSchema = 300` and `ConsumerDefault = 1000` and is published from SP3's own `EdgeIngestionStartupOrder`. `EdgeEventIds` is **not** changed — SP3 publishes `EdgeIngestionEventIds` in 900–999, SP1 holding 100–500 and SP2 600–899. `IEdgeTokenizer` is **not** changed, and under plan adjustment 1 there is no longer any circumstance in which SP3 would want it changed.

- [ ] **Step 1: Append the range**

Append to `EdgeErrorCode`, after `ReservedColumnName = 5213,`, exactly the block §13.1 declares: the five comment-delimited groups `6000-6049 configuration and runner`, `6050-6099 source`, `6100-6149 extraction`, `6150-6199 chunking`, `6200-6249 state and writes`, and the trailing `// 6250-6299 reserved for sub-project 3.` line. Transcribe §13.1 verbatim, comments included — `IngestionRecipeChanged`'s `// StrictRecipe only`, `IngestionCollectionSchemaMismatch`'s `// migration DDL != the runtime store's DDL`, and 6206/6207/6208's call-site notes are contract, not decoration: §13.3 separates 6206 from 6207 **by call site**, and the comment is where a reader learns that.

- [ ] **Step 2: `EdgeErrorCodeSp3RangeTests`**

Four facts, asserted as literals:

- every declared `EdgeErrorCode` value is unique;
- every SP3 value is in `[6000, 6299]`;
- the SP1 values `1001, 1002, 1003, 1004, 1005, 2001, 2002, 3001, 3002, 4001` and the SP2 values `5001` and `5213` still hold their numbers — the guard against a renumbering nobody meant;
- the SP3 set is **exactly** the thirty-six §13.1 declares, asserted as a literal `int[]`, so adding a code without adding its `errors.md` anchor fails in the wave-1 close rather than at a consumer's `HelpLink`.

- [ ] **Step 3: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp3\foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp3\w1\t14
```

Expected: `Passed!` with the pre-existing count plus the new tests, exit 0.

---

### Task 1.5: Wave 1 close — sequential re-verify and commit *(integrator)*

**Local-verifiable:** yes.

**Files:** none of its own. It reads what wave 1 wrote and commits it.

**Approach.** The parallel phase ran under `-p:ArtifactsPath=D:\Local\Temp\qedge-sp3\w1\t…`, so no two tasks contended for one `obj/`. That isolation is exactly why the wave is **not** yet proved: nothing has built these projects in the tree the next wave, and CI, will use. This task deletes the scratch roots, re-runs every verify sequentially and un-isolated, runs the one cross-task check neither 1.2 nor 1.4 could run alone, and only then commits.

- [ ] **Step 1: Clear the wave's isolated outputs**

Delete the directory `D:\Local\Temp\qedge-sp3\w1` and everything under it (PowerShell: `Remove-Item` with `-Recurse -Force -ErrorAction SilentlyContinue`).

- [ ] **Step 2: Re-run every verify, sequentially, in the real tree**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp3"
dotnet restore "$root\QavrenEdge.slnx"
if ($LASTEXITCODE -ne 0) { throw "FAILED: solution restore" }
dotnet run --project "$root\foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0
if ($LASTEXITCODE -ne 0) { throw "FAILED: Core.Tests" }
Write-Host 'OK: wave 1 re-verified sequentially'
```

Then re-run Task 1.1 Step 11's PdfPig assertion and Step 13's notices check, Task 1.2 Step 8, and **Task 1.3 Steps 3b and 7**, all verbatim. Step 3b is re-run here rather than trusted: it is the only thing in wave 1 that proves the seven committed PDFs open in the parser that will read them, and it costs one scratchpad restore. Carry its `NOTE broken-startxref…` line into the commit body — Task 6.1 Step 5 branches on it.

- [ ] **Step 3: The cross-task check — every 6xxx code has an `errors.md` anchor**

Neither 1.2 nor 1.4 could run this: they are parallel and neither may read the other's output.

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp3"
$src = Get-Content "$root\foundation\src\Qavren.Edge.Core\EdgeErrorCode.cs" -Raw
$docs = Get-Content "$root\foundation\docs\errors.md" -Raw
$codes = [regex]::Matches($src, '=\s*(6\d{3})\s*,') | ForEach-Object { [int]$_.Groups[1].Value }
if ($codes.Count -ne 36) { throw "expected 36 SP3 codes in EdgeErrorCode, found $($codes.Count)" }
foreach ($c in $codes) { if ($docs -notmatch "(?m)^## $c\s*$") { throw "errors.md has no anchor for $c" } }
Write-Host "OK: $($codes.Count) SP3 error codes, every one documented"
```

- [ ] **Step 4: Commit the wave**

One commit, Conventional Commits, **no `Co-Authored-By` and no AI attribution**, on `feat/sp3-ingestion`. Never on `main`.

```
feat(sp3): skeleton, CPM pins, docs, the fixture corpus and the 6000-6299 error range

- ingestion/ projects, .slnx entries and six PackageVersion pins
- ADRs 0009-0013, ingestion/README.md, errors.md 6000-6299, three notices
- 12 text/markdown, 7 PDF (xref-stream included, all seven opened in PdfPig) and 11 DOCX fixtures
  with a byte-exact manifest, plus the empty realworld-corpus.json owner-input manifest
- EdgeErrorCode gains 6000-6299 (the one SP1 edit, additive)
```

- [ ] **Step 5: Verify**

Steps 2 and 3 printed their `OK` lines and `git status --short` is clean. Wave 2 may start.

---

## WAVE 2 — The core's declared surface, the token budget, and CI

### Task 2.1: `Qavren.Edge.Ingestion` part 1 — value types, the tokenizer seam, the chunk budget, the recipe, the schema

**Local-verifiable:** yes.

**Files:**
- Create: `ingestion\src\Qavren.Edge.Ingestion\AssemblyInfo.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\EdgeIngestionEventIds.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\EdgeIngestionStartupOrder.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\IngestionExceptions.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\ContentHash.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\DocumentModel.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\IDocumentExtractor.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\IngestionSource.cs` *(the abstract class and `DocumentSourceItem` only — the static factories are Task 3.1's)*
- Create: `ingestion\src\Qavren.Edge.Ingestion\IChunkTokenizer.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\MlChunkTokenizer.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\EdgeTokenCounter.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\Internal\TokenIndexSearch.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\ChunkModelProfile.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\ChunkOptions.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\IChunker.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\IngestionRecipe.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\IngestionOptions.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\IngestionThrottle.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\IngestionBudget.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\IngestionColumns.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\IngestionSchema.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\IngestedChunk.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\IngestionMediaTypes.cs`
- Create: `ingestion\tests\Qavren.Edge.Ingestion.Tests\*` (the suites named in Step 9)

> **Hand-off from Task 3.1 — this task is NOT done.** Task 3.1 ran ahead of it and wrote
> eight of these files in `ingestion\src\Qavren.Edge.Ingestion` as a **minimal
> compile-against slice**, not as their real declarations: `AssemblyInfo.cs`,
> `EdgeIngestionEventIds.cs`, `EdgeIngestionStartupOrder.cs`, `IngestionExceptions.cs`,
> `DocumentModel.cs`, `IDocumentExtractor.cs`, `IngestionMediaTypes.cs`, and a ninth file
> **`ExtractionOptions.cs`** that is not on this list at all. When 2.1 runs it must:
> (a) **overwrite** those eight with the full declarations §11 lists; (b) **delete**
> `ExtractionOptions.cs`, whose type belongs inside `IngestionOptions.cs` per this Files
> list; and (c) decide `IngestionMediaTypes.Unknown`, which 3.1 left `internal` — keep it
> internal or add it to §11's four public constants. `IngestionSource.cs` also exists
> already, carrying both 2.1's abstract class and 3.1's five static factories.

**Approach.** This is the wave that gives every later task something to compile against, so it declares the **whole** public surface §11 lists that does not depend on a running pipeline, and implements for real the things that are pure functions: hashing, the budget arithmetic, the recipe hash, the schema definition, the record mapper, and the two tokenizers. Nothing here opens a connection, touches a file, or needs DI. `AddIngestion` is **not** in this task — §11.1's five steps need the registry, the state store and the pipeline, and those are Task 5.1's.

`AssemblyInfo.cs` carries **two** `InternalsVisibleTo` grants, and the second one is issued here — three waves before its consumer exists — for a structural reason, not a stylistic one (plan adjustment 24):

- `Qavren.Edge.Ingestion.Tests`, matching SP2's idiom, so `TokenIndexSearch` can be tested directly rather than only through a chunker;
- **`Qavren.Edge.Ingestion.Onnx`**, because Task 6.2's `EdgeChunkTokenizer` delegates to `TokenIndexSearch` (plan adjustment 1) and `TokenIndexSearch` is `internal`. The grant is a **write to the core**, and the Wave map's headline rule is that exactly one task per wave writes `ingestion/src/Qavren.Edge.Ingestion` and no other task in that wave compiles it. In wave 6, Tasks 6.1 and 6.3 compile the core while 6.2 runs; a grant added there would be a mid-edit compile of a shared assembly, which `ArtifactsPath` does not fix because it separates outputs and not sources. Task 5.2 Step 4 then freezes the core. So the grant is issued **now**, by the wave's single core writer, and wave 6 writes nothing in `ingestion/src/Qavren.Edge.Ingestion` at all.

**Types this task owns** (adopted by reference from the spec, transcribed verbatim with their XML docs):

| From | Types |
|---|---|
| §11 | `EdgeIngestionStartupOrder`, `EdgeIngestionEventIds` (all 28 ids), `ContentHash`, `IngestionRunOutcome`, `IngestionStage`, `IngestionProgress`, `DocumentBlockKind`, `DocumentBlock`, `ExtractedDocument`, `ExtractionContext`, `IDocumentExtractorRegistry`, `IngestionMediaTypes`, `ChunkOverflow`, `ChunkOptions`, `ResolvedChunkOptions`, `ChunkDraft`, `IChunker`, `ThrottlePauseBehavior`, `IngestionThrottleContext`, `IngestionThrottleDecision`, `IIngestionThrottle`, `FixedIngestionThrottle`, `MlChunkTokenizer`, `EdgeTokenCounter`, `ChunkModelProfile`, `IngestionOptions`, `ExtractionOptions`, `ChunkerIds`, `IngestionSource` (abstract members only), `DocumentSourceItem`, `IngestionColumns`, `IngestionSchema`, `IngestedChunk` |
| §7.1 | `IDocumentExtractor` |
| §8.1 | `IChunkTokenizer` |
| §9.2 | `IngestionRecipe` |
| §10.2 | `IngestionBudget` |
| §13.2 | `EdgeIngestionException`, `EdgeExtractionException`, `EdgeChunkingException`, `EdgeIngestionStateException` |

**Defaults that change behaviour** — restated here because a default is the part of a declaration a reader never questions, and each is asserted by a test in this task:

- `ChunkOptions.HeadingPathTokenBudget = 32`, `SplitHeadingLevels = [1, 2, 3]`, `PrependHeadingPath = true`, `IncludePreamble = true`, `MergeShortSections = true`, `SentenceAware = true`, `RepeatTableHeaderRow = true`, `Overflow = ChunkOverflow.Split`; `MaxTokens` / `OverlapTokens` / `MinTokens` are **null**, and setting one explicitly opts out of §8.1's derivation for that knob alone.
- `IngestionOptions.Model = ChunkModelProfile.MiniLmL6V2Int8`, `CollectionName = "chunks"`, `DistanceFunction = CosineDistance`, `FullTextIndexed = true`, `StateTablePrefix = "qedge_ingest"`, `ChunkerId = ChunkerIds.Auto`, `MaxDocumentBytes = 32 MiB`, `WriteBatchSize = 32`, `DeleteBatchSize = 500`, **`SkipUnchangedByTimestamp = false`** (correctness beats speed — matching size and mtime is not evidence a file is unchanged), `DeleteMissingDocuments = true`, `RepairOrdinals = true`, `ContinueOnDocumentError = true`, `AbortAfterConsecutiveErrors = 20`, `StrictRecipe = false`, `PauseBehavior = Suspend`, `SleepGraceBudget = 750 ms`, `StopGraceBudget = 5 s`, `RunHistoryLimit = 20`, `IncludeCountsInDiagnostics = false`, and — plan adjustment 4 — **`FullTextRemoveDiacritics = 2`**.
- `ExtractionOptions.NonSeekableBufferLimitBytes = 4 MiB`, `NormalizeText = true`.
- `IngestionBudget.Unlimited` is all-null; **`Quick` is `MaxDuration = 20 s` and nothing else; `Background` is `MaxDuration = 5 min` and nothing else** (plan adjustment 20, literal initialisers in Step 7). These are behaviour-changing defaults in the strongest sense — they decide when a run reports `Suspended` — and the spec left them as two tildes.
- The four `FtsMerge*` members and event id 926 are **absent** (plan adjustment 2). `EdgeIngestionEventIds` keeps the other 27 ids at their §11 numbers so a log filter written against the spec still works; 926 is left as a gap with a comment saying why, rather than renumbering 927.

- [ ] **Step 1: Re-run the tokenizer probe against the real vocabulary, and record the answer**

Before a line of `TokenIndexSearch` is written. The plan's **Environment ground truth** measured `SpecialTokenOverhead = 2` and the `GetIndexByTokenCount` table against a 23-entry toy vocabulary; this step repeats both against the real 30,522-entry `bert-base-uncased` `vocab.txt` (SHA-256 `07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3`).

**This step is not optional and has no "proceed without it" branch** (plan adjustment 23). The vocabulary **is on this box** — Environment ground truth pins the path, the size and the digest — so resolve it with the three-step order stated there (`%QAVREN_EDGE_VOCAB%`, then `%QAVREN_EDGE_MODEL_DIR%\vocab.txt`, then `C:\Users\steve\AppData\Local\Temp\qedge-model\vocab.txt`), and if all three miss, run the re-provisioning command in Environment ground truth **before continuing**. An earlier draft of this task said "if that vocabulary is not on the box, say so and proceed", which quietly created the branch on which Task 4.1's twenty goldens cannot be written and its own verify cannot pass. Verify the digest before use; a vocabulary that does not hash to `07eced…` is not the vocabulary every golden in this plan is pinned against.

Write a throwaway console **outside the repo** (the scratchpad), not a test:

- `BertTokenizer.Create(vocabPath)`; assert `EncodeToIds(s, addSpecialTokens: true).Count - CountTokens(s) == 2` for five strings including an NFD one and a CJK one. **If it is not 2, stop**: every number in §8.1 shifts, the two pinned triples in Step 4 move, and Task 4.1's goldens must not be written until the plan is amended.
- For the 55-character sentence in Environment ground truth, print `GetIndexByTokenCount` at budgets 1, 2, 3, 5, 8, 11 with `considerNormalization` true and false, and print the largest prefix index a linear `CountTokens` scan finds at each budget. Record all three columns in the task's output.

Record the outcome in the task notes. It changes nothing structurally — Step 3's search is what ships either way — but it is the difference between a documented measurement and a repeated assumption.

- [ ] **Step 2: `AssemblyInfo.cs`, `ContentHash`, `IngestionColumns`, `IngestionMediaTypes`, the event ids, the startup order, the exceptions**

`ingestion\src\Qavren.Edge.Ingestion\AssemblyInfo.cs`, in full — both grants, with the reason for the second one on it, because a reader three waves later will otherwise read it as copy-paste:

```csharp
using System.Runtime.CompilerServices;

// The test assembly, matching SP2's idiom: TokenIndexSearch is internal and is tested directly
// rather than only through a chunker.
[assembly: InternalsVisibleTo("Qavren.Edge.Ingestion.Tests")]

// Qavren.Edge.Ingestion.Onnx's EdgeChunkTokenizer delegates to Internal.TokenIndexSearch, which is
// the ONE implementation of IChunkTokenizer.IndexByTokenCount (plan adjustment 1, ADR 0013). The
// grant is issued HERE, in wave 2, and not in wave 6 where the satellite is written: a wave-6 edit
// to this file would be a write to Qavren.Edge.Ingestion while Tasks 6.1 and 6.3 compile it, and
// -p:ArtifactsPath separates outputs, not sources. The core is frozen from the wave-5 close on.
[assembly: InternalsVisibleTo("Qavren.Edge.Ingestion.Onnx")]
```

Neither grant is conditional and neither carries a public key: none of the assemblies in this repository is strong-named, and adding a key for one grant would make this the only signed assembly in the suite.

`ContentHash` exactly as §11 declares it, over `System.IO.Hashing.XxHash128`. Four things the declaration does not say and the body must:

- **Persisted bytes are big-endian**, which is how `System.IO.Hashing` writes them. `ToBlob()` is sixteen bytes; `FromBlob` raises `IngestionStateCorrupt` (6204) on any other length.
- `OfText` hashes UTF-8 with **no BOM**.
- `OfStreamAsync(stream, maxBytes, bufferSize = 65536, ct)` is §9.1's **counted read**: it carries a running byte count, aborts the moment the count exceeds `maxBytes`, and signals that to the caller (an out-of-range sentinel or a `null` result — the caller in Task 5.1 needs to distinguish "hashed" from "over the ceiling", and **must not** be handed a hash of a truncated read, because storing that would make a later shrink of the file look unchanged). At most `maxBytes + bufferSize` is ever read and nothing is buffered.
- `Combine` is order-sensitive and is what §9.3's key derivation uses with the `0x1F` separator.

`EdgeIngestionEventIds` is 28 `EventId` constants at §11's numbers, minus 926. `EdgeIngestionStartupOrder.Validate = 400`. The four exception types are §13.2's, each carrying `SourceId`, `DocumentId`, `RunId`, `Remediation`, `SizeBytes` on the base, and their own extras on the three derived types. Configuration faults reuse SP1's `EdgeConfigurationException` with a 60xx code, so `AddIngestion` fails the way every other builder call in the suite fails.

- [ ] **Step 3: `IChunkTokenizer`, `TokenIndexSearch`, `MlChunkTokenizer`, `EdgeTokenCounter`** (plan adjustment 1)

`IChunkTokenizer` exactly as §8.1 declares it, **with the XML doc on `IndexByTokenCount` rewritten** to state the contract the implementation actually honours:

> The largest index `i` such that `text[0..i)` costs at most `maxTokens` tokens, counted with the same settings the encoder encodes with. CONTRACT: `i` indexes into `text` AS PASSED. Implementations MUST NOT return an index into a normalised copy, and MUST NOT obtain the index by disabling normalisation — measured on `Microsoft.ML.Tokenizers` 2.0.0, `Tokenizer.GetIndexByTokenCount` does the first at its default and the second returns `text.Length` for every budget. Both implementations shipped here delegate to one bounded prefix search over `CountTokens`.

`internal static class TokenIndexSearch`, the single implementation:

```csharp
namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// The one implementation of IChunkTokenizer.IndexByTokenCount, shared by MlChunkTokenizer and
/// by Qavren.Edge.Ingestion.Onnx's EdgeChunkTokenizer. See ADR 0013 for why neither calls
/// Tokenizer.GetIndexByTokenCount.
/// <para>
/// It measures only PREFIXES OF THE ORIGINAL STRING, so the returned index is an index into the
/// string as passed by construction, and it counts with whatever settings the supplied delegate
/// carries - which is the tokenizer's own defaults, i.e. exactly what the encoder will do.
/// </para>
/// </summary>
internal static class TokenIndexSearch
{
    /// <summary>
    /// Largest grapheme-cluster boundary index i with count(text[0..i)) &lt;= maxTokens.
    /// </summary>
    /// <param name="text">The text. Not null.</param>
    /// <param name="maxTokens">The ceiling. Positive.</param>
    /// <param name="count">Counts tokens in a prefix. Called O(10) times per cut.</param>
    /// <param name="tokenCount">The count at the returned index.</param>
    /// <returns>The index, in [0, text.Length].</returns>
    public static int Find(string text, int maxTokens, Func<string, int> count, out int tokenCount);
}
```

The body, specified rather than transcribed because its shape is what matters:

1. `text.Length == 0 || maxTokens <= 0` → return 0 with `tokenCount = 0`.
2. `total = count(text)`; if `total <= maxTokens` return `text.Length` with `tokenCount = total`. One call covers the common short-block case, which is most calls.
3. Estimate `chars = text.Length * maxTokens / max(1, total)` — a chars-per-token ratio taken from the string in hand, not a constant.
4. Search the window `[estimate * 3 / 4, estimate * 5 / 4]`, clamped to `[0, text.Length]`, for the largest index whose count is at most `maxTokens`. Snap **every** candidate forward to a grapheme-cluster boundary with `StringInfo`/`Rune` before counting, so no cut can land mid-surrogate or mid-combining-mark. Binary search inside the window.
5. If the whole window is over budget, halve the window's lower bound and search again; if the whole window is under budget, extend the upper bound toward `text.Length`. At most two widenings, then fall back to a bounded linear walk from the best-known-good index. This is the part that matters: **`count` over prefixes is not monotone** — `"hell"` is two WordPiece tokens where `"hello"` is one — so a binary search can settle on a non-maximal index. A non-maximal index is *safe* (it never exceeds the budget) and only costs a slightly shorter chunk, which is why the search is allowed to be approximate and the invariant is not.
6. **Verify the chosen index**: `tokenCount = count(text[0..i])`, and if it exceeds `maxTokens`, walk back to the previous grapheme boundary until it does not. `ChunkExceedsTokenBudget` (6151) is an SP3 invariant violation and must be unreachable from here, not merely unlikely.
7. Never return 0 for non-empty input unless even the first grapheme cluster is over budget, in which case return that cluster's length — a zero-length chunk would loop forever in the token-window chunker.

`MlChunkTokenizer` holds the concrete `Microsoft.ML.Tokenizers.Tokenizer`, and — the trap §8.1 does not name but SP2's `IEdgeTokenizer` doc does — **holds it as the concrete type, never as the `Tokenizer` base where that matters**: `BertTokenizer.EncodeToIds` is declared `new`, so a base-typed field silently drops `[CLS]` and `[SEP]`. `CountTokens` forwards at the tokenizer's defaults. `IndexByTokenCount` calls `TokenIndexSearch.Find`. `Id` is `$"wordpiece:{vocabSize}:{modelHint}"`.

`EdgeTokenCounter.CreateWordPiece(vocabFilePath | Stream, maxSequenceLength, lowerCase = true)` builds a `BertTokenizer` through `BertTokenizer.Create(path, new BertOptions { LowerCaseBeforeTokenization = lowerCase })` and wraps it with `specialTokenOverhead: 2`. `FromTokenizer(tokenizer, maxSequenceLength, specialTokenOverhead)` takes the overhead from the caller, because a non-BERT tokenizer's is not 2.

- [ ] **Step 4: `ChunkModelProfile`, `ChunkOptions.Resolve`, `ResolvedChunkOptions`**

`ChunkModelProfile` exactly as §11 declares it, all four static profiles included (plan adjustment 16), with §11's XML doc on the ids — they are SP2's **lower-case** strings, the id feeds `IngestionRecipe.ModelProfileId`, and a cased copy would give the ONNX-free and ONNX paths different recipe hashes for the same model.

`ChunkOptions.Resolve(ChunkModelProfile model, IChunkTokenizer tokenizer)` implements §8.1's arithmetic with **integer truncation throughout, never rounding**:

```
documentPrefixTokens = tokenizer.CountTokens(model.DocumentPrefix ?? "")
maxTokens      = MaxTokens      ?? model.MaxSequenceLength - tokenizer.SpecialTokenOverhead
                                  - HeadingPathTokenBudget - documentPrefixTokens
overlapTokens  = OverlapTokens  ?? max(8, (maxTokens * 15 / 100) / 8 * 8)
minTokens      = MinTokens      ?? max(16, maxTokens / 8)
```

and then asserts, throwing `IngestionChunkBudgetInvalid` (6003) **with the arithmetic in the message**:

- `maxTokens + tokenizer.SpecialTokenOverhead + HeadingPathTokenBudget + documentPrefixTokens <= model.MaxSequenceLength`
- `overlapTokens < maxTokens / 2`
- `minTokens < maxTokens`
- `tokenizer.MaxSequenceLength == model.MaxSequenceLength`, or `ChunkTokenizerCeilingExceeded` (6153) — a tokenizer and a profile that disagree about the ceiling make every number above a guess.

- [ ] **Step 5: `IngestionRecipe`**

Exactly as §9.2 declares it. `Hash` is 32 lowercase hex over the `0x1F`-separated, order-stable rendering of every field, `ResolvedChunkOptions` included field by field. `ExtractorFingerprint` is the **selected** extractor's `"{Id}:{Version}"`, never the registry's — hashing the set would re-index an entire Markdown corpus because the PDF extractor's version was bumped.

- [ ] **Step 6: `IngestionSchema`, `IngestionColumns`, `IngestedChunk`**

`IngestionSchema.BuildDefinition(dimensions, distanceFunction, fullTextIndexed)` builds a `VectorStoreCollectionDefinition` programmatically — no attributed record type, no reflection, dimension a runtime value. The vector property is declared **`ReadOnlyMemory<float>`, not a `string` source**, because SP3 resolves every vector itself (§9.5 step a1); that single decision is what makes `EmbedCalls` countable and 6206 separable from 6207. `source_id` and `document_id` are `IsIndexed`; `text` and `heading_path` are `IsFullTextIndexed` when `fullTextIndexed`. Every CLR type is in `SqliteTypeMap.SupportedDataTypes` — verified: `int`, `string`, `byte[]`, `DateTimeOffset` all map.

`IngestionSchema.BuildStateSql(tablePrefix)` returns §6's three `CREATE TABLE` statements plus their indexes, **all three `WITHOUT ROWID` — including the two whose key is a single column** (plan adjustment 19; §6's prose says "where the key is composite", the plan widens it, and Task 9.1 Step 5 checks the plan's wording rather than the spec's). Literal, because the plan originates it:

```sql
CREATE TABLE IF NOT EXISTS "<p>_document" (
  "collection"    TEXT NOT NULL,
  "source_id"     TEXT NOT NULL,
  "document_id"   TEXT NOT NULL,
  "content_hash"  BLOB,
  "recipe_hash"   BLOB,
  "size_bytes"    INTEGER,
  "modified_utc"  TEXT,
  "extractor_id"  TEXT,
  "chunk_count"   INTEGER NOT NULL DEFAULT 0,
  "status"        TEXT NOT NULL,
  "error"         TEXT,
  "last_run_id"   TEXT,
  "updated_utc"   TEXT NOT NULL,
  PRIMARY KEY ("collection", "source_id", "document_id")
) WITHOUT ROWID
--
CREATE INDEX IF NOT EXISTS "<p>_document_last_run" ON "<p>_document"("collection", "source_id", "last_run_id")
--
CREATE INDEX IF NOT EXISTS "<p>_document_recipe" ON "<p>_document"("collection", "recipe_hash")
--
CREATE TABLE IF NOT EXISTS "<p>_run" (
  "run_id"            TEXT PRIMARY KEY,
  "collection"        TEXT NOT NULL,
  "source_id"         TEXT NOT NULL,
  "recipe_hash"       BLOB,
  "started_utc"       TEXT NOT NULL,
  "finished_utc"      TEXT,
  "outcome"           TEXT,
  "suspend_reason"    TEXT,
  "documents_indexed" INTEGER NOT NULL DEFAULT 0,
  "chunks_added"      INTEGER NOT NULL DEFAULT 0,
  "chunks_removed"    INTEGER NOT NULL DEFAULT 0,
  "error"             TEXT
) WITHOUT ROWID
--
CREATE INDEX IF NOT EXISTS "<p>_run_started" ON "<p>_run"("collection", "started_utc" DESC)
--
CREATE TABLE IF NOT EXISTS "<p>_meta" (
  "key"   TEXT PRIMARY KEY,
  "value" TEXT NOT NULL
) WITHOUT ROWID
```

Three notes the SQL does not carry. `<p>` is `IngestionOptions.StateTablePrefix`, default `qedge_ingest`, and it is quoted into the identifier position, so Task 5.1 validates it against `^[A-Za-z_][A-Za-z0-9_]*$` at registration and raises `IngestionOptionsInvalid` (6005) otherwise — the state DDL is the one place in SP3 where a consumer string reaches an identifier. The two indexes on `_document` are what make §12's `staleDocuments` and `recipeStaleDocuments` one indexed `COUNT` each rather than a table scan. `_run` and `_meta` are `WITHOUT ROWID` on a single `TEXT` primary key, which §6's wording does not require and **plan adjustment 19 records as a deliberate widening**: a `WITHOUT ROWID` table stores the row in the key's b-tree rather than a rowid table plus a separate unique index, which is the right shape for two tables that are read by primary key, never scanned by rowid, and hold twenty rows and two rows. The consequence to watch is that every row must supply its primary key and there is no `AUTOINCREMENT` — both already true, because `run_id` is generated by the runner and `key` is a literal. Statements are emitted with `\n` line endings and separated by the `--` marker only in this plan; the method returns them as a list, as `EdgeVectorSchema.BuildCreateSql()` does.

`IngestionColumns` publishes every storage name as a `const` plus `HeadingPathSeparator = " › "`. `IngestedChunk.FromRecord` / `ToRecord` is the mapper both directions, and `HeadingPath` splits `Breadcrumb` on the separator. **The sanitiser lives here too** — a `static string SanitizeHeading(string)` that collapses any occurrence of the separator sequence to a single space and strips control characters — because §8.3's round-trip property is a property of the pair, and putting the join in one file and the sanitiser in another is how the two drift.

- [ ] **Step 7: `IngestionOptions`, `ExtractionOptions`, `ChunkerIds`, the throttle, the budget**

All exactly as §11 and §10.2 declare, with plan adjustments 2 (no `FtsMerge*` members) and 4 (`FullTextRemoveDiacritics`). `FixedIngestionThrottle` always returns the configured batch and never pauses.

**The three `IngestionBudget` presets are literal, not approximate** (plan adjustment 20). §10.2 documents them as "~20 s" and "~5 min", and a tilde is not a pinnable default — it leaves the implementer to choose a number and the next reader to wonder which one was chosen:

```csharp
/// <summary>No ceiling of any kind. The default when IngestionRunOptions.Budget is null.</summary>
public static IngestionBudget Unlimited { get; } = new();

/// <summary>20 seconds of wall time. Fits inside an iOS BGAppRefreshTask slot.</summary>
public static IngestionBudget Quick { get; } = new() { MaxDuration = TimeSpan.FromSeconds(20) };

/// <summary>5 minutes of wall time. Fits inside a WorkManager Worker's 10-minute cap.</summary>
public static IngestionBudget Background { get; } = new() { MaxDuration = TimeSpan.FromMinutes(5) };
```

`MaxDocuments`, `MaxChunks` and `MaxTokens` stay **null on all three**, deliberately: a `Quick` that also capped documents would suspend on a count that has nothing to do with the twenty-second slot it exists to fit, and a caller who wants a count cap composes one — `IngestionBudget` is an `init`-only record, so `IngestionBudget.Quick with { MaxDocuments = 50 }` needs no fourth preset. `OptionsDefaultsTests` asserts all twelve members of the three presets.

- [ ] **Step 8: the model records and the two interfaces**

`ExtractedDocument`, `DocumentBlock`, `DocumentBlockKind`, `ExtractionContext`, `IDocumentExtractor`, `IDocumentExtractorRegistry`, `ChunkDraft`, `IChunker`, the abstract `IngestionSource` (two abstract members, no static factories yet) and `DocumentSourceItem` — all verbatim from §6, §7.1, §8.2 and §11, including `DocumentSourceItem.OpenAsync`'s XML doc, which is the seekability contract §7.1 turns on and is the single most-ignored sentence in the package.

- [ ] **Step 9: the tests**

In `Qavren.Edge.Ingestion.Tests`:

- `ContentHashTests` — known vectors for `OfText` and `OfBytes`; `ToBlob` is 16 bytes and big-endian; `FromBlob` of 15 bytes is 6204; `ToHex` is 32 lowercase; `TryParseHex` round-trips; `OfStreamAsync` over a stream longer than `maxBytes` reports the ceiling and **does not** return a hash; `OfStreamAsync` reads at most `maxBytes + bufferSize` (asserted with a counting stream).
- `ChunkBudgetTests` — `Resolve` produces **222 / 32 / 27** on `MiniLmL6V2Int8` and **478 / 64 / 59** on `BgeSmallEnV15`, with the bge overlap pinned at 64 because `478 * 15 / 100 / 8 * 8` is 64 and rounding to nearest would give 72. A third case with a fake tokenizer reporting a four-token `DocumentPrefix` asserts the reserve is **subtracted** rather than ignored. Every 6003 path: overlap at or above half, min at or above max, a negative heading budget, and a tokenizer whose `MaxSequenceLength` disagrees with the profile's (6153).
- `TokenIndexSearchTests` — the offset contract, directly: for NFD text, a ZWJ sequence, a Turkish dotted I and mixed-case non-ASCII, `text[..Find(...)]` is a valid prefix of the **original**, `CountTokens(text[..i]) <= maxTokens`, and `i` is a grapheme-cluster boundary. Plus the non-monotonicity guard: a hand-built counter that reports `"hell"` as 2 and `"hello"` as 1 must not make `Find` return an index whose verified count exceeds the budget. Plus the Step 1 equivalence: over the 55-character sentence, `Find` returns the same indices a linear scan does at budgets 1, 2, 3, 5, 8 and 11.
- `IngestionRecipeTests` — one test per field asserting the hash moves, plus one asserting that bumping an **unselected** extractor's version does not, plus order stability across two constructions.
- `IngestedChunkRoundTripTests` — every property survives `ToRecord` → `FromRecord`, `Breadcrumb` → `HeadingPath` included, and a null breadcrumb yields an empty path.
- `HeadingSanitiserTests` — a CsCheck property: over generated headings containing `›`, ` › `, control characters and leading/trailing whitespace, `Split(Join(sanitise(path))) == sanitise(path)`, and the sanitiser is idempotent. `seed:` and `iter: 500` pinned **at the call site** — a time-seeded property test in a gate is a flake generator. The nightly lane widens through `CsCheck_Iter` / `CsCheck_Seed`.
- `IngestionSchemaTests` — `BuildDefinition` declares the vector property as `ReadOnlyMemory<float>` and not a `string` source (asserted on the definition, because that one fact is what §9.5's whole write protocol rests on); every data property's CLR type is accepted by `SqliteTypeMap.IsSupportedDataType`; `source_id` and `document_id` are indexed; `text` and `heading_path` are full-text indexed when asked and not when not. `BuildStateSql` is a **golden-SQL** test: the exact statement list above, byte for byte, for the default prefix and for a custom one.
- `OptionsDefaultsTests` — a table test over every default in **Defaults that change behaviour**, so a silent change to one is a failing test rather than a behaviour change.

- [ ] **Step 10: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp3\ingestion\tests\Qavren.Edge.Ingestion.Tests\Qavren.Edge.Ingestion.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp3\w2\t21
```

Expected: `Passed!`, exit 0, with `ChunkBudgetTests` reporting the two pinned triples.

---

### Task 2.2: `ci.yml` test steps, the PdfPig asset assertion, `trim-smoke`, the nightly lanes, and the workflow contract *(integrator)*

**Local-verifiable:** yes (YAML parse + the python contract check; the workflow itself is CI-only).

**Files:**
- Edit: `.github\workflows\ci.yml`
- Edit: `foundation\tools\ci-checks\assert-workflows.py`

**Approach.** This task compiles nothing, which is why it can share wave 2 with the only task writing the core. The four new test steps reference projects that have no tests yet; that is fine, because no workflow runs during implementation — the YAML is parsed, `assert-workflows.py` is run, and every step's command is run by hand on this box in the wave that gives it something to run.

- [ ] **Step 1: four test steps in the `test` job**

Append after the `VectorData conformance` step, matching the established shape exactly (`-f net10.0 -p:TargetFrameworks=net10.0` on multi-TFM projects, `-p:` alone on single-TFM ones):

```yaml
      - name: Ingestion tests
        run: dotnet run --project ingestion/tests/Qavren.Edge.Ingestion.Tests/Qavren.Edge.Ingestion.Tests.csproj -c Release -f net10.0 -p:TargetFrameworks=net10.0

      - name: Ingestion extractor tests
        run: dotnet run --project ingestion/tests/Qavren.Edge.Ingestion.Extractors.Tests/Qavren.Edge.Ingestion.Extractors.Tests.csproj -c Release -f net10.0 -p:TargetFrameworks=net10.0

      # Single-TFM projects, so no -f. Both are host-only and never run on a device lane (spec 4.2).
      - name: Ingestion Onnx tests
        run: dotnet run --project ingestion/tests/Qavren.Edge.Ingestion.Onnx.Tests/Qavren.Edge.Ingestion.Onnx.Tests.csproj -c Release -p:TargetFrameworks=net10.0

      - name: Ingestion MEDI tests
        run: dotnet run --project ingestion/tests/Qavren.Edge.Ingestion.DataIngestion.Tests/Qavren.Edge.Ingestion.DataIngestion.Tests.csproj -c Release -p:TargetFrameworks=net10.0
```

- [ ] **Step 2: the PdfPig asset assertion on `windows-2025`**

Insert after the existing `Assert ONNX Runtime asset resolution` step, reusing its `dotnet restore QavrenEdge.slnx`:

```yaml
      # PdfPig ships NO net10.0 TFM and must resolve lib/net9.0 on every TFM. The failure this
      # guards is a static-constructor throw on a device with no compile error anywhere: the
      # netstandard2.0 copy of UglyToad.PdfPig.Fonts.dll contains neither AndroidSystemFontLister
      # nor IOSSystemFontLister and raises NotSupportedException the first time a PDF references
      # a non-embedded font (spec 14.6). Written against the PdfPig id directly - unlike ORT's
      # native package, PdfPig carries its own compile assets, so the assertion is not vacuous.
      - name: Assert PdfPig asset resolution
        if: matrix.os == 'windows-2025'
        shell: pwsh
        run: |
          $assets = 'ingestion/tests/Qavren.Edge.Ingestion.Extractors.Tests/obj/project.assets.json'
          if (-not (Test-Path $assets)) { throw "no project.assets.json at $assets" }
          $a = Get-Content $assets -Raw | ConvertFrom-Json
          foreach ($tfm in 'net10.0','net10.0-android','net10.0-ios','net10.0-maccatalyst','net10.0-windows10.0.19041.0') {
            $entry = $a.targets.$tfm.'PdfPig/0.1.16'
            if (-not $entry) { throw "$tfm did not resolve PdfPig 0.1.16" }
            $names = $entry.compile.PSObject.Properties.Name
            $bad = $names | Where-Object { $_ -notlike 'lib/net9.0/*' }
            if ($bad) { throw "$tfm resolved PdfPig to '$($bad -join ',')'; expected lib/net9.0" }
            Write-Host "OK $tfm -> lib/net9.0 ($($names.Count) assemblies)"
          }
```

- [ ] **Step 3: `trim-smoke` gains the satellite publish** (plan adjustment 15)

In the existing `trim-smoke` job, after the trimmed publish and its run, and before the AOT leg:

```yaml
      - name: Publish trimmed with both ingestion satellites
        continue-on-error: true
        run: >
          dotnet publish embeddings/tools/Qavren.Edge.TrimSmoke/Qavren.Edge.TrimSmoke.csproj
          -c Release -r linux-x64 --self-contained true -o ./artifacts/trim-smoke-satellites
          -p:PublishTrimmed=true -p:QedgeTrimSatellites=true
          -p:TargetFrameworks=net10.0 -p:TargetFramework=net10.0

      - name: Run the satellite trim smoke
        continue-on-error: true
        run: ./artifacts/trim-smoke-satellites/Qavren.Edge.TrimSmoke
```

`continue-on-error` on both, for one release cycle, exactly as the AOT leg already is: §17 item 4 says the satellites' warnings are recorded in the README rather than suppressed, and a `continue-on-error` step whose log carries the warnings is how a recording gets made. The **core-only** publish stays required, because that is the leg that puts Markdig under the trimmer.

- [ ] **Step 4: the two tier-3 nightly steps**

In the existing `model-tests` job, after the embeddings step:

```yaml
      - name: Ingestion tier-3 (real model)
        run: dotnet run --project ingestion/tests/Qavren.Edge.Ingestion.Tests/Qavren.Edge.Ingestion.Tests.csproj -c Release -f net10.0 -p:TargetFrameworks=net10.0
        env:
          QAVREN_EDGE_TIER3: '1'
```

and a real-world document lane, which downloads by pinned SHA-256 into the same content-hash cache and **commits nothing**. It reads `ingestion/tests/fixtures/realworld-corpus.json` (Task 1.3 Step 6b) — a committed manifest with a schema — rather than carrying URLs in a YAML comment, which is plan adjustment 26 and the difference between an owed item and a permanently green no-op:

```yaml
      - name: Fetch the real-world document corpus
        shell: bash
        run: |
          set -euo pipefail
          manifest=ingestion/tests/fixtures/realworld-corpus.json
          mkdir -p "$QAVREN_EDGE_DOCS_DIR"
          count=$(jq '.documents | length' "$manifest")
          if [ "$count" -eq 0 ]; then
            echo "::warning title=Spec 14.5 owed::realworld-corpus.json is empty; the real-world document lane asserts nothing. Add entries to ingestion/tests/fixtures/realworld-corpus.json."
            exit 0
          fi
          jq -r '.documents[] | [.id, .url, .sha256] | @tsv' "$manifest" |
          while IFS=$'\t' read -r id url sha; do
            out="$QAVREN_EDGE_DOCS_DIR/$id"
            curl -fsSL --retry 3 -o "$out" "$url"
            echo "$sha  $out" | sha256sum -c -
          done
          echo "fetched $count real-world documents"
        env:
          QAVREN_EDGE_DOCS_DIR: ${{ github.workspace }}/.docs-cache
```

Three properties make this a gate rather than decoration. It runs on the **nightly** `model-tests` job only, so an empty manifest never blocks a PR. An empty manifest emits a `::warning::` annotation on every nightly run, so the owed item is visible without anyone reading a plan. And a **wrong** digest fails the job loudly, which is the whole point of pinning one.

`jq`, `curl` and `sha256sum` are all present on `ubuntu-24.04`, which is what `model-tests` already runs on (verified in the existing `ci.yml`, line 480) — no `setup-*` step and no `apt-get` is added.

The file extension matters to the test, not to this step: `id` is used verbatim as the filename and as the `DocumentId`, so an entry's id carries its own `.docx` / `.pdf` suffix and the extractor registry resolves on it.

**The owner's part is four lines of JSON**, and it is listed as an owner-input row in **CI-only work** and gated in Task 9.1 Step 5. Task 7.1 Step 10 is the consumer.

- [ ] **Step 5: `assert-workflows.py`** (plan adjustment 9)

Replace the SP2 block

```python
sp2_tests = sorted((root / "embeddings" / "tests").glob("*/*.csproj"))
if not sp2_tests:
    problems.append("no test projects found under embeddings/tests/")
for proj in sp2_tests:
    rel = proj.relative_to(root).as_posix()
    if rel not in ci_text:
        problems.append("ci.yml has no explicit step for " + rel)
```

with the generalised form — one rule, two roots, and a loop variable that no longer shadows the `release.yml` document loaded at the top of the file:

```python
# Every test project under embeddings/tests/ and ingestion/tests/ must appear as an explicit
# ci.yml step, so a new test project cannot silently never run. Neither trim-smoke (published,
# not run as a test step) nor ingestion/tests/fixtures/ (no csproj) is matched by the glob.
for area in ("embeddings", "ingestion"):
    area_tests = sorted((root / area / "tests").glob("*/*.csproj"))
    if not area_tests:
        problems.append("no test projects found under %s/tests/" % area)
    for proj in area_tests:
        rel_path = proj.relative_to(root).as_posix()
        if rel_path not in ci_text:
            problems.append("ci.yml has no explicit step for " + rel_path)
```

and append the PdfPig assertion check beside the ORT one:

```python
# The PdfPig asset assertion (spec 15). PdfPig ships no net10.0 TFM; a silent fall-back to
# lib/netstandard2.0 is a static-constructor throw on a device with no compile error anywhere.
for token in ("PdfPig/0.1.16", "lib/net9.0"):
    if token not in ci_text:
        problems.append("ci.yml PdfPig asset assertion is incomplete (" + token + ")")
```

The `trx2junit.py` and `java-junit` counts **stay at 4** — SP3 adds no device lane.

- [ ] **Step 6: Verify**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp3"
$py = "C:\Python314\python.exe"      # Python 3.14.5 + PyYAML 6.0.3, both verified in Environment ground truth
if (-not (Test-Path $py)) { $py = (Get-Command python -ErrorAction Stop).Source }
& $py -c "import yaml,sys; yaml.safe_load(open(r'$root\.github\workflows\ci.yml', encoding='utf-8')); print('ci.yml parses')"
if ($LASTEXITCODE -ne 0) { throw "ci.yml did not parse (or PyYAML is missing - see Environment ground truth)" }
& $py "$root\foundation\tools\ci-checks\assert-workflows.py" $root
if ($LASTEXITCODE -ne 0) { throw "assert-workflows.py failed" }
```

Expected: `ci.yml parses`, then the script's `OK:` line, exit 0. The script now enumerates four `ingestion/tests/` projects and finds a step for each.

Both the interpreter and `yaml` are **measured** facts rather than assumptions — Environment ground truth records Python 3.14.5 at that exact path and `PyYAML` 6.0.3 importable from it, checked 2026-09-11. This verify is the plan's only use of a third-party Python module; `make_pdf_fixtures.py` (Task 1.3) is stdlib-only and needs nothing but CPython 3.11+.

---

### Task 2.3: Wave 2 close — sequential re-verify and commit *(integrator)*

**Local-verifiable:** yes.

**Files:** none of its own.

- [ ] **Step 1:** Delete `D:\Local\Temp\qedge-sp3\w2` and everything under it.
- [ ] **Step 2:** Re-run Task 2.1 Step 10 and Task 2.2 Step 6 sequentially, without `ArtifactsPath`.
- [ ] **Step 3:** `dotnet restore` the solution; confirm exit 0.
- [ ] **Step 4:** Commit.

```
feat(sp3): the ingestion core's declared surface, the token budget and the CI steps

- ContentHash, the document model, IChunkTokenizer and one shared prefix search
- ChunkOptions.Resolve pinned at 222/32/27 and 478/64/59; SpecialTokenOverhead measured at 2
- IngestionRecipe, IngestionSchema and the three state tables' golden SQL
- four ci.yml test steps, the PdfPig asset assert, the satellite trim-smoke publish
```

- [ ] **Step 5: Verify** — Step 2 passed and `git status --short` is clean. Wave 3 may start.

---

## WAVE 3 — Extraction in the core

### Task 3.1: `Qavren.Edge.Ingestion` part 2 — sources, the extractor registry, plain text and Markdown

**Local-verifiable:** yes.

**Files:**
- Edit: `ingestion\src\Qavren.Edge.Ingestion\IngestionSource.cs` *(the five static factories)*
- Create: `ingestion\src\Qavren.Edge.Ingestion\Internal\FolderIngestionSource.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\Internal\ItemsIngestionSource.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\Internal\SourceStream.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\Internal\DocumentExtractorRegistry.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\Internal\TextNormalizer.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\PlainTextExtractor.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\MarkdownExtractor.cs`
- Create: `ingestion\tests\Qavren.Edge.Ingestion.Tests\Extraction\*`

**Approach.** This wave has a single implementer (see the Wave map): every candidate sibling either writes the core or compiles it. The task turns the declarations Task 2.1 shipped into working extraction, and its whole job is to make two claims true — **`File.ReadAllBytes` appears nowhere in SP3**, and **every offset is an index into `ExtractedDocument.Text`**.

**Types this task owns:**

| From | Types |
|---|---|
| §11 | `IngestionSource`'s five static factories — `Folder`, `Files`, both `Items` overloads, `Single` (the abstract class and `DocumentSourceItem` are Task 2.1's) |
| §7.2 | `PlainTextExtractor`, `PlainTextExtractorOptions` |
| §7.3 | `MarkdownExtractor`, `MarkdownExtractorOptions` |

`DocumentExtractorRegistry`, `SourceStream`, `TextNormalizer`, `FolderIngestionSource` and `ItemsIngestionSource` are all `internal`; `IDocumentExtractorRegistry` itself was declared in Task 2.1.

**Defaults that change behaviour:** `PlainTextExtractorOptions.StrictUtf8 = false` (so the out-of-the-box behaviour on invalid UTF-8 is a logged Latin-1 fallback, event 914, never an exception); `MarkdownExtractorOptions.PromoteFrontMatterKeys` is **empty**, so no YAML key becomes a column by default.

- [ ] **Step 1: `TextNormalizer`**

One function: CRLF and lone CR to LF, NFC, BOM stripped, applied when `ExtractionOptions.NormalizeText`. It runs **before** any block offset is computed, because §6 says every offset indexes the normalised buffer, and an extractor that normalises after recording offsets has silently moved all of them. Off, it is the identity and the offsets index the raw decode. A test asserts both directions over `crlf-and-lone-cr.txt` and `bom.txt`.

- [ ] **Step 2: the five `IngestionSource` factories**

`Folder(path, searchPattern = "*", recursive = true, sourceId = null)` — `Directory.EnumerateFiles`, **ordinal-sorted** (so a run's document order is stable and a golden over a folder is meaningful), document ids are forward-slashed paths relative to the root, `SizeBytes` and `LastModifiedUtc` from `FileInfo` because they are free there, and `OpenAsync` returns `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true)`. A missing or unreadable root raises `IngestionSourceUnavailable` (6051) **before any document**, because a run over a folder that is not there is meaningless. `Files(paths, sourceId)` is the same open, ids are the paths.

`Items(IEnumerable<DocumentSourceItem>, sourceId)` and `Items(Func<CancellationToken, IAsyncEnumerable<DocumentSourceItem>>, sourceId)` — **the mobile shape**. SP3 never interprets the handle; the consumer's `OpenAsync` does. `Single(documentId, mediaType, openAsync, sourceId, sizeBytes = null, lastModifiedUtc = null)`.

`IngestionSourceIdInvalid` (6055) for a null, empty or whitespace source id, at construction.

- [ ] **Step 3: `SourceStream` — the seekability contract, enforced**

§7.1 is a contract with a runtime enforcement, and this is the file that enforces it:

- `OpenAsync` **SHOULD** return a seekable stream at position 0 and **MUST** be re-openable, because the pipeline calls it **twice** per document (§9.4 — once to hash, once to extract) so a hash match can skip extraction entirely.
- If the returned stream reports `CanSeek == false`, buffer it **once** into a `MemoryStream`, but only while the declared or counted length is at or below `ExtractionOptions.NonSeekableBufferLimitBytes` (4 MiB). Above that, raise `IngestionDocumentUnreadable` (6053) whose remediation says to copy the content to a file or a seekable stream first — and **do not read the rest of the stream**, because the whole point is to refuse the allocation, not to relocate it.
- An `IOException` or `UnauthorizedAccessException` on open is also 6053, recorded per document: a file deleted between enumeration and open is the common case and must never fail a run.

The reason the limit is low and the failure is loud, restated here because it is the trap: a non-seekable stream handed to `PdfDocument.Open(Stream)` makes **PdfPig itself** copy the whole PDF into a `MemoryStream`. Doing it silently on SP3's side for a 200 MB scan would be the same bug wearing our name.

- [ ] **Step 4: `DocumentExtractorRegistry`**

Resolves by media type first, then by extension, lower-case and dotted. **Consumer-registered extractors sit ahead of the built-ins**, so `.md` can be overridden. Two extractors sharing an `Id` throw `IngestionDuplicateExtractorId` (6004) **at registration**, not at the first document. No match is `ExtractorNotFound` (6101), reported per document — and when the extension is `.pdf` or `.docx` the remediation names `AddPdfExtractor()` / `AddDocxExtractor()` by name. `Describe()` returns `"text:1, markdown:1, pdf:1"` for diagnostics and is **not** the recipe fingerprint.

- [ ] **Step 5: `PlainTextExtractor`**

`Id = "text"`, `Version = 1`, `.txt .log .csv .text`, `text/plain`. Encoding resolves in three layers, no third-party dependency:

1. **BOM.** `new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true)` recognises UTF-8, UTF-16 LE/BE and UTF-32 LE/BE from the first four bytes.
2. **Strict UTF-8 probe** over the first 64 KiB with `new UTF8Encoding(false, throwOnInvalidBytes: true)`.
3. **Fallback** to `Encoding.Latin1` — byte-preserving and in the BCL — logged as event 914. `StrictUtf8 = true` raises `DocumentEncodingUndecodable` (6106) instead.

`UTF.Unknown` is **not** taken: MPL-1.1 in an MIT suite, and it drags the full legacy code-page tables into a mobile app for a case a notes corpus essentially never hits. Reading is streamed off the `StreamReader` in fixed char blocks into the text buffer — `ReadLine` drops the terminator, so a line loop cannot reproduce the raw decode the `NormalizeText = false` offset path is defined against; blocks are `Paragraph`s split on blank lines.

- [ ] **Step 6: `MarkdownExtractor`**

`Id = "markdown"`, `Version = 1`, `.md .markdown`, `text/markdown`, over Markdig 1.3.2. The pipeline is built **explicitly**:

```csharp
new MarkdownPipelineBuilder()
    .UsePipeTables()
    .UseYamlFrontMatter()
    .UsePreciseSourceLocation()
    .Build();
```

never `UseAdvancedExtensions()`, which pulls in roughly eighteen. Blocks come from the top-level `MarkdownDocument` children, except `Table` and `ListBlock`, which are descended one level so a table yields one `TableRow` block per row and a list one `ListItem` block per top-level item; every `MarkdownObject` carries a `SourceSpan`, so a block's `[Start, End)` is the verbatim source range and chunk text is a **source substring**, not a lossy re-render — fences, tables and links survive intact. Setext headings are headings. Fenced code becomes `Code` blocks rather than being dropped. YAML front matter becomes `ExtractedDocument.Metadata`; no key is promoted. A Markdig parse fault is `MarkdownParseFailed` (6155), recorded per document.

**§17 item 9 is this step's gate.** Confirm that `HeadingBlock.Span` and leaf-block spans index the *original* source rather than a normalised copy. If they do, normalise first and record spans against the normalised buffer — that is the design. If they do **not**, the extractor normalises first and re-parses the normalised text, and records offsets against that; either way the invariant that offsets index `ExtractedDocument.Text` holds, and the task records which branch was taken. A test asserts `document.Text.Substring(block.Start, block.End - block.Start)` equals the expected source slice for every block of `headings.md` and `fences.md`.

Three traps worth naming, because they are exactly what a line scanner gets wrong and a parser gets right: a `## ` line inside a fenced **or indented** code block is a code line, not a heading (`headings.md` and `fences.md` both carry one); a four-backtick fence containing three backticks is one block, not three; and `raw-html.md`'s `HtmlBlock` is a block, not prose to be split.

- [ ] **Step 7: the tests**

In `Qavren.Edge.Ingestion.Tests\Extraction\`:

- `TextNormalizerTests` — CRLF, lone CR, BOM, NFC; and that offsets recorded afterward land where expected.
- `EncodingFallbackTests` — BOM detection for all five encodings, the strict probe, the Latin-1 fallback with event 914 asserted through a capturing logger, and 6106 under `StrictUtf8`.
- `SeekabilityTests` — a non-seekable **small** stream is buffered once and ingests; a non-seekable stream over `NonSeekableBufferLimitBytes` raises 6053 **without being read to the end** (asserted with a counting stream); `OpenAsync` is called exactly **once** per extraction by a spy source and the item is re-openable. *(The "exactly twice per document" assertion this step used to carry moved to Task 5.1 Step 8 — extraction opens once, and the hash pass that makes it two does not exist until the pipeline lands.)*
- `NoReadAllBytesTests` — the assertion §7.1 asks for: reflect over `Qavren.Edge.Ingestion`'s IL, or grep the source tree from the test, and fail if `File.ReadAllBytes` or `File.ReadAllText` appears anywhere in `ingestion/src/**`. A source grep is the honest version here and is what the task ships, with the path resolved from the assembly location; on a device lane the test skips with a printed reason, because the source tree is not there.
- `ExtractorRegistryTests` — resolution order (consumer first), media type before extension, 6004 on a duplicate id, 6101's remediation naming the right satellite for `.pdf` and `.docx`, and `Describe()`'s format.
- `PlainTextExtractorTests` and `MarkdownExtractorTests` over the twelve committed fixtures: block kinds, block spans as verbatim substrings, the fenced `#` line not becoming a heading, the setext heading becoming one, the preamble surviving, front matter reaching `Metadata`, `empty.txt` and `whitespace-only.txt` yielding zero blocks and no throw.
- `FolderSourceTests` — ordinal ordering, forward-slashed relative ids, a missing root raising 6051 before any document, `SizeBytes` populated from `FileInfo`.

- [ ] **Step 8: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp3\ingestion\tests\Qavren.Edge.Ingestion.Tests\Qavren.Edge.Ingestion.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp3\w3\t31
```

Expected: `Passed!`, exit 0.

---

### Task 3.2: Wave 3 close *(integrator)*

**Local-verifiable:** yes. **Files:** none of its own.

- [ ] **Step 1:** Delete `D:\Local\Temp\qedge-sp3\w3` and everything under it.
- [ ] **Step 2:** Re-run Task 3.1 Step 8 without `ArtifactsPath`.
- [ ] **Step 3:** Commit as `feat(sp3): sources, the extractor registry, and the plain-text and Markdown extractors`.
- [ ] **Step 4: Verify** — Step 2 passed, `git status --short` clean.

---

## WAVE 4 — The three chunkers and the goldens

### Task 4.1: `Qavren.Edge.Ingestion` part 3 — `TokenWindowChunker`, `PlainChunker`, `MarkdownHeadingChunker`, the golden harness, the CsCheck properties

**Local-verifiable:** yes.

**Files:**
- Create: `ingestion\src\Qavren.Edge.Ingestion\TokenWindowChunker.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\PlainChunker.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\MarkdownHeadingChunker.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\Internal\ChunkerFactory.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\Internal\SentenceBoundary.cs`
- Create: `ingestion\tests\Qavren.Edge.Ingestion.Tests\Chunking\*`
- Create: `ingestion\tests\fixtures\golden\*.json` (20 files, generated once)
- Edit: `ingestion\tests\fixtures\README.md` *(its `## Golden generation` placeholder only — created empty by Task 1.3 Step 4; see **Cross-wave file ownership**. Replace the HTML comment and nothing else in the file.)*

**Approach.** All three chunkers are **synchronous** `IEnumerable<ChunkDraft>` over an in-memory string — chunking is CPU with no I/O, and a sync interface makes golden and property tests trivial and costs nothing. This wave has a single implementer for the same reason wave 3 does.

The one invariant that is never negotiable and is asserted in every configuration: **a chunker that emits a chunk over `MaxTokens` throws `ChunkExceedsTokenBudget` (6151)**. That is an SP3 bug, not user data, and it is distinct from `ChunkOverflow`, which governs an *input* unit bigger than the budget.

**Types this task owns:**

| From | Types |
|---|---|
| §8.2 | `TokenWindowChunker`, `PlainChunker`, `MarkdownHeadingChunker` — the three `IChunker` implementations, public so a consumer can construct one directly; `IChunker`, `ChunkDraft` and `ChunkerIds` were declared in Task 2.1 |

`ChunkerFactory` and `SentenceBoundary` are `internal`.

- [ ] **Step 0: Resolve the vocabulary, or stop** (plan adjustment 23)

**Before anything else in this wave.** Every one of the twenty goldens is generated against `MlChunkTokenizer` over the real 30,522-entry `bert-base-uncased` vocabulary at the MiniLM triple **222 / 32 / 27**. Resolve it with the three-step order in **Environment ground truth** — `%QAVREN_EDGE_VOCAB%`, then `%QAVREN_EDGE_MODEL_DIR%\vocab.txt`, then `C:\Users\steve\AppData\Local\Temp\qedge-model\vocab.txt`, where it is today — and verify the SHA-256 is `07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3`:

```powershell
$vocab = $env:QAVREN_EDGE_VOCAB
if (-not $vocab -and $env:QAVREN_EDGE_MODEL_DIR) { $vocab = Join-Path $env:QAVREN_EDGE_MODEL_DIR 'vocab.txt' }
if (-not $vocab) { $vocab = "C:\Users\steve\AppData\Local\Temp\qedge-model\vocab.txt" }
if (-not (Test-Path $vocab)) { throw "no vocab.txt - run the re-provisioning command in Environment ground truth, then retry" }
$sha = (Get-FileHash $vocab -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sha -ne '07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3') { throw "vocab.txt sha256 $sha - wrong vocabulary" }
$env:QAVREN_EDGE_VOCAB = $vocab
Write-Host "OK vocab.txt at $vocab"
```

If all three paths miss, run the **re-provisioning command in Environment ground truth** (one `Invoke-WebRequest`, 231 KB, digest-checked) and retry. **If that cannot be run, this wave stops here and the plan is blocked.** There is deliberately no degraded path: a golden generated against a probe vocabulary pins boundaries no shipped configuration produces, and a wave that writes no goldens leaves Step 9's "twenty golden comparisons" and Task 9.1 Step 5's "twenty golden files" unsatisfiable. Stopping is the cheap failure; twenty fictional goldens are not.

The test harness reads the same three-step order at runtime, so the suite behaves identically here, in `Ingestion.Tests` on a device lane (where it **skips** the golden class with a printed reason rather than failing — there is no vocabulary on a phone), and in CI's `model-tests` job (where `QAVREN_EDGE_MODEL_DIR` is already set).

- [ ] **Step 1: `SentenceBoundary`**

A backwards scan for the nearest of, in preference order: a paragraph break, a sentence terminator (`.` `!` `?` `…` `。` `！` `？` followed by whitespace or end of text, with an abbreviation stop-list — `Mr.`, `Dr.`, `e.g.`, `i.e.`, `etc.`, `vs.`, `No.`, `Fig.`, and single capital initials), then whitespace. It searches only a 15% look-back window, and returns "no boundary" rather than reaching further; **culture-invariant throughout**, because the dotted-I trap lives in any case-sensitive matcher and `InvariantGlobalization` is on repo-wide.

- [ ] **Step 2: `TokenWindowChunker`**

`Id = ChunkerIds.TokenWindow`, the terminal fallback every other chunker delegates to. Forward cut via `IChunkTokenizer.IndexByTokenCount`; the next window is seeded by counting `OverlapTokens` back from the emitted chunk's end. Each candidate cut is nudged backwards through `SentenceBoundary`; only if the window holds no boundary does it cut mid-word. Every cut is then snapped forward to a grapheme-cluster boundary via `StringInfo`/`Rune`, so no chunk ends mid-surrogate or mid-combining-mark. `SentenceAware = false` skips the nudge and keeps the snap.

Two loop-safety rules, because this is the one chunker that can fail to terminate: a cut that does not advance past the previous chunk's start is widened to the next grapheme cluster; and the emitted chunk's token count is re-verified against `MaxTokens` before it is yielded.

- [ ] **Step 3: `PlainChunker`**

`Id = ChunkerIds.Plain`. Accumulates `Paragraph` blocks while they fit; a block over budget goes to the token window; a block under `MinTokens` merges forward. Offsets are the accumulated blocks' `[first.Start, last.End)`.

- [ ] **Step 4: `MarkdownHeadingChunker`**

`Id = ChunkerIds.MarkdownHeading`. A `string?[7]` heading stack, cleared below the current level on each new heading. Splits at `SplitHeadingLevels` (H1–H3 by default), so H4+ stay inside their parent section, which is how documents are actually written. Seven rules, each of which the prior art got wrong:

1. `ChunkDraft.HeadingPath` is the structural array **in memory**; what is stored is the rendered breadcrumb, sanitised per Task 2.1's `SanitizeHeading`.
2. Headings are **excluded from the chunk body** and prepended to the *embed* text, never duplicated into the stored text.
3. Content before the first heading becomes a **preamble chunk** when `IncludePreamble` (default true). The prior art dropped it and lost every document's lede — `headings.md` has one and its golden proves it survives.
4. A section over budget falls to the token window **with overlap preserved and the same heading path on every piece**. MEDI's structure-aware chunkers produce zero overlap because their shared splitter never reads `OverlapTokens`; that is the defect this design exists not to inherit, and `giant-heading-section.md`'s golden is where it is asserted.
5. Sections under `MinTokens` merge into the next sibling, event 911.
6. Tables split row-wise with the header row re-emitted on every piece when `RepeatTableHeaderRow`.
7. A breadcrumb over `HeadingPathTokenBudget` is truncated **from the left** — the deepest headings are the most specific — and logged as event 910. It is never allowed to eat the content budget, which is how MEDI's splitter ends up throwing; if it still does not fit after left-truncation, that is `ChunkContextTooLong` (6152), or `ChunkOverflow`'s configured behaviour.

- [ ] **Step 5: `ChunkerFactory`**

`ChunkerIds.Auto` selects `MarkdownHeading` for `text/markdown` and `Plain` otherwise, logging event 922 when a structure-aware chunker falls back. An explicit `ChunkerId` is honoured verbatim.

- [ ] **Step 6: the golden harness and the twenty files** (plan adjustment 12)

One JSON per golden, holding `{ index, startChar, endChar, tokenCount, headingPath, breadcrumb, text, embedText }` — a declared **superset** of the six fields originally listed here, kept in their original order. `breadcrumb` and `embedText` are required because `PrependHeadingPath` changes only the embed text: without them a `.no-breadcrumb` golden is byte-identical to its `.auto` counterpart and asserts nothing (amended 2026-09-11 during Task 4.1; spec §14.1 carries the same amendment). **Full text**, because the fixtures are small and a moved boundary should be legible in the diff rather than a changed hash.

The rules that make a golden test a gate rather than a rubber stamp, and both are SP2's `reference-vectors.json` rule:

- Goldens are written **only** under `QAVREN_EDGE_WRITE_GOLDEN=1`, and **the writer refuses to overwrite an existing file.** Deleting it is the deliberate act, and regeneration is its own PR with the reason in the body.
- A golden is generated against the **MiniLM triple 222 / 32 / 27** and the `MlChunkTokenizer` built from the real `bert-base-uncased` vocabulary **that Step 0 resolved and digest-checked**. Step 0 is a hard precondition, not a conditional: the vocabulary is on this box, and if it ever is not, the wave stops rather than writing goldens against a substitute.

**The twenty, by file name** (plan adjustment 12). The naming convention is `<fixture-stem>.<chunker-id>[.<variant>].json`, so a diff's file list alone says what moved:

| # | Golden file | Fixture | Chunker | Options | Asserts |
|---|---|---|---|---|---|
| 1 | `empty.auto.json` | `empty.txt` | auto → plain | defaults | **zero chunks** |
| 2 | `whitespace-only.auto.json` | `whitespace-only.txt` | auto → plain | defaults | **zero chunks**, and no *empty* chunk |
| 3 | `three-paragraphs.auto.json` | `three-paragraphs.txt` | auto → plain | defaults | paragraph accumulation, no trailing newline |
| 4 | `crlf-and-lone-cr.auto.json` | `crlf-and-lone-cr.txt` | auto → plain | defaults | offsets index the **normalised** buffer |
| 5 | `long-token.auto.json` | `long-token.txt` | auto → plain | defaults | the hard split with no separator to back up to |
| 6 | `unicode.auto.json` | `unicode.txt` | auto → plain | defaults | no boundary inside a grapheme cluster |
| 7 | `bom.auto.json` | `bom.txt` | auto → plain | defaults | offsets start **after** the stripped BOM |
| 8 | `headings.auto.json` | `headings.md` | auto → markdown-heading | defaults | preamble survives; the fenced `#` is not a heading |
| 9 | `fences.auto.json` | `fences.md` | auto → markdown-heading | defaults | three code blocks, no headings at all |
| 10 | `tables-lists.auto.json` | `tables-lists.md` | auto → markdown-heading | defaults | row-wise table split, header re-emitted |
| 11 | `raw-html.auto.json` | `raw-html.md` | auto → markdown-heading | defaults | one `HtmlBlock`, kept verbatim |
| 12 | `giant-heading-section.auto.json` | `giant-heading-section.md` | auto → markdown-heading | defaults | over-budget section splits, breadcrumb on **every** piece |
| 13 | `headings.markdown-heading.no-preamble.json` | `headings.md` | markdown-heading | `IncludePreamble = false` | the lede is dropped, and only the lede |
| 14 | `giant-heading-section.markdown-heading.no-preamble.json` | `giant-heading-section.md` | markdown-heading | `IncludePreamble = false` | a document with no preamble is unchanged by the flag |
| 15 | `headings.markdown-heading.no-breadcrumb.json` | `headings.md` | markdown-heading | `PrependHeadingPath = false` | embed text loses the breadcrumb; **stored** text never had it |
| 16 | `tables-lists.markdown-heading.no-breadcrumb.json` | `tables-lists.md` | markdown-heading | `PrependHeadingPath = false` | table rows keep their header without the breadcrumb |
| 17 | `giant-heading-section.markdown-heading.no-breadcrumb.json` | `giant-heading-section.md` | markdown-heading | `PrependHeadingPath = false` | the flag shows in `embedText`; the boundaries are unchanged by design (amended 2026-09-11: `ChunkOptions.Resolve` subtracts `HeadingPathTokenBudget` whether or not the breadcrumb is prepended, so no budget is freed) |
| 18 | `long-token.token-window.json` | `long-token.txt` | token-window | defaults | the terminal fallback, explicitly |
| 19 | `three-paragraphs.token-window.json` | `three-paragraphs.txt` | token-window | defaults | sentence-aware nudge inside the 15% look-back |
| 20 | `giant-heading-section.token-window.json` | `giant-heading-section.md` | token-window | defaults | overlap exactness over a long run |

12 + 5 + 3 = 20. Rows 13–17 are authorised by plan adjustment 12, not invented here — an earlier draft carried them in this task with no adjustment behind them while the adjustment described a different, and arithmetically wrong, seventeen-file set.

Then **fill `ingestion/tests/fixtures/README.md`'s `## Golden generation` placeholder** — the only edit this task makes to a file another wave created, and it is declared in this task's Files list. Replace the HTML comment Task 1.3 left with: the resolved vocabulary path, its SHA-256, the MiniLM triple 222 / 32 / 27, the twenty file names above, the `QAVREN_EDGE_WRITE_GOLDEN=1` rule and the refuse-to-overwrite rule, and the sentence that regeneration is its own PR with the reason in the body. Change nothing else in that file.

- [ ] **Step 7: `FixtureDriftTests`**

Re-runs Task 1.3's manifest check from the **embedded resources**, so the guard holds on a device as well as on the host: every entry's byte length and SHA-256 match `manifest.json`, and the twelve text/Markdown entries match the literal table. A BOM or an eol flip fails here rather than confusingly at chunk 7.

- [ ] **Step 8: the CsCheck properties**

`seed:` and `iter: 500` pinned **at the call site**; the nightly lane widens through `CsCheck_Iter` / `CsCheck_Seed`, and a failure prints the seed to replay. Over generated strings from an ASCII + CJK + combining-mark + ZWJ alphabet, `maxTokens ∈ [24, 512]`, `overlap ∈ [0, maxTokens/2)`:

- every chunk's token count ≤ `MaxTokens`, counted with the **same `IChunkTokenizer` instance** the chunker used — never `text.Length / 4`;
- offsets strictly ascending, `start < end ≤ Text.Length`, coverage contiguous modulo overlap, no gaps;
- overlap exactness: the last *k* tokens of chunk *i* equal the first *k* of *i+1*;
- de-overlapped reassembly equals the input character for character, for the lossless chunkers;
- no boundary splits a surrogate pair or a grapheme cluster;
- empty input → zero chunks; non-empty input → no empty chunk (`ChunkerProducedEmptyChunk`, 6154, if one appears);
- monotonicity: raising `MaxTokens` never raises the chunk count;
- purity: two runs over one input produce identical output;
- culture independence: identical output under `InvariantCulture` and `tr-TR`.

The offset-contract property lives in Task 2.1's `TokenIndexSearchTests` and is not duplicated here; the `heading_path` round-trip lives in Task 2.1's `HeadingSanitiserTests`. The `SampleModelBased` incremental property belongs to Task 7.1, where there is a pipeline to compare against a naive re-embed-everything model.

- [ ] **Step 9: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp3\ingestion\tests\Qavren.Edge.Ingestion.Tests\Qavren.Edge.Ingestion.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp3\w4\t41
```

Expected: `Passed!`, exit 0, with twenty golden comparisons and the drift test green. Then, to prove the writer's refusal:

```powershell
$env:QAVREN_EDGE_WRITE_GOLDEN = '1'
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp3\ingestion\tests\Qavren.Edge.Ingestion.Tests\Qavren.Edge.Ingestion.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp3\w4\t41
Remove-Item Env:\QAVREN_EDGE_WRITE_GOLDEN
```

Expected: still `Passed!`, and **no golden file's timestamp changed** — the writer refused every existing file. A run that rewrites them is the failure mode this check exists for.

---

### Task 4.2: Wave 4 close *(integrator)*

**Local-verifiable:** yes. **Files:** none of its own.

- [ ] **Step 1:** Delete `D:\Local\Temp\qedge-sp3\w4` and everything under it.
- [ ] **Step 2:** Re-run Task 4.1 Step 9, both halves, without `ArtifactsPath`.
- [ ] **Step 3:** Commit as `feat(sp3): the three chunkers, twenty goldens and the CsCheck properties`.
- [ ] **Step 4: Verify** — Step 2 passed, `git status --short` clean (the golden re-run wrote nothing).

---

## WAVE 5 — State, the diff, the write protocol, the runner, registration

### Task 5.1: `Qavren.Edge.Ingestion` part 4 — the state store, the incremental diff, the writer, the pipeline, `AddIngestion`, the observer, diagnostics

**Local-verifiable:** yes.

**Files:**
- Create: `ingestion\src\Qavren.Edge.Ingestion\Internal\IngestionStateStore.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\Internal\IngestionStateMigration.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\Internal\ChunkDiff.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\Internal\ChunkWriter.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\Internal\IngestionRunner.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\Internal\IngestionPipeline.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\Internal\IngestionLifecycleObserver.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\Internal\IngestionDiagnosticsContributor.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\Internal\IngestionValidateStartupTask.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\Internal\CollectionShapeProjection.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion\IIngestionPipeline.cs` *(§11's pipeline surface: `IIngestionPipeline`, `IngestionRunOptions`, `IngestionDocumentOutcome`, `IngestionDocumentStatus`, `IngestionFailure`, `IngestionDocumentResult`, `IngestionRunResult`, `IngestionStatus`)*
- Create: `ingestion\src\Qavren.Edge.Ingestion\IngestionEdgeBuilderExtensions.cs`
- Create: `ingestion\tests\Qavren.Edge.Ingestion.Tests\Runtime\*`

**Approach.** The wave that closes the core. Everything after it reads a frozen assembly. Four invariants govern the whole task and each has a test:

1. **No SP3 write nests an SP2 collection call inside an SP3 transaction.** `EdgeDatabase.ExecuteInTransactionAsync` opens a **new connection** per call and SP2's `UpsertAsync` and `DeleteAsync` each wrap themselves in one, so nesting is two connections contending for the WAL write lock — `SQLITE_BUSY`, not atomicity.
2. **Additions land before removals**, so a crash mid-document never deletes content that has not yet been replaced.
3. **The state row commits last**, so a torn document is still dirty: its stored `content_hash` is still the old one, the next run re-enters at §9.4 step 4, finds the already-committed chunks by hash, and `added` is only the remainder. Resume is not a separate code path.
4. **Embedding happens strictly outside any transaction.** Step a1 completes before a2 is called, and a2 opens the only transaction in the pair.

**Types this task owns** (adopted by reference from the spec, transcribed verbatim with their XML docs). This is the last of §11's declarations to find an owner, so after this task obligation 1 — every public type the spec declares is NAMED by exactly one task — is discharged for the core:

| From | Types |
|---|---|
| §11 | `IIngestionPipeline`, `IngestionRunOptions`, `IngestionDocumentOutcome`, `IngestionDocumentStatus`, `IngestionFailure`, `IngestionDocumentResult`, `IngestionStatus`, `IngestionEdgeBuilderExtensions` (`AddIngestion`, both `AddDocumentExtractor` overloads, `UseChunkTokenizer`, `UseIngestionThrottle`) |
| §10.2 | `IngestionRunResult` (including its computed `Failures` projection) |

Everything else this task creates is `internal`: the state store, the state migration, the diff, the writer, the runner, the pipeline implementation, the observer, the diagnostics contributor, the startup task, `CollectionShapeProjection` and `IngestionSchemaOnlyGenerator`.

**Defaults that change behaviour** — the obligation-2 heading for this task. `IngestionRunOptions` is the type a consumer touches on every call, and every one of its members is null-or-false by design, which is not the same as arbitrary:

- **`Budget = null`** → `IngestionBudget.Unlimited`. A run with no budget never suspends for time, and that is the right default for a desktop `RunAsync` the caller is awaiting. The two presets are literal (plan adjustment 20, Task 2.1 Step 7): **`Quick` is `MaxDuration = 20 s` and nothing else**, **`Background` is `MaxDuration = 5 min` and nothing else** — restated here because this task is what meters them, and §10.2 pinned them no harder than "~20 s" and "~5 min".
- **`Force = false`** → the §9.4 hash gate is honoured, so an unchanged corpus performs zero embeddings. `Force = true` bypasses **both** gates (timestamp and hash) and re-extracts, re-chunks and re-diffs every document; it does **not** bypass the diff itself, so an unchanged document still writes nothing — `Force` costs CPU, never embeddings, and the XML doc says exactly that, because the name invites the opposite reading.
- **`DeleteMissing = null`** → falls back to `IngestionOptions.DeleteMissingDocuments`, which is `true`. A nullable-bool override rather than a bool is deliberate: `false` must be distinguishable from "not specified", or a per-run override could never turn pruning *off* for a run against options that have it on.
- **`FailFast = false`** → a document failure is recorded and the run continues, matching `IngestionOptions.ContinueOnDocumentError = true`. Set on a single run, it promotes the first document failure to a run failure and rethrows the original, without mutating the registered options.
- **`ThrowOnCancellation = false`** → cancellation surfaces as `IngestionRunOutcome.Cancelled` with **no exception**. §13.3 requires this: a cancelled run is a normal outcome carrying counters a caller wants to read, and an `OperationCanceledException` throws those counters away.
- **`WriteBatchSize = null`** → `IngestionOptions.WriteBatchSize`, 32. **`Progress = null`** → no callbacks. **`Filter = null`** → every enumerated item is considered.

`OptionsDefaultsTests` (Task 2.1) gains a second table covering all eight, so a silent change to one is a failing test rather than a behaviour change.

- [ ] **Step 1: `IngestionStateStore` and `IngestionStateMigration`**

The store is plain SQL through `IEdgeDatabase` over the three tables Task 2.1's `BuildStateSql` created. Its surface is: read a document row, upsert one, stamp `last_run_id`, delete one, enumerate rows under a source, count the four diagnostics populations, insert and finish a run row, and trim `_run` to `RunHistoryLimit` (20) at the end of each run.

`qedge_ingest_meta` holds `schema_version` (`IngestionSchema.StateSchemaVersion`, 1) and `hash_algorithm` (`"xxh128-v1"`). A stored `hash_algorithm` that differs is `IngestionHashAlgorithmMismatch` (6202) rather than a mis-diff; an unknown `schema_version` is `IngestionStateSchemaUnsupported` (6203); absent tables are `IngestionStateMissing` (6201) with the remediation `AddIngestion(version)`. Forward-only — this suite has no down-migrations.

A document row whose `status` is `Failed` or `NoTextLayer` is **kept**, so the next run skips it on an unchanged hash instead of retrying a broken file forever — and a recipe bump retries it automatically, which is the correct retry trigger.

`IngestionStateMigration : IEdgeMigration` carries `BuildStateSql`'s statements and runs inside the transaction SP1's migrator owns, opening none of its own.

- [ ] **Step 2: `CollectionShapeProjection`** (plan adjustments 4 and 5)

SP2's `EdgeVectorCollectionSchemaFactory.ToStoreOptions` is `internal`, so SP3 reimplements it. It must match **byte for byte in effect**, brace-escaping included — the `*NameFormat` properties are composite format strings, and a table name containing `{` that is passed through unescaped is a `FormatException` at DDL time:

```csharp
namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// Projects the per-collection overrides onto an EdgeVectorStoreOptions, which is what
/// EdgeVectorSchema's constructor takes. A five-line reimplementation of SP2's internal
/// EdgeVectorCollectionSchemaFactory.ToStoreOptions, because that type is not visible here and
/// Qavren.Edge.VectorData grants no InternalsVisibleTo.
/// <para>
/// FullTextRemoveDiacritics does NOT come from the collection options: SP2 declares
/// EdgeVectorStoreCollectionOptions.RemoveDiacritics as `internal`, so nothing outside that
/// assembly can set or read it. It comes from IngestionOptions instead (plan adjustment 4), and
/// error 6011's remediation names that member.
/// </para>
/// </summary>
internal static class CollectionShapeProjection
{
    public static EdgeVectorStoreOptions ToStoreOptions(
        EdgeVectorStoreCollectionOptions options, int fullTextRemoveDiacritics)
    {
        ArgumentNullException.ThrowIfNull(options);

        var store = new EdgeVectorStoreOptions();

        if (options.VectorTableName is { Length: > 0 } vectorTable)
        {
            store.VectorTableNameFormat = Escape(vectorTable);
        }

        if (options.FullTextTableName is { Length: > 0 } fullTextTable)
        {
            store.FullTextTableNameFormat = Escape(fullTextTable);
        }

        if (options.ChunkSize is { } chunkSize)
        {
            store.ChunkSize = chunkSize;
        }

        if (options.FullTextTokenizer is { } tokenizer)
        {
            store.FullTextTokenizer = tokenizer;
        }

        store.FullTextRemoveDiacritics = fullTextRemoveDiacritics;
        return store;

        static string Escape(string literal) => literal
            .Replace("{", "{{", StringComparison.Ordinal)
            .Replace("}", "}}", StringComparison.Ordinal);
    }
}
```

- [ ] **Step 3: `ChunkDiff` — §9.4 step 4 and 5**

One direct SQL read through `IEdgeDatabase`, five columns, never the text, using the `document_id` index SP2 creates for an `IsIndexed` property:

```sql
SELECT "key","content_hash","ordinal","char_start","char_end"
FROM "<data>" WHERE "source_id" = $s AND "document_id" = $d
```

Set-difference on `(content_hash, duplicateOrdinal)` produces `added`, `removed`, `unchanged`; among `unchanged`, any whose `ordinal`, `char_start` or `char_end` moved go to `repaired`.

Chunk identity is §9.3: `key = xxHash128(sourceId ␟ documentId ␟ chunkContentHash ␟ duplicateOrdinal)` rendered as 32 hex. `chunkContentHash` is over the **embed text as SP3 composes it** — breadcrumb plus text — which by §8.3 **excludes** `Model.DocumentPrefix`: the prefix is a property of the model, not of the chunk, and it lives in the recipe hash instead. `duplicateOrdinal` counts prior occurrences of the same hash **within the same document**, which is what makes identity unique for a document that legitimately repeats a paragraph. Ordinal is **stored but is not part of identity** — that is exactly what makes the cheap repair possible.

- [ ] **Step 4: `ChunkWriter` — §9.5's four-step protocol, in this order**

```
a. for each window of `added`, WriteBatchSize (32) chunks:
     a1. SP3 calls IEmbeddingGenerator<string, Embedding<float>>.GenerateAsync(embedTexts)  — ONE call
     a2. SP3 calls collection.UpsertAsync(records carrying ReadOnlyMemory<float>)           — ONE call
b. delete `removed` keys in windows of DeleteBatchSize (500), one collection.DeleteAsync per window
c. repair `repaired` — SP3's own ExecuteInTransactionAsync, batched UPDATEs
d. write the document state row — SP3's own transaction, LAST
```

**Step a1 is SP3's call, not SP2's, and that is a deliberate reversal of the obvious.** SP3's definition declares the vector property as `ReadOnlyMemory<float>` rather than a `string` source, so `CollectionModel.EmbeddingGenerationRequired` is false and `ResolveVectorsAsync` takes its pre-computed path. Four things depend on owning that call and none is reachable if SP2 makes it: `EmbedCalls` counts a1 exactly; `TokensEmbedded` is `GeneratedEmbeddings.Usage?.InputTokenCount` when published (verified: SP2's generator always sets it) and the summed `IChunkTokenizer.CountTokens` over the window's embed texts otherwise, with the diagnostics key `tokensEmbeddedSource` saying which; `IngestionBudget.MaxTokens` meters the same number; and **6206 and 6207 are separated by call site rather than by guesswork** — a throw out of a1 is `IngestionEmbeddingFailed` (6206), caught, batch halved, event 918 logged, the **embed** retried once, a second failure suspending the run; a throw out of a2 is `IngestionWriteFailed` (6207) and is not retried, with `EdgeVectorStoreException` preserved as inner.

The repair is `UPDATE "<data>" SET "ordinal"=?, "char_start"=?, "char_end"=? WHERE "key"=?`, batched inside one transaction. It never recomputes a vector and never touches the `vec0` row, because `vec0` is keyed on `_rowid` and an `UPDATE` does not move it. It **does** fire SP1's `"<fts>_au"` trigger, which is `AFTER UPDATE ON <data>` unqualified and performs an FTS5 delete-plus-insert per row — so inserting one paragraph at the top of a 500-chunk document is one embedding and 499 FTS5 delete/insert pairs. That is the honest number, `RepairOrdinals` exists to turn it off, and §17 item 7 measures it.

A failed state write is `IngestionCheckpointWriteFailed` (6205) and **aborts the run**: an unwritten checkpoint means the next run redoes work it believes is done. The transaction rolled back, so the document stays at its previous state.

- [ ] **Step 5: `IngestionRunner` and `IngestionPipeline`**

§10.1's sequential stages: `enumerate → [size gate] → hash → [hash gate] → extract → chunk → hash chunks → diff → embed+upsert (windowed) → delete → repair → state row`. No channels, no parallel stages.

The size gate is §9.1's **two** enforcement points, because `DocumentSourceItem.SizeBytes` is `long?`: when the source declares a size the gate runs at enumeration and the file is never opened; when it is null the gate degrades to the counted read inside the hash pass, and on that path the document is recorded `Failed` with `IngestionDocumentTooLarge` (6052) **with no content hash written**, so a later shrink of the file is re-attempted.

Before each document and before each write window, §10.2's three evaluations in order: stop-requested or caller-cancelled → `Cancelled` (or `Suspended` when the stop came from lifecycle); budget → `Suspended` with `budget:duration` / `budget:documents` / `budget:chunks` / `budget:tokens`; then `IIngestionThrottle.Evaluate` → a batch size, a delay, or a pause. A pause is `Suspended` by default; `Wait` delays and re-evaluates.

**Pruning (§9.6) is skipped entirely on a `Suspended`, `Cancelled` or `Failed` run.** A partial enumeration is not evidence a document is gone, and deleting live content on a budget timeout would be the worst bug this package could ship. Because someone who only ever runs under a `MaxDuration` budget therefore never prunes, `PruneAsync` is an explicit API performing a full enumeration and sweep with no write phase, and `IngestionStatus.StaleDocumentCount` reports how many rows are currently un-stamped.

`IProgress<IngestionProgress>` is invoked at most once per 100 ms **plus** once per document boundary — never per chunk. Counts, never a percentage.

`IngestionRunAlreadyActive` (6008) on a concurrent `RunAsync` against the same pipeline, naming the active run id. `AbortAfterConsecutiveErrors` (20) is `IngestionRunAborted` (6010) with the last failure as inner. `ContinueOnDocumentError = false` (or `IngestionRunOptions.FailFast`) promotes the first document failure to a run failure and **rethrows the original**. Cancellation is checked **before** opening a transaction, never inside one, and uses `CancellationToken.None` for rollback exactly as `EdgeMigrator` does.

**Four error codes the spec declares and an earlier draft of this plan left with no raise site.** Each is implemented here, each has a named test in Step 8, and each has a row in the **Error-code ownership** table. A declared code nobody raises is not harmless: Task 9.1 Step 5 gates on "every `EdgeErrorCode` 6000–6299 is raised from somewhere", and a gate whose subject nothing implements fails at the gate.

1. **6002 `IngestionCollectionNotConfigured`** (plan adjustment 21 — §13.3 has no row for it). Every `IIngestionPipeline` method that takes a `string? collectionName` — `RunAsync` (both overloads), `PruneAsync`, `RemoveSourceAsync`, `RemoveDocumentAsync`, `GetStatusAsync` — resolves it against the set of collections `AddIngestion` configured. A non-null name that is not in that set throws `IngestionCollectionNotConfigured` **before any enumeration, any open and any write**, with the requested name and the configured names in the message and the remediation *call `AddIngestion` for that collection, or pass `null` to use the single configured one*. A `null` name with exactly one configured collection resolves to it; a `null` name with more than one is also 6002, because guessing is worse. Without this check a typo'd collection name reaches the state store and comes back as `IngestionStateMissing` (6201), which blames the schema for a typo and sends the consumer to a migration that is already correct.

2. **6009 `IngestionRecipeChanged` — the `StrictRecipe` branch, which did not exist.** §13.3: *"Recipe drift with `StrictRecipe` | run | Throw with both hashes; default is to re-index and log 915."* The runner computes the run's recipe hash once and compares it against each document's stored `recipe_hash` during the §9.4 diff. **Two branches, and the plan previously implemented only the second:**
   - `IngestionOptions.StrictRecipe = true` → **throw** `IngestionRecipeChanged` (6009) on the **first** document whose stored hash differs, before any embed or write for that document, with **both hashes** in the message (`stored` and `current`, 32 hex each) and the remediation *re-index deliberately by clearing `StrictRecipe`, or delete the state rows for this collection and re-run*. It is a **run**-tier fault, not a document one: a recipe change dirties the whole corpus, so failing one document and continuing would produce a collection half in each recipe, which is the exact condition `StrictRecipe` exists to refuse.
   - `StrictRecipe = false` (the default) → log event **915 once per run** with both hashes and re-index every drifted document normally.

   Which branch a consumer is on is not a detail: a run that throws and a run that silently re-embeds a whole corpus are the two most different outcomes this package has, and both are reachable from one boolean.

3. **6054 `IngestionDuplicateDocumentId`** — §13.3: *"One `DocumentId` yielded twice in a run | document | Second recorded `Failed`."* Duplicate detection belongs to the **runner**, not to any source: `IngestionSource.Items` takes a consumer-supplied enumerable that SP3 does not control, and `Folder` can legitimately produce the same id twice if a consumer passes overlapping patterns. The runner holds a `HashSet<string>` of document ids seen **this run**, `StringComparer.Ordinal` (ids are paths or app-chosen keys, and case-folding them would merge two real documents on Linux). The first occurrence processes normally; every later occurrence is recorded `Failed` with `IngestionDuplicateDocumentId`, contributes to `DocumentsFailed`, and is **not** written — the second document never reaches the extractor, so the first document's chunks stay intact. Silently overwriting is the alternative and it is worse: the state row is keyed `(collection, source_id, document_id)`, so the second document would replace the first's state while both documents' chunks sat in the collection under different content hashes, and the next run would delete a live document's chunks as `removed`. The set is per-run and is dropped when the run ends, so a document that legitimately keeps its id across runs is unaffected.

4. **6102 `ExtractionFailed`** (plan adjustment 21 — §13.3 has no row for it either). The runner wraps the `ExtractAsync` call in a catch that lets the four **typed** extraction faults through unchanged — `DocumentEncrypted` (6103), `DocumentMalformed` (6104), `DocumentEncodingUndecodable` (6106), `DocumentPageBudgetExceeded` (6107), each of which an extractor raises with its own diagnosis — and converts **everything else** into an `EdgeExtractionException` with `ExtractionFailed`, the original as `InnerException`, and `ExtractorId` set from the selected extractor. Recorded per document, event 907, the run continues. This is the catch-all that stops a `NullReferenceException` in a third-party parser from becoming an unhandled run fault, and it is why 6103/6104/6106/6107 are raised by the **extractors** (Task 6.1, Task 3.1) while 6102 is raised by the **runner**: an extractor that knows what went wrong says so, and an extractor that does not gets one honest code rather than a leaked exception type.

- [ ] **Step 6: `AddIngestion` — §11.1's five steps, under plan adjustment 2**

1. **Validate every tokenizer-independent option**, throwing `IngestionOptionsInvalid` (6005) here: explicitly-set `OverlapTokens >= MaxTokens / 2` or `MinTokens >= MaxTokens`, a non-positive `HeadingPathTokenBudget` / `WriteBatchSize` / `DeleteBatchSize`, a `SleepGraceBudget` over 2 s, a `FullTextRemoveDiacritics` outside 0–2, and a `StateTablePrefix` that is not `^[A-Za-z_][A-Za-z0-9_]*$`. It does **not** resolve `ResolvedChunkOptions` — that needs an `IChunkTokenizer` for `SpecialTokenOverhead` and the `DocumentPrefix` reserve, no tokenizer exists at builder time, and `AddOnnxIngestion()` has not even been chained yet.
2. **Build the definition** via `IngestionSchema.BuildDefinition(Dimensions ?? Model.Dimensions, DistanceFunction, FullTextIndexed)`.
3. **Register two migrations.** `builder.AddVectorCollectionMigration(migrationVersion, CollectionName, definition, DatabaseName, ConfigureCollection)` for the collection — SP2's own path, which also enters SP3's collection in `EdgeVectorCollectionRegistry` — and `builder.AddMigrations([new IngestionStateMigration(migrationVersion + 1, StateTablePrefix)], DatabaseName)` for the state. The XML doc states plainly that both versions are claimed and that they must be unique and ascending across the whole database, because `PRAGMA user_version` is one counter.
4. **`TryAdd` everything else** — the pipeline, the state store, the registry, the chunker factory — and `TryAddEnumerable` the diagnostics contributor, which is a genuine enumerable. Every registration is guarded, because SP2 has two paths that are not and SP3 must not add a third. A second `AddIngestion` for the same collection is `IngestionMigrationVersionConflict` (6007).

   **The lifecycle observer is the one exception** (plan adjustment 7). It is registered by **inserting its `ServiceDescriptor` at index 0** of the `IServiceCollection`, guarded by a descriptor scan for an existing `IngestionLifecycleObserver` so `AddIngestion` stays idempotent — the same idiom SP2's `VectorDataLifecycleObserver` uses to get ahead of SP1's. `TryAddEnumerable` appends and cannot produce that order. The ordering matters *more* under plan adjustment 2, not less: SP2's bounded FTS5 merge now runs against **SP3's own sidecar**, and `[Ingestion, VectorData, Sqlite]` is what stops it running while SP3's runner is still writing to that table.
5. **One `IEdgeStartupTask` at order 400 that touches no database**, running in this order:
   - resolve `IChunkTokenizer` → `TokenCounterMissing` (6001), remediation naming `AddOnnxIngestion()` and `UseChunkTokenizer()`. **There is deliberately no chars/4 fallback** — that is the prior art's hidden-truncation bug.
   - resolve the unkeyed or `StoreName`-keyed `IEmbeddingGenerator<string, Embedding<float>>` with `GetService` → `IngestionEmbeddingGeneratorMissing` (6208). SP3 calls the generator itself, so its absence is a start-time fact, not a first-document surprise.
   - `ChunkOptions.Resolve(Model, tokenizer)`, cached on the pipeline and carried by the recipe → 6003 / 6153.
   - assert the generator's `EmbeddingGeneratorMetadata.DefaultModelDimensions`, when non-null, equals the collection's → `IngestionCollectionDimensionMismatch` (6006).
   - **the 6011 DDL comparison.** Resolve the store, call `GetDynamicCollection(collectionName, definition)`, read the resulting `EdgeVectorSchema` through `collection.GetService(typeof(EdgeVectorSchema))` — verified to be constructed eagerly with **no connection opened** — and compare its `BuildCreateSql()` against the statement list built from `new EdgeVectorSchema(new EdgeCollectionModelBuilder(name).BuildDynamic(definition, IngestionSchemaOnlyGenerator.Instance), name, CollectionShapeProjection.ToStoreOptions(collectionOptions, FullTextRemoveDiacritics), alwaysCreateFullTextIndex)`. Both sides are pure string construction, so this costs microseconds and runs on every start. A mismatch is `IngestionCollectionSchemaMismatch` (6011) naming the first differing statement, with the remediation: *pass the same shaping values to `AddIngestion`'s `ConfigureCollection` that you passed to `AddVectorStore`, and set `IngestionOptions.FullTextRemoveDiacritics` to match.* Comparing emitted SQL rather than field-by-field is deliberate — one comparison covers chunk size, tokenizer, diacritics, table names and dimensions at once, and cannot fall behind a future SP2 option.

   This is the **one** file in the core that names a `ProviderServices` type (`CollectionModel`, through `BuildDynamic`), so it carries a file-scoped `#pragma warning disable MEVD9001` with the reason written on it rather than a project-wide `NoWarn`. `IngestionSchemaOnlyGenerator` is the twelve-line `internal sealed class` §11.1 describes — `GenerateAsync` throws with a message naming `AddOnnxEmbeddings`, `GetService` returns null so nothing can read a width off it, `Dispose` is empty — and it is never invoked on any path, because SP3's vector property is `ReadOnlyMemory<float>` and the refusal that forces the stand-in in SP2 does not even arise here.

   None of the five opens a connection or acquires an ONNX session: the tokenizer is a vocabulary parse, the generator is resolved but not called, and both DDL sides are pure string construction.

Plus the three additional builder calls §11 declares: `AddDocumentExtractor` (instance and factory overloads), `UseChunkTokenizer`, `UseIngestionThrottle`.

- [ ] **Step 7: the observer and the diagnostics contributor**

`IngestionLifecycleObserver : EdgeLifecycleObserver`, §12's table minus the merge row (plan adjustment 2): `Sleeping` sets the stop flag and awaits the runner's next committed checkpoint within `SleepGraceBudget` (750 ms, validated ≤ 2 s), logging 902 or a timeout, and **never checkpoints WAL**; `MemoryPressure(Moderate)` halves the effective write batch with a floor of 4 and logs 918; `MemoryPressure(Critical)` halves and sets the stop flag; `Resumed` clears the shrink flag and does **not** auto-restart a run; `Stopping` sets the stop flag and waits up to `StopGraceBudget` (5 s).

The grace budget is capped for a measured reason: SP1's `AndroidLifecycleBridge` and `AppleLifecycleBridge` both raise with `GetAwaiter().GetResult()` **on the platform callback thread**, against iOS's documented ~5 s window and Android's `OnPause` ANR path. `BeginBackgroundTask` does not make a blocking delegate safe. A missed grace window logs and returns immediately — the run keeps going and hits its own checkpoint shortly, and the app was backgrounded anyway.

`IngestionDiagnosticsContributor` — `ComponentName = "Qavren.Edge.Ingestion"`, version from `AssemblyInformationalVersion`, and §12's key list minus `lastFtsMergePages` / `lastFtsMergeMs`. The five count keys are emitted **only** when `IncludeCountsInDiagnostics`; when off they are present with the value `"(disabled)"`, **never omitted**, so nobody reads a missing key as zero. `Describe()` is synchronous and cannot await, so those five are synchronous counting queries — four `COUNT(*)` over `<p>_document` with a `WHERE` and one over the collection's data table.

**Logging.** Every event id through `LoggerMessage.Define` source-generated delegates, because CA1848 is an error under `TreatWarningsAsErrors`. Information for run lifecycle, recipe change and pruning; Debug for per-document and per-window detail; Warning for extraction warnings, no-text-layer, encoding fallback, breadcrumb truncation, throttle pauses and a missed grace window; Error for document and run failures. **No document text is ever logged** — ids, paths, offsets and counts only, and a test asserts it by capturing every log line during an ingest of `unicode.txt` and failing if any contains a substring of the file.

- [ ] **Step 8: the tests**

In `Qavren.Edge.Ingestion.Tests\Runtime\` — unit and near-unit only; the full tier-2 integration suite is Task 7.1's:

- `StateStoreTests` — round-trip a document row; 6201 / 6202 / 6203 / 6204 each raised from a deliberately corrupted meta or blob; `_run` trimmed to 20; a `Failed` row surviving a re-run on an unchanged hash; a recipe bump re-attempting it.
- `ChunkDiffTests` — added / removed / unchanged / repaired over a hand-built stored set, including a document that repeats a paragraph three times (duplicate ordinals 0, 1, 2) and an insertion at the head that repairs the tail.
- `WriteProtocolOrderTests` — a spy collection and a spy generator record the call order and assert a1 before a2, every addition before every removal, repairs after removals, and the state row last.
- `TransactionInvariantTests` — a spy `IEdgeDatabase` that **fails if a nested open occurs**: no SP3 code path calls an SP2 collection method from inside an SP3 `ExecuteInTransactionAsync` callback.
- `StartupTaskTests` — a spy `IEdgeDatabase` asserts **zero** `OpenConnectionAsync` calls during startup; a container with `AddIngestion` but no tokenizer fails at order 400 with 6001 rather than at builder time or on the first document; a deliberately bad budget makes `AddIngestion` **return** and the order-400 task throw 6003, which is §13.3's registration-versus-startup split asserted directly.
- `CollectionShapeTests` — `CollectionShapeProjection` escapes braces; SP3's check-side statement list is byte-identical to what `AddVectorCollectionMigration` registers for the same definition and options (the guard on the projection); `AddVectorStore(o => o.ChunkSize = 512)` with `ConfigureCollection` unset fails at order 400 with 6011 naming the differing `CREATE VIRTUAL TABLE`, and setting `ConfigureCollection = o => o.ChunkSize = 512` then starts clean; the same parameterised over `VectorTableNameFormat` and `FullTextRemoveDiacritics`, the last of which goes through `IngestionOptions` rather than `ConfigureCollection` and is the reason plan adjustment 4 exists.
- `LifecycleOrderTests` — the canonical `AddSqlite → AddVectorStore → AddIngestion` sequence yields `[Ingestion, VectorData, Sqlite]`; a reversed registration still terminates cleanly; the grace budget is honoured with `FakeTimeProvider`; a second `AddIngestion` inserts no second observer.
- `ThrottleTableTests` — §10.2's decision table as `[Theory]` rows against a fake `IEdgeResourceMonitor`, including that `EdgeThermalState.Unknown` and a null memory reading mean **no signal**, which is `Proceed` — never "fine".
- `SuspendReasonTests` — every budget and throttle path produces its documented string, and the three `IngestionBudget` presets meter what plan adjustment 20 pins: a `Quick` run over a corpus whose processing exceeds 20 s of `FakeTimeProvider` time suspends with `budget:duration`, and a `Quick` run over a 500-document corpus that finishes inside 20 s does **not** suspend — proving `MaxDocuments` really is null on the preset rather than an unwritten assumption.
- `PipelineCollectionNameTests` — **6002** (adjustment 21): an unknown `collectionName` on each of `RunAsync`, `PruneAsync`, `RemoveSourceAsync`, `RemoveDocumentAsync` and `GetStatusAsync` throws `IngestionCollectionNotConfigured` with the requested and the configured names in the message and **before** any `IEdgeDatabase` call (asserted with the spy database); `null` with one configured collection resolves; `null` with two is also 6002.
- `RecipeDriftTests` — **6009**, both branches. With `StrictRecipe = true`, a stored `recipe_hash` differing from the run's throws `IngestionRecipeChanged` on the first drifted document, the message contains **both** 32-hex hashes, and **no** embed call and **no** write happened (spy generator and spy collection both at zero). With `StrictRecipe = false`, the same input re-indexes every drifted document and logs event **915 exactly once**, not once per document, with both hashes.
- `DuplicateDocumentIdTests` — **6054**: a source yielding `"a.md"`, `"b.md"`, `"a.md"` produces `DocumentsSeen == 3`, `DocumentsIndexed == 2`, `DocumentsFailed == 1`; the third result carries `IngestionDuplicateDocumentId`; the extractor was invoked **twice**, not three times; and the first `a.md`'s chunks and state row are untouched. Plus the ordinal-comparison case: `"A.md"` and `"a.md"` in one run are **two** documents, not a duplicate.
- `ExtractionFaultTests` — **6102**: an extractor that throws a bare `InvalidOperationException` yields a document recorded `Failed` with `ExtractionFailed`, `ExtractorId` set, the original as `InnerException`, event 907 logged, and the run `Completed`. Parameterised alongside four extractors that throw 6103 / 6104 / 6106 / 6107, each of which must arrive **unchanged** rather than re-wrapped as 6102 — the split is what makes the code worth having.
- `NoDocumentTextInLogsTests` — as described in Step 7.
- `SourceOpenCountTests` — **re-filed here from Task 3.1 Step 7**: `OpenAsync` is called exactly **twice** per document by a spy source, the hash pass plus the extraction pass (§9.4). Wave 3 could only prove the extraction half, because the hash pass does not exist until this task.

- [ ] **Step 9: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp3\ingestion\tests\Qavren.Edge.Ingestion.Tests\Qavren.Edge.Ingestion.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp3\w5\t51
```

Expected: `Passed!`, exit 0.

---

### Task 5.2: Wave 5 close *(integrator)*

**Local-verifiable:** yes. **Files:** none of its own.

- [ ] **Step 1:** Delete `D:\Local\Temp\qedge-sp3\w5` and everything under it.
- [ ] **Step 2:** Re-run Task 5.1 Step 9 without `ArtifactsPath`, then `dotnet build` the four satellite csprojs (they are still empty, and this is the check that the frozen core they are about to consume actually compiles from a clean tree).
- [ ] **Step 3:** Commit as `feat(sp3): the state schema, the incremental diff, the write protocol, the runner and AddIngestion`.
- [ ] **Step 4: Verify** — Step 2 passed, `git status --short` clean. **The core is frozen.** Waves 6 and 7 read it and do not write it.

---

## WAVE 6 — The four satellites

### Task 6.1: `Qavren.Edge.Ingestion.Pdf`, `Qavren.Edge.Ingestion.OpenXml`, and `Extractors.Tests`

**Local-verifiable:** yes.

**Files:**
- Create: `ingestion\src\Qavren.Edge.Ingestion.Pdf\PdfReadingOrderMode.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion.Pdf\PdfExtractorOptions.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion.Pdf\PdfTextExtractor.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion.Pdf\PdfIngestionBuilderExtensions.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion.Pdf\README.md`
- Create: `ingestion\src\Qavren.Edge.Ingestion.OpenXml\DocxExtractorOptions.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion.OpenXml\DocxTextExtractor.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion.OpenXml\OpenXmlIngestionBuilderExtensions.cs`
- Create: `ingestion\tests\Qavren.Edge.Ingestion.Extractors.Tests\*`

**Approach.** Both extractors in one task, because they share one test project and one owner per test csproj per wave is the rule (plan adjustment 14). Everything they compile is frozen.

**Types this task owns** (adopted by reference, transcribed verbatim with their XML docs). The satellites' public surface is small and was previously discharged only by prose, which is how `PdfReadingOrderMode` came to be named by no task at all while §11 declared it and `PdfExtractorOptions.ReadingOrder` had it as its type:

| From | Namespace | Types |
|---|---|---|
| §11 | `Qavren.Edge.Ingestion.Pdf` | **`PdfReadingOrderMode`** (`{ ContentOrder, Layout }`), `PdfIngestionBuilderExtensions.AddPdfExtractor` |
| §7.4 | `Qavren.Edge.Ingestion.Pdf` | `PdfExtractorOptions`, `PdfTextExtractor` (`Id = "pdf"`, `Version = 1`) |
| §11 | `Qavren.Edge.Ingestion.OpenXml` | `OpenXmlIngestionBuilderExtensions.AddDocxExtractor` |
| §7.5 | `Qavren.Edge.Ingestion.OpenXml` | `DocxExtractorOptions`, `DocxTextExtractor` (`Id = "docx"`, `Version = 1`) |

Seven public types across two packages; nothing else in either is public. **`PdfReadingOrderMode` is `Qavren.Edge.Ingestion.Pdf`'s, not the core's** — it names a PdfPig reading-order strategy and the core must not carry a type only one satellite can mean. §7.4 writes the property's default as `PdfReadingOrderMode.ContentOrder`; an earlier draft of this task wrote it `ReadingOrderMode.ContentOrder`, which is a different name and would not compile. Use the spec's spelling everywhere, including in `PdfExtractorOptions`' own declaration.

**Defaults that change behaviour:**

- `PdfExtractorOptions`: **`ReadingOrder = PdfReadingOrderMode.ContentOrder`**, **`SkipMissingFonts = true`**, `UseActualText = true`, `UseLenientParsing = true`, `JoinHyphenatedLineBreaks = true`, `PageBudget = 20 s`, `MaxStackDepth = 50`, `Passwords` empty.
- `DocxExtractorOptions`: **`ExcludeHeadersAndFooters = true`**, `IncludeTextBoxes = true`, `IncludeNotes = true`, `IncludeTables = true`, `StreamingThresholdBytes = 8 MiB`.

- [ ] **Step 1: `PdfTextExtractor`**

`Id = "pdf"`, `Version = 1`. Four rules are load-bearing and each gets a test:

- **Always `PdfDocument.Open(Stream, ParsingOptions)`, never `Open(string)`.** The path overload calls `File.ReadAllBytes`, so a 50 MB scanned PDF becomes a 50 MB LOH allocation on a device that jetsams. The `Stream` overload wraps a seekable stream in `StreamInputBytes` and does not buffer. **A test asserts the call shape** — the simplest honest version is a source grep of `ingestion/src/Qavren.Edge.Ingestion.Pdf/**` for `Open(` followed by a string argument, plus a behavioural test that passes a stream whose `Read` is counted and asserts the whole file is not read up front.
- **`GetPages()` is lazy and stays lazy.** One page's `Letter`s are live at a time; the page's blocks are appended to the text buffer and the page is released. No `ToList()` over `GetPages()` anywhere.
- **`SkipMissingFonts = true` by default.** On a font-name miss PdfPig otherwise `File.ReadAllBytes` + parses the name table of every file in the system font directory — a multi-second stall on Android the first time such a PDF appears. §17 item 6 measures what the flag costs in extraction quality; until it is measured the default stands and the README says so.
- **`PageBudget` is a between-pages watchdog and is honestly not a timeout.** PdfPig's per-page surface is fully synchronous and accepts no `CancellationToken`: `doc.GetPages()`, `page.Letters`, `ContentOrderTextExtractor.GetText(page)` and `NearestNeighbourWordExtractor` all run to completion or not at all. Abandoning a wedged page would need a worker thread, and abandoning a thread mid-parse leaves the `PdfDocument` and its `StreamInputBytes` in an undefined state with a half-consumed stream — so SP3 does not do it and does not pretend to. What it does: stamp a stopwatch at the start of each page and check the **cumulative** elapsed time at each page boundary; when exceeded, stop there, **keep every page already parsed**, record the document `Failed` with `DocumentPageBudgetExceeded` (6107) naming the page number reached, and log event 924. Cancellation and the lifecycle stop flag are checked at the same boundary. The package README states the limitation rather than papering over it.

`PdfReadingOrderMode.ContentOrder` uses `ContentOrderTextExtractor.GetText` with `SeparateParagraphsWithDoubleNewline`, `ReplaceWhitespaceWithSpace` and `NegativeGapAsWhitespace` on. `PdfReadingOrderMode.Layout` opts into `NearestNeighbourWordExtractor` → `DocstrumBoundingBoxes` → `UnsupervisedReadingOrderDetector`; it is materially more expensive per page and its options carry a `MaxDegreeOfParallelism` nobody wants spinning up on a phone, so it is not the default. **`UglyToad.PdfPig.DocumentLayoutAnalysis.Export` is never referenced** — its Alto/PageXml exporters use `XmlSerializer` and carry `RequiresUnreferencedCode`, and they are the only trim hazard in the package.

**Scanned pages are an outcome, not an error.** `page.Letters.Count == 0 && page.NumberOfImages > 0` marks a page as having no text layer; a document with no text-layer page at all is recorded `IngestionDocumentStatus.NoTextLayer` (6105), event 906, **with its content hash stored** so it is not re-parsed every run. Silently indexing an empty document is worse than recording that we looked.

Encrypted → `DocumentEncrypted` (6103) after the `Passwords` list is tried; malformed → `DocumentMalformed` (6104), inner preserved. One bad PDF fails one document, never a corpus.

`AddPdfExtractor(configure)` is **idempotent**: a second call re-applies `configure` to the same options instance and registers no second extractor, so it does not trip `IngestionDuplicateExtractorId` (6004) — that code is for two **different** registrations sharing an `Id`. Order-independent relative to `AddIngestion`, because the registry is composed at resolve time.

`ingestion\src\Qavren.Edge.Ingestion.Pdf\README.md` states the Apache-2.0 term in its **first paragraph** and carries `PageBudget`'s limitation.

- [ ] **Step 2: `DocxTextExtractor`**

`Id = "docx"`, `Version = 1`. `OpenXmlValidator` is never called.

**Heading level resolves from the outline level, never from the style name.** In priority order: `paragraph.ParagraphProperties?.OutlineLevel?.Val` (`w:outlineLvl`, 0–9 where 9 means "no level"), then `ParagraphStyleId?.Val` resolved against the styles part to `Style.StyleParagraphProperties.OutlineLevel`. Regexing the style id for `Heading{n}` breaks on every localised template — "Titre 1", "Überschrift 1" — and a `pStyle` naming a style absent from `styles.xml` must **degrade to a paragraph, not throw**. The `localised-style` and `unknown-style` fixtures assert both.

**Above `StreamingThresholdBytes` the extractor uses `OpenXmlPartReader`, not the DOM.** A 50 MB DOCX materialised as `MainDocumentPart.Document.Body` is precisely the allocation spike that kills an iOS app; refusing the document instead would be worse, because a 20 MB Word file with embedded images is something a user actually owns. Event 913 records which mode ran, so the choice is observable — and a test runs the **same** fixture through both paths and asserts identical `ExtractedDocument.Text` and identical block spans, which is the only thing that keeps the two implementations honest.

Runs are merged: one sentence split across five `w:r` with rsid noise is **one** paragraph block. Tables emit `TableRow` blocks, pipe-joined, header row first. Page headers and footers are excluded by default — they repeat on every page and poison embeddings. `w:txbxContent` is reached when `IncludeTextBoxes`.

`AddDocxExtractor(configure)` is idempotent and order-independent on the same terms as `AddPdfExtractor`.

- [ ] **Step 3: `DeterministicOpc`**

The zip helper, in `Extractors.Tests`, assembling a committed part-XML tree into an OPC package **byte-identically across runs**. Three rules, and the third is the one that throws if you get it wrong:

- entries added in a declared, fixed order;
- `CreateEntry(name, CompressionLevel.Optimal)`;
- `LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)` set **between `CreateEntry` and `Open()`** — setting it afterwards throws in `Create` mode.

A test builds one fixture twice and asserts the two byte arrays are equal, and a second asserts the package opens cleanly in `DocumentFormat.OpenXml` 3.5.1.

- [ ] **Step 4: `flate-content.pdf`, assembled at test time**

The one Flate path that cannot be spelled in ASCII. The test deflates the committed `flate-content.stream` at a fixed `CompressionLevel` and splices it into a `/Filter /FlateDecode` content stream — `DeflateStream` is deterministic where PdfPig's trailer `/ID` is not. A test asserts two assemblies produce identical bytes and that the result extracts the expected text.

- [ ] **Step 5: the tests**

Over the committed PDF corpus: `minimal-text` (text and offsets), `two-pages` (page numbers reach `DocumentBlock.PageNumber` and `ChunkDraft.Page`), `two-columns` (reading order under **both** `PdfReadingOrderMode` values), `hyphen-linebreak` (rejoined under the default, not rejoined with the flag off), `no-text-layer` (**`NoTextLayer`, not an exception**, and the content hash is stored), **`xref-stream`** (one page, the expected text, and the page dictionary came out of the object stream — it is committed, it was opened by the real PdfPig in Task 1.3 Step 3b before it was committed, and there is no "if"), `broken-startxref` (6104 with a message naming the file, and the run continues — **unless** Task 1.3 Step 3b's `NOTE` line reported `RECOVERED`, in which case that fixture asserts a successful recovery instead and 6104's raise site is a truncated-object PDF built at test time), `flate-content` (Step 4). `DocumentEncrypted` (6103) over a password-protected fixture built at test time, and `DocumentPageBudgetExceeded` (6107) with `PageBudget` set to zero over `two-pages`, so both codes have a raise site the **Error-code ownership** table can point at.

**For DOCX, one test per tree, each asserting the expected output Task 1.3 Step 5 states for that tree — not merely "it did not throw".** Task 1.3 gives every tree its block sequence, kind and exact text; those are the assertions, and they are what stop a tree from being eleven XML files nobody reads:

| Tree | The assertion that is the point |
|---|---|
| `headings` | six blocks; levels 1, 2, 3 from `w:outlineLvl` with `styles.xml` present but unreferenced |
| `run-split` | **one** block, text equal to the whole 60-character sentence — equality, never `Contains` |
| `table` | three `TableRow` blocks joined `" \| "`, header first, then the trailing paragraph as its own block |
| `numbered-list` | two `ListItem` then one `Paragraph`; no `"1. "` prefix rendered into the text |
| `footnotes` | `IncludeNotes = true` → note appended as a `Footer` block after the body; `false` → one block |
| `header-footer` | default → one block and **`DoesNotContain("RUNNING HEADER")`** and `DoesNotContain("RUNNING FOOTER")`; flipped → three |
| `hyperlink` | text equal to `"See the linked phrase for details."` and **`DoesNotContain("example.invalid")`** |
| `textbox` | `IncludeTextBoxes = true` → two blocks; `false` → one |
| `empty-body` | `Text == ""`, zero blocks, `HasTextLayer == true`, **no exception**, recorded `Indexed` not `Failed` |
| `unknown-style` | two blocks, **both `Paragraph`**, `HeadingLevel` null on both, no throw |
| `localised-style` | four blocks with levels 1 and 2 resolved through the styles part, and an assertion that the extractor source contains no `Heading` regex — the fixture and the grep together |

Plus the DOM-versus-SAX equivalence test (the **same** tree through both paths, asserting identical `ExtractedDocument.Text` and identical block spans) and the `DeterministicOpc` determinism test.

Plus the registration tests: `AddPdfExtractor` twice registers **one** extractor and re-applies `configure`; both satellites are order-independent relative to `AddIngestion`; a consumer who constructs `new PdfTextExtractor(options)` by hand and passes it to `AddDocumentExtractor` lands in the same registry and bypasses nothing.

- [ ] **Step 6: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp3\ingestion\tests\Qavren.Edge.Ingestion.Extractors.Tests\Qavren.Edge.Ingestion.Extractors.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp3\w6\t61
```

Expected: `Passed!`, exit 0.

---

### Task 6.2: `Qavren.Edge.Ingestion.Onnx` — the tokenizer bridge, the throttle, `AddOnnxIngestion`

**Local-verifiable:** yes.

**Files:**
- Create: `ingestion\src\Qavren.Edge.Ingestion.Onnx\EdgeChunkTokenizer.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion.Onnx\ResourceMonitorThrottle.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion.Onnx\ResourceMonitorThrottleOptions.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion.Onnx\OnnxIngestionBuilderExtensions.cs`
- Create: `ingestion\tests\Qavren.Edge.Ingestion.Onnx.Tests\*`

**Approach.** ~200 lines of product code and one table test that is worth more than all of it.

**Types this task owns** — §11 says this satellite adds "exactly three public types and one extension", and these are they:

| From | Types |
|---|---|
| §11 | `EdgeChunkTokenizer`, `ResourceMonitorThrottle`, `ResourceMonitorThrottleOptions`, `OnnxIngestionBuilderExtensions.AddOnnxIngestion` |

Nothing else in `Qavren.Edge.Ingestion.Onnx` is public.

- [ ] **Step 1: `EdgeChunkTokenizer`** (plan adjustment 1)

`IChunkTokenizer` over SP2's `IEdgeTokenizer`. `CountTokens` forwards to `IEdgeTokenizer.CountTokens`, which SP2 forwards verbatim to `Tokenizer.CountTokens` at its defaults — verified in source — so no normalised string can leak through it. `SpecialTokenOverhead` is **2**, the measured value, with the measurement cited in the XML doc. `MaxSequenceLength` forwards. `IndexByTokenCount` calls `TokenIndexSearch.Find` with `CountTokens` as the delegate — **it does not forward to `IEdgeTokenizer.IndexByTokenCount`**, whose implementation discards the normalised string SP2 does not need and SP3 cannot use.

`TokenIndexSearch` is `internal` to `Qavren.Edge.Ingestion`, and **this task reaches it through the `[assembly: InternalsVisibleTo("Qavren.Edge.Ingestion.Onnx")]` that Task 2.1 Step 2 already wrote** (plan adjustment 24). There is no decision to take here and **no file under `ingestion\src\Qavren.Edge.Ingestion` is edited by this task** — that assembly was frozen by the wave-5 close, Tasks 6.1 and 6.3 are compiling it right now, and a write to it from this task is exactly the mid-edit-compile hazard the Wave map's one-writer rule exists to prevent. If the grant is missing, **stop and fix wave 2** rather than adding it here; a public `EdgeTokenCounter.FindIndexByTokenCount` helper is also not the answer, because a public member that exists only so one first-party satellite can call it is public surface with no consumer and §11's type list does not carry it.

- [ ] **Step 2: `ResourceMonitorThrottle`**

Over SP2's `IEdgeResourceMonitor.Read()`. §10.2's table, **first match wins**, then clamp to `MinBatchSize`:

| Signal | Decision | Reason string |
|---|---|---|
| `LastPressure == Critical` | pause | `memory:critical` |
| `AvailableMemoryBytes < 48 MiB` | pause | `memory:floor` |
| `Thermal == Critical` | pause | `thermal:critical` |
| `Thermal == Serious` | batch ÷ 2, delay 250 ms | `thermal:serious` |
| `LastPressure == Moderate` | batch ÷ 2 | `memory:moderate` |
| `AvailableMemoryBytes < 96 MiB` | batch ÷ 2 | `memory:warn` |
| `IsLowPowerMode == true` | batch = min(batch, 4), delay 100 ms | `power:low` |
| otherwise | proceed at the configured batch | — |

**`EdgeThermalState.Unknown` and a null memory reading mean *no signal*, which is `Proceed` — never "fine".** That mirrors `EdgeResourceSnapshot`'s own documented contract, where every nullable member means UNKNOWN. `ResourceMonitorThrottleOptions.IgnoreLowPowerMode` and `IgnoreThermalState` exist for the app that disagrees about Low Power Mode. `Evaluate` must not block.

- [ ] **Step 3: `AddOnnxIngestion(embeddingsName = null, configure = null)`**

Resolves the generator's preset via `generator.GetService(typeof(EmbeddingPreset))` — the same route SP2's own store uses, verified to return the preset — and projects it:

```csharp
new ChunkModelProfile(
    preset.Id, preset.Dimensions, preset.MaxSequenceLength,
    preset.Pooling.ToString(), preset.DocumentPrefix, preset.QueryPrefix)
```

Then derives `ChunkOptions.MaxTokens` / `OverlapTokens` / `MinTokens` from that profile **unless the consumer already set them**, and registers `EdgeChunkTokenizer` and `ResourceMonitorThrottle`. Everything resolves lazily, so the call is idempotent and order-independent relative to `AddOnnxEmbeddings`.

**Two guards, because a default that silently disagrees with reality is worse than no default.** If the consumer set `IngestionOptions.Model` explicitly to something whose `Id`, `Dimensions`, `MaxSequenceLength`, `DocumentPrefix` or `QueryPrefix` differs from the resolved preset, throw `IngestionOptionsInvalid` (6005) naming both — never silently overwrite a value the consumer typed. The two prefixes are in that list for different reasons and both matter: `DocumentPrefix` is a **token reserve**, so a profile that omits one the generator will apply under-budgets every chunk by its length; `QueryPrefix` is a **recipe input**, so a profile that disagrees writes a recipe hash describing a retrieval convention the collection does not have.

- [ ] **Step 4: the drift table — §17 item 12** (plan adjustment 16)

The test this package exists for. For each of the **four** core profiles — `MiniLmL6V2Int8`, `MiniLmL6V2Fp32`, `BgeSmallEnV15`, `NomicEmbedTextV15Int8` — assert all six fields against the `EmbeddingPresets` member of the same name, `StringComparison.Ordinal`, **trailing spaces included**:

`Id`, `Dimensions`, `MaxSequenceLength`, `Pooling` (`preset.Pooling.ToString()`), `DocumentPrefix`, `QueryPrefix`.

Verified in SP2's source as committed: the ids are lower-case (`"all-minilm-l6-v2-int8"`, `"all-minilm-l6-v2-fp32"`, `"bge-small-en-v1.5"`, `"nomic-embed-text-v1.5-int8"`); nomic carries **both** prefixes, `"search_document: "` and `"search_query: "`; bge-small carries its query instruction, `"Represent this sentence for searching relevant passages: "`, and **no** document prefix; MiniLM carries neither. SP2's source marks two of these OWNER-CONFIRMATION-OWED — bge-small's query prefix string and nomic's 512 ceiling against the model card's 8192 — and if either moves, **this test fails loudly** and the core profiles and every affected recipe hash follow. That is the whole reason the duplication is tested rather than trusted.

Plus: `AddOnnxIngestion`'s projection round-trips every one of the six; the 6005 guard fires for each of the five compared fields; the derived triple on a `MiniLmL6V2Int8` generator is **222 / 32 / 27**.

- [ ] **Step 5: the remaining tests**

`ResourceMonitorThrottle` as a `[Theory]` over §10.2's eight rows against a fake `IEdgeResourceMonitor`, including the two `Unknown`-is-not-fine rows and the `MinBatchSize` clamp. `EdgeChunkTokenizer` against the real `bert-base-uncased` vocabulary when it is present and a `SkipUnless` when it is not: the offset contract on NFD, ZWJ, Turkish-I and mixed-case non-ASCII input, and equality with `MlChunkTokenizer` over the same vocabulary — the two implementations must return the **same** index for the same input, which is what plan adjustment 1 buys and what a reader would otherwise have to take on trust.

- [ ] **Step 6: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp3\ingestion\tests\Qavren.Edge.Ingestion.Onnx.Tests\Qavren.Edge.Ingestion.Onnx.Tests.csproj" -c Release -p:TargetFrameworks=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp3\w6\t62
```

Expected: `Passed!`, exit 0, with the drift table reporting twenty-four field comparisons.

---

### Task 6.3: `Qavren.Edge.Ingestion.DataIngestion` — the MEDI shim and the conformance test

**Local-verifiable:** yes.

**Files:**
- Create: `ingestion\src\Qavren.Edge.Ingestion.DataIngestion\EdgeDocumentConverter.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion.DataIngestion\EdgeChunkerMediAdapter.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion.DataIngestion\MediReaderAdapter.cs`
- Create: `ingestion\src\Qavren.Edge.Ingestion.DataIngestion\EdgeVectorStoreMediWriter.cs`
- Create: `ingestion\tests\Qavren.Edge.Ingestion.DataIngestion.Tests\*`

**Approach.** Four public types and one test that makes the ecosystem claim true rather than aspirational. This task restores a package graph the other two wave-6 tasks do not have, which is precisely why `ArtifactsPath` is load-bearing here.

**Types this task owns:**

| From | Types |
|---|---|
| §11 | `EdgeDocumentConverter`, `EdgeChunkerMediAdapter : IngestionChunker<string>`, `MediReaderAdapter : IDocumentExtractor`, `EdgeVectorStoreMediWriter : IngestionChunkWriter<string>` |

Four types, no more. With Tasks 2.1, 3.1, 4.1, 5.1, 6.1 and 6.2 that is every public type §§6–11 declare, each named by exactly one task — obligation 1, discharged.

- [ ] **Step 1: `EdgeDocumentConverter`, both directions**

`ToMedi` / `FromMedi` between `ExtractedDocument` and MEDI's document model, lossless in both directions **except** for spans and images, both documented on the type. `DocumentBlockKind` deliberately uses MEDI's vocabulary — `Paragraph`, `Heading`, `ListItem`, `TableRow`, `Code`, `Caption`, `Quote`, `Footer` — so the mapping is an enum switch with no fallback bucket. There is **no image kind**: suite decision 9 says images later, and a public payload nothing reads is surface without capability. `IngestionDocumentImage` converts to nothing and logs event **917 once per document**, not once per image.

- [ ] **Step 2: `EdgeChunkerMediAdapter : IngestionChunker<string>`**

Wraps SP3's `IChunker` behind MEDI's chunker contract, feeding a `ResolvedChunkOptions` and an `IChunkTokenizer` the caller supplies. MEDI's shipped defaults of 2000 / 500 are four to eight times what a 384-dim model can encode; the adapter never carries a constant and takes the resolved budget instead.

- [ ] **Step 3: `MediReaderAdapter : IDocumentExtractor`**

The other direction: any MEDI `IngestionDocumentReader` becomes an SP3 extractor, which is how a consumer gets HTML without SP3 shipping an HTML extractor (§18).

- [ ] **Step 4: `EdgeVectorStoreMediWriter : IngestionChunkWriter<string>`**

Writes MEDI chunks into an SP2 collection. **Its XML doc states plainly that content-hash incremental re-index is not available on that path** — MEDI's model has nowhere to put a document hash — so every write is a full rewrite of the document's chunks, and `IIngestionPipeline` is what a phone should use. That sentence is the point of the type existing at all: without it a consumer reasonably assumes the shim carries SP3's incremental behaviour, and discovers otherwise on a battery.

- [ ] **Step 5: the conformance test**

Build a **real** MEDI `IngestionPipeline<string>` over `EdgeChunkerMediAdapter` and `EdgeVectorStoreMediWriter` and assert it writes and searches. That is the only proof the ecosystem claim is true.

The reader is the trap (§14.4). MEDI's `MarkdownReader` ships in `Microsoft.Extensions.DataIngestion.Markdig`, which depends on `Markdig.Signed` — a second package id emitting the same `Markdig.dll` assembly name as the core's `Markdig` 1.3.2. Two ids producing one assembly name in one output directory is `MSB3277`, which `TreatWarningsAsErrors` turns into a build failure, and the alternative outcomes (a silent last-writer-wins copy, or a binding mismatch at runtime) are worse than the failure. So the test does **not** restore that package: `IngestionDocumentReader` is an abstract class with a single abstract member — `ReadAsync(Stream, string, string, CancellationToken)` — and the test supplies a **fifteen-line reader** over SP3's own `MarkdownExtractor`, feeding the pipeline through `EdgeDocumentConverter.ToMedi`. That exercises everything the claim needs — MEDI's pipeline type, MEDI's chunker contract, MEDI's writer contract, and the converter in both directions — while keeping `Markdig.Signed` out of every graph in this repository.

The consequence is documented rather than hidden, in the test project's README: **if a future test genuinely needs MEDI's own Markdig reader, it cannot live in a project that also references `Qavren.Edge.Ingestion`.** It would need a separate test project referencing only `…DataIngestion.Abstractions` plus the MEDI Markdig package, and that is a new project, not a package addition. No CI assertion is warranted for a package that is absent; the `MSB3277`-as-error setting is the guard and it already exists repo-wide.

Plus: the converter is lossless both ways except spans and images, asserted field by field; images drop with exactly one 917 per document.

- [ ] **Step 6: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp3\ingestion\tests\Qavren.Edge.Ingestion.DataIngestion.Tests\Qavren.Edge.Ingestion.DataIngestion.Tests.csproj" -c Release -p:TargetFrameworks=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp3\w6\t63
```

Expected: `Passed!`, exit 0, and **no `MSB3277` in the build output** — assert that explicitly, because `TreatWarningsAsErrors` would already have failed the build and the point of looking is to confirm the graph is what the plan says it is.

---

### Task 6.4: Wave 6 close *(integrator)*

**Local-verifiable:** yes. **Files:** none of its own.

- [ ] **Step 1:** Delete `D:\Local\Temp\qedge-sp3\w6` and everything under it.
- [ ] **Step 2:** Re-run Tasks 6.1, 6.2 and 6.3's verifies sequentially, without `ArtifactsPath`. A failure here that did not appear in the parallel phase is real — the usual cause is a project that only restored because a sibling had already written its assets file, and Task 6.3's MEDI graph is the likeliest source.
- [ ] **Step 3:** `dotnet restore QavrenEdge.slnx`, then re-run Task 1.1 Step 11's PdfPig assertion in the real tree.
- [ ] **Step 4:** Commit as `feat(sp3): the PDF, DOCX, ONNX and MEDI satellites`.
- [ ] **Step 5: Verify** — Steps 2 and 3 passed, `git status --short` clean.

---

## WAVE 7 — Tier-2 integration, trim smoke, device-lane assertions

### Task 7.1: The tier-2 integration suite, and SP3's device-only assertions

**Local-verifiable:** yes (the device-only assertions compile here and skip through `[DeviceFact]`).

**Files:**
- Edit: `ingestion\tests\Qavren.Edge.Ingestion.Tests\Qavren.Edge.Ingestion.Tests.csproj` *(adds the `.Pdf`, `.OpenXml` and `.Onnx` references and, with the ONNX reference, SP2's raised platform floors)*
- Create: `ingestion\tests\Qavren.Edge.Ingestion.Tests\Integration\*`
- Create: `ingestion\tests\Qavren.Edge.Ingestion.Tests\Platforms\*`
- Create: `ingestion\tests\Qavren.Edge.Ingestion.Tests\Tier3\*`
- **Edit: `foundation\tests\Qavren.Edge.Sqlite.Cipher.Tests\Qavren.Edge.Sqlite.Cipher.Tests.csproj`** *(adds one `ProjectReference` to `Qavren.Edge.Ingestion` — see Step 8)*
- Create: `foundation\tests\Qavren.Edge.Sqlite.Cipher.Tests\IngestionCipherLifecycleTests.cs`

**This task owns two test projects, in two different areas, and the Wave map says so.** `foundation/tests/Qavren.Edge.Sqlite.Cipher.Tests` is 7.1's for the whole of wave 7 — 7.2 and 7.3 neither write nor compile it. The csproj edit above is listed because §14.3's SQLCipher test cannot compile without it and no other task adds it: an earlier draft created `IngestionCipherLifecycleTests.cs` in a project that had no reference to `Qavren.Edge.Ingestion`, which is a file that cannot build in a folder the wave map did not list.

**Approach.** The suite that proves the claims no golden file can make. The generator is a `RecordingEmbeddingGenerator`: a deterministic hash-seeded unit vector that **counts `GenerateAsync` calls and records every input string**. `ci.yml` already downloads the `native-*` artifacts on all three host OSes, so vec0 and FTS5 are free.

Adding the `.Onnx` reference brings a transitive ORT dependency into this test project for the first time, so the csproj gains SP2's raised floors — `android 24.0`, `ios 15.1`, `maccatalyst 15.1` — in the same edit. A device lane at the wrong floor is testing a configuration no consumer can ship.

**SP3 never uses the SP2 tiny ONNX fixture, and §14.3's `distance_metric = L2` rule therefore does not apply to any task in this plan** (plan adjustment 27). Every embedding in every SP3 test comes from `RecordingEmbeddingGenerator`, which is the point of the suite: what SP3 must prove is *how many* embed calls happen and *which* text reached them, not what a model returns. Nothing here links SP2's `TinyModels.g.cs` — Task 1.1 Step 6's csproj declares no `<Compile Link=…>` of it, this task's csproj edit adds only the `.Pdf`, `.OpenXml` and `.Onnx` references plus SP2's raised floors, and neither task's Files list carries it. The `.Onnx` reference exists for `EdgeChunkTokenizer`, `ResourceMonitorThrottle` and `AddOnnxIngestion`'s registration, none of which runs an ORT session.

The consequence to respect: **a later task that wants the tiny fixture must add the `<Compile Link=…>` in its own Files list and take §14.3's L2 rule with it** — the fixture embedding table is row *t* = `[t, t+0.5, t+0.25, t+0.75]`, every row is near-collinear, cosine over rows 1..15 spans 0.983417 to 0.999999, and any recall assertion on cosine there is float noise. Carrying the rule in this plan without carrying the fixture would be a paragraph that governs nothing, which is how a reader comes to believe a link exists that does not.

- [ ] **Step 1: the corpus round trip**

Ingest the committed corpus → assert the expected keys, the key format, row counts on all three tables, FTS5 rows and `vec0` rows.

- [ ] **Step 2: the incremental claims**

- **Re-run unchanged → `EmbedCalls == 0`, `ChunksAdded == 0`, `DocumentsSkipped == DocumentsSeen`.** This is the real assertion behind "content-hash incremental re-index", and no golden file can make it.
- Edit one paragraph → exactly one chunk added, one removed.
- **Insert a paragraph at the head of a 20-chunk document** → one embed, 19 repairs, and the `vec0` rows for the 19 read back **byte-identical** before and after, by direct SQL. Plus an assertion that the FTS5 sidecar content is still correct after the repair, which is the only guard on the `_au` trigger path.
- Bump the recipe → every document re-indexed, event 915 logged once with both hashes. Bump an **unselected** extractor's version → nothing re-indexed.
- A CsCheck `SampleModelBased` property against a naive re-embed-everything model over random single-character edits: an unchanged document re-embeds **zero** chunks; an edited one re-embeds only chunks whose hash moved.

- [ ] **Step 3: durability**

- **Crash injection**: a writer that throws after the second write window, then re-run → the document completes, no duplicate keys, and only the un-written chunks were embedded. This is the load-bearing claim of §9.5 and it gets a mechanism, not a description.
- **`SQLITE_BUSY` under a concurrent writer** → 6205, and the document stays at its old hash.
- **The transaction invariant**, again at integration scope: a spy `IEdgeDatabase` that fails if a nested open occurs during any SP3 write path.
- Cancel mid-run → `Cancelled`; resume produces the same final state as an uninterrupted run (byte-compare the collection).

- [ ] **Step 4: budgets, pruning and suspension**

- `MaxDocuments: 3` → `Suspended` with `SuspendReason == "budget:documents"`, **pruning skipped**, resume completes, and total embeds across the two runs equal a single unbudgeted run.
- Delete a source file → the sweep removes its chunks on a `Completed` run and **does not** on a `Suspended` one; `PruneAsync` then removes them.
- A simulated `MemoryPressure(Critical)` mid-run → `Suspended`, `SuspendReason == "memory:critical"`, resumable state.

- [ ] **Step 5: the prefix, the reserve and the generator's view**

With `Model = ChunkModelProfile.NomicEmbedTextV15Int8`: assert every string `RecordingEmbeddingGenerator` receives starts with the breadcrumb or the chunk text and **never** with `"search_document: "` — the double-prefix guard — and that the resolved `MaxTokens` is `512 − 2 − 32 − CountTokens("search_document: ")` rather than 478 — the guard on the encoder overrun a missing reserve would cause. `Model.QueryPrefix` is **never** applied to a stored chunk at all.

- [ ] **Step 6: the error separations and the startup checks**

- **6206 and 6207 are separable.** A generator that throws on the third call → 6206, one halved retry, then `Suspended`. A collection whose `UpsertAsync` throws → 6207, no retry. Both assert the **code**, not just the failure.
- Dimension mismatch → 6006 before any write.
- A source item with `SizeBytes = null` over `MaxDocumentBytes` → 6052 from the counted read, **no `content_hash` written**, and a subsequent run over a shrunk file ingests it.
- Non-seekable source streams at integration scope: a small one is buffered and ingests; one over `NonSeekableBufferLimitBytes` raises 6053 without being read to the end.
- **Collection-shaping divergence → 6011 at startup, before any write**, parameterised over `ChunkSize`, `VectorTableNameFormat` and `FullTextRemoveDiacritics`.
- The §4.3 snippet resolved from a **real** `ServiceCollection`, end to end, in **both** builder-call orders, so it cannot regress into `EmbeddingGeneratorMissing` — the same guard SP2 §16.2 has.

- [ ] **Step 7: `Sleeping`, under plan adjustment 2**

The observer chain is `[Ingestion, VectorData, Sqlite]`, and SP3's collection **is** in SP2's registry now, so the merge that §12 assigned to SP3 belongs to SP2's observer. Assert: with a run active, `RaiseSleepingAsync` returns inside `SleepGraceBudget` plus SP2's bounded merge; SP2's observer merged SP3's sidecar (its event 803 fired naming SP3's FTS table — which is the test that fails if a later change moves SP3 back off `AddVectorCollectionMigration`); with `FullTextIndexed = false` there is no FTS table and no merge; and the stop flag reached the runner at a committed boundary.

- [ ] **Step 8: SQLCipher**

One test in `Qavren.Edge.Sqlite.Cipher.Tests` — the full ingest lifecycle over a keyed connection. SP1 already keeps that project out of the device lanes, which is why the test lives there and not here. `ci.yml`'s existing `Cipher tests` step covers it with no new step.

**First, the csproj edit, because the test cannot compile without it.** `Qavren.Edge.Sqlite.Cipher.Tests` today references `Qavren.Edge.Sqlite` and `Qavren.Edge.Sqlite.Native` and nothing above them. Add **one** reference, in the `ItemGroup` that already carries them:

```xml
    <!-- Spec 14.3's one SQLCipher test: the full ingest lifecycle over a keyed connection.
         Qavren.Edge.Ingestion transitively brings Qavren.Edge.VectorData, which is what the
         collection half of the lifecycle needs. NOT Qavren.Edge.Ingestion.Onnx: the test uses
         the RecordingEmbeddingGenerator, so no ORT graph enters this project and its
         SupportedOSPlatformVersion floors are unchanged. -->
    <ProjectReference Include="..\..\..\ingestion\src\Qavren.Edge.Ingestion\Qavren.Edge.Ingestion.csproj" />
```

`Qavren.Edge.Sqlite.Cipher.Tests` is `net10.0` alone, and `Qavren.Edge.Ingestion` is `net10.0` alone, so this adds no TFM question. Deliberately **not** added: `.Onnx` (would drag ORT into a project SP1 keeps deliberately small), `.Pdf` and `.OpenXml` (the lifecycle under test is Markdown-and-text; the extractors are covered on the ordinary connection by Task 6.1).

Then the test itself: `AddSqlite` with a key, `AddIngestion`, a `RecordingEmbeddingGenerator`, ingest three committed fixtures, assert the chunk rows and the three state tables exist **and are readable only with the key**, re-run unchanged and assert `EmbedCalls == 0`, then `RemoveSourceAsync` and assert the sweep. The point is that nothing in SP3 reads a connection string — suite decision 10 — so the whole pipeline is indifferent to SQLCipher, and this is the test that says so rather than the README.

- [ ] **Step 9: the device-only assertions this project owns**

Under `Platforms\**` with `[DeviceFact]`-style guards so the host lane skips them with a printed reason:

- a 200-page PDF ingests inside a `Quick` budget with peak `GC.GetTotalAllocatedBytes` under an asserted ceiling;
- a simulated `MemoryPressure(Critical)` mid-run yields `Suspended` with `SuspendReason == "memory:critical"` and a resumable state;
- **`RaiseSleepingAsync` — the whole observer chain, not SP3's link — returns inside a measured wall-clock ceiling of 3 s** with a run active and one full-text collection, leaving margin inside iOS's ~5 s window. SP3's own observer is asserted under `SleepGraceBudget` separately, but the number that decides whether an iOS app is killed is `SP3 + SP2's merge + SP1's WAL checkpoint`, and §12's arithmetic is only credible if something measures the sum. **A failure is a signal to lower `SleepGraceBudget`, not to raise the ceiling** — write that in the test's message.

- [ ] **Step 10: the tier-3 lane and the §14.5 real-world lane (test half)**

All of this step's files live under `Tier3\`, which this task's Files list already declares — including the two real-world tests that are **not** tier-3 gated and run on every lane, because they are about the manifest that feeds tier 3 and belong beside it rather than in a fourth folder.

The tier-3 tests proper are guarded on `QAVREN_EDGE_TIER3` at **runtime** — after the class constructor, so a "skipped" test never loads a 23 MB model. Real int8 MiniLM at the HF revision SP2 already pins and caches by SHA-256; a ~20-document prose corpus; assert a known query's top-3 contains the expected chunk, cosine tolerance 1e-3. Retrieval *quality* is not obtainable from a 4-dim fixture model and must never gate a PR.

Plus the **real-world document lane**, whose input is `ingestion/tests/fixtures/realworld-corpus.json` (Task 1.3 Step 6b, fetched by Task 2.2 Step 4). Three tests, and the first two run **on this box and on every PR lane**, because they are about the manifest and not about the documents:

- `RealWorldCorpusManifestTests` — the manifest parses, every entry has all five fields, every `sha256` is 64 lower-case hex, every `producer` is one of `word` / `libreoffice` / `acrobat`, and every `id` is unique. This is a plain tier-1 test with no network and no skip: a malformed manifest fails immediately rather than at 03:00 in a nightly job.
- `RealWorldCorpusCoverageTests` — **skips with a printed reason when `documents` is empty**, and the reason is the sentence *"spec 14.5 is owed: ingestion/tests/fixtures/realworld-corpus.json has no entries"*, so the owed item is visible in the output of every local and CI run rather than only in this plan. When it is non-empty it asserts the three producers §14.5 names are each represented at least once — the requirement is a *lane*, and one Word file three times is not one.
- `RealWorldIngestionTests` — tier-3, guarded on `QAVREN_EDGE_TIER3`. For each manifest entry, read `$QAVREN_EDGE_DOCS_DIR/<id>`, skip that entry with a printed reason if the fetch step did not produce it, and otherwise assert only its `phrase` is contained in the extracted text and that the pipeline does not throw. Nothing is committed and no digest is re-checked here — the fetch step already did that, and re-hashing in the test would only duplicate it.

- [ ] **Step 11: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp3\ingestion\tests\Qavren.Edge.Ingestion.Tests\Qavren.Edge.Ingestion.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp3\w7\t71
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp3\foundation\tests\Qavren.Edge.Sqlite.Cipher.Tests\Qavren.Edge.Sqlite.Cipher.Tests.csproj" -c Release -p:TargetFrameworks=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp3\w7\t71
```

Expected: `Passed!` from both, exit 0, with the device-only and tier-3 classes reporting as skipped with printed reasons.

---

### Task 7.2: `trim-smoke` gains `AddIngestion` and a Markdown one-document run

**Local-verifiable:** yes (`win-x64`; the `linux-x64` leg is CI's).

**Files:**
- Edit: `embeddings\tools\Qavren.Edge.TrimSmoke\Program.cs`
- **Edit: `ingestion\README.md`** *(its `## Trim warnings` placeholder only — created empty by Task 1.2 Step 7; see **Cross-wave file ownership**. Replace the HTML comment and nothing else in the file.)*

**Approach.** §17 requires the trim-smoke console to exercise `AddIngestion` plus a one-document run over a committed **Markdown** string through `EdgeDynamicVectorStoreCollection`. Markdown rather than plain text on purpose: it puts **Markdig** — the only dependency in the *core* that declares neither `IsTrimmable` nor `IsAotCompatible` — under the trimmer on every PR, rather than exercising a path with no third-party parser in it. That is also the real check that nothing in SP3 carries a `[RequiresDynamicCode]` annotation, and it is otherwise free because SP3 has no reflection path at all.

- [ ] **Step 1:** Add `AddIngestion(migrationVersion: <the next free version in the console's database>)` to the existing builder chain, with a `UseChunkTokenizer` that builds an `MlChunkTokenizer` from a **tiny embedded vocabulary** rather than provisioning a real one — the console must download nothing.
- [ ] **Step 2:** Ingest one document through `IngestionSource.Single` over an in-memory Markdown string with a heading, a fence and a table, then read one chunk back through `EdgeDynamicVectorStoreCollection` and print its breadcrumb and offsets. A non-zero exit on any exception.
- [ ] **Step 3:** When `QedgeTrimSatellites` was set at publish time, additionally register `AddPdfExtractor()` and `AddDocxExtractor()` and ingest one real PDF and one real DOCX.

**Where those bytes come from, named rather than implied.** Task 1.1 Step 9 embedded four fixture resources in this console's csproj under the same `QedgeTrimSatellites` condition as the two `ProjectReference`s, so this step reads them with `Assembly.GetManifestResourceStream` and **this task edits no csproj** — `Qavren.Edge.TrimSmoke.csproj` has exactly one writer in this plan and it is six waves back. There is no other supply route: the console's three `ProjectReference`s are the core and the two extractor satellites, it does not reference `Qavren.Edge.Ingestion.Extractors.Tests` and **must never gain a reference to a test project**, so `DeterministicOpc` is out of reach by design. An earlier draft of this step said "one `DeterministicOpc`-built DOCX", which named a type in a project this console cannot see.

- `trimsmoke/minimal-text.pdf` is a complete PDF; hand its bytes straight to `IngestionSource.Single`.
- The DOCX is **not** a zip — no `.docx` is committed anywhere in this repo (Task 1.3 Step 5) — so the console assembles one from the three embedded `run-split` parts. Fifteen lines, console-local, no determinism requirement at all (nothing here compares bytes; that assertion is Task 6.1's, in the test project that owns it):

```csharp
static byte[] BuildDocx()
{
    var parts = new[] { "[Content_Types].xml", "_rels/.rels", "word/document.xml" };
    var asm = typeof(Program).Assembly;
    using var ms = new MemoryStream();
    using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
    {
        foreach (var part in parts)
        {
            using var src = asm.GetManifestResourceStream("trimsmoke/docx/" + part)
                ?? throw new InvalidOperationException("missing embedded part " + part);
            var entry = zip.CreateEntry(part, CompressionLevel.Optimal);
            using var dst = entry.Open();
            src.CopyTo(dst);
        }
    }
    return ms.ToArray();
}
```

Both are guarded by a runtime check on a generated constant (or a `#if`), so the core-only publish neither references, links nor embeds any of it. `Assembly.GetManifestResourceStream` is a string-keyed lookup on the assembly's own resources, not reflection over types, so it raises no `IL2xxx` — and if it somehow does, that warning is a finding this task exists to record, not a reason to change the mechanism.
- [ ] **Step 4: Verify — §17 item 4, both runs, recorded separately**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp3"
dotnet publish "$root\embeddings\tools\Qavren.Edge.TrimSmoke\Qavren.Edge.TrimSmoke.csproj" -c Release -r win-x64 --self-contained true -o "$root\artifacts\trim-core" -p:PublishTrimmed=true -p:TargetFrameworks=net10.0 -p:TargetFramework=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp3\w7\t72 2>&1 | Tee-Object "$root\artifacts\trim-core.log"
& "$root\artifacts\trim-core\Qavren.Edge.TrimSmoke.exe"
if ($LASTEXITCODE -ne 0) { throw "core trim smoke failed" }
dotnet publish "$root\embeddings\tools\Qavren.Edge.TrimSmoke\Qavren.Edge.TrimSmoke.csproj" -c Release -r win-x64 --self-contained true -o "$root\artifacts\trim-satellites" -p:PublishTrimmed=true -p:QedgeTrimSatellites=true -p:TargetFrameworks=net10.0 -p:TargetFramework=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp3\w7\t72 2>&1 | Tee-Object "$root\artifacts\trim-satellites.log"
& "$root\artifacts\trim-satellites\Qavren.Edge.TrimSmoke.exe"
Select-String -Path "$root\artifacts\trim-core.log","$root\artifacts\trim-satellites.log" -Pattern 'IL2\d{3}|IL3\d{3}' | ForEach-Object { $_.Line }
```

Expected: both publishes succeed and both runs exit 0.

**Record the trim warnings from each run separately — in the task output and in `ingestion/README.md`'s `## Trim warnings` section**, which Task 1.2 Step 7 created empty for exactly this and which is **declared in this task's Files list** above. Replace the HTML comment there with two labelled blocks, `### Core only` and `### With the Pdf and OpenXml satellites`, each holding either the `IL2xxx`/`IL3xxx` lines the `Select-String` above printed or the sentence *"this publish was warning-free"*. Change nothing else in that file — Task 1.2 owns the rest of it, four waves back, and a task that rewrites a file it was handed one section of is the hazard the **Cross-wave file ownership** rule exists to stop.

§17 item 4's assumption is that (b) is clean because SP3 never touches `DocumentLayoutAnalysis.Export` or `OpenXmlValidator`, and (a) is clean because Markdig's only reflection is `Markdown.Version`'s `GetCustomAttribute` and its `Configure(string)` switch, neither of which SP3 calls. **(a) is the one to watch.** If warnings appear they are documented in the README, never suppressed — and if the **core's** are the noisy ones, the plan reconsiders whether the Markdown extractor belongs in a satellite of its own, which is an Open risk, not a silent decision.

- [ ] **Step 5: Verify the README handover**

```powershell
$readme = Get-Content "C:\Users\steve\projects\qavren-edge-sp3\ingestion\README.md" -Raw
if ($readme -notmatch '(?m)^## Trim warnings\s*$') { throw "the Trim warnings heading is gone" }
if ($readme -match 'Filled by Task 7\.2') { throw "the placeholder comment is still there - Step 4 did not record the warnings" }
foreach ($h in '### Core only', '### With the Pdf and OpenXml satellites') {
  if ($readme -notmatch [regex]::Escape($h)) { throw "README is missing $h" }
}
Write-Host 'OK: both trim results recorded in ingestion/README.md'
```

Expected: the `OK:` line, exit 0.

---

### Task 7.3: Extractor device-lane assertions — the PdfPig asset trap

**Local-verifiable:** partly (compiles here; the assertions run on a device only).

**Files:**
- Create: `ingestion\tests\Qavren.Edge.Ingestion.Extractors.Tests\Platforms\*`

**Approach.** The device-only assertions that belong to the extractors, in the project that owns them (plan adjustment 14). Fixtures reach the device as `EmbeddedResource` — the globs are already in the csproj from Task 1.1 — and the DOCX zip is assembled at runtime from the embedded part XML, which is **§17 item 10**: `ZipArchive` in `Create` mode over a `MemoryStream` on all four device runtimes. If that does not work on some runtime, the fallback is a pre-zipped embedded blob and the determinism assertion stays host-only; record which branch was taken.

- [ ] **Step 1: the PdfPig asset trap — the assertion this task exists for**

A committed PDF referencing **Helvetica without embedding it** — `minimal-text.pdf` is exactly that — opens without `TypeInitializationException` on all four device TFMs. PdfPig ships no `net10.0` TFM and must resolve `lib/net9.0`; the `netstandard2.0` copy of `UglyToad.PdfPig.Fonts.dll` contains neither `AndroidSystemFontLister` nor `IOSSystemFontLister` and throws `NotSupportedException` out of a **static constructor** on exactly that input. The `ci.yml` assertion Task 2.2 added catches the resolution; this catches the consequence.

- [ ] **Step 2:** the DOCX runtime-zip assertion (§17 item 10) and one extraction per format from an embedded fixture, so the device lane proves the embedded-resource path end to end.

- [ ] **Step 3: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp3\ingestion\tests\Qavren.Edge.Ingestion.Extractors.Tests\Qavren.Edge.Ingestion.Extractors.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp3\w7\t73
dotnet build "C:\Users\steve\projects\qavren-edge-sp3\ingestion\tests\Qavren.Edge.Ingestion.Extractors.Tests\Qavren.Edge.Ingestion.Extractors.Tests.csproj" -c Release -f net10.0-android -p:ArtifactsPath=D:\Local\Temp\qedge-sp3\w7\t73
```

Expected: `Passed!` on the host lane with the device classes skipped, and the `net10.0-android` build succeeding — note **no `-p:TargetFrameworks`** on the device-TFM build (plan adjustment 10: a global `TargetFrameworks` would force every `net10.0`-only project in the closure to a TFM it does not have, `NETSDK1005`).

---

### Task 7.4: Wave 7 close *(integrator)*

**Local-verifiable:** yes. **Files:** none of its own.

- [ ] **Step 1:** Delete `D:\Local\Temp\qedge-sp3\w7` and everything under it.
- [ ] **Step 2:** Re-run Tasks 7.1, 7.2 and 7.3's verifies sequentially, without `ArtifactsPath`.
- [ ] **Step 3:** Commit as `feat(sp3): tier-2 integration, trim smoke and the device-lane assertions`, and record the two trim-warning sets in the commit body if they are non-empty.
- [ ] **Step 4: Verify** — Step 2 passed, `git status --short` clean.

---

## WAVE 8 — Device host, sample page, background samples *(strictly serial — one implementer at a time)*

These three are **not parallel-safe and are not run in parallel.** Two MAUI/platform builds over one shared reference closure, each staging the same native `.dll`s, is the worst pairing available in this plan — SP2's wave 7 established it and nothing here changes the arithmetic. 8.1 may also conditionally edit its own csproj depending on a build outcome, so a second builder over that closure would be reading a file mid-edit. Serial removes both problems and costs one build.

### Task 8.1: Device-host wiring — two `ProjectReference`s and the platform-floor question

**Local-verifiable:** yes for `net10.0-android` and `net10.0-windows10.0.19041.0`; the two Apple TFMs are CI-only.

**Files:**
- Edit: `foundation\tests\Qavren.Edge.DeviceTests\Qavren.Edge.DeviceTests.csproj`

**Approach.** §14.6: **no new device lane.** `Qavren.Edge.Ingestion.Tests` and `Qavren.Edge.Ingestion.Extractors.Tests` join the existing `ProjectReference` list, and the four existing lanes pick SP3 up for free. `SetTargetFramework` is not needed: both multi-target the four device TFMs, so MSBuild resolves the matching one. `Qavren.Edge.Ingestion.Onnx.Tests` and `Qavren.Edge.Ingestion.DataIngestion.Tests` are deliberately **not** added — both are `net10.0` alone and host-only.

- [ ] **Step 1: add the two references**

In the `ItemGroup` that already carries the three SP2 test libraries:

```xml
    <!-- Spec 14.6: no new device lane. These two multi-target the same four device TFMs as the
         SP2 test libraries above, so the four existing lanes run SP3's tier-1 assertions, the
         vec0/FTS5 half of tier 2, the PdfPig asset trap and the whole-observer-chain Sleeping
         measurement against the real Android .so and the real iOS xcframework.
         Qavren.Edge.Ingestion.Onnx.Tests and ...DataIngestion.Tests are deliberately absent:
         both are net10.0 alone and host-only. -->
    <ProjectReference Include="..\..\..\ingestion\tests\Qavren.Edge.Ingestion.Tests\Qavren.Edge.Ingestion.Tests.csproj" />
    <ProjectReference Include="..\..\..\ingestion\tests\Qavren.Edge.Ingestion.Extractors.Tests\Qavren.Edge.Ingestion.Extractors.Tests.csproj" />
```

- [ ] **Step 2: build for android and decide the floor**

`Qavren.Edge.Ingestion.Tests` now carries a transitive ORT reference through `.Onnx` (added in Task 7.1), and the device host already sits at `android 24.0` for exactly that reason. Build it and read the merged manifest:

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge-sp3\foundation\tests\Qavren.Edge.DeviceTests\Qavren.Edge.DeviceTests.csproj" -c Release -f net10.0-android
```

If the merged Android manifest demands a higher `minSdkVersion` than 24 — it should not, because SP3 adds no AAR — raise the floor **on this test host only**, never on an SP1 or SP2 package, and record the reason in a comment. If it builds clean, change nothing and say so.

- [ ] **Step 3: build for windows**

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge-sp3\foundation\tests\Qavren.Edge.DeviceTests\Qavren.Edge.DeviceTests.csproj" -c Release -f net10.0-windows10.0.19041.0
```

This is the leg that proves the `net10.0-windows10.0.19041.0` TFM on both new test libraries was worth declaring: without it the device host cannot reference them at all.

- [ ] **Step 4: Verify**

Both builds exit 0. **No `-p:TargetFrameworks` on either** (plan adjustment 10). The `net10.0-ios` and `net10.0-maccatalyst` legs are CI-only; do not attempt them here and do not mark the task blocked on them.

---

### Task 8.2: The sample app's Ingest page

**Local-verifiable:** yes (`net10.0-windows10.0.19041.0`; the mobile heads are CI-only).

**Files:**
- **Edit: `foundation\samples\Qavren.Edge.Sample\Qavren.Edge.Sample.csproj`** *(adds four `ProjectReference`s — see Step 1. Listed because `MauiProgram.cs` cannot compile without them, and no other task adds them)*
- Edit: `foundation\samples\Qavren.Edge.Sample\MauiProgram.cs`
- Edit: `foundation\samples\Qavren.Edge.Sample\AppShell.xaml`
- Create: `foundation\samples\Qavren.Edge.Sample\Pages\IngestPage.xaml`
- Create: `foundation\samples\Qavren.Edge.Sample\Pages\IngestPage.xaml.cs`

**Approach.** **One** page, not a second sample app. The existing Search page then finds what the Ingest page wrote, with no code change. The csproj edit is in the Files list for the same reason Task 7.1's is: this plan already corrected one draft that created a source file in a project with no reference to `Qavren.Edge.Ingestion`, which is a file that cannot build in a folder the wave map did not list. Wave 8 is strictly serial and this task is the sole writer of everything under `foundation/samples/Qavren.Edge.Sample/`, csproj included.

- [ ] **Step 1: wire it up**

In `MauiProgram.CreateMauiApp`, after `.AddVectorCollectionMigration<string, Note>(version: 2, NotesCollectionName)`:

```csharp
                // Version 3 AND 4: M001_CreateNotes is 1, the Note collection is 2, and
                // AddIngestion claims migrationVersion and migrationVersion + 1 (plan
                // adjustment 2) - N for the chunk collection, N+1 for the three state tables.
                .AddIngestion(migrationVersion: 3, o => o.CollectionName = "sample_chunks")
                .AddOnnxIngestion()
                .AddPdfExtractor()
                .AddDocxExtractor()
```

and `builder.Services.AddTransient<IngestPage>();`.

`Qavren.Edge.Sample.csproj` gains four references, in the `ItemGroup` that already carries `Qavren.Edge.VectorData` and `Qavren.Edge.Embeddings.Onnx` — literally, because "gains `ProjectReference`s to the core and the three satellites it uses" is a sentence and not a file:

```xml
    <!-- SP3's Ingest page (plan Task 8.2). The core plus the three satellites this sample calls:
         .Onnx for AddOnnxIngestion (the tokenizer and the throttle over SP2's resource monitor),
         .Pdf and .OpenXml for AddPdfExtractor/AddDocxExtractor. NOT .DataIngestion - the sample
         does not use MEDI and that package is prerelease-only. All four are net10.0 and this
         project is multi-TFM over the four platform heads; a platform TFM consuming a net10.0
         library is ordinary TFM compatibility, and the sample already resolves ORT's platform
         asset through Qavren.Edge.Embeddings.Onnx, so no SupportedOSPlatformVersion moves. -->
    <ProjectReference Include="..\..\..\ingestion\src\Qavren.Edge.Ingestion\Qavren.Edge.Ingestion.csproj" />
    <ProjectReference Include="..\..\..\ingestion\src\Qavren.Edge.Ingestion.Onnx\Qavren.Edge.Ingestion.Onnx.csproj" />
    <ProjectReference Include="..\..\..\ingestion\src\Qavren.Edge.Ingestion.Pdf\Qavren.Edge.Ingestion.Pdf.csproj" />
    <ProjectReference Include="..\..\..\ingestion\src\Qavren.Edge.Ingestion.OpenXml\Qavren.Edge.Ingestion.OpenXml.csproj" />
```

`AddVectorStore` in this sample sets only `IncludeRowCountsInDiagnostics`, so no shaping value diverges and the 6011 check passes with `ConfigureCollection` unset — which is worth a comment, because a reader who later adds `o.ChunkSize = 512` to `AddVectorStore` needs to know the other half moves with it.

`AppShell.xaml` gains a `ShellContent` for `IngestPage`, placed **before** Search so the two read in the order a user performs them.

- [ ] **Step 2: the page**

- A location picker, an `IProgress<IngestionProgress>` live view (stage, documents done, chunks written), a Budget picker (`Quick` / `Background` / `Unlimited`), **Stop** and **Prune** buttons, and the finished `IngestionRunResult` rendered in full **including `SuspendReason`**.
- Below it, `GetStatusAsync`'s counts, with `recipeStaleDocuments` and `staleDocuments` together labelled **"known outstanding"**, and a line of copy stating that a count of zero means nothing is *known* to be owed, **not** that the files on disk are unchanged (§12). A consumer who reads the first as the second stops re-indexing edited files, and the sample is where that sentence does the most good.

- [ ] **Step 3: the platform split, which is the point of the page**

On **Windows and Mac Catalyst**, `IngestionSource.Folder` over a picked path. On **Android and iOS**, `IngestionSource.Items` over `FilePicker.Default.PickMultipleAsync` results — because a SAF `content://` tree is not a path `Directory.EnumerateFiles` can walk, and an iOS picked URL is security-scoped. The iOS delegate wraps `StartAccessingSecurityScopedResource` / `StopAccessingSecurityScopedResource` around **each** open, since `OpenAsync` is called twice per document; a delegate that starts the scope once outside and never stops it leaks it. `DocumentId` is `f.FileName`, not the raw `content://` string, because every state row is keyed on `(collection, source_id, document_id)` and an id the OS re-issues between sessions turns every re-run into a full re-index. `SizeBytes` is `null`, which is §9.1's counted-read path and is why that path exists.

Shipping the desktop path on mobile would make the sample a demonstration of the one shape a mobile consumer cannot use.

- [ ] **Step 4: Verify**

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge-sp3\foundation\samples\Qavren.Edge.Sample\Qavren.Edge.Sample.csproj" -c Release -f net10.0-windows10.0.19041.0
dotnet build "C:\Users\steve\projects\qavren-edge-sp3\foundation\samples\Qavren.Edge.Sample\Qavren.Edge.Sample.csproj" -c Release -f net10.0-android
```

Expected: both exit 0, no `-p:TargetFrameworks`. Run the Windows head once by hand, ingest the committed corpus folder, and confirm the Search page finds a chunk — that is the end-to-end check no test performs.

---

### Task 8.3: The two background wirings under `ingestion/samples/`

**Local-verifiable:** android yes; ios is a probe that skips with a printed reason if this host refuses (SP2 Task 2.2 Step 8's idiom).

**Files:**
- Create: `ingestion\samples\Ingestion.Background.Android\IngestionWorker.cs`
- Create: `ingestion\samples\Ingestion.Background.Android\README.md`
- Create: `ingestion\samples\Ingestion.Background.iOS\IngestionBackgroundTasks.cs`
- Create: `ingestion\samples\Ingestion.Background.iOS\README.md`

**Approach.** These are documentation projects that compile (plan adjustment 8). They ship **no** library code and no page: SP1's decision 1 keeps scheduling with the consumer, and each of these wirings ships a permission or a manifest entry into whatever app contains it. Each README carries the manifest or plist keys as text for a reader to copy.

- [ ] **Step 1: Android**

A `Worker` promoting itself with `SetForegroundAsync(new ForegroundInfo(id, notification, (int)ForegroundService.TypeDataSync))`. **Note the spelling**: the enum members are `ForegroundService.TypeDataSync` (1) and `TypeMediaProcessing` (8192); `ForegroundService.DataSync` does not exist and will not compile. Manifest keys for the README: `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_DATA_SYNC` and, on API 33+, `POST_NOTIFICATIONS`.

Three failure modes the sample **handles**, in the order a consumer meets them:

1. **`ForegroundServiceStartNotAllowedException` on Android 12+ (API 31).** An app in the background may not start a foreground service, so `SetForegroundAsync` from a Worker that WorkManager scheduled while the app was backgrounded throws. Catch it, log, and let the work run **unpromoted** under the plain Worker's ~10-minute cap with `IngestionBudget.Background` — which is exactly the budget-suspends-on-a-committed-boundary shape SP3 exists to make safe — rather than failing the job. The README names an expedited request or a foreground-initiated start as the alternative.
2. **API 36 is the .NET 10 default, and jobs started from a foreground service now count against runtime job quotas.** So the sample does the ingestion **inside** the Worker rather than having the Worker schedule further jobs, and the README states why.
3. **The Android 15 (API 35) wall.** `dataSync` gets **six hours per rolling 24**, tracked separately from `mediaProcessing` and reset only when the app comes to the foreground; on expiry the system calls `Service.OnTimeout(int, ForegroundService)` and the process has a few seconds to `stopSelf()` before `RemoteServiceException`. Wire `OnTimeout` to `pipeline.RequestStop("fgs:timeout")` and return.

- [ ] **Step 2: iOS**

`BGTaskScheduler.Shared.Register` with a `BGProcessingTaskRequest` (`RequiresExternalPower`, `RequiresNetworkConnectivity`), an `ExpirationHandler` that calls `pipeline.RequestStop("bgtask:expiring")` and then `SetTaskCompleted(false)`, and the `BGTaskSchedulerPermittedIdentifiers` and `UIBackgroundModes` Info.plist keys in the README.

**`Submit` is checked, because "the OS said no" is the normal path.** `BGTaskScheduler.Submit(request, out NSError? error)` returns `bool`, and the sample branches on it rather than discarding it. A failure carries a `BGTaskSchedulerErrorCode`: `Unavailable` (1 — Background App Refresh is off for the app or device-wide, a user setting and not an error to retry), `TooManyPendingTaskRequests` (2 — cancel or coalesce before resubmitting), `NotPermitted` (3 — the identifier is missing from `BGTaskSchedulerPermittedIdentifiers`, i.e. a build mistake, and the sample says so in the log) and `ImmediateRunIneligible` (4). Each is surfaced with the action a developer should take, because a submit whose return value is ignored is how a background task silently never runs.

The iOS 26 `BGContinuedProcessingTask` variant, bridging `IProgress<IngestionProgress>` to `NSProgress`, is **`#if IOS`-guarded**: `BGContinuedProcessingTask`, `BGContinuedProcessingTaskRequest` and `BGContinuedProcessingTaskRequestResources` ship in `Microsoft.iOS.dll` only — not Mac Catalyst, not tvOS — while the rest of `BackgroundTasks` is in all three.

And on the other platform: Apple publishes **no** guaranteed duration for anything — read `UIApplication.BackgroundTimeRemaining` rather than assuming the community-measured ~30 s. Put that sentence in the README.

- [ ] **Step 3: Verify**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp3"
dotnet build "$root\ingestion\samples\Ingestion.Background.Android\Ingestion.Background.Android.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "android sample failed to build" }
dotnet build "$root\ingestion\samples\Ingestion.Background.iOS\Ingestion.Background.iOS.csproj" -c Release
if ($LASTEXITCODE -ne 0) { Write-Host 'SKIPPED: the net10.0-ios compile needs a Mac on this host; CI is the gate' }
```

Expected: the android build exits 0. The iOS build either exits 0 — restore demonstrably works on this box, and whether the *compile* of `BackgroundTasks` bindings succeeds without a Mac is the open half of the same question SP2 asked — or prints the skip line. **Print the reason; do not fail the task**, and record which happened. The `windows-2025` CI leg compiles both through `dotnet restore QavrenEdge.slnx` and `dotnet pack` regardless.

---

### Task 8.4: Wave 8 close *(integrator)*

**Local-verifiable:** yes. **Files:** none of its own.

- [ ] **Step 1:** Re-run 8.1's two builds, 8.2's two builds and 8.3's two builds sequentially. Wave 8 ran serially, so there is no scratch root to delete — and that is worth stating rather than silently skipping the step.
- [ ] **Step 2:** Commit as `feat(sp3): device-host wiring, the sample Ingest page and the two background wirings`.
- [ ] **Step 3: Verify** — every build exits 0 (the iOS sample may skip with its printed reason), `git status --short` clean.

---

## WAVE 9 — Closing gate

### Task 9.1: Full-solution verification *(integrator)*

**Local-verifiable:** yes, except the lanes named in **CI-only work**.

**Files:** none. This task is read-only and changes nothing.

- [ ] **Step 1: Restore, format and pack the whole solution**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp3"
dotnet restore "$root\QavrenEdge.slnx"
dotnet format "$root\QavrenEdge.slnx" --verify-no-changes --no-restore
```

Expected: both exit 0. `dotnet format` loads every project including the MAUI heads and both `ingestion/samples/` libraries, which is why this box — with every workload installed — is where it runs.

- [ ] **Step 2: Run every host-lane test project**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp3"
$multi = @(
  "foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj",
  "foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj",
  "embeddings\tests\Qavren.Edge.Onnx.Tests\Qavren.Edge.Onnx.Tests.csproj",
  "embeddings\tests\Qavren.Edge.Embeddings.Tests\Qavren.Edge.Embeddings.Tests.csproj",
  "embeddings\tests\Qavren.Edge.VectorData.Tests\Qavren.Edge.VectorData.Tests.csproj",
  "ingestion\tests\Qavren.Edge.Ingestion.Tests\Qavren.Edge.Ingestion.Tests.csproj",
  "ingestion\tests\Qavren.Edge.Ingestion.Extractors.Tests\Qavren.Edge.Ingestion.Extractors.Tests.csproj")
$single = @(
  "foundation\tests\Qavren.Edge.Provider.Tests\Qavren.Edge.Provider.Tests.csproj",
  "foundation\tests\Qavren.Edge.Sqlite.Cipher.Tests\Qavren.Edge.Sqlite.Cipher.Tests.csproj",
  "embeddings\tests\Qavren.Edge.VectorData.Conformance.Tests\Qavren.Edge.VectorData.Conformance.Tests.csproj",
  "ingestion\tests\Qavren.Edge.Ingestion.Onnx.Tests\Qavren.Edge.Ingestion.Onnx.Tests.csproj",
  "ingestion\tests\Qavren.Edge.Ingestion.DataIngestion.Tests\Qavren.Edge.Ingestion.DataIngestion.Tests.csproj")
foreach ($p in $multi) {
  dotnet run --project "$root\$p" -c Release -f net10.0 -p:TargetFrameworks=net10.0
  if ($LASTEXITCODE -ne 0) { throw "FAILED: $p" }
}
foreach ($p in $single) {
  dotnet run --project "$root\$p" -c Release -p:TargetFrameworks=net10.0
  if ($LASTEXITCODE -ne 0) { throw "FAILED: $p" }
}
Write-Host 'OK: all twelve host-lane suites passed'
```

Expected: `OK: all twelve host-lane suites passed`, exit 0. **Never `dotnet test`** — SP1 adjustment 36.

- [ ] **Step 3: Pack and confirm the five new packages**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp3"
dotnet pack "$root\QavrenEdge.slnx" -c Release -o "$root\artifacts\packages"
$expected = 'Qavren.Edge.Ingestion','Qavren.Edge.Ingestion.Pdf','Qavren.Edge.Ingestion.OpenXml',
            'Qavren.Edge.Ingestion.Onnx','Qavren.Edge.Ingestion.DataIngestion'
foreach ($id in $expected) {
  $pkg = Get-ChildItem "$root\artifacts\packages\$id.*.nupkg" -ErrorAction SilentlyContinue |
         Where-Object { $_.Name -notmatch '\.symbols\.' } | Select-Object -First 1
  if (-not $pkg) { throw "$id did not pack" }
  Write-Host "OK $($pkg.Name)"
}
```

Expected: five `OK` lines. Package validation is on from day one for the four stable packages; `Qavren.Edge.Ingestion.DataIngestion` has it off with the reason in its csproj (plan adjustment 11).

- [ ] **Step 4: Workflow contract and the PdfPig assertion**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp3"
$py = "C:\Python314\python.exe"      # Python 3.14.5, verified in Environment ground truth
if (-not (Test-Path $py)) { $py = (Get-Command python -ErrorAction Stop).Source }
& $py "$root\foundation\tools\ci-checks\assert-workflows.py" $root
if ($LASTEXITCODE -ne 0) { throw "assert-workflows.py failed" }
```

The interpreter is a **measured** fact, not an assumption: Environment ground truth records Python 3.14.5 at that path (also first on `PATH`) and `PyYAML` 6.0.3 importable, which are the two things this command and Task 2.2 Step 6 need. The `Get-Command` fallback exists so a path change is a one-line fix rather than a stalled gate.

Then Task 1.1 Step 11's PdfPig assertion, verbatim, in the real tree.

- [ ] **Step 5: Trace every spec requirement to a task**

Walk the spec and confirm each of the following is in the tree. Anything missing means the plan is **not** closed:

- Five packages — core plus four satellites — and **no satellite references another** (§4.1); grep the csprojs.
- `Qavren.Edge.Ingestion` has **no** reference to `Qavren.Edge.Embeddings.Onnx` and **names no type from it** (§2 decision 2, §4.1); grep the source for `Qavren.Edge.Embeddings.Onnx` and for `EmbeddingPreset`.
- Every shipped SP3 package is `net10.0` alone; the two device-hosted test projects take all five TFMs and the two host-only ones take `net10.0` (§2 decision 6).
- Exactly **one** SP1/SP2 library edit — `EdgeErrorCode` — plus the `DeviceTests` test-host edit §14.6 anticipates (§5.1).
- `EdgeIngestionStartupOrder.Validate = 400`, one startup task there, and **zero** `OpenConnectionAsync` calls during startup (§11.1 step 5).
- `EdgeIngestionEventIds` in 900–999, every call site through `LoggerMessage.Define`, and **no document text in any log line** (§12).
- Every `EdgeErrorCode` 6000–6299 is raised from somewhere, and every one has an anchor in `foundation/docs/errors.md` (§13). **Walk the plan's Error-code ownership table, not the enum** — it assigns all thirty-six a raise site, an owning task and a test, including the four the spec declares without a complete §13.3 row or an obvious owner (6002, 6009, 6054, 6102; plan adjustments 21 and 22). Mechanically:

  ```powershell
  $root = "C:\Users\steve\projects\qavren-edge-sp3"
  $src = Get-Content "$root\foundation\src\Qavren.Edge.Core\EdgeErrorCode.cs" -Raw
  $codes = [regex]::Matches($src, '(?m)^\s*(\w+)\s*=\s*(6\d{3})\s*,') |
           ForEach-Object { [pscustomobject]@{ Name = $_.Groups[1].Value; Code = $_.Groups[2].Value } }
  if ($codes.Count -ne 36) { throw "expected 36 SP3 codes, found $($codes.Count)" }
  $ing = (Get-ChildItem "$root\ingestion\src" -Recurse -Filter *.cs | Get-Content -Raw) -join "`n"
  foreach ($c in $codes) {
    if ($ing -notmatch [regex]::Escape("EdgeErrorCode.$($c.Name)")) {
      throw "$($c.Name) ($($c.Code)) is declared but never raised in ingestion/src"
    }
  }
  Write-Host "OK: all 36 SP3 error codes have a raise site"
  ```

  A code that fails this check is not fixed by deleting it from the enum — §13.1 is the spec's declaration and the enum is append-only. It is fixed by implementing the condition its **Error-code ownership** row names.
- The state schema is **three** tables, **all three `WITHOUT ROWID`** — including `<p>_run` and `<p>_meta`, whose keys are single columns — with golden SQL asserted (§6 **as widened by plan adjustment 19**). §6's own wording is "`WITHOUT ROWID` where the key is composite", which would pass a two-table implementation and fail the three-table one this plan actually ships; the authority is the golden-SQL test in Task 2.1 Step 9, and this bullet checks the plan, not the spec.
- The vector property is declared `ReadOnlyMemory<float>` and SP3 calls `GenerateAsync` itself, so `EmbedCalls` counts a1 exactly and 6206 is separable from 6207 (§9.5).
- `ChunkOptions.Resolve` produces **222 / 32 / 27** and **478 / 64 / 59**, and `SpecialTokenOverhead` is 2 with the measurement recorded (§8.1, §17 item 2).
- **Both `IChunkTokenizer` implementations return the same index for the same input**, and neither calls `GetIndexByTokenCount` (plan adjustment 1, ADR 0013).
- **Twenty golden files, by the names plan adjustment 12 and Task 4.1 Step 6 enumerate** — twelve `*.auto.json`, five `*.markdown-heading.{no-preamble,no-breadcrumb}.json`, three `*.token-window.json` — all generated against the digest-checked `bert-base-uncased` vocabulary at 222 / 32 / 27, the writer refusing to overwrite, and a fixture drift test that runs from embedded resources (§14.1). Check the count **and** the names, because twenty files with the wrong composition is the failure an earlier draft's self-contradicting arithmetic would have produced:

  ```powershell
  $g = @(Get-ChildItem "$root\ingestion\tests\fixtures\golden" -Filter *.json)
  if ($g.Count -ne 20) { throw "expected 20 goldens, found $($g.Count)" }
  $auto = @($g | Where-Object Name -like '*.auto.json').Count
  $mdh  = @($g | Where-Object Name -like '*.markdown-heading.*.json').Count
  $tw   = @($g | Where-Object Name -like '*.token-window.json').Count
  if ($auto -ne 12 -or $mdh -ne 5 -or $tw -ne 3) { throw "golden composition is $auto/$mdh/$tw, expected 12/5/3" }
  $fx = Get-Content "$root\ingestion\tests\fixtures\README.md" -Raw
  if ($fx -notmatch '07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3') {
    throw "the fixtures README does not record the vocabulary the goldens were generated against"
  }
  Write-Host "OK: 20 goldens, 12/5/3, vocabulary recorded"
  ```
- **The two cross-wave handovers are complete**: `ingestion/tests/fixtures/README.md` has a filled `## Golden generation` section (Task 1.3 → 4.1) and `ingestion/README.md` has a filled `## Trim warnings` section with both sub-headings (Task 1.2 → 7.2). A surviving `<!-- Filled by Task … -->` comment in either means a wave closed without doing what its Files list declared.
- The CsCheck properties with `seed:` and `iter: 500` pinned at the call site (§14.1).
- Re-run unchanged → `EmbedCalls == 0`; head-insertion → one embed and N−1 repairs with the `vec0` rows byte-identical; crash injection; `SQLITE_BUSY` → 6205; the transaction invariant; budget suspension with pruning skipped (§14.3).
- The MEDI conformance test builds a **real** `IngestionPipeline<string>`, and `Markdig.Signed` is in **no** graph (§14.4).
- `ChunkModelProfile` asserted against `EmbeddingPresets` — **all four profiles, all six fields, ordinally, trailing spaces included** (§17 item 12).
- Four new `ci.yml` test steps, the PdfPig asset assertion, both trim-smoke publishes, the tier-3 ingestion step and the real-world document lane, and `assert-workflows.py` covering `ingestion/tests/` (§15).
- **Seven committed PDFs, `xref-stream.pdf` among them** (§14.2, plan adjustment 25) — the count and the names, plus the four 1.5 markers, exactly as Task 1.3 Step 7 asserts them. A six-PDF corpus means the fixture was dropped, which this plan no longer permits.
- **The §14.5 owner-input gate — the one item this plan can prepare and cannot finish.** `ingestion/tests/fixtures/realworld-corpus.json` exists, parses, and its schema tests pass. Then report its state explicitly, and do **not** call the plan closed while quietly green:

  ```powershell
  $rw = Get-Content "$root\ingestion\tests\fixtures\realworld-corpus.json" -Raw | ConvertFrom-Json
  $n = @($rw.documents).Count
  if ($n -eq 0) {
    Write-Host "OWED: spec 14.5 has no corpus. The nightly lane warns; the coverage test skips."
  } else {
    $producers = @($rw.documents | ForEach-Object { $_.producer } | Sort-Object -Unique)
    foreach ($p in 'word','libreoffice','acrobat') {
      if ($producers -notcontains $p) { throw "realworld-corpus.json has no '$p' document - 14.5 wants all three" }
    }
    Write-Host "OK: $n real-world documents, all three producers represented"
  }
  ```

  An empty array is the **declared** unfinished state and prints `OWED:`, which goes in the PR body beside §17 item 3. A non-empty array that is missing a producer is a **failure** — half a lane is worse than none, because it reads as covered. This is an owner input and it blocks the first publish, not the first commit.
- **The tier-3 lane is *not* in `ci-gate`'s `needs`**, and `trx2junit.py` and `java-junit` still count 4 (§15).
- One sample page, platform-split between `Folder` and `Items` (§16), and two background wirings under `ingestion/samples/` (§16).
- ADRs 0009–0013 and three new third-party notices (§4.1, §17 item 3).
- **Every wave has a close commit on `feat/sp3-ingestion`** — nine of them, none on `main`, none carrying an AI attribution trailer.

- [ ] **Step 6: Verify**

Steps 1–5 all pass. The plan is closed; the branch `feat/sp3-ingestion` is ready for its PR, and the PR body carries the two trim-warning sets from Task 7.2 and the §17 items still owed.

---

## CI-only work

Everything below is authored and locally checked as far as this box allows, and executes for the first time in CI or on the Mac Mini. Nothing here is unverified *design* — it is verified design with a host this machine does not have.

**Every row names the task that writes it.** A row with no owning task would be work nobody does — which is exactly how SP2's tier-3 lane and device assertions went missing from an earlier draft of that plan. CI does not write code. If a row below has no task column entry, that is a plan bug.

| Item | Written by | Why its *execution* is CI-only |
|---|---|---|
| Every `ci.yml` run: the four new test steps, the PdfPig asset assertion on `windows-2025`, the satellite `trim-smoke` publish, the two `model-tests` steps | **Task 2.2** (YAML, asserts) + **Task 7.2** (the console) | No workflow runs during implementation; implementers never push. The YAML is parsed, `assert-workflows.py` is run, and every test step's command is run by hand here |
| `net10.0-ios` and `net10.0-maccatalyst` **compilation** of `Qavren.Edge.Ingestion.Tests`, `…Extractors.Tests` and `Ingestion.Background.iOS` | **Tasks 7.1, 7.3, 8.3** | Needs a Mac unless Task 8.3 Step 3's probe says otherwise; restore demonstrably works here, compilation is the open half |
| `device-tests-android`, `-ios`, `-maccatalyst`, `-windows` picking up the two new test libraries | **Task 8.1** | No macOS, no Xcode, no Android emulator on this box. The android and windows **builds** of the device host are Task 8.1's Steps 2 and 3 |
| **The PdfPig asset trap on a device** — a non-embedded-font PDF opening without `TypeInitializationException` on all four device TFMs (§14.6) | **Task 7.3** | Only a device can prove the `netstandard2.0` font lister is not what got linked. The `ci.yml` assertion catches the resolution; this catches the consequence |
| **The whole-chain `Sleeping` measurement** — SP3's observer + SP2's FTS5 merge + SP1's WAL checkpoint inside a 3 s ceiling with a run active (§14.6, §17 item 15) | **Task 7.1 Step 9** | A host measurement of SP3's link alone proves nothing about the number that decides whether an iOS app is killed |
| §17 item 5: peak RSS for a 200-page PDF and a 20 MB DOCX on a real Android device and an iOS simulator | **Task 7.1 Step 9** writes the allocation-ceiling half; the OS-footprint half is a measurement | Needs a device. **If it does not fit**, lower `MaxDocumentBytes` and record the measured number in the README instead of the current 32 MiB default |
| §17 item 6: what `SkipMissingFonts = true` costs in extraction quality, on a real PDF with a non-embedded font | — (measurement; the default and its README sentence are **Task 6.1**'s) | Needs a real-world PDF and a human reading the output. **If glyphs are dropped**, the default flips and the Android font-scan cost is documented instead |
| §17 item 7: what the ordinal repair actually costs — insert at the head of a **500**-chunk document with `RepairOrdinals` on and off, measuring wall time and FTS5 write volume | — (measurement; the 20-chunk correctness version is **Task 7.1 Step 2**) | A 500-chunk timing on a phone is the number an SP1 issue proposing `AFTER UPDATE OF <fts columns>` would be judged against |
| §17 item 11: the cost of the shared prefix search on an ARM64 device — calls per cut and wall time over a 200 KB document at `MaxTokens = 222` | — (measurement; the correctness half is **Task 2.1 Step 9**) | ARM64. Note that §17's stated fallback is **withdrawn** (plan adjustment 1): no setting of `considerNormalization` would help, so a bad measurement is answered by tuning the window, not by an SP2 edit |
| Tier 3: the real-model ingestion lane and the real-world document lane | **Task 2.2** (the two `model-tests` steps) + **Task 7.1 Step 10** (the test half) | Nightly schedule, never on a PR, and **not** in `ci-gate`'s `needs`. The skip path was exercised here |
| `trim-smoke`'s `linux-x64` publishes, both variants | **Task 7.2** | Published and run for `win-x64` here; the CI leg is `ubuntu-24.04` |
| §17 item 3: whether a plain `PackageReference` carries a NOTICE obligation in Qavren's distribution model | — (owner decision; the notices file is **Task 1.2**'s) | Blocks the first publish, not the first commit |
| **§14.5's three or four real-world documents — OWNER INPUT.** Add entries to `ingestion/tests/fixtures/realworld-corpus.json`: `id`, `producer` (`word`/`libreoffice`/`acrobat`), `url`, `sha256`, `phrase`. Nothing else changes — the fetch step, the tests and the gate are all written | — (owner input; the manifest is **Task 1.3 Step 6b**'s, the fetch step **Task 2.2 Step 4**'s, the three tests **Task 7.1 Step 10**'s, the gate **Task 9.1 Step 5**'s) | Only the owner can choose which real documents represent real users' files. Until the array is non-empty the nightly lane emits a `::warning::`, the coverage test skips with `"spec 14.5 is owed"`, and Task 9.1 prints `OWED:` — three visible signals rather than one green no-op. Blocks the first publish, not the first commit |
| Branch protection and the PR | — | The owner's, after the branch is pushed |

## Open risks

1. **The `considerNormalization: false` measurement used a toy vocabulary.** The conclusion that shipped — one shared prefix search — is correct under either outcome, but the *stated reason* ("it returns the whole string for every budget") may be vocabulary-dependent. Task 2.1 Step 1 re-runs it against the real 30,522-entry vocabulary and records the answer; if the real-vocab behaviour differs, ADR 0013's context paragraph is amended and the decision stands. **The vocabulary itself is no longer a risk** — Environment ground truth verifies it present at 231,508 bytes and SHA-256 `07eced…` and pins the re-provisioning command, so Step 1's probe and Task 4.1's twenty goldens both have their input.

1a. **The vocabulary lives in `%LOCALAPPDATA%\Temp`, which is a directory Windows will eventually empty.** That is where SP2's model store put it and SP3 does not move it — copying a 231 KB vocabulary into the repo to dodge a cleanup would put a generated artifact in git for the first time in this suite. The mitigation is that losing it is *cheap and loud*: Task 2.1 Step 1 and Task 4.1 Step 0 both hard-fail with the one-line re-fetch command in the message, and the golden test class **skips with a printed reason** rather than failing when the vocabulary is genuinely unavailable (a device lane, where there is no vocabulary and never will be). What must never happen is a golden **generated** against a substitute, which is why plan adjustment 23 makes that branch a stop rather than a fallback.
2. **`SpecialTokenOverhead = 2` is the single highest-consequence number in the package.** Every expected boundary in every committed golden moves if it is wrong. It is measured, pinned by a unit test, and re-measured against the real vocabulary before any golden is written — but a `Microsoft.ML.Tokenizers` bump that changes `CountTokens`'s special-token treatment would invalidate twenty golden files at once, and the first symptom would be twenty red tests with no obvious common cause. The pinning test's failure message says so.
3. **Plan adjustment 2 makes SP3 depend on SP2's registry behaviour, which is `internal`.** SP3's collection is merged and reported because `AddVectorCollectionMigration` writes `EdgeVectorCollectionRegistry`. Nothing in SP2's public contract promises that, and a future SP2 refactor could quietly stop registering. Task 7.1 Step 7's assertion — SP2's event 803 fires naming SP3's FTS table — is the detector, and it is the test that must never be deleted as "testing someone else's code".
4. **`EdgeVectorStoreCollectionOptions.RemoveDiacritics` being `internal` is a papered-over gap.** `IngestionOptions.FullTextRemoveDiacritics` closes it for SP3's consumers, but the underlying asymmetry — a store-level option a collection-level options object cannot carry — remains, and any future SP2 shaping value added the same way will need the same workaround. File an SP2 issue asking for the member to be public; do not take it on an unmerged branch.
5. **`xref-stream.pdf` ships, and what remains is narrower.** The fixture is generated by `build_xref_stream` (written in full in Task 1.3 Step 3) and opened by PdfPig 0.1.16 in Step 3b before it is committed, so "it may not ship" is no longer the risk — plan adjustment 25 removed the fallback that created it. The residual is that the fixture is validated against **one** parser: it is a hand-built 1.5 file, not one Acrobat produced, so it proves PdfPig reads *this* correct shape, not that it reads every shape a real writer emits. That gap is exactly what §14.5's lane is for, which is why risk 6 matters more than it looks. A second residual: `broken-startxref.pdf` may be *recovered* by PdfPig rather than rejected (Step 3b prints which), and if it is, 6104's raise site moves to a truncated-object PDF built at test time — Task 6.1 Step 5 carries that branch.
6. **The real-world document lane ships with an empty corpus, and that is now visible in three places instead of none.** The three or four files are the owner's to choose; until `ingestion/tests/fixtures/realworld-corpus.json` has entries, §14.5's claim — "the only thing that proves anything about the documents users actually have" — is unbacked, and hand-rolled fixtures prove only that the parser handles what we hand-rolled. What changed (plan adjustment 26) is that the unfinished state is **declared**: the nightly fetch step emits a `::warning::` on every run, `RealWorldCorpusCoverageTests` skips with the printed reason *"spec 14.5 is owed"*, and Task 9.1 Step 5 prints `OWED:` into the gate output and the PR body. A gate that goes green on an empty lane is the failure this replaces. The remaining risk is ordinary: nobody fills it in.
7. **Markdig declares neither `IsTrimmable` nor `IsAotCompatible`, and it is in every consumer's graph.** It is effectively reflection-free — the only `System.Reflection` use in the library is `Markdown.Version`'s `GetCustomAttribute`, and `MarkdownExtensions.Configure(string)` is a hardcoded switch, neither of which SP3 calls — but that is an expectation, not an annotation. Task 7.2's core-only publish is what measures it. **If the core's warnings are the noisy ones, the plan reconsiders whether the Markdown extractor belongs in a satellite of its own**, which would be a package split after the fact and is the reason this is a risk rather than a footnote.
8. **`PageBudget` does not defend against a single page that never returns.** PdfPig's per-page surface takes no `CancellationToken`, and abandoning a worker thread mid-parse leaves the `PdfDocument` and its `StreamInputBytes` in an undefined state. Nothing in-process can fix this; the only real defence is the consumer's own process-level budget, and the package README says so rather than papering over it.
9. **`MaxDocumentBytes = 32 MiB` is reasoned, not measured.** The whole-text buffer `ExtractedDocument` holds is the one thing kept whole, and §17 item 5 is the measurement that decides whether 32 MiB fits a phone. Guessing high is a jetsam that loses the run as well as the document.
10. **The two `ingestion/samples/` projects compile but nothing runs them.** Being in the `.slnx` stops them rotting through a rename; it does not stop them being wrong about `BGTaskScheduler` or `WorkManager` semantics, which change with every OS release. They are documentation, and they are dated the day they are written.
11. **`Microsoft.Extensions.DataIngestion` is preview after eleven months and fourteen releases.** The shim ships prerelease and is excluded from package validation, so a breaking MEDI change fails one test project rather than a release — but the conformance test is the *only* thing standing between "Semantic Kernel drop-in" as a claim and as a hope, and it is the test most likely to be disabled when it goes red for an upstream reason.
12. **A device lane that picks up two more test libraries gets slower, and nothing budgets for it.** The four existing lanes now run SP3's tier-1 suite, the vec0/FTS5 half of tier 2, the PdfPig asset trap and a 3-second `Sleeping` measurement on top of what they already do. If a lane starts timing out, the answer is to split the device suite, not to delete assertions — say so in the PR body.
13. **Inherited from SP2, unchanged:** a new `ci.yml` job that is forgotten in `ci-gate`'s `needs` does not block a merge, and `assert-workflows.py` cannot know which future jobs ought to be gated. SP3 adds no job, so nothing new is exposed — but the `trim-smoke` satellite publish is a *step*, not a job, and its `continue-on-error` means its warnings are advisory until somebody reads them. Task 7.2's README recording is the mechanism that makes them read.
