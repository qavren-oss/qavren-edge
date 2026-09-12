# Qavren.Edge

Free, MIT-licensed .NET packages that make **SQLite + sqlite-vec + ONNX Runtime**
a first-class citizen in .NET MAUI and plain .NET 10.

Four sub-projects ship today: the hosting core and the SQLite provider
(`foundation/`), embeddings and the vector store (`embeddings/`),
file-to-collection ingestion (`ingestion/`), and on-device chat with a
retrieval-augmented recipe (`chat/`). Everything runs in-process, on the
device, with no service to call.

## Layout

| Folder | Sub-project | README | ADRs |
|---|---|---|---|
| `foundation/` | 1: Core, Maui, Sqlite, Sqlite.Provider, Sqlite.Native(.Cipher), meta | [`foundation/README.md`](foundation/README.md) | [`foundation/docs/adr/`](foundation/docs/adr/) |
| `embeddings/` | 2: Onnx, Embeddings.Onnx, VectorData | [`embeddings/README.md`](embeddings/README.md) | [`embeddings/docs/adr/`](embeddings/docs/adr/) |
| `ingestion/` | 3: Ingestion and its four satellites | [`ingestion/README.md`](ingestion/README.md) | [`ingestion/docs/adr/`](ingestion/docs/adr/) |
| `chat/` | 4: Chat.Onnx, Rag | [`chat/README.md`](chat/README.md) | [`chat/docs/adr/`](chat/docs/adr/) |
| `docs/` | 5: docs site + 1.0 (not started) | | |

## Packages

Layers are the suite's dependency rule, not decoration. **L0** owns a native
library or a host seam, **L1** is the managed surface over one of those, and
**L2** is a recipe that composes L1 public surface only. Dependencies point
strictly downward. The four ingestion satellites are part of one L2 recipe and
do reference its core package, `Qavren.Edge.Ingestion`; nothing else crosses
sideways.

