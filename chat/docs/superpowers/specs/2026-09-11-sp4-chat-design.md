# Qavren.Edge — Sub-project 4: chat + RAG

Date: 2026-09-11
Status: draft for planning. Branch `feat/sp4-chat` in the worktree
`C:\Users\steve\projects\qavren-edge-sp4`, based on `feat/sp2-embeddings` and
rebased onto `main` once SP2 merges. Until that rebase SP4 references SP2 by
public type name only.
Sub-project 1 is MERGED and is the contract; sub-project 2 is on
`feat/sp2-embeddings` and its **code** is the contract where it and its spec
disagree. The two additive edits this sub-project needs to prior sub-projects are
enumerated in §5 and nowhere else.
Owner: Steve Ackley (Qavren Solutions LLC)

## 1. Summary

Sub-project 4 turns the semantic-search stack into an answering one: a
Microsoft.Extensions.AI `IChatClient` over ONNX Runtime GenAI, and a RAG recipe
that retrieves through SP2's hybrid `vec0` + FTS5 store and streams a cited
answer. Two packages, matching roadmap row 4 exactly — `Qavren.Edge.Chat.Onnx`
(L1) and `Qavren.Edge.Rag` (L2).

Six decisions shape everything below, and the first three contradict assumptions
the suite has been carrying since SP1's decision table. They are stated first
because every later section depends on them.

1. **We wrap ORT GenAI's primitives, not its `IChatClient`.**
   `Microsoft.ML.OnnxRuntimeGenAI.Managed` 0.15.2 already ships
   `OnnxRuntimeGenAIChatClient : IChatClient`, and SP4 deliberately does not use
   it. It **skips `search.max_length` entirely when `EnableCaching` is true**
   (its own comment: "don't set this if we're caching, since we want to be able
   to generate more tokens later"), so a cached conversation's KV cache is
   allocated against the model's declared `context_length` — 131072 on a stock
   Llama export — which removes the only KV-cache memory lever the runtime
   exposes. SP4's answer is not "set it every turn": `SetSearchOption` lives on
   `GeneratorParams` and is consumed at `Generator` construction, so there is no
   API to change it on a live generator and no honest way to. SP4 splits the two
   jobs that one value was doing — a cached generator is built once with
   `max_length = resolvedContext` as the **memory** cap, and the per-turn
   **output** cap is a managed counter in the decode loop (§6.6, §10 e–f). It also
   keeps exactly one `Interlocked`-swapped `Generator`,
   so a second concurrent turn allocates a second full KV cache; it checks
   cancellation only between decoded tokens, so prefill is uncancellable; it
   matches stop sequences by **single-decoded-token string equality**, so a
   multi-token stop string never fires; it swallows a failed `max_length` set;
   and it is compiled against `Microsoft.Extensions.AI.Abstractions` 9.8.0 while
   this repo pins 10.10.0. Every one of SP4's charter items — memory budgets,
   jetsam guards, honest tokens/sec — requires being inside the decode loop.
   Wrapping `Model`/`Config`/`Tokenizer`/`Generator` **is** decision 5's "wrap,
   never rebuild": their MEAI adapter is not ORT GenAI.

2. **ORT GenAI has no CoreML execution provider and no NNAPI execution
   provider.** Its canonical provider set is
   CPU / cuda / DML / QNN / WebGPU / OpenVINO / VitisAI / RyzenAI / NvTensorRtRtx.
   On iOS, Mac Catalyst and Android, chat is **CPU int4 and nothing else**. The
   `WeakFrameworks="CoreML"` on the iOS `NativeReference` is a link-time weak
   reference, not a provider. SP2's `EdgeExecutionProvider { Cpu, XnnPack, CoreMl }`
   therefore says nothing about chat and is deliberately not reused, and SP2's
   `OnnxOptions.ShareThreadPool` does not reach the chat model either — GenAI
   builds its session options natively from `genai_config.json`, so SP2 design
   §1607's claim that a chat model would land on one shared thread pool is an
   overstatement SP4 corrects rather than inherits (§5, §14.3).

3. **Constrained decoding is compiled out of the shipped mobile natives, and the
   failure is silent.** At tag v0.15.2 `CreateGuidanceLogitsProcessor` returns
   `nullptr` with at most a log line when `USE_GUIDANCE` is off — it does not
   throw; the throwing behaviour the docs describe landed on `main` — and
   `tools/ci_build/github/apple/default_full_ios_framework_build_settings.json`
   passes `--parallel --build_apple_framework --skip_tests --skip_wheel` and no
   `--use_guidance`. Passing a `ChatResponseFormatJson` straight to
   `SetGuidance` would hand a caller unconstrained output labelled as
   schema-constrained. But the Windows/Linux/macOS core pipelines **do** pass
   `--use_guidance`, so an unconditional throw is wrong on desktop. SP4 ships
   `EdgeGuidancePolicy` with `Disabled` as the default and a **positive-control
   probe** — generate at most eight tokens under a schema admitting exactly one
   string and compare the decoded output, never catch an exception — whose
   result is published as the diagnostics key `guidanceEnforced`.

4. **The memory budget is arithmetic that a measurement is allowed to beat.**
   `required = weights + kv(context) + workspace + reserve`, where
   `kv = layers × 2 × kvHeads × headSize × context × bytesPerElement`. When a
   preset carries a `MeasuredPeakBytes` from a real device, the budget takes
   `max(arithmetic, measured + reserve)` and records which term bound. The
   `+ reserve` is not decoration: a measured peak is the *model's* resident
   footprint on the device it was measured on, and it says nothing about the
   headroom this app still owes its UI, its images and the SQLite page cache —
   which is exactly what `ReserveBytes` is. §9.3 is the normative formula and
   every other statement of it in this document is that formula.

   **Units, stated once and applied everywhere: every byte figure in this document
   is binary — KiB, MiB, GiB — unless it quotes a publisher's own decimal figure,
   in which case it is marked "decimal" at the point of use.** This is not
   pedantry: the calibration below is an agreement to about 1%, the device floor in
   §6.4 is a constant compared against three different platform totals, and a
   MB/MiB slip is 4.9% — larger than either margin. Every constant in the options
   classes is written binary (`192L * 1024 * 1024`), and every doc comment and
   every prose figure now says MiB or GiB to match.

   The arithmetic is calibrated, not guessed. Llama-3.2-1B int4 computes **1167 MiB**
   of weights — that is the publisher's `model_size_mb`, which is MiB:
   1,224,094,720 bytes, the same file its card renders as "~1.24 GB" decimal — plus
   32 KiB/token × 4096 = **128 MiB** of KV, for a model footprint of **1295 MiB**,
   against a **measured 1280 MiB** peak RSS on a vivo X300. That is within **1.2%**,
   and it is a like-for-like comparison: weights plus KV against a measured peak,
   with `WorkspaceBytes` and `ReserveBytes` deliberately outside it because they are
   the app's headroom and not the model's footprint. It is also the reason
   `KvCacheBytesPerElement` defaults to 2 rather than 4: at fp32 the KV term doubles
   to 256 MiB, the footprint becomes 1423 MiB, and the arithmetic is 11% over a
   measurement it should be tracking.

5. **Nothing downloads implicitly.** The default preset is 1.24 GB. App Store
   Review 4.2.3(ii) requires disclosing the size of a first-launch download and
   prompting before it, Google Play's base module is capped at 500 MB compressed
   (200 MB is only the mobile-data warning threshold), and neither ceiling has a
   workaround inside v1. `AcquireAsync` on an unprovisioned model throws
   `ChatModelNotProvisioned` carrying the byte count; a download happens only
   through `IChatModelProvisioner.Plan()` → a consent screen →
   `ProvisionAsync`. There is **no default preset**: `AddOnnxChat` takes one as a
   required argument, because a silently-chosen model under the Llama 3.2
   Community Licence is not a default anyone should inherit.

6. **The RAG recipe is a `DelegatingChatClient`, not a façade.** It is
   `RagChatClient` plus `ChatClientBuilder.UseRag()`, over MEAI and MEVD
   abstractions only — no reference to `Qavren.Edge.Chat.Onnx`, none to
   `Qavren.Edge.VectorData`. So it composes with `UseLogging()` and
   `UseOpenTelemetry()`, it survives `chatClient.AsAIAgent()` and
   `chatClient.AsChatCompletionService()` with zero glue packages, and it runs
   against an Azure OpenAI client and a Qdrant collection unchanged. A second
   entry point returning a bespoke answer type would have composed with nothing.

Two things SP4 gets for free by getting one method right. Semantic Kernel's
`GetModelId()` is literally `client.GetService<ChatClientMetadata>()?.DefaultModelId`;
Agent Framework's `ChatClientAgent` avoids double-wrapping by calling
`chatClient.GetService<FunctionInvokingChatClient>()`. `GetService` is the entire
integration surface for both, and §6.6 specifies it as a contract rather than an
implementation detail.

## 2. Suite decisions that apply

| # | Decision | How SP4 honours it |
|---|---|---|
| 2 | L0 → L1 → L2 layering | `Chat.Onnx` is L1 over SP2's L0 `Onnx`; `Rag` is L2 over MEAI + MEVD abstractions and references neither ONNX package. `Rag` works with any `IChatClient` and any `VectorStoreCollection`, which is the layering rule made testable rather than asserted. |
| 5 | Wrap `Microsoft.ML.OnnxRuntime`, never rebuild it | One `PackageReference` to `Microsoft.ML.OnnxRuntimeGenAI` 0.15.2. No `USE_GUIDANCE=ON` rebuild (which would need a Rust/Corrosion toolchain), no custom EP, no `.ort` conversion. ORT itself stays at SP2's 1.30.0 pin: GenAI's `>= 1.28.0` is a **floor**, its native links ORT dynamically and negotiates through `OrtApiBase::GetApi` which fails only on an *older* runtime, and §17 adds the guard that stops anyone "fixing" the skew by downgrading ORT. |
| 5 | v1 ships embeddings **and** ORT GenAI chat | This sub-project. |
| 6 | TFMs | SP1's vocabulary, no new spelling. `Qavren.Edge.Chat.Onnx` takes `net10.0` + android/ios/maccatalyst (no windows — §4.1, same reason SP2's ONNX packages take four); `Qavren.Edge.Rag` takes `net10.0` alone; `Qavren.Edge.Chat.Tests` takes all five so the Windows device lane can reference it; `Qavren.Edge.Rag.Tests` takes `net10.0` alone because it is host-only. `SupportedOSPlatformVersion` rises again on SP4's own projects — android `24.0`, ios and maccatalyst **`15.4`** — because GenAI's managed assets are `net9.0-ios15.4` / `net9.0-maccatalyst14.0` and its Android AAR declares `minSdkVersion 24`. That property plays no part in restore, so it is not a TFM change. |
| 9 | Images later | No multimodal. `Images`, `Audios`, `MultiModalProcessor` and `StreamingProcessor` are out (§20); a non-`TextContent` in a message is a named refusal, not a silent drop. |
| 10 | Encryption ships in v1 | The RAG recipe runs over whatever collection it is handed, SQLCipher included. SP2's cipher test already covers the store; SP4 adds no second one. |
| 13 | MS-native hosting | `edge.AddOnnxChat(preset)`, `edge.AddVectorStoreRetriever<TKey,TRecord>(…)` on SP1's `EdgeBuilder`; registration goes through MEAI's own `services.AddChatClient` so the descriptor Agent Framework and Semantic Kernel resolve is the one they expect; `IEdgeStartupTask` ordering; `IEdgeLifecycleObserver`; `IEdgeDiagnosticsContributor`; `EdgeException` subclasses with stable `EdgeErrorCode`s in a new range. |
| 14 | GitHub-hosted CI | Two new host-test steps on the existing three-OS matrix, one new nightly job, and the four existing device lanes pick SP4 up through `Qavren.Edge.DeviceTests`. No new device lane. |

## 3. Goals and non-goals

Goals:

- A consumer adds one package, writes one builder call with a preset, injects
  `IChatClient`, and gets streaming on-device chat with a memory budget that
  refused before it loaded rather than after the OS killed the app.
- Adding `UseRag()` and one retriever registration turns that into a grounded,
  cited, streaming answer over SP2's hybrid search.
- The client is a literal MEAI `IChatClient`: `UseLogging()`,
  `UseOpenTelemetry()`, `AsAIAgent()` and `AsChatCompletionService()` all work
  with no Qavren-authored glue, because `GetService` is implemented to contract.
- Every runtime fact is inspectable: the model's declared shape, the context the
  budget actually allowed and why, tokens/sec, time-to-first-token, peak working
  set, whether the chat template rendered, whether guidance is enforced on this
  build, and which ABI is running.
- Nothing is missed on iOS or Android: no bundled multi-GB model, no download
  without consent, no backed-up weights, no generation that ignores a memory
  warning, no thermal state sampled once and then forgotten for thirteen
  seconds.
- The product still answers when no chat model is on the device. The extractive
  floor is an `IChatClient`, so the same `UseRag()` pipeline produces a cited
  search result with zero weights resident.

Non-goals:

- No tool calling. `ChatOptions.Tools` is a named refusal (§15). A ~1B local
  model is unreliable at raw tool-calling — that is the prior art's central
  finding, and the grammar sampling that made it survivable there is exactly
  what decision 3 says is unavailable here.
- No structured output by default, and never a silent one (§6.6).
- No multimodal, no LoRA adapters, no beam search, no speculative decoding.
- No agent loop. SP4 ships a client that composes with `UseFunctionInvocation`;
  it does not ship an agent.
- No second telemetry system. `UseOpenTelemetry()` already emits the GenAI
  semantic-convention spans; SP4 sets `ProviderName` so `gen_ai.system`
  populates and puts everything else on `AdditionalProperties` under documented
  keys.
- No KV-cache persistence across launches, and no "checkpoint on sleep" (§14.2
  explains why that is already free).

## 4. Architecture

### 4.1 Packages

| Package | TFMs | Depends on | Purpose |
|---|---|---|---|
| `Qavren.Edge.Chat.Onnx` | `net10.0`, `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst` (Linux hosts: `net10.0`, `net10.0-android` only — §4.1) | `Qavren.Edge.Onnx`; `Microsoft.ML.OnnxRuntimeGenAI` 0.15.2; `Microsoft.Extensions.AI` 10.10.0 | L1. The single process-wide `OgaHandle`; a ref-counted chat-model host over `Model` + `Tokenizer`; the `genai_config.json` shape reader; the KV-cache-aware memory budget and context ladder; chat-template and guidance probes; the streaming decode loop with thermal pacing and cooperative termination; history reduction; model presets; provisioning with a consent plan; lifecycle observer; diagnostics contributor. |
| `Qavren.Edge.Rag` | `net10.0` | `Qavren.Edge.Core`; `Microsoft.Extensions.AI` 10.10.0; `Microsoft.Extensions.VectorData.Abstractions` 10.10.0 | L2. `IEdgeRetriever` and the MEVD collection adapter; `RagChatClient : DelegatingChatClient` and `UseRag()`; numbered context assembly under a token budget; `[n]` → `CitationAnnotation` + `TextSpanAnnotatedRegion`; `ExtractiveChatClient`, the no-LLM answer floor. |

Dependency direction is strictly downward and matches SP2's: `Chat.Onnx` →
`Onnx` → `Core`. `Rag` → `Core`, and otherwise only Microsoft abstractions. The
two packages do **not** reference each other: that is what lets `UseRag()` sit
over an Azure OpenAI client, and what lets `AddOnnxChat` be used with no
retrieval at all.

`Qavren.Edge.Rag` is `net10.0` only for exactly SP2's `VectorData` reason: it
touches no platform API, and every platform TFM consumes a `net10.0` library
unchanged. Multi-targeting it would add three build legs for nothing.

`Qavren.Edge.Chat.Onnx` multi-targets for one reason: `IEdgeChatDeviceProfile`
(§6.3) needs `ActivityManager.MemoryInfo.TotalMem` on Android and
`NSProcessInfo.PhysicalMemory` on Apple, and the ABI check needs
`Android.OS.Build.SupportedAbis`. Everything else in the package is portable,
and the `net10.0` leg carries a complete **portable** device profile (§6.3) —
`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`,
`EdgeMemoryBudgetKind.SystemWide`, and `RuntimeInformation.RuntimeIdentifier` —
so the budget is fully defined off-device, which is the leg every hosted CI
runner and every Windows consumer actually executes.

The project file carries **SP2's per-OS `TargetFrameworks` guard, verbatim**:

```xml
<TargetFrameworks>net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst</TargetFrameworks>
<!-- Restore evaluates EVERY entry of TargetFrameworks for every project in the graph, so the
     ios and maccatalyst SDK packs (which do not exist for linux-x64, NETSDK1178) have to be
     dropped on Linux hosts or the android device lane fails before a test can run. -->
<TargetFrameworks Condition="$([MSBuild]::IsOSPlatform('Linux'))">net10.0;net10.0-android</TargetFrameworks>
```

Without it the ubuntu host leg and the Linux-hosted Android device lane both
fail restore before a single test runs — SP2 learned this and wrote the comment;
SP4 copies both the guard and the comment rather than rediscovering it. The test
projects carry the same guard (§16.4).

**No `net10.0-windows10.0.19041.0` leg**, matching SP2's ONNX packages.
GenAI ships no Windows *platform* managed asset at all — a Windows consumer
binds `lib/net8.0` and takes `runtimes/win-x64` or `runtimes/win-arm64` by RID,
exactly as `Qavren.Edge.Sqlite` is already consumed. A fifth TFM would buy a
build leg and nothing else.

The Apple floor moves to **15.4**, above SP2's 15.1, because GenAI's managed
asset group is `net9.0-ios15.4`. A consumer referencing both SP2 and SP4 takes
the maximum, which is what `SupportedOSPlatformVersion` already does. Mac
Catalyst takes 15.4 too, not GenAI's nominal `net9.0-maccatalyst14.0`, because
Catalyst resolves the **iOS** xcframework through the SDK RID graph (§9.4) and
inherits whatever that slice was built at. §19 item 1 is the build that confirms
it.

### 4.2 Repository layout

`chat/` mirrors `embeddings/`, which mirrors `foundation/`:

```
qavren-edge/
  QavrenEdge.slnx                       # SP4 projects added here
  Directory.Packages.props              # one new PackageVersion
  chat/
    README.md
    src/
      Qavren.Edge.Chat.Onnx/                       # net10.0 + android/ios/maccatalyst
        Platforms/Android/ , Platforms/iOS/        # device profile only
        ChatPresets.g.cs                           # generated, committed
      Qavren.Edge.Rag/                             # net10.0 only
    tests/
      Qavren.Edge.Chat.Tests/                      # 5 TFMs (incl. windows), device-hosted
      Qavren.Edge.Rag.Tests/                       # net10.0 only, host-only
      fixtures/make_tiny_chat_model.py             # regeneration script, never a build step
      fixtures/TinyChatModel.g.cs                  # the committed artifact
    tools/model-hashes/fetch_chat_model_hashes.py  # emits ChatPresets.g.cs
    samples/                                       # none; the SP1 sample app gains two pages
    docs/superpowers/{specs,plans}/ , docs/adr/
```

`foundation/samples/Qavren.Edge.Sample` gains two pages rather than SP4 adding a
second sample app (§18). `foundation/tests/Qavren.Edge.DeviceTests` gains one
`ProjectReference`.

### 4.3 The consumer's calls

Chat alone:

```csharp
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddOnnxChat(ChatPresets.Llama32_1BInstructInt4));

var chat = sp.GetRequiredService<IChatClient>();
await foreach (var update in chat.GetStreamingResponseAsync("summarise my week"))
    Console.Write(update.Text);
```

Chat with retrieval, over SP2's store:

```csharp
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddOnnxEmbeddings(o => o.Preset = EmbeddingPresets.MiniLmL6V2Int8)
    .AddVectorStore()
    .AddVectorCollectionMigration<string, Note>(version: 1, "notes")
    .AddOnnxChat(ChatPresets.Llama32_1BInstructInt4,
                 pipeline: chat => chat.UseRag())
    .AddVectorStoreRetriever<string, Note>("notes",
        n => new RagSource(n.Key, n.Body) { Title = n.Title, Uri = n.Url }));
```

```csharp
// Citations arrive ONCE, on the final metadata update, and their spans index the
// accumulated answer - not any single update. So accumulate, then index your own buffer.
var answer = new StringBuilder();
await foreach (var update in chat.GetStreamingResponseAsync("what does the warranty cover?"))
{
    Console.Write(update.Text);
    answer.Append(update.Text);

    foreach (var citation in update.Contents.SelectMany(c => c.Annotations ?? [])
                                            .OfType<CitationAnnotation>())
    foreach (var region in (citation.AnnotatedRegions ?? []).OfType<TextSpanAnnotatedRegion>())
        Console.WriteLine($"\n[{citation.Title}] {citation.Url} → " +
            answer.ToString(region.StartIndex, region.EndIndex - region.StartIndex));
}
```

Non-streaming is simpler and needs no accumulation, because `ChatResponse.Text`
*is* the string the spans index:

```csharp
var response = await chat.GetResponseAsync("what does the warranty cover?");
foreach (var citation in response.Messages[^1].Contents
                                 .SelectMany(c => c.Annotations ?? [])
                                 .OfType<CitationAnnotation>())
    Console.WriteLine($"[{citation.Title}] {citation.Url}");
```

`AddOnnxChat` calls `AddOnnx()` for you; it is idempotent, so an app that also
called `AddOnnxEmbeddings` gets one `IEdgeModelPaths`, one
`IEdgeResourceMonitor`, one model store and one order-200 `OrtEnv` task.

The order of `AddOnnxChat` and `AddVectorStoreRetriever` is irrelevant:
`UseRag()` resolves `IEdgeRetriever` at resolve time, and MEAI's `AddChatClient`
registers a descriptor whose factory is `builder.Build`, so the whole pipeline is
constructed lazily from the app's provider. §16.2 asserts this exact snippet from
a real container in both orders.

First run, with the consent sheet decision 5 requires:

```csharp
var provisioner = sp.GetRequiredService<IChatModelProvisioner>();
var plan = provisioner.Plan();
if (!plan.Provisioned && await ConfirmAsync(plan.BytesToTransfer, plan.SpdxLicense, plan.LicenseUri))
    await provisioner.ProvisionAsync(progress);
```

And the floor, for an app that ships no weights at all:

```csharp
edge.AddExtractiveChat(pipeline: chat => chat.UseRag())
    .AddVectorStoreRetriever<string, Note>("notes", n => new RagSource(n.Key, n.Body));
```

That is the same `IChatClient`, the same `UseRag()`, the same
`CitationAnnotation`s, and no model on the device.

## 5. Amendments to prior sub-projects

**Two edits to prior sub-projects' public API surface and documentation, both
additive, both in the same PR as the first SP4 commit. No breaking changes to
SP1's merged surface or to SP2's branch surface, and no third API amendment —
which is a constraint §8.1 and §14.3 are held to, not a slogan.**

Seven files outside `chat/` are nevertheless *touched*, none of them an API
change, and they are enumerated here so the claim above is exact rather than
convenient:

| File | Change | Where it is specified |
|---|---|---|
| `QavrenEdge.slnx` | four project entries under `/chat/src/` and `/chat/tests/` | §17 |
| `Directory.Packages.props` | one `PackageVersion` | §17 |
| `.github/workflows/ci.yml` | two `test` steps, one asset assertion, one nightly job | §17 |
| `foundation/tools/ci-checks/assert-workflows.py` | one rule extension | §17 |
| `foundation/tests/Qavren.Edge.DeviceTests` | one `ProjectReference`, possibly one `SupportedOSPlatformVersion` line | §5 (below), §16.4 |
| `foundation/samples/Qavren.Edge.Sample` | two pages, two iOS entitlement keys | §18 |
| `embeddings/docs/…/2026-09-11-sp2-embeddings-vectorstore-design.md` | one sentence corrected | §8.2 |

Build plumbing, a test host, a sample app and a prose correction. Not one of
them alters a type, a member, a signature or a default in `Qavren.Edge.Core`,
`Qavren.Edge.Sqlite`, `Qavren.Edge.Onnx`, `Qavren.Edge.Embeddings.Onnx` or
`Qavren.Edge.VectorData`, which is the surface the two-edit rule is about.

Four near-misses are worth naming, because each is a change SP4 could plausibly
have demanded and deliberately does not.

- **`OnnxModelManifest` is not touched, and `OnnxModelFileRole` gains nothing.**
  A GenAI bundle provisions today, unchanged. `Files` is already
  `IReadOnlyList<OnnxModelFile>` and its own XML doc anticipates external data;
  all three model sources already call
  `Directory.CreateDirectory(Path.GetDirectoryName(destination)!)`; and
  `OnnxModelLayout.FilePath` already translates forward slashes. The one thing
  that looked like it needed an edit — the content-addressed directory
  `<Models>/<modelId>/<sha16>`, whose `sha16` is `Graph(manifest).Sha256[..16]`
  — is solved by **nominating the decoder `.onnx` as `GraphFile`**, so the
  directory is keyed on the weights, which is the correct identity. Nominating
  `genai_config.json` instead would have keyed a 1.5 KB file whose digest does
  not move when `model.onnx.data` does, and would then have needed a new
  `DirectoryIdentity` property and a change to `OnnxModelLayout.Sha16` to repair
  a problem SP4 simply does not create. A `GenAiConfig` role was considered and
  cut: ORT GenAI requires the literal filename `genai_config.json`, so the role
  would carry no information the `RelativePath` does not, and `Auxiliary`'s own
  doc already reads "anything else the consumer wants provisioned alongside".
- **`EdgeResourceSnapshot` gains nothing.** SP4 needs total RAM and needs to
  know whether `AvailableMemoryBytes` is a per-process allowance (Apple) or a
  system-wide figure (Android). Both would be natural as two more members — and
  it is a `sealed record` with six positional parameters, so appending even
  defaulted ones changes its generated `Deconstruct` arity and its binary shape,
  which §5's additive-only rule forbids. SP2's own `IEdgeResourceMonitor` doc
  refuses a *smaller* change to `EdgeMemoryPressure` on exactly this ground. SP4
  publishes `IEdgeChatDeviceProfileProvider` in its own package instead (§6.3),
  read **once at startup** because every fact on it is static for the process —
  which is also what stops the two readers drifting: per-turn readings still come
  from `IEdgeResourceMonitor` and nowhere else.
- **SP2's `internal`s are not widened, and SP4 never reads them.**
  `OnnxEnvironmentState` — the object that records whether SP2's order-200 task
  created `OrtEnv` or found it pre-existing — is `internal sealed` in
  `Qavren.Edge.Onnx`, and that assembly's `InternalsVisibleTo` names exactly two
  assemblies, `Qavren.Edge.Onnx.Tests` and `Qavren.Edge.Embeddings.Tests`.
  `Qavren.Edge.Chat.Onnx` is not one of them and does not ask to be. SP4's
  ordering fact is therefore derived from a **public** signal instead —
  `OrtEnv.IsCreated`, sampled before SP4 loads its first GenAI type (§8.1) — and
  the reader who wants SP2's side of the story reads SP2's own **public**
  diagnostics key `ortEnvironmentPreexisting`, which `IEdgeDiagnostics.Report()`
  already publishes in the same report under the `Qavren.Edge.Onnx` component.
  Two keys, one report, no third amendment.
- **`IOnnxSessionHost` gains nothing.** It is `InferenceSession`-typed end to
  end and cannot host an `OgaModel`. SP4 owns a parallel `IChatModelHost` with
  the identical lease discipline rather than widening SP2's. SP2 documents that
  no L1 package can drive a session load from a startup task, because
  `AcquireAsync` awaits `EnsureStartedAsync` and `AcquireCoreAsync` is
  `internal`. **SP4 does not inherit that hole** — its host lives in the same
  assembly as its warm-up task — and it does not fix SP2's either. Adding
  `IOnnxSessionHost.PreloadAsync` is recommended and out of scope.

**5.1 `Qavren.Edge.Core` — `EdgeErrorCode` gains the 7000–7299 range.** Values
1001–4001 (SP1) and 5001–5213 (SP2) are untouched; the full list is in §15.
`EdgeException`'s `HelpLink` convention applies to the new codes for free.

**5.2 `foundation/docs/errors.md` — a new sub-project 4 section.** One
`## <code>` heading per value with **Name**, Meaning and Remediation, in the
shape 1001–5213 already use. The range list at the top of the file gains two
lines:

```
- **6000–6299** — reserved for sub-project 3 (ingestion). Unallocated.
- **7000–7299** — sub-project 4 (chat + RAG): `Qavren.Edge.Chat.Onnx`,
  `Qavren.Edge.Rag`.
```

The 6000–6299 reservation is recorded here because nothing in the repo says so
today and SP4 is the first sub-project in a position to notice.

`EdgeStartupOrder` is **not** changed: SP4's orders (400, 410, 420) fit between
SP2's `VectorSchema = 300` and SP1's `ConsumerDefault = 1000`, and they are
published from SP4's own `EdgeChatStartupOrder`. `EdgeEventIds` and
`EdgeAiEventIds` are likewise not changed; SP4 publishes `EdgeChatEventIds`
(900–959) and, from the RAG package, `EdgeRagEventIds` (960–999). Publishing the
RAG ids from the package that uses them matters: `Qavren.Edge.Rag` does not
reference `Qavren.Edge.Chat.Onnx`, so a constant declared in the chat package
would be unreachable from the code that logs it.

The one contingency, exactly as SP2 §16.4 already names it for itself:
`foundation/tests/Qavren.Edge.DeviceTests` gains one `ProjectReference` and, if
the merged Android manifest or the Apple deployment target demands it, a
`SupportedOSPlatformVersion` bump to android 24.0 / Apple 15.4. That is a **test
host**, not a shipped package, and §19 item 1 is the build that decides.

## 6. `Qavren.Edge.Chat.Onnx`

### 6.1 Ordering, ids and property keys

