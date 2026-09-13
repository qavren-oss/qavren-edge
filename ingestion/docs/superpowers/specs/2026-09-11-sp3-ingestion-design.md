# Qavren.Edge — Sub-project 3: ingestion

Date: 2026-09-11
Status: draft for planning. Branch `feat/sp3-ingestion`, based on `feat/sp2-embeddings`
and to be rebased onto `main` once SP2 merges.
Sub-project 1 is MERGED and is the contract. Sub-project 2 is **not merged**: this
document references it only by public type name, and every SP2 fact it relies on is
listed in §5 so a rebase has one place to check. Where this document and the SP1 or
SP2 code disagree, the code wins.
Owner: Steve Ackley (Qavren Solutions LLC)

## 1. Summary

Sub-project 3 turns files into a searchable collection. `Qavren.Edge.Ingestion` is
an L2 recipe over SP2's `EdgeVectorStore` and `IEmbeddingGenerator` and SP1's
`IEdgeDatabase`, lifecycle hub and diagnostics. Roadmap row 3 names one package;
this ships one core package plus four optional satellites, each isolated for a
reason stated in §4.1.

The headline is two calls:

```csharp
edge.AddIngestion(migrationVersion: 10);
var result = await pipeline.RunAsync(IngestionSource.Folder(@"C:\docs"));
```

Six decisions shape everything below.

1. **The collection is the state.** There is no chunk-level state table. The diff
   for a changed document is one indexed read of `(key, chunk_hash, ordinal,
   char_start, char_end)` straight off SP2's data table, using the `document_id`
   index SP2 already creates for an `IsIndexed` property. Only *document*-level
   state is persisted. A second copy of chunk hashes would be a dual-write
   consistency problem in exchange for a read we already have an index for.

2. **Chunk keys are content-addressed, so a repair is not a re-embed.** The key is
   `xxHash128(sourceId, documentId, chunkContentHash, duplicateOrdinal)`. Insert a
   paragraph at the top of a 500-chunk document and exactly one chunk is embedded;
   the other 499 keep their keys, their rows and their vectors, and get an
   ordinal/offset **repair** — a plain `UPDATE` on the data table that never touches
   the `vec0` row, because `vec0` is keyed on the data row's `_rowid` and an `UPDATE`
   does not move it. §9.5 states what that repair does cost, which is not nothing.

3. **One knob drives the whole budget, and it is a core-owned value type.**
   `IngestionOptions.Model` is a `ChunkModelProfile` — `(Id, Dimensions,
   MaxSequenceLength, Pooling, DocumentPrefix, QueryPrefix)`, six fields of plain
   data declared in the **core** package — and it supplies both the collection's
   dimensions and the chunk token budget. `MaxTokens` defaults to
   `Model.MaxSequenceLength − tokenizer.SpecialTokenOverhead − HeadingPathTokenBudget
   − tokenizer.CountTokens(Model.DocumentPrefix)` — 222 on the default MiniLM-int8
   profile, 478 on bge-small, and a computed value on a prefixed profile like nomic —
   and is resolved once in the order-400 startup task, which is the first moment an
   `IChunkTokenizer` exists (§8.1, §11.1). It is deliberately *not* SP2's `EmbeddingPreset`: that type lives
   in `Qavren.Edge.Embeddings.Onnx`, and naming it in the core's options would
   contradict §2 decision 2 and re-introduce the ORT dependency §4.1 exists to avoid.
   `AddOnnxIngestion()` projects the resolved `EmbeddingPreset` onto the profile
   (§11.2). The prior art's `MaxLen = 64` silent truncation is structurally
   unreachable: the chunker counts with the same tokenizer the encoder encodes with,
   and a chunk over budget is an exception, never a quiet truncation at the encoder.

4. **SP3 never nests a transaction inside an SP2 write.** Verified in source:
   `EdgeDatabase.ExecuteInTransactionAsync` opens a **new connection** and `BEGIN`s
   its own transaction per call (`foundation/src/Qavren.Edge.Sqlite/EdgeDatabase.cs`
   lines 194–212), and SP2's `UpsertAsync` and `DeleteAsync` each wrap themselves in
   one (`embeddings/src/Qavren.Edge.VectorData/EdgeVectorStoreCollection.cs` lines
   280 and 328). A single transaction spanning an SP2 collection write and an SP3
   state write is therefore not reachable through today's public surface — it is a
   second connection contending for the write lock, which is `SQLITE_BUSY`, not
   atomicity. So SP3 does not have per-document atomicity and does not pretend to:
   writes commit per window, the state row commits **last**, and a torn window is
   repaired by the next run's hash diff. Resume is not a separate code path; it is
   the same code path.

5. **Budget-driven, platform-free.** `RunAsync` returns `Completed`, `Suspended`,
   `Cancelled` or `Failed`. `Suspended` is a normal outcome of an
   `IngestionBudget`, a throttle pause or an SP1 `Sleeping` event, and it carries a
   machine-readable `SuspendReason`. There is no `BGTaskScheduler`, no
   `WorkManager`, no `foregroundServiceType` — SP1 non-goal, and the platform rules
   (Android 15's six-hour `dataSync` wall, iOS's unguaranteed CPU) belong to the
   consumer app, with samples.

6. **`Microsoft.Extensions.DataIngestion` is mirrored, not depended on — and the
   shim ships anyway.** MEDI has 14 prereleases in 11 months, `<Stage>preview</Stage>`
   in its csproj, abstractions that are abstract classes, chunks that carry no
   ordinal / offset / hash / token count, `Guid.NewGuid()` chunk keys and a
   delete-and-replace "incremental" mode that re-embeds every chunk of an unchanged
   document. A stable MIT 1.0 package cannot carry it. But the shapes are sane, so
   SP3's verbs match MEDI's, and `Qavren.Edge.Ingestion.DataIngestion` — prerelease,
   **shipping** against only `Microsoft.Extensions.DataIngestion.Abstractions`, which
   has zero dependencies on net8.0+ — makes the Semantic Kernel / Agent Framework
   drop-in a tested claim rather than an aspirational one. The *conformance test*
   additionally restores the MEDI implementation package and everything it drags;
   that graph is confined to one test project and is not cheap (§14.4).

## 2. Suite decisions that apply

| # | Decision | How SP3 honours it |
|---|---|---|
| 1 | Hosting ergonomics = hosting + DX only | No scheduling. `IngestionBudget` + `Suspended` + durable state is the seam; the two OS wirings are samples and a docs page. |
| 2 | L0 → L1 → L2 layering | Ingestion is L2 and consumes only L1 public surface: `IEmbeddingGenerator`, `EdgeVectorStore`, `IEdgeDatabase`. The core package does **not** reference `Qavren.Edge.Embeddings.Onnx` and **names no type from it** — `IChunkTokenizer` and `ChunkModelProfile` are the core-owned seams, and `Qavren.Edge.Ingestion.Onnx` is the bridge (§4.1, §11.2). |
| 6 | TFMs | Every SP3 shipped package is `net10.0` alone. Nothing in SP3 touches a platform API: PdfPig, DocumentFormat.OpenXml and Markdig are pure managed with no `runtimes/` folder between them. **Two** of the four test projects — `Ingestion.Tests` and `Ingestion.Extractors.Tests` — are device-hosted and take all five TFMs (`net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst;net10.0-windows10.0.19041.0`) so the Windows device lane can reference them, exactly as SP2's do. `Ingestion.Onnx.Tests` and `Ingestion.DataIngestion.Tests` are `net10.0` host-only (§4.2, §14.6). |
| 9 | Ingestion inputs | Text + Markdown chunkers, PDF and DOCX extraction. Images later — an image element is not in the document model, and the MEDI shim drops images with a logged warning (§7.1). HTML is **not** in decision 9 and is not in SP3 (§18). |
| 10 | Encryption ships in v1 | Ingestion runs on whatever connection `IEdgeDatabase` hands it, SQLCipher included. No SP3 code reads a connection string. |
| 11 | MIT | The core and three of four satellites are MIT. PdfPig is **Apache-2.0**, which is why it is isolated in `Qavren.Edge.Ingestion.Pdf` and nowhere else (§4.1, ADR 0009). |
| 13 | MS-native hosting | `edge.AddIngestion()` on SP1's `EdgeBuilder`; one `IEdgeMigration`; one `IEdgeStartupTask` at order 400 that touches no database; an `IEdgeLifecycleObserver`; an `IEdgeDiagnosticsContributor`; `EdgeException` subclasses with stable `EdgeErrorCode`s in a new 6000–6299 range. |
| 14 | GitHub-hosted CI | Four new host-test steps on the existing three-OS matrix; the four existing device lanes pick SP3 up through `Qavren.Edge.DeviceTests`. No new device lane, no new gate. |

## 3. Goals and non-goals

Goals:

- A consumer who already has SP1 + SP2 wired adds one builder call and one
  `RunAsync` and gets a folder indexed for hybrid search.
- Re-running over an unchanged corpus performs **zero** embedding calls and
  **zero** writes. Editing one paragraph re-embeds one chunk.
- A run can be given a 20-second budget, suspend on a committed boundary, and be
  resumed by calling the same method again with no special argument.
- A crash — jetsam, power loss, a thrown writer — never corrupts the index and
  never loses committed work. The next run converges.
- Nothing is missed on iOS or Android: no whole-file `ReadAllBytes`, no DOM
  materialisation of a large DOCX, no unbounded embed batch, no ignored thermal or
  low-power signal, no lifecycle observer that blocks a platform callback thread.
- Every ingestion fact is inspectable: the recipe hash, the chunk budget, the
  tokenizer identity, the registered extractors, the last run's outcome and
  counters, and how many documents are dirty right now.

Non-goals:

- No background job scheduling (SP1 non-goal). No `Info.plist` identifier, no
  notification channel, no `POST_NOTIFICATIONS` prompt inside a library.
- No OCR. Scanned-page **detection** is in v1 and is a first-class outcome; text
  recognition is not.
- No HTML extractor, no EPUB/PPTX/XLSX/RTF, no image ingestion.
- No semantic-similarity chunker, no recursive-character splitter, no enrichers.
- No retriever, re-ranker or RAG recipe (sub-project 4). SP3 writes; SP2 searches.
- No parallel stage pipeline. One ORT session at `MaxConcurrency = 1` and one
  SQLite writer make channels a net loss on the device most likely to be jetsammed
  (§18).

## 4. Architecture

### 4.1 Packages

| Package | TFMs | Depends on | Purpose |
|---|---|---|---|
| `Qavren.Edge.Ingestion` | net10.0 | `Qavren.Edge.VectorData` (project); `Markdig` 1.3.2; `System.IO.Hashing` 10.0.12; `Microsoft.ML.Tokenizers` 2.0.0 | The core. `AddIngestion()`, the document model, the plain-text and Markdown extractors, the three chunkers, the token budget, xxHash128 content hashing, the recipe, the incremental diff, the runner, the chunk writer, the state migration, the lifecycle observer, the diagnostics contributor, the exceptions. |
| `Qavren.Edge.Ingestion.Pdf` | net10.0 | `Qavren.Edge.Ingestion`; `PdfPig` 0.1.16 | `PdfTextExtractor`. Isolated because PdfPig is **Apache-2.0** and suite decision 11 is MIT, and because its net9.0 assets are ~5.7 MB dominated by embedded CMap/ICC resources. One call opts in. |
| `Qavren.Edge.Ingestion.OpenXml` | net10.0 | `Qavren.Edge.Ingestion`; `DocumentFormat.OpenXml` 3.5.1 | `DocxTextExtractor`. MIT, so this split is size not licence: the nupkg is 14.66 MB and a Markdown-only app must not carry it. |
| `Qavren.Edge.Ingestion.Onnx` | net10.0 | `Qavren.Edge.Ingestion`; `Qavren.Edge.Embeddings.Onnx` (project) | ~200 lines. `EdgeChunkTokenizer : IChunkTokenizer` over SP2's `IEdgeTokenizer`, `ResourceMonitorThrottle : IIngestionThrottle` over SP2's `IEdgeResourceMonitor`, and `AddOnnxIngestion()` which derives the chunk budget from the resolved `EmbeddingPreset`. |
| `Qavren.Edge.Ingestion.DataIngestion` | net10.0 | `Qavren.Edge.Ingestion`; `Microsoft.Extensions.DataIngestion.Abstractions` 10.10.0-preview.1.26459.2 | The MEDI shim, both directions. **Ships prerelease only** until MEDI ships stable, so the stable core is never blocked by it. The **implementation** package `Microsoft.Extensions.DataIngestion` is referenced by the conformance *test* alone and never by this package (§14.4). |

Dependency direction is strictly downward and matches SP1's and SP2's:
`Ingestion.{Pdf,OpenXml,Onnx,DataIngestion}` → `Ingestion` → `VectorData` →
`Sqlite` → `Core`. The satellites do not reference each other.

**Why the core does not reference `Qavren.Edge.Embeddings.Onnx`, and why that is
not a compromise.** The chunker must count tokens with the *same* tokenizer the
encoder encodes with, or chunks truncate silently — that is non-negotiable, and
SP2 built `IEdgeTokenizer.IndexByTokenCount` for it, with the doc comment "Exists
so SP3's token-window chunker never writes a second token counter." A
`ProjectReference` from the core would satisfy it and would also drag roughly
100 MB of ORT AARs and xcframeworks into a Markdown-only app using a cloud
embedding generator — the exact shape `Qavren.Edge.VectorData` was kept `net10.0`
and ONNX-free to avoid. So the core publishes `IChunkTokenizer` (§8.1) and two
construction paths: `EdgeTokenCounter.CreateWordPiece(vocabPath, …)` over
`Microsoft.ML.Tokenizers` directly, which is ONNX-free and is what a cloud-generator
consumer uses; and `AddOnnxIngestion()` in the satellite, which forwards to SP2's
`IEdgeTokenizer` and is one extra builder line for ONNX consumers only. This costs
**zero** edits to SP1 or SP2 — which matters, because SP2 is unmerged (§5).

That separation is not only a package reference. **No core type names an SP2 ONNX
type**, which is why `IngestionOptions.Model` is a core-owned `ChunkModelProfile`
and not SP2's `EmbeddingPreset`: a core options class with a
`Qavren.Edge.Embeddings.Onnx.EmbeddingPreset` property would not compile against the
dependency set above. `ChunkModelProfile` carries only data — id, dimensions, max
sequence length, pooling name, document and query prefixes — so it has no ONNX
surface to inherit, and §11.2 is the one place the two are reconciled.

`Qavren.Edge.Ingestion.Onnx` is `net10.0` even though it references a four-TFM
project. A `net10.0` assembly referencing a multi-TFM package is ordinary TFM
compatibility, and the consuming app resolves `Qavren.Edge.Embeddings.Onnx`'s
platform asset for *its own* TFM through the package graph, so an Android app
still links ORT's `net9.0-android35.0` asset. Nothing is lost by the satellite
being unversioned.

**License.** `THIRD-PARTY-NOTICES.md` gains PdfPig (Apache-2.0), Markdig
(BSD-2-Clause) and DocumentFormat.OpenXml (MIT). `Qavren.Edge.Ingestion.Pdf`'s
README states the Apache-2.0 term in its first paragraph. Whether a plain
`PackageReference` (rather than a vendored copy) carries a NOTICE obligation in
Qavren's distribution model is an owner call, recorded as verification item 3.

### 4.2 Repository layout

`ingestion/` mirrors `embeddings/`:

```
qavren-edge/
  QavrenEdge.slnx                       # SP3 projects added under /ingestion/{src,tests,samples}/
  Directory.Packages.props              # SP3 PackageVersions added here
  ingestion/
    README.md
    src/
      Qavren.Edge.Ingestion/                       # net10.0
      Qavren.Edge.Ingestion.Pdf/                   # net10.0
      Qavren.Edge.Ingestion.OpenXml/               # net10.0
      Qavren.Edge.Ingestion.Onnx/                  # net10.0
      Qavren.Edge.Ingestion.DataIngestion/         # net10.0, prerelease
    tests/
      Qavren.Edge.Ingestion.Tests/                 # 5 TFMs, device-hosted
      Qavren.Edge.Ingestion.Extractors.Tests/      # 5 TFMs, device-hosted
      Qavren.Edge.Ingestion.Onnx.Tests/            # net10.0
      Qavren.Edge.Ingestion.DataIngestion.Tests/   # net10.0
      fixtures/                                    # committed corpus + golden files + .gitattributes
    samples/
      Ingestion.Background.iOS/                    # BGProcessingTask + BGContinuedProcessingTask wiring
      Ingestion.Background.Android/                # foreground-promoted Worker wiring
    docs/superpowers/{specs,plans}/ , docs/adr/
```

The samples are **documentation projects, not shipped packages** and are not in
`ci-gate`'s path; they exist so the two OS wirings live somewhere a reader can
copy from. `foundation/samples/Qavren.Edge.Sample` gains one page (§16) and
`foundation/tests/Qavren.Edge.DeviceTests` gains two `ProjectReference`s.

New `Directory.Packages.props` entries, under three new labels:

```xml
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
     The MEDI IMPLEMENTATION package ships IngestionPipeline<T>, which the conformance
     test needs and the shim does not. Never referenced by a src/ project. -->
<ItemGroup Label="SP3 test (MEDI conformance)">
  <PackageVersion Include="Microsoft.Extensions.DataIngestion" Version="10.10.0-preview.1.26459.2" />
</ItemGroup>
```

`System.IO.Hashing` 10.0.12 is a **new** reference, not an existing pin — it
happens to sit on the repo's 10.0.12 `Microsoft.Extensions.*` version line, which
is convenient and is not the same thing. Central transitive pinning stays off.

`Microsoft.Extensions.DataIngestion.Markdig` is deliberately **absent**, and §14.4
explains the consequence: it pulls `Markdig.Signed`, a second package id shipping the
same `Markdig.dll` assembly name as the core's `Markdig` 1.3.2. Two package ids
producing one assembly name in one graph is an `MSB3277` assembly-conflict warning,
which `TreatWarningsAsErrors` turns into a build failure. The conformance test
therefore supplies its own 15-line `IngestionDocumentReader` rather than restoring
MEDI's reader, and `Markdig.Signed` never enters any graph in this repository.

### 4.3 The consumer's two calls

```csharp
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddOnnxEmbeddings()                  // SP2
    .AddVectorStore()                     // SP2
    .AddIngestion(migrationVersion: 10)   // SP3 — one version, one migration
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

Search is SP2's, unchanged:

```csharp
var chunks = store.GetDynamicCollection(
    "chunks", IngestionSchema.BuildDefinition(dimensions: 384, DistanceFunction.CosineDistance, fullTextIndexed: true));

await foreach (var hit in chunks.HybridSearchAsync("water damage", ["roof", "leak"], top: 10))
{
    var chunk = IngestedChunk.FromRecord(hit.Record);
    Console.WriteLine($"{chunk.DocumentId} §{chunk.Breadcrumb} {hit.Score:F4}");
}
```

`AddOnnxIngestion` is idempotent and order-independent relative to
`AddOnnxEmbeddings`, because everything it registers resolves lazily.

`IngestionSource.Folder` is a **filesystem-path** factory and is the desktop and
app-private-storage shape. It is not what a mobile app that lets the user pick a
location uses — see §11.3, which is a goal-level requirement, not a footnote.

## 5. Amendments to sub-projects 1 and 2

**One edit, additive, in the same PR as SP3's first commit. There are no others.**

**5.1 `Qavren.Edge.Core` — `EdgeErrorCode` gains the 6000–6299 range.** Pure
append, exactly as SP2 appended 5000–5299. SP1's 1001–4001 and SP2's 5001–5213 are
untouched and nothing is renumbered. The full list is §13.1.
`foundation/docs/errors.md` gains one `## 6000–6299 — sub-project 3` section and
one heading per code, because `EdgeException.HelpLink` addresses it by number.

`EdgeStartupOrder` is **not** changed: SP3's order 400 already sits between SP2's
`VectorSchema = 300` and `ConsumerDefault = 1000`, and is published from SP3's own
`EdgeIngestionStartupOrder`. `EdgeEventIds` is **not** changed: SP3 publishes
`EdgeIngestionEventIds` in the 900–999 range, SP1 holding 100–500 and SP2 600–899.
`IEdgeTokenizer` is **not** changed — §4.1 explains why the satellite exists
instead. No csproj of SP1's or SP2's moves.

**5.2 Things SP3 deliberately does not ask for.**

- **No startup-safe database open.** SP2 documents, on `AddVectorCollection`'s own
  XML doc, that an order-300 startup task calling `OpenConnectionAsync` deadlocks
  against `IEdgeHost.EnsureStartedAsync`. SP3 routes around it entirely: all DDL
  goes through SP1's order-100 migrator, and `RunAsync` awaits `EnsureStartedAsync`
  from **outside** the startup sequence. SP3's one startup task (order 400) opens
  no connection and acquires no ONNX session. The deadlock stays filed as an SP2
  issue; it is not an SP3 dependency.
- **No in-place `vec0` UPDATE.** sqlite-vec 0.1.9's `vec0Update` does implement a
  plain `UPDATE`, and SP2's comment at `EdgeVectorStoreCollection.cs:302–306` is
  imprecise in conflating that with `INSERT OR REPLACE`. It is also irrelevant
  here: content-addressed keys mean a changed chunk is a *different* chunk, so SP3
  never needs to update a vector in place. The only case that would want it —
  identical text, new model — changes the vector space and is correctly answered by
  recreating the collection.
- **No transaction overload on SP2's collection.** Per-document atomicity across
  vectors and state would need an `UpsertAsync`/`DeleteAsync` overload accepting an
  ambient `SqliteTransaction`. SP3 does not need it (§9.5) and does not ask for it
  on an unmerged branch. File it as an SP2 issue; do not let a later revision
  quietly adopt a single-transaction writer, because it does not compile against
  today's SP2.
- **No change to SP2's lifecycle observer — SP3 merges its own FTS5 sidecar
  instead.** An earlier draft asserted that SP2's `VectorDataLifecycleObserver`
  already covers SP3's sidecar. It does not, and the reason is stated in SP2's own
  source. The observer merges only *registered* collections —
  `Internal/VectorDataLifecycleObserver.cs:61` is
  `var mergeable = _registry.Registrations.Where(r => r.FullTextTable is not null)` —
  and the only writer to that registry is `AddMigrationCore`
  (`EdgeVectorDataBuilderExtensions.cs:242`), reached only from
  `AddVectorCollectionMigration`. SP2 documents the consequence verbatim at
  `EdgeVectorCollectionMigration.cs:9–12`: "a collection reached only through
  `GetCollection` at runtime has no registration and appears in neither, which is why
  `AddVectorCollectionMigration` is the recommended path." SP3 emits its **own**
  migration (§11.1 step 3) and takes the collection at runtime through
  `GetDynamicCollection`, and `EdgeVectorCollectionRegistry` is `internal`, so SP3
  can neither be registered nor register itself.

  Two consequences, both owned here rather than pushed onto SP2. SP3's own
  lifecycle observer runs the bounded incremental merge for SP3's collection (§12),
  with the same budget shape SP2 uses. And SP3's collection does **not** appear in
  SP2's diagnostics report, so SP3's diagnostics contributor publishes its own
  `vectorTable` and `fullTextTable` names (§12) — otherwise the tables would be
  invisible to every report in the suite. Adopting SP2's migration path instead
  would fix both, and it is exactly the two-version fallback verification item 1
  already carries; this is its **second** reason to be taken, and the plan weighs
  them together.