| Package | Layer | TFMs | Purpose | Third-party it pulls in |
|---|---|---|---|---|
| `Qavren.Edge.Core` | L0 | `net10.0` | `AddQavrenEdge()`, host, lifecycle hub, paths, diagnostics, exceptions | `Microsoft.Extensions.*` DI/Logging/Hosting abstractions plus Options |
| `Qavren.Edge.Maui` | L0 | `net10.0-android`, `-ios`, `-maccatalyst`, `-windows10.0.19041.0` | `UseQavrenEdge()`, platform lifecycle bridge, `FileSystem`-backed paths | `Microsoft.Maui.Controls` |
| `Qavren.Edge.Sqlite` | L0 | `net10.0` | `AddSqlite()`, `IEdgeDatabase`, migrations, vec/FTS helpers | `Microsoft.Data.Sqlite.Core` |
| `Qavren.Edge.Sqlite.Provider` | L0 | `net10.0`, `net10.0-ios` | Generated `SQLite3Provider_qedge : ISQLite3Provider` | `SQLitePCLRaw.core` |
| `Qavren.Edge.Sqlite.Native` | L0 | `net10.0`, `net10.0-ios` | `UseSqliteNative()`, one native per RID with sqlite-vec compiled in | none; the natives are built in this repo |
| `Qavren.Edge.Sqlite.Native.Cipher` | L0 | `net10.0`, `net10.0-ios` | `UseSqliteNativeCipher()`, SQLCipher + libtomcrypt | none; built in this repo |
| `Qavren.Edge` | meta | `net10.0`, `net10.0-ios` | One-line install for the common case | |
| `Qavren.Edge.Onnx` | L0 | `net10.0`, `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst` | ORT hosting: session factory, EP policy, model provisioning, `AddOnnx()` | `Microsoft.ML.OnnxRuntime` 1.30.0 |
| `Qavren.Edge.Embeddings.Onnx` | L1 | same four | `AddOnnxEmbeddings()`, a `Microsoft.Extensions.AI` `IEmbeddingGenerator`, `EmbeddingPresets`, `IEdgeTokenizer` | `Microsoft.ML.Tokenizers` 2.0.0, `Microsoft.Extensions.AI` 10.10.0 |
| `Qavren.Edge.VectorData` | L1 | `net10.0` | `AddVectorStore()`, a clean-room MEVD `VectorStore` over `vec0` + FTS5 with reciprocal rank fusion | `Microsoft.Extensions.VectorData.Abstractions` 10.10.0 |
| `Qavren.Edge.Ingestion` | L2 | `net10.0` | `AddIngestion()`, the document model, the plain-text and Markdown extractors, three chunkers, the token budget, xxHash128 content hashing, the incremental diff, the runner, the state migration | `Markdig` 1.3.2, `System.IO.Hashing`, `Microsoft.ML.Tokenizers` 2.0.0 |
| `Qavren.Edge.Ingestion.Pdf` | L2 | `net10.0` | `AddPdfExtractor()`, `PdfTextExtractor`, page-at-a-time and stream-only | `PdfPig` 0.1.16, **Apache-2.0** rather than MIT, which is why it is opt-in (ADR 0009) |
| `Qavren.Edge.Ingestion.OpenXml` | L2 | `net10.0` | `AddDocxExtractor()`, `DocxTextExtractor` | `DocumentFormat.OpenXml` 3.5.1; MIT, so the split is size, the nupkg is 14.66 MB |
| `Qavren.Edge.Ingestion.Onnx` | L2 | `net10.0` | `AddOnnxIngestion()`, `EdgeChunkTokenizer`, `ResourceMonitorThrottle`, the chunk budget derived from the resolved `EmbeddingPreset` | none new; it bridges to `Qavren.Edge.Embeddings.Onnx` |
| `Qavren.Edge.Ingestion.DataIngestion` | L2 | `net10.0` | The `Microsoft.Extensions.DataIngestion` shim, both directions (ADR 0011). Prerelease only | `Microsoft.Extensions.DataIngestion.Abstractions` 10.10.0-preview.1.26459.2 |
| `Qavren.Edge.Chat.Onnx` | L1 | `net10.0`, `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst` | `AddOnnxChat()`, an `IChatClient` over ONNX Runtime GenAI, a KV-cache-aware memory budget, consent-gated provisioning, thermal-paced streaming | `Microsoft.ML.OnnxRuntimeGenAI` 0.15.2, `Microsoft.Extensions.AI` 10.10.0 |
| `Qavren.Edge.Rag` | L2 | `net10.0` | `UseRag()`, `AddVectorStoreRetriever()`, `RagChatClient : DelegatingChatClient`, numbered context under a token budget, `[n]` resolved into `CitationAnnotation`, `ExtractiveChatClient` | `Microsoft.Extensions.AI` 10.10.0, `Microsoft.Extensions.VectorData.Abstractions` 10.10.0 |

**There is no Windows platform TFM for `Qavren.Edge.Chat.Onnx`**, and that is
not an omission: GenAI ships no Windows platform managed asset, so a Windows
consumer binds its `lib/net8.0` asset and takes `runtimes/win-x64` or
`win-arm64` by RID, exactly as `Qavren.Edge.Sqlite` is already consumed. On a
Linux build host the four-TFM projects restore as `net10.0;net10.0-android`
only, because the iOS and Mac Catalyst SDK packs do not exist there.

## Getting started

The whole stack, composed once. Each line names the sub-project that owns it;
nothing below is optional except where it says so.

```csharp
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()                                                  // SP1
    .AddSqlite(o => o.DatabaseName = "notes.db")                        // SP1
    .AddOnnxEmbeddings(o => o.Preset = EmbeddingPresets.MiniLmL6V2Int8) // SP2
    .AddVectorStore()                                                   // SP2
    .AddIngestion(migrationVersion: 10)                                 // SP3, claims 10 AND 11
    .AddOnnxIngestion()                                                 // SP3, tokenizer + throttle
    .AddPdfExtractor()                                                  // SP3, optional
    .AddOnnxChat(ChatPresets.Qwen3_600MInt4,                            // SP4, no default preset
                 pipeline: chat => chat.UseRag())
    .AddRetriever(ChunkRetriever));                                     // SP4 over SP3's collection
```

`AddIngestion` claims **two consecutive migration versions**, `N` for the
collection and `N + 1` for the three state tables it owns (ADR 0012). Outside
MAUI the same chain hangs off `services.AddQavrenEdge(edge => ...)`.