```csharp
namespace Qavren.Edge.Chat;

/// <summary>
/// 400-999 is free between sub-project 2's <c>VectorSchema</c> (300) and sub-project 1's
/// <c>ConsumerDefault</c> (1000). Sub-project 4 does not squat in sub-project 2's band.
/// </summary>
public static class EdgeChatStartupOrder
{
    /// <summary>
    /// Creates the one process-wide <c>OgaHandle</c>, disables telemetry, and checks the RID
    /// and ABI. <b>Must run after <c>EdgeAiStartupOrder.OnnxEnvironment</c> (200)</b>: ORT GenAI
    /// creates ORT's process-wide <c>OrtEnv</c> from NATIVE code on its first call
    /// (<c>OrtGlobals::OrtGlobals() : env_{OrtEnv::Create(...)}</c>), and sub-project 2's managed
    /// <c>OrtEnv.IsCreated</c> guard cannot see that - so touching any GenAI type before order 200
    /// silently costs sub-project 2 its log id, its severity and its <c>DOrtLoggingFunction</c>
    /// bridge, with no error anywhere. See section 9.1.
    /// </summary>
    public const int ChatEnvironment = 400;

    /// <summary>Opt-in. Verifies presence. Never downloads.</summary>
    public const int ChatModelProvisioning = 410;

    /// <summary>Opt-in. Loads the model, probes the chat template, optionally probes guidance.</summary>
    public const int ChatWarmUp = 420;
}

/// <summary>
/// 900-959 is sub-project 4's chat range. Sub-project 1's <c>EdgeEventIds</c> is a non-partial
/// static class and sub-project 2's <c>EdgeAiEventIds</c> owns 600-899, so sub-project 4
/// publishes its own and continues the numbering.
/// </summary>
public static class EdgeChatEventIds
{
    public const int GenAiRuntimeInitialized    = 900;
    public const int GenAiRuntimeShutdown       = 901;
    public const int GenAiTelemetryDisabled     = 902;
    public const int GenAiEnvironmentOutOfOrder = 903;   // OrtEnv.IsCreated was false when SP4's order-400 task ran

    public const int ChatModelLoaded            = 910;
    public const int ChatModelLoadFailed        = 911;
    public const int ChatModelDropped           = 912;
    public const int ChatModelProvisioned       = 913;

    public const int BudgetResolved             = 920;
    public const int BudgetReduced              = 921;
    public const int BudgetRefused              = 922;
    public const int BudgetUnknown              = 923;

    public const int TurnStarted                = 930;
    public const int TurnCompleted              = 931;
    public const int TurnQueued                 = 932;
    public const int TurnRejected               = 933;
    public const int TurnTerminated             = 934;
    public const int HistoryReduced             = 935;
    public const int ThermalThrottled           = 936;
    public const int ThermalAborted             = 937;

    public const int ChatTemplateProbed         = 950;
    public const int ChatTemplateUnsupported    = 951;
    public const int PromptFormatterOverridden  = 952;
    public const int GuidanceProbed             = 953;
}

/// <summary>
/// The <c>AdditionalProperties</c> keys sub-project 4 writes. Public so a consumer never types a
/// string literal and an OpenTelemetry exporter can allow-list them.
/// </summary>
public static class EdgeChatProperties
{
    /// <summary>A <see cref="ChatTurnStatus"/>. One key, one strongly-typed record.</summary>
    public const string TurnStatus = "qavren.edge.chat.turn";

    /// <summary>
    /// A <c>IReadOnlyList&lt;string&gt;</c> of <c>ChatOptions</c> members this client did not
    /// honour on the request. Present only when non-empty.
    /// </summary>
    public const string UnhonouredOptions = "qavren.edge.chat.unhonouredOptions";
}
```

### 6.2 Model shape, read from `genai_config.json`

```csharp
namespace Qavren.Edge.Chat;

/// <summary>
/// The decoder geometry the memory budget is computed from. A preset declares it - the budget has
/// to refuse <i>before</i> 1.24 GB is mapped, and reading the shape at load time is too late -
/// and the loader cross-checks the declaration against the provisioned <c>genai_config.json</c>
/// field by field, so a republished model folder is
/// <see cref="EdgeErrorCode.ChatModelShapeMismatch"/> rather than a wrong budget.
/// </summary>
public sealed record ChatModelShape
{
    /// <summary><c>model.type</c>: "llama", "qwen3", "phi3". Diagnostics only.</summary>
    public required string ModelType { get; init; }

    /// <summary><c>model.context_length</c>. The hard ceiling on <c>search.max_length</c>.</summary>
    public required int ContextLength { get; init; }

    public required int VocabSize { get; init; }
    public required int NumHiddenLayers { get; init; }
    public required int NumKeyValueHeads { get; init; }
    public required int HeadSize { get; init; }

    /// <summary><c>model.decoder.sliding_window</c> when the model declares one; null otherwise.</summary>
    public int? SlidingWindow { get; init; }

    /// <summary><c>model.decoder.filename</c>.</summary>
    public required string DecoderFileName { get; init; }

    /// <summary>
    /// Bytes per KV cache element. Default 2 (fp16).
    /// <para>
    /// <b>This is the one field the cross-check cannot validate.</b> <c>genai_config.json</c>
    /// states layers, KV heads and head size but says nothing about the KV dtype, so a
    /// disagreement here is undetectable at load and shows up only as a budget that is wrong by a
    /// factor of two. 2 is the calibrated default, not a guess: for
    /// <c>Llama32_1BInstructInt4</c> it puts the arithmetic within 1.2% of a measured 1280 MiB
    /// peak, where 4 would put it 11% over. Section 19 item 4 is the nightly measurement that answers it
    /// per preset, and <see cref="MeasuredPeakBytes"/> is what absorbs the error meanwhile.
    /// </para>
    /// </summary>
    public int KvCacheBytesPerElement { get; init; } = 2;

    /// <summary>
    /// Resident weight estimate: the sum of the manifest's <c>Graph</c> and
    /// <c>GraphExternalData</c> file sizes, and <b>not</b> <c>TotalSizeBytes</c> - a 17 MB
    /// <c>tokenizer.json</c> is not resident weights, and counting it would over-report by tens of
    /// megabytes inside a user-visible refusal message.
    /// </summary>
    public required long WeightsBytes { get; init; }

    /// <summary>
    /// A peak RSS measured on a real device at <see cref="ContextLength"/>, or null. When present
    /// and <see cref="ChatMemoryBudgetOptions.PreferMeasuredPeak"/> is set, the budget takes
    /// <c>max(arithmetic, measured + ReserveBytes)</c> - section 9.3 is the normative formula -
    /// and records which term bound in <see cref="ChatMemoryDecision.UsedMeasuredPeak"/>. The
    /// reserve is added to the measured term because a measured peak is the model's own resident
    /// footprint and carries no headroom for the rest of the app. Pure arithmetic
    /// underestimates large-vocabulary models badly - Gemma-3-1b computes ~929 MiB and measures
    /// 1502 MiB - so a preset without a measurement is running on arithmetic alone and the
    /// diagnostics block says so.
    /// <para>
    /// <b>For both shipped presets this term is inert today, and that is stated rather than
    /// implied.</b> <c>Llama32_1BInstructInt4</c>'s measured 1280 MiB plus a 192 MiB reserve is
    /// 1472 MiB, below its own arithmetic of 1679 MiB at 4096 tokens, so the arithmetic binds;
    /// <c>Qwen3_600MInt4</c> has no device measurement at all (section 6.5). The machinery exists
    /// for the case it was built for - a large-vocabulary preset whose arithmetic under-reports,
    /// which is exactly Gemma-3-1b - and section 19 item 3 is what would make it bind here.
    /// </para>
    /// </summary>
    public long? MeasuredPeakBytes { get; init; }

    /// <summary>
    /// What device and runtime <see cref="MeasuredPeakBytes"/> came from. Never a guess.
    /// <para>
    /// It is a diagnostics string and <b>nothing else</b>: section 9.3's formula reads
    /// <see cref="MeasuredPeakBytes"/> alone and cannot discount a measurement by provenance. So
    /// the rule is on the way in, not on the way out - <see cref="MeasuredPeakBytes"/> is set only
    /// from a measurement taken on a device of the class the preset is meant to run on. A server
    /// or laptop peak is recorded here with <see cref="MeasuredPeakBytes"/> left null, which is
    /// what <c>Qwen3_600MInt4</c> does with its published Graviton figure.
    /// </para>
    /// </summary>
    public string? MeasuredOn { get; init; }

    /// <summary>layers x 2 (K and V) x kvHeads x headSize x bytesPerElement.</summary>
    public long KvCacheBytesPerToken =>
        (long)NumHiddenLayers * 2 * NumKeyValueHeads * HeadSize * KvCacheBytesPerElement;

    /// <summary>KV bytes for a context, clamped by <see cref="SlidingWindow"/> when the model declares one.</summary>
    public long KvCacheBytes(int contextTokens) =>
        KvCacheBytesPerToken * Math.Min(contextTokens, SlidingWindow ?? int.MaxValue);

    public static ChatModelShape FromGenAiConfig(string json, long weightsBytes);
    public static bool TryFromGenAiConfig(string json, long weightsBytes,
        out ChatModelShape? shape, out string? error);
}
```

Parsing is source-generated (`JsonSerializerContext`), never reflection-based, so
the load path stays trim-safe. `model.eos_token_id` is normalised on the way in:
the field is an `int` in some exports and an array in others.

### 6.3 Device profile

```csharp
namespace Qavren.Edge.Chat;

/// <summary>
/// Where <see cref="EdgeChatDeviceProfile.TotalMemoryBytes"/> came from, because the three sources
/// are not the same measurement and the floor in <see cref="ChatMemoryBudgetOptions.MinTotalMemoryBytes"/>
/// is a constant compared against all of them.
/// </summary>
public enum EdgeTotalMemorySource
{
    /// <summary>No figure.</summary>
    Unknown = 0,

    /// <summary>
    /// <c>ActivityManager.MemoryInfo.TotalMem</c>. Physical RAM <b>minus</b> what the kernel
    /// reserved before userspace saw it - about 5.4-5.7 GiB on a device marketed as 6 GB.
    /// </summary>
    AndroidActivityManager,

    /// <summary>
    /// <c>NSProcessInfo.ProcessInfo.PhysicalMemory</c>. The full nominal figure, so a nominal
    /// 6 GB device reports 6,442,450,944 exactly.
    /// </summary>
    ApplePhysicalMemory,

    /// <summary>
    /// <c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c>. <b>Not physical RAM</b>: it is the
    /// GC's available-memory figure, which on a containerised host is the container's limit. It is
    /// reported for diagnostics and the device floor is <b>skipped</b> for it (section 9.3),
    /// because a 2 GiB container limit on a 64 GiB build agent is a false refusal and a 64 GiB
    /// figure on a machine with no limit tells the budget nothing it does not already get from
    /// <c>AvailableMemoryBytes</c>.
    /// </summary>
    GcMemoryInfo,
}

/// <summary>What <see cref="EdgeResourceSnapshot.AvailableMemoryBytes"/> actually measures.</summary>
public enum EdgeMemoryBudgetKind
{
    /// <summary>The platform reports nothing usable.</summary>
    Unknown = 0,

    /// <summary>
    /// A per-process budget the caller may spend. Apple's <c>os_proc_available_memory()</c>:
    /// "the current memory limit minus the memory footprint of your app".
    /// </summary>
    PerProcess,

    /// <summary>
    /// A system-wide free-memory figure that is NOT a per-app allowance. Android's
    /// <c>ActivityManager.MemoryInfo.AvailMem</c>, whose own documentation says it "should not be
    /// considered absolute". Spending it in full is how a 1.24 GB model gets the app killed by
    /// lmkd, so the budget multiplies it by
    /// <see cref="ChatMemoryBudgetOptions.SystemWideMemoryFraction"/> before believing it.
    /// </summary>
    SystemWide,
}

/// <summary>
/// Static facts about the device, read <b>once</b> at startup and cached for the life of the
/// process. Everything here is constant, which is what stops this and <c>IEdgeResourceMonitor</c>
/// drifting apart: per-turn readings come from the monitor and nowhere else.
/// </summary>
/// <param name="TotalMemoryBytes">
/// The platform's best total-memory figure, or null when it will not say. <b>Not comparable
/// across platforms without <paramref name="TotalMemorySource"/></b> - see that parameter.
/// </param>
/// <param name="TotalMemorySource">
/// Which of three different measurements <paramref name="TotalMemoryBytes"/> actually is. The
/// device floor consults it: it applies to a real physical reading and is skipped for
/// <see cref="EdgeTotalMemorySource.GcMemoryInfo"/>.
/// </param>
/// <param name="AvailableMemoryKind">How to read the monitor's available-memory figure.</param>
/// <param name="IsLowRamDevice">
/// Android's <c>ActivityManager.IsLowRamDevice</c>, documented as "1GB or less of RAM". Reported,
/// and used only as a hard refusal - it is false on every phone that is still far too small for a
/// 1.24 GB model, so it is useless as the gate.
/// </param>
/// <param name="RuntimeIdentifier">The running RID.</param>
/// <param name="Abi">The running Android ABI, or null off Android.</param>
public readonly record struct EdgeChatDeviceProfile(
    long? TotalMemoryBytes,
    EdgeTotalMemorySource TotalMemorySource,
    EdgeMemoryBudgetKind AvailableMemoryKind,
    bool? IsLowRamDevice,
    string RuntimeIdentifier,
    string? Abi);

public interface IEdgeChatDeviceProfileProvider
{
    EdgeChatDeviceProfile Read();
}
```

Three implementations, one per compilation, registered by `AddGenAiRuntime()`
behind `#if ANDROID` / `#if IOS || MACCATALYST` / `#else`, exactly as sub-project
2 splits `IEdgeResourceMonitor`. **The `#else` leg is not a stub**: it is the leg
every hosted CI runner executes in tier 1 and tier 2, and the leg every Windows,
macOS-desktop and Linux consumer binds, so every field on it is defined here
rather than left to the implementation.

| Field | `net10.0-android` | `net10.0-ios` / `-maccatalyst` | `net10.0` (Windows, macOS desktop, Linux) |
|---|---|---|---|
| `TotalMemoryBytes` | `ActivityManager.MemoryInfo.TotalMem` | `(long)NSProcessInfo.ProcessInfo.PhysicalMemory` | `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`, or null when that is `0` |
| `TotalMemorySource` | `AndroidActivityManager` | `ApplePhysicalMemory` | `GcMemoryInfo`, or `Unknown` when the figure is null |
| `AvailableMemoryKind` | `SystemWide` | `PerProcess` | `SystemWide` |
| `IsLowRamDevice` | `ActivityManager.IsLowRamDevice` | `null` | `null` |
| `RuntimeIdentifier` | `RuntimeInformation.RuntimeIdentifier` | `RuntimeInformation.RuntimeIdentifier` | `RuntimeInformation.RuntimeIdentifier` |
| `Abi` | `Android.OS.Build.SupportedAbis?[0]` | `null` | `null` |

`SystemWide` on desktop is the honest classification, not a fallback: sub-project
2's desktop monitor fills `AvailableMemoryBytes` from
`GC.GetGCMemoryInfo()` — its own XML doc says "Desktop: `GC.GetGCMemoryInfo()`,
advisory only" — and that figure describes the machine, not a per-process
allowance the OS will enforce. Classifying it `SystemWide` means the budget
applies `SystemWideMemoryFraction` (0.60) to it and consults
`MinTotalMemoryBytes`, which is the conservative branch, and it means the
budget's three-way branch in §9.3 has no undefined arm on any TFM SP4 ships.
`IsLowRamDevice` is `null` rather than `false` off Android because "not a
low-RAM device" and "nobody asked the question" are different claims, and §9.3's
hard refusal fires only on an explicit `true`.

**The three totals are three different measurements, and `TotalMemorySource` is
what stops the budget pretending otherwise.** Android's `TotalMem` excludes
kernel-reserved memory; Apple's `PhysicalMemory` does not; and
`GCMemoryInfo.TotalAvailableMemoryBytes` is not physical memory at all — it is the
GC's available-memory figure, which under a container is the container's limit.
§9.3 therefore applies the device floor only to the first two and skips it for the
third, and §6.4's floor constants are set at about 0.85× nominal so that one
constant means one device class across the two platforms that report physical
memory. Calling this row "Physical RAM" would have been wrong for one of its three
cells, which is the kind of wrong that only shows up as a refusal on somebody
else's phone.

Desktop `TotalMemoryBytes` may legitimately be null — `GCMemoryInfo` returns `0`
for `TotalAvailableMemoryBytes` in some container configurations — and that is
the `SkippedUnknown`/no-floor path in §9.3, not an error. The device profile is
read **once** and cached, so a null here is a null for the process, which is
correct: neither physical RAM nor a container limit changes under a running
process.

### 6.4 The memory budget

```csharp
namespace Qavren.Edge.Chat;

public sealed class ChatMemoryBudgetOptions
{
    /// <summary>
    /// Contexts tried in order until one fits. The first entry at or below the requested length is
    /// the starting rung. An explicit ladder rather than repeated halving, so the diagnostics block
    /// reports a value a reader recognises.
    /// </summary>
    public IReadOnlyList<int> ContextLadder { get; set; } = [4096, 3072, 2048, 1536, 1024];

    /// <summary>Below this, refuse rather than degrade further.</summary>
    public int MinContextTokens { get; set; } = 1024;

    /// <summary>
    /// Transient allocation beyond weights and KV: the logits buffer, the sampler copy, ORT arenas,
    /// the tokenizer. Default 192 MiB. An engineering estimate, and the refusal message says so in
    /// the same words sub-project 2's pre-flight already uses.
    /// </summary>
    public long WorkspaceBytes { get; set; } = 192L * 1024 * 1024;

    /// <summary>Headroom left to the rest of the app: UI, images, the SQLite page cache. Default 192 MiB.</summary>
    public long ReserveBytes { get; set; } = 192L * 1024 * 1024;

    /// <summary>
    /// What fraction of a <see cref="EdgeMemoryBudgetKind.SystemWide"/> reading to believe.
    /// Default 0.60. Ignored for <see cref="EdgeMemoryBudgetKind.PerProcess"/>.
    /// </summary>
    public double SystemWideMemoryFraction { get; set; } = 0.60;

    /// <summary>
    /// Refuse outright on a device whose total memory is below this, whatever it claims is free.
    /// Return null to disable the gate for a preset. Applied only when
    /// <see cref="EdgeChatDeviceProfile.TotalMemoryBytes"/> is known <b>and</b>
    /// <see cref="EdgeChatDeviceProfile.TotalMemorySource"/> is a real physical-memory reading -
    /// see section 9.3, which skips the floor entirely for
    /// <see cref="EdgeTotalMemorySource.GcMemoryInfo"/>.
    /// <para>
    /// <b>The floor is compared against a number the platforms do not agree on, so it is set
    /// below the nominal figure on purpose.</b> Android's <c>ActivityManager.MemoryInfo.TotalMem</c>
    /// excludes memory the kernel reserved before userspace saw it and reports roughly 5.4-5.7 GiB
    /// on a device marketed as 6 GB; Apple's <c>NSProcessInfo.PhysicalMemory</c> reports the full
    /// nominal 6,442,450,944. A floor of exactly 6 GiB would therefore refuse the default preset on
    /// essentially every nominal-6 GB Android device while admitting every nominal-6 GB iPhone -
    /// the precise opposite of the intended calibration. The defaults are set at about 0.85x
    /// nominal so the same constant means the same device class on both platforms:
    /// </para>
    /// <list type="bullet">
    /// <item>weights over 1 GiB  -&gt; <b>5.0 GiB</b> (a nominal-6 GB device, by either reading)</item>
    /// <item>weights over 200 MiB -&gt; <b>3.4 GiB</b> (a nominal-4 GB device)</item>
    /// <item>weights at or below 200 MiB -&gt; <b>null</b>, no floor at all</item>
    /// </list>
    /// <para>
    /// The third band is not a courtesy. It is what keeps the gate from refusing the tier-2 test
    /// fixture: a ~500 KB random-weight model has no device floor worth enforcing, and a default
    /// Android emulator on a hosted runner reports about 2 GiB of <c>TotalMem</c>, so a blanket
    /// floor would specify the sub-project's highest-value device assertion into a guaranteed 7006
    /// (section 16.2). A floor is a property of the preset's weight class, and below 200 MiB there
    /// is no class to gate.
    /// </para>
    /// <para>
    /// Section 19 item 10 is the measurement that calibrates all three numbers against real
    /// <c>TotalMem</c> and <c>PhysicalMemory</c> readings from the device lanes, because until then
    /// the 0.85x is an engineering estimate like every other constant here.
    /// </para>
    /// </summary>
    public Func<ChatModelShape, long?> MinTotalMemoryBytes { get; set; } =
        static shape => shape.WeightsBytes > 1024L * 1024 * 1024 ? 5_368_709_120L   // 5.0 GiB
                      : shape.WeightsBytes >  200L * 1024 * 1024 ? 3_650_722_201L   // 3.4 GiB
                      : null;

    /// <summary>
    /// Take <c>max(arithmetic, ChatModelShape.MeasuredPeakBytes + ReserveBytes)</c> when a
    /// measurement exists. Section 9.3 is the normative formula; the reserve is added to the
    /// measured term because a measured peak is the model's resident footprint and owes the rest
    /// of the app no headroom.
    /// </summary>
    public bool PreferMeasuredPeak { get; set; } = true;

    /// <summary>
    /// Default false, and that is sub-project 2's rule restated: a reading nobody can make is never
    /// a refusal. Apple documents <c>os_proc_available_memory()</c> returning 0 for "unknown or
    /// already over", and inverting this would make the feature unusable on desktop. True makes an
    /// unknown reading fatal, for a kiosk build that would rather not boot.
    /// </summary>
    public bool RefuseWhenUnknown { get; set; }

    /// <summary>Replaces the whole computation. The last word belongs to whoever measured the device.</summary>
    public Func<ChatBudgetRequest, ChatMemoryDecision>? Override { get; set; }
}

public enum ChatMemoryVerdict
{
    /// <summary>The requested context fits.</summary>
    Allowed,

    /// <summary>A shorter context fits; <see cref="ChatMemoryDecision.ContextTokens"/> is the rung that did.</summary>
    AllowedReduced,

    /// <summary>Not even <see cref="ChatMemoryBudgetOptions.MinContextTokens"/> fits.</summary>
    RefusedInsufficientMemory,

    /// <summary>Total RAM is below the floor for this preset.</summary>
    RefusedDeviceTooSmall,

    /// <summary>The platform reported nothing usable and the gate was skipped.</summary>
    SkippedUnknown,
}

public readonly record struct ChatBudgetRequest(
    string PresetId, ChatModelShape Shape, int RequestedContextTokens,
    EdgeResourceSnapshot Resources, EdgeChatDeviceProfile Device);

/// <param name="Explanation">
/// One sentence naming every term and which one bound. It is what the refusal message says and
/// what the diagnostics block publishes, so a consumer can see why they got 2048 tokens on one
/// phone and 4096 on another without attaching a debugger.
/// </param>
public sealed record ChatMemoryDecision(
    ChatMemoryVerdict Verdict,
    int ContextTokens,
    long RequiredBytes,
    long WeightsBytes,
    long KvCacheBytes,
    long WorkspaceBytes,
    long ReserveBytes,
    long? AvailableBytes,
    long? UsableBytes,
    long? TotalMemoryBytes,
    EdgeMemoryBudgetKind BudgetKind,
    bool UsedMeasuredPeak,
    string Explanation);

/// <summary>
/// Pure, static, no ORT, no GenAI, no I/O - so the entire gate is a tier-1 unit test over a
/// hand-written <see cref="EdgeResourceSnapshot"/> with no natives at all.
/// </summary>
public static class ChatMemoryBudget
{
    public static ChatMemoryDecision Resolve(ChatBudgetRequest request, ChatMemoryBudgetOptions options);

    /// <summary>weights + KV(context) + workspace + reserve, before any measured-peak substitution.</summary>
    public static long RequiredBytes(ChatModelShape shape, int contextTokens, ChatMemoryBudgetOptions options);
}
```

### 6.5 Presets

```csharp
namespace Qavren.Edge.Chat;

/// <summary>One ready-made ORT GenAI model folder: its manifest, its geometry, its defaults.</summary>
public sealed record ChatPreset
{
    public required string Id { get; init; }

    /// <summary>
    /// Sub-project 2's manifest, unchanged. <c>GraphFile</c> names the decoder <c>.onnx</c>, so
    /// the content-addressed directory is keyed on the weights (section 5).
    /// <c>genai_config.json</c>, <c>tokenizer.json</c>, <c>tokenizer_config.json</c> and
    /// <c>chat_template.jinja</c> ride as <c>Auxiliary</c>; <c>model.onnx.data</c> rides as
    /// <c>GraphExternalData</c>.
    /// </summary>
    public required OnnxModelManifest Manifest { get; init; }

    public required ChatModelShape Shape { get; init; }
    public string GenAiConfigFile { get; init; } = "genai_config.json";
    public required string DisplayName { get; init; }

    /// <summary>
    /// The licence URL an about screen needs; <c>Manifest.SpdxLicense</c> carries the identifier.
    /// <para>
    /// <b>Not every chat model's licence has an SPDX identifier, and the field must not pretend
    /// otherwise.</b> Sub-project 2 documents <c>SpdxLicense</c> as "the SPDX identifier of the
    /// model's licence, for an about screen", and section 12.2's consent sheet and the repo's
    /// <c>THIRD-PARTY-NOTICES.md</c> both surface that string verbatim. The Llama 3.2 Community
    /// Licence is not on the SPDX list, so it takes SPDX's own convention for a licence that is
    /// not: <c>LicenseRef-LLAMA-3.2-Community</c>, never a bare <c>LLAMA-3.2-Community</c>, which
    /// is not a valid SPDX expression at all. <c>Qwen3_600MInt4</c> is <c>Apache-2.0</c>, a real
    /// identifier. The field therefore holds an SPDX <i>expression</i>, of which a
    /// <c>LicenseRef-</c> value is a legal one, and the generator script emits that spelling.
    /// </para>
    /// </summary>
    public Uri? LicenseUri { get; init; }

    /// <summary>Matched against the accumulated decoded text, never against one decoded token.</summary>
    public IReadOnlyList<string> StopSequences { get; init; } = [];

    /// <summary>Never above <see cref="ChatModelShape.ContextLength"/>.</summary>
    public int DefaultMaxContextTokens { get; init; } = 4096;

    public int DefaultMaxOutputTokens { get; init; } = 512;
    public float DefaultTemperature { get; init; } = 0.7f;
    public float DefaultTopP { get; init; } = 0.9f;
    public int DefaultTopK { get; init; } = 50;
}

/// <summary>
/// The shipped catalogue, generated by <c>chat/tools/model-hashes/fetch_chat_model_hashes.py</c>
/// into <c>ChatPresets.g.cs</c> with literal SHA-256s and a pinned commit revision, exactly as
/// sub-project 2's <c>EmbeddingPresets.g.cs</c> is. The script reads each repo's own
/// <c>genai_config.json</c> and emits the <see cref="ChatModelShape"/> from it, so no geometry
/// value in this catalogue is hand-typed.
/// <para>
/// <b>There is deliberately no default preset.</b> The two entries carry different licences, and a
/// silently-chosen 1.24 GB download under the Llama 3.2 Community Licence is not a default anyone
/// should inherit. <c>AddOnnxChat</c> takes a preset as a required argument.
/// </para>
/// </summary>
public static class ChatPresets
{
    /// <summary>
    /// <c>Arm/llama-3-2-1b-instruct-onnx-genai-int4-kquantlast-emb-int8-vivo-x300</c>.
    /// ~1.24 GB on disk (the publisher's decimal figure; 1167 MiB of weights); context 4096 as
    /// shipped (the publisher patched it down from Llama's stock 131072 so the KV cache fits a
    /// phone); SPDX <c>LicenseRef-LLAMA-3.2-Community</c>.
    /// Geometry: 16 layers, 8 KV heads, head size 64, vocab 128256 - <b>32 KiB of KV per
    /// token</b>. Measured on a vivo X300 (Android 16, ORT 1.27.0 CPU EP, 4 threads):
    /// 30.564 tok/s decode, TTFT 950 ms, peak RSS 1280 MiB, model load 1.61 s, MMLU 5-shot 44.2%.
    /// <b>No iOS figure exists for this or any other ORT GenAI model</b>; section 19 item 3
    /// measures one.
    /// </summary>
    public static ChatPreset Llama32_1BInstructInt4 { get; }

    /// <summary>
    /// <c>Arm/qwen3-0-6b-onnx-genai-int4-kquantlast-emb-int4</c>. ~461 MB, Apache-2.0 - the preset
    /// for an app that cannot take the Llama terms, and the model the nightly lane uses.
    /// <para>
    /// <b>Smaller on disk is not smaller in memory.</b> The Qwen3 family shares its attention
    /// geometry across sizes, so this model's KV cache per token is several times
    /// <c>Llama32_1BInstructInt4</c>'s while its weights are roughly a third the size - which is
    /// exactly the case the budget exists to catch, and exactly the case a "pick the smaller file"
    /// heuristic gets wrong. The generator script emits the real numbers from the shipped
    /// <c>genai_config.json</c>; section 19 item 2 confirms them. Its only published throughput is
    /// AWS Graviton (70.4 tok/s, peak 838 MB decimal), and <b>that figure is deliberately not in
    /// <see cref="ChatModelShape.MeasuredPeakBytes"/></b>. §9.3 is the normative formula and it
    /// reads <c>MeasuredPeakBytes</c> alone: there is no field, rule or option that discounts a
    /// measurement by where it was taken, so a server peak entering the budget would silently
    /// become a phone budget. <c>MeasuredPeakBytes</c> is therefore <c>null</c> for this preset,
    /// the arithmetic governs, and <c>MeasuredOn</c> records where the published figure came from
    /// and that it is not in the budget. Section 19 item 3 is the on-device measurement that would
    /// fill the field honestly.
    /// </para>
    /// </summary>
    public static ChatPreset Qwen3_600MInt4 { get; }

    public static IReadOnlyList<ChatPreset> All { get; }

    /// <exception cref="EdgeChatException"><see cref="EdgeErrorCode.ChatModelNotRegistered"/>.</exception>
    public static ChatPreset ById(string id);
}
```