**5.3 SP2 facts SP3 depends on.** A rebase checks exactly these, and nothing else:
`EdgeVectorStore.GetDynamicCollection(name, definition)`;
`EdgeVectorStoreCollection.UpsertAsync(IEnumerable<TRecord>)` and
`DeleteAsync(IEnumerable<TKey>)`; `EdgeVectorSchema`'s `DataTable`, `KeyColumn`,
`RowIdColumn`, `BuildCreateSql()`; `EdgeCollectionModelBuilder`;
`EdgeVectorStoreCollectionOptions`; `EdgeVectorData.QueryGeneratorServiceKey`;
`IEdgeTokenizer.{CountTokens, IndexByTokenCount, MaxSequenceLength}`;
`IEdgeTokenizerProvider.GetAsync`; `EmbeddingPreset.{Id, Dimensions,
MaxSequenceLength, Pooling, Normalize, DocumentPrefix, QueryPrefix}` (read only by
`Qavren.Edge.Ingestion.Onnx`, §11.2); `EmbeddingPresets.MiniLmL6V2Int8`;
`IEdgeResourceMonitor.{Read, LastPressure}` and `EdgeResourceSnapshot`;
`EdgeAiStartupOrder` and `EdgeAiEventIds` (read only, to stay out of their ranges).
All public, all pinned by SP2's spec §§6–8.

## 6. Document model

Extraction produces one `ExtractedDocument`: a single normalised text buffer plus
blocks that carry **offsets into it**. Chunkers consume that and never re-parse.
The offsets are what make golden tests legible, what make the repair mechanic
possible, and what let a search hit point back at a byte range in the source.

```
source file ─► IDocumentExtractor ─► ExtractedDocument { Text, Blocks[], Metadata }
                                          │
                                          ▼
                                     IChunker ─► ChunkDraft[] (ordinal, text, headingPath, span, tokens)
                                          │
                                          ▼
                              hash ─► diff ─► embed ─► write
```

`ExtractedDocument.Text` is CRLF and lone-CR normalised to LF, NFC-normalised, and
BOM-stripped. A `DocumentBlock` is `(Kind, Start, End, HeadingLevel?, PageNumber?)`
with `[Start, End)` half-open into `Text`.

`DocumentBlockKind` deliberately uses MEDI's vocabulary so the shim is lossless in
both directions: `Paragraph`, `Heading`, `ListItem`, `TableRow`, `Code`, `Caption`,
`Quote`, `Footer`. There is **no image kind** — suite decision 9 says images later,
and a public payload nothing reads is surface without capability. The MEDI shim
converts `IngestionDocumentImage` to nothing and logs event 917 once per document.

Extractors stream internally and append into the text buffer as they go — PDF
page-at-a-time, DOCX through `OpenXmlPartReader` above a threshold, text and
Markdown streamed off a `StreamReader` in fixed char blocks into the text
buffer. `ReadLine` is deliberately not used: it drops the terminator, so a line
loop cannot reproduce the raw decode the `NormalizeText = false` offset path is
defined against — it would rewrite every CRLF to LF and move every offset. The
buffer is the one thing held
whole, which is why `MaxDocumentBytes` exists (§9.1) and why verification item 5
measures peak RSS on a device rather than asserting it here.

State persistence for checkpoints is document-level only. Three tables, all
created by SP3's single migration, all `WITHOUT ROWID` where the key is composite:

| Table | Key | Columns |
|---|---|---|
| `qedge_ingest_document` | `(collection, source_id, document_id)` | `content_hash BLOB(16)`, `recipe_hash BLOB(16)`, `size_bytes`, `modified_utc`, `extractor_id`, `chunk_count`, `status`, `error`, `last_run_id`, `updated_utc` |
| `qedge_ingest_run` | `run_id` | `collection`, `source_id`, `recipe_hash`, `started_utc`, `finished_utc`, `outcome`, `suspend_reason`, `documents_indexed`, `chunks_added`, `chunks_removed`, `error` |
| `qedge_ingest_meta` | `key` | `value` — holds `schema_version` and `hash_algorithm` (`"xxh128-v1"`) |

`qedge_ingest_run` is trimmed to `RunHistoryLimit` (20) rows at the end of each
run. A document row whose `status` is `Failed` or `NoTextLayer` is *kept*, so the
next run skips it on an unchanged hash instead of retrying a broken file forever —
and a recipe bump retries it automatically, which is the correct retry trigger.

## 7. Extractors

### 7.1 The contract and the registry

```csharp
public interface IDocumentExtractor
{
    string Id { get; }                              // stable; feeds the recipe hash
    int Version { get; }                            // bump when output changes for identical input
    IReadOnlyList<string> Extensions { get; }       // lower-case, dotted
    IReadOnlyList<string> MediaTypes { get; }
    bool CanExtract(DocumentSourceItem item);
    ValueTask<ExtractedDocument> ExtractAsync(
        DocumentSourceItem item, ExtractionContext context, CancellationToken cancellationToken);
}
```

`IDocumentExtractorRegistry` resolves by media type first, then by extension.
Consumer-registered extractors sit ahead of the built-ins, so `.md` can be
overridden. Two extractors sharing an `Id` throw
`IngestionDuplicateExtractorId` (6004) at registration. No match is
`ExtractorNotFound` (6101), reported per document — and when the extension is
`.pdf` or `.docx` the remediation names `AddPdfExtractor()` / `AddDocxExtractor()`
by name, because that is the mistake a first-time consumer actually makes.

**Only the selected extractor's `"{Id}:{Version}"` enters the recipe hash**, not
the fingerprint of the whole registered set. Hashing the set would re-index an
entire Markdown corpus because the PDF extractor's version was bumped, which on a
battery is not a defensible trade.

Every extractor opens the source through `DocumentSourceItem.OpenAsync`.
`File.ReadAllBytes` appears nowhere in SP3, and a test asserts it.

**Seekability is a contract, not an assumption.** `OpenAsync` is a consumer-supplied
delegate — `IngestionSource.Single` and `IngestionSource.Items` both take one — so
SP3 cannot simply declare that it returns a `FileStream`. The contract, stated on
`DocumentSourceItem.OpenAsync`'s XML doc and enforced at runtime:

- The delegate **SHOULD** return a seekable stream positioned at 0, and **MUST** be
  re-openable: the pipeline calls it twice per document (§9.4 — once to hash, once to
  extract) so a hash match can skip extraction entirely.
- `IngestionSource.Folder` and `.Files` return
  `new FileStream(path, …, useAsync: true, bufferSize: 64 * 1024)`, which is seekable.
- `IngestionSource.Items` wraps a consumer-supplied factory: if the returned stream
  reports `CanSeek == false`, SP3 buffers it **once** into a `MemoryStream`, but only
  while the declared or counted length is at or below `ExtractionOptions.NonSeekableBufferLimitBytes`
  (4 MiB default). Above that it raises `IngestionDocumentUnreadable` (6053) whose
  remediation says to copy the content to a file or a seekable stream first.
- The reason the limit is low and the failure is loud: a non-seekable stream handed
  to `PdfDocument.Open(Stream)` makes **PdfPig itself** copy the whole PDF into a
  `MemoryStream`, which is exactly the LOH allocation §7.4's `Open(Stream)` rule
  exists to prevent. Silently doing it on SP3's side for a 200 MB scan would be the
  same bug wearing our name.

An extractor may therefore assume `CanSeek` and a re-openable source, and a test
asserts both that a non-seekable small stream is buffered and that a non-seekable
oversized one raises 6053 rather than being read.

### 7.2 Plain text

`PlainTextExtractor` (`Id = "text"`, `Version = 1`, `.txt .log .csv .text`,
`text/plain`).

Encoding resolves in three layers, no third-party dependency:

1. **BOM.** `StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true)`
   recognises UTF-8, UTF-16 LE/BE and UTF-32 LE/BE from the first four bytes.
2. **Strict UTF-8 probe.** `new UTF8Encoding(false, throwOnInvalidBytes: true)` over
   the first 64 KiB.
3. **Fallback.** `Encoding.Latin1` — byte-preserving and in the BCL. Logged as
   event 914. `PlainTextExtractorOptions.StrictUtf8 = true` raises
   `DocumentEncodingUndecodable` (6106) instead.

`UTF.Unknown` is **not** taken: it is MPL-1.1, the only non-MIT candidate in the
text stack, and it drags `System.Text.Encoding.CodePages`' full legacy code-page
tables into a mobile app for a case a notes or document corpus essentially never
hits. A consumer who genuinely needs legacy CJK detection writes an
`IDocumentExtractor`.

Blocks are `Paragraph`s split on blank lines.

### 7.3 Markdown

`MarkdownExtractor` (`Id = "markdown"`, `Version = 1`, `.md .markdown`,
`text/markdown`), over **Markdig 1.3.2** (BSD-2-Clause, 1.2 MB, net10.0 TFM, no
dependencies on net10.0).

Pipeline is built explicitly — `UsePipeTables().UseYamlFrontMatter().UsePreciseSourceLocation()`
— never `UseAdvancedExtensions()`, which pulls in roughly eighteen. Blocks come
from the top-level `MarkdownDocument` children, except `Table` and `ListBlock`,
which are descended one level so a table yields one `TableRow` block per row and
a list one `ListItem` block per top-level item; every `MarkdownObject` carries a
`SourceSpan`, so a block's `[Start, End)` is the verbatim source range and chunk
text is a source substring, not a lossy re-render. Fences, tables and links
survive intact.

Markdig earns its place precisely because it is a parser: a `## ` line inside a
fenced or indented code block is a code line, not a heading. Hand-rolling a
fence-aware line scanner to avoid one BSD-2 line item would reintroduce exactly
the bug class the prior art shipped, and would then need its own golden suite.

**Markdig's trim posture, stated because it is the only unannotated dependency in
the *core*.** Markdig sets neither `IsTrimmable` nor `IsAotCompatible`: its repo has
no root `Directory.Build.props`, and `Markdig.targets` — which carries the whole
packaging property group — declares neither. PdfPig and OpenXml both do declare
`IsTrimmable`, and both are opt-in satellites; Markdig is in every consumer's graph,
so it is the dependency most likely to surface a trim warning and the one that
reaches the most people.

It is nevertheless effectively reflection-free. The only `System.Reflection` use in
the library is `typeof(Markdown).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()`
behind the `Markdown.Version` property, and `MarkdownExtensions.Configure(string)` —
the string-driven pipeline configuration used by `SelfPipeline` — is a hardcoded
switch over extension names, not a lookup; `MarkdownExtensions.Use<TExtension>()`
carries a `new()` constraint. SP3 touches none of the three: the pipeline is built
from typed `Use*()` calls, `Markdown.Version` is never read, and `SelfPipeline` is
never enabled.

So the expectation is warnings-free, but it is an expectation and not an annotation.
Verification item 4 measures it **for the core**, not only the satellites, and
`trim-smoke`'s one-document run is a Markdown document precisely so the Markdig path
is the one under the trimmer. Any warning that does appear is recorded in the
README, never suppressed.

YAML front matter becomes `ExtractedDocument.Metadata`; no key is promoted to a
column, and `MarkdownExtractorOptions.PromoteFrontMatterKeys` is empty by default.
Setext headings are headings. Fenced code becomes `Code` blocks rather than being
dropped.

### 7.4 PDF

`PdfTextExtractor` (`Id = "pdf"`, `Version = 1`), over **PdfPig 0.1.16**
(Apache-2.0, published 2026-08-22).

PdfPig is the only candidate that runs everywhere SP3 ships: pure managed, zero
native binaries and no `runtimes/` folder at all, `IsTrimmable` for net6.0+ and
`IsAotCompatible` for net8.0+. Every alternative fails on a hard constraint —
Docnet.Core 2.6.0 ships no android, ios or maccatalyst natives and is three years
stale; PDFsharp 6.2.4 has literally no text-extraction member; iText 9.7.0 is
AGPL; Syncfusion and Telerik are commercial. The platform fast paths
(`PdfKit.PdfDocument.Text`, `PdfRenderer.Page.TextContents` at API 35+) are
deliberately **not** taken: two extra code paths, two extra test matrices, an
unverified SDK-extension floor on Android 30–34, and Windows has no first-party
extractor anyway, so PdfPig would remain the fallback everywhere.

```csharp
public sealed class PdfExtractorOptions
{
    public PdfReadingOrderMode ReadingOrder { get; set; } = PdfReadingOrderMode.ContentOrder;
    public bool SkipMissingFonts { get; set; } = true;
    public bool UseActualText { get; set; } = true;
    public bool UseLenientParsing { get; set; } = true;
    public IList<string> Passwords { get; }
    public bool JoinHyphenatedLineBreaks { get; set; } = true;
    /// <summary>Cumulative parse budget for one document, checked BETWEEN pages. Not an abort. 20 s.</summary>
    public TimeSpan PageBudget { get; set; } = TimeSpan.FromSeconds(20);
    public int MaxStackDepth { get; set; } = 50;
}
```

Four rules are load-bearing:

- **Always `PdfDocument.Open(Stream)`, never `Open(string)`.** The path overload
  calls `File.ReadAllBytes`, so a 50 MB scanned PDF becomes a 50 MB LOH allocation
  on a device that jetsams. The `Stream` overload wraps a seekable stream in
  `StreamInputBytes` and does not buffer. A test asserts the call shape.
- **`GetPages()` is lazy and stays lazy.** One page's `Letter`s are live at a
  time; the page's blocks are appended to the text buffer and the page is released.
- **`SkipMissingFonts = true` by default.** On a font-name miss PdfPig otherwise
  `File.ReadAllBytes` + parses the name table of every file in the system font
  directory. On Android that is a multi-second stall on the first such PDF.
  Verification item 6 measures what it costs in extraction quality.
- **`PageBudget` is a between-pages watchdog, and it is honestly not a timeout.**
  PdfPig's per-page surface is fully synchronous and accepts no `CancellationToken`:
  `doc.GetPages()`, `page.Letters`, `ContentOrderTextExtractor.GetText(page)` and
  `NearestNeighbourWordExtractor` all run to completion or not at all. Abandoning a
  wedged page would need a worker thread, and abandoning a thread mid-parse leaves
  the `PdfDocument` and its `StreamInputBytes` in an undefined state with a
  half-consumed stream — so SP3 does not do it, and does not pretend to.

  What `PageBudget` actually does: the extractor stamps a stopwatch at the start of
  each page and checks the **cumulative** elapsed time at each page boundary. When it
  is exceeded, extraction stops there; every page already parsed is kept, the document
  is recorded `Failed` with `DocumentPageBudgetExceeded` (6107) naming the page number
  reached, and event 924 logs the count. Cancellation and the lifecycle stop flag are
  checked at the same boundary.

  So it bounds the realistic case — a 400-page PDF of individually reasonable but
  cumulatively slow pages eating a run that was given twenty seconds — and it does
  **not** defend against a single page that never returns. Nothing in-process can,
  and the only real defence there is the consumer's own process-level budget. That
  limitation is stated in the package README rather than papered over.

`ReadingOrderMode.ContentOrder` uses `ContentOrderTextExtractor.GetText` with
`SeparateParagraphsWithDoubleNewline`, `ReplaceWhitespaceWithSpace` and
`NegativeGapAsWhitespace` on. `Layout` opts into
`NearestNeighbourWordExtractor` → `DocstrumBoundingBoxes` →
`UnsupervisedReadingOrderDetector` for multi-column documents; it is materially
more expensive per page and its options carry a `MaxDegreeOfParallelism` nobody
wants spinning up on a phone, so it is not the default.

`UglyToad.PdfPig.DocumentLayoutAnalysis.Export` is never referenced: its
Alto/PageXml exporters use `XmlSerializer` and carry `RequiresUnreferencedCode`,
and they are the only trim hazard in the package.

**Scanned pages are an outcome, not an error.** `page.Letters.Count == 0 &&
page.NumberOfImages > 0` marks a page as having no text layer; a document with no
text-layer page at all is recorded `IngestionDocumentStatus.NoTextLayer` (6105),
event 906, with its content hash stored so it is not re-parsed every run. Silently
indexing an empty document is worse than recording that we looked.

### 7.5 DOCX

`DocxTextExtractor` (`Id = "docx"`, `Version = 1`), over **DocumentFormat.OpenXml
3.5.1** (MIT, net10.0 TFM, `IsTrimmable` for net6.0+, no `Activator.CreateInstance`
in shipping source; 3.4.1 explicitly cut AOT size by removing the generic builder
from element metadata creation). `OpenXmlValidator` is never called.

```csharp
public sealed class DocxExtractorOptions
{
    public bool ExcludeHeadersAndFooters { get; set; } = true;
    public bool IncludeTextBoxes { get; set; } = true;      // w:txbxContent, routinely missed
    public bool IncludeNotes { get; set; } = true;
    public bool IncludeTables { get; set; } = true;
    /// <summary>Above this, switch from the part DOM to OpenXmlPartReader (SAX). 8 MiB.</summary>
    public long StreamingThresholdBytes { get; set; } = 8L * 1024 * 1024;
}
```

**Heading level resolves from the outline level, never from the style name.** In
priority order: `paragraph.ParagraphProperties?.OutlineLevel?.Val` (`w:outlineLvl`,
0–9 where 9 means "no level"), then `ParagraphStyleId?.Val` resolved against the
styles part to `Style.StyleParagraphProperties.OutlineLevel`. Regexing the style
id for `Heading{n}` breaks on every localised template — "Titre 1", "Überschrift 1"
— and a `pStyle` naming a style absent from `styles.xml` must degrade to a
paragraph, not throw. Two fixtures assert both (§14).

**Above `StreamingThresholdBytes` the extractor uses `OpenXmlPartReader`, not the
DOM.** A 50 MB DOCX materialised as `MainDocumentPart.Document.Body` is precisely
the allocation spike that kills an iOS app; refusing the document instead would be
worse, because a 20 MB Word file with embedded images is something a user actually
owns. Event 913 records which mode ran, so the choice is observable.

Runs are merged: one sentence split across five `w:r` with rsid noise is one
paragraph block. Tables emit `TableRow` blocks, pipe-joined, header row first.
Page headers and footers are excluded by default — they repeat on every page and
poison embeddings.

## 8. Chunkers

### 8.1 The token budget

```csharp
public interface IChunkTokenizer
{
    string Id { get; }                    // e.g. "wordpiece:30522:minilm-l6-v2-int8"
    int MaxSequenceLength { get; }        // the model ceiling, special tokens INCLUDED
    int SpecialTokenOverhead { get; }     // tokens the encoder adds that CountTokens does not report
    int CountTokens(ReadOnlySpan<char> text);

    /// <summary>
    /// The largest index i such that text[0..i) costs at most maxTokens tokens.
    /// CONTRACT: i indexes into <paramref name="text"/> AS PASSED — never into a
    /// normalised copy of it. See "The offset contract" below; this is load-bearing.
    /// </summary>
    int IndexByTokenCount(string text, int maxTokens, out int tokenCount);
}
```

`SpecialTokenOverhead` is a property of the **tokenizer**, not of the chunk
options, because the asymmetry that makes it necessary is a tokenizer fact:
`BertTokenizer` overrides `EncodeToIds` with `addSpecialTokens` defaulting to
**true**, but does **not** override `CountTokens`, which resolves to
`WordPieceTokenizer.CountTokens` and has no special-token notion. A chunk sized to
exactly 512 by `CountTokens` therefore encodes to 514 ids and is silently
truncated. `EdgeChunkTokenizer` reports `2`; `MlChunkTokenizer` takes it as a
constructor argument.

This is the single highest-consequence number in the package, and it is
**verification item 2**: it must be measured against the shipped
`Microsoft.ML.Tokenizers` 2.0.0 assembly *before any golden file is written*,
because every expected boundary in every committed golden moves if it is wrong.

**Budget resolution happens in the order-400 startup task, not at container build,
and that is forced by the dependency graph.** `SpecialTokenOverhead` is a member of
`IChunkTokenizer`, and the prefix reserve below needs
`IChunkTokenizer.CountTokens`. No tokenizer exists at builder time: `UseChunkTokenizer`
takes a `Func<IServiceProvider, IChunkTokenizer>` against a container that has not
been built, and `AddOnnxIngestion()` is chained *after* `AddIngestion` in §4.3 and
resolves SP2's `IEdgeTokenizerProvider` lazily. So the resolver's signature takes
the tokenizer:

```csharp
public ResolvedChunkOptions Resolve(ChunkModelProfile model, IChunkTokenizer tokenizer);
```

`AddIngestion` still validates at **registration** everything that does not need a
tokenizer — an explicitly-set `OverlapTokens >= MaxTokens / 2` or
`MinTokens >= MaxTokens`, a non-positive `HeadingPathTokenBudget`, a non-positive
`WriteBatchSize` or `DeleteBatchSize`, a `SleepGraceBudget` over 2 s — and throws
`IngestionOptionsInvalid` (6005) there. Everything that needs the tokenizer is
`IngestionChunkBudgetInvalid` (6003) at **startup**, before the first document and
before any migration has been given a chance to matter. §13.3 tiers the two
accordingly. The resolved triple is computed once, cached on the pipeline, and is
what `IngestionRecipe.Chunking` carries.

| Knob | Default | On MiniLM-int8 (256) | On bge-small (512) |
|---|---|---|---|
| `MaxTokens` | `MaxSequenceLength − SpecialTokenOverhead − HeadingPathTokenBudget − DocumentPrefixTokens` | **222** | **478** |
| `HeadingPathTokenBudget` | 32 | 32 | 32 |
| `OverlapTokens` | `max(8, (MaxTokens * 15 / 100) / 8 * 8)` — integer arithmetic throughout | **32** | **64** |
| `MinTokens` | `max(16, MaxTokens / 8)` — integer division | **27** | **59** |

`DocumentPrefixTokens` is `tokenizer.CountTokens(Model.DocumentPrefix ?? "")`, and it
is **0 on both columns above** — neither MiniLM nor bge-small defines a document
prefix — which is why the two pinned triples are unaffected by it. It is not zero
everywhere: `nomic-embed-text-v1.5-int8` ships `DocumentPrefix = "search_document: "`,
and §8.3 explains why that reserve has to exist even though SP3 never writes the
prefix itself. A prefixed profile's triple is therefore **computed at startup, not
tabulated here** — tabulating it would pin a number that depends on a vocabulary.

