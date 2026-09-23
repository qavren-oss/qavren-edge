# Qavren.Edge.Ingestion

An L2 recipe on top of `Qavren.Edge.VectorData` (SP2) and `Qavren.Edge.Sqlite`
(SP1) that turns a folder, a picker result or a single stream into a
searchable, hybrid-search-ready collection: plain-text and Markdown
extraction in the core, opt-in PDF and DOCX extractors, three token-aware
chunkers, content-hash incremental re-index, a budget that suspends on a
committed boundary, and durable per-document state.

## Getting started

Two calls. `AddIngestion` claims **two consecutive migration versions** —
`migrationVersion` (`N`) for the collection and `N + 1` for the three state
tables it owns (see ADR 0012) — and `AddOnnxIngestion` is the ONNX satellite
call that wires a real tokenizer and throttle in:

```csharp
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddOnnxEmbeddings()                  // SP2
    .AddVectorStore()                     // SP2
    .AddIngestion(migrationVersion: 10)   // SP3 — claims 10 AND 11
    .AddOnnxIngestion()                   // SP3 — tokenizer + throttle, ONNX consumers only
    .AddPdfExtractor()                    // optional
    .AddDocxExtractor());                 // optional
```

```csharp
var pipeline = services.GetRequiredService<IIngestionPipeline>();

var result = await pipeline.RunAsync(
    IngestionSource.Folder(@"C:\docs", "*.md", recursive: true),
    options: new IngestionRunOptions { Budget = IngestionBudget.Background });

Console.WriteLine($"{result.Outcome}: +{result.ChunksAdded} −{result.ChunksRemoved} " +
                  $"({result.DocumentsSkipped} skipped) {result.SuspendReason}");
```

Search afterwards is plain SP2 — nothing about ingestion changes it:

```csharp
var chunks = store.GetDynamicCollection(
    "chunks", IngestionSchema.BuildDefinition(dimensions: 384, DistanceFunction.CosineDistance, fullTextIndexed: true));

await foreach (var hit in chunks.HybridSearchAsync("water damage", ["roof", "leak"], top: 10))
{
    var chunk = IngestedChunk.FromRecord(hit.Record);
    Console.WriteLine($"{chunk.DocumentId} §{chunk.Breadcrumb} {hit.Score:F4}");
}
```

## Packages

| Package | Depends on | Purpose |
|---|---|---|
| `Qavren.Edge.Ingestion` | `Qavren.Edge.VectorData` (project); `Markdig`; `System.IO.Hashing`; `Microsoft.ML.Tokenizers` | The core. `AddIngestion()`, the document model, the plain-text and Markdown extractors, the three chunkers, the token budget, xxHash128 content hashing, the recipe, the incremental diff, the runner, the chunk writer, the state migration, the lifecycle observer, the diagnostics contributor and the exceptions. |
| `Qavren.Edge.Ingestion.Pdf` | `Qavren.Edge.Ingestion`; `PdfPig` | `PdfTextExtractor`. **PdfPig is licensed Apache-2.0**, unlike the rest of this suite (MIT) — see ADR 0009 — which is exactly why it lives in its own opt-in package rather than the core: a Markdown-only app carries neither the Apache-2.0 term nor PdfPig's ~5.7 MB of assets unless it calls `AddPdfExtractor()`. |
| `Qavren.Edge.Ingestion.OpenXml` | `Qavren.Edge.Ingestion`; `DocumentFormat.OpenXml` | `DocxTextExtractor`. MIT, so this split is size, not licence: the nupkg is 14.66 MB. |
| `Qavren.Edge.Ingestion.Onnx` | `Qavren.Edge.Ingestion`; `Qavren.Edge.Embeddings.Onnx` (project) | `EdgeChunkTokenizer : IChunkTokenizer` over SP2's `IEdgeTokenizer`, `ResourceMonitorThrottle : IIngestionThrottle` over SP2's `IEdgeResourceMonitor`, and `AddOnnxIngestion()`, which derives the chunk budget from the resolved `EmbeddingPreset`. |
| `Qavren.Edge.Ingestion.DataIngestion` | `Qavren.Edge.Ingestion`; `Microsoft.Extensions.DataIngestion.Abstractions` (prerelease) | The MEDI shim, both directions — see ADR 0011. Ships prerelease only, against the zero-dependency Abstractions package; never references the MEDI implementation package. On a stable suite tag `X.Y.Z` it still packs as `X.Y.Z-preview` — `dotnet add package Qavren.Edge.Ingestion.DataIngestion --prerelease` to pick it up. |

No satellite references another. Dependency direction is strictly downward:
`Ingestion.{Pdf,OpenXml,Onnx,DataIngestion}` → `Ingestion` → `VectorData` →
`Sqlite` → `Core`.

## The chunk budget