`Phi-4-mini` is not a preset. Its only `cpu_and_mobile` folder is **4.909 GB**
with `context_length` 131072 and a 200,064-token vocabulary — a desktop artifact
wearing a mobile folder name. `Gemma-3-1b` is not a preset either: it is faster
(35.1 tok/s) and smaller on disk (825 MB) than the default, but its
262,144-token vocabulary drives measured peak RSS to 1502 MiB against Llama's
1280 MiB, it scores 37.8 MMLU against 44.2, and the Gemma Terms of Use carry a
Prohibited Use Policy that travels downstream. Wrong trade three ways. Two
presets — one default, one permissively licensed — and the rest when somebody
asks.

### 6.6 Options and the client

```csharp
namespace Qavren.Edge.Chat;

/// <summary>Process-wide ORT GenAI settings, applied by the order-400 startup task.</summary>
public sealed class EdgeGenAiOptions
{
    /// <summary>
    /// Calls <c>Utils.DisableTelemetryEvents()</c>. <b>Default true.</b> GenAI 0.15.0 made 1DS
    /// telemetry opt-OUT, and its Android AAR merges <c>INTERNET</c>,
    /// <c>ACCESS_NETWORK_STATE</c> and an <c>ai.onnxruntime.genai.TelemetryInitializer</c> content
    /// provider into every consuming APK whether the app wanted them or not. An offline-first MIT
    /// suite does not ship that on by default; the merged permissions are documented in the
    /// package README and asserted by a device test (section 16.4) so they are visible rather than
    /// discovered.
    /// </summary>
    public bool DisableTelemetry { get; set; } = true;

    /// <summary>Creates the single process-wide <c>OgaHandle</c>. Set false when another library already owns one.</summary>
    public bool OwnRuntimeHandle { get; set; } = true;

    /// <summary>
    /// Disposes that handle on <c>Stopping</c>, which calls <c>OgaShutdown()</c>. Safe since GenAI
    /// 0.15.0, which added transparent re-initialisation on the next call; before 0.15.0 the
    /// process could never use GenAI again, which is one more reason the floor is 0.15.2.
    /// </summary>
    public bool ShutdownOnStopping { get; set; } = true;
}

public enum EdgeGuidancePolicy
{
    /// <summary>
    /// Default. A <c>ChatResponseFormatJson</c> is refused with
    /// <see cref="EdgeErrorCode.ChatGuidanceUnavailable"/>, never silently ignored.
    /// <para>
    /// The refusal is scoped to <c>ChatResponseFormatJson</c> and to nothing else.
    /// <c>ChatResponseFormat.Text</c> is a non-null value that asks for plain text, needs no
    /// constrained decoding, and is already what this client does - refusing it would fail a
    /// caller for requesting the default behaviour. A null <c>ResponseFormat</c> is likewise
    /// untouched. Upstream's own client maps only <c>ChatResponseFormatJson</c> onto
    /// <c>SetGuidance</c>, and this is the same boundary drawn honestly instead of silently.
    /// </para>
    /// </summary>
    Disabled,

    /// <summary>Probe once per model; apply <c>SetGuidance</c> only when the probe proves the constraint is enforced, otherwise fall through unconstrained and record it.</summary>
    PreferNative,

    /// <summary>Probe once; throw <see cref="EdgeErrorCode.ChatGuidanceUnavailable"/> when the probe does not prove enforcement.</summary>
    RequireNative,
}

/// <summary>
/// The probe's outcome.
/// </summary>
/// <remarks>
/// <see cref="NotEnforced"/> means "not proven enforced", <b>not</b> "proven absent": a
/// guidance-enabled build could also fail the probe if the model or its template is unusual, which
/// is why the taxonomy separates a throw (<see cref="ProbeFailed"/>) from a mismatch. The
/// diagnostics key says exactly that.
/// </remarks>
public enum EdgeGuidanceProbeResult { Unprobed, Enforced, NotEnforced, ProbeFailed }

public sealed class ChatThermalOptions
{
    /// <summary>At or above this, pace the decode to <see cref="ThrottledTokensPerSecond"/>. Default <c>Serious</c>.</summary>
    public EdgeThermalState ThrottleAt { get; set; } = EdgeThermalState.Serious;

    /// <summary>At or above this, terminate the turn. Default <c>Critical</c>.</summary>
    public EdgeThermalState AbortAt { get; set; } = EdgeThermalState.Critical;

    /// <summary>Android's headroom, where 1.0 <i>is</i> the documented SEVERE threshold. Null disables.</summary>
    public float? ThrottleHeadroom { get; set; } = 1.0f;

    /// <summary>The paced rate. Default 8 tok/s - readable, and a visibly slow answer beats a dead one.</summary>
    public double ThrottledTokensPerSecond { get; set; } = 8.0;

    /// <summary>
    /// How often the monitor is re-read mid-decode. Default 1 s, which is also sub-project 2's
    /// monitor cache window and Android's <c>GetThermalHeadroom</c> rate limit - so sampling costs
    /// nothing beyond the cached read.
    /// </summary>
    public TimeSpan SampleInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Refuse to START a turn in low-power mode. Default false: that is a product decision, not a safety one.</summary>
    public bool RefuseNewTurnsInLowPowerMode { get; set; }

    /// <summary>
    /// Refuse to start a turn when <c>EdgeResourceSnapshot.Thermal</c> is
    /// <see cref="EdgeThermalState.Unknown"/>. <b>Default false</b>, and that is a decision rather
    /// than an accident of enum ordering.
    /// <para>
    /// Sub-project 2 numbers <c>Unknown = 0</c>, below <c>Nominal</c>, and its own doc says "the
    /// platform reports nothing usable. Never treat this as &quot;fine&quot;." A bare
    /// <c>state &gt;= ThrottleAt</c> would treat it as better than fine - never throttling, never
    /// aborting - on every desktop, every hosted runner, and every Android device below API 29
    /// where the thermal API is unavailable. So the comparisons in section 10(f) are written
    /// explicitly as <c>thermal != Unknown &amp;&amp; thermal &gt;= threshold</c>, with the intent
    /// in the code rather than in the enum's numbering.
    /// </para>
    /// <para>
    /// Having made it explicit, the default is still "do not refuse", for the same reason the
    /// memory gate skips an unreadable <c>AvailableMemoryBytes</c> rather than refusing on it
    /// (section 9.3): a reading nobody can make is never a refusal, and refusing here would make
    /// chat unusable on every desktop. What sub-project 4 owes instead is visibility - <c>Unknown</c>
    /// is published verbatim in the <c>thermalState</c> diagnostics key and in
    /// <c>ChatTurnStatus.Thermal</c>, never normalised to <c>Nominal</c> - and this switch, for a
    /// caller who has measured their hardware and wants the opposite.
    /// </para>
    /// <para>
    /// <c>ThermalHeadroom</c> gets the same treatment for free: Android returns <c>NaN</c> when it
    /// is polled faster than about once a second or when the device does not support it, and
    /// <c>NaN &gt;= 1.0f</c> is <c>false</c>, so an unreadable headroom never throttles. That is
    /// the same rule reached by a different route, and it is stated so nobody "fixes" it.
    /// </para>
    /// </summary>
    public bool RefuseWhenThermalUnknown { get; set; }
}

public sealed class ChatHistoryOptions
{
    public int MaxTurns { get; set; } = 8;
    public int MaxHistoryTokens { get; set; } = 1024;
    public bool PreserveSystemMessages { get; set; } = true;

    /// <summary>Never reduce below this many trailing messages, whatever the budget says.</summary>
    public int MinimumPreservedMessages { get; set; } = 2;

    /// <summary>
    /// A message whose <c>AdditionalProperties</c> carries any of these keys is <b>pinned</b> and
    /// is never evicted, whatever the budget says. Default: one entry,
    /// <c>"qavren.edge.rag.context"</c>.
    /// <para>
    /// That literal is <c>Qavren.Edge.Rag</c>'s <c>RagCitations.ContextMessagePropertyKey</c>,
    /// duplicated here rather than referenced, because this package must not depend on that one.
    /// A tier-1 test asserts the two strings are equal - the same deliberate duplication, with the
    /// same guard, that sub-project 2 uses for its two query-generator service keys.
    /// </para>
    /// <para>
    /// Pinning is what keeps the RAG block out of the eviction set. Without it the injected
    /// context is an unpaired user message at the tail, the reducer's "evict whole turns" rule has
    /// no turn to put it in, and the first thing a tight budget would throw away is the grounding.
    /// </para>
    /// </summary>
    public IList<string> PinnedMessageKeys { get; }
}

public sealed class ChatProvisioningOptions
{
    /// <summary>
    /// Free disk required beyond the bytes still to transfer, checked against the <b>bundle
    /// total</b> before the first byte. Sub-project 2's HTTP source checks per file against a
    /// 32 MiB margin, which is the right question for a 23 MiB embedding graph and the wrong one
    /// for a 1.24 GB-decimal folder. Default 512 MiB.
    /// </summary>
    public long FreeDiskMarginBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>
    /// Consulted once, immediately before the first byte of a transfer. Returning false makes
    /// <c>ProvisionAsync</c> throw <see cref="EdgeErrorCode.ChatDownloadNotPermitted"/> without
    /// opening a connection. Null (the default) permits the transfer.
    /// <para>
    /// <b>This is a hook and not a network stack, deliberately.</b> Reading whether the connection
    /// is metered means <c>Microsoft.Maui.Networking.Connectivity</c> on MAUI,
    /// <c>NetworkInterface</c>/<c>NWPathMonitor</c> elsewhere, and a per-platform answer that does
    /// not exist on a bare <c>ServiceCollection</c> - which suite decision 13 says this package
    /// must run on. So the policy is one line in the app:
    /// <c>o.IsTransferPermitted = () =&gt; Connectivity.Current.ConnectionProfile == ConnectionProfile.WiFi;</c>
    /// and the sample app (section 18) shows exactly that. Google Play warns a user above 200 MB
    /// on mobile data and both shipped presets are far past it, so an app that ships chat and
    /// never sets this has made a decision, which is why the plan's consent sheet names the
    /// transfer size.
    /// </para>
    /// </summary>
    public Func<bool>? IsTransferPermitted { get; set; }
}

public sealed class EdgeChatOptions
{
    /// <summary>Set by <c>AddOnnxChat(preset, …)</c>. The callback may read it; replacing it is unsupported.</summary>
    public ChatPreset Preset { get; internal set; } = null!;

    /// <summary>An already-staged model directory, bypassing provisioning. Tests and the nightly lane.</summary>
    public string? ModelDirectoryOverride { get; set; }

    /// <summary>
    /// Null derives the value from the budget, capped by the preset's default and by
    /// <c>Shape.ContextLength</c>. Setting it turns the budget into a refusal rather than a cap: a
    /// value that does not fit throws <see cref="EdgeErrorCode.ChatInsufficientMemory"/> carrying
    /// the largest context that would have fit.
    /// </summary>
    public int? MaxContextTokens { get; set; }

    /// <summary>
    /// Null falls through to <c>Preset.DefaultMaxOutputTokens</c>. A per-call
    /// <c>ChatOptions.MaxOutputTokens</c> beats both - see "Precedence" below.
    /// </summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>
    /// Null falls through to <c>Preset.DefaultTemperature</c>; a per-call
    /// <c>ChatOptions.Temperature</c> beats both.
    /// </summary>
    public float? Temperature { get; set; }

    /// <summary>Null falls through to <c>Preset.DefaultTopP</c>; <c>ChatOptions.TopP</c> beats both.</summary>
    public float? TopP { get; set; }

    /// <summary>Null falls through to <c>Preset.DefaultTopK</c>; <c>ChatOptions.TopK</c> beats both.</summary>
    public int? TopK { get; set; }

    /// <summary>Tokens held back from the prompt so a reply always has room. Default 64.</summary>
    public int ReservedPromptTokens { get; set; } = 64;

    public string? SystemPrompt { get; set; }

    /// <summary>
    /// <b>Added to</b> <c>Preset.StopSequences</c>, never replacing them: stop sequences are a
    /// property of the model's template as much as of the caller's intent, and a consumer adding
    /// <c>"\nUser:"</c> must not thereby delete the preset's <c>&lt;|eot_id|&gt;</c>. The effective
    /// set is the ordinal-distinct union of the preset's, these, and
    /// <c>ChatOptions.StopSequences</c> - see "Precedence" below.
    /// </summary>
    public IList<string> StopSequences { get; }

    /// <summary>
    /// Overrides <c>Tokenizer.ApplyChatTemplate</c>. The escape hatch for a model whose Jinja
    /// template minja cannot parse - which surfaces on the FIRST <c>ApplyChatTemplate</c> call and
    /// not at load, which is why section 9.3 probes it at warm-up.
    /// </summary>
    public ChatPromptFormatter? PromptFormatter { get; set; }

    /// <summary>Throw <see cref="EdgeErrorCode.ChatTemplateUnsupported"/> rather than falling back. Default true.</summary>
    public bool RequireChatTemplate { get; set; } = true;

    public EdgeGuidancePolicy Guidance { get; set; } = EdgeGuidancePolicy.Disabled;

    /// <summary>
    /// Keep ONE <c>Generator</c> alive between turns, keyed by <c>ChatOptions.ConversationId</c>,
    /// so a follow-up skips prefill (TTFT 950 ms -&gt; the append alone).
    /// <para>
    /// <b>Who mints the id.</b> MEAI callers do not invent a <c>ConversationId</c>, so this client
    /// does, exactly once per conversation: when caching is on and the request's
    /// <c>ChatOptions.ConversationId</c> is null, the turn builds a fresh <c>Generator</c> and
    /// mints a new id (a GUID string). That id is stamped on <b>every</b>
    /// <c>ChatResponseUpdate.ConversationId</c> of that response and therefore on the aggregated
    /// <c>ChatResponse.ConversationId</c>, and a caller who wants the next turn to hit the cache
    /// echoes it back in <c>ChatOptions.ConversationId</c>. A caller who supplies an id keeps it;
    /// nothing is minted over the top of one.
    /// </para>
    /// <para>
    /// <b>A null id never hits the cache.</b> Not "matches the cached null" - never hits. Two
    /// unrelated conversations that both leave the field null would otherwise share one KV cache
    /// and therefore one history, which is a correctness bug wearing a performance optimisation's
    /// clothes. Null means "build me a fresh generator and tell me its id", and that is the whole
    /// rule. With caching off, no id is minted and the caller's own value is echoed unchanged.
    /// </para>
    /// <para>
    /// <b>What <c>max_length</c> means on a cached generator, which is not what an earlier draft
    /// of this spec claimed.</b> <c>SetSearchOption</c> exists only on <c>GeneratorParams</c>,
    /// which is consumed by <c>Generator(Model, GeneratorParams)</c> at construction; the shipped
    /// <c>Generator</c> surface has no way to change a search option afterwards, and
    /// <c>SetRuntimeOption</c>'s documented keys are only <c>terminate_session</c>,
    /// <c>enable_profiling</c> and <c>lang_id</c>. So "set <c>max_length</c> on every turn
    /// including a cached one" is not implementable, and sub-project 4 does not pretend to. It
    /// splits the two jobs that value was doing instead: a cached generator is built once with
    /// <c>max_length = resolvedContext</c> - the budget's answer, and the <b>KV memory cap</b> -
    /// while the <b>per-turn output cap</b> is a managed generated-token counter in the decode
    /// loop (section 10 f). Upstream's defect is not that it sets a stale value, it is that with
    /// caching on it sets <b>no</b> <c>max_length</c> at all and KV allocation falls back to the
    /// model's declared <c>context_length</c> - 131072 on a stock Llama export, which is the
    /// entire jetsam scenario. Section 9.1 step 5 bakes the same <c>resolvedContext</c> into the
    /// config as the belt to this brace.
    /// </para>
    /// </summary>
    public bool EnableConversationCache { get; set; } = true;

    /// <summary>
    /// Drop the cached <c>Generator</c> - not the model - on <c>Sleeping</c>. Default true: the KV
    /// cache is the half that is cheap to rebuild and expensive to be killed for.
    /// </summary>
    public bool DropConversationCacheOnSleep { get; set; } = true;

    /// <summary>Dispose the model itself on <c>MemoryPressure(Critical)</c>. Default true.</summary>
    public bool DropOnMemoryPressure { get; set; } = true;

    /// <summary>
    /// Unload on <c>Sleeping</c>. <b>Default false</b>: an app backgrounded for two seconds should
    /// not pay a 1.6 s reload, and <c>Sleeping</c> already stops the in-flight turn and drops the
    /// KV cache either way.
    /// </summary>
    public bool UnloadOnSleeping { get; set; }

    /// <summary>Turns beyond this waiting for the model gate are refused with <see cref="EdgeErrorCode.ChatBusy"/>. Default 4.</summary>
    public int MaxQueuedTurns { get; set; } = 4;

    /// <summary>How long a queued turn waits for the gate. Default 30 s.</summary>
    public TimeSpan TurnQueueTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Bounds <c>new Model(config)</c> plus tokenizer construction. Default 2 minutes.</summary>
    public TimeSpan LoadTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Raw <c>GeneratorParams.SetSearchOption</c> entries, applied last so they win over everything
    /// mapped from <c>ChatOptions</c>.
    /// <para>
    /// <b>The native API has exactly two overloads</b> - <c>SetSearchOption(string, double)</c> and
    /// <c>SetSearchOption(string, bool)</c> - so this dictionary's values are converted or refused,
    /// never quietly dropped:
    /// </para>
    /// <list type="bullet">
    /// <item><c>bool</c> -&gt; the bool overload.</item>
    /// <item><c>double</c> -&gt; the double overload.</item>
    /// <item><c>int</c>, <c>long</c>, <c>short</c>, <c>byte</c>, <c>float</c>, <c>decimal</c> -&gt;
    ///   the double overload <b>when the conversion round-trips exactly</b>; when it does not (a
    ///   <c>long</c> past 2^53, a <c>decimal</c> that is not representable),
    ///   <see cref="EdgeErrorCode.ChatOptionUnsupported"/> naming the key and the value.</item>
    /// <item>anything else - <c>string</c>, <c>null</c>, an enum, an object -&gt;
    ///   <see cref="EdgeErrorCode.ChatOptionUnsupported"/> naming the key and the CLR type.</item>
    /// </list>
    /// <para>
    /// <b>That is deliberately the opposite of the rule for
    /// <c>ChatOptions.AdditionalProperties</c></b>, where a non-<c>bool</c>/<c>double</c> value is
    /// ignored without error (section 10 e). The two dictionaries are different animals. That one
    /// is an open bag shared with the rest of the MEAI pipeline - <c>Qavren.Edge.Rag</c> puts an
    /// <c>IReadOnlyList&lt;RagSource&gt;</c> in it - so ignoring what this leaf does not recognise
    /// is the only correct behaviour. This one exists for exactly one purpose and nothing else
    /// writes to it, so a <c>"0.7"</c> typed as a string is a typo, and a typo in the escape hatch
    /// must not become a temperature that was never applied.
    /// </para>
    /// <para>
    /// <c>max_length</c> is refused whatever its type: it is the memory cap, and section 9.3's
    /// entire gate depends on the generator getting the value the budget chose.
    /// </para>
    /// </summary>
    public IDictionary<string, object> SearchOptions { get; }

    /// <summary>
    /// <c>Config.Overlay(json)</c>, applied last before load. The whole execution-provider escape
    /// hatch, and deliberately the only one: GenAI names providers as strings inside
    /// <c>genai_config.json</c> and has no CoreML and no NNAPI provider, so a typed policy object
    /// would model choices that do not exist on any platform this suite ships to. Setting any
    /// provider other than CPU on a mobile TFM is refused with
    /// <see cref="EdgeErrorCode.ChatExecutionProviderUnsupported"/> naming the provider, rather
    /// than being silently ignored by the native config parser.
    /// </summary>
    public string? ConfigOverlayJson { get; set; }

    public ChatMemoryBudgetOptions Memory { get; } = new();
    public ChatThermalOptions Thermal { get; } = new();
    public ChatHistoryOptions History { get; } = new();
    public ChatProvisioningOptions Provisioning { get; } = new();
}

/// <summary>Builds the prompt string handed to the tokenizer.</summary>
public delegate string ChatPromptFormatter(
    IReadOnlyList<ChatMessage> messages, ChatOptions? options, IChatPromptContext context);

/// <summary>
/// What a formatter may reach. Deliberately not the raw <c>Tokenizer</c>: the GenAI C API is
/// documented as not thread safe and the decode loop owns the gate.
/// </summary>
public interface IChatPromptContext
{
    /// <param name="messagesJson">A JSON array of <c>{"role","content"}</c>.</param>
    /// <exception cref="EdgeChatException"><see cref="EdgeErrorCode.ChatTemplateUnsupported"/>.</exception>
    string ApplyChatTemplate(string messagesJson, bool addGenerationPrompt);

    int CountTokens(string text);
    ChatModelShape Shape { get; }

    /// <summary>The context the budget allowed, which may be below the preset's default.</summary>
    int ResolvedContextTokens { get; }
}

/// <summary>Why a turn ended.</summary>
/// <remarks>
/// MEAI's <c>ChatFinishReason</c> cannot express "the OS suspended us" or "we ran out of memory
/// mid-turn", and a turn that was cut short must never be presented as one that finished. Both are
/// emitted: the nearest <c>ChatFinishReason</c> for the ecosystem, and this for the truth.
/// </remarks>
public enum EdgeChatStopReason
{
    Completed, MaxOutputTokens, StopSequence, Cancelled,
    Suspended, MemoryPressure, Thermal, Error,
}

/// <summary>
/// One turn's honest record, carried on the final <c>ChatResponseUpdate</c> and on the
/// <c>ChatResponse</c> under <see cref="EdgeChatProperties.TurnStatus"/>. One strongly-typed record
/// under one key, rather than seven loose string keys.
/// </summary>
/// <param name="PromptTokens">
/// Total sequence length the model conditioned on, after any conversation-cache append. Equal to
/// <c>UsageDetails.InputTokenCount</c> on the same response.
/// </param>
/// <param name="PromptTokensAppended">
/// Tokens actually encoded and appended this turn: equal to <paramref name="PromptTokens"/> on a
/// fresh generator, and the delta alone on a conversation-cache hit (section 10 f).
/// </param>
/// <param name="ConversationId">
/// The effective id for this response - the caller's if they supplied one, otherwise the one this
/// client minted (section 6.6). Also on every <c>ChatResponseUpdate.ConversationId</c>.
/// </param>
public sealed record ChatTurnStatus(
    EdgeChatStopReason StopReason,
    string ModelId,
    string? ConversationId,
    int PromptTokens,
    int PromptTokensAppended,
    int GeneratedTokens,
    int ContextTokens,
    int MessagesDropped,
    TimeSpan TimeToFirstToken,
    TimeSpan Duration,
    double TokensPerSecond,
    EdgeThermalState Thermal,
    bool ThermalThrottled);

public static class EdgeChat
{
    public static ChatTurnStatus? GetTurnStatus(this ChatResponse response);
    public static ChatTurnStatus? GetTurnStatus(this ChatResponseUpdate update);
}

public sealed record ChatClientStatistics(
    int Turns, int RejectedTurns, long TokensGenerated,
    double? TokensPerSecondP50, double? TokensPerSecondP95,
    double? LastTokensPerSecond, TimeSpan? LastTimeToFirstToken,
    long? ProcessWorkingSetBytes, long? PeakWorkingSetBytes,
    int ThermalThrottleEvents, int ThermalAbortEvents,
    int UnloadEvents, int TerminationEvents, int HistoryReductions,
    EdgeChatStopReason? LastStopReason);

/// <summary>MEAI <see cref="IChatClient"/> over ORT GenAI.</summary>
/// <remarks>
/// <b>Thread safety.</b> <c>src/ort_genai_c.h</c> states flatly "This API is not thread safe",
/// while <c>IChatClient</c>'s own contract requires every member to be safe for concurrent use.
/// Both are honoured by SERIALISATION, not parallelism: one async gate per model holds for a whole
/// turn, and a second concurrent caller queues rather than allocating a second <c>Generator</c> and
/// therefore a second full KV cache. On a phone that is the point; on a server it is a throughput
/// ceiling, and the README says so rather than letting someone read it as a bug.
/// </remarks>
public sealed class EdgeChatClient : IChatClient
{
    public string ModelId { get; }
    public ChatClientStatistics Statistics { get; }
    public ChatModelInfo? Model { get; }

    /// <summary>Implemented as <c>GetStreamingResponseAsync(...).ToChatResponseAsync(ct)</c>, so the two paths cannot diverge.</summary>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves, in order: <c>this</c> when <c>serviceKey is null &amp;&amp;
    /// serviceType.IsInstanceOfType(this)</c>; <see cref="ChatClientMetadata"/> with
    /// <c>ProviderName</c> <c>"onnxruntime-genai"</c> and <c>DefaultModelId</c> =
    /// <see cref="ModelId"/>; <see cref="ChatModelInfo"/>; <see cref="ChatClientStatistics"/>;
    /// <see cref="IChatModelHost"/>; and, while a model is loaded, <c>Model</c>, <c>Tokenizer</c>
    /// and <c>Config</c>.
    /// <para>
    /// <b>This method is the entire integration surface for three downstream consumers</b> and is
    /// specified as a contract, not an implementation detail: Semantic Kernel's
    /// <c>GetModelId()</c> is literally
    /// <c>GetService&lt;ChatClientMetadata&gt;()?.DefaultModelId</c>, Agent Framework's
    /// <c>ChatClientAgent</c> avoids double-wrapping by calling
    /// <c>GetService&lt;FunctionInvokingChatClient&gt;()</c>, and MEAI's own
    /// <c>GetRequiredService&lt;T&gt;()</c> goes through it. Returning the raw <c>Model</c> is also
    /// what lets a consumer build <c>OnnxRuntimeGenAIChatClient</c>, <c>MultiModalProcessor</c> or
    /// LoRA <c>Adapters</c> themselves without sub-project 4 wrapping any of it.
    /// </para>
    /// </summary>
    public object? GetService(Type serviceType, object? serviceKey = null);

    /// <summary>Releases this client's lease and conversation cache. Does not dispose the shared model - the host owns it.</summary>
    public void Dispose();
}
```

**Precedence.** Three layers can set the same generation knob, and the rule is
one sentence: **the per-call `ChatOptions` wins, then `EdgeChatOptions`, then the
`ChatPreset` default.** Nearest to the call site wins, and a `null` at any layer
is "not set" rather than "set to nothing", so falling through is the default
behaviour and overriding is the exception. This is §10(e)'s mapping stated as a
rule so the mapping does not have to restate it per knob.

| Knob | Per-call `ChatOptions` | `EdgeChatOptions` | `ChatPreset` | Then |
|---|---|---|---|---|
| Max output tokens | `MaxOutputTokens` | `MaxOutputTokens` | `DefaultMaxOutputTokens` | → `max_length` with the prompt (§10 e) |
| Temperature | `Temperature` | `Temperature` | `DefaultTemperature` | → `temperature` |
| Top-P | `TopP` | `TopP` | `DefaultTopP` | → `top_p`, forces `do_sample` |
| Top-K | `TopK` | `TopK` | `DefaultTopK` | → `top_k`, forces `do_sample` |
| Context length | — | `MaxContextTokens` | `DefaultMaxContextTokens` | → the budget (§9.3); `ChatOptions` has no equivalent and cannot raise it |
| Seed | `Seed` | — | — | → `random_seed` |
| Repetition penalty | `PresencePenalty` | — | — | → `repetition_penalty` |

Two knobs are **unions rather than overrides**, and they are called out because
a reader who assumed the table covered everything would get them wrong:

- **Stop sequences.** The effective set is the ordinal-distinct union of
  `ChatPreset.StopSequences`, `EdgeChatOptions.StopSequences` and
  `ChatOptions.StopSequences`, in that order, de-duplicated with
  `StringComparer.Ordinal` and then sorted longest-first so the rolling matcher
  in §10(f) always reports the longest match at a given position. A preset's stop
  sequences describe the model's own template — `<|eot_id|>` is not a preference —
  so a caller adding one must not silently delete them. A caller who genuinely
  needs the preset's set gone clears `EdgeChatOptions.StopSequences` and
  registers a preset that declares none; there is no "replace" flag, because the
  failure mode of one is a model that never stops.
- **Raw search options.** `EdgeChatOptions.SearchOptions` is applied **last** and
  beats everything in the table, by design — it is the escape hatch. The single
  exception is `max_length`, refused with `ChatOptionUnsupported` (7108), because
  it is the memory cap and §9.3's entire gate depends on the value the budget
  chose being the value the generator gets.

`ChatOptions.AdditionalProperties` entries are forwarded as raw search options
only when the value is a `bool` or a `double`; any other value is **ignored, not
an error**, which is what lets `Qavren.Edge.Rag` carry a structured payload on
the same dictionary (§11).

`SystemPrompt` is not in the table because it is not a knob: `EdgeChatOptions.SystemPrompt`
and `ChatOptions.Instructions` both become a leading `ChatRole.System` message,
the options value first, and an explicit `ChatRole.System` message already in the
list is preserved ahead of both. Nothing is dropped, so nothing needs a winner.

### 6.7 History reduction