Both rules **truncate**; neither rounds to nearest. Spelled out, because an earlier
draft of this table carried 72 in the bge column and that was wrong: `478 * 15 / 100`
is 71, `71 / 8 * 8` is **64**. The MiniLM column is 33 → 32 under either rule, which
is exactly why only the bge column exposed the inconsistency. `MinTokens` truncates
the same way — `478 / 8` is 59, not 60 — so one rule governs both lines. The two
resolved triples **222 / 32 / 27** and **478 / 64 / 59** are pinned by a unit test
(§14.1) and every committed golden is generated against them.

`Resolve` asserts
`MaxTokens + SpecialTokenOverhead + HeadingPathTokenBudget + DocumentPrefixTokens
<= Model.MaxSequenceLength`, `OverlapTokens < MaxTokens / 2` and
`MinTokens < MaxTokens`, and throws `IngestionChunkBudgetInvalid` (6003) **with the
arithmetic in the message** — at startup, not on the thousandth document. It also
asserts `tokenizer.MaxSequenceLength == Model.MaxSequenceLength`, because a
tokenizer and a profile that disagree about the ceiling make every number above a
guess; a mismatch is `ChunkTokenizerCeilingExceeded` (6153). MEDI's shipped defaults of 2000 /
500 are four to eight times what a 384-dim model can encode; SP3 never carries a
constant here.

The ONNX-free construction path is `EdgeTokenCounter.CreateWordPiece(vocabPath,
maxSequenceLength, lowerCase: true)` / `.FromTokenizer(Tokenizer, maxSequenceLength,
specialTokenOverhead)`.

**The offset contract, and why the two implementations reach it differently.**
Every `char_start` / `char_end` in §6's document model, every repair in §9.5 and
every number in every golden file is an index into `ExtractedDocument.Text`. So
`IChunkTokenizer.IndexByTokenCount` must return an index into the string it was
handed. `Microsoft.ML.Tokenizers`' `Tokenizer.GetIndexByTokenCount` only does that
when `considerNormalization: false`; at its **default of true** it returns an index
into the *normalised* string it hands back through the `out string? normalizedText`
parameter — and with `BertOptions.LowerCaseBeforeTokenization` and basic tokenization
both on by default, normalisation is not identity for non-ASCII input. An index taken
from the normalised string and applied to the original silently mislocates every
boundary after the first difference.

That matters here because SP2's forwarding does exactly that. Verified in source at
`embeddings/src/Qavren.Edge.Embeddings.Onnx/Internal/BertEdgeTokenizer.cs:137`:

```csharp
public int IndexByTokenCount(string text, int maxTokens, out int tokenCount)
    => _tokenizer.GetIndexByTokenCount(text, maxTokens, out _, out tokenCount);
```

`considerNormalization` is left at its default and `normalizedText` is discarded. SP2
uses the result only to truncate an input it is about to re-encode, where the
discrepancy is invisible; SP3 would persist it as a character offset, where it is not.
And §5.1's commitment stands: `IEdgeTokenizer` is not changed, because SP2 is
unmerged and a cross-sub-project signature edit is exactly what this design has
avoided everywhere else.

So the contract is met per implementation, and the seam is the reason that is possible:

- **`MlChunkTokenizer`** (the ONNX-free path) holds the real `Tokenizer` and
  derives `IndexByTokenCount` from `CountTokens` through a shared prefix search
  (`Internal.TokenIndexSearch`); `Tokenizer.GetIndexByTokenCount` is not used,
  because `considerNormalization: true` returns an index into the normalised
  string and `considerNormalization: false` makes the shipped `BertTokenizer`
  return `text.Length` with `tokenCount` 1 for every budget (measured against the
  real 30,522-entry vocabulary, 2026-09-11).
- **`EdgeChunkTokenizer`** (the ONNX satellite) has only `IEdgeTokenizer`, so it does
  **not** forward `IndexByTokenCount` at all. It derives the index itself from
  `CountTokens`, which SP2 forwards verbatim (`_tokenizer.CountTokens(text)`) and
  which takes a span rather than returning an index, so no normalised string can leak
  through it. The derivation is a bounded binary search: estimate a starting cut from
  a running chars-per-token ratio, snap every candidate to a grapheme-cluster boundary
  with `StringInfo`, and search for the largest prefix whose `CountTokens` is at most
  `maxTokens`. The search window is the estimate ± 25%, not the document, so it costs
  roughly 8–12 `CountTokens` calls per cut over a bounded span — not an extra pass
  over the text.

The cost difference between the two paths is real and is **verification item 11**,
which also carries the documented fallback: if the binary search measures badly on
device, the answer is an additive `IEdgeTokenizer.IndexByTokenCount` overload taking
`considerNormalization`, filed as an SP2 issue and taken only after SP2 merges. A
property test in §14.1 asserts the contract directly on both implementations — for
NFD text, a ZWJ sequence and a Turkish dotted I, `text.Substring(0, returnedIndex)`
round-trips and the offsets land where the goldens say.

### 8.2 The three chunkers

All three are **synchronous** `IEnumerable<ChunkDraft>` over an in-memory string.
Chunking is CPU with no I/O; a sync interface makes golden and property tests
trivial and costs nothing.

**`TokenWindowChunker`** — the terminal fallback every other chunker delegates to.
Forward cut via `IndexByTokenCount`; the next window is seeded by counting
`OverlapTokens` back from the emitted chunk's end. Each candidate cut is nudged
backwards within a 15% look-back window to the nearest of: a paragraph break, a
sentence terminator (`. ! ? … 。 ！ ？` followed by whitespace or EOF, with an
abbreviation stop-list) then whitespace. Only if none exists in that window does it
cut mid-word. Every cut is then snapped forward to a grapheme-cluster boundary via
`StringInfo`/`Rune`, so no chunk ever ends mid-surrogate or mid-combining-mark.
MEDI's equivalent backs up to the last `\n` and is explicitly not sentence-aware.

**`PlainChunker`** — accumulates `Paragraph` blocks while they fit; an over-budget
block goes to the token window; a block under `MinTokens` merges forward.

**`MarkdownHeadingChunker`** — a `string?[7]` heading stack, cleared below the
current level on each new heading. Splits at `SplitHeadingLevels` (H1–H3 by
default), so H4+ stay inside their parent section, which is how documents are
actually written. `ChunkDraft.HeadingPath` is an ordered `IReadOnlyList<string>`
**in memory**; what is **stored** is the rendered breadcrumb, one `string` column
(§8.3 says exactly why, and what the round-trip costs). Headings are excluded from
the chunk body and prepended to the *embed* text, never duplicated into the stored text.
Content before the first heading becomes a preamble chunk when `IncludePreamble`
(default true) — the prior art dropped it and lost every document's lede. A
section over budget falls to the token window **with overlap preserved and the same
heading path on every piece**; MEDI's structure-aware chunkers produce zero overlap
because their shared splitter never reads `OverlapTokens`, and that is the defect
this design exists not to inherit. Sections under `MinTokens` merge into the next
sibling (event 911). Tables split row-wise with the header row re-emitted on every
piece.

A breadcrumb over `HeadingPathTokenBudget` is truncated **from the left** — the
deepest headings are the most specific — and logged as event 910. It is never
allowed to eat the content budget, which is how MEDI's splitter ends up throwing.

**Oversized-unit policy** is `ChunkOptions.Overflow`:
`Split` (default, the cascade above), `Truncate` (cut to budget, log 921) or
`Throw` (`ChunkContextTooLong`, 6152). That governs an *input* unit bigger than the
budget. It is a different thing from the invariant in §13: a chunker that *emits* a
chunk over `MaxTokens` is an SP3 bug and always throws `ChunkExceedsTokenBudget`
(6151) in every configuration, because silent truncation at the encoder is the
exact failure this package exists to prevent.

### 8.3 Chunk metadata

Every chunk carries a superset of what MEDI's `IngestionChunk<T>` can hold, and
every field is a real column, not a JSON blob:

`source_id`, `document_id`, `ordinal`, `text`, `heading_path`, `content_hash`,
`char_start`, `char_end`, `token_count`, `page`, `block_kind`, `extractor_id`,
`media_type`, `created_utc`, plus the `embedding` vector and the `key`. Their CLR
types are pinned by `IngestedChunk` in §11 and every one of them is in SP2's
`SqliteTypeMap.SupportedDataTypes` — "int, long, short, bool, float, double, string,
Guid, DateTime, DateTimeOffset, DateOnly, TimeOnly, byte[]". `source_id` and
`document_id` are `IsIndexed`; `text` and `heading_path` are `IsFullTextIndexed`
when `FullTextIndexed` is on (default), because hybrid search is why SP2 built the
FTS5 sidecar.

**`heading_path` is a `string` column holding the rendered breadcrumb, and the
structural array does not survive the write.** That list has no array type to go
into: SP2's supported-type list above has exactly one collection type, `byte[]`,
and a `byte[]` breadcrumb could not be `IsFullTextIndexed`, which is half the point
of storing it. So the column is `TEXT` carrying
`string.Join(IngestionColumns.HeadingPathSeparator, path)` with the separator
`" › "` (U+203A between two spaces), and `IngestedChunk.HeadingPath` is an
`IReadOnlyList<string>` **reconstructed by splitting on that separator** when a
record is read back.

The split round-trips because the join is made safe rather than assumed safe: at
chunk time each heading is sanitised — any occurrence of the separator sequence is
collapsed to a single space, and control characters are stripped — before it enters
the path. Heading text is display text, so that is lossless in every way a reader
would notice and lossy in exactly the way that would otherwise corrupt a boundary.
A property test asserts `Split(Join(path)) == path` over generated headings that
deliberately contain `›`, and `IngestedChunk.Breadcrumb` exposes the stored string
verbatim for a consumer who wants it unsplit.

`IngestionColumns` publishes every storage name as a `const`, plus
`HeadingPathSeparator`, so the same constants build the collection definition, the
record mapper and any SQL a consumer writes. That is the whole compile-time safety
story for a dynamic record, and it is backed by a round-trip test over every
property.

**The embed text, and the prefix SP3 must reserve but must not write.** The embed
text is `breadcrumb + "\n\n" + text` when `PrependHeadingPath`, and `text` alone
otherwise. It does **not** carry `Model.DocumentPrefix`, because the generator
already applies it — verified at
`embeddings/src/Qavren.Edge.Embeddings.Onnx/OnnxEmbeddingGenerator.cs:417–422`:

```csharp
private string ApplyPrefix(string value)
{
    var text = value ?? string.Empty;
    var prefix = _inputKind == EmbeddingInputKind.Query ? _preset.QueryPrefix : _preset.DocumentPrefix;
    return string.IsNullOrEmpty(prefix) ? text : prefix + text;
}
```

with `OnnxEmbeddingOptions.DefaultInputKind = Document`. An SP3 that prefixed as
well would send every nomic chunk through the encoder as
`"search_document: search_document: …"`, and would bake that duplication into the
chunk content hash, so the damage would persist in the index rather than staying in
one call. Hence the three rules that make the prefix work correctly:

- SP3 **never** writes `DocumentPrefix` into the embed text.
- SP3 **reserves its token cost** in `MaxTokens` (§8.1). The generator's prefix is
  real tokens against a real ceiling; not reserving them is how a 478-token nomic
  chunk becomes a 482-token encode that silently truncates — the exact failure
  §1 decision 3 calls structurally unreachable, arriving through the one door SP3
  does not control.
- `DocumentPrefix` is in the **recipe hash** (§9.2), so changing it re-indexes,
  even though the chunk content hash never sees it.

`Model.QueryPrefix` is **never** applied to a stored chunk at all: bge-small's
"Represent this sentence for searching relevant passages: " instruction belongs on
the query side, and putting it on documents is a silent quality bug. It is carried
on `ChunkModelProfile` for exactly one reason — it is part of the recipe, so
swapping to a model whose query-side instruction differs invalidates the index
rather than mixing two retrieval conventions in one collection.

## 9. Content hash and incremental re-index

### 9.1 Hashing

**xxHash128 via `System.IO.Hashing` 10.0.12**, stored as `BLOB(16)`. Nothing here
is adversarial, 128 bits is ample for an on-device corpus, and SHA-256 would double
both the stored key width and the CPU for no benefit. `XxHash128.HashToUInt128` for
one-shot, `Append` / `GetCurrentHashAsUInt128` for a streamed file. Persisted bytes
are **big-endian**, which is how `System.IO.Hashing` writes them; `ContentHash.ToHex`
is 32 lowercase hex characters. `qedge_ingest_meta.hash_algorithm` stores
`"xxh128-v1"`, and a different stored value raises
`IngestionHashAlgorithmMismatch` (6202) rather than mis-diffing.

`MaxDocumentBytes` is 32 MiB by default and is a **source-byte** ceiling. A document
over it is recorded `Failed` with `IngestionDocumentTooLarge` (6052) and a
remediation, not attempted: on iOS a 200 MB PDF is a jetsam, and a jetsam loses the
run as well as the document.

It is enforced in **two** places, because `DocumentSourceItem.SizeBytes` is
`long?` — `IngestionSource.Single` and `.Items` both take it as an optional argument,
and a stream-backed source frequently cannot know a length in advance:

1. **When the source declares a size**, the gate runs at enumeration and the file is
   never opened. This is the `Folder` and `Files` path, where `FileInfo.Length` is
   free.
2. **When `SizeBytes` is null**, the gate degrades to a **counted read during the
   hash pass** (§9.4 step 2). The hashing loop already reads the stream in 64 KiB
   buffers; it carries a running byte count and aborts the moment the count exceeds
   `MaxDocumentBytes`. At most `MaxDocumentBytes + 64 KiB` is ever read, and nothing
   is buffered — `XxHash128.Append` is streaming, so the partially-read content is
   discarded with the stream and never materialised.

On the degraded path the document is recorded `Failed` with 6052 exactly as on the
declared path, **but no content hash is written** — the hash of a truncated read
would be a lie, and storing it would make a later shrink of the file look unchanged.
The state row therefore keeps `content_hash` at its previous value (or none), so the
next run re-attempts the document. The extractor is never reached either way, so the
LOH allocation the ceiling exists to prevent does not happen on either path.

### 9.2 The recipe hash

A chunk's vector is a pure function of a tuple, and that tuple gets a hash:

```csharp
public sealed record IngestionRecipe(
    int SchemaVersion,          // const 1
    string ModelProfileId, int Dimensions, string Pooling,
    string? DocumentPrefix, string? QueryPrefix,
    string TokenizerId, int TokenizerMaxSequenceLength, int SpecialTokenOverhead,
    string ChunkerId, int ChunkerVersion,
    ResolvedChunkOptions Chunking,
    string ExtractorFingerprint,   // the SELECTED extractor, "pdf:1" — not the registry
    string DistanceFunction)
{
    public string Hash { get; }    // 32 lowercase hex, 0x1F-separated, order-stable
}
```

When a document's stored `recipe_hash` differs from the run's, that document is
dirty regardless of its content hash. Without this, changing `MaxTokens` from 222
to 128 leaves every old vector in place — which is precisely what MEDI's
`IncrementalIngestion` does. A recipe change logs event 915 once, with both hashes.

Because the extractor fingerprint is per-document rather than per-registry,
bumping `PdfTextExtractor.Version` dirties PDFs and leaves a Markdown corpus alone.

### 9.3 Chunk identity

```
key = xxHash128(sourceId ␟ documentId ␟ chunkContentHash ␟ duplicateOrdinal) → 32 hex
```

`chunkContentHash` is over the *embed* text **as SP3 composes it** — breadcrumb plus
text — which by §8.3 excludes `Model.DocumentPrefix`. The prefix is a property of the
model, not of the chunk, and it lives in the recipe hash instead; hashing it here as
well would make identity depend on a string SP3 never writes. `duplicateOrdinal` counts prior
occurrences of the same hash **within the same document**, which is what makes
identity unique for a document that legitimately repeats a paragraph. Ordinal is
**stored but is not part of identity** — that is exactly what makes the cheap
repair in §9.5 possible.

Pure content addressing breaks on repeated paragraphs. The prior art's
`documentId#index` breaks the moment anything is inserted, shifting every later id
and re-embedding the tail. This shape breaks on neither.

### 9.4 The diff

Per document, in order:

1. **Timestamp gate** (`SkipUnchangedByTimestamp`, **default `false`**). When
   enabled and `size_bytes`, `modified_utc` and `recipe_hash` all match: skip with
   zero file opens. Off by default because matching size and mtime is not evidence
   a file is unchanged — mtime-preserving copy tools, two-second filesystem
   granularity, restore-from-backup — and the hash gate below is already one
   sequential 64 KiB-buffered read.
2. **Hash gate.** Open once, stream-hash the bytes. If `content_hash` **and**
   `recipe_hash` both match and the status is terminal, stamp `last_run_id` and
   skip: no decode, no parse, no chunk, no embed. This is the path a no-op re-run
   takes for every document, and it is what makes "re-ingest performs zero embed
   calls" a testable claim rather than a hope.
3. **Extract, chunk, hash each chunk, assign duplicate ordinals and keys.**
4. **Read the stored set.** One direct SQL read through `IEdgeDatabase`:
   `SELECT "key","content_hash","ordinal","char_start","char_end" FROM "<data>"
   WHERE "source_id"=$s AND "document_id"=$d`, using the `document_id` index SP2
   creates for `IsIndexed`. Five columns, never the text.
5. **Set-difference** on `(content_hash, duplicateOrdinal)`: `added`, `removed`,
   `unchanged`. Among `unchanged`, any whose `ordinal`, `char_start` or `char_end`
   moved go to `repaired`.

### 9.5 The write protocol, and the transaction invariant

**Invariant, and the plan carries it as a test: no SP3 write nests an SP2
collection call inside an SP3 transaction.** `ExecuteInTransactionAsync` opens its
own connection; `UpsertAsync` and `DeleteAsync` each own a transaction. Nesting is
two connections contending for the WAL write lock. This is the single fact that
shapes the rest of this section, and it was verified in source, not assumed.

Per document, in this order:

```
a. for each window of `added`, WriteBatchSize (32) chunks:
     a1. SP3 calls IEmbeddingGenerator<string, Embedding<float>>.GenerateAsync(embedTexts)  — ONE call
     a2. SP3 calls collection.UpsertAsync(records carrying ReadOnlyMemory<float>)           — ONE call
b. delete `removed` keys in windows of DeleteBatchSize (500), one collection.DeleteAsync per window
c. repair `repaired` — SP3's own ExecuteInTransactionAsync, batched UPDATEs
d. write the document state row — SP3's own transaction, LAST
```

**Step a1 is SP3's call, not SP2's, and that is a deliberate reversal of the
obvious.** SP3's collection definition declares the vector property as
`ReadOnlyMemory<float>` rather than as a `string` source, so
`CollectionModel.EmbeddingGenerationRequired` is false and
`EdgeVectorStoreCollection.ResolveVectorsAsync` takes its pre-computed path. SP2
explicitly supports this shape — `EdgeVectorDataBuilderExtensions.cs:275` carries
the comment "GetService, never GetRequiredService: a store used only with
pre-computed vectors needs…" — and string *search* is unaffected, because
`ResolveSearchVectorAsync` reaches the store's `QueryEmbeddingGenerator` before it
ever consults the model's dispatcher.

Four things depend on owning that call, and none of them is reachable if SP2 makes
it. `EmbedCalls` is a count of a1, exactly. `TokensEmbedded` is
`GeneratedEmbeddings.Usage?.InputTokenCount` when the generator publishes it, and the
summed `IChunkTokenizer.CountTokens` over the window's embed texts when it does not —
which is a number SP3 has already computed for the budget, so the fallback costs
nothing and the diagnostics key `tokensEmbeddedSource` says which one is in force.
`IngestionBudget.MaxTokens` meters against that same number. And **6206 and 6207 are
separated by call site rather than by guesswork**: a throw out of a1 is
`IngestionEmbeddingFailed` (6206) and is retried once at half the batch; a throw out
of a2 is `IngestionWriteFailed` (6207) and is not. Folded into one `UpsertAsync` the
two are indistinguishable — SP2 wraps only the dimension-mismatch case in
`EdgeVectorStoreException` (`EdgeVectorStoreCollection.cs:556`), so a generator fault
arrives as whatever the generator threw — and §13.3's "halve the batch and retry the
window once" would be unimplementable, because there would be no way to retry the
embed without also retrying the write.

Three properties follow, and they are the whole durability story:

- **Additions land before removals.** A crash mid-document never deletes content
  that has not yet been replaced.
- **The state row is last, so a torn document is still dirty.** Its stored
  `content_hash` is still the old one, so the next run re-enters at step 4 of §9.4,
  finds the already-committed chunks by hash, and `added` is only the remainder.
  Resume costs nothing extra, needs no cursor column, no watermark and no second
  code path.
- **Orphans self-heal.** Chunks written for a version that never finished are
  found by the next run's `removed` set and deleted.

**What the repair costs.** The `UPDATE` is
`UPDATE "<data>" SET "ordinal"=?, "char_start"=?, "char_end"=? WHERE "key"=?`,
batched inside one transaction. It never recomputes a vector, and it never touches
the `vec0` row, because `vec0` is keyed on `_rowid` and an `UPDATE` does not move
it. It does, however, fire SP2's FTS5 `"<fts>_au"` trigger, which is
`AFTER UPDATE ON <data>` — unqualified — and performs an FTS5 delete-plus-insert
per row (verified in `foundation/src/Qavren.Edge.Sqlite/Fts/FtsTable.cs:166`). So
inserting one paragraph at the top of a 500-chunk document is **one embedding and
499 FTS5 delete/insert pairs**, not one embedding and nothing. That is still three
to four orders of magnitude cheaper than 499 embeddings, and it is the honest
number. Two consequences: `ChunkOptions`-adjacent option `RepairOrdinals` (default
`true`) exists so a consumer who does not need stable ordinals can turn it off; and
verification item 7 measures it. An SP1 amendment emitting
`AFTER UPDATE OF <fts columns> ON` would remove the cost entirely — filed as an SP1
issue, **not** an SP3 dependency.

Embedding happens strictly **outside** any transaction — a1 completes before a2 is
called, and a2 opens the only transaction in the pair. That is the same shape SP2's
own `UpsertAsync` establishes internally for a generator-backed property (resolve
every vector, then open); SP3 simply performs the resolve step itself. SP3 windows
at `WriteBatchSize` precisely because SP2 does not: an unwindowed 100k-chunk
document would hold 100k vectors — 150 MB at 384 dimensions — before its first
commit.

The generator SP3 resolves is the unkeyed `IEmbeddingGenerator<string,
Embedding<float>>`, or the one keyed on `IngestionOptions.StoreName` when that is
set, matching how SP2's store factory resolves its own. It is resolved with
`GetService`, and the absence of one is `IngestionEmbeddingGeneratorMissing` (6208)
at startup rather than a null-reference on the first document.