`ChunkOptions.Resolve(model, tokenizer)` runs at startup (not at container
build — no `IChunkTokenizer` exists yet when `AddIngestion` runs) and
produces the resolved triple every chunker uses:

| Knob | Default | On MiniLM-int8 (256) | On bge-small (512) |
|---|---|---|---|
| `MaxTokens` | `MaxSequenceLength − SpecialTokenOverhead − HeadingPathTokenBudget − DocumentPrefixTokens` | **222** | **478** |
| `HeadingPathTokenBudget` | 32 | 32 | 32 |
| `OverlapTokens` | `max(8, (MaxTokens * 15 / 100) / 8 * 8)` — integer arithmetic throughout | **32** | **64** |
| `MinTokens` | `max(16, MaxTokens / 8)` — integer division | **27** | **59** |

`SpecialTokenOverhead` — the tokens `BertTokenizer.EncodeToIds` adds by
default that `CountTokens` never reports — is measured at **2** against the
shipped `Microsoft.ML.Tokenizers` 2.0.0 assembly, for both pinned profiles
above. A chunk sized to exactly the ceiling by `CountTokens` alone would
encode to two more ids than the model accepts and be silently truncated,
which is why `SpecialTokenOverhead` is subtracted before `MaxTokens` is ever
handed to a chunker.

A **prefixed** profile — one whose `DocumentPrefix` is non-empty, such as
`nomic-embed-text-v1.5-int8`'s `"search_document: "` — resolves a different
triple, because `DocumentPrefixTokens` is `tokenizer.CountTokens(prefix)` and
is not zero for it. That triple is deliberately **not** tabulated here: it
depends on the tokenizer's vocabulary, and a number that depends on the
vocabulary is computed at startup, not pinned in documentation.

## What re-running over an unchanged corpus costs

One sequential, 64 KiB-buffered read per document, to compute its content
hash — zero embeddings, zero writes, for every document whose hash matches
its last recorded state.

The diagnostics contributor's `recipeStaleDocuments == 0 && staleDocuments ==
0 && lastRunOutcome == Completed` means **nothing known is outstanding**: no
recipe bump to absorb, no pruning sweep owed, and the last run finished
cleanly. It does **not** mean the files on disk are unchanged — discovering
that costs exactly the read-per-document pass above, which is what a run
with `IngestionBudget.Quick` is for. A consumer who reads the first sentence
as the second will stop re-indexing edited files, because nothing in the
diagnostics keys tells them a file changed without actually opening it.

## The `Sleeping` budget arithmetic

Three lifecycle observers run back to back, awaited in sequence, on the
platform callback thread when the app is about to background:

1. **`IngestionLifecycleObserver`** (SP3) sets the stop flag, awaits the
   runner's next committed checkpoint within `SleepGraceBudget` (750 ms,
   validated ≤ 2 s), then merges its own FTS5 sidecar. SP3's collection is
   registered through SP2's `AddVectorCollectionMigration` (ADR 0012), so it
   is one of the collections the **next** observer merges — SP3 no longer
   runs a merge of its own.
2. **SP2's `VectorDataLifecycleObserver`** runs its bounded FTS5 merge —
   `INSERT INTO "<t>"("<t>", rank) VALUES ('merge', 500)` — 500 pages, at
   most 4 iterations, against a 2-second budget, once per **registered**
   full-text collection, SP3's included.
3. **SP1's SQLite observer** runs `wal_checkpoint(TRUNCATE)` against a
   database nobody is writing to any more, because the stop flag was set and
   awaited first.

For the common shape — one SP3 collection, nothing else registered — the
chain is `SleepGraceBudget` plus one merge, well inside iOS's documented ~5 s
`applicationDidEnterBackground:` window. An app that also registers its own
collections through `AddVectorCollectionMigration` pays SP2's merge again for
each one, and enough of them can push the chain past that window — fewer
registered full-text collections, or a lower `SleepGraceBudget`, is the
remedy.

## Sources on iOS and Android

`IngestionSource.Folder` is a filesystem-path factory: correct for desktop
and for app-private storage, wrong for a location the user picked. A folder
chosen through Android's Storage Access Framework is a `content://` tree
URI, not a path; a file or folder chosen through an iOS/Mac Catalyst document
picker is a security-scoped URL. Neither can be walked with
`Directory.EnumerateFiles`. `IngestionSource.Items` is the shape that
expresses a picker result instead:

```csharp
// MAUI, both platforms. The handle stays opaque to SP3.
var picked = await FilePicker.Default.PickMultipleAsync(options);

var source = IngestionSource.Items(
    picked.Select(f => new DocumentSourceItem(
        DocumentId: f.FileName,                       // stable id the app chooses
        MediaType:  f.ContentType ?? IngestionMediaTypes.FromExtension(f.FileName),
        OpenAsync:  async ct => await f.OpenReadAsync(),   // re-openable; see below
        Path:       f.FullPath,                            // diagnostics only
        SizeBytes:  null)),                                // unknown: see below
    sourceId: "user-picked");

await pipeline.RunAsync(source);
```