```csharp
namespace Qavren.Edge.Chat;

/// <summary>
/// Implements MEAI's <b>stable</b> <c>IChatReducer</c>. System messages are preserved, pinned
/// messages are preserved, then whole <i>groups</i> are evicted oldest-first until the token budget
/// or <see cref="ChatHistoryOptions.MaxTurns"/> binds, with
/// <see cref="ChatHistoryOptions.MinimumPreservedMessages"/> as the floor. Counts with the model's
/// own tokenizer, not an estimate. Never throws.
/// </summary>
/// <remarks>
/// <para>
/// <b>"Whole turns" needs a definition once the tail is not turn-shaped, so here it is.</b> An
/// <b>eviction group</b> is a non-system, non-pinned message together with every message after it
/// up to but excluding the next <c>ChatRole.User</c> message. On an ordinary history that is
/// exactly a user/assistant pair, which is the property the old wording was reaching for: an
/// assistant message is never evicted without the user message it answered. On a history whose
/// tail is user-then-user - which is what the RAG recipe produces, because it injects a pinned
/// context message immediately before the real question (section 11 step 6) - the pinned message
/// is not in any group at all, and the real user message opens the newest group. Nothing is left
/// dangling and nothing needs a special case.
/// </para>
/// <para>
/// <b>The floor is system messages + pinned messages + the newest
/// <see cref="ChatHistoryOptions.MinimumPreservedMessages"/> messages</b>, and the reducer stops
/// there even if the budget is still exceeded. What happens next is section 10(d)'s job: the turn
/// fails with <see cref="EdgeErrorCode.ChatPromptTooLong"/>. That is the deliberate trade - an
/// answer that silently lost its sources is worse than a turn that says the context did not fit
/// and names <c>RagOptions.MaxContextTokens</c> as the knob.
/// </para>
/// </remarks>
/// <remarks>
/// Public, and applied internally. <c>ReducingChatClient</c>, <c>UseChatReducer</c>,
/// <c>MessageCountingChatReducer</c> and <c>SummarizingChatReducer</c> are all
/// <c>[Experimental(AIChatReduction)]</c>, and sub-project 4 does not put an experimental attribute
/// on a shipped public surface - but <c>IChatReducer</c> itself is stable, so a consumer who
/// accepts that warning can hand this instance straight to <c>UseChatReducer</c>.
/// </remarks>
public sealed class EdgeChatTokenBudgetReducer : IChatReducer
{
    public EdgeChatTokenBudgetReducer(Func<string, int> countTokens, ChatHistoryOptions options);
    public Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages, CancellationToken cancellationToken);
}
```

### 6.8 Model host, lease and provisioning

```csharp
namespace Qavren.Edge.Chat;

public sealed record ChatBackendReport(
    string GenAiVersion, string OrtVersion, string RuntimeIdentifier, string? Abi,
    IReadOnlyList<string> Providers, bool ChatTemplateSupported,
    EdgeGuidanceProbeResult Guidance, string PromptFormatter);

public sealed record ChatModelInfo(
    string PresetId, string ModelId, string Directory, string DirectorySha16,
    ChatModelShape Shape, ChatMemoryDecision Budget, int ResolvedContextTokens,
    ChatBackendReport Backend, TimeSpan LoadDuration, DateTimeOffset LoadedAtUtc,
    int LoadCount, int ActiveLeases, bool IsLoaded);

/// <summary>
/// A borrowed model. <b>Exclusive</b> for the duration of one turn - there is no shared-reader
/// mode, because the GenAI C API is not thread safe and a second generator is a second full KV
/// cache. Disposing releases the lease; the <c>Model</c> and <c>Tokenizer</c> are disposed only
/// when the last lease returns after a drop. This is sub-project 2's <c>OnnxSessionLease</c>
/// contract and it exists for the same reason: every ORT GenAI wrapper type is <c>IDisposable</c>
/// <i>with a finalizer</i>, so a dropped-but-undisposed model pins the whole native graph until GC
/// - jetsam bait - while disposing one under a live <c>GenerateNextToken</c> is a native access
/// violation.
/// </summary>
public sealed class ChatModelLease : IDisposable
{
    public Model Model { get; }
    public Tokenizer Tokenizer { get; }
    public Config Config { get; }
    public ChatModelInfo Info { get; }

    /// <summary>Idempotent.</summary>
    public void Dispose();
}

public interface IChatModelHost
{
    /// <summary>
    /// Awaits <c>IEdgeHost.EnsureStartedAsync</c>, verifies provisioning, resolves the budget,
    /// loads on first call, and returns the exclusive lease.
    /// </summary>
    ValueTask<ChatModelLease> AcquireAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything <see cref="AcquireAsync"/> does <b>except</b> awaiting host startup, so an
    /// <c>IEdgeStartupTask</c> can drive a load without deadlocking on its own completion. Public
    /// on purpose: sub-project 2 documents this exact hole as unclosable from L1 because its
    /// equivalent is <c>internal</c> to another assembly. Sub-project 4's host and its warm-up task
    /// are in the same assembly, so sub-project 4 does not inherit it.
    /// </summary>
    ValueTask PreloadAsync(CancellationToken cancellationToken = default);

    ChatModelInfo? Describe();

    /// <summary>Marks the model for drop and disposes it once the lease returns.</summary>
    Task<bool> UnloadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a volatile abort flag AND calls
    /// <c>Generator.SetRuntimeOption("terminate_session", "1")</c> on the live generator - the
    /// documented cooperative abort, and the only thing that can stop a multi-second prefill. Safe
    /// to call from a lifecycle observer on another thread; it is the one GenAI call sub-project 4
    /// makes outside the turn gate, and section 20 records that as a stated risk.
    /// </summary>
    void TerminateActiveGeneration();

    /// <summary>False between <c>MemoryPressure(Moderate)</c> and the next <c>Resumed</c>.</summary>
    bool IsAcceptingTurns { get; }
}

/// <summary>What a consent screen needs, and nothing it does not. No network I/O beyond a stat.</summary>
public sealed record ChatModelPlan(
    string PresetId, string DisplayName, bool Provisioned,
    long TotalBytes, long BytesAlreadyPresent, long BytesToTransfer,
    long? FreeDiskBytes, bool FitsOnDisk, string Directory,
    string SpdxLicense, Uri? LicenseUri,
    string? HuggingFaceRepo, string? HuggingFaceRevision);

/// <summary>
/// The only way a chat model's bytes arrive. Nothing downloads implicitly:
/// <c>IChatModelHost.AcquireAsync</c> on an unprovisioned model throws
/// <see cref="EdgeErrorCode.ChatModelNotProvisioned"/> rather than starting a 1.24 GB transfer. The
/// <see cref="Plan"/> - consent - <see cref="ProvisionAsync"/> sequence is what App Store Review
/// 4.2.3(ii) requires ("disclose the size of the download and prompt users before doing so"),
/// expressed in the API rather than left to a README.
/// </summary>
public interface IChatModelProvisioner
{
    ChatModelPlan Plan();
    bool IsProvisioned { get; }

    /// <summary>
    /// Pre-checks free disk against the <b>bundle</b> total, then delegates to sub-project 2's
    /// <c>IOnnxModelStore.EnsureAsync</c> - resumable ranged GET, per-file SHA-256 against the
    /// git-LFS oid, atomic marker, stale-revision purge - all reused unchanged.
    /// </summary>
    ValueTask<ProvisionedModel> ProvisionAsync(
        IProgress<ModelProvisioningProgress>? progress = null,
        CancellationToken cancellationToken = default);

    ValueTask RemoveAsync(CancellationToken cancellationToken = default);
}
```

### 6.9 Registration

```csharp
namespace Qavren.Edge.Chat;

public static class ChatOnnxEdgeBuilderExtensions
{
    /// <summary>
    /// Calls <c>AddOnnx()</c> (so sub-project 2's order-200 <c>OrtEnv</c> task, model paths,
    /// resource monitor and model store are present and correctly ordered), registers the device
    /// profile, the model host, the provisioner, the order-400 startup task, the lifecycle observer
    /// and the diagnostics contributor, then delegates to
    /// <c>services.AddChatClient(sp =&gt; …)</c>.
    /// <para>
    /// Registration goes through MEAI's own <c>AddChatClient</c> and not a hand-rolled
    /// <c>IChatClient</c> descriptor, because that is the descriptor Agent Framework and Semantic
    /// Kernel resolve, and because its factory is <c>ChatClientBuilder.Build</c> - so the pipeline
    /// is constructed lazily from the app's provider and the order of builder calls does not
    /// matter.
    /// </para>
    /// Returns <see cref="EdgeBuilder"/> rather than <c>ChatClientBuilder</c>, matching
    /// <c>AddOnnxEmbeddings</c> and the rest of the suite's chaining idiom; the MEAI pipeline is
    /// the optional <paramref name="pipeline"/> callback. <b>Remember MEAI's ordering
    /// contract</b>: <c>ChatClientBuilder.Build</c> applies factories in REVERSE, so the FIRST
    /// <c>Use()</c> call is the OUTERMOST client.
    /// </summary>
    /// <param name="preset">Required. There is no default - see <see cref="ChatPresets"/>.</param>
    public static EdgeBuilder AddOnnxChat(this EdgeBuilder builder, ChatPreset preset,
        Action<EdgeChatOptions>? configure = null, Action<ChatClientBuilder>? pipeline = null);

    /// <summary>Keyed registration, mirroring sub-project 1's named databases and sub-project 2's named generators. Uses <c>AddKeyedChatClient</c>.</summary>
    public static EdgeBuilder AddOnnxChat(this EdgeBuilder builder, string name, ChatPreset preset,
        Action<EdgeChatOptions>? configure = null, Action<ChatClientBuilder>? pipeline = null);

    /// <summary>
    /// Order 410. Faults startup with <see cref="EdgeErrorCode.ChatModelNotProvisioned"/> when the
    /// model is absent. It never downloads: blocking <c>IEdgeHost.Started</c> on a 1.24 GB transfer
    /// also blocks every <c>IEdgeDatabase.OpenConnectionAsync</c>.
    /// </summary>
    public static EdgeBuilder RequireChatModelAtStartup(this EdgeBuilder builder, string? name = null);

    /// <summary>
    /// Order 420. Loads the model through <c>IChatModelHost.PreloadAsync</c>, probes the chat
    /// template, and probes guidance when the policy is not <c>Disabled</c>. Off by default: it
    /// forces provisioning. It runs no generation - one token on a 1B model is 30 ms of decode on
    /// top of a 1.6 s load, and nothing about the decode loop is proven by doing it twice.
    /// <para>
    /// Idempotent per registration, made so by scanning the <c>IServiceCollection</c> for a marker
    /// record - <b>not</b> <c>TryAddEnumerable</c>, which on an enumerable service like
    /// <c>IEdgeStartupTask</c> would suppress every <i>other</i> package's task rather than a
    /// duplicate of this one. Sub-project 2's <c>WarmUpSessionAtStartup</c> registers with a bare
    /// <c>AddSingleton</c> and gets duplicate order-220 tasks when called twice; sub-project 4 does
    /// not repeat that.
    /// </para>
    /// </summary>
    public static EdgeBuilder WarmUpChatAtStartup(this EdgeBuilder builder, string? name = null);
}
```

## 7. `Qavren.Edge.Rag`

```csharp
namespace Qavren.Edge.Rag;

/// <summary>
/// Which direction a retriever's score runs.
/// </summary>
/// <remarks>
/// <b>Load-bearing.</b> MEVD's <c>SearchAsync</c> over sub-project 2's store returns a DISTANCE
/// (lower is better, threshold pushed into SQL as <c>distance &lt;=</c>) while
/// <c>HybridSearchAsync</c> returns the RRF score (higher is better, threshold applied client-side
/// as <c>&gt;=</c>). Mixing them silently inverts every ranking, which is why the kind rides on the
/// source rather than being inferred.
/// </remarks>
public enum RetrievalScoreKind { Relevance, Distance }

public sealed record RagSource
{
    public RagSource(string id, string text);

    /// <summary>Stable identifier - a chunk id, a row key. Becomes <c>CitationAnnotation.FileId</c>.</summary>
    public string Id { get; init; }
    public string Text { get; init; }
    public string? Title { get; init; }
    public Uri? Uri { get; init; }
    /// <summary>
    /// Set by the retriever, never by a projector. <see cref="VectorStoreRetriever{TKey,TRecord}"/>
    /// stamps it from the MEVD result after projection.
    /// </summary>
    public double Score { get; init; }

    /// <summary>
    /// Set by the retriever from the search lane it actually took, never by a projector - a
    /// projector cannot know which lane ran, and guessing inverts the ranking.
    /// </summary>
    public RetrievalScoreKind ScoreKind { get; init; }

    /// <summary>
    /// 1-based position in the assembled block. What the model is told to cite as <c>[n]</c>.
    /// Assigned by <c>RagPrompts.Rank</c> after ranking and clamping; 0 until then.
    /// </summary>
    public int Ordinal { get; init; }

    public object? Record { get; init; }
}

public sealed class RetrievalRequest
{
    public int Top { get; set; } = 5;
    public int Skip { get; set; }
    public IReadOnlyCollection<string>? Keywords { get; set; }
    public double? ScoreThreshold { get; set; }
}

/// <summary>The whole retrieval seam.</summary>
/// <remarks>
/// The shape deliberately mirrors Agent Framework's
/// <c>TextSearchProvider(Func&lt;string, CancellationToken, Task&lt;IEnumerable&lt;TextSearchResult&gt;&gt;&gt;)</c>
/// so an app that later adopts an <c>AIContextProvider</c> hands over the same lambda - while
/// sub-project 4 takes no dependency on <c>Microsoft.Agents.AI</c>, because
/// <c>UseAIContextProviders</c> throws <c>InvalidOperationException</c> at invocation time unless
/// there is a live <c>AIAgent.CurrentRunContext</c>, which a plain <c>IChatClient</c> on a bare
/// <c>ServiceCollection</c> never has.
/// </remarks>
public interface IEdgeRetriever
{
    string Name { get; }
    ValueTask<IReadOnlyList<RagSource>> RetrieveAsync(
        string query, RetrievalRequest request, CancellationToken cancellationToken);
}

public sealed class DelegateRetriever : IEdgeRetriever
{
    public DelegateRetriever(string name,
        Func<string, RetrievalRequest, CancellationToken, Task<IEnumerable<RagSource>>> retrieve);
}

public sealed class VectorStoreRetrieverOptions<TRecord>
{
    /// <summary>Use <c>IKeywordHybridSearchable&lt;TRecord&gt;</c> when the collection implements it and keywords are present. Default true.</summary>
    public bool PreferHybridSearch { get; set; } = true;

    /// <summary>Applied with the correct polarity for the lane actually taken. Null means no threshold.</summary>
    public double? ScoreThreshold { get; set; }

    /// <summary>
    /// Upper bound on <c>Top + Skip</c>. 4096 is vec0's <c>SQLITE_VEC_VEC0_K_MAX</c>, which is what
    /// sub-project 2 enforces; exceeding it throws
    /// <see cref="EdgeErrorCode.RagRetrievalFailed"/> naming the limit, rather than surfacing
    /// sub-project 2's <c>KnnLimitExceeded</c> three frames down.
    /// </summary>
    public int MaxCandidates { get; set; } = 4096;

    public Expression<Func<TRecord, bool>>? Filter { get; set; }
}

/// <summary>
/// Adapts <b>any</b> MEVD collection. Nothing here is Qavren-specific: point it at Qdrant, Azure AI
/// Search or <c>EdgeVectorStoreCollection</c> and it behaves identically. It chooses the hybrid
/// lane when the collection implements <c>IKeywordHybridSearchable&lt;TRecord&gt;</c> and keywords
/// are present, the vector lane otherwise, stamps <see cref="RagSource.ScoreKind"/> to match, and
/// always returns best-first.
/// </summary>
public sealed class VectorStoreRetriever<TKey, TRecord> : IEdgeRetriever
    where TKey : notnull where TRecord : class
{
    /// <param name="project">
    /// Maps a record to its <b>content</b>: <see cref="RagSource.Id"/>, <see cref="RagSource.Text"/>,
    /// and optionally <see cref="RagSource.Title"/>, <see cref="RagSource.Uri"/> and
    /// <see cref="RagSource.Record"/>. It takes the record alone, and it is the same one-argument
    /// signature <c>AddVectorStoreRetriever</c> takes - there is exactly one projector shape in
    /// this package.
    /// <para>
    /// <b>The retriever, not the projector, supplies the scoring fields.</b> After calling
    /// <paramref name="project"/> the retriever re-stamps the result with
    /// <c>source with { Score = result.Score ?? 0d, ScoreKind = kind, Ordinal = 0 }</c>, where
    /// <c>kind</c> is <see cref="RetrievalScoreKind.Relevance"/> on the hybrid lane and
    /// <see cref="RetrievalScoreKind.Distance"/> on the vector lane. Anything the projector set on
    /// those three <c>init</c> properties is overwritten. That is deliberate: the polarity is a
    /// property of the search lane the retriever chose at runtime, not of the record, and a
    /// projector cannot know which lane ran. A projector that guesses is the exact bug
    /// <see cref="RetrievalScoreKind"/> exists to prevent.
    /// </para>
    /// <para>
    /// <see cref="RagSource.Ordinal"/> stays 0 here and is assigned by <c>RagPrompts.Rank</c>
    /// after ranking and clamping (section 11), because it is a position in the assembled block
    /// rather than a property of the source.
    /// </para>
    /// </param>
    [RequiresDynamicCode("MEVD reflects over TRecord to build a collection model.")]
    [RequiresUnreferencedCode("MEVD reflects over TRecord to build a collection model.")]
    public VectorStoreRetriever(VectorStoreCollection<TKey, TRecord> collection,
        Func<TRecord, RagSource> project,
        Action<VectorStoreRetrieverOptions<TRecord>>? configure = null);
}

public enum RetrievalQuerySource { LastUserMessage, AllUserMessages }

public sealed class RagOptions
{
    public int Top { get; set; } = 5;

    /// <summary>Budget for the retrieved-context block alone, not the whole prompt. Default 1024.</summary>
    public int MaxContextTokens { get; set; } = 1024;

    /// <summary>Per-source clamp, with a visible <c>…(truncated)</c> suffix. Default 1200 characters.</summary>
    public int MaxCharsPerSource { get; set; } = 1200;

    public RetrievalQuerySource QuerySource { get; set; } = RetrievalQuerySource.LastUserMessage;

    /// <summary>Null uses a stop-worded word split capped at eight terms. Returning empty degrades to pure vector search rather than emitting a malformed FTS MATCH.</summary>
    public Func<string, IReadOnlyCollection<string>>? KeywordExtractor { get; set; }

    /// <summary>
    /// Null uses a chars/4 estimate. Pass the chat model's own counter - reachable as
    /// <c>chatClient.GetService&lt;Tokenizer&gt;()</c> - for exactness against the model that will
    /// actually read the block.
    /// </summary>
    public Func<string, int>? TokenCounter { get; set; }

    public string ContextPrompt { get; set; } = RagPrompts.DefaultContextPrompt;
    public string CitationsPrompt { get; set; } = RagPrompts.DefaultCitationsPrompt;

    /// <summary>Replaces both prompts and the whole block layout.</summary>
    public Func<IReadOnlyList<RagSource>, RagOptions, string>? ContextFormatter { get; set; }

    /// <summary>
    /// Default <c>ChatRole.User</c>, and <b>never</b> <c>System</c>: retrieved text is untrusted
    /// input and must not reach the model as instruction. MEAI's own <c>IChatClient</c>
    /// documentation says the application is responsible for prompt injection unless an
    /// implementation documents safeguards; sub-project 4 documents that it has none, and that this
    /// role choice plus the explicit instruction prefix is the whole of its posture.
    /// </summary>
    public ChatRole ContextRole { get; set; } = ChatRole.User;

    public bool EmitCitations { get; set; } = true;
    public bool AttachSourcesToResponse { get; set; } = true;

    /// <summary>
    /// Default true: a retrieval failure is logged and the turn continues <b>ungrounded</b>, marked
    /// as such, rather than failing. That is what Agent Framework's <c>TextSearchProvider</c> does,
    /// and the difference here is that the failure also reaches <c>IEdgeDiagnostics</c>. False
    /// rethrows as <see cref="EdgeErrorCode.RagRetrievalFailed"/>.
    /// </summary>
    public bool ContinueOnRetrievalFailure { get; set; } = true;

    /// <summary>Answer "nothing in the sources matched" without calling the model when retrieval returned nothing. Default true.</summary>
    public bool ShortCircuitOnNoContext { get; set; } = true;

    /// <summary>Return false to skip retrieval for a turn - a greeting, a follow-up that needs no sources.</summary>
    public Func<IEnumerable<ChatMessage>, ChatOptions?, bool>? ShouldRetrieve { get; set; }
}

public static class RagPrompts
{
    public const string DefaultContextPrompt;    // "Answer using ONLY the numbered sources below. If they do not contain the answer, say you could not find it. Be concise."
    public const string DefaultCitationsPrompt;  // "Cite every claim with the bracketed number of its source, like [1]. Do not invent source numbers."
    public const string DefaultNoContextAnswer;

    /// <summary>Numbers, clamps, orders and renders the block. Pure - the tier-1 golden-string target.</summary>
    public static string Format(IReadOnlyList<RagSource> sources, RagOptions options);

    /// <summary>
    /// Normalises to best-first without inventing a number: a <see cref="RetrievalScoreKind.Distance"/>
    /// list is ordered ascending, a <see cref="RetrievalScoreKind.Relevance"/> list descending.
    /// Nothing is rescaled, because there is no defensible scale to rescale onto.
    /// </summary>
    public static IReadOnlyList<RagSource> Rank(IReadOnlyList<RagSource> sources);
}

/// <summary>Retrieve, assemble, stream, cite. Ordinary MEAI middleware.</summary>
public sealed class RagChatClient : DelegatingChatClient
{
    public RagChatClient(IChatClient innerClient, IEdgeRetriever retriever,
        RagOptions? options = null, ILoggerFactory? loggerFactory = null);

    public IEdgeRetriever Retriever { get; }
    public RagOptions Options { get; }

    /// <summary>
    /// <c>GetStreamingResponseAsync(...).ToChatResponseAsync(ct)</c>, plus exactly one fix-up:
    /// the citation list is moved from the streaming carrier - an empty <c>TextContent</c> on the
    /// final update - onto the aggregated assistant message's first <c>TextContent</c>, whose
    /// <c>Text</c> is the same concatenation the spans already index, and the emptied carrier is
    /// dropped. The offsets are not recomputed because the string does not change. The fix-up
    /// exists because MEAI's aggregation may coalesce adjacent <c>TextContent</c>s, so the content
    /// holding the full text is the only one whose identity survives it. Section 11 step 8.
    /// </summary>
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <c>this</c>, <see cref="IEdgeRetriever"/> and <see cref="RagOptions"/>, then
    /// delegates inward - so <c>GetService&lt;ChatClientMetadata&gt;()</c> still reaches the leaf
    /// through the stack, which is what keeps Semantic Kernel and Agent Framework working over a
    /// RAG pipeline.
    /// </summary>
    public override object? GetService(Type serviceType, object? serviceKey = null);
}

public static class RagChatClientBuilderExtensions
{
    /// <summary>Null <paramref name="retriever"/> resolves <see cref="IEdgeRetriever"/> as REQUIRED from DI, matching <c>UseDistributedCache</c>'s convention.</summary>
    public static ChatClientBuilder UseRag(this ChatClientBuilder builder,
        IEdgeRetriever? retriever = null, Action<RagOptions>? configure = null);
}

/// <summary>
/// The no-LLM floor: answers verbatim from the highest-ranked sources with their citations
/// attached. Needs no model, no natives and no memory - so the product still answers on a device
/// that refused the budget or never downloaded a model, and the entire RAG path is tier-1 testable.
/// It is an <see cref="IChatClient"/>, so <c>UseRag()</c> over it is the same call, the same
/// pipeline and the same <c>CitationAnnotation</c>s.
/// </summary>
public sealed class ExtractiveChatClient : IChatClient
{
    /// <summary>
    /// Reads its sources from <c>options.AdditionalProperties[RagCitations.SourcesPropertyKey]</c>
    /// - the request-side entry <see cref="RagChatClient"/> sets on a cloned <c>ChatOptions</c>
    /// before invoking its inner client. It does <b>not</b> parse the rendered context block, and
    /// <see cref="RagChatClient"/> does <b>not</b> special-case it: the channel is a documented
    /// public key, so a consumer's own extractive client works identically.
    /// <para>
    /// With no such entry - this client used bare, outside a <c>UseRag()</c> pipeline - it answers
    /// <see cref="ExtractiveChatOptions.NoResultsAnswer"/> with the grounded flag clear. That is
    /// the honest answer: it has no model and was given no sources.
    /// </para>
    /// </summary>
    public ExtractiveChatClient(ExtractiveChatOptions? options = null);
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default);
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default);
    public object? GetService(Type serviceType, object? serviceKey = null);
    public void Dispose();
}

public sealed class ExtractiveChatOptions
{
    public int MaxSources { get; set; } = 3;
    public int MaxCharsPerSource { get; set; } = 600;
    public string NoResultsAnswer { get; set; } = RagPrompts.DefaultNoContextAnswer;
}

public static class RagCitations
{
    /// <summary>
    /// Carries <c>IReadOnlyList&lt;RagSource&gt;</c>, ranked, clamped and <c>Ordinal</c>-stamped.
    /// <para>
    /// <b>It is used on both sides of the call, and that is the specified channel by which an
    /// inner client receives the structured sources.</b> Before invoking the inner client,
    /// <see cref="RagChatClient"/> clones the caller's <c>ChatOptions</c> and sets this key on the
    /// clone's <c>AdditionalProperties</c>; after the turn it sets the same key on the
    /// <c>ChatResponse</c>. An inner client that wants the sources reads the request-side entry
    /// and never re-parses the rendered text block, which exists for the *model* to read.
    /// <see cref="ExtractiveChatClient"/> is the one in-box client that does so.
    /// </para>
    /// <para>
    /// The clone matters: MEAI's <c>IChatClient</c> contract permits an implementation to mutate
    /// the <c>ChatOptions</c> it is handed, so consumers must not share one across concurrent
    /// calls - which means this client must not write into the caller's instance either.
    /// <c>ChatOptions.Clone()</c> is public and shallow-copies <c>AdditionalProperties</c> into a
    /// new dictionary, which is exactly the isolation needed.
    /// </para>
    /// <para>
    /// A leaf that does not know this key ignores it: <c>EdgeChatClient</c> forwards
    /// <c>AdditionalProperties</c> to <c>GeneratorParams.SetSearchOption</c> only for
    /// <c>bool</c> and <c>double</c> values and skips everything else (section 10 e), so a
    /// <c>RagSource</c> list on the request is inert rather than a failure. A third-party leaf
    /// that inspected every property would see a documented, publicly-typed value.
    /// </para>
    /// </summary>
    public const string SourcesPropertyKey  = "qavren.edge.rag.sources";

    /// <summary>Response-side only. Whether the answer was produced from retrieved sources.</summary>
    public const string GroundedPropertyKey = "qavren.edge.rag.grounded";

    /// <summary>
    /// Set to <c>true</c> on the <c>AdditionalProperties</c> of the retrieved-context
    /// <c>ChatMessage</c> this middleware injects, marking it <b>pinned</b>: a history reducer must
    /// never evict it.
    /// <para>
    /// It exists because the injected block breaks the shape a turn-based reducer assumes. The
    /// block is a second <c>ChatRole.User</c> message sitting immediately before the real user
    /// message, so the tail of the list is user-then-user rather than the user/assistant pairs
    /// <see cref="EdgeChatTokenBudgetReducer"/> evicts in (section 6.7) - and evicting the
    /// grounding the whole recipe exists to supply, to make room for history, would be the worst
    /// possible trade.
    /// </para>
    /// <para>
    /// <b>The literal is duplicated in <c>Qavren.Edge.Chat.Onnx</c> on purpose.</b>
    /// <c>Qavren.Edge.Rag</c> does not reference that package and must not, so the two cannot share
    /// a constant; the chat package's <c>ChatHistoryOptions.PinnedMessageKeys</c> defaults to this
    /// exact string and a tier-1 test asserts the two literals are equal. That is the same
    /// deliberate duplication, with the same asserted-equality guard, that sub-project 2 already
    /// uses for <c>EdgeVectorData.QueryGeneratorServiceKey</c> and
    /// <c>EdgeEmbeddings.QueryServiceKey</c>. A reducer that does not know the key simply sees an
    /// ordinary user message - it degrades to the old behaviour rather than breaking.
    /// </para>
    /// </summary>
    public const string ContextMessagePropertyKey = "qavren.edge.rag.context";

    /// <summary>
    /// Finds <c>[n]</c> markers in a completed answer and builds one
    /// <c>CitationAnnotation { Title, Url, FileId, Snippet }</c> per distinct marker, each carrying
    /// a <c>TextSpanAnnotatedRegion(StartIndex, EndIndex)</c> per occurrence. A marker with no
    /// matching source is left as plain text, counted, and <b>never</b> fabricated into a citation.
    /// A total function: it does not throw.
    /// </summary>
    /// <param name="answer">
    /// The <b>complete</b> answer text, and the string every returned <c>StartIndex</c> and
    /// <c>EndIndex</c> indexes. On the streaming path that is the concatenation of every text delta
    /// in the response; after aggregation it is <c>ChatResponse.Text</c>. They are the same string,
    /// which is why one set of offsets serves both (section 11 step 8).
    /// </param>
    public static IReadOnlyList<CitationAnnotation> Build(string answer, IReadOnlyList<RagSource> sources);

    /// <summary>
    /// Attaches <paramref name="citations"/> to <paramref name="update"/> by appending one
    /// <c>TextContent(string.Empty)</c> whose <c>Annotations</c> hold them. The carrier is spelled
    /// out as a public helper because an <c>AIAnnotation</c> hangs off an <c>AIContent</c> and the
    /// final update has no text content of its own to hang them on - and because a consumer
    /// writing their own middleware should attach them the same way.
    /// </summary>
    public static void AttachTo(ChatResponseUpdate update, IReadOnlyList<CitationAnnotation> citations);
}

/// <summary>960-999 is the RAG range, published from the package that logs it.</summary>
public static class EdgeRagEventIds
{
    public const int Retrieved          = 960;
    public const int ContextAssembled   = 961;
    public const int RetrievalFailed    = 962;
    public const int NoContext          = 963;
    public const int CitationsAttached  = 964;
    public const int CitationUnresolved = 965;
    public const int ExtractiveAnswer   = 966;
}

public static class EdgeRagBuilderExtensions
{
    /// <summary>Registers <see cref="RagOptions"/> and the diagnostics contributor. Registers no retriever and no chat client - both are the consumer's choice, which is what makes this package work with either.</summary>
    public static EdgeBuilder AddEdgeRag(this EdgeBuilder builder, Action<RagOptions>? configure = null);

    public static EdgeBuilder AddRetriever<TRetriever>(this EdgeBuilder builder) where TRetriever : class, IEdgeRetriever;
    public static EdgeBuilder AddRetriever(this EdgeBuilder builder, Func<IServiceProvider, IEdgeRetriever> factory);

    /// <summary>
    /// Calls <c>AddEdgeRag()</c>, resolves MEVD's abstract <c>VectorStore</c> from DI and takes
    /// <c>GetCollection&lt;TKey,TRecord&gt;(collectionName)</c>.
    /// </summary>
    /// <param name="storeName">
    /// Null resolves the unkeyed <c>MEVD.VectorStore</c>; a name resolves the keyed one. The
    /// parameter exists because sub-project 2 registers both - <c>AddVectorStore()</c> uses
    /// <c>AddSingleton&lt;VectorStore&gt;</c> and <c>AddVectorStore(name, …)</c> uses
    /// <c>AddKeyedSingleton&lt;VectorStore&gt;</c> - and its own
    /// <c>AddVectorCollection&lt;TKey,TRecord&gt;</c> carries the same <c>storeName</c>
    /// parameter. Without it an app with a named store would have no first-class path here and
    /// would have to drop to <see cref="AddRetriever(EdgeBuilder, Func{IServiceProvider, IEdgeRetriever})"/>,
    /// which is the wrong shape for the suite's most common wiring.
    /// </param>
    [RequiresDynamicCode("MEVD reflects over TRecord to build a collection model.")]
    [RequiresUnreferencedCode("MEVD reflects over TRecord to build a collection model.")]
    public static EdgeBuilder AddVectorStoreRetriever<TKey, TRecord>(this EdgeBuilder builder,
        string collectionName, Func<TRecord, RagSource> project,
        Action<VectorStoreRetrieverOptions<TRecord>>? configure = null,
        Action<RagOptions>? options = null,
        string? storeName = null)
        where TKey : notnull where TRecord : class;

    /// <summary>Registers <see cref="ExtractiveChatClient"/> as the <c>IChatClient</c>, for an app that ships no weights.</summary>
    public static EdgeBuilder AddExtractiveChat(this EdgeBuilder builder,
        Action<ExtractiveChatOptions>? configure = null, Action<ChatClientBuilder>? pipeline = null);
}
```