### 9.6 Pruning

Every document touched by a run is stamped with `last_run_id`. At the end of a
**`Completed`** run with `DeleteMissing` (default true), each state row under this
source whose `last_run_id` differs has its chunks deleted
(`DELETE FROM "<data>" WHERE "source_id"=$s AND "document_id"=$d`, triggers cascade
to `vec0` and FTS5) and its state row removed. Event 912.

**Pruning is skipped entirely on a `Suspended`, `Cancelled` or `Failed` run.** A
partial enumeration is not evidence a document is gone, and deleting live content
on a budget timeout would be the worst bug this package could ship.

That correctness rule has a consequence a consumer will hit: someone who only ever
runs under a `MaxDuration` budget never prunes. So it is not left to a README
sentence. `IIngestionPipeline.PruneAsync(sourceId, …)` is an explicit API that
performs a full enumeration and sweep with no write phase, and
`IngestionStatus.StaleDocumentCount` reports how many rows are currently
un-stamped, surfaced through diagnostics when counts are enabled (§12).

## 10. Pipeline

### 10.1 Stages

Sequential, per document, with bounded write windows *inside* a document:

```
enumerate → [size gate] → hash → [hash gate] → extract → chunk → hash chunks
          → diff → embed+upsert (windowed) → delete → repair → state row
```

There are no channels and no parallel stages. SP2's generator is
`MaxConcurrency = 1` and SQLite serialises writers, so the only real overlap is PDF
extraction against embedding — a modest desktop win that costs roughly a doubling
of peak RSS (channel capacity × batch) on the device most likely to be jetsammed,
plus non-deterministic failure ordering. Revisit on a measured benchmark, never on
a hunch.

### 10.2 Budget, throttle and suspension

Before each document, and before each write window inside a document, the runner
evaluates in order:

1. **Stop requested or caller cancelled** → `Cancelled` (or `Suspended` when the
   stop came from lifecycle). No exception; `ThrowOnCancellation` opts into one.
2. **Budget** — `MaxDuration`, `MaxDocuments`, `MaxChunks`, `MaxTokens` →
   `Suspended`, reason `"budget:duration"` and so on.
3. **`IIngestionThrottle.Evaluate`** → a `BatchSize`, a `Delay`, or a pause.

```csharp
public sealed record IngestionBudget
{
    public TimeSpan? MaxDuration { get; init; }
    public int? MaxDocuments { get; init; }
    public int? MaxChunks { get; init; }
    public long? MaxTokens { get; init; }

    public static IngestionBudget Unlimited { get; }
    /// <summary>~20 s. Fits inside an iOS BGAppRefreshTask slot.</summary>
    public static IngestionBudget Quick { get; }
    /// <summary>~5 min. Fits inside a WorkManager Worker's 10-minute cap.</summary>
    public static IngestionBudget Background { get; }
}
```

The core ships `FixedIngestionThrottle` (always the configured batch, never
pauses). `Qavren.Edge.Ingestion.Onnx` ships `ResourceMonitorThrottle` over SP2's
`IEdgeResourceMonitor.Read()`, which already exposes `AvailableMemoryBytes`,
`IsLowMemory`, `Thermal`, `ThermalHeadroom`, `IsLowPowerMode` and `LastPressure`.
First match wins, then clamp to `MinBatchSize`:

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

`EdgeThermalState.Unknown` and a null memory reading mean **no signal**, which is
`Proceed` — never "fine". That mirrors `EdgeResourceSnapshot`'s own documented
contract, and `IgnoreLowPowerMode` / `IgnoreThermalState` exist for the app that
disagrees about Low Power Mode. A pause is `Suspended` by default
(`ThrottlePauseBehavior.Suspend`); `Wait` delays and re-evaluates instead.

A suspension always lands on a committed boundary, so nothing is owed to the
caller beyond the reason:

```csharp
public sealed record IngestionRunResult(
    string RunId, string CollectionName, string SourceId,
    IngestionRunOutcome Outcome, string? SuspendReason,
    int DocumentsSeen, int DocumentsSkipped, int DocumentsIndexed,
    int DocumentsRemoved, int DocumentsFailed,
    int ChunksAdded, int ChunksRemoved, int ChunksUnchanged, int ChunksRepaired,
    long TokensEmbedded, int EmbedCalls, TimeSpan Duration, string RecipeHash,
    IReadOnlyList<IngestionDocumentResult> Documents, IngestionFailure? Failure)
{
    /// <summary>
    /// The per-document failures, projected from <see cref="Documents"/>. A computed view, not a
    /// second list: one source of truth, and `Failure` above stays what it says it is — the
    /// single RUN-level fault, null on a Completed or Suspended run.
    /// </summary>
    public IEnumerable<IngestionDocumentResult> Failures =>
        Documents.Where(d => d.Outcome is IngestionDocumentOutcome.Failed);
}
```

`EmbedCalls` and `TokensEmbedded` are defined by §9.5 step a1, which is SP3's own
`GenerateAsync` call: `EmbedCalls` counts those calls, and `TokensEmbedded` is the
generator's published `Usage.InputTokenCount` where there is one and SP3's own
`CountTokens` sum otherwise. `IngestionBudget.MaxTokens` meters against the same
number, so a budget and a report can never disagree about what a token is.

`SuspendReason` is one of `budget:duration`, `budget:documents`, `budget:chunks`,
`budget:tokens`, `memory:critical`, `memory:floor`, `memory:warn`,
`thermal:critical`, `thermal:serious`, `power:low`, `lifecycle:sleeping`,
`lifecycle:stopping`, `caller:stop`. A caller deciding whether to reschedule never
has to parse a log line.

### 10.3 Resume

There is no resume method and no resume code path. Calling `RunAsync` again
re-runs the diff: committed documents skip in microseconds on the hash gate, the
interrupted document redoes only its own remainder. One code path is one thing to
test, and it is the same path a first run takes.

### 10.4 Progress

`IProgress<IngestionProgress>` is invoked at most once per 100 ms plus once per
document boundary — never per chunk. A ten-thousand-chunk corpus would otherwise
marshal ten thousand callbacks onto a UI thread to report something whose unit of
meaning is the document. `IngestionProgress` carries counts, not a percentage:
enumeration is streaming, so the denominator is unknown until the run ends, and a
fabricated percentage is worse than none.

`IngestionProgress.Stage` is `Enumerating | Extracting | Chunking | Embedding |
Writing | Repairing | Pruning`, which is what a `BGContinuedProcessingTask`'s
`NSProgress` description or an Android `ForegroundInfo` notification shows.

## 11. Public API

Signatures below are `net10.0`, nullable-enabled, and carry **no**
`[RequiresDynamicCode]` or `[RequiresUnreferencedCode]` anywhere.

Seven types are declared **inline in the section that explains them** and are not
repeated here: `IDocumentExtractor` (§7.1), `PdfExtractorOptions` (§7.4),
`DocxExtractorOptions` (§7.5), `IChunkTokenizer` (§8.1), `IngestionRecipe` (§9.2),
`IngestionBudget` and `IngestionRunResult` (§10.2). `PdfTextExtractor` and
`DocxTextExtractor` are `IDocumentExtractor` implementations whose `Id` and
`Version` §7.4 and §7.5 pin, constructed by their builder calls below.
Everything else this document names is declared
below — including the throttle tri-state §10.2's table produces, the chunk options
§8.1's arithmetic resolves, and the two satellite builder calls §4.3 chains and
6101's remediation names.

```csharp
namespace Qavren.Edge.Ingestion;

public static class EdgeIngestionStartupOrder { public const int Validate = 400; }

public static class EdgeIngestionEventIds
{
    public const int RunStarted = 900;            public const int RunCompleted = 901;
    public const int RunSuspended = 902;          public const int RunFailed = 903;
    public const int DocumentSkipped = 904;       public const int DocumentIndexed = 905;
    public const int DocumentNoTextLayer = 906;   public const int DocumentFailed = 907;
    public const int DocumentUnsupported = 908;   public const int ExtractorSelected = 909;
    public const int HeadingPathTruncated = 910;  public const int ChunkMergedUp = 911;
    public const int StaleDocumentsPruned = 912;  public const int ExtractionStreamingMode = 913;
    public const int EncodingFallback = 914;      public const int RecipeChanged = 915;
    public const int OrdinalsRepaired = 916;      public const int MediImagesDropped = 917;
    public const int EmbedBatchShrunk = 918;      public const int ThrottleAdjusted = 919;
    public const int ThrottlePaused = 920;        public const int ChunkTruncated = 921;
    public const int ChunkerFellBack = 922;       public const int ExtractionWarning = 923;
    public const int PageTimedOut = 924;          public const int StateCommitted = 925;
    public const int FtsMergeCompleted = 926;     public const int HeadingSanitised = 927;
}

// ── Value types the sections above name ───────────────────────────────────────

/// <summary>A 128-bit xxHash128 content hash. Persisted big-endian as BLOB(16) (§9.1).</summary>
public readonly record struct ContentHash(UInt128 Value)
{
    public static ContentHash Zero { get; }
    public bool IsZero { get; }
    public static ContentHash OfBytes(ReadOnlySpan<byte> bytes);
    public static ContentHash OfText(string text);                  // UTF-8, no BOM
    public static ValueTask<ContentHash> OfStreamAsync(Stream stream, long maxBytes,
        int bufferSize = 65536, CancellationToken ct = default);    // §9.1's counted read
    public static ContentHash Combine(ReadOnlySpan<ContentHash> parts);
    public byte[] ToBlob();                                          // 16 bytes, big-endian
    public static ContentHash FromBlob(ReadOnlySpan<byte> blob);     // != 16 -> 6204
    public string ToHex();                                           // 32 lowercase hex
    public override string ToString() => ToHex();
    public static bool TryParseHex(ReadOnlySpan<char> hex, out ContentHash hash);
}

public enum IngestionRunOutcome { Completed, Suspended, Cancelled, Failed }

public enum IngestionStage { Enumerating, Extracting, Chunking, Embedding, Writing, Repairing, Pruning }

/// <summary>
/// Counts, never a percentage: enumeration is streaming, so the denominator is unknown until the
/// run ends and a fabricated percentage is worse than none (§10.4).
/// </summary>
public readonly record struct IngestionProgress(
    string RunId,
    IngestionStage Stage,
    int DocumentsSeen,
    int DocumentsIndexed,
    int DocumentsSkipped,
    int DocumentsFailed,
    int ChunksAdded,
    int ChunksRemoved,
    string? CurrentDocumentId,
    int? CurrentPage,
    int EffectiveWriteBatchSize);

// ── Extraction ────────────────────────────────────────────────────────────────

/// <summary>MEDI's element vocabulary, so the shim is lossless both ways. No image kind (§6).</summary>
public enum DocumentBlockKind { Paragraph, Heading, ListItem, TableRow, Code, Caption, Quote, Footer }

/// <summary><c>[Start, End)</c> half-open into <see cref="ExtractedDocument.Text"/>.</summary>
public readonly record struct DocumentBlock(
    DocumentBlockKind Kind, int Start, int End, int? HeadingLevel = null, int? PageNumber = null);

/// <summary>
/// One normalised text buffer plus blocks indexing into it. CRLF and lone CR to LF, NFC, BOM
/// stripped, when <see cref="ExtractionOptions.NormalizeText"/> is on (§6).
/// </summary>
public sealed record ExtractedDocument(
    string DocumentId,
    string ExtractorId,
    int ExtractorVersion,
    string MediaType,
    string Text,
    IReadOnlyList<DocumentBlock> Blocks,
    IReadOnlyDictionary<string, string> Metadata,
    int? PageCount = null,
    bool HasTextLayer = true,
    IReadOnlyList<IngestionFailure>? Warnings = null);

/// <summary>What an extractor is handed. Read-only: warnings come back on ExtractedDocument.</summary>
public sealed record ExtractionContext(
    string SourceId,
    string CollectionName,
    long MaxDocumentBytes,
    ExtractionOptions Options,
    ILogger Logger);

public interface IDocumentExtractorRegistry
{
    /// <summary>Consumer registrations first, then the built-ins, in registration order.</summary>
    IReadOnlyList<IDocumentExtractor> Extractors { get; }
    /// <summary>Media type first, then extension. False means 6101 for this document.</summary>
    bool TryResolve(DocumentSourceItem item, out IDocumentExtractor extractor);
    /// <summary>"text:1, markdown:1, pdf:1" — the diagnostics string, not the recipe (§7.1).</summary>
    string Describe();
}

/// <summary>Extension-to-media-type mapping for the formats SP3 ships. Used by §11.3's sample.</summary>
public static class IngestionMediaTypes
{
    public const string PlainText = "text/plain";
    public const string Markdown  = "text/markdown";
    public const string Pdf       = "application/pdf";
    public const string Docx      = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>
    /// Accepts a bare extension, a file name or a full path; matches the last dot-segment,
    /// ordinal-ignore-case. Returns "application/octet-stream" for anything unmapped, never null,
    /// so a caller can hand the result straight to DocumentSourceItem.MediaType.
    /// </summary>
    public static string FromExtension(string fileNameOrExtension);
}

/// <summary>§7.2. Built in; registered by AddIngestion after any consumer extractor.</summary>
public sealed class PlainTextExtractor : IDocumentExtractor
{
    public PlainTextExtractor();
    /// <summary>The only way PlainTextExtractorOptions can ever apply.</summary>
    public PlainTextExtractor(PlainTextExtractorOptions options);
}

/// <summary>§7.3. Built in; registered by AddIngestion after any consumer extractor.</summary>
public sealed class MarkdownExtractor : IDocumentExtractor
{
    public MarkdownExtractor();
    /// <summary>The only way MarkdownExtractorOptions can ever apply.</summary>
    public MarkdownExtractor(MarkdownExtractorOptions options);
}

// ── Chunking ──────────────────────────────────────────────────────────────────

/// <summary>Policy for an INPUT unit larger than the budget. Distinct from 6151 (§8.2).</summary>
public enum ChunkOverflow { Split, Truncate, Throw }

public sealed class ChunkOptions
{
    /// <summary>Null resolves per §8.1. Setting it explicitly opts out of the derivation.</summary>
    public int? MaxTokens { get; set; }
    public int? OverlapTokens { get; set; }
    public int? MinTokens { get; set; }
    public int HeadingPathTokenBudget { get; set; } = 32;
    public IReadOnlyList<int> SplitHeadingLevels { get; set; } = [1, 2, 3];
    public bool PrependHeadingPath { get; set; } = true;
    /// <summary>Content before the first heading becomes a chunk. The prior art dropped it.</summary>
    public bool IncludePreamble { get; set; } = true;
    public bool MergeShortSections { get; set; } = true;
    public bool SentenceAware { get; set; } = true;
    public bool RepeatTableHeaderRow { get; set; } = true;
    public ChunkOverflow Overflow { get; set; } = ChunkOverflow.Split;

    /// <summary>
    /// Needs the tokenizer, so it runs in the order-400 startup task, not at builder time (§8.1).
    /// Throws IngestionChunkBudgetInvalid (6003) with the arithmetic in the message.
    /// </summary>
    public ResolvedChunkOptions Resolve(ChunkModelProfile model, IChunkTokenizer tokenizer);
}

/// <summary>The frozen budget. Carried verbatim by IngestionRecipe, so every field is in the hash.</summary>
public sealed record ResolvedChunkOptions(
    int MaxTokens,
    int OverlapTokens,
    int MinTokens,
    int HeadingPathTokenBudget,
    int SpecialTokenOverhead,
    int DocumentPrefixTokens,
    IReadOnlyList<int> SplitHeadingLevels,
    bool PrependHeadingPath,
    bool IncludePreamble,
    bool MergeShortSections,
    bool SentenceAware,
    bool RepeatTableHeaderRow,
    ChunkOverflow Overflow);

/// <summary>
/// One chunk before identity and embedding. HeadingPath is the STRUCTURAL array; the stored column
/// is the joined breadcrumb (§8.3).
/// </summary>
public sealed record ChunkDraft(
    int Ordinal,
    string Text,
    string EmbedText,
    IReadOnlyList<string> HeadingPath,
    string? Breadcrumb,
    int CharStart,
    int CharEnd,
    int TokenCount,
    DocumentBlockKind Kind,
    int Page);              // -1 when unknown; avoids a nullable column

public interface IChunker
{
    string Id { get; }
    int Version { get; }
    /// <summary>Synchronous by design: CPU over an in-memory string, no I/O (§8.2).</summary>
    IEnumerable<ChunkDraft> Chunk(
        ExtractedDocument document, ResolvedChunkOptions options, IChunkTokenizer tokenizer);
}

// ── Throttle ──────────────────────────────────────────────────────────────────

/// <summary>What a pause means. Suspend ends the run on a committed boundary; Wait delays (§10.2).</summary>
public enum ThrottlePauseBehavior { Suspend, Wait }

public readonly record struct IngestionThrottleContext(
    int ConfiguredBatchSize,
    int CurrentBatchSize,
    int DocumentsProcessed,
    long ChunksWritten,
    TimeSpan Elapsed);

/// <summary>
/// The tri-state §10.2's table produces. Pause wins over Delay wins over BatchSize; Reason is one
/// of that table's strings and becomes IngestionRunResult.SuspendReason on a Suspend.
/// </summary>
public readonly record struct IngestionThrottleDecision(
    int BatchSize, TimeSpan Delay, bool Pause, string? Reason)
{
    public static IngestionThrottleDecision Proceed(int batchSize);
}

public interface IIngestionThrottle
{
    string Name { get; }
    /// <summary>Called before each document and before each write window. Must not block.</summary>
    IngestionThrottleDecision Evaluate(IngestionThrottleContext context);
}

/// <summary>The core's default: always the configured batch, no delay, never pauses.</summary>
public sealed class FixedIngestionThrottle : IIngestionThrottle
{
    public FixedIngestionThrottle();
    public string Name => "fixed";
    public IngestionThrottleDecision Evaluate(IngestionThrottleContext context);
}

// ── The ONNX-free tokenizer path (§8.1) ───────────────────────────────────────

/// <summary>
/// IChunkTokenizer over a Microsoft.ML.Tokenizers Tokenizer. Calls
/// GetIndexByTokenCount(..., considerNormalization: false) so the returned index is into the
/// string as passed - the offset contract §8.1 turns on.
/// </summary>
public sealed class MlChunkTokenizer : IChunkTokenizer
{
    public MlChunkTokenizer(Microsoft.ML.Tokenizers.Tokenizer tokenizer, int maxSequenceLength,
        int specialTokenOverhead, string? id = null);
    public string Id { get; }
    public int MaxSequenceLength { get; }
    public int SpecialTokenOverhead { get; }
    public int CountTokens(ReadOnlySpan<char> text);
    public int IndexByTokenCount(string text, int maxTokens, out int tokenCount);
}

public static class EdgeTokenCounter
{
    /// <summary>WordPiece over a vocab.txt. bert-base-uncased shape; overhead 2.</summary>
    public static IChunkTokenizer CreateWordPiece(string vocabFilePath, int maxSequenceLength,
        bool lowerCase = true);
    public static IChunkTokenizer CreateWordPiece(Stream vocabTxt, int maxSequenceLength,
        bool lowerCase = true);
    public static IChunkTokenizer FromTokenizer(Microsoft.ML.Tokenizers.Tokenizer tokenizer,
        int maxSequenceLength, int specialTokenOverhead);
}

// ── Registration ───────────────────────────────────────────────────────────────
public static class IngestionEdgeBuilderExtensions
{
    /// <summary>
    /// The one call. Emits ONE IEdgeMigration at <paramref name="migrationVersion"/> carrying the
    /// chunk-collection DDL and the ingestion-state DDL together; registers the pipeline, the state
    /// store, the extractor registry (text + markdown), the chunkers, the lifecycle observer, the
    /// diagnostics contributor, and one order-400 startup task that opens NO database.
    /// The version must be unique and ascending across the whole database: PRAGMA user_version is
    /// one counter. Idempotent per collection; a second call for the same collection throws
    /// IngestionMigrationVersionConflict (6007).
    /// </summary>
    public static EdgeBuilder AddIngestion(this EdgeBuilder builder, int migrationVersion,
        Action<IngestionOptions>? configure = null);

    public static EdgeBuilder AddDocumentExtractor(this EdgeBuilder builder, IDocumentExtractor extractor);
    public static EdgeBuilder AddDocumentExtractor(this EdgeBuilder builder,
        Func<IServiceProvider, IDocumentExtractor> factory);
    public static EdgeBuilder UseChunkTokenizer(this EdgeBuilder builder,
        Func<IServiceProvider, IChunkTokenizer> factory);
    public static EdgeBuilder UseIngestionThrottle(this EdgeBuilder builder,
        Func<IServiceProvider, IIngestionThrottle> factory);
}

/// <summary>
/// The six facts about an embedding model that chunking and the recipe need. Plain data,
/// declared HERE in the core, so no core type names Qavren.Edge.Embeddings.Onnx.
/// AddOnnxIngestion() projects SP2's EmbeddingPreset onto this (§11.2).
/// </summary>
public sealed record ChunkModelProfile(
    string Id, int Dimensions, int MaxSequenceLength,
    string Pooling, string? DocumentPrefix = null, string? QueryPrefix = null)
{
    /// <summary>
    /// Defaults matching SP2's preset of the same name, FIELD FOR FIELD. The ids are the
    /// lower-case strings SP2 declares - EmbeddingPresets.cs:19 is "all-minilm-l6-v2-int8", not
    /// "all-MiniLM-L6-v2-int8" - and the id is not cosmetic: it feeds
    /// IngestionRecipe.ModelProfileId, so a cased copy would give the ONNX-free and ONNX paths
    /// DIFFERENT recipe hashes for the same model, and would trip §11.2's equality guard on day
    /// one. Verification item 12 asserts every field of every profile against its preset.
    /// </summary>
    public static ChunkModelProfile MiniLmL6V2Int8 { get; } = new("all-minilm-l6-v2-int8", 384, 256, "Mean");
    public static ChunkModelProfile MiniLmL6V2Fp32 { get; } = new("all-minilm-l6-v2-fp32", 384, 256, "Mean");
    public static ChunkModelProfile BgeSmallEnV15   { get; } = new("bge-small-en-v1.5", 384, 512, "Cls",
        QueryPrefix: "Represent this sentence for searching relevant passages: ");

    /// <summary>
    /// BOTH prefixes, trailing spaces included, because SP2 declares both at
    /// EmbeddingPresets.cs:94-95 and this model requires them. DocumentPrefix here is a token
    /// RESERVE and a recipe input only - SP3 never writes it into an embed text, because
    /// OnnxEmbeddingGenerator.ApplyPrefix already does (§8.3).
    /// </summary>
    public static ChunkModelProfile NomicEmbedTextV15Int8 { get; } = new(
        "nomic-embed-text-v1.5-int8", 768, 512, "Mean",
        DocumentPrefix: "search_document: ",
        QueryPrefix: "search_query: ");
}

public sealed class IngestionOptions
{
    /// <summary>Supplies BOTH the collection's dimensions and the chunk budget. Never an ONNX type.</summary>
    public ChunkModelProfile Model { get; set; } = ChunkModelProfile.MiniLmL6V2Int8;
    public int? Dimensions { get; set; }                 // null = Model.Dimensions
    public string CollectionName { get; set; } = "chunks";
    public string? DatabaseName { get; set; }
    public string? StoreName { get; set; }
    public string DistanceFunction { get; set; } = MEVD.DistanceFunction.CosineDistance;
    public bool FullTextIndexed { get; set; } = true;

    /// <summary>
    /// Shapes the collection exactly as SP2's AddVectorCollectionMigration does. MUST carry the
    /// same values passed to AddVectorStore, because SP2's store-level options are NOT reachable
    /// from DI — see §11.1 and error 6011.
    /// </summary>
    public Action<EdgeVectorStoreCollectionOptions>? ConfigureCollection { get; set; }
    public string StateTablePrefix { get; set; } = "qedge_ingest";
    public ChunkOptions Chunking { get; } = new();
    public ExtractionOptions Extraction { get; } = new();   // see below
    public string ChunkerId { get; set; } = ChunkerIds.Auto;
    public long MaxDocumentBytes { get; set; } = 32L * 1024 * 1024;
    public int WriteBatchSize { get; set; } = 32;
    public int DeleteBatchSize { get; set; } = 500;
    public bool SkipUnchangedByTimestamp { get; set; }            // OFF: correctness beats speed
    public bool DeleteMissingDocuments { get; set; } = true;
    public bool RepairOrdinals { get; set; } = true;
    public bool ContinueOnDocumentError { get; set; } = true;
    public int AbortAfterConsecutiveErrors { get; set; } = 20;
    public bool StrictRecipe { get; set; }
    public ThrottlePauseBehavior PauseBehavior { get; set; } = ThrottlePauseBehavior.Suspend;
    public TimeSpan SleepGraceBudget { get; set; } = TimeSpan.FromMilliseconds(750);  // validated <= 2 s
    public TimeSpan StopGraceBudget { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// SP3 merges its OWN FTS5 sidecar on Sleeping, because SP2's observer walks a registry SP3's
    /// collection is not in (§5.2, §12). Validated <= 2 s, and Sleep + Fts is validated <= 3 s.
    /// </summary>
    public TimeSpan FtsMergeBudget { get; set; } = TimeSpan.FromSeconds(1);
    public int FtsMergePages { get; set; } = 500;
    public int FtsMergeMaxIterations { get; set; } = 4;
    public int RunHistoryLimit { get; set; } = 20;
    public bool IncludeCountsInDiagnostics { get; set; }
    public IList<IDocumentExtractor> Extractors { get; }
}

/// <summary>Extractor-agnostic knobs. Per-format options live on the satellites' own option types.</summary>
public sealed class ExtractionOptions
{
    /// <summary>
    /// A non-seekable source stream (§7.1) is buffered once up to this size; above it, 6053.
    /// 4 MiB, deliberately low: the whole point is to refuse the allocation, not to relocate it.
    /// </summary>
    public long NonSeekableBufferLimitBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>Normalise CRLF and lone CR to LF, apply NFC, strip a BOM. On; §6 depends on it.</summary>
    public bool NormalizeText { get; set; } = true;
}

public static class ChunkerIds
{
    public const string Auto = "auto";
    public const string Plain = "plain";
    public const string MarkdownHeading = "markdown-heading";
    public const string TokenWindow = "token-window";
}

// ── Sources ────────────────────────────────────────────────────────────────────
public abstract class IngestionSource
{
    public abstract string Id { get; }
    public abstract IAsyncEnumerable<DocumentSourceItem> EnumerateAsync(CancellationToken ct = default);

    /// <summary>
    /// FILESYSTEM PATHS ONLY. Ordinal-sorted; document ids are forward-slashed paths relative to
    /// the root. This is the desktop and app-private-storage shape — see §11.3 for mobile.
    /// </summary>
    public static IngestionSource Folder(string path, string searchPattern = "*",
        bool recursive = true, string? sourceId = null);

    /// <summary>Filesystem paths only, same caveat as Folder.</summary>
    public static IngestionSource Files(IEnumerable<string> paths, string sourceId);

    /// <summary>
    /// THE MOBILE SHAPE. Opaque handles: a SAF content:// URI, a security-scoped bookmark, a
    /// MAUI FileResult, a blob in app storage. SP3 never interprets the handle; the consumer's
    /// OpenAsync does. See §11.3.
    /// </summary>
    public static IngestionSource Items(IEnumerable<DocumentSourceItem> items, string sourceId);

    /// <summary>Streaming form, for a picker that pages or a provider that enumerates lazily.</summary>
    public static IngestionSource Items(
        Func<CancellationToken, IAsyncEnumerable<DocumentSourceItem>> enumerate, string sourceId);

    public static IngestionSource Single(string documentId, string mediaType,
        Func<CancellationToken, ValueTask<Stream>> openAsync, string sourceId,
        long? sizeBytes = null, DateTimeOffset? lastModifiedUtc = null);
}

public sealed record DocumentSourceItem(
    string DocumentId, string MediaType,
    /// <summary>
    /// Called TWICE per document (hash pass, then extraction pass) and so MUST be re-openable.
    /// SHOULD return a seekable stream at position 0; a non-seekable one is buffered once below
    /// ExtractionOptions.NonSeekableBufferLimitBytes and raises 6053 above it (§7.1).
    /// </summary>
    Func<CancellationToken, ValueTask<Stream>> OpenAsync,
    string? Path = null, long? SizeBytes = null, DateTimeOffset? LastModifiedUtc = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

// ── Pipeline ───────────────────────────────────────────────────────────────────
public interface IIngestionPipeline
{
    Task<IngestionRunResult> RunAsync(IngestionSource source, string? collectionName = null,
        IngestionRunOptions? options = null, CancellationToken cancellationToken = default);
    Task<IngestionRunResult> RunAsync(IngestionSource source,
        IProgress<IngestionProgress> progress, CancellationToken cancellationToken = default);

    /// <summary>Full enumeration + sweep, no write phase. The answer to "a budgeted run never prunes".</summary>
    Task<int> PruneAsync(IngestionSource source, string? collectionName = null,
        CancellationToken cancellationToken = default);

    Task<int> RemoveSourceAsync(string sourceId, string? collectionName = null, CancellationToken ct = default);
    Task<int> RemoveDocumentAsync(string sourceId, string documentId, string? collectionName = null,
        CancellationToken ct = default);
    Task<IngestionStatus> GetStatusAsync(string? collectionName = null, CancellationToken ct = default);
    ValueTask<IngestionRecipe> GetRecipeAsync(CancellationToken ct = default);

    IngestionProgress? Current { get; }
    void RequestStop(string reason);
}

public sealed class IngestionRunOptions
{
    public IngestionBudget? Budget { get; set; }
    public IProgress<IngestionProgress>? Progress { get; set; }
    public bool Force { get; set; }
    public bool? DeleteMissing { get; set; }
    public bool FailFast { get; set; }
    public bool ThrowOnCancellation { get; set; }
    public int? WriteBatchSize { get; set; }
    public Func<DocumentSourceItem, bool>? Filter { get; set; }
}

/// <summary>The per-document verdict. The same values are persisted as the state row's status.</summary>
public enum IngestionDocumentOutcome { Skipped, Indexed, Removed, NoTextLayer, Unsupported, Failed }

/// <summary>Alias kept for the state table's column, whose values are this enum's names.</summary>
public static class IngestionDocumentStatus
{
    public const string Indexed = nameof(IngestionDocumentOutcome.Indexed);
    public const string NoTextLayer = nameof(IngestionDocumentOutcome.NoTextLayer);
    public const string Unsupported = nameof(IngestionDocumentOutcome.Unsupported);
    public const string Failed = nameof(IngestionDocumentOutcome.Failed);
}

/// <summary>One reported (not thrown) failure. Carries the code so a caller can switch on it.</summary>
public sealed record IngestionFailure(
    EdgeErrorCode Code, string Message, string? ExtractorId = null, string? Remediation = null);

public sealed record IngestionDocumentResult(
    string DocumentId, IngestionDocumentOutcome Outcome, string? ExtractorId,
    int ChunksAdded, int ChunksRemoved, int ChunksUnchanged, int ChunksRepaired,
    long TokensEmbedded, TimeSpan Duration, IngestionFailure? Failure);

public sealed record IngestionStatus(
    string CollectionName, string RecipeHash,
    int DocumentCount, int IndexedCount, int FailedCount, int NoTextLayerCount,
    /// <summary>Rows whose last_run_id is not the newest completed run's: candidates for a sweep.</summary>
    int StaleDocumentCount,
    /// <summary>
    /// Rows whose stored recipe_hash differs from the current recipe — one indexed COUNT, and the
    /// ONLY sense of "outstanding" that is cheap to compute. Content drift on disk is NOT counted:
    /// establishing it means opening and streaming every file, which is a run. See §12.
    /// </summary>
    int RecipeStaleCount,
    long ChunkCount,
    string? LastRunId, IngestionRunOutcome? LastOutcome, string? LastSuspendReason,
    DateTimeOffset? LastRunUtc);

// ── Records and schema ─────────────────────────────────────────────────────────
public static class IngestionColumns
{
    public const string Key = "key";                     // string, VectorStoreKey
    public const string Embedding = "embedding";         // ReadOnlyMemory<float>, VectorStoreVector
    public const string SourceId = "source_id";          // string, IsIndexed
    public const string DocumentId = "document_id";      // string, IsIndexed
    public const string Ordinal = "ordinal";             // int
    public const string Text = "text";                   // string, IsFullTextIndexed
    public const string HeadingPath = "heading_path";    // string, IsFullTextIndexed - the BREADCRUMB
    public const string ContentHash = "content_hash";    // byte[](16)
    public const string CharStart = "char_start";        // int
    public const string CharEnd = "char_end";            // int
    public const string TokenCount = "token_count";      // int
    public const string Page = "page";                   // int, -1 when unknown
    public const string BlockKind = "block_kind";        // string
    public const string ExtractorId = "extractor_id";    // string
    public const string MediaType = "media_type";        // string
    public const string CreatedUtc = "created_utc";      // DateTimeOffset

    /// <summary>" > " with U+203A. Joins and splits heading_path; headings are sanitised against
    /// it at chunk time so the split round-trips (§8.3).</summary>
    public const string HeadingPathSeparator = " › ";
}

public static class IngestionSchema
{
    public const int StateSchemaVersion = 1;
    /// <summary>
    /// The vector property is declared ReadOnlyMemory&lt;float&gt;, NOT a string source: SP3
    /// resolves every vector itself (§9.5 step a1). Programmatic, so no attributed record type and
    /// no reflection, and the dimension is a runtime value.
    /// </summary>
    public static MEVD.VectorStoreCollectionDefinition BuildDefinition(
        int dimensions, string distanceFunction, bool fullTextIndexed);
    public static IReadOnlyList<string> BuildStateSql(string tablePrefix);
}

/// <summary>
/// The typed view of one stored row. SP3 writes Dictionary&lt;string, object?&gt; through the
/// dynamic collection; this is the mapper both directions, and the round-trip test covers every
/// member.
/// </summary>
public sealed record IngestedChunk(
    string Key,
    string SourceId,
    string DocumentId,
    int Ordinal,
    string Text,
    /// <summary>The stored breadcrumb, verbatim. Null when the chunk has no heading path.</summary>
    string? Breadcrumb,
    int CharStart,
    int CharEnd,
    int TokenCount,
    int Page,
    string BlockKind,
    string ExtractorId,
    string MediaType,
    ContentHash ContentHash,
    DateTimeOffset CreatedUtc)
{
    /// <summary>
    /// <see cref="Breadcrumb"/> split on <see cref="IngestionColumns.HeadingPathSeparator"/>.
    /// Empty when there is none. This is the ONLY place the structural array comes back; there is
    /// no array column, because SP2's SqliteTypeMap has no array type but byte[] (§8.3).
    /// </summary>
    public IReadOnlyList<string> HeadingPath { get; }

    public static IngestedChunk FromRecord(IReadOnlyDictionary<string, object?> record);
    public IDictionary<string, object?> ToRecord(ReadOnlyMemory<float> embedding);
}
```