Three traps, each the mistake a consumer makes first:

1. **`DocumentId` is the app's choice and must be stable across runs.** Every
   state row is keyed on `(collection, source_id, document_id)`; an id
   derived from a SAF URI the OS re-issues between sessions turns every
   re-run into a full re-index. Use a filename, a relative path, or the
   app's own record id — never a raw `content://` string unless the
   permission is persisted.
2. **`SizeBytes` is legitimately unknown here.** A picker result frequently
   cannot report a size up front, which is exactly why the document-size
   ceiling has a counted-read path, not only an enumeration-time gate.
3. **`OpenAsync` is called twice**, and on iOS it must re-acquire the
   security scope — `StartAccessingSecurityScopedResource()` /
   `StopAccessingSecurityScopedResource()` — on **each** call. A delegate
   that starts the scope once outside `OpenAsync` and never stops it leaks
   it.

## `PageBudget`'s honest limitation

`PdfExtractorOptions.PageBudget` (20 s default) is a between-pages watchdog,
not a timeout, and that distinction is deliberate rather than an oversight:
PdfPig's per-page surface — `doc.GetPages()`, `page.Letters`,
`ContentOrderTextExtractor.GetText(page)` — is fully synchronous and takes no
`CancellationToken`. `PageBudget` bounds the realistic case, a document with
many individually reasonable but cumulatively slow pages; it does **not**
defend against a single page that never returns, because nothing in-process
can abandon a synchronous call mid-parse without leaving the `PdfDocument`
and its stream in an undefined state. The only real defence against a
wedged page is the consumer's own process-level budget.

## Samples

`ingestion/samples/` holds two single-TFM class libraries —
`Ingestion.Background.iOS` (a `BGProcessingTask` / `BGContinuedProcessingTask`
wiring) and `Ingestion.Background.Android` (a foreground-promoted `Worker`
wiring). The solution compiles both so they never silently rot on an API
rename, but nobody installs them: they exist so each platform's background
wiring lives somewhere a reader can copy from, not as shipped packages.

## Trim warnings

Spec 17 item 4, measured rather than assumed. `embeddings/tools/Qavren.Edge.TrimSmoke`
is published with `-p:PublishTrimmed=true` twice from one project — once with
the core alone, once with `-p:QedgeTrimSatellites=true`, which adds the Pdf and
OpenXml `ProjectReference`s, the four embedded fixtures and the
`QEDGE_TRIM_SATELLITES` compile symbol the console's `#if` switches on — and
each publish log is scanned for `IL2xxx` / `IL3xxx` lines. Nothing in either
publish graph suppresses a trim warning: no `SuppressTrimAnalysisWarnings`, no
`TrimmerSingleWarn` override, no `NoWarn` on an `IL` code in any `src/` project
or in the console (two *test* projects mute `IL2026`; neither is referenced
here). The repo-wide `TreatWarningsAsErrors` reaches ILLink too, so a trim
warning would have failed the publish outright rather than scrolled past.
Both runs below exit 0 through `EdgeDynamicVectorStoreCollection`, on SDK
10.0.401, `win-x64`, 2026-09-11. The `linux-x64` leg is the `trim-smoke` CI
job's.

### Core only

`Qavren.Edge.Ingestion` plus its Markdig, System.IO.Hashing and
Microsoft.ML.Tokenizers references, running the Markdown one-document path
(a heading, a fence and a table, chunked by `markdown-heading` and read back
through the dynamic collection): **this publish was warning-free.**

Markdig — the one core dependency that declares neither `IsTrimmable` nor
`IsAotCompatible` — survived the trimmer with nothing to say; the two
reflection sites it does carry (`Markdown.Version`'s `GetCustomAttribute` and
the `Configure(string)` switch) are not on SP3's path, and the linker agreed.
The Markdown extractor therefore stays in the core; the open risk that it
might need a satellite of its own is closed on this evidence.

### With the Pdf and OpenXml satellites

The core above plus `Qavren.Edge.Ingestion.Pdf` (PdfPig 0.1.16, six
assemblies) and `Qavren.Edge.Ingestion.OpenXml` (DocumentFormat.OpenXml, two
assemblies), ingesting the committed `minimal-text.pdf` byte for byte and a
DOCX the console assembles from the three embedded `run-split` parts: **this
publish was warning-free.**

Neither `DocumentLayoutAnalysis.Export` nor `OpenXmlValidator` is on SP3's
path, which is the assumption spec 17 item 4 made, and the linker produced no
`IL2104` for any of the eight third-party assemblies. The CI step for this
variant is `continue-on-error` for one release cycle; on this measurement it
could be promoted to required.