## 8. Hosting ORT GenAI beside sub-project 2's ORT

### 8.1 One `OrtEnv`, and who creates it

ORT's `OrtEnv` is a **ref-counted process singleton**. `OrtEnv::GetOrCreateInstance`
builds the logging manager from the first caller's `LoggingManagerConstructionInfo`
and then only increments a refcount; a second creator's log id, severity and
logging function are **silently discarded**, with no error returned anywhere.

ORT GenAI creates that singleton **from native code**:
`OrtGlobals::OrtGlobals() : env_{OrtEnv::Create(GetDefaultOrtLoggingLevel())}`,
lazily on the first Oga call, with severity `ERROR` and no logging function. SP2's
guard is managed: `OrtEnv.IsCreated => _instance.IsValueCreated`. So if GenAI goes
first, SP2's order-200 task sees `IsCreated == false`, calls
`CreateInstanceWithOptions`, is handed the pre-existing native singleton whose
options were already fixed, records that it created the environment, and its
`DOrtLoggingFunction` bridge is dead — with nothing logged, no exception, and
SP2's published `ortEnvironmentPreexisting` key reading `false` when the truth is
the opposite.

The fix is one ordering rule, and it is enforced rather than hoped for:

> **No type in `Qavren.Edge.Chat.Onnx` touches any `Microsoft.ML.OnnxRuntimeGenAI`
> type during composition or before startup order 400.**

Concretely: no GenAI type appears in a constructor parameter, a field
initialiser, or a static constructor of anything DI builds eagerly;
`EdgeChatClient` resolves its model lazily on the first call;
`ChatEnvironmentStartupTask` (order 400) is the first code in the assembly to name
`OgaHandle`. A tier-1 test asserts the shape (`AddOnnxChat` on a bare
`ServiceCollection`, build the provider, resolve `IChatClient`, and assert
`OrtEnv.IsCreated` is still false), and the device lanes assert the runtime
outcome (§16.4).

The order-400 task, in order. Steps 1–3 touch no GenAI type at all; step 4 is
the first line of SP4 that names one.

1. Read `IEdgeChatDeviceProfileProvider.Read()` once and cache it.
2. **Sample `OrtEnv.IsCreated` into `ortEnvCreatedBeforeGenAi`.** It is a
   `public static bool` on `Microsoft.ML.OnnxRuntime`, already on SP4's reference
   graph through `Qavren.Edge.Onnx`, and it is sampled here — after order 200,
   before step 4 — precisely because it is the last moment at which the answer is
   still meaningful. `true` is the correct boot: the managed `OrtEnv` singleton
   already exists, which on a composed app is SP2's order-200 task having created
   it, so SP2's log id, severity and `DOrtLoggingFunction` bridge are the ones
   installed. `false` means GenAI is about to create the native singleton first
   and SP2's options are lost. Log 903 at `Warning` when it is false, with a
   message naming the likely cause: a GenAI type touched during composition, or
   `AddOnnx()` not registered.
3. Check the RID and Android ABI against GenAI's actual native payload (§9.2).
   Unsupported → `ChatUnsupportedRuntime` **at startup**, naming the RID or ABI,
   rather than a `DllNotFoundException` on the user's first message.
4. Create the single process-wide `OgaHandle` when `OwnRuntimeHandle`.
5. `Utils.DisableTelemetryEvents()` when `DisableTelemetry` (the default), logging 902.

**Why a public signal and not SP2's own record of the same fact.** SP2 already
knows the better answer: its `OnnxEnvironmentState` distinguishes "we created it"
from "we found it pre-existing", which `OrtEnv.IsCreated` cannot. That type is
`internal sealed` in `Qavren.Edge.Onnx`, and that assembly's `InternalsVisibleTo`
names exactly `Qavren.Edge.Onnx.Tests` and `Qavren.Edge.Embeddings.Tests` —
`Qavren.Edge.Chat.Onnx` is not among them. Adding it would be a third amendment
to a prior sub-project, and §5 does not permit one for a diagnostics nicety.

So SP4 reads the public signal and the two facts are **paired in the report
rather than joined in code**: SP4 publishes `ortEnvCreatedBeforeGenAi` under the
`Qavren.Edge.Chat.Onnx` component, SP2 already publishes
`ortEnvironmentPreexisting` under `Qavren.Edge.Onnx`, and both appear in the same
`EdgeDiagnosticsReport`. Reading them together gives the whole picture, and the
README says how:

| `ortEnvCreatedBeforeGenAi` | SP2's `ortEnvironmentPreexisting` | Meaning |
|---|---|---|
| `true` | `false` | The correct boot. SP2 created `OrtEnv`; its options are live. |
| `true` | `true` | `OrtEnv` existed before SP2 ran — a third library got there first. SP2's options were discarded; SP4's ordering is nonetheless intact. |
| `false` | either | SP4's ordering rule was violated: something touched GenAI before order 400. Event 903. |

`OgaShutdown()` on `Stopping` (§14.2) returns GenAI to a just-loaded state and,
since 0.15.0, a later call transparently re-initialises with a fresh `OrtEnv`.
ORT's own `OrtEnv` is **not** disposed, matching SP2's observer and for its
reason: it is a process-wide singleton with a one-shot options hook.

### 8.2 What SP2's ONNX options do *not* reach

`OnnxOptions.ShareThreadPool` calls `SessionOptions.DisablePerSessionThreads()` on
options built by SP2's `SessionOptionsFactory`. GenAI builds its own session
options **natively**, from the `session_options` block inside
`genai_config.json`. So chat and embeddings do not share a thread pool, and SP2
design §1607's sentence — "the moment SP4's chat model lands beside it, it is one
thread pool on a six-core phone instead of two" — is wrong and SP4 does not
inherit it. §14.3 reports each side's `intra_op_num_threads` separately so the
real position is visible rather than asserted. Correcting that sentence in SP2's
spec is a one-line docs change the plan should make while it is there; it changes
no code, no type and no default, and it is enumerated in §5's table of files
touched outside `chat/` for exactly that reason.

Likewise `OnnxSessionOptions.MemoryHeadroomFactor` (2.5) and
`MemoryHeadroomBytes` (48 MB) are calibrated for a 23–137 MB embedding graph.
Applied to a 1.24 GB bundle they would demand roughly 3.2 GB before the KV cache
is counted at all, and would refuse on every phone. SP4 never goes through
`OnnxSessionHost.PreflightMemory`; §6.4 is its budget and §9.3 is where it runs.

### 8.3 Model folder provisioning, reused unchanged

A GenAI model folder is flat: `genai_config.json`, the decoder `.onnx` named by
`model.decoder.filename`, its `model.onnx.data` sidecar, and the Hugging Face
tokenizer files. SP2's provisioning handles it today with no source change:

- `OnnxModelManifest.Files` is already a list, and its doc already anticipates a
  graph plus external data.
- All three sources (`HttpOnnxModelSource`, `FileOnnxModelSource`,
  `BundledOnnxModelSource`) create nested destination directories.
- `OnnxModelLayout.FilePath` translates forward slashes to the platform separator.
- `IOnnxModelStore.EnsureAsync` takes a manifest **directly**; it does not require
  an `AddOnnxModel` registration. So the chat model is never registered as an ORT
  session, SP2's `InferenceSession`-typed host never sees it, and SP2's
  `PreflightMemory` is never in the path.
- `ProvisionedModel.Directory` is precisely the argument `new Config(string)` and
  `new Model(string)` want.

Two things SP4 adds around it rather than inside it. First, `GraphFile` names the
**decoder `.onnx`**, so `OnnxModelLayout.Sha16` keys `<Models>/<modelId>/<sha16>`
on the weights (§5). Second, `IChatModelProvisioner.ProvisionAsync` pre-checks
free disk against the **bundle total plus `FreeDiskMarginBytes`** before the first
byte moves; SP2's HTTP source checks per file against its own 32 MB margin, which
passes six times for a bundle that will not fit and then fails 900 MB in.

Hugging Face mechanics are SP2's, unchanged: `HuggingFaceRevision` is a full
commit SHA and never `"main"`; large files verify against the git-LFS `oid`
(a SHA-256) and never the `xetHash` those Xet-backed repos also return; small
files carry only a git blob SHA-1 and are hashed after download, which is what
`fetch_chat_model_hashes.py` already does for `vocab.txt` in SP2.

## 9. Model loading

### 9.1 The load path

`ChatModelHost.AcquireAsync` → `EnsureStartedAsync` → `LoadCoreAsync`;
`PreloadAsync` enters `LoadCoreAsync` directly, which is how the order-420 warm-up
task loads without awaiting its own completion. `LoadCoreAsync` runs under a
`SemaphoreSlim(1,1)` load gate, so N concurrent first callers produce one model:

1. **Runtime check.** Already done at order 400; re-asserted here for a host used
   without the startup task (tests).
2. **Provisioning.** `ModelDirectoryOverride` short-circuits. Otherwise
   `IOnnxModelStore.IsProvisioned(manifest)` — marker plus per-file lengths, never
   a re-hash. Absent → `ChatModelNotProvisioned` (7051) carrying
   `Manifest.TotalSizeBytes` and a remediation naming `Plan()`/`ProvisionAsync`.
   **Never a download.**
3. **Shape.** Read `genai_config.json` from the provisioned directory and parse
   `model.type`, `model.context_length`, `model.vocab_size`,
   `model.decoder.{filename, num_hidden_layers, num_key_value_heads, head_size,
   sliding_window}`. Unparseable or incoherent (zero layers, zero head size, a
   missing decoder filename) → `ChatConfigurationInvalid` (7007) naming the file
   and the field. Then cross-check against `preset.Shape` field by field; any
   disagreement → `ChatModelShapeMismatch` (7008) naming the field, the declared
   value and the preset's. That is the guard that turns "somebody repointed the
   preset at a different export" from a wrong memory budget into a message —
   with the single documented exception of `KvCacheBytesPerElement`, which the
   config does not state and the cross-check therefore cannot validate (§6.2).
4. **Budget.** `requested = min(options.MaxContextTokens ?? preset.DefaultMaxContextTokens,
   shape.ContextLength, configDeclaredContextLength)`. Then
   `ChatMemoryBudget.Resolve` against one `IEdgeResourceMonitor.Read()` and the
   cached device profile (§9.3). `RefusedDeviceTooSmall` → 7006;
   `RefusedInsufficientMemory` → 7005 carrying both byte counts and the largest
   context that would have fit; `AllowedReduced` → log 921 with the rung;
   `SkippedUnknown` → log 923 at `Warning` and proceed at `requested`.