`Qavren.Edge.Rag` retrieves through any `Microsoft.Extensions.VectorData`
collection. `AddVectorStoreRetriever<TKey, TRecord>(collectionName, project)`
is the one-liner for a typed record; the ingestion collection is **dynamic**,
so the seam here is `DelegateRetriever`:

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
unchanged corpus costs one sequential 64 KiB-buffered read per document and
nothing else:

```csharp
var pipeline = services.GetRequiredService<IIngestionPipeline>();

var run = await pipeline.RunAsync(
    IngestionSource.Folder(@"C:\docs", "*.md", recursive: true),
    options: new IngestionRunOptions { Budget = IngestionBudget.Background });

Console.WriteLine($"{run.Outcome}: +{run.ChunksAdded} -{run.ChunksRemoved} ({run.DocumentsSkipped} skipped)");
```

Then one ask, streamed, with citations. Citations arrive **once**, on the
final metadata update, and their spans index the accumulated answer rather
than any single update, so accumulate first and index your own buffer:

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

Search with no model in the loop stays plain sub-project 2:
`GetCollection<TKey, TRecord>` or `GetDynamicCollection`, then `SearchAsync`
(a `vec0` distance, lower is better) or `HybridSearchAsync` (an RRF score,
higher is better).

## Supported minimums

Each layer raises the floor, and every number below is the native
dependency's own rather than one this suite picked:

| Adopting | Android | iOS / Mac Catalyst | Where it comes from |
|---|---|---|---|
| foundation only | 21 | 15.0 | The MAUI SDK defaults; no foundation csproj pins `SupportedOSPlatformVersion`. Recorded in `embeddings/README.md` |
| + embeddings, vector store, ingestion | **24** | **15.1** | ONNX Runtime 1.30.0's own floors, ADR 0008 in `embeddings/`. Sub-project 3 adds no floor of its own: every ingestion package is `net10.0` |
| + chat | **24** | **15.4** | GenAI's managed asset ships at `lib/net9.0-ios15.4` and Mac Catalyst inherits the iOS slice through the RID graph, ADR 0013 in `chat/` |

An app that must stay on iOS 15.1 keeps the database, the embeddings and
ingestion, and cannot add chat. `osx-x64` is unsupported outright rather than
merely untested: ORT 1.30.0 ships no `osx-x64` native at all, and
`Qavren.Edge.Onnx` raises `OnnxUnsupportedRuntime` (5006) at startup instead
of failing later with a `DllNotFoundException`. Chat additionally ships only
`arm64-v8a` and `x86_64`, so an `armeabi-v7a` device gets everything except
chat and is told so at startup with `ChatUnsupportedRuntime` (7004).

## Error codes

Every `EdgeException` sets `HelpLink` into
[`foundation/docs/errors.md`](foundation/docs/errors.md), keyed on the bare
numeric code. The ranges never overlap and are never reused:

- **1001-4999** sub-project 1 (`Core`, `Sqlite`); 1001-4001 allocated.
- **5000-5299** sub-project 2 (`Onnx`, `Embeddings.Onnx`, `VectorData`).
- **6000-6299** sub-project 3 (`Ingestion` and its four satellites).
- **7000-7299** sub-project 4 (`Chat.Onnx`, `Rag`).

## Status

| # | Sub-project | Packages | State |
|---|---|---|---|
| 1 | SQLite foundation + hosting core | `Core`, `Maui`, `Sqlite`, `Sqlite.Provider`, `Sqlite.Native(.Cipher)`, meta | **Shipped** |
| 2 | Embeddings + vector store | `Onnx`, `Embeddings.Onnx`, `VectorData` | **Shipped** |
| 3 | Ingestion | `Ingestion`, `.Pdf`, `.OpenXml`, `.Onnx`, `.DataIngestion` | **Shipped** |
| 4 | Chat + RAG | `Chat.Onnx`, `Rag` | **Shipped** |
| 5 | Docs + 1.0 | docs site, benchmarks, NuGet 1.0 | **Next** |

## What has been measured