The two satellites each publish exactly one extension plus the options type §7.4 and
§7.5 already declare. These are the calls §4.3 chains and the calls 6101's
remediation names, so they are declared rather than implied:

```csharp
namespace Qavren.Edge.Ingestion.Pdf;

public enum PdfReadingOrderMode { ContentOrder, Layout }

public static class PdfIngestionBuilderExtensions
{
    /// <summary>
    /// Registers PdfTextExtractor (Id "pdf") and its PdfExtractorOptions (§7.4). Idempotent: a
    /// second call re-applies <paramref name="configure"/> to the same options instance and
    /// registers no second extractor, so it does NOT trip IngestionDuplicateExtractorId (6004) -
    /// that code is for two DIFFERENT registrations sharing an Id. Order-independent relative to
    /// AddIngestion, because the registry is composed at resolve time; the extractor takes its
    /// place among the built-ins, after any consumer-registered extractor (§7.1).
    /// </summary>
    public static EdgeBuilder AddPdfExtractor(
        this EdgeBuilder builder, Action<PdfExtractorOptions>? configure = null);
}
```

```csharp
namespace Qavren.Edge.Ingestion.OpenXml;

public static class OpenXmlIngestionBuilderExtensions
{
    /// <summary>
    /// Registers DocxTextExtractor (Id "docx") and its DocxExtractorOptions (§7.5). Idempotent and
    /// order-independent on the same terms as AddPdfExtractor.
    /// </summary>
    public static EdgeBuilder AddDocxExtractor(
        this EdgeBuilder builder, Action<DocxExtractorOptions>? configure = null);
}
```

A consumer who wants a format-specific knob sets it there —
`AddPdfExtractor(o => o.PageBudget = TimeSpan.FromSeconds(45))`,
`AddDocxExtractor(o => o.StreamingThresholdBytes = 2L * 1024 * 1024)` — which is why
those options types live on the satellites and not on `IngestionOptions.Extraction`,
whose members are the extractor-agnostic ones. `AddDocumentExtractor` remains the
path for a consumer's own `IDocumentExtractor`, and a consumer who constructs
`new PdfTextExtractor(options)` by hand and passes it to `AddDocumentExtractor`
bypasses nothing: both routes land in the same registry.

`Qavren.Edge.Ingestion.Onnx` adds exactly three public types —
`EdgeChunkTokenizer`, `ResourceMonitorThrottle`, `ResourceMonitorThrottleOptions` —
and one extension:

```csharp
/// <summary>
/// Registers EdgeChunkTokenizer and ResourceMonitorThrottle, and derives
/// ChunkOptions.MaxTokens / OverlapTokens / MinTokens from the resolved EmbeddingPreset
/// unless the consumer already set them. This is the call that stops a constant from
/// hiding a truncation. Idempotent; order-independent relative to AddOnnxEmbeddings.
/// </summary>
public static EdgeBuilder AddOnnxIngestion(this EdgeBuilder builder,
    string? embeddingsName = null, Action<ResourceMonitorThrottleOptions>? configure = null);
```

`Qavren.Edge.Ingestion.DataIngestion` adds `EdgeDocumentConverter` (both
directions), `EdgeChunkerMediAdapter : IngestionChunker<string>`,
`MediReaderAdapter : IDocumentExtractor` and
`EdgeVectorStoreMediWriter : IngestionChunkWriter<string>`. The writer's XML doc
states plainly that **content-hash incremental re-index is not available on that
path** — MEDI's model has nowhere to put a document hash — so every write is a full
rewrite of the document's chunks, and `IIngestionPipeline` is what a phone should
use.

### 11.1 What `AddIngestion` does, and the one migration

1. Validates every **tokenizer-independent** option and throws
   `IngestionOptionsInvalid` (6005) here: explicitly-set chunk relationships, a
   non-positive `HeadingPathTokenBudget`, a non-positive `WriteBatchSize` or
   `DeleteBatchSize`, a `SleepGraceBudget` over 2 s. It does **not** resolve
   `ResolvedChunkOptions` — that needs an `IChunkTokenizer` for
   `SpecialTokenOverhead` and for the `DocumentPrefix` reserve, no tokenizer exists
   at builder time, and `AddOnnxIngestion()` has not even been chained yet. Budget
   resolution and `IngestionChunkBudgetInvalid` (6003) are step 5's, in the
   order-400 startup task (§8.1).
2. Builds the `VectorStoreCollectionDefinition` programmatically via
   `IngestionSchema.BuildDefinition` — no attributed record type, no reflection,
   so the dimension is a runtime value and 384-dim MiniLM and 768-dim nomic share
   one code path. The vector property is declared `ReadOnlyMemory<float>`, because
   SP3 resolves vectors itself (§9.5 step a1).