5. **Config and model.** `new Config(directory)`, then **exactly one**
   `Overlay(json)` call, then `new Model(config)` and `new Tokenizer(model)`, both
   inside `LoadTimeout` and a CTS linked to the caller's.
   `OnnxRuntimeGenAIException` → `ChatModelLoadFailed` (7002).

   **One call, because `Overlay`'s merge semantics are undocumented and SP4 will
   not bet the memory cap on them.** Nothing in the researched surface says whether
   native `Overlay` deep-merges into the existing config or replaces the named
   section, and the difference is load-bearing twice over: two sequential calls
   compose only if it merges, and a `{"search":{…}}` overlay preserves the model's
   shipped `do_sample` / `temperature` / `top_k` / `top_p` only if it merges
   *within* a section. So SP4 does the merge itself, in managed code, and hands the
   native API a single document. The composition, in order:

   a. Parse the provisioned `genai_config.json`'s own `search` object, if any.
   b. Parse `ConfigOverlayJson` when set, after validating it declares no non-CPU
      provider on a mobile TFM (7009).
   c. Deep-merge (a) under (b), then set `search.max_length` to the budget's
      `resolvedContext` on the result — SP4's value wins, which is the point.
   d. `config.Overlay(<that one document>)`.

   Because (a) is re-emitted, the model's own search defaults survive whichever way
   the native call behaves, and a consumer's `ConfigOverlayJson` is never silently
   wiped by a second call landing on top of it. §19 item 12 confirms the native
   behaviour anyway and records it, because "we made it not matter" is a weaker
   claim than "we know".

   Writing `max_length` into the config at all is the **belt**: on the static KV
   path the cache is allocated to `max_length` (`shape_[2] = max_length;` in
   v0.15.2's `src/models/kv_cache.cpp`), so a model whose config carries the
   budget's answer is capped even on a turn that never reaches
   `GeneratorParams`. The braces are §10(e)'s per-generator value, and §6.6
   explains why the cached-generator case needs the belt most.

   **There is no load-cancellation flag.** ORT's
   `SessionOptions.SetLoadCancellationFlag` has no GenAI equivalent, so a
   multi-second model load cannot be aborted once started. SP4 cancels *before*
   the load and refuses to start one while `LastPressure == Critical`; that is the
   whole mitigation, and it is documented rather than papered over.
6. **Chat-template probe** (§9.5) and, when the policy is not `Disabled`, the
   **guidance probe** (§9.6).
7. Record `ChatModelInfo`, hand out the lease, log 910 with load ms and the
   resolved context.

Drop discipline is SP2's `OnnxSessionHost`, line for line: `UnloadAsync` sets
`DropRequested` and disposes only at zero leases; the release path disposes when
the last lease returns. Disposing a `Model` under a running `Generator` is a
native access violation, and every GenAI wrapper has a finalizer, so
dropped-but-undisposed pins the native graph until GC.

### 9.2 Execution providers, and the RID/ABI allowlist

GenAI's native payload is: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`,
`osx-arm64`, `runtimes/ios/native/onnxruntime-genai.xcframework.zip`, and
`runtimes/android/native/onnxruntime-genai.aar` whose `jni/` holds **arm64-v8a
and x86_64 only**. There is no `osx-x64`, no `win-x86`, and no `armeabi-v7a`.

SP1's SQLite native ships `android-arm`. **An armeabi-v7a device therefore gets
the database and embeddings and has no chat at all**, and the order-400 task says
so by name (7004) rather than letting it surface as a `DllNotFoundException`.
The README states it; the device lane reports the running ABI.

Providers themselves are named inside `genai_config.json` and appended with
`Config.AppendProvider(string)`. SP4 exposes no typed EP policy, only
`ConfigOverlayJson`, because on every platform this suite ships to the correct
answer is CPU and a typed policy would model choices that do not exist. On
Windows the DirectML lane dead-ends at GenAI 0.14.1 against ORT-DML 1.23.0; QNN
packaging was removed in 0.14.0 and its registration entry point
(`OgaRegisterExecutionProviderLibrary`, which does exist in `ort_genai_c.h` at
v0.15.2) has **no C# binding**. All of that is recorded in ADR 0009 so nobody
re-opens it from a docs page.

### 9.3 The memory budget, in full

```
weights        = shape.WeightsBytes                        // Graph + GraphExternalData only
kv(ctx)        = layers × 2 × kvHeads × headSize × min(ctx, slidingWindow) × bytesPerElement
arithmetic(ctx)= weights + kv(ctx) + workspace + reserve
required(ctx)  = PreferMeasuredPeak && MeasuredPeakBytes is { } m
                 ? max(arithmetic(ctx), m + reserve)
                 : arithmetic(ctx)

available      = monitor.Read().AvailableMemoryBytes
usable         = profile.AvailableMemoryKind == SystemWide
                 ? (long)(available × SystemWideMemoryFraction)
                 : available
```

Rules, in the order they apply:

- `profile.TotalMemorySource` is `AndroidActivityManager` or `ApplePhysicalMemory`,
  `profile.TotalMemoryBytes` is known, `MinTotalMemoryBytes(shape)` is non-null,
  and the total is below it → `RefusedDeviceTooSmall`. A nominal-4 GB phone that
  transiently reports 2.5 GiB free passes an availability check and is still
  killed; total memory is the gate that catches it, it is a function of the
  preset's weight class so the 461 MB-decimal model is not refused on a device that
  can run it, and it is **skipped entirely** for `GcMemoryInfo` — a container limit
  is not a device class. Below 200 MiB of weights there is no floor at all, which
  is what lets the tier-2 fixture run on an emulator that reports ~2 GiB (§6.4).
- `profile.IsLowRamDevice == true` → `RefusedDeviceTooSmall`. A hard refusal
  only; it is documented as "1GB or less of RAM" and is false on every phone that
  is still far too small, so it is useless as the gate.
- `available is not > 0` → `SkippedUnknown` unless `RefuseWhenUnknown`. SP2's
  rule, kept verbatim, and Apple's own `os_proc_available_memory()` contract:
  a reading nobody can make is never a refusal.
- `MaxContextTokens` explicitly set → evaluate that one context. `required > usable`
  → `RefusedInsufficientMemory`, carrying the largest ladder rung that would have
  fit. Setting the value turns the budget from a cap into a named refusal, which
  is the point of setting it.
- Otherwise walk `ContextLadder` from the first rung at or below `requested`.
  First fit → `Allowed` (or `AllowedReduced` below `requested`). Nothing at or
  above `MinContextTokens` fits → `RefusedInsufficientMemory`.

The two shipped presets, worked through at 4096 tokens, are the calibration and
the warning:

| Preset | Weights | KV/token | KV @4096 | Arithmetic | Measured |
|---|---|---|---|---|---|
| `Llama32_1BInstructInt4` | 1167 MiB | 32 KiB | 128 MiB | 1295 MiB footprint; 1679 MiB `required` @4096 | **1280 MiB** (vivo X300) |
| `Qwen3_600MInt4` | 461 MB decimal on disk | several times Llama's | dominates the total | weights-plus-KV, KV-bound | 838 MB decimal (Graviton — **recorded, not in the budget**) |

The first row is why `KvCacheBytesPerElement` defaults to 2 and why the formula
is trusted at all. The second is why "pick the smaller file" is not a heuristic
this design offers: a model a third the size on disk can want more resident
memory at a long context, and the budget is the only thing that notices.

**What the ladder can actually rescue, stated plainly, because the name oversells
it.** The ladder is a **KV lever and nothing else**, and KV is a minority of a
weight-dominated preset's footprint. Walked end to end for
`Llama32_1BInstructInt4` — 1167 MiB of weights, 32 KiB/token, 192 MiB workspace,
192 MiB reserve:

| Rung | KV | `required` |
|---|---|---|
| 4096 | 128 MiB | 1679 MiB |
| 3072 | 96 MiB | 1647 MiB |
| 2048 | 64 MiB | 1615 MiB |
| 1536 | 48 MiB | 1599 MiB |
| 1024 | 32 MiB | 1583 MiB |

The whole descent buys **96 MiB, about 6%**. So a device short by more than that
is refused at every rung and gets `RefusedInsufficientMemory`, not a degraded
context — `AllowedReduced` is a real outcome for this preset only in a narrow
~96 MiB band. That is not a defect in the ladder; it is what a 1167 MiB weight
floor means, and the honest consequence is that **for a weight-dominated preset
the lever is a smaller preset, not a shorter context**, which is exactly what the
7005 remediation names first. The ladder earns its keep on the other shape: a
KV-heavy preset like `Qwen3_600MInt4`, where KV dominates the total and halving
the context halves most of the footprint, and on any consumer who raises
`MaxContextTokens` past the shipped default.

The same arithmetic is why `PreferMeasuredPeak` is inert for both shipped presets
today: Llama's measured 1280 MiB plus a 192 MiB reserve is 1472 MiB, below the
1679 MiB arithmetic at 4096, and Qwen has no device measurement at all (§6.5). The
measured term is live machinery for the case it was built for — a preset whose
arithmetic under-reports, which is Gemma-3-1b's 929 MiB computed against 1502 MiB
measured — and §19 item 3 is what would make it bind here.

Every constant here — `WorkspaceBytes`, `ReserveBytes`,
`SystemWideMemoryFraction`, the ladder, and the two `MinTotalMemoryBytes` floors —
is an engineering estimate, and the refusal message says so in the same words
SP2's `PreflightMemory` already uses. §19 items 3, 4 and 10 are the measurements
that replace them.

### 9.4 Platform native wiring

**iOS** ships a zipped **xcframework**, statically linked. The package's own
`buildTransitive/net9.0-ios15.4` targets add
`<NativeReference Kind="Static" IsCxx="True" SmartLink="True" ForceLoad="True"
LinkerFlags="-lc++" WeakFrameworks="CoreML">`, under
`Condition="('$(OutputType)'!='Library' OR '$(IsAppExtension)'=='True')"`. That
condition is evaluated in the **consuming app**, so `Qavren.Edge.Chat.Onnx`
correctly contributes nothing and the app gets the link; `NativeMethods.cs` binds
`DllName = "__Internal"` to match. This is the recipe the prior art hand-rolled
for llama.cpp, now productised — SP4 hand-writes no targets file.

**Mac Catalyst** gets `build/net9.0-maccatalyst14.0/_._` and
`buildTransitive/net9.0-maccatalyst14.0/_._` on purpose, and relies on the .NET
SDK's `PortableRuntimeIdentifierGraph.json` falling back to
`runtimes/ios/native/`, whose xcframework does list an
`ios-arm64_x86_64-maccatalyst` slice. **That is the upstream README's assertion,
not a proven build**, and its failure mode is a runtime `DllNotFoundException`
rather than a build error. §19 item 1 is the smoke that decides it, and the
documented response if it fails is to ship three TFMs and say so in the README.

**Android** ships an AAR whose targets add
`<AndroidLibrary Bind="false" Include="runtimes/android/native/*">` under
`Condition="'$(AndroidApplication)'=='true'"` — so the natives land in an app
project, not a library, reached transitively. 16 KB page alignment is already
satisfied (`PT_LOAD p_align = 0x4000` on all three LOAD segments of the arm64
`.so`). The AAR also merges `INTERNET`, `ACCESS_NETWORK_STATE` and an
`ai.onnxruntime.genai.TelemetryInitializer` content provider into every consuming
APK, and carries a ~32 MB `libmat.so` per ABI. All three are consumer-visible
consequences of adopting chat; all three are in the package README and §16.4
asserts the manifest merge so they are documented rather than discovered.

**Windows** binds `lib/net8.0` plus `runtimes/win-x64` or `win-arm64` by RID.
Fine for the library, which has no Windows TFM; a **packaged MAUI WinUI** app is
a different resolution path and is part of §19 item 1's smoke.

### 9.5 The chat template

Prompt formatting goes through the model's own Jinja `chat_template`, rendered by
**minja** inside onnxruntime-extensions:
`tokenizer.ApplyChatTemplate(template_str: null, messages: <json>, tools: null,
add_generation_prompt: true)`. SP4 never hand-formats a Phi, Llama or Qwen
prompt.

The failure mode is the reason there is a probe. `TokenizerImpl::LoadChatTemplate`
returns `kOrtxOK` with only a **warning** — "The chat template for this model is
not yet supported, trying to apply chat template will cause an error" — when minja
cannot parse the template. So the failure surfaces on the first real
`ApplyChatTemplate` call, in the middle of a user's first message, and not at
load. The order-420 task (and the loader, when warm-up is off) calls it once with
a two-message probe and records `chatTemplateSupported`. Failure →
`ChatTemplateUnsupported` (7101) when `RequireChatTemplate`, otherwise log 951 and
switch to a `role: text` fallback formatter whose output quality is materially
worse and which the diagnostics block names.

`EdgeChatOptions.PromptFormatter` replaces the whole step and is the documented
escape hatch. `ChatOptions.Instructions` becomes a synthetic leading system
message, matching upstream. Non-`TextContent` in a message is a refusal, not a
silent drop (§15) — upstream's default formatter reads only `message.Text` and
discards the rest.

### 9.6 The guidance probe

Runs at most once per `Model`: at warm-up when the policy is not `Disabled`,
otherwise lazily on the first turn that asks for guidance. It generates at most
eight tokens under
`SetGuidance("json_schema", "{\"type\":\"string\",\"const\":\"qedge\"}")` and
checks the decoded text.

It asserts **output shape**, never absence of an exception, and that is the whole
design. At v0.15.2 `CreateGuidanceLogitsProcessor` on a `USE_GUIDANCE=OFF` build
returns `nullptr` after an optional log line and **does not throw**; an
exception-based probe would report success on a build that enforces nothing.

Outcomes map exactly: the probe threw → `ProbeFailed`; it ran and matched →
`Enforced`; it ran and did not match → `NotEnforced`. The diagnostics key
`guidanceEnforced` publishes the value and the README states that `NotEnforced`
means *not proven enforced*, not *proven absent* — a guidance-enabled build could
also fail the probe with an unusual model or template. `RequireNative` +
`NotEnforced` → `ChatGuidanceUnavailable` (7103) with a remediation naming the
shipped mobile natives' build settings.

## 10. Generating a turn

`GetResponseAsync` is literally
`GetStreamingResponseAsync(messages, options, ct).ToChatResponseAsync(ct)`.
Two independent implementations is how streaming and non-streaming paths drift,
so there is one.

`GetStreamingResponseAsync` is an `async IAsyncEnumerable` with
`[EnumeratorCancellation]`:

**a. Refuse early, by name.** `ChatOptions.Tools is { Count: > 0 }` → 7107.
`ToolMode` is `RequireAny` or `RequireSpecific` → 7107 as well: a required tool
call that can never be emitted is a hard failure, not a footnote.
`ResponseFormat is ChatResponseFormatJson` with policy `Disabled` → 7103 — and
**only** that case. `ChatResponseFormat.Text` is a non-null value that asks for
plain text, needs no constrained decoding and is already what this client
produces, so it is honoured by being ignored; a null `ResponseFormat` is
likewise untouched. Refusing a caller for requesting the default behaviour would
be a bug, and upstream's own client draws the boundary in the same place — it
maps only `ChatResponseFormatJson` onto `SetGuidance`. Any `ChatMessage`
content that is not `TextContent` → 7108 naming the content type. Options this
client cannot honour but that are harmless to ignore — `FrequencyPenalty` (GenAI
has one `repetition_penalty` knob for MEAI's two, and `PresencePenalty` wins),
`AllowMultipleToolCalls`, `ContinuationToken`, `AllowBackgroundResponses` — are
listed in `AdditionalProperties` under
`EdgeChatProperties.UnhonouredOptions` on the response. Silently ignoring an
option is the failure mode this design refuses elsewhere; it is not going to
practise it here.

**b. Gate.** `host.IsAcceptingTurns` false → `ChatBusy` (7105). Low-power mode
with `RefuseNewTurnsInLowPowerMode` → 7105. Thermal at or above
`ThermalOptions.AbortAt` → `ChatThermalAbort` (7106) before anything is
allocated. Queue depth at `MaxQueuedTurns` → 7105, logged as 933. Otherwise
`AcquireAsync` bounded by `TurnQueueTimeout`; a wait logs 932, a timeout is 7105.

**c. Reduce.** `EdgeChatTokenBudgetReducer` over the message list, counting with
the model's own tokenizer (`tokenizer.Encode(text)[0].Length`). Budget =
`resolvedContext − maxOutput − ReservedPromptTokens`. System messages and
**pinned** messages — any carrying a key from `ChatHistoryOptions.PinnedMessageKeys`,
which is how the RAG recipe's injected context survives (§6.7, §11 step 6) — are
never evicted; everything else is evicted by whole eviction group, oldest first.
Log 935 with the count dropped and carry it into `ChatTurnStatus.MessagesDropped`.

**d. Format and encode.** The template formatter (§9.5). Then
`tokenizer.Encode(prompt)`; `promptTokens = sequences[0].Length`. If the reducer's
floor — system messages, pinned messages, and the newest
`MinimumPreservedMessages` messages — still does not fit, the turn is
`ChatPromptTooLong` (7102) with `PromptTokens` and the resolved context. The usual
culprit is a RAG block, and the remediation names `RagOptions.MaxContextTokens`
first and `MaxOutputTokens` second — because the block is pinned, the alternative
to failing here would have been discarding the grounding, and a cited answer that
quietly stopped being grounded is the worst outcome available.

**e. Params.** Each knob is resolved by §6.6's precedence rule — per-call
`ChatOptions`, then `EdgeChatOptions`, then the `ChatPreset` default — and then
mapped.

`max_length` has **two cases, because a search option cannot be changed after the
generator is constructed** (§6.6):

- **Fresh generator** — caching off, a cache miss, or the first turn of a
  conversation: `max_length = min(promptTokens + maxOutput, resolvedContext)`.
- **Cached generator** — a hit: the value is whatever it was built with, and it
  was built with `max_length = resolvedContext`. Nothing re-sets it, because
  nothing can.

In both cases the **memory** cap is `≤ resolvedContext`, which is the budget's
answer and the only thing jetsam cares about; and in both cases the **output** cap
is enforced by a managed counter in the decode loop, which breaks at `maxOutput`
and reports `FinishReason.Length`. Upstream's bug is the absence of the first, not
staleness in the second: with caching on it sets no `max_length` at all and KV is
allocated against the model's declared `context_length`.

Then `temperature`, `top_p`, `top_k`
(either also forces `do_sample = true`), `random_seed` from `Seed`,
`repetition_penalty` from `PresencePenalty`. Stop sequences are the ordinal
union of all three layers, longest-first (§6.6).

`ChatOptions.AdditionalProperties` entries are forwarded as raw search options
**only when the value is a `bool` or a `double`**; every other value is skipped
without error. That is a contract, not an accident: it is what lets
`Qavren.Edge.Rag` carry a structured `IReadOnlyList<RagSource>` on the same
dictionary under `RagCitations.SourcesPropertyKey` (§11) and have this leaf
ignore it rather than fail on it.

`EdgeChatOptions.SearchOptions` is applied last and wins — except `max_length`,
which is refused with 7108.

**f. Decode.** The generator is resolved first, and the rule is written out
because "reuse the cached one when the id matches" leaves three questions the
implementation cannot guess at.

*Which generator.* With `EnableConversationCache` on and a **non-null**
`options.ConversationId` that is ordinally equal to the cached one, the cached
generator is a **candidate** — subject to the two checks below. A null id is never
a hit (§6.6): it builds fresh and mints an id. A different id is a miss: dispose,
build fresh, keep the caller's id. Every update of the response carries the
effective id in `ChatResponseUpdate.ConversationId`, so the aggregated
`ChatResponse.ConversationId` is what a caller echoes back next turn.

*Check one — does the cached prefix still hold?* Beside the cached generator SP4
keeps `cachedText`: the exact character sequence that generator has consumed and
produced, i.e. the rendered prompt of the previous turn plus the decoded text it
generated. This turn's full render (§10 d) is `fullText`. If
`fullText.StartsWith(cachedText, StringComparison.Ordinal)` the prefix holds and
the **delta** is `fullText[cachedText.Length..]`: only that is encoded, and it goes
in with `generator.AppendTokens`. If it does not hold, it is a **miss** — dispose
and rebuild from the full render.

That one string test carries three invariants for free, which is why it is the
test rather than a bookkeeping scheme. A history reduction that evicted a message
the generator already consumed cannot produce a `fullText` that starts with
`cachedText`, so it rebuilds automatically. A caller who edited or reordered
earlier history rebuilds. And a chat template that is not append-only across turns
— one that re-emits a preamble, say — rebuilds rather than silently duplicating
the conversation, which is what re-encoding the whole prompt into a generator that
already held it would do.

The seam is a stated limit: the delta is tokenised on its own, so a BPE merge that
would have spanned the join is lost. In practice the join falls immediately after
a special token (`<|eot_id|>` and friends are atomic), which is why this is
tolerable rather than ignored — §19 item 11 is the check, and
`EnableConversationCache = false` is the one-line fallback if it proves lossy on a
preset.

*Check two — does the turn still fit?* On a hit, `generator.TokenCount() +
maxOutput > resolvedContext` means the cached generator cannot hold this turn.
That is a **miss, not a 7102**: dispose and rebuild from the reduced list, which
step (c) has already trimmed to fit. The error is for a prompt that does not fit a
*fresh* generator.

*What gets reported.* `ChatTurnStatus.PromptTokens` and
`UsageDetails.InputTokenCount` are the **total** sequence length the model
conditioned on — `generator.TokenCount()` after the append —
and `ChatTurnStatus.PromptTokensAppended` is what was actually encoded this turn,
which equals `PromptTokens` on a fresh generator and is the delta on a hit. Both
are published, because "fewer prompt tokens on the second turn" is true of one of
them and false of the other, and a spec that says only "fewer" has specified
neither. Log 930 on a hit with both counts.

Production runs on
a single `Task.Run` writing into a **bounded `Channel<ChatResponseUpdate>`**
(capacity 64, `FullMode.Wait`); the iterator reads it. One thread hop per turn,
real backpressure, and the caller's UI thread never enters native code. Upstream
instead awaits a custom `YieldAwaiter` that does a `Task.Run` **per token** —
correct, but allocation-heavy and with no backpressure.

The decode loop is the **producer body**, and it is inside the `Task.Run` — it
writes to the channel and never yields, because a `yield return` here would put
`GenerateNextToken()` back on the consumer's thread and undo the whole
arrangement:

```
// ---- producer: runs on Task.Run, writes the channel, never yields ----
var writer = channel.Writer;
try
{
    ttft.Start();
    while (!generator.IsDone())
    {
        ct.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _terminate)) { stop = MemoryPressure | Suspended; break; }

        if (now - lastSample >= Thermal.SampleInterval)      // the monitor caches for 1 s anyway
        {
            var s = resources.Read();
            // Unknown is 0, BELOW Nominal, so `>=` alone would read "nothing usable" as
            // "fine". The guard is explicit, not implied by the enum's numbering - §6.6.
            var known = s.Thermal != EdgeThermalState.Unknown;
            if (known && s.Thermal >= Thermal.AbortAt)
            { generator.SetRuntimeOption("terminate_session", "1"); log 937; stop = Thermal; break; }
            throttled = (known && s.Thermal >= Thermal.ThrottleAt)
                     || s.ThermalHeadroom >= Thermal.ThrottleHeadroom;   // NaN >= x is false
            if (throttled) log 936 once per turn;
            lastSample = now;
        }
        if (throttled) await PaceAsync(Thermal.ThrottledTokensPerSecond, ct);

        generator.GenerateNextToken();                       // blocking native call, off the caller's thread
        generated++;
        var text = stream.Decode(generator.GetSequence(0)[^1]);
        if (first) { timeToFirstToken = ttft.Elapsed; first = false; }

        // the per-turn OUTPUT cap. max_length is the KV MEMORY cap and, on a cached
        // generator, cannot be changed after construction - see step (e).
        if (generated >= maxOutput)
        { await writer.WriteAsync(Update(text), ct); stop = MaxOutputTokens; finish = Length; break; }

        tail.Append(text);              // rolling buffer, capacity = longest stop sequence
        if (MatchesStopSequence(tail, out var trimmed))
        {
            if (trimmed.Length > 0) await writer.WriteAsync(Update(trimmed), ct);
            stop = StopSequence;
            break;
        }

        await writer.WriteAsync(Update(text), ct);           // FullMode.Wait: real backpressure
    }
    await writer.WriteAsync(FinalUpdate(stop, usage, status), ct);   // step (g)
}
finally { writer.TryComplete(); }                            // always, on success, fault and cancel

ChatResponseUpdate Update(string text) => new(ChatRole.Assistant, text)
{
    MessageId = messageId, ResponseId = responseId,
    ConversationId = options?.ConversationId, ModelId = presetId,
};

// ---- consumer: the [EnumeratorCancellation] iterator the caller awaits ----
try
{
    await foreach (var update in channel.Reader.ReadAllAsync(ct))
        yield return update;
}
finally { await producer; }                                  // observe faults; never unobserved
```

Three details in that loop are the whole reason SP4 wrote it rather than taking
upstream's. Stop sequences are matched over a **rolling decoded-character
buffer** sized to the longest configured stop string, so a multi-token stop
(`<|eot_id|>` arriving as three fragments) actually fires. Thermal is sampled
**mid-decode**, at the monitor's existing one-second cache boundary so it costs
nothing, because a 400-token answer at 30 tok/s is thirteen seconds of sustained
CPU and a start-of-turn check would not notice the device crossing `Serious`
until the *next* turn. And throttling **paces** rather than stops: a visibly slow
answer beats a dead one.

**g. Final update.** Empty text, `UsageContent(new UsageDetails { InputTokenCount,
OutputTokenCount, TotalTokenCount })`, `FinishReason` = `Length` when either the
managed output counter or `max_length` bound and `Stop` otherwise, and
`AdditionalProperties[EdgeChatProperties.TurnStatus] = ChatTurnStatus`. The
`ChatFinishReason` is for the ecosystem; the `EdgeChatStopReason` inside the
status is for the truth, because MEAI's enum cannot say "the OS suspended us".
Emitting a trailing metadata-only update with the same `MessageId` is what makes
`ToChatResponseAsync` aggregate into one message identically to the
non-streaming path.

**h. `finally`.** Both `finally` blocks above are the contract, not an
illustration: the producer always calls `writer.TryComplete()`, and the
iterator's `finally` awaits the producer task, so a cancelled or faulted turn is
never an unobserved exception. Dispose the generator (unless cached) and the
token stream, release the lease, decrement the queue counter, fold the turn into
`ChatClientStatistics` — tokens/sec into a bounded 64-sample ring for P50/P95,
`PeakWorkingSetBytes = max(peak, Environment.WorkingSet)`.

**Cancellation** is registered to call
`generator.SetRuntimeOption("terminate_session", "1")`, so a cancel landing
mid-`GenerateNextToken` aborts inside native code rather than after it. Prefill
(`Encode`, `AppendTokenSequences`) is bracketed by explicit checks on both sides;
it is not itself interruptible, which is stated rather than hidden.

## 11. The RAG recipe

`RagChatClient.GetStreamingResponseAsync`:

1. `ShouldRetrieve?.Invoke(...)` false → straight passthrough to the inner client.
2. Query from `QuerySource`; keywords from `KeywordExtractor` (default: split on
   non-letters, drop a small stop list, keep three-plus characters, cap at eight).
3. `retriever.RetrieveAsync(query, new RetrievalRequest { Top, Keywords }, ct)`.
   A throw is caught and logged 962; `ContinueOnRetrievalFailure` (default) marks
   the turn ungrounded and continues with no block, otherwise it is
   `RagRetrievalFailed` (7202). A failed search degrades to an ungrounded answer,
   never a thrown request — and unlike Agent Framework's `TextSearchProvider`,
   which swallows it to an `ILogger`, the failure also reaches `IEdgeDiagnostics`.
4. `RagPrompts.Rank` orders best-first by polarity — a `Distance` list ascending,
   a `Relevance` list descending, nothing rescaled — reading the `ScoreKind` the
   **retriever** stamped, which is why a projector never sets it (§7). Then
   assign `Ordinal` 1..n,
   clamp each `Text` to `MaxCharsPerSource` with a visible `…(truncated)`, and
   drop from the tail until the block fits `MaxContextTokens` measured with
   `TokenCounter`.
5. Empty, with `ShortCircuitOnNoContext` → yield `DefaultNoContextAnswer` as a
   single update with `FinishReason.Stop` and return. No model call, no tokens
   spent, log 963.
6. **Two carriers, one list.** The ranked, clamped, `Ordinal`-stamped sources go
   to the inner client **twice**, in two different shapes for two different
   readers, and this is the mechanism §7's `ExtractiveChatClient` depends on:

   - *For the model*, `RagPrompts.Format` renders one text block, injected as a
     single `ChatMessage(ContextRole /* User */, block)` immediately before the
     newest user message, with
     `AdditionalProperties[RagCitations.ContextMessagePropertyKey] = true`. A
     language model reads prose.

     That property is the **pin**, and it is not decoration. The injected block is
     a second user message in front of the real one, so the tail of the list stops
     being the user/assistant pairs a turn-based reducer evicts in — and the chat
     package's reducer would otherwise treat the grounding as the oldest evictable
     thing in the newest group. `ChatHistoryOptions.PinnedMessageKeys` defaults to
     this exact literal (§6.7); the two constants are duplicated across packages
     rather than shared, because `Qavren.Edge.Rag` does not reference
     `Qavren.Edge.Chat.Onnx`, and a tier-1 test asserts they are equal — the same
     pattern and the same guard SP2 already uses for its two query-generator keys.
     A reducer that has never heard of the key sees an ordinary user message and
     behaves as it did before, which is the right failure mode for a cross-package
     convention.
   - *For the inner client itself*, `RagChatClient` calls
     `options?.Clone() ?? new ChatOptions()` and sets
     `AdditionalProperties[RagCitations.SourcesPropertyKey]` on the **clone** to
     the `IReadOnlyList<RagSource>`, then passes that clone inward. The caller's
     own `ChatOptions` instance is never mutated — MEAI's contract permits an
     implementation to mutate what it is handed, which is exactly why this one
     does not — and `ChatOptions.Clone()` shallow-copies `AdditionalProperties`
     into a fresh dictionary, so the isolation is free.

   An inner client that does not know the key ignores it: `EdgeChatClient`
   forwards only `bool` and `double` `AdditionalProperties` values to
   `SetSearchOption` and skips everything else (§10 e), so the list is inert on
   the model path. An inner client that does know it — `ExtractiveChatClient`,
   or a consumer's own — reads the structured list and never re-parses the
   rendered block. `RagChatClient` does **not** type-test its inner client and
   has no special case for `ExtractiveChatClient`; the channel is a documented
   public key, which is what keeps the no-LLM floor reproducible outside this
   package.

   The rendered block:

```
## Additional context
Answer using ONLY the numbered sources below. If they do not contain the answer, say you could not find it. Be concise.

[1] Title: Warranty terms
    Source: https://…
    ---
    <clamped chunk text>

[2] …

Cite every claim with the bracketed number of its source, like [1]. Do not invent source numbers.
```

7. Stream the inner client through unchanged, forwarding every update verbatim
   while accumulating the full text.
8. On completion, `RagCitations.Build` scans `\[(\d+)\]` over the accumulated
   text and emits one `CitationAnnotation { Title, Url, FileId = source.Id,
   Snippet }` per distinct marker, each with a
   `TextSpanAnnotatedRegion(StartIndex, EndIndex)` per occurrence. An ordinal with
   no source is left as plain text, counted, logged 965, and never fabricated into
   a citation; citation resolution is a total function.

   **What carries them, and what the offsets index.** An `AIAnnotation` hangs off
   an `AIContent`, not off an update, so the final update needs a content item to
   hang them on and this spec names it rather than leaving it to be guessed:
   `RagChatClient` appends **one `TextContent(string.Empty)`** to the final
   update's `Contents`, and that content's `Annotations` carry the whole list. It
   is an empty text content on purpose — it contributes nothing to the answer,
   `ChatResponse.Text` is unchanged by it, and any consumer that concatenates
   `update.Text` sees no difference. It sits alongside the leaf's own
   `UsageContent` on the same update (§10 g); neither knows about the other.

   The offsets index the **concatenation of every text delta already yielded in
   this response** — equivalently `ChatResponse.Text` after aggregation, since the
   carrier adds an empty string. They do **not** index any single update, and no
   single update holds the text they refer to. A streaming consumer therefore
   accumulates as it goes and indexes its own buffer; §4.3's snippet is that, and
   is written that way deliberately.

   **Non-streaming.** `RagChatClient.GetResponseAsync` is
   `GetStreamingResponseAsync(messages, options, ct).ToChatResponseAsync(ct)` —
   the same rule `EdgeChatClient` follows, and for the same reason: two
   implementations is how the paths drift. It then does exactly one thing after
   aggregation, and only this: it moves the annotation list from the carrier onto
   the aggregated assistant message's **first `TextContent`**, whose `Text` is that
   same concatenation, and drops the now-empty carrier. That is a fix-up rather
   than a re-computation — the offsets do not move, because the string does not —
   and it exists because MEAI's aggregation is free to coalesce adjacent
   `TextContent`s, so the only content item whose identity survives aggregation is
   the one holding the full text. A consumer of `ChatResponse` therefore finds the
   citations on the content whose text the spans actually index, which is the only
   place they mean anything.
9. `AttachSourcesToResponse` puts the source list under
   `RagCitations.SourcesPropertyKey` and the grounded flag under
   `GroundedPropertyKey`.

`VectorStoreRetriever` is the only type in the package that knows MEVD exists.
It takes the hybrid lane through `IKeywordHybridSearchable<TRecord>.HybridSearchAsync`
when the collection implements it and keywords are present, the vector lane
through `SearchAsync` otherwise, and **stamps `ScoreKind` to match** — the two
return opposite polarities and mixing them silently inverts every ranking.
`ScoreThreshold` is applied with the correct polarity for the lane actually
taken. `Top + Skip > MaxCandidates` (4096, vec0's `SQLITE_VEC_VEC0_K_MAX`) is
refused here, naming the limit, rather than surfacing SP2's `KnnLimitExceeded`
from three frames down.

Query-side embedding is not SP4's business: the retriever passes the question
**string** to the collection and SP2's store resolves its own
`"qavren.edge.query"`-keyed sibling generator internally, so the asymmetric
query/document prefix convention keeps working with nothing duplicated here.

`ExtractiveChatClient` reads
`options.AdditionalProperties[RagCitations.SourcesPropertyKey]` — the request-side
entry step 6 set on the cloned options — and answers from the top `MaxSources` of
them, each clamped to `MaxCharsPerSource`, with synthesised `[n]` markers,
`FinishReason.Stop`, and the grounded flag set. No sources on the request, or an
empty list, gives `NoResultsAnswer` with the flag clear. It never inspects the
rendered text block, so a consumer who replaces `ContextFormatter` does not
thereby break the floor.

Note that steps 7–9 then run over its output **unchanged**: the `[n]` markers it
synthesised are resolved into real `CitationAnnotation`s by the same
`RagCitations.Build` call that resolves a model's, because to the middleware they
are the same markers over the same source list. That is what makes
`UseRag()` over `ExtractiveChatClient` the same pipeline rather than a parallel
one, and it is the single most valuable idea carried over from the Foreman prior
art: **the product still answers when the model is absent, and the answer says
so.**

## 12. What sub-project 4 costs an app

### 12.1 The model never ships in the package

The default preset is 1.24 GB and the small one is 461 MB. Neither can ride in an
app package, and the ceilings have no workaround inside v1:

- **Google Play.** Base module 500 MB compressed; an individual asset pack
  1.5 GB; all modules plus install-time asset packs 4 GB; 200 MB is the threshold
  above which a user on mobile data sees a non-blocking large-size dialog, not a
  limit. Play Asset Delivery is out of scope (§20), so the base module is the only
  lane an SP4 consumer has by default, and 461 MB of model plus their own app does
  not fit in it.
- **Apple.** 4 GB uncompressed total, 80 MB `__TEXT` executable. The total would
  technically hold the small preset, and Apple's own guidance is to use Background
  Assets instead — also out of scope.

So: provision on first run, into `IEdgeModelPaths.Models`, over HTTP, with
consent. `AddBundledModelSource` remains an embeddings mechanism and SP4
documents that it is not a chat one.

### 12.2 Consent is a parameter, not a README line

App Store Review guideline 4.2.3(ii): "If your app needs to download additional
resources in order to function on initial launch, disclose the size of the
download and prompt users before doing so." `IChatModelProvisioner.Plan()` exists
to feed exactly that sheet — bytes to transfer, bytes already present, free disk,
SPDX identifier and licence URL — and `AcquireAsync` on an unprovisioned model
throws rather than starting the transfer. An app cannot accidentally ship a
silent 1.24 GB first-launch download, because there is no code path that performs
one.

### 12.2.1 What a 1.24 GB transfer actually does on a phone

Consent is necessary and it is not sufficient, so the three things v1 does **not**
do are stated here rather than discovered on a 4G connection.

**The transfer is foreground-only, and it resumes rather than restarting.**
Sub-project 2's `HttpOnnxModelSource` is a plain `HttpClient`. It is not an
`NSURLSession` background transfer and it is not driven by an Android foreground
service or a WorkManager job, so backgrounding the app stalls the transfer and iOS
will suspend the process outright. What survives is the part that matters: every
verified byte range is on disk under the model root with its marker, so the next
foregrounded `ProvisionAsync` continues from where it stopped and never from zero.
An app whose users will realistically background a 1.24 GB download should drive
`ProvisionAsync` from its own foreground service or `WorkManager` job — the method
is cancellation-safe and resumable precisely so that it can be — and SP4 ships no
wrapper for either in v1 (§20).

**Metered networks are a policy hook, not a guess.**
`ChatProvisioningOptions.IsTransferPermitted` is consulted once before the first
byte and refuses with `ChatDownloadNotPermitted` (7053). SP4 does not read
connectivity itself: doing so means a MAUI or per-platform dependency in a package
that suite decision 13 requires to work on a bare `ServiceCollection`. The app
supplies one line, the sample app shows it, and Google Play's own 200 MB
mobile-data warning is the reason the default consent sheet shows bytes.

**Android process death is the app's problem, and the README says so.** A
long-running transfer from a plain activity or a MAUI page can be killed with the
process. The resumability contract above is what makes that survivable rather than
catastrophic, but "survivable" is the claim, not "unattended".

### 12.3 Where the bytes live, and what they are mapped from

`IEdgeModelPaths` is reused unchanged, and both of its platform choices matter
more at this size than they did at 23 MB: Android
`Context.NoBackupFilesDir/qavren-edge/models` (Auto Backup's per-app quota is
25 MB), Apple `Library/Application Support/qavren-edge/models` with
`NSURLIsExcludedFromBackupKey` set after creation. Never `Cache`, which the OS may
purge mid-session and turn into a 1.24 GB re-download.

ORT memory-maps external-data initializers — `env.MapFileIntoMemory` first, a
heap copy only on failure — so an export that keeps weights in a sidecar is
mapped rather than read. Both shipped presets have that layout, and SP4 prefers
it for that reason.

But the mapping is not free, and both kernels kill for abusing it. Apple's jetsam
reason list includes `fc-thrashing`: "A process thrashed the system file cache.
This occurs when non-sequential parts of memory-mapped files are read and written
too frequently." Android's lmkd has been PSI-driven since Android 10 and is tuned
by `ro.lmk.thrashing_limit` on workingset refaults. Random access over a 1.24 GB
mapping is exactly that pattern. So the design **keeps the model resident across
turns** (`UnloadOnSleeping` defaults false) and, when it does release, releases
outright rather than leaving a large mapping resident.

### 12.4 iOS entitlements are a consumer requirement

Two entitlements raise the ceiling and are the only real lever on a nominal-6 GB
phone:
`com.apple.developer.kernel.increased-memory-limit` (iOS 15+, raises the
OS-imposed resident limit) and
`com.apple.developer.kernel.extended-virtual-addressing` (iOS 14+, extends the
process virtual address space — the one that matters for a multi-GB mapping).
They are plist lines in the **app**, not code in a library. SP4 does not write
anyone's entitlements file; the sample app sets both, the README states them as a
requirement, and the 7005 remediation text names them.

### 12.5 The Android payload

Adopting chat roughly doubles what a consumer's APK carries in natives versus
embeddings alone: the AAR is 21.5 MB compressed and each ABI holds
`libonnxruntime-genai.so` (5.2 MB on arm64) plus `libonnxruntime-genai-jni.so`
and a `libmat.so` of about 32 MB whose purpose is documented nowhere upstream and
which there is no known way to exclude. The merged manifest also gains
`INTERNET`, `ACCESS_NETWORK_STATE` and a telemetry content provider (§9.4).
`DisableTelemetry` defaults true, a device test asserts the merge, and the README
states all of it — measured from the device lanes' own artifacts (§19 item 5),
never estimated.

## 13. Data flow

App launch (MAUI), with both SP2 and SP4 registered:

1. `MauiProgram` → `UseQavrenEdge(...)`. **No GenAI type is constructed** (§8.1).
2. Platform launch → `IEdgeHost.Start()`.
3. Startup tasks: native provider install (0) → database open (10) → migrations,
   including vector collection DDL (100) → **`OrtEnv` + ORT log bridge (200)** →
   ONNX model provisioning (210, opt-in) → ONNX session warm-up (220, opt-in) →
   vector schema (300, opt-in) → **`OgaHandle` + telemetry + RID/ABI check (400)**
   → chat model presence (410, opt-in) → chat warm-up + template probe + guidance
   probe (420, opt-in).
4. `Started` completes or faults; diagnostics captures both.

One turn, grounded:

1. `IChatClient.GetStreamingResponseAsync(question)` enters `RagChatClient`.
2. Retrieve → SP2's `EdgeVectorStoreCollection.HybridSearchAsync` → vec0 KNN ∪
   FTS5 bm25 fused by RRF, over `IEdgeDatabase`'s connection.
3. Assemble the numbered block; inject as one user message.
4. `EdgeChatClient` gates, reduces history, applies the chat template, encodes,
   sets `max_length`, decodes token by token into a bounded channel, sampling
   thermal once a second.
5. Each update streams straight through `RagChatClient` to the caller.
6. The final update carries `UsageContent`, a `ChatFinishReason`, a
   `ChatTurnStatus` and the `CitationAnnotation`s.

Memory warning, mid-turn:

1. iOS `DidReceiveMemoryWarning` → SP1's MAUI bridge →
   `IEdgeLifecycle.RaiseMemoryPressureAsync(Critical)`.
2. SP2's observer latches the level and drops ORT sessions.
3. SP4's observer calls `TerminateActiveGeneration()` — the generator aborts
   inside native code — then cancels the turn, drops the conversation cache, and
   unloads the model, all inside a two-second drain budget.
4. The in-flight stream **completes** with `StopReason = MemoryPressure` and
   `FinishReason.Stop`, carrying the text the user already saw. It does not throw:
   an OS memory warning is not a place to throw at a UI thread.
5. The next turn reloads the model lazily.

## 14. Lifecycle and diagnostics

### 14.1 Startup

| Order | Task | Opt-in | Package |
|---|---|---|---|
| 200 | `OnnxEnvironment` — `OrtEnv.CreateInstanceWithOptions` + the `ILogger` bridge | always | Onnx (SP2) |
| 400 | `ChatEnvironment` — device profile, RID/ABI check, `OgaHandle`, telemetry opt-out, ordering record | always | Chat.Onnx |
| 410 | `ChatModelProvisioning` — presence only, never a download | opt-in | Chat.Onnx |
| 420 | `ChatWarmUp` — `PreloadAsync` + chat-template probe + guidance probe | opt-in | Chat.Onnx |

Order 400 is the first code in the process permitted to name a GenAI type, and
that is the whole of §8.1's rule. It never faults startup for an environment
problem — a bad environment surfaces at load with a better message — but it does
fault for an unsupported RID or ABI, because that is knowable at launch and is
worse to discover mid-conversation.

Both opt-in tasks are idempotent per registration via a marker record scanned out
of the `IServiceCollection`, not `TryAddEnumerable` (§6.9).

Every SP4 entry point begins with `await host.EnsureStartedAsync(ct)`, matching
`IEdgeDatabase` and SP2, so a provisioning failure surfaces on the first
`GetStreamingResponseAsync` with its real cause rather than as a
`FileNotFoundException` from inside GenAI.

### 14.2 Lifecycle observers

`ChatLifecycleObserver : EdgeLifecycleObserver`, registered with
`TryAddEnumerable`, resolving `IChatModelHost` **lazily from `IServiceProvider`**
— a constructor dependency would be a DI cycle, because `EdgeLifecycleHub`
materialises every observer in its own constructor and the host depends on
`IEdgeHost`. SP2 hit this exact wall. SP2's observer is registered first, so ORT
sessions drop before chat models, which is the right order: the embedding session
is the cheaper one to rebuild.

| Event | Action |
|---|---|
| `MemoryPressure(Low)` | Nothing. SP2 latches it; SP4 reads the latch. |
| `MemoryPressure(Moderate)` | `SetAcceptingTurns(false)` and drop the conversation `Generator` — that frees the whole KV cache, up to hundreds of megabytes, without paying a reload. The running turn finishes; new turns get 7105. |
| `MemoryPressure(Critical)` | `TerminateActiveGeneration()` first, so a multi-second prefill aborts rather than running to completion into a kill; then cancel the turn's CTS; then drop the cache; then `UnloadAsync` raced against a **two-second drain budget** — SP2's number and SP2's reason, "an iOS memory warning is not a place to block". `DropOnMemoryPressure = false` still terminates. The model survives only until its last lease returns. |
| `Sleeping` | Stop the in-flight turn at the next token boundary; the partial text reaches the caller with `StopReason = Suspended` and the real counts. Drop the conversation cache when `DropConversationCacheOnSleep`. Unload only when `UnloadOnSleeping` (default false). |
| `Resumed` | `SetAcceptingTurns(true)`. Nothing is pre-warmed: a resume that immediately reloads 1.24 GB is a resume that stutters. |
| `Stopping` | Terminate, unload unconditionally, then dispose the `OgaHandle` exactly once behind an `Interlocked` guard. `OrtEnv` is **not** disposed (§8.1). |

**There is no "checkpoint on sleep", and none is needed.** There is no ORT GenAI
API to serialise a KV cache, and inventing a persistence format for one would be
the largest thing in this sub-project. It is also unnecessary: MEAI is stateless
per call. Every token already streamed is in the caller's hands, and the next call
replays the full message list. So `Sleeping` cancels honestly, the partial text
stands as the user saw it, and the resumed conversation continues from the
message list. `Generator.RewindTo(ulong)` would make a true mid-token resume
possible and no consumer has asked for one; this paragraph exists so nobody
re-opens it as a missing feature.

`Qavren.Edge.Rag` registers **no lifecycle observer**. A `DelegatingChatClient`
has no resources to release.

### 14.3 Diagnostics

Two `IEdgeDiagnosticsContributor` implementations. Neither component name
contains "Native", so SP1's `EdgeDiagnostics.Report()` keeps picking the SQLite
native block for `EdgeDiagnosticsReport.Native` — SP2's rule, obeyed. Keyed
registrations get one contributor each, told apart by a `serviceKey` detail
rather than a decorated component name, matching SP2's embeddings contributor.
Every read is wrapped in a `Safe(...)` helper returning
`"(unavailable: <TypeName>)"`: a diagnostics report must never be the thing that
throws.

`"Qavren.Edge.Chat.Onnx"` —

*Environment:* `genAiVersion`, `genAiManagedAsset` (the `TargetFrameworkAttribute`
of the loaded `.Managed` assembly, which is how a reader discovers whether their
`net10.0-maccatalyst` app actually landed on `lib/net9.0-maccatalyst14.0`),
`ortVersionInProcess`, `ortEnvCreatedBeforeGenAi` (§8.1: `OrtEnv.IsCreated`
sampled at order 400 before SP4 loads its first GenAI type — read it beside
sub-project 2's own `ortEnvironmentPreexisting` key, published in the same
report under the `Qavren.Edge.Onnx` component, for the whole picture),
`ogaHandleOwned`,
`telemetryDisabled`, `runtimeIdentifier`, `abi`, `chatSupportedOnThisAbi`,
`totalMemoryBytes`, `availableMemoryKind`, `isLowRamDevice`,
`chatIntraOpNumThreads` (read from the resolved `genai_config.json`'s
`session_options`, published separately from SP2's `sharedThreadPool` because the
two are unrelated — §8.2).

*Model:* `presetId`, `modelId`, `displayName`, `spdxLicense`,
`huggingFaceRepo`, `huggingFaceRevision`, `modelDirectory`, `provisioned`,
`bundleBytes`, `weightsBytes`, `freeDiskBytes`, `modelType`,
`configContextLength`, `vocabSize`, `numHiddenLayers`, `numKeyValueHeads`,
`headSize`, `slidingWindow`, `kvBytesPerToken`, `kvBytesPerElement`,
`providers`, `chatTemplateSupported`, `promptFormatter`, `guidanceEnforced`,
`loaded`, `loadMs`, `loadCount`, `leases`.

*Budget:* `budgetVerdict`, `budgetExplanation`, `requestedContextTokens`,
`resolvedContextTokens`, `requiredBytes`, `kvCacheBytes`, `workspaceBytes`,
`reserveBytes`, `availableBytes`, `usableBytes`, `usedMeasuredPeak`,
`measuredPeakBytes`, `measuredOn`.

*Runtime honesty:* `turns`, `rejectedTurns`, `tokensGenerated`,
`tokensPerSecondP50`, `tokensPerSecondP95`, `lastTokensPerSecond`,
`lastTtftMs`, `lastStopReason`, `historyReductions`, `thermalThrottleEvents`,
`thermalAbortEvents`, `terminationEvents`, `unloadEvents`,
`processWorkingSetBytes` (from `Environment.WorkingSet`, labelled advisory
because it is unreliable on some platforms), `peakWorkingSetBytes`, plus the
current `thermalState`, `thermalHeadroom`, `isLowPowerMode` and
`lastMemoryPressure` read straight from `IEdgeResourceMonitor` so both blocks
agree.

The `ortEnvCreatedBeforeGenAi` / `ortEnvironmentPreexisting` pairing is
deliberately two keys from two components rather than one joined value: the
better-informed fact lives in sub-project 2's `internal` `OnnxEnvironmentState`,
which sub-project 4 is not an `InternalsVisibleTo` friend of and does not ask to
become one (§5). Joining them is the reader's job, and §8.1 has the table.

`guidanceEnforced`, `chatTemplateSupported` and `budgetExplanation` are the three
keys that pay for themselves. The first two are properties of the *shipped native
build* and of the *specific model*, neither knowable from a version number, and
both fail silently if nobody looks. The third is why a consumer got 2048 tokens
on one phone and 4096 on another.

`"Qavren.Edge.Rag"` — `retriever`, `retrieverType`, `top`, `maxContextTokens`,
`preferHybridSearch`, `chatClientPresent`, `collectionName`, `asks`,
`lastRetrievedCount`, `lastRetrievalMs`, `lastScoreKind`, `lastContextTokens`,
`lastCitationsAttached`, `lastCitationsUnresolved`, `lastGrounded`,
`retrievalFailures`, `extractiveAnswers`.

### 14.4 Logging and tracing

Every log site goes through `LoggerMessage.Define` — this repo's
`TreatWarningsAsErrors` plus `latest-recommended` makes CA1848 an error, as SP1's
`EdgeHost` already discovered. Event ids come from `EdgeChatEventIds` (900–959)
and `EdgeRagEventIds` (960–999).

Prompt and completion text are logged at `Trace` **only** and never above,
matching MEAI's own `UseLogging` contract, because a RAG prompt contains the
user's private corpus.

SP4 ships **no tracing of its own**. `UseOpenTelemetry()` over `EdgeChatClient`
already produces the GenAI semantic-convention spans, and `ChatClientMetadata.ProviderName`
is `"onnxruntime-genai"` so `gen_ai.system` populates. Everything SP4 adds rides
on `AdditionalProperties` under the documented `EdgeChatProperties` keys, which an
exporter can allow-list. Building a second telemetry system next to the one MEAI
already has is not a thing this suite does.

## 15. Error handling

### 15.1 New `EdgeErrorCode` values

SP1's 1001–4001 and SP2's 5001–5213 are untouched. 6000–6299 is reserved for
sub-project 3 and stays unallocated. SP4 adds 7000–7299:

```
// Qavren.Edge.Chat.Onnx — runtime and model hosting
ChatEnvironmentNotStarted        = 7001
ChatModelLoadFailed              = 7002
ChatModelNotRegistered           = 7003
ChatUnsupportedRuntime           = 7004   // no GenAI native for this RID or Android ABI
ChatInsufficientMemory           = 7005
ChatDeviceTooSmall               = 7006
ChatConfigurationInvalid         = 7007   // genai_config.json missing, unparseable or incoherent
ChatModelShapeMismatch           = 7008   // the config disagrees with the preset's declared shape
ChatExecutionProviderUnsupported = 7009

// Qavren.Edge.Chat.Onnx — provisioning
ChatModelNotProvisioned          = 7051
ChatInsufficientDiskSpace        = 7052
ChatDownloadNotPermitted         = 7053   // ChatProvisioningOptions.IsTransferPermitted said no

// Qavren.Edge.Chat.Onnx — generation
ChatTemplateUnsupported          = 7101
ChatPromptTooLong                = 7102
ChatGuidanceUnavailable          = 7103
ChatGenerationFailed             = 7104
ChatBusy                         = 7105
ChatThermalAbort                 = 7106
ChatToolCallingUnsupported       = 7107
ChatOptionUnsupported            = 7108

// Qavren.Edge.Rag
RagRetrieverMissing              = 7201
RagRetrievalFailed               = 7202
RagCollectionNotSearchable       = 7203
RagContextBudgetTooSmall         = 7204
```

**There is deliberately no "chat client missing" code.** An earlier draft carried
one, and it had no reachable raise site: `RagChatClient` takes its inner client as
a constructor parameter, and `UseRag()` sits on a `ChatClientBuilder` that already
has one, so "no inner client" is a state the type system prevents. A consumer who
registers no `IChatClient` at all fails at `GetRequiredService<IChatClient>()`,
which is the DI container's error and not SP4's to restate. A code with no raise
site would also be unsatisfiable under §16.1's rule that every 7000-range member is
thrown by at least one test, so it is removed rather than left as a number nobody
can produce.

Every one inherits `EdgeException`'s `HelpLink` convention
(`…/foundation/docs/errors.md#<code>`).

### 15.2 Exception types

| Type | Base | Carries | Raised from |
|---|---|---|---|
| `EdgeChatException` | `EdgeException` | preset id, model id, required/available/total bytes, budget kind, requested and fitting context tokens, prompt tokens, thermal state, RID, remediation | the environment task, the model host, the budget, the decode loop |
| `EdgeRagException` | `EdgeException` | retriever name, collection name, remediation | the middleware and the retriever adapter |
| `EdgeModelProvisioningException` | `EdgeException` (SP2's, **reused verbatim**) | model id, relative path, expected/actual sha and bytes, source URI | `ProvisionAsync` |

Provisioning failures reuse SP2's type and its 5053/5054/5055/5056 codes rather
than minting duplicates: a hash mismatch on a chat model is the same failure as a
hash mismatch on an embedding model and deserves the same doc page. SP4's own
7051, 7052 and 7053 cover the three cases SP2 has no code for — "absent and we
refuse to fetch it", "the whole bundle will not fit", and "the app's own network
policy said no" (§12.2.1).

### 15.3 Failure catalogue

| Situation | Behaviour |
|---|---|
| A GenAI type is reached before order 400, or order 200 has not run | `ChatEnvironmentNotStarted` (7001). Remediation: call `AddOnnxChat()` and resolve `IChatClient` from the container rather than constructing `EdgeChatClient` yourself |
| RID or Android ABI has no GenAI native | `ChatUnsupportedRuntime` (7004) **at startup**, naming the RID or ABI and listing the ones that do work |
| `genai_config.json` missing, unparseable or incoherent | `ChatConfigurationInvalid` (7007) naming the file and the offending field |
| The config disagrees with the preset's `ChatModelShape` | `ChatModelShapeMismatch` (7008) naming the field, the declared value and the preset's. Remediation: re-run `fetch_chat_model_hashes.py` |
| Total RAM below the preset's floor, or `IsLowRamDevice` | `ChatDeviceTooSmall` (7006) carrying `TotalMemoryBytes`. Remediation: set `MinTotalMemoryBytes` to null to try anyway, at the risk of an OS kill |
| Budget refuses even at `MinContextTokens` | `ChatInsufficientMemory` (7005) carrying required, available, total, budget kind, and the largest context that would have fit. Remediation names the smaller preset, a lower `MaxContextTokens`, and the two iOS entitlements — and states that the workspace and reserve terms are engineering estimates |
| `AvailableMemoryBytes` null or 0 | **Skipped, not refused.** Log 923 at `Warning`, record `SkippedUnknown`. Apple's own contract; inverting it would make the feature unusable on desktop |
| Model absent on disk | `ChatModelNotProvisioned` (7051) carrying the byte count and naming `Plan()`/`ProvisionAsync`. **Never a download** |
| Free disk short of the bundle total plus margin | `ChatInsufficientDiskSpace` (7052) **before the first byte**, not 900 MiB in |
| `ChatProvisioningOptions.IsTransferPermitted` returns false | `ChatDownloadNotPermitted` (7053) before a connection is opened. Remediation names the hook and the usual policy: wait for unmetered |
| The app is backgrounded mid-transfer | The transfer stalls, and on iOS is suspended outright. Every verified byte range is already on disk, so the next foregrounded `ProvisionAsync` resumes from the marker. v1 ships no background-transfer integration and §12.2.1 says so rather than implying a guarantee |
| Download, hash, or asset failure | SP2's `EdgeModelProvisioningException` with 5053/5054/5055, unchanged |
| `new Model` / `new Tokenizer` throws, or `LoadTimeout` elapses | `ChatModelLoadFailed` (7002) with the directory and the inner message preserved |
| `MemoryPressure(Critical)` during a load | No cancellation flag exists in GenAI. The load completes or fails on its own; SP4 refuses to *start* one while the latch is `Critical` and documents the gap |
| minja cannot parse the model's chat template | `ChatTemplateUnsupported` (7101) when `RequireChatTemplate`, else log 951 and use the fallback formatter, named in diagnostics. Remediation: set `PromptFormatter` |
| Prompt still over budget after reduction to the floor | `ChatPromptTooLong` (7102) with both numbers. Remediation names `MaxOutputTokens`, `ChatHistoryOptions` and `RagOptions.MaxContextTokens` |
| `ChatOptions.ResponseFormat` is a `ChatResponseFormatJson`, policy `Disabled` | `ChatGuidanceUnavailable` (7103), with the reason: the shipped mobile natives are built without `USE_GUIDANCE` and a request on such a build is silently ignored |
| `ResponseFormat` is `ChatResponseFormat.Text`, or null, **any** policy | Honoured by being ignored. Never 7103 — it asks for plain text, which is what this client produces, and refusing a caller for requesting the default would be a bug |
| `ResponseFormat` is a `ChatResponseFormatJson`, policy `RequireNative`, probe not `Enforced` | 7103 naming the probe result |
| `ResponseFormat` is a `ChatResponseFormatJson`, policy `PreferNative`, probe not `Enforced` | Unconstrained turn, recorded in `guidanceEnforced` and in `UnhonouredOptions`. Never a silent claim of constraint |
| `ChatOptions.Tools` non-empty, or `ToolMode` requires a call | `ChatToolCallingUnsupported` (7107). A required tool call that can never be emitted is a hard failure, not a footnote |
| Non-`TextContent` in a message; `max_length` in `SearchOptions` | `ChatOptionUnsupported` (7108) naming the member |
| `FrequencyPenalty`, `AllowMultipleToolCalls`, `ContinuationToken`, `AllowBackgroundResponses` set | Honoured where GenAI has an equivalent, otherwise listed in `UnhonouredOptions` on the response. Never silently dropped |
| Second concurrent turn, or the gate times out, or the queue is full, or turns are not being accepted | `ChatBusy` (7105) naming which. Remediation states that the GenAI C API is not thread safe, so turns serialise |
| Thermal at or above `AbortAt` before a turn, or mid-decode | `ChatThermalAbort` (7106) before; mid-decode the stream **completes** with `StopReason = Thermal` |
| Thermal is `Unknown` (desktop, a hosted runner, Android below API 29) | **Neither throttles nor aborts**, and never silently: the comparison is written `thermal != Unknown && thermal >= threshold`, `Unknown` is published verbatim in `thermalState` and on the turn status, and `ChatThermalOptions.RefuseWhenThermalUnknown` (default false) is there for a caller who wants the opposite. Same rule as an unreadable `AvailableMemoryBytes`. `ThermalHeadroom` of `NaN` likewise never throttles, because `NaN >= x` is false |
| Thermal at or above `ThrottleAt` mid-decode | Paced to `ThrottledTokensPerSecond`, logged 936 once per turn, recorded on the turn status. Never an abort |
| Native throw mid-decode | `ChatGenerationFailed` (7104). The partial text already streamed stays streamed; the final update says `StopReason = Error`; then the exception surfaces from the iterator |
| `MemoryPressure(Critical)` or `Sleeping` mid-decode | The stream **completes** with `StopReason = MemoryPressure`/`Suspended` and `FinishReason.Stop`. Not a throw |
| Retrieval throws | Logged 962; ungrounded answer with `grounded = false` under `ContinueOnRetrievalFailure` (default), else `RagRetrievalFailed` (7202) |
| `Top + Skip > MaxCandidates` | `RagRetrievalFailed` (7202) naming `SQLITE_VEC_VEC0_K_MAX`, before SP2 sees it |
| Hybrid requested, collection is not `IKeywordHybridSearchable<TRecord>` | `RagCollectionNotSearchable` (7203); or the vector lane when `PreferHybridSearch` left it optional |
| Retrieval returns nothing | `DefaultNoContextAnswer`, no model call, log 963. Not an error |
| A `[7]` with five sources | Dropped, counted, logged 965. Never fabricated into a citation |
| `UseRag()` with no retriever resolvable | `RagRetrieverMissing` (7201) naming `AddVectorStoreRetriever`/`AddRetriever` |
| `MaxContextTokens` cannot fit one truncated source | `RagContextBudgetTooSmall` (7204) |
| Any diagnostics read that throws | `"(unavailable: <TypeName>)"`. A report never throws |

**Cancellation is never converted.** `OperationCanceledException` propagates from
`GetResponseAsync`, from the streaming iterator, from `ProvisionAsync` and from
`AcquireAsync`. The final update in a cancelled stream still carries
`StopReason = Cancelled` and the real token counts before the exception surfaces,
so a caller who catches it still learns what was produced.

## 16. Testing

Five tiers. Tier 0 runs before any API is written. Tiers 1 and 2 run on every PR,
download nothing, and run on every device lane.

### 16.0 Tier 0 — the five-target link-and-generate smoke, before any SP4 code

**Nothing about ORT GenAI on `net10.0-*` is proven by anything anyone has
published.** The Managed package ships `netstandard2.0`, `net8.0`,
`net9.0-android31.0`, `net9.0-ios15.4` and `net9.0-maccatalyst14.0`, and no
`net10.0` or Windows-platform asset at all. So the first task of the plan is a
throwaway per target — Android emulator, iOS simulator, Mac Catalyst, Windows,
Linux — that references `Microsoft.ML.OnnxRuntimeGenAI` 0.15.2, constructs an
`OgaHandle`, loads the tier-2 fixture and generates one token.

Three go/no-go answers come out of it, and each has a written response:

1. **Mac Catalyst links at all.** The `_._` build folder plus the SDK RID-graph
   fallback to `runtimes/ios/native/` is the upstream README's assertion, and the
   failure is a runtime `DllNotFoundException`, not a build error. **A restore
   assertion cannot catch it** — `net10.0-maccatalyst` will resolve
   `lib/net9.0-maccatalyst14.0` perfectly and tell you nothing. If it fails,
   `Qavren.Edge.Chat.Onnx` ships three TFMs and the README states that Mac
   Catalyst has no chat in v1.
2. **`net10.0-*` consumes the `net9.0-*` assets and gets the natives**, including
   `net10.0` on Windows landing on `lib/net8.0` plus `runtimes/win-x64`, and
   including a **packaged MAUI WinUI** app, which is a different resolution path.
3. **The Managed assembly, compiled against `Microsoft.Extensions.AI.Abstractions`
   9.8.0, binds against this repo's 10.10.0.** .NET permits a higher assembly
   version to satisfy a lower reference and no API SP4 touches looked removed, but
   nobody has diffed the two. A `TypeLoadException` on first GenAI type load is
   the failure, and this smoke is the cheapest possible place to find it.

This runs first because each answer can change the TFM table, and a TFM cut
discovered after the API is written is a rewrite.

### 16.1 Tier 1 — unit, no native GenAI, every PR, every host, every device lane

Most of SP4's code is here and none of it needs ORT GenAI. Fakes live in
`Fakes/`, mirroring SP2's layout: a `FakeChatClient` (scripted updates, scripted
faults and delays), a `FakeChatModelHost`, a `FakeRetriever`, a
`StubResourceMonitor` and `StubDeviceProfile` (SP2 already ships the first), and
`FixedTimeProvider`. One committed text fixture: a ~700-byte `genai_config.json`
as a `const string` — reviewable in a diff, no binary.

Covers:

- **`ChatMemoryBudget.Resolve`**, the single most important target, table-driven
  over 4/6/8/12 GB devices × `PerProcess`/`SystemWide`/`Unknown` × both preset
  shapes × every ladder rung. Asserts the exact verdict, the exact
  `ContextTokens`, and that `SystemWideMemoryFraction` actually bites on Android.
  The device floor gets its own table, because it is one constant over three
  different platform totals (§6.4): a nominal-6 GB Android device reporting
  5.6 GiB of `TotalMem` and a nominal-6 GB iPhone reporting 6.0 GiB both **pass**
  for the large preset, a nominal-4 GB Android device reporting 3.6 GiB **fails**
  it and **passes** the small one, a `GcMemoryInfo` total is **never** compared
  against the floor whatever its value, and a preset under 200 MiB of weights —
  the tier-2 fixture's band — has no floor at all. Then `Unknown` skips rather than
  refuses, `RefuseWhenUnknown` inverts that, and `MeasuredPeakBytes` wins when
  larger with `UsedMeasuredPeak` recording it — plus the negative that matters
  here, that it does **not** bind for either shipped preset at 4096 (§9.3), so a
  future edit that makes it bind is visible. Plus the arithmetic anchors:
  `KvCacheBytesPerToken` for `Llama32_1BInstructInt4` asserted as the literal
  32,768, and the whole ladder walked once and asserted against the 1679 → 1583 MiB
  table in §9.3, so the ~6% leverage is a test rather than a claim.
- `ChatModelShape.FromGenAiConfig` over both real config shapes and every
  malformed variant (scalar vs array `eos_token_id`, missing decoder, zero layers)
  → 7007 naming the field; and the cross-check producing 7008 for a deliberately
  wrong preset shape.
- History reduction: system preserved, **pinned messages preserved**, whole
  eviction groups evicted oldest-first, the floor (system + pinned + newest
  `MinimumPreservedMessages`), 7102 at the floor, `MessagesDropped` counted. Plus
  the two shapes that used to have no specified answer: a tail that is
  user-then-user (a pinned RAG block in front of the question) reduces without
  stranding an assistant message, and a budget too small to hold the pinned block
  produces 7102 rather than evicting it. And the cross-package literal:
  `ChatHistoryOptions.PinnedMessageKeys`'s default equals
  `RagCitations.ContextMessagePropertyKey`, asserted as string equality in the
  chat test project — SP2's asserted-equal-duplicate pattern, copied.
- **Multi-token stop sequences** across a decode-chunk boundary — the upstream bug
  this design exists to fix, asserted directly.
- Option refusal: 7107 for `Tools` and for each `ToolMode` that requires a call,
  7108 for non-`TextContent` and for `max_length` in `SearchOptions`, 7103 for a
  `ChatResponseFormatJson` under `Disabled`, and `UnhonouredOptions` populated for
  `FrequencyPenalty`. **Plus the negative:** `ResponseFormat = ChatResponseFormat.Text`
  and `ResponseFormat = null` each complete a normal turn under every one of the
  three policies and never throw — the over-fire this boundary exists to prevent.
- **Precedence (§6.6)**, table-driven over the four overridable knobs: a
  `ChatOptions` value beats an `EdgeChatOptions` value beats a `ChatPreset`
  default; a `null` at a layer falls through rather than zeroing; and stop
  sequences come out as the ordinal-distinct union of all three layers sorted
  longest-first, with a test asserting that setting
  `EdgeChatOptions.StopSequences` does **not** delete the preset's.
- **The device profile's `net10.0` leg (§6.3)**: `AvailableMemoryKind` is
  `SystemWide`, `IsLowRamDevice` is `null` not `false`, `Abi` is `null`, and
  `TotalMemoryBytes` is either a plausible figure or `null` — never `0`. This is
  the leg every hosted runner executes, so it is asserted rather than assumed.
- Thermal: `StubResourceMonitor` + `FixedTimeProvider` drives throttle and abort
  over a fake generator, asserting mid-decode sampling at the one-second boundary,
  pacing rather than stopping at `Serious`, and `StopReason = Thermal` at
  `Critical`. **Plus the `Unknown` arm, which the enum's numbering would otherwise
  decide by accident** (§6.6): `EdgeThermalState.Unknown` neither throttles nor
  aborts, is published verbatim rather than normalised to `Nominal`,
  `RefuseWhenThermalUnknown = true` inverts it, and a `ThermalHeadroom` of `NaN`
  never throttles.
- `SearchOptions` value typing (§6.6): a `bool` and a `double` are applied, an
  `int` and a lossless `long` are converted, a `long` past 2^53 and a `string`
  each produce 7108 naming the key — asserted alongside the deliberate opposite,
  that the same `string` in `ChatOptions.AdditionalProperties` is ignored without
  error, which is what keeps a `RagSource` list inert on the model path.
- Queueing: a second concurrent turn waits, a fifth is refused with 7105.
- Streaming contract: one `MessageId` across updates, exactly one final
  metadata-only update carrying `UsageContent` and `ChatTurnStatus`,
  `FinishReason` `Length` vs `Stop`, and `GetResponseAsync` structurally equal to
  the aggregated stream. The bounded-channel producer completes its writer on
  success, fault and cancel, and the iterator observes the producer task.
- Lifecycle: each of the six events against a fake host, including the two-second
  drain bound and exactly one `OgaShutdown`.
- **DI and ecosystem shape**: `AddOnnxChat` on a **bare `ServiceCollection`** with
  no MAUI; `AddOnnx` registered exactly once when both SP2 and SP4 call it;
  keyed and unkeyed coexisting; `WarmUpChatAtStartup` twice registering one task;
  startup orders 400/410/420 sorting after 200; **and `OrtEnv.IsCreated` still
  false after building the provider and resolving `IChatClient`** — the
  composition half of §8.1's rule.
- **`GetService`**: returns `this`, `ChatClientMetadata` with a non-null
  `DefaultModelId` (the assertion Semantic Kernel's `GetModelId()` depends on),
  `ChatModelInfo`, `ChatClientStatistics`, null for a non-null `serviceKey`, and
  — through a `DelegatingChatClient` stack including `RagChatClient` —
  `ChatClientMetadata` still reaching the leaf. That last one is the Agent
  Framework and Semantic Kernel compatibility assertion, and it needs neither
  package.
- Error-code coverage: every 7000-range member is thrown by at least one test,
  and a string test over `foundation/docs/errors.md` asserts each has a
  `## <code>` heading with a non-empty remediation — SP2's shape.
- **RAG, all of it**, over a fake retriever and a fake `IChatClient`: golden-string
  assertion on the assembled block byte for byte; `Rank` ordering a `Distance`
  list ascending and a `Relevance` list descending and rescaling nothing;
  `ScoreThreshold` applied with the right polarity per lane, in a test whose name
  states the inversion; citation spans mapped to the right offsets **against the
  accumulated answer**, asserted by slicing the accumulation with each region and
  comparing to the marker text; the annotations arriving on exactly one update —
  the final one — carried by an empty `TextContent` and never on a token update;
  the non-streaming path landing them on the aggregated message's first
  `TextContent` with the **same** offsets, asserted by slicing
  `ChatResponse.Text`; a duplicate marker producing one annotation with two
  regions, a `[7]` with five sources dropped and counted, a marker inside a code
  fence; retrieval failure contained
  and `grounded = false`; `ShortCircuitOnNoContext` spending zero tokens;
  `ShouldRetrieve` skipping; `Top + Skip > MaxCandidates` → 7202;
  `ExtractiveChatClient` answering with citations and with no results; and
  `UseRag()` over `ExtractiveChatClient` producing a cited answer with no model,
  which is the no-LLM floor proven rather than asserted.
- **The source-passing channel itself (§11 step 6)**, because the floor test
  above is worthless without it: `RagChatClient` sets
  `RagCitations.SourcesPropertyKey` on the `ChatOptions` it passes inward and
  **not** on the instance the caller supplied (asserted by handing it a frozen
  options object and checking it is untouched afterwards); a fake inner client
  reads the list back with `Ordinal` stamped 1..n; `ExtractiveChatClient`
  invoked bare — no `UseRag()`, no property — returns `NoResultsAnswer` with
  `grounded` clear rather than throwing; and `EdgeChatClient` given the same
  property forwards **no** search option for it, proving the list is inert on
  the model path.
- **Projector shape (§7)**: one `Func<TRecord, RagSource>` signature reaches both
  `VectorStoreRetriever`'s constructor and `AddVectorStoreRetriever`; a projector
  that sets `Score`, `ScoreKind` or `Ordinal` has all three overwritten by the
  retriever; and the same projector produces `Distance` on the vector lane and
  `Relevance` on the hybrid lane over the same fake collection.

### 16.2 Tier 2 — one committed tiny GenAI model, every PR, host and device lanes

A fake cannot prove that SP4's client binds to real ORT GenAI, and that is the
highest-risk thing in the sub-project. **The "tokenizer-only fixture" idea does
not work and must not be planned for**: at v0.15.2 `Tokenizer` has exactly one
constructor, `Tokenizer(Model)`, and `OgaCreateTokenizerFromPath` exists only on
unreleased `main`. Anything touching real GenAI needs a real model directory.

`chat/tests/fixtures/make_tiny_chat_model.py` builds one under a pinned `uv`
venv — never a build step, and CI never runs it, exactly as SP2's
`make_tiny_model.py` is treated. It writes a hand-authored `config.json`
(`LlamaForCausalLM`, vocab 256, hidden 64, **4 attention heads → head_size 16**,
2 KV heads, 2 layers, intermediate 128, context 512, `tie_word_embeddings` true),
instantiates a seeded randomly-initialised model, `save_pretrained`s it beside a
256-entry byte-level BPE `tokenizer.json` carrying a minimal `chat_template`, and
runs `python -m onnxruntime_genai.models.builder -p fp32 -e cpu`.

**Head size 16 is not arbitrary.** On the fp32 CPU path the builder emits
`GroupQueryAttention` with fused RoPE, and ORT 1.30's
`group_query_attention_helper.h` enforces `head_size % 8 == 0` and, when rotary
cos/sin caches are present, `head_size % 16 == 0`. The obvious shortcut is a trap:
`hf-internal-testing/tiny-random-LlamaForCausalLM` has head_size 4 and will not
run through that path at all, and its 32,000-row vocabulary makes it 4 MB.

Target: **about 500 KB of graph** (a purpose-built 2-layer / 256-vocab fp32 Llama
is roughly that before the tokenizer), emitted as base64 `const string`s in
`TinyChatModel.g.cs` with a hard cap of **1 MB of generated source**, byte counts
asserted so an onnx or builder upgrade that moves them is loud. Upstream's own
smallest committed GenAI fixture is 258 KB for a decoder alone and its working
gpt2 fixture is 3.50 MB, so this is the right order of magnitude and not an
aspiration. The test fixture materialises the files into a temp directory once per
collection, because `Model(string)` needs a directory.

**Two written contingencies, decided in the plan rather than discovered late.**
If the hand-made BPE `tokenizer.json` is not accepted by onnxruntime-extensions —
every upstream fixture uses a full 2.1 MB GPT-2 one, and this is unverified — the
fallback is to vendor upstream's MIT-licensed
`test/models/hf-internal-testing/tiny-random-gpt2-fp32/` wholesale: 3.50 MB, five
files, a known-working GenAI folder with a matching tokenizer, and CI still
downloads nothing. If neither fits the size cap, or the builder rejects the
config, tier 2 moves wholesale to the nightly lane and the per-PR guarantee drops
to tier 1 plus tier 0's restore and link assertions. Tier 0 answers this before
any API is written.

**The fixture preset takes the no-floor band, and it does so by arithmetic rather
than by a special case.** `ChatMemoryBudgetOptions.MinTotalMemoryBytes` returns
null for any preset under 200 MiB of weights (§6.4), and the fixture is about
500 KB, so it is exempt by the same rule that exempts any small model. That is not
a nicety: a default GitHub-hosted Android AVD reports roughly 2 GiB of `TotalMem`,
and a blanket floor would turn the highest-value assertion in this sub-project —
real GenAI natives loading on a real device lane — into a guaranteed 7006. The
fixture's preset also leaves `RefuseWhenUnknown` at its default false, so a
host with no readable available-memory figure runs it rather than refusing.

Tier 2 asserts **mechanics only, never text** — the weights are random:

- `OgaHandle` constructs and disposes; `Utils.DisableTelemetryEvents()` does not
  throw; a fresh load after `OgaShutdown` succeeds (the 0.15.0 re-init claim,
  proved rather than trusted).
- Load → `ChatModelInfo` reports the geometry read from the fixture's
  `genai_config.json`; a deliberately-wrong preset shape produces 7008.
- One streaming turn yields N text updates then exactly one final update carrying
  `UsageContent`, a `FinishReason` and a `ChatTurnStatus`; every update shares one
  `MessageId`; `ToChatResponseAsync` groups them into one message.
- `MaxOutputTokens` produces `FinishReason.Length` at exactly N generated tokens,
  with the conversation cache **off** (where `max_length` and the managed counter
  agree) and **on** (where only the counter can, because a cached generator's
  search options are fixed at construction — §6.6). Separately, and this is the
  upstream bug asserted from the outside: with the cache on, `ChatModelInfo`
  reports the cached generator's `max_length` equal to the budget's
  `resolvedContextTokens` and **not** the model's declared `context_length`.
- Cancellation mid-stream throws within one token; cancellation during prefill is
  bracketed.
- `TerminateActiveGeneration()` from another thread ends the turn within a
  bounded time.
- The conversation cache, all five rules of §10(f): a first turn with a null
  `ConversationId` **mints** one and stamps it on every update and on the
  aggregated `ChatResponse.ConversationId`; echoing that id back hits, with
  `PromptTokensAppended` on turn two strictly less than turn one's while
  `PromptTokens` is strictly greater (the total the model conditioned on); a
  **different** id rebuilds; a **null** id on turn two rebuilds rather than
  reusing turn one's cache, which is the "two unrelated conversations share one KV
  cache" bug asserted directly; and a mutated history — the same id with an
  earlier message edited — fails the `StartsWith` prefix check and rebuilds rather
  than duplicating the conversation.
- A second concurrent turn **queues** rather than running; a fifth gets 7105.
- Unload with a live lease does not dispose until the lease returns, and the
  process does not crash — the access-violation guard.
- `ApplyChatTemplate` round-trips; a `PromptFormatter` override replaces it.
- **The guidance positive-control probe runs and its result is printed** whichever
  way it goes. This is the single test that answers whether the host's natives
  enforce constraints.
- **The ordering assertion**, phrased entirely in public signals because
  sub-project 2's `OnnxEnvironmentState` is `internal` to an assembly that does
  not befriend this one (§5): after `EnsureStartedAsync` on a container built by
  `AddOnnxChat`, the `Qavren.Edge.Chat.Onnx` diagnostics block reports
  `ortEnvCreatedBeforeGenAi = true` and no event 903 was logged, and the
  `Qavren.Edge.Onnx` block in the same `EdgeDiagnosticsReport` reports
  `ortEnvironmentPreexisting = false` — §8.1's first table row, asserted from
  outside both packages.

### 16.3 Tier 3 — the real model, nightly only

A new `chat-model-tests` job, cloned from SP2's `model-tests`:
`if: github.event_name == 'schedule' || github.event_name == 'workflow_dispatch'`,
**not** in `ci-gate`'s `needs`, `timeout-minutes: 45`,
`actions/cache@v6.1.0` keyed on the model's **SHA-256** (not a URL, not a date),
and `sha256sum -c` on **every** run including cache hits — a poisoned cache entry
is exactly the failure this lane would otherwise report as a quality regression.
`QAVREN_EDGE_CHAT_MODEL_DIR` gates it through a `SkipUnless` on its own
`ChatModelAvailable` static class with `SkipType` set, evaluated at **runtime**,
so a skipped test loads nothing — SP2's `ModelAvailable` pattern, copied.

The nightly model is **`Qwen3_600MInt4`** (461 MB, Apache-2.0): the smallest
genai-config-ready model, permissively licensed so its presence in a CI cache is
not a question, and the one whose KV-heavy geometry the budget most needs to be
right about.

Asserts and records:

- The model loads inside a time budget and a real prompt produces non-empty,
  coherently-shaped output.
- **The chat template renders for a real model's Jinja** — the single biggest
  per-preset unknown, since minja parsing any specific template is unverified.
- The resolved `max_length` matches the budget's decision, and the decision's
  `Explanation` is printed.
- **Peak RSS at the resolved context is recorded to the job summary and compared
  to the arithmetic**, printed rather than asserted tight. That is the measurement
  that answers §19 item 4 — whether `KvCacheBytesPerElement` is 2 or 4 for this
  build — on the first nightly, rather than carrying it as prose forever.
- tokens/sec and TTFT recorded as artifacts, **not** assertions: there is no
  measured device baseline to compare a hosted-runner number against.
- **Deterministic RAG quality**, with zero new package dependencies: over a
  committed 20-chunk corpus with `do_sample` off and a fixed `random_seed`, every
  `[n]` marker in the answer maps to a retrieved chunk, and the answer contains a
  required token from the gold chunk. `Microsoft.Extensions.AI.Evaluation.NLP` was
  considered and cut — it is still 10.10.0-preview.1, and this suite does not take
  a preview dependency for a weaker signal. `Evaluation.Quality` needs an LLM
  judge whose thresholds were calibrated for frontier models, and
  `Evaluation.Safety` needs the Foundry service; both are unusable here and are
  named as such.

### 16.4 Tier 4 — device lanes

No new lane. `Qavren.Edge.Chat.Tests` copies `Qavren.Edge.Onnx.Tests`'s shape
exactly: `net10.0` is an MTP application, the four platform TFMs are plain class
libraries with `IsTestingPlatformApplication=false`, same `NoWarn` set, same
per-OS `TargetFrameworks` guards (the `IsOSPlatform('Linux')` condition quoted
in §4.1, which the shipped package carries too) — with
`SupportedOSPlatformVersion` at android 24.0 and Apple **15.4** (§4.1). `Qavren.Edge.Rag.Tests` is `net10.0`
alone and never reaches a device lane, exactly as SP2's conformance project is.

`foundation/tests/Qavren.Edge.DeviceTests` gains one `ProjectReference` and
copies the tier-2 fixture — a megabyte into a device app is fine.

The lanes run tiers 1 and 2 with **no model download**. What they prove and
nothing else can:

- GenAI's natives actually load — a real Android `.so` from the AAR, a real iOS
  xcframework force-loaded into the app, and **whatever Mac Catalyst's RID-graph
  fallback really does** (§16.0 answers it first; this is the standing guard).
- `ortEnvCreatedBeforeGenAi` is true and sub-project 2's
  `ortEnvironmentPreexisting` is false — both read out of the one
  `EdgeDiagnosticsReport`, both public keys, no `internal` reached for. This is
  the only place §8.1's ordering rule is provable against real natives rather
  than asserted against a fake.
- `EdgeChatDeviceProfile.TotalMemoryBytes` is non-null and plausible, and
  `AvailableMemoryKind` is `PerProcess` on iOS and `SystemWide` on Android — the
  distinction the whole budget branches on.
- A simulated `MemoryPressure(Critical)` moves `LastPressure`, terminates the
  turn, unloads the model, and the next acquire reloads it.
- The Android lane asserts the **merged manifest** carries `INTERNET`,
  `ACCESS_NETWORK_STATE` and `ai.onnxruntime.genai.TelemetryInitializer`, so the
  AAR's contribution to every consumer's APK is documented by a test rather than
  discovered by a reviewer. It also reports the running ABI and asserts
  `chatSupportedOnThisAbi`.
- The lane logs the full chat diagnostics block and publishes it as an artifact,
  so tokens/sec and peak working set accumulate run over run rather than being
  measured once and forgotten.

Two things the lanes prove by **building at all**: the platform floors (an AAR
declaring `minSdkVersion 24` merged into a project declaring less is a build
failure, and a 15.4-built static slice linked into a lower deployment target is a
warning), and the package-size numbers, since each lane already produces an app
package and §19 item 5 is `ls -l` and `size -m` on an artifact they create anyway.

**Each lane records the execution provider; none asserts one.** SP2's rule, and
doubly right here: GenAI is CPU-only on every one of these lanes by construction,
and `device-tests-ios` and `device-tests-maccatalyst` both run on
`macos-15-intel` with x64 RIDs, so there is no Apple Neural Engine present to
assert about even if a provider existed.

## 17. CI changes

New projects go into `QavrenEdge.slnx` under `/chat/src/` and `/chat/tests/`
folders. `Directory.Packages.props` gains exactly **one** entry:

```xml
<ItemGroup Label="SP4 runtime">
  <PackageVersion Include="Microsoft.ML.OnnxRuntimeGenAI" Version="0.15.2" />
</ItemGroup>
```

`Microsoft.ML.OnnxRuntimeGenAI.Managed` is deliberately **not** pinned:
`CentralPackageTransitivePinningEnabled` is false in this repo precisely because a
pinned transitive is promoted into the produced nuspec, and SP4 has no reason to
widen its own public dependency set.

`ci.yml`'s `test` job gains two steps, following its explicit-enumeration
convention and SP2 plan adjustment 12's incantation — never `dotnet test`:

```yaml
      - name: Chat tests
        run: dotnet run --project chat/tests/Qavren.Edge.Chat.Tests/Qavren.Edge.Chat.Tests.csproj -c Release -f net10.0 -p:TargetFrameworks=net10.0

      - name: Rag tests
        run: dotnet run --project chat/tests/Qavren.Edge.Rag.Tests/Qavren.Edge.Rag.Tests.csproj -c Release -p:TargetFrameworks=net10.0
```

`Qavren.Edge.Rag.Tests` is single-TFM, so it takes `-p:TargetFrameworks=net10.0`
and no `-f`, matching SP2's conformance step.

Two new assertions on the `windows-2025` leg, mirroring SP2's ORT asserts, which
already run `dotnet restore QavrenEdge.slnx` there because it is the only image
with every workload:

1. **GenAI asset resolution.** Scan `project.assets.json` and assert that
   **`Microsoft.ML.OnnxRuntimeGenAI.Managed/0.15.2`** resolves `lib/net8.0` for
   `net10.0`, `lib/net9.0-android31.0` for `net10.0-android`, `lib/net9.0-ios15.4`
   for `net10.0-ios`, and `lib/net9.0-maccatalyst14.0` for `net10.0-maccatalyst`.
   It scans the **Managed** package and not the native one, and additionally
   asserts the native package resolves **zero** `compile` assets — SP2 adjustment
   2's lesson, learned the hard way: the native package carries only
   `build`/`buildTransitive`, so an assertion written against its id would pass
   vacuously forever.
2. **The ORT floor guard.** Assert the resolved `Microsoft.ML.OnnxRuntime` is
   `>= 1.28.0` **and still equals the repo pin (1.30.0)**. GenAI declares a floor,
   not a pin; its native links ORT dynamically and negotiates through
   `OrtApiBase::GetApi`, which fails only on an *older* runtime. This is the guard
   that stops someone "fixing" the version skew by downgrading ORT to match.

One new nightly job, `chat-model-tests` (§16.3), on a `schedule` trigger and not
in `ci-gate`'s `needs`.

`foundation/tools/ci-checks/assert-workflows.py` gains three assertions: every
test project under `chat/tests/` appears as an explicit step in `ci.yml`; the
GenAI asset assert exists and names the `.Managed` package; the ORT floor guard
exists. The existing `trx2junit.py` and `java-junit` counts stay at 4 — the device
lanes are unchanged in number.

`Qavren.Edge.Rag` joins the existing `trim-smoke` console: it is pure managed and
drives a fake chat client plus an in-memory retriever, so it proves the whole RAG
recipe is trim-safe for free. `Qavren.Edge.Chat.Onnx` sets `IsAotCompatible=true`
and its build must be clean of IL2026/IL3050, with source-generated JSON on every
serialisation path — GenAI's own csproj sets `IsAotCompatible` for net8.0-compatible
TFMs and its MEAI path uses source-generated JSON, so there is reason for optimism
and no proof. The README claims whatever `trim-smoke` proves and nothing more.

`native.yml` and `release.yml` are untouched. Path filters: SP4's projects live
under `chat/**`, which no native path filter matches, so a managed-only SP4 PR
still takes `native.yml`'s `reuse` job.

**Branch hygiene, before any of the above.** `feat/sp4-chat` sits on
`feat/sp2-embeddings` at `4fe723a`, which is behind `origin/main` by `a3a3a2c`
(build each native leg once / self-heal a missing native cache) and `ab5fb26`
(iOS device lane links and runs). Cherry-pick both — or rebase once SP2 merges —
**before** touching `ci.yml` or running tier 0, or the five-target smoke will
chase CI failures that main already fixed and the whole go/no-go will be read
through a broken lane.

## 18. Sample app

`foundation/samples/Qavren.Edge.Sample` gains two pages rather than SP4 shipping a
second sample. A second sample project would double the workload installs on four
device lanes to prove nothing new.

- **Chat** — the consent sheet first: model name, size, licence, free disk, and a
  download button wired to `IProgress<ModelProvisioningProgress>` and to the
  metered-network hook in one line —
  `o.Provisioning.IsTransferPermitted = () => Connectivity.Current.ConnectionProfile == ConnectionProfile.WiFi;`
  — which is how a reader discovers that SP4 does not read connectivity for them
  and why (§12.2.1). Then a
  streaming conversation showing tokens/sec, time-to-first-token, the resolved
  context, and the stop reason for every turn — including, deliberately, the ugly
  ones: a `Suspended` turn after backgrounding the app, and a
  `ChatInsufficientMemory` refusal with its full explanation rendered rather than
  swallowed.
- **Ask** — the RAG recipe over the existing Search page's notes: question in,
  streamed grounded answer out, with the numbered sources listed beside it and
  each `[n]` in the answer resolving to its citation. A toggle swaps
  `EdgeChatClient` for `ExtractiveChatClient` in place, so a reader can see the
  no-LLM floor answer the same question through the same pipeline.
- **Diagnostics** — the existing page, now rendering two more components with no
  code change, since `EdgeDiagnosticsReport` walks contributors generically.

The app's `Entitlements.plist` sets `com.apple.developer.kernel.increased-memory-limit`
and `com.apple.developer.kernel.extended-virtual-addressing`, which is also how a
consumer discovers they need them.

The sample targets the four MAUI platform TFMs, as SP1 adjustment 31 established.

## 19. Verification items for the plan

Each carries a stated assumption; the plan confirms or adjusts, and records the
result.

1. **The five-target link-and-generate smoke — do this FIRST, before any SP4
   code.** §16.0 is the full description; this is the item that schedules it.
   Three sub-answers: (a) does Mac Catalyst actually link, given the `_._` build
   folder and the RID-graph fallback to `runtimes/ios/native/`; (b) do
   `net10.0-android` / `-ios` / `-maccatalyst` / `net10.0`-on-Windows resolve the
   `net9.0-*` and `net8.0` assets **and get the natives**, including a packaged
   MAUI WinUI app; (c) does the Managed assembly compiled against MEAI
   Abstractions 9.8.0 bind against this repo's 10.10.0. Assumption: all three yes.
   **If (a) fails**, `Qavren.Edge.Chat.Onnx` ships `net10.0`, `net10.0-android`
   and `net10.0-ios`, the README states that Mac Catalyst has no chat in v1, and
   §4.1's block follows. A restore assertion is not a substitute for either (a) or
   (b): both fail at runtime, not at restore. **All five targets run on
   GitHub-hosted runners**, which is why this item can be gating without being
   blocked on hardware: Windows, Linux and the Android emulator on the existing
   `test` matrix, and the iOS simulator and Mac Catalyst on `macos-15-intel` — the
   same runner `device-tests-ios` and `device-tests-maccatalyst` already use
   (§16.4), and sufficient because GenAI's xcframework ships
   `ios-arm64_x86_64-simulator` and `ios-arm64_x86_64-maccatalyst` slices. The Mac
   Mini is **not** on tier 0's critical path — suite decision 14 and SP1's
   decision 14 both say it is not on the critical path at all — and is needed only
   for item 3's on-device iOS measurement, which is not gating.
2. **The two presets' real geometry, read from the shipped `genai_config.json`.**
   `Llama32_1BInstructInt4`'s values are known and stated in §6.5 (16 layers,
   8 KV heads, head size 64, vocab 128256, context 4096 as published).
   `Qwen3_600MInt4`'s are **not** — its published metadata gives size and
   parameter count and no attention geometry — and §6.5 deliberately describes its
   KV cost qualitatively rather than inventing numbers. `fetch_chat_model_hashes.py`
   reads both configs and emits both shapes; the plan runs it and records the
   literals, and the §16.1 catalogue table test then guards them. Assumption: the
   Qwen3 family shares head_dim 128 and 8 KV heads across sizes, which would make
   its KV cache per token several times Llama-3.2-1B's despite a third the
   weights. **If that is wrong**, §6.5's warning paragraph and §9.3's table change
   and nothing else does — the budget computes from whatever the script emits.
3. **iOS memory, measured.** There is **no published tokens/sec or peak-RSS figure
   for ORT GenAI on any iPhone or Apple silicon device, for any model.** Every
   phone number in this spec is a vivo X300 under Android 16. Apple publishes no
   per-device jetsam table either, which is why §9.3 reads
   `os_proc_available_memory()` at runtime and refuses on that rather than on a
   device table. Measure on the Mac Mini and a real device before the README
   claims anything about iOS: peak RSS at 2048 and 4096 context for both presets,
   with and without the two entitlements, plus tokens/sec and TTFT. Record in the
   vault, then in the README.
4. **`KvCacheBytesPerElement` — 2 or 4, per preset.** `genai_config.json` does not
   state the KV dtype, so the load-time cross-check cannot validate the one field
   that multiplies the entire KV term (§6.2). The default of 2 is calibrated
   against Llama-3.2-1B's measured 1280 MiB peak, where the arithmetic lands within
   1.2%. §16.3's nightly prints measured peak RSS beside the arithmetic at the
   resolved context, which answers it for the Qwen preset on the first run.
   Related and unverified: whether a given int4 CPU export takes GenAI's **static**
   KV path (allocated once to `max_length`; `shape_[2] = max_length;`) or the
   **dynamic** one (`shape_[2] = total_length;`, grown per step). `config.h`
   annotates `past_present_share_buffer` as "(cuda only)" yet `kv_cache.cpp` can
   force it on when the graph has a fixed KV shape. If the dynamic path is taken,
   capping `max_length` bounds the ceiling but not mid-conversation growth, and
   §9.3's formula is optimistic. Measure before the README states the formula as
   fact.
5. **What SP4 costs an app, measured rather than estimated.** From the device
   lanes' own artifacts, so this is `ls -l` and `size -m` and not new CI: (a) the
   `.apk`/`.aab` delta from adding `Qavren.Edge.Chat.Onnx`, with all ABIs and with
   `$(AndroidSupportedAbis)` trimmed to `arm64-v8a`, including what `libmat.so`
   contributes; (b) the iOS binary's `__TEXT` before and after, against Apple's
   80 MB cap — the only ceiling with no workaround, because the xcframework is
   force-loaded statically, and SP2's ORT already spends part of that budget;
   (c) the same for Mac Catalyst if item 1(a) says it links. Record all three in
   the vault and put (a)-trimmed and (b) in the README.
6. **Whether minja parses each preset's chat template.** The mechanism is
   confirmed; per-model parse success is not, and the failure surfaces only at the
   first `ApplyChatTemplate` because `LoadChatTemplate` returns OK with a warning.
   §9.5's probe converts it from a mid-turn throw into a startup fact, and §16.3
   asserts it per preset on the nightly. Assumption: both parse. **If one does
   not**, that preset ships with a `PromptFormatter` in its own definition and the
   README says its output quality is model-template-dependent.
7. **Whether `SetRuntimeOption("terminate_session", "1")` is safe from another
   thread.** `src/ort_genai_c.h` says flatly "This API is not thread safe", and
   this is the one GenAI call SP4 makes outside the turn gate. It is documented as
   the cooperative abort and is presumably designed for exactly this, but that is
   an inference. §16.2 asserts it works; the plan must additionally run it under a
   stress loop before the jetsam guard is *claimed* to work. **If it is unsafe**,
   the fallback is the volatile flag alone — the decode loop still aborts between
   tokens, and a long prefill runs to completion, which is stated as the limit.
8. **Whether `Microsoft.Extensions.AI` 10.10.0's `ChatClientBuilder` ordering and
   `AddChatClient` lazy-factory behaviour are as documented**, since the whole
   "order of builder calls does not matter" claim in §4.3 rests on it. Assumption:
   yes — `Build` applies factories in reverse and the descriptor's factory is
   `builder.Build`. Verify with the §16.1 DI test in both call orders before the
   README states it.
9. **Android telemetry.** Whether `Utils.DisableTelemetryEvents()` prevents the
   1DS initialisation or merely suppresses events after the fact, and whether the
   `TelemetryInitializer` provider can be stripped with `tools:node="remove"`
   without breaking the library, are both unknown. §16.4 asserts the merge exists;
   the plan decides whether the README says "disabled" or "events suppressed", and
   says whichever is true.
10. **The device floor's three totals, measured.** §6.4 sets the floor at about
    0.85x nominal (5.0 GiB and 3.4 GiB) because Android's `TotalMem` excludes
    kernel-reserved memory while Apple's `PhysicalMemory` does not, and a floor at
    the nominal figure would refuse the default preset on essentially every
    nominal-6 GB Android device while admitting every nominal-6 GB iPhone. The
    0.85x is an engineering estimate like every other constant here. Record the
    real `ActivityManager.MemoryInfo.TotalMem` and
    `NSProcessInfo.PhysicalMemory` from every device and emulator the lanes touch —
    the device lanes already publish the diagnostics block (§16.4), so this is
    reading an artifact — and adjust the two constants to sit safely below the
    lowest nominal-class reading observed. **If an Android device of a class SP4
    means to support reports below 5.0 GiB**, the constant moves; it is a number,
    not a design.
11. **Whether the conversation cache's delta append is lossless at the seam.**
    §10(f) appends only `fullText[cachedText.Length..]`, encoded on its own, so a
    BPE merge that would have spanned the join is lost. The join is expected to
    fall immediately after a special token, which is atomic, making this a
    non-issue — but that is an expectation about each preset's template, not a
    proof. The check is cheap and exact: for the same conversation, compare the
    token sequence produced by the append path against `tokenizer.Encode(fullText)`
    and assert they are identical, per preset, on the nightly lane. **If they
    differ**, the affected preset ships with `EnableConversationCache` defaulted
    off in its own definition and the README says why — a one-line change, because
    the option is already per-registration.
12. **`Config.Overlay`'s merge semantics.** §9.1 step 5 composes one overlay
    document in managed code precisely so this does not matter, but "we made it not
    matter" is a weaker claim than "we know". Load a real preset, call
    `Overlay({"search":{"max_length":N}})` once, and read back whether the model's
    shipped `do_sample`, `temperature`, `top_k` and `top_p` survived — deep merge —
    or were replaced. Record the answer. **If it replaces**, §9.1's step (a)
    re-emission is load-bearing rather than belt-and-braces and the plan says so;
    **if it merges**, step (a) stays anyway, because a consumer's
    `ConfigOverlayJson` and SP4's `max_length` still have to be composed with each
    other regardless of what the native call does with the result.
13. **ADRs to write during planning:** 0009 **execution providers cut** — GenAI
    has no CoreML and no NNAPI EP, DirectML dead-ends at 0.14.1/ORT-DML 1.23.0,
    QNN packaging was removed in 0.14.0 and `OgaRegisterExecutionProviderLibrary`
    has no C# binding, so v1 is CPU everywhere and the only escape hatch is
    `ConfigOverlayJson`; records the revisit trigger. 0010 **the upstream
    `IChatClient` is not shipped**, with the five defects and the file-and-line
    evidence, so nobody re-opens it as duplicated work. 0011 **guidance is
    unverified, not unavailable**, recording `CreateGuidanceLogitsProcessor`'s
    silent-nullptr behaviour at v0.15.2, the Apple build settings that omit
    `--use_guidance`, and the positive-control probe as the only honest test.
    0012 **no tool calling in v1**, recording the prior art's finding and the
    dependency on 0011. 0013 **Apple platform floor 15.4**, sourced from GenAI's
    own asset TFM rather than chosen, with item 1's link result as the evidence
    and the README's supported-minimum statement as the consequence.

## 20. Out of scope for sub-project 4

Chunkers, extractors, the ingestion pipeline (SP3). Docs site, benchmarks, NuGet
1.0 (SP5).

Also deliberately out, with the reason:

- **Shipping or wrapping `OnnxRuntimeGenAIChatClient`** — §1 decision 1 lists the
  five defects. Two clients with different memory behaviour behind one
  `IChatClient` registration would be a support burden with no payoff, and
  `GetService(typeof(Model))` lets anyone build it themselves in one line.
- **Tool calling** — no `FunctionCallContent` emitted or consumed, and
  `UseFunctionInvocation` above this client is therefore inert, which the README
  states rather than leaving to be discovered. A ~1B on-device model emits
  malformed tool calls often enough to violate the suite's "never looks broken"
  rule, and the grammar sampling that made the prior art's version survivable is
  exactly what the next item says is unavailable. ADR 0012.
- **Structured output / constrained decoding by default** — `USE_GUIDANCE` is off
  in the shipped mobile natives and a request on such a build is silently ignored
  at v0.15.2. `EdgeGuidancePolicy` plus the positive-control probe is the honest
  middle; reinstating it as a default needs either a GenAI build with
  `USE_GUIDANCE=ON` (a Rust/Corrosion toolchain, contradicting decision 5) or
  upstream shipping it. ADR 0011.
- **Multimodal** — `Images`, `Audios`, `MultiModalProcessor`, `StreamingProcessor`
  and the vision/speech halves of `genai_config.json`. Suite decision 9 puts
  images after v1, and the lease exposes `Model` for anyone who wants them
  unwrapped.
- **LoRA adapters** (`Adapters`, `Generator.SetActiveAdapter`) — no caller, and a
  second adapter set is more resident memory on the platform with none to spare.
- **Every non-CPU execution provider** — `.Cuda`, `.DirectML`, `.QNN`, `.WinML`,
  `.Foundry`, and any typed EP policy. ADR 0009.
- **Beam search and speculative decoding** — `num_beams > 1` disables
  `past_present_share_buffer` and multiplies the KV cache by the beam count, which
  is the opposite of what a phone needs.
- **Summarising history compaction** — on-device it costs a second full generation
  pass per compaction. The token-budget reducer ships; summarisation does not.
- **A public dependency on `ReducingChatClient` / `UseChatReducer` /
  `MessageCountingChatReducer` / `SummarizingChatReducer`** — all
  `[Experimental(AIChatReduction)]`. SP4 implements the stable `IChatReducer` and
  applies it internally, so a consumer who accepts the warning can still hand it
  to `UseChatReducer`.
- **`Generator.RewindTo`-based incremental history, and KV-cache persistence
  across launches** — the first is an optimisation with no measured need, the
  second has no API and would mean inventing a serialisation format. §14.2 records
  why "checkpoint on sleep" needs neither.
- **Concurrent conversations** — one gate, one cached `Generator`, one turn at a
  time; a second queues and a fifth is refused by name. Two simultaneous KV caches
  on a nominal-6 GB phone is the jetsam scenario this whole design exists to
  avoid.
- **`Microsoft.Agents.AI`** — `AIContextProvider`, `TextSearchProvider`,
  `UseAIContextProviders` and the Compaction subsystem.
  `UseAIContextProviders` throws at invocation time without a live
  `AIAgent.CurrentRunContext`, which a plain `IChatClient` never has, so it cannot
  be the default path — and a dependency that cannot be the default path is not
  worth a package reference. `IEdgeRetriever` mirrors its delegate shape so the
  two interconvert in one line.
- **A Semantic Kernel or Agent Framework adapter package** — `AsChatCompletionService()`
  and `AsAIAgent()` are extension methods in *their* packages over `IChatClient`.
  Getting `GetService` right is the entire integration, and §16.1 asserts it.
- **`Microsoft.Extensions.AI.Evaluation.*` in CI** — `.NLP` is preview-only,
  `.Quality` needs an LLM judge calibrated for frontier models, `.Safety` needs
  the Foundry service. §16.3's deterministic assertions need none of them.
- **`UseDistributedCache` over chat** — its own docs warn the cache round-trips
  through JSON, dropping `RawRepresentation` and turning `object` values into
  `JsonElement`. For a local model whose cost is milliseconds of CPU it is the
  wrong cache twice over.
- **ORT model-package layout** (`manifest.json`, `shared_assets/sha256-<hex>/`) —
  v1 supports the flat `genai_config.json` layout only. `Model(string)` handles
  both, so adding it later is additive.
- **Bundling a chat model in the app package**, and any Play Asset Delivery or
  iOS Background Assets integration — §12.1. A consumer who wants asset delivery
  registers their own `IOnnxModelSource`, which is SP2's extension point, reused.
- **Background transfer** — an `NSURLSession` background download, an Android
  foreground service, a `WorkManager` job, or any wrapper that keeps a 1.24 GB
  fetch alive across backgrounding or process death. §12.2.1 states the v1
  behaviour instead of implying a guarantee: the transfer is foreground-only, it
  resumes from the marker rather than restarting, and an app that needs it
  unattended drives the same cancellation-safe `ProvisionAsync` from its own
  background job. Reading connectivity is out for the same reason — it would put a
  MAUI or per-platform dependency in a package suite decision 13 requires to run on
  a bare `ServiceCollection` — so metered-network policy is the one-line
  `IsTransferPermitted` hook and nothing more.
- **Gemma-3-1b, Phi-4-mini, Phi-3.5-mini and TinyLlama presets** — §6.5 gives the
  reason for each. Two presets, one default and one permissively licensed.
- **Injecting the two iOS entitlements from MSBuild** — §12.4. Writing someone's
  entitlements plist from a NuGet package is not a thing this suite does.
- **A `Qavren.Edge.Chat.Abstractions` package** — `IChatClient` already is the
  abstraction. A second one would be the second chat abstraction in the same
  namespace neighbourhood.
- **Qavren-branded logging, caching or tracing middleware** — `UseLogging`,
  `UseOpenTelemetry` and `UseDistributedCache` already exist upstream and compose,
  because registration goes through `services.AddChatClient`.