**The tier-0 GenAI smoke passes on all six legs.** Tier 0 links ORT GenAI and
generates one token over the committed tiny fixture on `tier0-windows`,
`tier0-linux`, `tier0-winui`, `tier0-android`, `tier0-ios` and
`tier0-maccatalyst`, and every leg is PASS as of workflow run
[`34637304766`](https://github.com/qavren-oss/qavren-edge/actions/runs/34637304766)
on `feat/sp4-chat` commit `58dde19`. The two console legs print the
`key=value` block (`ichatClientAssignable=True`, `firstTokenId=33`); the four
device legs run the same body as the xUnit fact
`Tier0SmokeFacts.LoadsAndGeneratesOneToken`. What tier 0 does not close is the
packaged (MSIX) WinUI path: this repo has no signing identity, so that leg ran
unpackaged. Tier 3 is nightly and `workflow_dispatch` only, on two lanes that
each fetch a pinned model and verify its SHA-256 on every run including cache
hits: `model-tests` (the real MiniLM weights) and `chat-model-tests` (Qwen3
0.6B int4). The trim-smoke evidence for ingestion is in `ingestion/README.md`.

**The mobile posture is memory and thermals, not throughput.** Chat sizes its
context from a KV-cache-aware budget rather than a fixed number, and the
smaller download is not the cheaper resident model: `Qwen3_600MInt4` is 495 MB
against `Llama32_1BInstructInt4`'s 1.241 GB, but costs 112 KiB of KV per token
against Llama's 32 KiB, so the ladder down to a 1024-token context buys Qwen
26% and Llama 6%. Turns serialise behind one gate per model, because the GenAI
C API is not thread safe, and a fifth queued turn is refused with `ChatBusy`
(7105); streaming is thermally paced and terminates cooperatively. Two iOS
entitlements, `com.apple.developer.kernel.increased-memory-limit` and
`com.apple.developer.kernel.extended-virtual-addressing`, are the only real
lever on a nominal-6 GB phone, and both are plist lines in the **consuming
app**, not code in these packages. Nothing downloads implicitly:
`IChatModelProvisioner.Plan()` feeds a consent screen carrying the byte count
and the SPDX licence, and only then does `ProvisionAsync` move a byte. On
Android, adding chat costs a 21.5 MB AAR whose `libmat.so` is 32.7 MB per ABI
and emits `warning XA0141` (Android 16 will require 16 KB page sizes) on every
device-lane build. That warning is upstream in
`microsoft.ml.onnxruntimegenai` 0.15.2, does not fail the build, and chat
still runs on the Android 16 tier-0 device; the fix is a re-linked upstream
binary, never a suppression here.

## Build

```powershell
dotnet build QavrenEdge.slnx -c Release
```

The managed build needs no native artifacts. To run the SQLite tests you must
first build the native library for your host, see `foundation/native/README.md`.

## Bootstrap checklist (owner actions, not automatable)

The GitHub organisation is `qavren-oss` and the repo is `qavren-oss/qavren-edge`
(both created 2026-09-10). The bare name `qavren` is a squatted **user** account,
never use it in a remote URL, a workflow, or a tool argument.

- [x] **`main` exists and CI reports on it.** Sub-projects 1 and 2 merged through PRs #1 and #15.
- [ ] **Apply branch protection**, now that `ci.yml` and `native.yml` have each reported and their
      contexts are known to GitHub:

      ```powershell
      gh api --method PUT repos/qavren-oss/qavren-edge/branches/main/protection --input .github/branch-protection.json
      ```

      Required contexts are `ci-gate` and `natives / native-gate` (see
      `.github/branch-protection.json`); both gate jobs always report, so a managed-only
      PR is never blocked waiting on a skipped native build.
- [ ] Reserve the `Qavren.` NuGet ID prefix by **emailing `account@nuget.org`** with the
      nuget.org owner display name (`Qavren`, admin `stevenfackley`) and the requested prefix.
      There is no web form. Do this after the first package is published with a `license`
      expression and an embedded `icon`.
- [ ] Add the repo to `_tooling/lib/repos.psd1` (`ActiveCI`) and regenerate the roster.
- [ ] Add the `NUGET_API_KEY` repository secret before the first `v*` tag, or `release.yml` fails at the push step.

## Licence

MIT. See `LICENSE`. `Qavren.Edge.Ingestion.Pdf` is the one package whose
dependency is not MIT: PdfPig is Apache-2.0, which is why that extractor is a
separate, opt-in package. See `THIRD-PARTY-NOTICES.md`.
