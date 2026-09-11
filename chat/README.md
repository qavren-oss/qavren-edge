# Qavren.Edge — Chat and RAG

Sub-project 4 turns the semantic-search stack (sub-project 2) into an
answering one. Two packages, matching roadmap row 4:

- **`Qavren.Edge.Chat.Onnx`** (L1) — a `Microsoft.Extensions.AI` `IChatClient`
  over ONNX Runtime GenAI, with a KV-cache-aware memory budget, consent-gated
  model provisioning, thermal-paced streaming and honest per-turn
  diagnostics. It wraps ORT GenAI's own primitives (`Model`, `Config`,
  `Tokenizer`, `Generator`) rather than the runtime's own
  `OnnxRuntimeGenAIChatClient` — see ADR 0010 for why.
- **`Qavren.Edge.Rag`** — a retrieval-augmented generation recipe: a
  `DelegatingChatClient` (`RagChatClient` / `ChatClientBuilder.UseRag()`)
  that retrieves through any `Microsoft.Extensions.VectorData` collection,
  assembles a numbered context block under a token budget, and resolves
  bracketed `[n]` markers into `CitationAnnotation`s. It references no ONNX
  package and no `Qavren.Edge.Chat.Onnx` type, so it composes with
  `UseLogging()`, `UseOpenTelemetry()`, `chatClient.AsAIAgent()` and an
  Azure OpenAI client or a Qdrant collection unchanged.

## Supported minimums

Adopting `Qavren.Edge.Chat.Onnx` raises the Apple deployment floor above
the rest of the suite:

- **iOS 15.4** and **Mac Catalyst 15.4** — up from sub-project 1's and
  sub-project 2's 15.1. This is GenAI's own floor, not one this suite
  chose: its managed asset ships at `lib/net9.0-ios15.4` and its
  xcframework slices are built there; Mac Catalyst inherits the iOS slice
  through the SDK RID graph. See ADR 0013. An app that stays on 15.1 keeps
  the database and embeddings and cannot add chat.
- **Android API 24** (`minSdkVersion 24`) — unchanged from sub-project 2,
  because the GenAI AAR's own manifest declares 24. `arm64-v8a` and
  `x86_64` only; see the APK-cost section below.
- **Windows** binds GenAI's `lib/net8.0` asset by RID (`win-x64`,
  `win-arm64`); there is no Windows platform TFM.

`Qavren.Edge.Rag` has no native dependency and no floor of its own — it
runs wherever `Microsoft.Extensions.AI` and
`Microsoft.Extensions.VectorData.Abstractions` run.

## The consumer's calls

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

Citations arrive **once**, on the final metadata update, and their spans
index the accumulated answer, not any single update — so accumulate, then
index your own buffer. `TextSpanAnnotatedRegion.StartIndex`/`EndIndex` are
`int?` on the shipped MEAI 10.10.0 surface (measured), so a slice needs a
null check even though `RagCitations.Build` always sets both:

