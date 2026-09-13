# Qavren.Edge

[![NuGet](https://img.shields.io/nuget/vpre/Qavren.Edge?label=Qavren.Edge)](https://www.nuget.org/packages/Qavren.Edge)
[![CI](https://github.com/qavren-oss/qavren-edge/actions/workflows/ci.yml/badge.svg)](https://github.com/qavren-oss/qavren-edge/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

On-device data and AI for .NET MAUI and .NET 10: **SQLite with `sqlite-vec`**,
**ONNX Runtime embeddings**, a **`Microsoft.Extensions.VectorData` store**,
**document ingestion**, and **local chat with retrieval-augmented generation**.
Everything runs in-process on the device. There is no service to call, nothing
leaves the phone, and nothing is downloaded without the user's consent.

Wired the way the rest of your app already is: `Microsoft.Extensions.*`
dependency injection, options, logging and hosting abstractions, plus the
`Microsoft.Extensions.AI` and `Microsoft.Extensions.VectorData` abstractions
where a standard one exists. No framework, no base classes.

## What you get

- **SQLite, batteries included.** The suite's own native build of SQLite with
  `sqlite-vec` compiled in (plain, or SQLCipher for encryption at rest),
  pragma-tuned connections, versioned migrations, and helpers for KNN and FTS5.
  Microsoft.Data.Sqlite, EF Core Sqlite, sqlite-net-pcl and Dapper work
  unchanged on top of it.
- **Embeddings on the device.** A `Microsoft.Extensions.AI`
  `IEmbeddingGenerator` over ONNX Runtime with presets that pin model hashes.
- **A vector store you can query like any other.** A
  `Microsoft.Extensions.VectorData` `VectorStore` over `vec0` and FTS5, with
  hybrid search fused by reciprocal rank fusion and LINQ filters pushed into SQL.
- **Ingestion that re-runs cheaply.** Plain text, Markdown, PDF and DOCX
  extractors, heading-aware and token-window chunkers, content hashing so an
  unchanged corpus costs one read per file, and a cancellable, checkpointed
  runner that yields under memory or thermal pressure.
- **Chat with citations.** A `Microsoft.Extensions.AI` `IChatClient` over ONNX
  Runtime GenAI with a memory budget sized from the KV cache, thermally paced
  streaming, and a RAG recipe that returns `[n]` citations as
  `CitationAnnotation` spans over the answer.
- **Failures you can act on.** Every exception carries a numbered code with a
  documented remediation, detected at startup rather than as a
  `DllNotFoundException` later.

## Install

```
dotnet add package Qavren.Edge          # hosting core + SQLite + the native library
dotnet add package Qavren.Edge.Maui     # MAUI lifecycle bridge and app paths
```

Add what you use:

```
dotnet add package Qavren.Edge.Embeddings.Onnx    # embeddings (pulls Qavren.Edge.Onnx)
dotnet add package Qavren.Edge.VectorData         # the vector store
dotnet add package Qavren.Edge.Ingestion          # ingestion core
dotnet add package Qavren.Edge.Ingestion.Onnx     # real tokenizer + resource-aware throttle
dotnet add package Qavren.Edge.Ingestion.Pdf      # PDF (PdfPig, Apache-2.0, opt-in)
dotnet add package Qavren.Edge.Ingestion.OpenXml  # DOCX
dotnet add package Qavren.Edge.Chat.Onnx          # local chat over ONNX Runtime GenAI
dotnet add package Qavren.Edge.Rag                # retrieval-augmented generation recipe
```

Encryption at rest: replace `Qavren.Edge.Sqlite.Native` with
`Qavren.Edge.Sqlite.Native.Cipher` and call `UseSqliteNativeCipher()`.
Referencing both native packages is a configuration error reported at startup.

Releases before 1.0 are prereleases; pass `--prerelease` to `dotnet add package`.

## Quick start

The whole stack, composed once in `MauiProgram.cs`. Outside MAUI the same
chain hangs off `services.AddQavrenEdge(edge => ...)`.

```csharp
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddOnnxEmbeddings(o => o.Preset = EmbeddingPresets.MiniLmL6V2Int8)
    .AddVectorStore()
    .AddIngestion(migrationVersion: 10)          // claims versions 10 and 11
    .AddOnnxIngestion()                          // tokenizer + throttle
    .AddPdfExtractor()                           // optional
    .AddOnnxChat(ChatPresets.Qwen3_600MInt4,     // there is no default preset
                 pipeline: chat => chat.UseRag())
    .AddRetriever(ChunkRetriever));
```

`AddIngestion` claims two consecutive migration versions, `N` for the
collection and `N + 1` for the state tables it owns.

`Qavren.Edge.Rag` retrieves through any `Microsoft.Extensions.VectorData`
collection. `AddVectorStoreRetriever<TKey, TRecord>(collectionName, project)`
is the one-liner for a typed record; the ingestion collection is dynamic, so it
goes through `DelegateRetriever`:

```csharp
static IEdgeRetriever ChunkRetriever(IServiceProvider services)
{
    var chunks = services.GetRequiredService<EdgeVectorStore>().GetDynamicCollection(
        "chunks",
        IngestionSchema.BuildDefinition(
            dimensions: 384, DistanceFunction.CosineDistance, fullTextIndexed: true));

    return new DelegateRetriever("ingestion-chunks", async (query, request, ct) =>
    {
        var sources = new List<RagSource>();

        await foreach (var hit in chunks.HybridSearchAsync(
            query, request.Keywords?.ToArray() ?? [], request.Top, cancellationToken: ct))
        {
            var chunk = IngestedChunk.FromRecord(hit.Record);
            sources.Add(new RagSource(chunk.Key, chunk.Text)
            {
                Title = chunk.Breadcrumb,
                Score = hit.Score ?? 0,
                ScoreKind = RetrievalScoreKind.Relevance,   // HybridSearchAsync fuses by RRF
            });
        }

        return sources;
    });
}
```

Index the corpus once, then again whenever it changes. A re-run over an
unchanged corpus costs one sequential read per document and nothing else:

```csharp
var pipeline = services.GetRequiredService<IIngestionPipeline>();

var run = await pipeline.RunAsync(
    IngestionSource.Folder(@"C:\docs", "*.md", recursive: true),
    options: new IngestionRunOptions { Budget = IngestionBudget.Background });

Console.WriteLine($"{run.Outcome}: +{run.ChunksAdded} -{run.ChunksRemoved} ({run.DocumentsSkipped} skipped)");
```

Then ask, streamed, with citations. Citations arrive once, on the final
metadata update, and their spans index the accumulated answer rather than any
single update, so accumulate first and slice your own buffer:

```csharp
var chat = services.GetRequiredService<IChatClient>();
var answer = new StringBuilder();

await foreach (var update in chat.GetStreamingResponseAsync("what does the warranty cover?"))
{
    Console.Write(update.Text);
    answer.Append(update.Text);

    foreach (var citation in update.Contents.SelectMany(c => c.Annotations ?? [])
                                            .OfType<CitationAnnotation>())
    foreach (var region in (citation.AnnotatedRegions ?? []).OfType<TextSpanAnnotatedRegion>())
    {
        // StartIndex/EndIndex are int? on the shipped MEAI 10.10.0 surface.
        if (region.StartIndex is not { } start || region.EndIndex is not { } end) continue;
        Console.WriteLine($"\n[{citation.Title}] {citation.Url} -> {answer.ToString(start, end - start)}");
    }
}
```

Search with no model in the loop is the vector store on its own:
`GetCollection<TKey, TRecord>` or `GetDynamicCollection`, then `SearchAsync`
(a `vec0` distance, lower is better) or `HybridSearchAsync` (an RRF score,
higher is better).

## Packages

Dependencies point strictly downward: a native library or host seam at the
bottom, a managed surface over it, and recipes that compose public surface
only. The ingestion satellites reference their core package and nothing else
crosses sideways.

| Package | TFMs | Purpose | Third-party dependencies |
|---|---|---|---|
| `Qavren.Edge` | `net10.0`, `net10.0-ios` | One-line install: Core + Sqlite + the plain native library | |
| `Qavren.Edge.Core` | `net10.0` | `AddQavrenEdge()`, host, lifecycle hub, paths, diagnostics, exceptions | `Microsoft.Extensions.*` DI, Logging, Hosting abstractions, Options |
| `Qavren.Edge.Maui` | `net10.0-android`, `-ios`, `-maccatalyst`, `-windows10.0.19041.0` | `UseQavrenEdge()`, platform lifecycle bridge, `FileSystem`-backed paths | `Microsoft.Maui.Controls` |
| `Qavren.Edge.Sqlite` | `net10.0` | `AddSqlite()`, `IEdgeDatabase`, migrations, vec and FTS helpers | `Microsoft.Data.Sqlite.Core` |
| `Qavren.Edge.Sqlite.Provider` | `net10.0`, `net10.0-ios` | The generated SQLitePCLRaw provider for the suite's native library | `SQLitePCLRaw.core` |
| `Qavren.Edge.Sqlite.Native` | `net10.0`, `net10.0-ios` | `UseSqliteNative()`; one native per RID with `sqlite-vec` compiled in | none; built in this repository |
| `Qavren.Edge.Sqlite.Native.Cipher` | `net10.0`, `net10.0-ios` | `UseSqliteNativeCipher()`; SQLCipher + libtomcrypt | none; built in this repository |
| `Qavren.Edge.Onnx` | `net10.0`, `net10.0-android`, `-ios`, `-maccatalyst` | `AddOnnx()`: session factory, execution-provider policy, consent-gated model provisioning, resource monitor | `Microsoft.ML.OnnxRuntime` 1.30.0 |
| `Qavren.Edge.Embeddings.Onnx` | same four | `AddOnnxEmbeddings()`, `IEmbeddingGenerator`, `EmbeddingPresets`, `IEdgeTokenizer` | `Microsoft.ML.Tokenizers` 2.0.0, `Microsoft.Extensions.AI` 10.10.0 |
| `Qavren.Edge.VectorData` | `net10.0` | `AddVectorStore()`; a `VectorStore` over `vec0` + FTS5 with reciprocal rank fusion | `Microsoft.Extensions.VectorData.Abstractions` 10.10.0 |
| `Qavren.Edge.Ingestion` | `net10.0` | `AddIngestion()`: document model, text and Markdown extractors, three chunkers, token budget, content hashing, incremental diff, runner | `Markdig` 1.3.2, `System.IO.Hashing`, `Microsoft.ML.Tokenizers` 2.0.0 |
| `Qavren.Edge.Ingestion.Pdf` | `net10.0` | `AddPdfExtractor()`; page-at-a-time, stream-only | `PdfPig` 0.1.16 (**Apache-2.0**, which is why this is opt-in) |
| `Qavren.Edge.Ingestion.OpenXml` | `net10.0` | `AddDocxExtractor()` | `DocumentFormat.OpenXml` 3.5.1 (MIT; separate for size) |
| `Qavren.Edge.Ingestion.Onnx` | `net10.0` | `AddOnnxIngestion()`: the embedding preset's tokenizer as the chunk tokenizer, a throttle over the resource monitor | none new |
| `Qavren.Edge.Ingestion.DataIngestion` | `net10.0` | The `Microsoft.Extensions.DataIngestion` shim, both directions. Prerelease only | `Microsoft.Extensions.DataIngestion.Abstractions` 10.10.0-preview |
| `Qavren.Edge.Chat.Onnx` | `net10.0`, `net10.0-android`, `-ios`, `-maccatalyst` | `AddOnnxChat()`: an `IChatClient` over ONNX Runtime GenAI, KV-cache-aware memory budget, consent-gated provisioning, thermally paced streaming | `Microsoft.ML.OnnxRuntimeGenAI` 0.15.2, `Microsoft.Extensions.AI` 10.10.0 |
| `Qavren.Edge.Rag` | `net10.0` | `UseRag()`, `AddVectorStoreRetriever()`, numbered context under a token budget, `[n]` citations, `ExtractiveChatClient` | `Microsoft.Extensions.AI` 10.10.0, `Microsoft.Extensions.VectorData.Abstractions` 10.10.0 |

`Qavren.Edge.Chat.Onnx` has no Windows platform TFM on purpose: ONNX Runtime
GenAI ships no Windows platform managed asset, so a Windows app binds the
`net10.0` build and takes `runtimes/win-x64` or `win-arm64` by RID, exactly as
`Qavren.Edge.Sqlite` is consumed.

## Supported platforms

Every minimum below is the native dependency's own floor, not one this suite
chose.

| Adopting | Android | iOS / Mac Catalyst | Why |
|---|---|---|---|
| SQLite and hosting only | 21 | 15.0 | The MAUI SDK defaults |
| + embeddings, vector store, ingestion | **24** | **15.1** | ONNX Runtime 1.30.0's floors |
| + chat | **24** | **15.4** | ONNX Runtime GenAI's managed asset targets iOS 15.4; Mac Catalyst inherits it |

Windows x64 and arm64, Linux x64 and arm64, and macOS arm64 are supported for
plain .NET 10. `osx-x64` is unsupported for anything that loads ONNX Runtime,
which ships no `osx-x64` native; `Qavren.Edge.Onnx` reports
`OnnxUnsupportedRuntime` (5006) at startup instead of failing later. Chat ships
only `arm64-v8a` and `x86_64` on Android, so an `armeabi-v7a` device gets
everything except chat and is told so at startup with `ChatUnsupportedRuntime`
(7004).

## On-device chat: what to expect

- **Nothing downloads implicitly.** `IChatModelProvisioner.Plan()` reports the
  byte count and the SPDX licence of a preset so you can show a consent screen;
  only `ProvisionAsync` moves bytes. Model files are verified against pinned
  SHA-256 digests.
- **Memory is the constraint, not throughput.** The context window is sized
  from a KV-cache-aware budget and steps down on small devices. The smaller
  download is not the cheaper resident model: `Qwen3_600MInt4` is 495 MB on
  disk but costs 112 KiB of KV cache per token; `Llama32_1BInstructInt4` is
  1.24 GB but 32 KiB per token.
- **Turns serialise.** The GenAI C API is not thread safe, so one gate per
  model orders turns; a fifth queued turn is refused with `ChatBusy` (7105).
  Streaming is thermally paced and terminates cooperatively under memory
  pressure or app suspension.
- **iOS needs two entitlements in your app** for a multi-gigabyte model:
  `com.apple.developer.kernel.increased-memory-limit` and
  `com.apple.developer.kernel.extended-virtual-addressing`. They are plist
  lines in the consuming app, not code in these packages. The sample app shows
  the wiring.
- **Android cost.** Adding chat adds a 21.5 MB AAR whose `libmat.so` is
  32.7 MB per ABI. ONNX Runtime GenAI 0.15.2 emits `warning XA0141` (16 KB page
  alignment for Android 16) on that library; it does not fail the build and
  chat runs on Android 16 devices. The fix is an upstream re-link.

## Error codes

Every `EdgeException` sets `HelpLink` into
[`foundation/docs/errors.md`](foundation/docs/errors.md), keyed on the numeric
code. Ranges never overlap and are never reused:

| Range | Packages |
|---|---|
| 1001-4999 | `Core`, `Sqlite` |
| 5000-5299 | `Onnx`, `Embeddings.Onnx`, `VectorData` |
| 6000-6299 | `Ingestion` and its satellites |
| 7000-7299 | `Chat.Onnx`, `Rag` |

## Documentation

| Area | README | Design records |
|---|---|---|
| Hosting core and SQLite | [`foundation/README.md`](foundation/README.md) | [`foundation/docs/adr/`](foundation/docs/adr/) |
| Embeddings and the vector store | [`embeddings/README.md`](embeddings/README.md) | [`embeddings/docs/adr/`](embeddings/docs/adr/) |
| Ingestion | [`ingestion/README.md`](ingestion/README.md) | [`ingestion/docs/adr/`](ingestion/docs/adr/) |
| Chat and RAG | [`chat/README.md`](chat/README.md) | [`chat/docs/adr/`](chat/docs/adr/) |

Every package also carries its own README, shown on nuget.org. A sample MAUI
app with search, ingest, chat and ask pages lives in
[`foundation/samples/`](foundation/samples/).

## Building from source

```
dotnet build QavrenEdge.slnx -c Release
```

The managed build needs no native artifacts. Running the SQLite tests needs the
native library for your host; see
[`foundation/native/README.md`](foundation/native/README.md). Maintainer
notes, including how releases are cut, are in
[`docs/maintainers.md`](docs/maintainers.md).

## Versioning

Versions come from git tags. Releases before 1.0 are prereleases and the
public API may still change between them; 1.0 follows the documentation site
and the benchmark suite. Every GitHub release ships the packages, the native
library archives, an SPDX SBOM and SHA-256 checksums.

## Licence

MIT. See [`LICENSE`](LICENSE). `Qavren.Edge.Ingestion.Pdf` is the one package
whose dependency is not MIT: PdfPig is Apache-2.0, which is why that extractor
is a separate, opt-in package. See
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).

Qavren.Edge is a Qavren Solutions LLC project.