3. Registers **one** `IEdgeMigration` at `migrationVersion`, through SP1's
   `AddMigrations`. Its `UpAsync` emits SP2's collection DDL followed by SP3's
   state DDL, in one transaction, so the two schemas cannot diverge. The collection
   DDL is obtained by constructing an `EdgeVectorSchema` over a `CollectionModel`
   built from the definition by `EdgeCollectionModelBuilder`, then calling
   `schema.BuildCreateSql()` — all public SP2 surface, needing no SP2 change.

   **The model build needs a generator instance, and SP2's is `internal`, so SP3
   ships its own.** MEVD's model builder takes an `IEmbeddingGenerator` argument,
   and SP2 passes a stand-in for exactly this situation — verified at
   `EdgeVectorDataBuilderExtensions.cs:127` and `:159`,
   `BuildDynamic(options.Definition, options.EmbeddingGenerator ?? SchemaOnlyEmbeddingGenerator.Instance)`,
   with the reason stated at `EdgeVectorCollectionMigration.cs:86–89`: "MEVD's model
   builder refuses a `string` source vector property unless some generator can turn
   it into an `Embedding{T}` — and the DDL it is about to emit needs only the
   DECLARED width, the distance function and the column names, none of which a
   generator contributes." `SchemaOnlyEmbeddingGenerator` is declared
   `internal sealed class` at line 100 of that file and is unreachable from SP3.

   So `Qavren.Edge.Ingestion` carries a twelve-line `internal sealed class
   IngestionSchemaOnlyGenerator : IEmbeddingGenerator<string, Embedding<float>>`
   modelled on it: `GenerateAsync` throws with a message naming
   `AddOnnxEmbeddings`, `GetService` returns null so nothing can read a width off
   it, and `Dispose` is empty. It is passed on every model build and is **never**
   invoked on any path, because SP3's vector property is declared
   `ReadOnlyMemory<float>` rather than a `string` source — the refusal that forces
   the stand-in in SP2 does not even arise here. Passing it anyway costs twelve
   lines and removes a dependency on whether that MEVD parameter is nullable, which
   is the kind of thing that changes in a minor version.

   **Verification item 1** confirms the exact model-build member and its MEVD9001
   suppression, with a documented fallback (two versions, `N` for the collection via
   `AddVectorCollectionMigration`'s definition overload and `N + 1` for the state)
   if it does not resolve. A tier-2 test asserts SP3's emitted DDL is byte-identical
   to what `AddVectorCollectionMigration` emits for the same definition **and the
   same `EdgeVectorStoreCollectionOptions`**, so the two paths cannot drift whichever
   one ships.

   **Where the shaping values come from, which is not the store.** `EdgeVectorSchema`'s
   constructor takes an `EdgeVectorStoreOptions` — `ChunkSize`, `VectorTableNameFormat`,
   `FullTextTableNameFormat`, `FullTextTokenizer`, `FullTextRemoveDiacritics` — and
   every one of those changes the emitted DDL. SP3 cannot read the consumer's
   registered store options, and neither can SP2: `AddVectorStore` captures its
   `Action<EdgeVectorStoreOptions>` in the `CreateStore` **factory closure** and never
   places an options instance in DI, so there is nothing to resolve. Verified in
   `embeddings/src/Qavren.Edge.VectorData/EdgeVectorDataBuilderExtensions.cs`.

   So SP3 does exactly what SP2's own migration path does, and nothing cleverer:
   `IngestionOptions.ConfigureCollection` produces an `EdgeVectorStoreCollectionOptions`,
   which is projected onto a fresh `EdgeVectorStoreOptions` — the same projection
   SP2's internal `EdgeVectorCollectionSchemaFactory.ToStoreOptions` performs, and
   which SP3 reimplements in five lines because that type is `internal`. Unset
   members take `new EdgeVectorStoreOptions()` defaults, which is `ChunkSize = 256`,
   `"{0}_vec"`, `"{0}_fts"`, `Unicode61`, `remove_diacritics 2`.

   **The divergence that leaves, and how it is caught rather than documented away.**
   The *runtime* store does consult its store-level options: `EdgeVectorStore.CollectionOptions`
   projects `VectorTableNameFormat`, `FullTextTableNameFormat`, `ChunkSize`,
   `FullTextTokenizer`, `FullTextRemoveDiacritics`, `KeywordCombinator` and `Rrf` onto
   every collection it hands out, `GetDynamicCollection` included. So a consumer who
   writes `AddVectorStore(o => o.ChunkSize = 512)` and leaves
   `AddIngestion`'s `ConfigureCollection` unset gets migration DDL declaring
   `chunk_size=256` and a runtime collection that believes 512. That hazard is
   **pre-existing in SP2** — `AddVectorCollectionMigration` has it identically — but
   SP3 is where it would first bite a real corpus, so SP3 closes it:

   the order-400 startup task (step 5) resolves the store, calls
   `GetDynamicCollection(collectionName, definition)`, reads the resulting
   `EdgeVectorSchema` through `collection.GetService(typeof(EdgeVectorSchema))`, and
   compares its `BuildCreateSql()` against the statement list the migration
   registered. Both are pure string construction — no connection is opened, nothing
   is executed — so this costs microseconds and runs on every start. A mismatch
   throws `IngestionCollectionSchemaMismatch` (6011) naming the first differing
   statement and the remediation: *pass the same shaping values to `AddIngestion`'s
   `ConfigureCollection` that you passed to `AddVectorStore`.* Comparing the emitted
   SQL rather than field-by-field is deliberate — one comparison covers chunk size,
   tokenizer, diacritics, table names and dimensions at once, and cannot fall behind
   a future SP2 option.
4. `TryAdd`s the pipeline, the state store, the registry, the chunker factory and
   the diagnostics contributor (`TryAddEnumerable` for the contributor, which is a
   genuine enumerable). Every registration is guarded, because SP2 has two paths
   that are not — the unkeyed `AddOnnxEmbeddings` generator and both
   `AddVectorStore` overloads — and SP3 must not add a third.

   **The lifecycle observer is the one exception, and it is not `TryAddEnumerable`.**
   §12 requires it to run *before* SP2's and SP1's observers, and
   `TryAddEnumerable` appends — it cannot produce that order. So the observer is
   registered by **inserting its `ServiceDescriptor` at index 0** of the
   `IServiceCollection`, which is the same idiom SP2's `VectorDataLifecycleObserver`
   uses to get ahead of SP1's. Idempotence comes from a descriptor scan rather than
   from `TryAdd`: `AddIngestion` walks the collection for an existing descriptor
   whose `ImplementationType` is `IngestionLifecycleObserver` and inserts only if
   there is none. One mechanism, stated once, and §12 names the same one.
5. Registers one `IEdgeStartupTask` at order 400 that **touches no database**. It
   runs, in order:
   - resolves `IChunkTokenizer` (`TokenCounterMissing`, 6001, whose remediation
     names `AddOnnxIngestion()` and `UseChunkTokenizer()`);
   - resolves the unkeyed or `StoreName`-keyed
     `IEmbeddingGenerator<string, Embedding<float>>` SP3 will call in §9.5 step a1
     (`IngestionEmbeddingGeneratorMissing`, 6208);
   - calls `ChunkOptions.Resolve(Model, tokenizer)` and caches the
     `ResolvedChunkOptions` for the pipeline and the recipe
     (`IngestionChunkBudgetInvalid` 6003, `ChunkTokenizerCeilingExceeded` 6153);
   - asserts the generator's `EmbeddingGeneratorMetadata.DefaultModelDimensions`,
     when non-null, equals the collection's dimensions
     (`IngestionCollectionDimensionMismatch`, 6006);
   - runs the DDL comparison described in step 3 (6011).

   None of that opens a connection or acquires an ONNX session: the tokenizer is a
   vocabulary parse, the generator is resolved but not called, and both DDL sides
   are pure string construction. The alternative to the dimension check is a
   collection holding vectors from two spaces; the alternative to the DDL check is a
   `chunk_size` that disagrees between the file on disk and the object reading it;
   and the alternative to resolving the budget here is resolving it on the first
   document, where a 6003 is a failed run instead of a failed start.

### 11.2 `AddOnnxIngestion` and the model profile

`Qavren.Edge.Ingestion.Onnx` is the one place SP2's `EmbeddingPreset` and the core's
`ChunkModelProfile` meet. `AddOnnxIngestion` resolves the generator's preset — via
`generator.GetService(typeof(EmbeddingPreset))`, the same route SP2's own store
uses — and projects it:

```csharp
new ChunkModelProfile(
    preset.Id, preset.Dimensions, preset.MaxSequenceLength,
    preset.Pooling.ToString(), preset.DocumentPrefix, preset.QueryPrefix)
```

Then it derives `ChunkOptions.MaxTokens` / `OverlapTokens` / `MinTokens` from that
profile unless the consumer already set them, and registers `EdgeChunkTokenizer` and
`ResourceMonitorThrottle`. This is the call that stops a constant from hiding a
truncation.

Two guards, because a default that silently disagrees with reality is worse than no
default:

- If the consumer set `IngestionOptions.Model` explicitly to something whose `Id`,
  `Dimensions`, `MaxSequenceLength`, `DocumentPrefix` or `QueryPrefix` differs from
  the resolved preset, `AddOnnxIngestion` throws `IngestionOptionsInvalid` (6005)
  naming both. It does not silently overwrite a value the consumer typed. The two
  prefixes are in that list for different reasons and both matter: `DocumentPrefix`
  is a token reserve, so a profile that omits one the generator will apply
  under-budgets every chunk by its length (§8.3); `QueryPrefix` is a recipe input,
  so a profile that disagrees writes a recipe hash describing a retrieval
  convention the collection does not have.
- The core's `ChunkModelProfile.MiniLmL6V2Int8` and friends duplicate six values
  that SP2 also declares — **id, dimensions, max sequence length, pooling name and
  both prefixes**, not three numbers. That duplication is deliberate; it is what
  keeps the core ONNX-free. It is also not allowed to drift: a test in
  `Qavren.Edge.Ingestion.Onnx.Tests` (which references both packages) asserts every
  one of the six against the `EmbeddingPresets` member of the same name, ordinally
  and including trailing spaces, so a preset change in SP2 fails an SP3 test rather
  than mis-sizing a chunk or mis-hashing a recipe. The ids are the lower-case
  strings SP2 declares; the nomic profile carries `"search_document: "` and
  `"search_query: "`; bge-small carries its query instruction and no document
  prefix. Verification item 12 is this test.

### 11.3 Sources on iOS and Android

`IngestionSource.Folder` walks a filesystem path with `Directory.EnumerateFiles`.
That is correct for desktop, for app-private storage, and for a path the app itself
owns. It is **not** what a mobile app that lets the user pick a location has:

- **Android.** A folder chosen through the Storage Access Framework is a
  `content://` tree URI, not a path. `Directory.EnumerateFiles` cannot walk it;
  enumeration goes through `DocumentFile`/`ContentResolver`, and reading goes through
  `ContentResolver.OpenInputStream(uri)`. Persisting access across restarts needs
  `TakePersistableUriPermission`.
- **iOS and Mac Catalyst.** A folder or file chosen through the document picker is a
  security-scoped URL. Reading it requires `StartAccessingSecurityScopedResource()`
  before the open and `StopAccessingSecurityScopedResource()` after, and surviving a
  restart requires resolving a saved bookmark (`NSUrl.FromBookmarkData`).

None of that belongs in a library: it is UI-framework and permission-model surface,
and SP1's decision 1 keeps that with the consumer. What SP3 owes instead is a shape
that **can express it**, which is `IngestionSource.Items`:

```csharp
// MAUI, both platforms. The handle stays opaque to SP3.
var picked = await FilePicker.Default.PickMultipleAsync(options);

var source = IngestionSource.Items(
    picked.Select(f => new DocumentSourceItem(
        DocumentId: f.FileName,                       // stable id the app chooses
        MediaType:  f.ContentType ?? IngestionMediaTypes.FromExtension(f.FileName),
        OpenAsync:  async ct => await f.OpenReadAsync(),   // re-openable; see §7.1
        Path:       f.FullPath,                            // diagnostics only
        SizeBytes:  null)),                                // unknown: §9.1's counted-read gate
    sourceId: "user-picked");

await pipeline.RunAsync(source);
```

Three things this makes explicit, and each is the thing a consumer gets wrong first:

1. **`DocumentId` is the app's choice and must be stable across runs.** Every state
   row is keyed on `(collection, source_id, document_id)`, so an id derived from a
   SAF URI that the OS re-issues between sessions turns every re-run into a full
   re-index. Use a filename, a relative path, or the app's own record id — never a
   raw `content://` string unless the permission is persisted.
2. **`SizeBytes` is legitimately unknown here**, which is why §9.1's ceiling has a
   counted-read path rather than only an enumeration-time gate.
3. **`OpenAsync` is called twice** and must re-acquire the security scope each time
   on iOS. The sample's delegate wraps the `Start`/`Stop` pair; a delegate that
   starts the scope once outside and never stops it leaks it.

The sample app's Ingest page (§16) uses `Items` on Android and iOS and `Folder` on
Windows and Mac Catalyst desktop builds, so the mobile path is the one actually
exercised rather than the one described.

## 12. Lifecycle and diagnostics

**Observer.** `IngestionLifecycleObserver : EdgeLifecycleObserver`, registered by
**inserting its descriptor at index 0** of the service collection — guarded by a
descriptor scan so `AddIngestion` stays idempotent — which is the same idiom SP2's
`VectorDataLifecycleObserver` uses to get ahead of SP1's. §11.1 step 4 names the
same mechanism; it is deliberately **not** `TryAddEnumerable`, which appends and
therefore cannot produce the order this section requires.

That ordering is the fix to a real inversion, not a preference. `EdgeLifecycleHub`
invokes observers sequentially in registration order and awaits each. If SP3 merely
appended, the canonical `AddSqlite → AddVectorStore → AddIngestion` sequence would
run **SP2's bounded FTS5 merge first** — up to 500 pages × 4 iterations against a
2-second budget, per *registered* full-text collection — *while SP3 was still
writing rows to tables in the same database.* Inserting at index 0 yields
`[Ingestion, VectorData, Sqlite]`: stop ingesting and merge SP3's own sidecar, then
let SP2 merge its registered ones, then `wal_checkpoint(TRUNCATE)` a database
nobody is writing to.

**SP3 merges its own FTS5 sidecar, because SP2's observer cannot see it.** §5.2 has
the citations; the operational fact is that
`VectorDataLifecycleObserver.OnSleepingAsync` iterates
`_registry.Registrations.Where(r => r.FullTextTable is not null)`, the registry is
written only by `AddVectorCollectionMigration`, and SP3 emits its own migration and
takes its collection through `GetDynamicCollection`. So SP3's collection is in no
registration, and an observer that assumed otherwise would leave the sidecar
unmerged forever — an FTS5 index that only ever grows segments, on the one table
this package writes hardest.

SP3's observer therefore runs the same bounded merge for its own
`fullTextTable`, with the same shape and its own budget:
`INSERT INTO "<fts>"("<fts>") VALUES('merge', FtsMergePages)` — 500 pages, at most
`FtsMergeMaxIterations` (4) iterations, stopping the moment `FtsMergeBudget`
(1 second, validated ≤ 2 s) is spent or the statement reports nothing left to do.
It runs **after** the stop flag has been set and the runner has reached its
checkpoint, never concurrently with a write, and it is skipped entirely when
`FullTextIndexed` is off. Event 926 logs pages merged and elapsed time, so a merge
that never converges is visible rather than inferred.

Ordering remains an optimisation rather than a correctness requirement — the stop
signal is a flag the runner polls at its next committed boundary, so a consumer who
registers in some other order gets a less tidy checkpoint, not a torn one. A unit
test asserts the order for the canonical sequence and a second asserts the reversed
registration still terminates cleanly.

**The `Sleeping` budget is a sum, and the sum is what has to fit.** The three
observers run back to back on the platform callback thread:

| Observer | Worst case on `Sleeping` |
|---|---|
| SP3 `IngestionLifecycleObserver` | `SleepGraceBudget` (750 ms, capped at 2 s) **+** `FtsMergeBudget` (1 s, capped at 2 s) = 1.75 s default |
| SP2 `VectorDataLifecycleObserver` | ~2 s per **registered** full-text collection — and SP3's collection is **not** one of them, so this row is **0 for an app whose only collection is SP3's** |
| SP1 SQLite observer | `wal_checkpoint(TRUNCATE)`, unbounded in principle, milliseconds against a quiesced WAL |

The middle row being zero for SP3's own collection is the direct consequence of the
registration fact above, and it is the reason SP3's row grew a second term: the
merge did not disappear, it moved. For the common shape — one SP3 collection and
nothing else — the chain is ~1.75 s against iOS's documented ~5 s
`applicationDidEnterBackground:` window, which is more headroom than the previous
draft claimed and is honestly attributed. An app that *also* registers its own
collections through `AddVectorCollectionMigration` pays SP2's row on top, ~2 s each,
and two such collections put the chain past the window.

So three things follow. `SleepGraceBudget` and `FtsMergeBudget` are each validated
at 2 s, and their **sum** is validated at 3 s — a config that cannot fit is rejected
at registration rather than discovered on a device. The README states the arithmetic
and names the remedy: fewer registered full-text collections, or lower budgets. And
the device test in §14.6 measures the **whole chain**, not SP3's observer alone,
because SP3's 750 ms in isolation proves nothing about the number that actually
matters.

| Event | Action | Budget |
|---|---|---|
| `Sleeping` | Set the stop flag; await the runner's next committed checkpoint with `SleepGraceBudget`; then run SP3's own bounded FTS5 merge within `FtsMergeBudget` (event 926); log 902 or a timeout. Never checkpoints WAL. | 750 ms + 1 s default; each **validated ≤ 2 s**, sum **validated ≤ 3 s** |
| `MemoryPressure(Moderate)` | Halve the effective write batch (floor 4), log 918. | < 1 ms |
| `MemoryPressure(Critical)` | Halve, and set the stop flag. | < 1 ms |
| `Resumed` | Clear the shrink flag. Does **not** auto-restart a run — scheduling is the consumer's business. | < 1 ms |
| `Stopping` | Set the stop flag, wait up to `StopGraceBudget` (5 s). | ≤ 5 s |

The grace budget is load-bearing and is capped for a measured reason: SP1's
`AndroidLifecycleBridge` and `AppleLifecycleBridge` both raise with
`GetAwaiter().GetResult()` **on the platform callback thread**, against iOS's
documented ~5 s `applicationDidEnterBackground:` window and Android's `OnPause`
ANR path. `BeginBackgroundTask` does not make a blocking delegate safe. 750 ms sits
well inside it; anything above 2 s is rejected as `IngestionOptionsInvalid` (6005).
A missed grace window logs and returns immediately — the run keeps going and hits
its own checkpoint shortly, and the app was backgrounded anyway. SP1's WAL
checkpoint observer then runs against a database the ingester has stopped writing
to, which is the order that makes it cheap.

**Diagnostics contributor.** `ComponentName = "Qavren.Edge.Ingestion"`, version
from `AssemblyInformationalVersion`. `Describe()` returns, all as strings:

`collection`, `database`, `store`, `dimensions`, `distanceFunction`,
`fullTextIndexed`, `vectorTable`, `fullTextTable`, `lastFtsMergePages`,
`lastFtsMergeMs`, `tokensEmbeddedSource` (`usage` or `counted`, per §9.5),
`recipeHash`, `modelProfileId`, `documentPrefixTokens`, `chunkerId`, `maxTokens`,
`overlapTokens`, `minTokens`, `headingPathTokenBudget`, `tokenizerId`,
`tokenizerMaxSequenceLength`, `specialTokenOverhead`, `extractors`
(`"text:1, markdown:1, pdf:1"`), `throttle`, `lastThrottleReason`, `writeBatchSize`,
`effectiveWriteBatchSize`, `maxDocumentBytes`, `stateSchemaVersion`,
`hashAlgorithm`, `activeRunId`, `activeRunStage`, `activeRunDocumentsCompleted`,
`lastRunEmbedCalls`, `lastRunTokensEmbedded`,
`lastRunId`, `lastRunOutcome`, `lastRunSuspendReason`, `lastRunUtc`,
`lastRunDurationMs`, `lastRunDocumentsIndexed`, `lastRunDocumentsSkipped`,
`lastRunDocumentsFailed`, `lastRunChunksAdded`, `lastRunChunksRemoved`.

Plus, **only when `IncludeCountsInDiagnostics` is set**, five keys: `documents`,
`chunks`, `failedDocuments`, `staleDocuments`, `recipeStaleDocuments`. `Describe()`
is synchronous and cannot await, so these are **five** counting queries executed
synchronously — four `COUNT(*)` over `qedge_ingest_document` with a `WHERE` and one
over the collection's data table — and they are off by default. When they are off the
keys are present with the value `"(disabled)"`, never omitted, so nobody reads a
missing key as zero.

**What "outstanding work" can and cannot mean here.** `recipeStaleDocuments` counts
rows whose stored `recipe_hash` differs from the current recipe — one indexed
predicate, cheap, and exactly the population a recipe bump dirtied.
`staleDocuments` counts rows not stamped by the newest completed run, which is the
pruning backlog §9.6 creates when every run is budgeted. Neither counts **content
drift on disk**, and no synchronous query can: §9.4's content-hash gate establishes
that by opening and streaming every file, which is a run, not a `COUNT`.

So the scheduling question is answered honestly and in two parts. `recipeStaleDocuments
== 0 && staleDocuments == 0 && lastRunOutcome == Completed` means **nothing known is
outstanding** — no recipe bump to absorb, no sweep owed, the last run finished — and
that is a sound reason to skip submitting a background task. It is not a claim that
the corpus is unchanged; discovering that is what a run with `IngestionBudget.Quick`
is for, and on the unchanged path it costs one sequential hash read per document and
zero embeddings (§9.4). The README states both halves, because a consumer who reads
the first as the second will stop re-indexing edited files.

**Logging.** Every event id through `LoggerMessage.Define` source-generated
delegates, because CA1848 is an error under `TreatWarningsAsErrors`. Information
for run lifecycle, recipe change and pruning; Debug for per-document and per-window
detail; Warning for extraction warnings, no-text-layer, encoding fallback,
breadcrumb truncation, throttle pauses and a missed grace window; Error for
document and run failures. **No document text is ever logged** — ids, paths,
offsets and counts only.

## 13. Error handling

The governing rule: a bad document must never kill a run, and a bad run must never
leave the index lying.

### 13.1 New `EdgeErrorCode` values — 6000–6299

```csharp
// ---- Sub-project 3: 6000-6299. SP1's 1001-4001 and SP2's 5001-5213 are untouched. ----

// 6000-6049 configuration and runner
TokenCounterMissing                 = 6001,
IngestionCollectionNotConfigured    = 6002,
IngestionChunkBudgetInvalid         = 6003,
IngestionDuplicateExtractorId       = 6004,
IngestionOptionsInvalid             = 6005,
IngestionCollectionDimensionMismatch= 6006,
IngestionMigrationVersionConflict   = 6007,
IngestionRunAlreadyActive           = 6008,
IngestionRecipeChanged              = 6009,   // StrictRecipe only
IngestionRunAborted                 = 6010,
IngestionCollectionSchemaMismatch   = 6011,   // migration DDL != the runtime store's DDL

// 6050-6099 source
IngestionSourceUnavailable          = 6051,
IngestionDocumentTooLarge           = 6052,
IngestionDocumentUnreadable         = 6053,
IngestionDuplicateDocumentId        = 6054,
IngestionSourceIdInvalid            = 6055,

// 6100-6149 extraction
ExtractorNotFound                   = 6101,
ExtractionFailed                    = 6102,
DocumentEncrypted                   = 6103,
DocumentMalformed                   = 6104,
DocumentHasNoTextLayer              = 6105,
DocumentEncodingUndecodable         = 6106,
DocumentPageBudgetExceeded          = 6107,

// 6150-6199 chunking
ChunkExceedsTokenBudget             = 6151,
ChunkContextTooLong                 = 6152,
ChunkTokenizerCeilingExceeded       = 6153,
ChunkerProducedEmptyChunk           = 6154,
MarkdownParseFailed                 = 6155,

// 6200-6249 state and writes
IngestionStateMissing               = 6201,
IngestionHashAlgorithmMismatch      = 6202,
IngestionStateSchemaUnsupported     = 6203,
IngestionStateCorrupt               = 6204,
IngestionCheckpointWriteFailed      = 6205,
IngestionEmbeddingFailed            = 6206,   // SP3's own GenerateAsync (§9.5 a1)
IngestionWriteFailed                = 6207,   // SP2's UpsertAsync / DeleteAsync (§9.5 a2, b)
IngestionEmbeddingGeneratorMissing  = 6208,   // no IEmbeddingGenerator<string, Embedding<float>>

// 6250-6299 reserved for sub-project 3.
```

### 13.2 Exception types

```csharp
public class EdgeIngestionException : EdgeException
{
    public EdgeIngestionException(EdgeErrorCode code, string message, Exception? inner = null);
    public string? SourceId { get; init; }
    public string? DocumentId { get; init; }
    public string? RunId { get; init; }
    public string? Remediation { get; init; }
    public long? SizeBytes { get; init; }
}

public sealed class EdgeExtractionException : EdgeIngestionException   // + ExtractorName, PageNumber
public sealed class EdgeChunkingException   : EdgeIngestionException   // + ChunkerId, RequiredTokens, BudgetTokens
public sealed class EdgeIngestionStateException : EdgeIngestionException // + Expected/ActualSchemaVersion
```

Configuration faults reuse SP1's `EdgeConfigurationException` with a 60xx code, so
`AddIngestion` fails the way every other builder call in the suite fails.
`EdgeException.HelpLink` already resolves into `foundation/docs/errors.md#<code>`.

SP2's `EdgeVectorStoreException` derives from MEVD's `VectorStoreException`, not
`EdgeException`. SP3 does **not** re-wrap it — it already carries an
`EdgeErrorCode`, and re-wrapping would bury an identity a consumer may be filtering
on. It is attached as `InnerException` on a run-tier failure.

### 13.3 Failure catalogue

| Code | Condition | Tier | Behaviour |
|---|---|---|---|
| 6001 | No `IChunkTokenizer` resolvable | startup | Throw. There is deliberately no chars/4 fallback — that is the prior art's hidden-truncation bug. |
| 6005 | Tokenizer-**independent** options: explicitly-set chunk relationships, non-positive `HeadingPathTokenBudget` / `WriteBatchSize` / `DeleteBatchSize`, `SleepGraceBudget` or `FtsMergeBudget` over 2 s, their sum over 3 s, a `Model` disagreeing with the resolved preset (§11.2) | registration | Throw, with the numbers in the message. |
| 6003 / 6153 | Budget arithmetic; tokenizer ceiling ≠ profile ceiling | **startup** | Throw, with the arithmetic in the message. Not registration: `SpecialTokenOverhead` and the `DocumentPrefix` reserve are tokenizer facts, and no `IChunkTokenizer` exists at builder time (§8.1, §11.1 step 5). |
| 6006 | Generator metadata dimensions ≠ collection dimensions | startup | Throw. |
| 6007 | Second `AddIngestion` for the same collection | registration | Throw. |
| 6008 | Concurrent `RunAsync` on the same pipeline | run | Throw; the message names the active run id. |
| 6009 | Recipe drift with `StrictRecipe` | run | Throw with both hashes; default is to re-index and log 915. |
| 6010 | `AbortAfterConsecutiveErrors` (20) consecutive document failures | run | Abort; inner = the last failure. Twenty in a row is systemic. |
| 6011 | The migration's collection DDL differs from the runtime store's `BuildCreateSql()` | startup | Throw, naming the first differing statement. Remediation: pass the same shaping values to `AddIngestion`'s `ConfigureCollection` that you passed to `AddVectorStore` (§11.1). |
| 6051 | Folder missing or unreadable | run | Throw before any document — the whole run is meaningless. |
| 6052 | Over `MaxDocumentBytes` | document | Recorded `Failed`, event 907. File never opened when the source declared a size; otherwise the hash pass's counted read aborts at the ceiling and **no content hash is stored**, so a later shrink is re-attempted (§9.1). |
| 6053 | `IOException` / `UnauthorizedAccessException` on open; or a non-seekable stream over `NonSeekableBufferLimitBytes` (§7.1) | document | Recorded. A file deleted between enumeration and open is the common case and must never fail a run. |
| 6054 | One `DocumentId` yielded twice in a run | document | Second recorded `Failed`. Silently overwriting is worse — the prior art's empty-front-matter id collision produced a mid-run primary-key violation. |
| 6101 | No extractor claims the extension | document | Recorded `Unsupported`; remediation names the missing satellite. |
| 6102 / 6103 / 6104 | Extractor threw / encrypted / malformed | document | Recorded, inner preserved. One bad PDF fails one document, never a corpus. |
| 6105 | Every page has no text layer | document | Recorded `NoTextLayer`, **not** an error outcome. Hash stored so it is not re-parsed. |
| 6106 | Invalid UTF-8 with `StrictUtf8` | document | Recorded. Unreachable on defaults (Latin-1 fallback, event 914). |
| 6107 | Cumulative page parse time passed `PageBudget`, checked at a page boundary | document | Pages already parsed are kept; extraction stops; the document is recorded `Failed` naming the page reached, event 924. Not an abort of the offending page — PdfPig's page API takes no `CancellationToken` (§7.4). |
| 6151 | A chunker emitted a chunk over `MaxTokens` | chunk | **Throw, in every configuration.** An SP3 invariant violation, not user data. |
| 6152 | Breadcrumb over budget and still does not fit after left-truncation | chunk | Throw, or honour `Overflow`. |
| 6201–6204 | State tables absent, unknown hash algorithm, unsupported schema version, a 16-byte hash that is not | run | Throw with a remediation (`AddIngestion(version)`, or delete the state rows and re-run). Forward-only; this suite has no down-migrations. |
| 6205 | State write failed (disk full, `SQLITE_BUSY` past `BusyTimeout`) | run | Report and **abort the run**. An unwritten checkpoint means the next run redoes work it believes is done. The transaction rolled back, so the document stays at its previous state. |
| 6206 | **SP3's own `GenerateAsync` (§9.5 step a1) threw** | window | Catch, halve the batch, log 918, retry the **embed** once. A second failure suspends the run rather than thrashing a device already under pressure. SP2's `OnnxInsufficientMemory` (5005) is surfaced verbatim as the inner exception. Retrying only the embed is possible precisely because a1 and a2 are separate calls. |
| 6207 | **`UpsertAsync` or `DeleteAsync` (§9.5 step a2, b) threw** | window | Reported, not retried; `EdgeVectorStoreException` preserved as inner. The split from 6206 is by **call site**, not by inspecting an exception type — folded into one generator-backed `UpsertAsync` the two would be indistinguishable, because SP2 wraps only the dimension-mismatch case in `EdgeVectorStoreException` (`EdgeVectorStoreCollection.cs:556`) and a generator fault arrives as whatever the generator threw. |
| 6208 | No `IEmbeddingGenerator<string, Embedding<float>>` resolvable, unkeyed or keyed on `StoreName` | startup | Throw. SP3 calls the generator itself (§9.5), so its absence is a start-time fact, not a first-document surprise. |

Per-document failures are recorded in the state row (`status = failed`, `error`) and
surface on the result through `IngestionRunResult.Documents`, whose entries carry the
per-document outcome and failure; `IngestionRunResult.Failures` is the computed
projection of that list (§10.2), not a second collection. `IngestionRunResult.Failure`
— singular — stays what its name says: the one **run**-level fault, null on a
`Completed` or `Suspended` run. Recording the failure in the state row is what makes
the next run skip a broken file on an unchanged hash rather than retrying it forever,
and a recipe bump retries it automatically. `ContinueOnDocumentError = false` promotes
the first document failure to a run failure and rethrows the original.

Cancellation is checked **before** opening a transaction, never inside one, and
uses `CancellationToken.None` for rollback exactly as `EdgeMigrator` does. It
surfaces as `IngestionRunOutcome.Cancelled` with no exception unless
`ThrowOnCancellation` is set.

## 14. Testing

Four tiers, mirroring SP2 §16. **Nothing on a PR lane downloads anything**: the
whole PR corpus is roughly 40 KB of committed ASCII and part-XML, and the only
model is SP2's existing base64 tiny-ONNX fixture.

### 14.1 Tier 1 — unit, no network, no binary in git

`ingestion/tests/fixtures/` carries a `.gitattributes` with `corpus/** -text`.
That line is **load-bearing**: chunk offsets are positions, this machine's editing
tooling flips LF to CRLF, and a CRLF that sneaks in moves every expected boundary
on the Linux leg only. A drift test asserts every fixture's byte length and
SHA-256, exactly as the SP2 fixtures README asserts byte counts, so a BOM or an
eol flip fails at the fixture rather than confusingly at chunk 7.

**Text and Markdown — 12 committed files.** `empty.txt`; `whitespace-only.txt`;
`three-paragraphs.txt` (no trailing newline); `crlf-and-lone-cr.txt`;
`long-token.txt` (one 5,000-character run with no whitespace — forces the hard
split where there is no separator to back up to); `unicode.txt` (é as NFC **and**
NFD, a ZWJ emoji family, CJK, an RTL run); `bom.txt`; `headings.md` (h1/h2/h3, a
setext heading, a `#` line **inside a fence**, YAML front matter); `fences.md`
(tilde fence, nested backticks, indented code); `tables-lists.md`; `raw-html.md`;
`giant-heading-section.md` (one section far over budget, which must still split and
keep the breadcrumb on every piece).

**Golden files.** One JSON per (fixture × chunker config) holding
`{ index, startChar, endChar, tokenCount, headingPath, breadcrumb, text, embedText }`
— a declared **superset** of the six fields this section originally pinned, which
are kept in their original order. `breadcrumb` and `embedText` are required because
`PrependHeadingPath` changes only the embed text: without them a
`.no-breadcrumb` golden is byte-identical to its `.auto` counterpart and asserts
nothing about the flag it exists to cover (amended 2026-09-11, Task 4.1). **Full text**,
because the fixtures are small and a moved boundary should be legible in the diff
rather than a changed hash. Written only under `QAVREN_EDGE_WRITE_GOLDEN=1`, and
**the writer refuses to overwrite an existing file**: deleting it is the deliberate
act and regeneration is its own PR with the reason in the body. That is SP2's
`reference-vectors.json` rule, and it is the only thing that stops a golden test
from ratifying a regression.

**Property tests — CsCheck 4.8.0** (Apache-2.0, zero dependencies, net8.0+, no
xunit integration package to reconcile with xunit.v3 3.2.2). `seed:` and
`iter: 500` pinned **at the call site** on the PR lane; a time-seeded property test
in a gate is a flake generator. The nightly lane widens via `CsCheck_Iter` /
`CsCheck_Seed`, and a failure prints the seed to replay. Over generated strings
from an ASCII + CJK + combining-mark + ZWJ alphabet, `maxTokens ∈ [24, 512]`,
`overlap ∈ [0, maxTokens/2)`:

- every chunk's token count ≤ `MaxTokens`, counted with the **same** `IChunkTokenizer`
  instance the chunker used — never `text.Length / 4`;
- offsets strictly ascending, `start < end ≤ Text.Length`, coverage contiguous
  modulo overlap, no gaps;
- overlap exactness: the last *k* tokens of chunk *i* equal the first *k* of *i+1*;
- de-overlapped reassembly equals the input byte for byte, for the lossless chunkers;
- no boundary splits a surrogate pair or a grapheme cluster;
- **the offset contract (§8.1), asserted against both `IChunkTokenizer`
  implementations**: for every cut, `IndexByTokenCount`'s returned index is an index
  into the string as passed — `text[..index]` is a valid prefix of the original and
  `CountTokens(text[..index]) <= maxTokens` while `index` is maximal to the nearest
  grapheme boundary. Seeded explicitly with NFD-composed text, a ZWJ sequence, a
  Turkish dotted I and mixed-case non-ASCII, which is the input class where a
  normalised index and an original index diverge;
- empty input → zero chunks; non-empty input → no empty chunk;
- monotonicity: raising `MaxTokens` never raises the chunk count;
- purity: two runs over one input produce identical output;
- culture independence: identical output under `InvariantCulture` and `tr-TR` —
  the dotted-I trap in any heading matcher;
- `SampleModelBased` against a naive re-embed-everything model over random
  single-character edits: an unchanged document re-embeds **zero** chunks, an
  edited one re-embeds only chunks whose hash moved;
- **`heading_path` round-trips**: for a generated heading path whose members
  deliberately contain `›`, ` › `, control characters and leading or trailing
  whitespace, `Split(Join(sanitise(path)))` equals `sanitise(path)`, and the
  sanitiser is idempotent. This is the whole guard on §8.3's decision to store a
  joined string, and it is the property that fails if the separator is ever changed
  without changing the sanitiser.

**Deterministic unit tests.** `ContentHash` known vectors and big-endian byte form;
`ChunkKey` stability and duplicate-paragraph ordinals; `IngestionRecipe.Hash`
sensitivity — one test per field, each asserting the hash moves, plus one asserting
that bumping an *unselected* extractor's version does **not** move it;
`IngestedChunk` round-trip over every property **including `Breadcrumb` → `HeadingPath`**;
`ChunkOptions.Resolve(model, tokenizer)` producing
**222 / 32 / 27** and **478 / 64 / 59** — the truncating rules of §8.1, with the bge
overlap pinned at 64 because `478 * 15 / 100 / 8 * 8` is 64 and rounding to nearest
would give 72 — plus a third case with a fake tokenizer reporting a four-token
`DocumentPrefix`, asserting the reserve is subtracted rather than ignored, and every
6003 path; the split between registration-time 6005 and startup-time 6003, asserted
by building a container with a deliberately bad budget and observing that
`AddIngestion` returns and the order-400 task throws; the extractor registry's
resolution order and that `AddPdfExtractor` twice registers one extractor;
the throttle decision table as `[Theory]` rows against a fake
`IEdgeResourceMonitor`, including `Unknown`-is-not-fine; the lifecycle observer's
grace budget with `FakeTimeProvider`; `SuspendReason` for every budget and throttle
path.

### 14.2 Tier 1 extractors — `Qavren.Edge.Ingestion.Extractors.Tests`

**Commit the inputs; generate nothing at test time except the zip container.**
Measured, not assumed: PdfPig's `PdfDocumentBuilder` writes a random trailer `/ID`
pair, so identical content produces a different SHA on every build, and
`WordprocessingDocument.Create` embeds execution-time zip stamps. A generated
fixture also makes the *input* move whenever the generator library upgrades, at
which point the golden test measures PdfPig rather than the chunker.

**PDF — seven committed ASCII files plus one assembled.** A hand-written
uncompressed PDF is pure 7-bit ASCII and reviewable in a diff; a verified 626-byte
one-page fixture (five objects, classic xref, Helvetica Type1) parses correctly in
PdfPig 0.1.16. The set: `minimal-text`, `two-pages` (page numbers in metadata),
`two-columns` (reading order), `hyphen-linebreak`, `no-text-layer` (a rectangle and
nothing else — must yield `NoTextLayer`, not throw), `xref-stream` (PDF 1.5 xref
stream plus an object stream via `/ASCIIHexDecode`, so a modern shape stays ASCII),
`broken-startxref` (error-path message). The eighth, `flate-content.pdf`, is
assembled at test time by deflating a committed ASCII content stream at a fixed
`CompressionLevel` — the one Flate path that cannot be spelled in ASCII, and
`DeflateStream` is deterministic where PdfPig's `/ID` is not. A checked-in
`make_pdf_fixtures.py` documents regeneration and, like `make_tiny_model.py`, **is
never a build step and CI never runs it**.

**DOCX — ten committed part-XML trees plus one helper.** `headings`; `run-split`
(one sentence across five `w:r` with rsid noise — the single most common extractor
bug); `table`; `numbered-list` (+ `numbering.xml`); `footnotes`; `header-footer`
(must be *excluded*); `hyperlink` (+ `document.xml.rels`); `textbox`
(`w:txbxContent`); `empty-body`; `unknown-style` (a `pStyle` naming a style absent
from `styles.xml` — must degrade, not throw), plus a localised-style fixture
("Titre 1") asserting the outline-level path. `DeterministicOpc.Build` zips them:
entries in declared order, `CreateEntry(name, CompressionLevel.Optimal)`,
`LastWriteTime = 1980-01-01T00:00:00Z` set **between `CreateEntry` and `Open()`** —
setting it afterwards throws in Create mode. Measured byte-identical across runs; a
verified 1,039-byte three-part package opens cleanly in OpenXml 3.5.1.

### 14.3 Tier 2 — integration against the real natives

`ci.yml` already downloads the `native-*` artifacts on all three host OSes, so
vec0 and FTS5 are free. The generator is a `RecordingEmbeddingGenerator`: a
deterministic hash-seeded unit vector that **counts `GenerateAsync` calls and
inputs**.

Where the SP2 tiny ONNX fixture is used, the collection is created with
**`distance_metric = L2`**, not cosine. The fixture embedding table is row *t* =
`[t, t+0.5, t+0.25, t+0.75]`, so every row is near-collinear — cosine over rows
1..15 spans 0.983417 to 0.999999, and cos(14,15) is 0.999999. Any recall assertion
on cosine there is float noise. L2 separates by exactly 2.0 per token-id step, so
the assertions are **exact** rank order and exact distances. With a deterministic
model you can afford equality, not tolerance.

- Ingest the corpus → assert the expected keys, key format, row counts on all three
  tables, FTS5 rows and vec0 rows.
- **Re-run unchanged → `EmbedCalls == 0`, `ChunksAdded == 0`,
  `DocumentsSkipped == DocumentsSeen`.** This is the real assertion behind
  "content-hash incremental re-index", and no golden file can make it.
- Edit one paragraph → exactly one chunk added, one removed.
- **Insert a paragraph at the head of a 20-chunk document** → one embed, 19
  repairs, and the `vec0` rows for the 19 read back **byte-identical** before and
  after (direct SQL). Plus an assertion that the FTS5 sidecar content is still
  correct after the repair, which is the only guard on the `_au` trigger path.
- Delete a source file → the sweep removes its chunks on a `Completed` run and
  **does not** on a `Suspended` one; `PruneAsync` then removes them.
- Bump the recipe → every document re-indexed. Bump an unselected extractor's
  version → nothing re-indexed.
- **Crash injection**: a writer that throws after the second write window, then
  re-run → the document completes, no duplicate keys, and only the un-written
  chunks were embedded. This is the load-bearing claim of §9.5 and it gets a
  mechanism, not a description.
- **`SQLITE_BUSY` under a concurrent writer** → 6205, and the document stays at its
  old hash.
- **The transaction invariant**: a test asserting no SP3 code path calls an SP2
  collection method from inside an SP3 `ExecuteInTransactionAsync` callback,
  enforced by a spy `IEdgeDatabase` that fails if a nested open occurs.
- `MaxDocuments: 3` → `Suspended` with `SuspendReason == "budget:documents"`,
  pruning skipped, resume completes; total embeds across the two runs equal a single
  unbudgeted run.
- Cancel mid-run → `Cancelled`; resume produces the same final state as an
  uninterrupted run (byte-compare the collection).
- Dimension mismatch → 6006 before any write.
- **The generator sees the embed text without a prefix, and the reserve is real.**
  With `Model = ChunkModelProfile.NomicEmbedTextV15Int8`, assert every string
  `RecordingEmbeddingGenerator` receives starts with the breadcrumb or the chunk
  text and **never** with `"search_document: "`, and that the resolved `MaxTokens`
  is `512 − 2 − 32 − CountTokens("search_document: ")` rather than 478. The first
  half is the double-prefix guard; the second is the guard on the encoder overrun
  that a missing reserve would cause (§8.3).
- **6206 and 6207 are separable.** A generator that throws on the third call →
  6206, one halved retry, then `Suspended`. A collection whose `UpsertAsync` throws
  → 6207, no retry. Both assert the code, not just the failure.
- **The startup task resolves the budget and does not open a connection.** A spy
  `IEdgeDatabase` asserts zero `OpenConnectionAsync` calls during startup, and a
  container with `AddIngestion` but no tokenizer fails at order 400 with 6001 rather
  than at builder time or on the first document.
- **SP3's own FTS5 merge runs on `Sleeping`.** Raise `Sleeping` after a run that
  wrote several hundred chunks, and assert event 926 fired with a non-zero page
  count and that `Sleeping` returned inside `SleepGraceBudget + FtsMergeBudget`.
  Second case: `FullTextIndexed = false` → no merge, no 926. This is the test that
  fails if the collection is ever assumed to be in SP2's registry (§5.2).
- **Collection-shaping divergence → 6011 at startup, before any write.**
  `AddVectorStore(o => o.ChunkSize = 512)` with `AddIngestion`'s `ConfigureCollection`
  left unset must fail at order 400 naming the differing `CREATE VIRTUAL TABLE`
  statement; setting `ConfigureCollection = o => o.ChunkSize = 512` must then start
  clean. The same test parameterised over `VectorTableNameFormat` and
  `FullTextRemoveDiacritics`, because the comparison is of emitted SQL and must catch
  all three (§11.1).
- Non-seekable source streams: a small one is buffered and ingests; one over
  `NonSeekableBufferLimitBytes` raises 6053 without being read to the end (§7.1).
- A source item with `SizeBytes = null` over `MaxDocumentBytes` → 6052 from the
  counted read, with **no** `content_hash` written, and a subsequent run over a
  shrunk file ingests it (§9.1).
- The §4.3 snippet resolved from a **real** `ServiceCollection`, asserted end to
  end so it cannot regress into `EmbeddingGeneratorMissing` — the same guard SP2
  §16.2 has.
- The `Sleeping` observer returns within `SleepGraceBudget` while a run is active.
- One SQLCipher test: the full ingest lifecycle over a keyed connection, in
  `Qavren.Edge.Sqlite.Cipher.Tests`, which SP1 already keeps out of the device lanes.

### 14.4 Tier 2 — MEDI conformance

`Qavren.Edge.Ingestion.DataIngestion.Tests` builds a real MEDI
`IngestionPipeline<string>` over an `EdgeChunkerMediAdapter` and an
`EdgeVectorStoreMediWriter` and asserts it writes and searches. That is the only
proof the ecosystem claim is true rather than aspirational. It also asserts the
converter is lossless in both directions except for spans and images, both
documented.

**What that test has to restore, stated because it is not what the shim ships
against.** `IngestionPipeline<T>` lives in `Microsoft.Extensions.DataIngestion` — the
**implementation** package — not in `…DataIngestion.Abstractions`, which is all the
shim references. So the test project adds:

```xml
<PackageReference Include="Microsoft.Extensions.DataIngestion" />   <!-- version from §4.2 -->
```

and with it, on net10.0, eight transitive packages: `Microsoft.Extensions.AI`,
`Microsoft.Extensions.DataIngestion.Abstractions`,
`Microsoft.Extensions.Logging.Abstractions`,
`Microsoft.Extensions.VectorData.Abstractions`, `Microsoft.ML.Tokenizers`,
`System.Numerics.Tensors`, `System.Diagnostics.DiagnosticSource` and
`System.Linq.AsyncEnumerable`. Most are already on this repo's version line, but the
last two are new surface, and the graph is **not** cheap. The claim that survives is
narrower and is the one that matters: the *shipped* `Qavren.Edge.Ingestion.DataIngestion`
package costs a consumer one prerelease reference with zero net8.0+ dependencies,
because it carries only the Abstractions; the implementation package and its eight
are confined to this one test project and reach nobody's app.

**The reader, and the Markdig collision it exists to avoid.** MEDI's `MarkdownReader`
ships in `Microsoft.Extensions.DataIngestion.Markdig`, which depends on
`Markdig.Signed`. The core already depends on `Markdig` 1.3.2. Two different package
ids emitting the same `Markdig.dll` assembly name into one output directory is an
assembly conflict — MSBuild's reference resolution picks the higher assembly version
and raises `MSB3277`, which `TreatWarningsAsErrors` turns into a build failure, and
the alternative outcomes (a silent last-writer-wins copy, or a binding mismatch at
runtime) are worse than the failure.

So the test does **not** restore that package. MEDI's `IngestionDocumentReader` is an
abstract class with a single abstract member — `ReadAsync(Stream, string, string,
CancellationToken)` — so the conformance test supplies a fifteen-line reader over
SP3's own `MarkdownExtractor`, feeding the pipeline through
`EdgeDocumentConverter.ToMedi`. That exercises everything the claim needs (MEDI's
pipeline type, MEDI's chunker contract, MEDI's writer contract, and the converter in
both directions) while keeping `Markdig.Signed` out of every graph in this repository.

The consequence is documented rather than hidden: **if a future test genuinely needs
MEDI's own Markdig reader, it cannot live in a project that also references
`Qavren.Edge.Ingestion`.** It would need a separate test project referencing only
`…DataIngestion.Abstractions` plus the MEDI Markdig package, and the plan should
treat that as a new project rather than a package addition. A CI assertion is not
warranted for a package that is absent; the `MSB3277`-as-error setting is the guard,
and it already exists repo-wide.

### 14.5 Tier 3 — nightly only

`model-tests` (`if: schedule || workflow_dispatch`, never in `ci-gate`'s `needs`).
Real int8 MiniLM at the HF revision SP2 already pins and caches by SHA-256. A
~20-document prose corpus; assert a known query's top-3 contains the expected
chunk, cosine tolerance 1e-3. Retrieval *quality* is not obtainable from a 4-dim
fixture model and must never gate a PR.

Plus a **real-world document lane**: three or four files produced by actual Word,
LibreOffice and Acrobat, fetched by pinned SHA-256 into the existing content-hash
cache, never committed, asserting only phrase containment and "the pipeline does
not throw". Hand-rolled fixtures prove the parser handles what we hand-rolled;
this is the only thing that proves anything about the documents users actually
have.

### 14.6 Tier 4 — device lanes

No new lane. `Qavren.Edge.Ingestion.Tests` and
`Qavren.Edge.Ingestion.Extractors.Tests` join `Qavren.Edge.DeviceTests`'s
`ProjectReference` list and copy `Qavren.Edge.VectorData.Tests`'s project shape
exactly, including SP2's raised `SupportedOSPlatformVersion` floors where a
transitive ORT reference exists.

**Fixtures ship as `EmbeddedResource`, not files.** Device filesystem layout
differs from the host, and SP2's `TinyModels.g.cs` `<Compile Link=…>` idiom covers
base64 text constants only, not a binary corpus. The DOCX zip is assembled at
runtime from embedded part XML.

Device-only assertions — the things nothing else can prove:

- **The PdfPig asset trap.** A committed PDF referencing Helvetica **without
  embedding it** opens without `TypeInitializationException` on all four device
  TFMs. PdfPig ships no `net10.0` TFM and must resolve `lib/net9.0`; the
  `netstandard2.0` copy of `UglyToad.PdfPig.Fonts.dll` contains neither
  `AndroidSystemFontLister` nor `IOSSystemFontLister` and throws
  `NotSupportedException` out of a static constructor on exactly that input.
- A 200-page PDF ingests inside a `Quick` budget with peak
  `GC.GetTotalAllocatedBytes` under an asserted ceiling.
- A simulated `MemoryPressure(Critical)` mid-run yields `Suspended` with
  `SuspendReason == "memory:critical"` and a resumable state.
- **`RaiseSleepingAsync` — the whole observer chain, not SP3's link — returns inside
  a measured ceiling with a run active and one full-text collection.** SP3's own
  observer is asserted under `SleepGraceBudget` separately, but the number that
  decides whether an iOS app is killed is `SP3 + SP2's FTS5 merge + SP1's WAL
  checkpoint`, and §12's arithmetic is only credible if something measures the sum.
  The assertion is a wall-clock ceiling of 3 s, leaving margin inside iOS's ~5 s
  window; a failure is a signal to lower `SleepGraceBudget`, not to raise the ceiling.

## 15. CI changes

New projects into `QavrenEdge.slnx` under `/ingestion/src/` and `/ingestion/tests/`;
new package versions into `Directory.Packages.props` per §4.2. Central transitive
pinning stays off.

`ci.yml`'s `test` job gains four steps, matching the established shape exactly
(`-f net10.0 -p:TargetFrameworks=net10.0` on multi-TFM projects, `-p:` alone on
single-TFM ones):

```yaml
      - name: Ingestion tests
        run: dotnet run --project ingestion/tests/Qavren.Edge.Ingestion.Tests/Qavren.Edge.Ingestion.Tests.csproj -c Release -f net10.0 -p:TargetFrameworks=net10.0

      - name: Ingestion extractor tests
        run: dotnet run --project ingestion/tests/Qavren.Edge.Ingestion.Extractors.Tests/Qavren.Edge.Ingestion.Extractors.Tests.csproj -c Release -f net10.0 -p:TargetFrameworks=net10.0

      - name: Ingestion Onnx tests
        run: dotnet run --project ingestion/tests/Qavren.Edge.Ingestion.Onnx.Tests/Qavren.Edge.Ingestion.Onnx.Tests.csproj -c Release -p:TargetFrameworks=net10.0

      - name: Ingestion MEDI tests
        run: dotnet run --project ingestion/tests/Qavren.Edge.Ingestion.DataIngestion.Tests/Qavren.Edge.Ingestion.DataIngestion.Tests.csproj -c Release -p:TargetFrameworks=net10.0
```

One new assertion on the `windows-2025` leg, modelled on SP2's ORT asset check and
running off the same `dotnet restore QavrenEdge.slnx`: **PdfPig asset resolution.**
Scan `project.assets.json` and fail if `PdfPig` resolved to `lib/netstandard2.0`
for any TFM; `net10.0` must land on `lib/net9.0`. The failure it guards is a
static-constructor throw on a device, with no compile error anywhere.

`trim-smoke` gains an `AddIngestion` plus a one-document run over a committed
**Markdown** string through `EdgeDynamicVectorStoreCollection`. Markdown rather than
plain text on purpose: it puts Markdig — the only dependency in the *core* that
declares neither `IsTrimmable` nor `IsAotCompatible` (§7.3) — under the trimmer on
every PR, rather than only exercising a path that has no third-party parser in it.
That is also the real check that nothing in SP3 carries a `[RequiresDynamicCode]`
annotation, and it is otherwise free because SP3 has no reflection path at all.

`foundation/tools/ci-checks/assert-workflows.py` gains two assertions: every test
project under **`ingestion/tests/`** appears as an explicit step in `ci.yml` (SP2's
rule globs `embeddings/tests/`; extend the same rule rather than duplicating it),
and the PdfPig asset assert exists. The `trx2junit.py` and `java-junit` counts stay
at 4 — the device lanes are unchanged in number.

`model-tests` gains the tier-3 ingestion step and the real-world document lane.
`native.yml` and `release.yml` are untouched; SP3 lives under `ingestion/**`, which
no native path filter matches, so a managed-only SP3 PR still takes `native.yml`'s
`reuse` job.

`Qavren.Edge.Ingestion.DataIngestion` carries `<IsPackable>true</IsPackable>` with
a prerelease-only version suffix and is **excluded from package validation's
baseline** until MEDI ships stable.

## 16. Sample app

`foundation/samples/Qavren.Edge.Sample` gains **one** page rather than SP3 shipping
a second sample app:

- **Ingest** — pick a location, show `IProgress<IngestionProgress>` live (stage,
  documents done, chunks written), a Budget picker (`Quick` / `Background` /
  `Unlimited`), Stop and Prune buttons, and the finished `IngestionRunResult`
  rendered in full including `SuspendReason`. Below it, `GetStatusAsync`'s counts,
  with `recipeStaleDocuments` and `staleDocuments` together labelled "known
  outstanding" — and a line of copy stating that a count of zero means nothing is
  *known* to be owed, not that the files on disk are unchanged (§12). The existing
  Search page then finds what the Ingest page wrote, with no code change.

  **The picker is platform-split, and that is the point of the page.** On Windows and
  Mac Catalyst it uses `IngestionSource.Folder` over a picked path. On **Android and
  iOS it uses `IngestionSource.Items`** over `FilePicker.Default.PickMultipleAsync`
  results, because a SAF `content://` tree is not a path `Directory.EnumerateFiles`
  can walk and an iOS picked URL is security-scoped (§11.3). The iOS delegate wraps
  `StartAccessingSecurityScopedResource` / `StopAccessingSecurityScopedResource`
  around **each** open, since `OpenAsync` is called twice per document. Shipping the
  desktop path on mobile would make the sample a demonstration of the one shape a
  mobile consumer cannot use.

The two background wirings are **sample projects under `ingestion/samples/`**, not
library code and not pages in the MAUI sample, because each one ships a permission
or a manifest entry into whatever app contains it:

- **iOS** — `BGTaskScheduler.Shared.Register` with a `BGProcessingTaskRequest`
  (`RequiresExternalPower`, `RequiresNetworkConnectivity`), an `ExpirationHandler`
  that calls `pipeline.RequestStop("bgtask:expiring")` and then
  `SetTaskCompleted(false)`, and the `BGTaskSchedulerPermittedIdentifiers` and
  `UIBackgroundModes` Info.plist keys. The iOS 26 `BGContinuedProcessingTask`
  variant, bridging `IProgress` to `NSProgress`, is **`#if IOS`-guarded**:
  `BGContinuedProcessingTask`, `BGContinuedProcessingTaskRequest` and
  `BGContinuedProcessingTaskRequestResources` ship in `Microsoft.iOS.dll` only —
  not Mac Catalyst, not tvOS — while the rest of `BackgroundTasks` is in all three.

  **`Submit` is checked, because "the OS said no" is the normal path.**
  `BGTaskScheduler.Submit(request, out NSError? error)` returns `bool`, and the
  sample branches on it rather than discarding it. A failure carries a
  `BGTaskSchedulerErrorCode`: `Unavailable` (1 — Background App Refresh is off for
  the app or device-wide, which is a user setting and not an error to retry),
  `TooManyPendingTaskRequests` (2 — cancel or coalesce before resubmitting),
  `NotPermitted` (3 — the identifier is missing from
  `BGTaskSchedulerPermittedIdentifiers`, i.e. a build mistake, and the sample says so
  in the log) and `ImmediateRunIneligible` (4). The sample surfaces each with the
  action a developer should take, because a submit whose return value is ignored is
  how a background task silently never runs.
- **Android** — a `Worker` promoting itself with
  `SetForegroundAsync(new ForegroundInfo(id, notification, (int)ForegroundService.TypeDataSync))`.
  Note the spelling: the enum members are `ForegroundService.TypeDataSync` (1) and
  `TypeMediaProcessing` (8192); `ForegroundService.DataSync` does not exist and will
  not compile. Manifest: `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_DATA_SYNC` and,
  on API 33+, `POST_NOTIFICATIONS`.

  Three further failure modes the sample handles, in the order a consumer meets them:

  1. **`ForegroundServiceStartNotAllowedException` on Android 12+ (API 31).** An app
     in the background may not start a foreground service, so `SetForegroundAsync`
     from a Worker that WorkManager scheduled while the app was backgrounded throws.
     The sample catches it, logs, and lets the work run **unpromoted** under the plain
     Worker's ~10-minute cap with `IngestionBudget.Background` — which is exactly the
     budget-suspends-on-a-committed-boundary shape SP3 exists to make safe — rather
     than failing the job. An expedited request or a foreground-initiated start is
     named in the README as the alternative.
  2. **API 36 is the .NET 10 default, and jobs started from a foreground service now
     count against runtime job quotas.** `net10.0-android` targets API 36 out of the
     box in .NET 10, and on 36 a background job kicked off *from* a foreground service
     is charged to the app's job quota rather than running freely; Google points
     user-triggered transfers at user-initiated data-transfer jobs instead. The sample
     therefore does the ingestion **inside** the Worker rather than having the Worker
     schedule further jobs, and the README states why.
  3. **The Android 15 (API 35) wall.** `dataSync` gets **six hours per rolling 24**,
     tracked separately from `mediaProcessing` and reset only when the app comes to
     the foreground; on expiry the system calls
     `Service.OnTimeout(int, ForegroundService)` and the process has a few seconds to
     `stopSelf()` before `RemoteServiceException`. The sample wires `OnTimeout` to
     `pipeline.RequestStop("fgs:timeout")` and returns.

  And on the other platform: Apple publishes **no** guaranteed duration for anything
  — read `UIApplication.BackgroundTimeRemaining` rather than assuming the
  community-measured ~30 s.

## 17. Verification items for the plan

Each carries a stated assumption; the plan confirms or adjusts and records the
result.

1. **One migration carrying both DDLs — do this first, and weigh it against the
   registry cost.** Confirm that `EdgeCollectionModelBuilder` plus MEVD's
   `CollectionModelBuilder.BuildDynamic` (or its current spelling) is reachable from
   SP3 with the same `MEVD9001` suppression SP2 already uses, **and that SP3's own
   twelve-line `IngestionSchemaOnlyGenerator` satisfies the generator parameter** —
   SP2's `SchemaOnlyEmbeddingGenerator` is `internal sealed class` at
   `EdgeVectorCollectionMigration.cs:100` and is unreachable, and the parameter may
   or may not be nullable. Then confirm that `new EdgeVectorSchema(model, name,
   options).BuildCreateSql()` emits byte-identical DDL to
   `AddVectorCollectionMigration`'s definition overload for the same definition and
   the same `EdgeVectorStoreCollectionOptions`. Assumption: yes; all three SP2 types
   are public and a stand-in is never invoked because SP3's vector property is
   `ReadOnlyMemory<float>` rather than a `string` source.

   **Two things push toward the fallback, and the plan weighs them together, not
   one at a time.** The fallback is two versions — `N` for the collection through
   SP2's own `AddVectorCollectionMigration` and `N + 1` for the state. Taking it
   costs §11.1's one-version claim, the README and the conflict test. It **buys**
   the registry: SP2's `VectorDataLifecycleObserver` would then merge SP3's FTS5
   sidecar, SP3's collection would appear in SP2's diagnostics report, and §12's
   SP3-owned merge plus its two extra options (`FtsMergeBudget`, `FtsMergePages`,
   `FtsMergeMaxIterations`) and event 926 could all be deleted. So this is not
   purely a "does it resolve" question: if the model build does not resolve cleanly
   the fallback is forced, and if it does resolve the fallback is still arguably
   the better trade. Record the decision and its reasoning, not just the outcome.

   Confirm at the same time that `EdgeVectorStoreCollectionOptions` is projectable
   onto `EdgeVectorStoreOptions` from outside the assembly (SP2's own
   `EdgeVectorCollectionSchemaFactory.ToStoreOptions` is `internal`, so SP3
   reimplements the five-line projection) and that a runtime collection's
   `EdgeVectorSchema` is reachable via `collection.GetService(typeof(EdgeVectorSchema))`
   **without opening a connection** — the 6011 startup check in §11.1 depends on both.
2. **`SpecialTokenOverhead` measured, before any golden file exists.** Encode a
   known string with `BertTokenizer` at the pinned `Microsoft.ML.Tokenizers` 2.0.0
   and compare `CountTokens` against `EncodeToIds(…, addSpecialTokens: true).Count`.
   Assumption: `CountTokens` excludes `[CLS]`/`[SEP]`, so the overhead is 2 and
   SP3's reserve is exactly right. **If it includes them**, the overhead is 0 and
   every budget in §8.1 shifts by two — which is why this precedes the goldens, not
   follows them.
3. **PdfPig's Apache-2.0 NOTICE obligation.** Owner decision: does a plain
   `PackageReference` (no vendored source) require a NOTICE file in Qavren's
   distribution model? Assumption: no, a dependency needs attribution in
   `THIRD-PARTY-NOTICES.md` and nothing more. Blocks the first publish, not the
   first commit.
4. **Asset resolution and trim behaviour — the CORE first, then the satellites.**
   Restore the solution on `windows-2025` and assert `PdfPig` → `lib/net9.0` for
   `net10.0` and for all four device TFMs, never `lib/netstandard2.0`. Then
   `dotnet publish -p:PublishTrimmed=true` the trim-smoke console **twice**:
   (a) with the core alone — `Qavren.Edge.Ingestion` plus its Markdig,
   System.IO.Hashing and ML.Tokenizers references — running the Markdown one-document
   path, and (b) with both satellites added. Record the warnings from each run
   separately. Assumption: (b) is clean because SP3 never touches
   `DocumentLayoutAnalysis.Export` or `OpenXmlValidator`, and (a) is clean because
   Markdig's only reflection is `Markdown.Version`'s `GetCustomAttribute` and its
   `Configure(string)` switch, neither of which SP3 calls. **(a) is the one to watch**:
   Markdig declares neither `IsTrimmable` nor `IsAotCompatible` (no root
   `Directory.Build.props`; `Markdig.targets` carries neither) and, unlike the two
   satellites, it is in every consumer's graph. **If warnings appear**, they are
   documented in the README rather than suppressed, and if the core's are the noisy
   ones the plan reconsiders whether the Markdown extractor belongs in a satellite of
   its own.
5. **Peak RSS on a device for the two worst shapes.** A 200-page PDF and a 20 MB
   DOCX, on a real Android device and an iOS simulator, measuring peak
   `GC.GetTotalAllocatedBytes` and OS-reported footprint against the whole-text
   buffer `ExtractedDocument` holds. Assumption: the page-at-a-time PDF path and
   the SAX DOCX path keep it inside a phone's budget at
   `MaxDocumentBytes = 32 MiB`. **If not**, lower `MaxDocumentBytes` and record the
   measured number in the README instead of the current default.
6. **`SkipMissingFonts = true` quality cost.** One real PDF with a non-embedded
   font, extracted with the flag on and off. Assumption: substituted rather than
   dropped glyphs, so the default stands. **If glyphs are dropped**, the default
   flips and the Android scan cost is documented instead.
7. **What the ordinal repair actually costs.** Insert a paragraph at the head of a
   500-chunk document with `RepairOrdinals` on and off, and measure wall time and
   FTS5 write volume. Assumption: the 499 `_au` trigger delete/insert pairs are
   three to four orders of magnitude cheaper than 499 embeddings and the default
   stands. Record the number; it is what an SP1 issue proposing
   `AFTER UPDATE OF <fts columns>` would be judged against.
8. **`Microsoft.Extensions.DataIngestion.Abstractions` dependency reality.**
   Confirm from the `.nuspec` that the Abstractions package has **zero**
   dependencies on net8.0/net9.0/net10.0 (it does carry
   `Microsoft.Bcl.AsyncInterfaces` and `System.Memory` on netstandard2.0/net462,
   which SP3 never targets). Assumption: yes. **If not**, the shim's isolation
   claim weakens and the plan reconsiders shipping it at all.
9. **Markdig source spans under `UsePreciseSourceLocation()`.** Confirm that
   `HeadingBlock.Span` and leaf-block spans index into the *original* source, not a
   normalised copy, so a chunk's text is a verbatim substring. Assumption: yes.
   **If not**, the Markdown extractor normalises first and records offsets against
   the normalised buffer, and the goldens follow.
10. **Device-lane `EmbeddedResource` fixture loading, including runtime DOCX zip
    assembly.** `ZipArchive` in `Create` mode over a `MemoryStream` on all four
    device runtimes. Assumption: works. **If not**, the DOCX device tests load a
    pre-zipped embedded blob and the determinism assertion stays host-only.
11. **The cost of `EdgeChunkTokenizer`'s derived index.** Benchmark the bounded
    binary search over `CountTokens` (§8.1) against a single direct
    `GetIndexByTokenCount` call, on an ARM64 device, over a 200 KB document at
    `MaxTokens = 222`. Measure calls-per-cut and wall time, and confirm the returned
    index is into the original string for NFD and mixed-case non-ASCII input.
    Assumption: 8–12 `CountTokens` calls per cut over a bounded span, costing well
    under the embedding time it feeds. **If it measures badly**, the documented
    answer is an additive `IEdgeTokenizer.IndexByTokenCount` overload taking
    `considerNormalization`, filed as an SP2 issue and taken only **after** SP2
    merges — never as an edit to an unmerged branch. Run this before the goldens,
    alongside item 2.
12. **`ChunkModelProfile` against SP2's presets — all six fields, ordinally.** In
    `Ingestion.Onnx.Tests`, assert each core profile's `Id`, `Dimensions`,
    `MaxSequenceLength`, `Pooling` name, `DocumentPrefix` and `QueryPrefix` equal the
    `EmbeddingPresets` member of the same name — `StringComparison.Ordinal`, trailing
    spaces included — and that `AddOnnxIngestion`'s projection round-trips every one.
    Assumption: they match as committed, which required two corrections this draft
    now carries: the ids are SP2's **lower-case** strings
    (`EmbeddingPresets.cs:19` is `"all-minilm-l6-v2-int8"`, line 33 is
    `"all-minilm-l6-v2-fp32"`), and `NomicEmbedTextV15Int8` carries **both** prefixes
    from `EmbeddingPresets.cs:94–95`. Either error alone would have tripped §11.2's
    equality guard on the first `AddOnnxIngestion()` call. **If SP2's two
    OWNER-CONFIRMATION-OWED preset values move** — bge-small's query prefix string,
    nomic's 512 ceiling versus the model card's 8192 — this test fails loudly and the
    core profiles and every affected recipe hash follow, which is the whole reason
    the duplication is tested rather than trusted.
13. **The vector property as `ReadOnlyMemory<float>`, end to end.** SP3 declares a
    pre-computed vector property and calls `GenerateAsync` itself (§9.5 a1).
    Confirm on real natives that `UpsertAsync` takes `ResolveVectorsAsync`'s
    non-generating path; that `SearchAsync("text", …)` and
    `HybridSearchAsync("text", …, …)` still work against that collection, which they
    should because `ResolveSearchVectorAsync` consults the store's
    `QueryEmbeddingGenerator` before the model's dispatcher
    (`EdgeVectorStoreCollection.cs:1014–1034`); and that
    `GeneratedEmbeddings<Embedding<float>>.Usage?.InputTokenCount` is non-null from
    SP2's `OnnxEmbeddingGenerator`. Assumption: all three hold. **If `Usage` is
    null**, `TokensEmbedded` uses the counted fallback and `tokensEmbeddedSource`
    reports `counted`; nothing else changes. **If string search does not work**, the
    definition keeps the pre-computed vector property and the README documents
    passing a vector to `SearchAsync`, because SP3 owning the embed call is what
    makes 6206 and 6207 separable at all.
14. **`heading_path` round-trip and the separator sanitiser.** Property-test
    `Split(Join(path)) == path` over generated headings that contain `›`, ` › `,
    control characters and leading/trailing whitespace, and confirm the sanitised
    heading is what both the FTS5 sidecar and `IngestedChunk.HeadingPath` see.
    Assumption: collapsing the separator sequence to a single space is lossless for
    display and sufficient for the split. **If a corpus is found where it is not**,
    the fallback is a second `heading_depth` integer column plus a per-level
    encoding, which costs a column and a migration and is why it is not the default.
15. **The `Sleeping` chain with SP3's own FTS5 merge.** Measure the whole chain on
    device — stop flag, checkpoint wait, SP3's bounded merge, SP2's observer, SP1's
    WAL checkpoint — for one SP3 collection and for one SP3 collection plus one
    `AddVectorCollectionMigration` collection. Assumption: ~1.75 s and ~3.75 s
    against iOS's ~5 s window. **If the merge routinely exhausts `FtsMergeBudget`
    without converging**, lower `FtsMergePages` rather than raising the budget, and
    record the measured numbers in the README.

## 18. Out of scope for sub-project 3

- **Background job scheduling.** SP1 non-goal, and the platform evidence agrees.
  Samples and a docs page only.
- **`Microsoft.Extensions.DataIngestion` as a core dependency.** Preview-only after
  11 months and 14 releases. The shim is separate and prerelease; revisit folding it
  in when MEDI ships stable.
- **HTML extraction.** Not in suite decision 9. A consumer writes an
  `IDocumentExtractor` in a few dozen lines, and `MediReaderAdapter` gives them
  MEDI's readers for free.
- **OCR.** Detection ships; recognition is a second model, a second licence and a
  second megabyte budget.
- **Image ingestion and an image element in the document model.** Decision 9 says
  images later.
- **Semantic-similarity chunking.** One embedding pass over every element *before*
  chunking is the worst trade available on a device with one ORT session.
- **A recursive-character splitter.** No first-party .NET implementation to copy,
  and with a real tokenizer the sentence-aware token window dominates it.
- **Channels and a parallel stage pipeline.** Higher peak RSS and non-deterministic
  failure ordering for a desktop-only overlap win, against a generator pinned at
  `MaxConcurrency = 1` and a single SQLite writer. Revisit on a benchmark.
- **`UTF.Unknown` charset detection.** MPL-1.1 in an MIT suite, plus the full legacy
  code-page tables on a phone, for a case this corpus shape does not hit.
- **Platform-native PDF extraction** (`PdfKit`, `PdfRenderer.TextContents`). Two
  extra code paths and two extra test matrices, and PdfPig would still be the
  Windows fallback.
- **In-place `vec0` vector UPDATE**, and any SP2 change to support it.
- **Mid-document checkpointing.** The hash diff makes resume a re-run, which is one
  code path instead of two.
- **A RAG retriever, re-ranker, or query-side helper.** Sub-project 4.
- **Chunk enrichers** (summary, keywords, sentiment, classification, alt-text).
  Each is an extra model call per chunk on a device with one session.
- **Down-migrations, index-rebuild tooling, compaction.** `RemoveSourceAsync` plus
  `EnsureCollectionDeletedAsync` is the whole recovery story.
- **A build-time index-publisher `dotnet` tool.** The prior art's shape and a real
  use case, but a sub-project 5 docs-and-tooling item, not an SP3 API.
- **Progress percentages and ETAs.** Enumeration is streaming; the denominator is
  unknown until the run ends.
- **Configurable key formats.** One key shape, documented and parseable. A
  configurable one is a migration hazard the first time somebody changes it.
- **`ActivitySource` / `Meter` / OpenTelemetry.** Neither SP1 nor SP2 ships either,
  and adding one here would make `System.Diagnostics.DiagnosticSource` a public
  dependency of an L2 package for telemetry nobody asked for.