```csharp
var answer = new StringBuilder();
await foreach (var update in chat.GetStreamingResponseAsync("what does the warranty cover?"))
{
    Console.Write(update.Text);
    answer.Append(update.Text);

    foreach (var citation in update.Contents.SelectMany(c => c.Annotations ?? [])
                                            .OfType<CitationAnnotation>())
    foreach (var region in (citation.AnnotatedRegions ?? []).OfType<TextSpanAnnotatedRegion>())
    {
        // StartIndex/EndIndex are int?. RagCitations.Build always sets both, but the
        // null check is what makes this compile against the nullable API rather than
        // assuming a guarantee the type itself does not carry.
        if (region.StartIndex is not { } start || region.EndIndex is not { } end) continue;
        Console.WriteLine($"\n[{citation.Title}] {citation.Url} → " +
            answer.ToString(start, end - start));
    }
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

## There is no default preset

`AddOnnxChat` takes a preset as a **required** argument. There is no
default, because a silently-chosen model under the Llama 3.2 Community
Licence is not a default anyone should inherit, and because App Store
Review 4.2.3(ii) and Google Play's 500 MB base-module cap both require an
app to know — and disclose — what it is about to download before it does.

The two shipped presets, with their measured numbers:

| | `Llama32_1BInstructInt4` | `Qwen3_600MInt4` |
|---|---|---|
| Bundle download | **1.241 GB** decimal | **495 MB** decimal |
| Weights (`model.onnx` + `.data`) | **1167.52 MiB** | **461.25 MiB** |
| KV cache per token (2 B/elem) | **32 KiB** | **112 KiB** |
| SPDX licence | `LicenseRef-LLAMA-3.2-Community` | `Apache-2.0` |

**The smaller download wants *more* resident memory at a long context.**
Qwen's bundle is 40% of Llama's size, but its KV cache is 3.5× as expensive
per token (112 KiB vs 32 KiB) — a consequence of its geometry (28 layers ×
8 KV heads × head size 128 vs Llama's 16 × 8 × 64), not of its weights.
At a 4096-token context the arithmetic budget (`WorkspaceBytes` 192 MiB,
`ReserveBytes` 192 MiB, `KvCacheBytesPerElement` 2) needs 1679.52 MiB for
Llama and 1293.25 MiB for Qwen; the ladder down to 1024 tokens buys Llama
only 96 MiB (6%) but buys Qwen 336 MiB (26%) — the KV-dominated shape the
context ladder exists for, stated with numbers instead of an adjective.

## Nothing downloads implicitly

`AcquireAsync` on an unprovisioned model throws `ChatModelNotProvisioned`
(7051) carrying the byte count. A download happens through exactly one
path: `IChatModelProvisioner.Plan()` → a consent screen showing the bytes,
the SPDX identifier and the licence URL → `ProvisionAsync`. This is what
App Store Review guideline 4.2.3(ii) requires ("disclose the size of the
download and prompt users before doing so"), and there is no code path
that performs a download without it.

Metered networks are a policy hook, not a guess: `ChatProvisioningOptions
.IsTransferPermitted` is consulted once before the first byte and refuses
with `ChatDownloadNotPermitted` (7053) when it says no. The library never
reads connectivity itself — that would be a MAUI or per-platform
dependency in a package that has to work on a bare `ServiceCollection` —
so the app supplies that one line.

## What the transfer does not do

The transfer is a plain `HttpClient` (SP2's `HttpOnnxModelSource`), not an
`NSURLSession` background transfer and not driven by an Android foreground
service or a `WorkManager` job:

- **Foreground-only.** Backgrounding the app stalls the transfer, and iOS
  will suspend the process outright.
- **Resumable, not restarted.** Every verified byte range is already on
  disk under the model root with its marker, so the next foregrounded
  `ProvisionAsync` continues from where it stopped and never from zero.
- **No background-transfer integration ships in v1.** An app whose users
  will realistically background a 1.24 GB download should drive
  `ProvisionAsync` from its own foreground service or `WorkManager` job —
  the method is cancellation-safe and resumable precisely so that it can
  be — but this library ships no wrapper for either.
- **Android process death is the app's problem.** A long-running transfer
  from a plain activity or a MAUI page can be killed with the process. The
  resumability contract above makes that survivable, not unattended.

## Two iOS entitlements are a consumer requirement

Two entitlements raise the ceiling and are the only real lever on a
nominal-6 GB phone:

- `com.apple.developer.kernel.increased-memory-limit` (iOS 15+) raises the
  OS-imposed resident limit.
- `com.apple.developer.kernel.extended-virtual-addressing` (iOS 14+)
  extends the process virtual address space — the one that matters for a
  multi-GB memory-mapped model.

These are plist lines in the **consuming app**, not code in this library.
`Qavren.Edge.Chat.Onnx` does not write anyone's entitlements file; the
sample app sets both, and `ChatInsufficientMemory` (7005)'s remediation
text names them.

## What adopting chat costs an Android APK

Measured from the `Microsoft.ML.OnnxRuntimeGenAI` 0.15.2 native package on
disk, not estimated:

- The AAR is **21.5 MB compressed** (21,570,331 bytes).
- Per ABI: `libonnxruntime-genai.so` **5.2 MB**, `libonnxruntime-genai-jni.so`
  **1.9 MB**, and **`libmat.so` 32.7 MB** — a library whose purpose is
  documented nowhere upstream and which there is no known way to exclude.
- Only **`arm64-v8a`** and **`x86_64`** ship in the AAR's `jni/` — there is
  no `armeabi-v7a` slice. An `armeabi-v7a` device therefore gets the
  database and the embeddings model and **has no chat at all**;
  `ChatUnsupportedRuntime` (7004) fires at startup rather than a
  `DllNotFoundException` on the first message.
- The merged manifest gains `INTERNET`, `ACCESS_NETWORK_STATE` and a
  `ai.onnxruntime.genai.TelemetryInitializer` content provider.

`DisableTelemetry` defaults **true**. Whether that call prevents 1DS
(Microsoft's telemetry pipeline)
initialisation outright, or only suppresses events emitted after it,
is **unknown** as of this task (§19 item 9 is unmeasured). Read this as
"events are disabled at the API GenAI exposes" — not as "telemetry is
off" — until that item is measured.

## Turns serialise

One gate per model, one cached `Generator`; a second concurrent turn
queues and a fifth is refused with `ChatBusy` (7105). On a phone that is
the point — the GenAI C API is not thread safe, so this is not a
limitation to work around. On a server it is a throughput ceiling: one
model instance serves one turn at a time.

## No tool calling, no structured output by default, no multimodal

- **No tool calling.** `ChatOptions.Tools` non-empty, or a `ToolMode` that
  requires a call, is refused with `ChatToolCallingUnsupported` (7107).
  `UseFunctionInvocation` layered above this client is inert. See ADR 0012.
- **No structured output by default.** `EdgeGuidancePolicy.Disabled` is the
  default, and under it a `ChatResponseFormatJson` request is refused with
  `ChatGuidanceUnavailable` (7103) unconditionally — the probe is not
  consulted. Opting in to `RequireNative` refuses with the same 7103 when
  the positive-control probe did not come back `Enforced`; `PreferNative`
  instead runs an unconstrained turn and records it in `guidanceEnforced`
  and `UnhonouredOptions`. See ADR 0011.
- **No multimodal.** `Images`, `Audios` and any non-`TextContent` message
  content are out of scope for v1 and are refused by name with
  `ChatOptionUnsupported` (7108) — never silently dropped. This is a scope
  decision, not a native-build defect like guidance, so it has no ADR of
  its own.

## Diagnostics

Every read is wrapped so a diagnostics report never itself throws.

**`Qavren.Edge.Chat.Onnx`** —

*Environment:* `genAiVersion`, `genAiManagedAsset`, `ortVersionInProcess`,
`ortEnvCreatedBeforeGenAi`, `ogaHandleOwned`, `telemetryDisabled`,
`runtimeIdentifier`, `abi`, `chatSupportedOnThisAbi`, `totalMemoryBytes`,
`availableMemoryKind`, `isLowRamDevice`, `chatIntraOpNumThreads`.

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
`processWorkingSetBytes` (advisory — unreliable on some platforms),
`peakWorkingSetBytes`, `thermalState`, `thermalHeadroom`, `isLowPowerMode`,
`lastMemoryPressure`.

**`Qavren.Edge.Rag`** — `retriever`, `retrieverType`, `top`,
`maxContextTokens`, `preferHybridSearch`, `chatClientPresent`,
`collectionName`, `asks`, `lastRetrievedCount`, `lastRetrievalMs`,
`lastScoreKind`, `lastContextTokens`, `lastCitationsAttached`,
`lastCitationsUnresolved`, `lastGrounded`, `retrievalFailures`,
`extractiveAnswers`.

**Reading `ortEnvCreatedBeforeGenAi` beside SP2's `ortEnvironmentPreexisting`.**
ORT's `OrtEnv` is a ref-counted process singleton, and whichever library
creates it first fixes its logging manager for the process — a second
creator's log id, severity and logging function are silently discarded.
`ortEnvCreatedBeforeGenAi` is sampled at chat's startup order 400, *after*
SP2's order-200 `OrtEnv` task and *before* SP4 names its first GenAI type.
The two keys live under two different components (SP2's `OnnxEnvironmentState`
is `internal` and SP4 is not an `InternalsVisibleTo` friend of it — joining
them in code would be a third amendment to a prior sub-project), so joining
them is the reader's job. This is the table that does it:

| `ortEnvCreatedBeforeGenAi` | SP2's `ortEnvironmentPreexisting` | Meaning |
|---|---|---|
| `true` | `false` | The correct boot. SP2 created `OrtEnv`; its options are live. |
| `true` | `true` | `OrtEnv` existed before SP2 ran — a third library got there first. SP2's options were discarded; SP4's ordering is nonetheless intact. |
| `false` | either | SP4's ordering rule was violated: something touched GenAI before order 400. Event 903. |

## The GenAI managed-asset lookup table

`genAiManagedAsset` is published as `"<mvid> (System.Runtime/<v>[, android])"`
rather than a `TargetFrameworkAttribute` — none of the five shipped `lib/`
assets carries one, and iOS and Mac Catalyst have byte-different assemblies
with **identical** referenced-assembly sets, so only the MVID tells them
apart. Look the reported MVID up here:

| Asset | Size (bytes) | MVID | Distinguishing reference |
|---|---|---|---|
| `lib/net8.0` | 54,584 | `324d5b97-7f06-44d8-b891-c7c016bf320a` | `System.Runtime/8.0.0.0` |
| `lib/net9.0-android31.0` | 55,608 | `55ff0c26-68af-4221-a722-a6f886235964` | `_Microsoft.Android.Resource.Designer` |
| `lib/net9.0-ios15.4` | 55,608 | `9f386bb3-4703-4d05-ad01-48546f73ebcc` | `System.Runtime.InteropServices/9.0.0.0` |
| `lib/net9.0-maccatalyst14.0` | 55,608 | `c90a773f-14a8-44fb-94a6-6a4cec55074c` | *(identical reference set to iOS)* |
| `lib/netstandard2.0` | 55,608 | `59176d71-3b46-4c05-8436-341fd6fb98b8` | `Microsoft.Bcl.AsyncInterfaces` |

A `net10.0` host is expected to report the `lib/net8.0` row's MVID,
`324d5b97-7f06-44d8-b891-c7c016bf320a` — asserted by a tier-1 test.

## What is proven and what is not

Trim-smoke proves what trim-smoke proves: that `Qavren.Edge.Rag` and its
closure trim clean under `IsAotCompatible`, nothing about runtime behaviour
on a device. There is **no published tokens/sec or peak-RSS figure for ORT
GenAI on any Apple device** as of this task — every phone number anywhere
in this repo is a vivo X300 under Android 16.

### The tier-2 fixture's tokenizer is accepted, proved end-to-end

The committed tiny fixture carries a **hand-made 256-entry byte-level BPE
`tokenizer.json`** rather than a vendored one, and whether ORT GenAI would
accept it was the one open question in that chain. It is now answered by
measurement, not assumption: onnxruntime-genai 0.15.2 and its bundled
onnxruntime-extensions load the hand-made tokenizer, `Encode("hello world")`
returns the bytes themselves (`[104 101 108 108 111 32 119 111 114 108 100]`
— token id equals byte value, the design intent), decode round-trips
exactly, `ApplyChatTemplate` renders, and a short generation completes.

So the fixture keeps its **primary** shape. The contingency — vendoring the
3.50 MB `tiny-random-gpt2-fp32` model — is **not needed and not taken**, and
tier 2 therefore stays a per-PR lane rather than something gated behind a
multi-megabyte download.

One caveat the fixture carries: the generator is a by-hand **Windows** step.
`genai_config.json` is written with the platform newline, so the committed
base64 encodes CRLF and regenerating on Linux or macOS moves the byte counts
for a reason unrelated to any onnx or builder upgrade. Regenerate it on
Windows. (`.gitattributes` is `* text=auto eol=lf`, so the generated `.cs`
itself stays LF-stable everywhere; only the base64 payload is host-dependent.)

### Tier 0's six legs

Tier 0 (Task 1.5, the five-target link-and-generate smoke) proves each of
these legs independently, over the committed tier-2 fixture, before any
SP4 API is written. This table's rows are written now, empty; Task 1.6
Step 4 fills in pass/fail and the printed `key=value` block for each once
the workflow has run.

| Leg | Result | `key=value` |
|---|---|---|
| `tier0-windows` | *(pending — Task 1.6 Step 4)* | |
| `tier0-linux` | *(pending — Task 1.6 Step 4)* | |
| `tier0-winui` | *(pending — Task 1.6 Step 4)* | |
| `tier0-android` | *(pending — Task 1.6 Step 4)* | |
| `tier0-ios` | *(pending — Task 1.6 Step 4)* | |
| `tier0-maccatalyst` | *(pending — Task 1.6 Step 4)* | |

**What tier 0 does not close.** The packaged (MSIX) MAUI WinUI path is
**untested, because this repo has no signing identity** —
`Qavren.Edge.DeviceTests` sets `WindowsPackageType=None` for exactly that
reason. Tier 0 ran the **unpackaged** head: it proves the unpackaged WinUI
resolution path and nothing about MSIX packaging or signing.

## Prompt and completion text

Prompt and completion text are logged at `Trace` **only**, and never
above, because a RAG prompt contains the user's private corpus — turning
`UseLogging()` up to `Debug` to debug a retrieval will show neither the
prompt nor the answer text.
