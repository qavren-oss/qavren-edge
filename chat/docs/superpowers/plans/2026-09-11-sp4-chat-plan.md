# Qavren.Edge Sub-project 4 — Chat + RAG: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `Qavren.Edge.Chat.Onnx` and `Qavren.Edge.Rag` — a `Microsoft.Extensions.AI` `IChatClient` over ONNX Runtime GenAI with a KV-cache-aware memory budget, and a RAG recipe that retrieves through sub-project 2's hybrid `vec0` + FTS5 store and streams a cited answer — composed on top of merged SP1 and branch SP2.

**Architecture:** Two packages on two layers. `Qavren.Edge.Chat.Onnx` (L1, over SP2's L0 `Qavren.Edge.Onnx`) owns the single process-wide `OgaHandle`, the `genai_config.json` shape reader, the device profile, the memory budget and context ladder, the ref-counted chat-model host, provisioning with a consent plan, the chat-template and guidance probes, the streaming decode loop, history reduction, the presets, the lifecycle observer and the diagnostics contributor. `Qavren.Edge.Rag` (L2) owns `IEdgeRetriever`, the MEVD collection adapter, `RagChatClient : DelegatingChatClient` + `UseRag()`, numbered context assembly, `[n]` → `CitationAnnotation`, and `ExtractiveChatClient`. The two packages **never reference each other**, and `Qavren.Edge.Rag` references no ONNX package at all.

**Tech stack (measured on this box — see Environment ground truth):** .NET SDK 10.0.401, C# 14, `Microsoft.ML.OnnxRuntimeGenAI` **0.15.2** (managed 0.15.2 transitive), `Microsoft.ML.OnnxRuntime` 1.30.0 (SP2's pin, unchanged), `Microsoft.Extensions.AI(.Abstractions)` 10.10.0, `Microsoft.Extensions.VectorData.Abstractions` 10.10.0, xunit.v3 3.2.2 on Microsoft.Testing.Platform, uv 0.12.1 + CPython 3.14.5 for the two by-hand Python toolchains.

**Worktree:** `C:\Users\steve\projects\qavren-edge-sp4`, branch **`feat/sp4-chat`** (currently at `4fe723a`, SP2's last commit). The `main` checkout at `C:\Users\steve\projects\qavren-edge` and the `-sp2` / `-sp3` worktrees are in use by other agents and **must never be touched**.

**Branch and commit model.** Implementers **never run git**. One **integrator** per wave owns every root file — `QavrenEdge.slnx`, `Directory.Packages.props`, `Directory.Build.props`, `Directory.Build.targets`, `.gitignore`, `.github/**` — and commits the wave onto `feat/sp4-chat`. Tasks marked **(integrator)** below are the integrator's own; every other task is an implementer's, and an implementer that finds itself wanting to edit a root file has found a plan bug, not a licence.

**There is exactly one carve-out from that rule, and it is named here rather than discovered.** `.github/workflows/tier0-genai-smoke.yml` is **delegated to Task 1.5's implementer** (adjustment 30). Four conditions make it safe and all four are checked, not assumed: it is a **new file at a path no other task touches**; it is a **throwaway** deleted in the closing PR; it is `workflow_dispatch`-only and outside `ci-gate`'s `needs`, so it can gate nothing; and Task 1.5 is a **serial interlude that runs alone**, so there is no concurrent writer anywhere in `.github/**`. No other implementer task in this plan may create or edit any file under `.github/`, `QavrenEdge.slnx`, `Directory.Packages.props`, `Directory.Build.*` or `.gitignore` — and the wave-1 close (Task 1.6) reviews the delegated file as part of the wave it commits. A second carve-out is a plan bug; this one is a plan decision.

**Every Python invocation in this plan names the absolute interpreter `C:\Python314\python.exe`.** A bare `python` or `python3` on this box resolves to the Windows Store alias or fails outright, and `pyyaml` is installed into that one interpreter and no other. There is no exception anywhere in this file: if a step shows a bare `python`, the step is wrong.

**Every wave has three phases, and every wave has an integrator.** SP2's rules, kept verbatim because both hazards are still live here:

1. **Prologue (serial, integrator).** A task that mutates a file every project in the repo imports — `Directory.Packages.props`, `QavrenEdge.slnx` — cannot share a wave with a task whose verify *builds* anything. Only wave 1 has a prologue (Task 1.1). The only later root-file edit is Task 2.3, which touches `.github/**` and `foundation/tools/ci-checks/` — paths no `dotnet build` reads.
2. **Parallel phase (implementers, at most three).** Folder-disjoint **and** build-graph-disjoint as the Wave map's columns show — plus **output-disjoint**, which folders alone do not buy. MSBuild takes no cross-process lock on a project's `obj/` or `bin/`, so two tasks that both build `Qavren.Edge.Core` intermittently produce `MSB3021` or "the process cannot access the file … because it is being used by another process", which reads like a flaky test and is not. **Every verify command in a parallel phase therefore carries `-p:ArtifactsPath=<the task's own scratch root>`.**
3. **Close (serial, integrator).** The integrator deletes the wave's scratch roots, re-runs **every** verify in the wave **sequentially in the repo's real `obj/`/`bin/`**, and only then commits. A wave that passes only under isolation has not passed.

**Scratch roots.** `D:\Local\Temp\qedge-sp4\w<wave>\t<task>` — e.g. Task 3.2's verify appends `-p:ArtifactsPath=D:\Local\Temp\qedge-sp4\w3\t32`. On a box where `D:` is unavailable, `C:\Users\steve\AppData\Local\Temp\qedge-sp4\…` is the substitute; the path is arbitrary, the disjointness is not.

**Convention for "literal code".** Every artifact this plan *originates* is literal and complete: the four csproj files, the `Directory.Packages.props` block, the `.slnx` entries, the `.gitignore` entries, the workflow YAML, the `assert-workflows.py` rules, both Python generators, `ChatPresets.g.cs`'s measured constants, and every test assertion whose expected value was measured rather than guessed.

**Public API surfaces are adopted BY REFERENCE, not transcribed.** Where the spec declares a type as literal C# — §6.1–§6.9, §7 — the plan says "exactly as §N declares it" and the implementer transcribes that block **verbatim, XML docs included**. The spec is 4,174 lines of normative prose; re-typing its declarations here would create a second source of truth that can drift. Two obligations come with the by-reference rule:

1. **Every public type the spec declares is NAMED by exactly one task.** A type nobody names is a type nobody writes, and that failure presents as a compile error three waves later.
2. **Every default value that changes behaviour is restated in the owning task**, under a **Defaults that change behaviour** heading, and is either asserted by a test in the same task or named in the README. Those headings appear in Tasks 2.1 (`RagOptions`, `ExtractiveChatOptions`), 3.1 (`ChatMemoryBudgetOptions`, `ChatThermalOptions`, `ChatHistoryOptions`, `EdgeChatOptions`, `ChatPreset`), **3.2 (`VectorStoreRetrieverOptions<TRecord>`, and `RetrievalRequest`'s two consumed defaults)** and 4.1 (`EdgeGenAiOptions`, `ChatProvisioningOptions`).

Method **bodies** of the larger internal implementations are specified by their declared signature, their stated behaviour and the spec section that defines them. Where a body has a trap in it (the `StartsWith` prefix check, the `Unknown`-is-zero thermal comparison, the rolling stop-sequence buffer, the single composed `Overlay` document, the `ChatOptions.Clone()` before injection) the trap is restated inside the task, because that is exactly what a reader of the task alone would otherwise get wrong.

---

## Environment ground truth (this box)

Everything below was measured on 2026-09-11 in the `feat/sp4-chat` worktree, not assumed. Where it contradicts the spec, the **Spec adjustments** section names the consequence.

| Fact | Value |
|---|---|
| .NET SDK | 10.0.401 (`global.json`, `rollForward: latestFeature`), MTP runner selected repo-wide |
| Workloads | android, ios, maccatalyst, maui-windows installed; **restore and compile** for `net10.0-ios` / `net10.0-maccatalyst` both succeed on this Windows host |
| NuGet global packages | `D:\packages\nuget` |
| `Microsoft.ML.OnnxRuntimeGenAI` 0.15.2 | **exists on nuget.org and restores**; native package is 109 MB, managed is transitive |
| uv | 0.12.1; Python `C:\Python314\python.exe`, CPython **3.14.5** |
| Python invocation | **always the absolute `C:\Python314\python.exe`.** A bare `python` / `python3` hits the Store alias (`WindowsApps\python.exe`) or fails; `pyyaml` is installed into that one interpreter by **Task 1.5 Step 7**, which is the first step in the plan that needs it, and every later step re-runs the same idempotent install |
| Network | `huggingface.co` and `pypi.org` are reachable; every preset figure below was fetched here |
| Build isolation | `Directory.Build.props` sets neither `ArtifactsPath` nor `UseArtifactsOutput`, so `-p:ArtifactsPath=…` is free |
| NOT available | macOS / Xcode / a Mac link; Android emulator; any workflow run (implementers never push) |

### Branch hygiene — measured, and it is a real gap

`feat/sp4-chat` is at `4fe723a` and `origin/main` carries two commits it does not have: `a3a3a2c` (build each native leg once / self-heal a missing native cache) and `ab5fb26` (iOS device lane links and runs). Both touch `.github/workflows/**`, which Task 2.3 edits. Cherry-picking them is **Task 1.1 Step 0** — before `ci.yml` is touched and before any CI-shaped assertion is written.

### ORT GenAI asset resolution — spec §17 assertion 1, CLOSED here

A throwaway library targeting `net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst`, referencing `Microsoft.ML.OnnxRuntimeGenAI` 0.15.2 + `Microsoft.ML.OnnxRuntime` 1.30.0 + `Microsoft.Extensions.AI` 10.10.0, restored **and built** on this Windows box for all four TFMs:

```
net10.0             -> Microsoft.ML.OnnxRuntimeGenAI.Managed  lib/net8.0/Microsoft.ML.OnnxRuntimeGenAI.dll
net10.0-android     -> Microsoft.ML.OnnxRuntimeGenAI.Managed  lib/net9.0-android31.0/...
net10.0-ios         -> Microsoft.ML.OnnxRuntimeGenAI.Managed  lib/net9.0-ios15.4/...
net10.0-maccatalyst -> Microsoft.ML.OnnxRuntimeGenAI.Managed  lib/net9.0-maccatalyst14.0/...
```

The **native** `Microsoft.ML.OnnxRuntimeGenAI` package resolves **zero** `compile` assets on every TFM — only `build` / `buildTransitive` — exactly as spec §17 predicted and exactly the vacuous-assertion trap SP2 adjustment 2 recorded. Its build folders are `build/net8.0` (win-x64 / win-arm64 `None` copies), `buildTransitive/net9.0-android31.0`, `buildTransitive/net9.0-ios15.4`, and `buildTransitive/net9.0-maccatalyst14.0/_._` — a real zero-byte placeholder.

Native payload, listed from the package on disk:

```
runtimes/android/native/onnxruntime-genai.aar                21,570,331
runtimes/ios/native/onnxruntime-genai.xcframework.zip        55,422,019
runtimes/linux-arm64/native/libonnxruntime-genai.so          36,959,176
runtimes/linux-x64/native/libonnxruntime-genai.so            35,741,680
runtimes/osx-arm64/native/libonnxruntime-genai.dylib         11,668,432
runtimes/win-arm64/native/onnxruntime-genai.{dll,lib}         7,344,952
runtimes/win-x64/native/onnxruntime-genai.{dll,lib}           7,509,304
```

**No `osx-x64`, no `win-x86`.** The AAR's `jni/` holds **`arm64-v8a` and `x86_64` only** — no `armeabi-v7a` — with `libmat.so` at **32,696,832 bytes** on arm64 and 31,044,760 on x86_64, plus `libonnxruntime-genai.so` (5,163,184) and `libonnxruntime-genai-jni.so` (1,966,936). The AAR's `AndroidManifest.xml`, read verbatim:

```xml
<uses-sdk android:minSdkVersion="24" />
<uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
<uses-permission android:name="android.permission.INTERNET" />
<application>
  <provider android:name="ai.onnxruntime.genai.TelemetryInitializer"
            android:authorities="${applicationId}.onnxruntime_genai_telemetry_initializer"
            android:exported="false" android:initOrder="100" />
</application>
```

Every §9.4 / §12.5 claim about the Android payload is therefore confirmed rather than inferred. The xcframework zip carries three slices — `ios-arm64`, `ios-arm64_x86_64-simulator`, `ios-arm64_x86_64-maccatalyst` — so the Mac Catalyst *slice* exists; whether the SDK's RID-graph fallback actually **links** it is still CI's question (§19 item 1(a)).

The iOS targets file is exactly what §9.4 describes, and the condition is evaluated in the consuming app:

```xml
<ItemGroup Condition="('$(OutputType)'!='Library' OR '$(IsAppExtension)'=='True')">
  <NativeReference Include="...runtimes\ios\native\onnxruntime-genai.xcframework.zip">
    <Kind>Static</Kind><IsCxx>True</IsCxx><SmartLink>True</SmartLink>
    <ForceLoad>True</ForceLoad><LinkerFlags>-lc++</LinkerFlags><WeakFrameworks>CoreML</WeakFrameworks>
  </NativeReference>
</ItemGroup>
```

and Android's is `<AndroidLibrary Bind="false" Include="...runtimes\android\native\*">` under `Condition=" '$(AndroidApplication)'=='true' "`.

### Package dependency floors — measured from the nuspecs

`Microsoft.ML.OnnxRuntimeGenAI` 0.15.2 declares `Microsoft.ML.OnnxRuntime` **>= 1.28.0** (a floor, not a pin) and `Microsoft.ML.OnnxRuntimeGenAI.Managed` = 0.15.2, on every TFM group. The Managed package declares `Microsoft.Extensions.AI.Abstractions` **>= 9.8.0** on every group. With SP2's explicit 1.30.0 and 10.10.0 pins in the graph, the resolved closure is ORT **1.30.0** and MEAI **10.10.0** — nothing is downgraded and nothing new is promoted.

### Spec §16.0 tier-0 answer (c) — CLOSED here, on Windows (and re-closed over a real model by Task 1.5)

A `net10.0` console referencing the package, run on this box:

```
OnnxRuntimeGenAIChatClientOptions constructed OK; EnableCaching=True
MEAI.Abstractions loaded: 10.10.0.0
IChatClient assignable from OnnxRuntimeGenAIChatClient: True
OgaHandle constructed OK
DisableTelemetryEvents OK
OgaHandle disposed (OgaShutdown) OK
second OgaHandle after shutdown OK
OrtEnv.IsCreated after all of the above: False
```

Five facts fall out and all five change what the plan writes:

1. **The Managed assembly compiled against MEAI.Abstractions 9.8.0 binds against 10.10.0.** No `TypeLoadException`, and `IChatClient` is satisfied. §16.0's third go/no-go is answered *yes* before any SP4 code exists.
2. **`OgaHandle` constructs, `OgaShutdown` runs on dispose, and a second handle afterwards works** on win-x64 — the 0.15.0 transparent-re-initialisation claim §6.6 rests on, proved rather than trusted.
3. **`Utils.DisableTelemetryEvents()` is `static` and does not throw.**
4. **Creating and disposing an `OgaHandle` leaves `OrtEnv.IsCreated` false.** That is §8.1's trap made visible: GenAI's `OrtEnv` is the *native* singleton, and SP2's managed guard cannot see it. The ordering rule is real.
5. The GenAI managed assembly's identity is `Version=0.0.0.0`, `FileVersion=0.0.0.0`, and it carries **no `AssemblyInformationalVersionAttribute`**. See adjustment 1.

### ORT GenAI managed surface — spot-checked against the shipped assembly

Present and shaped as §6–§10 assume: `OgaHandle()`, `Utils.{DisableTelemetryEvents,EnableTelemetryEvents,SetLogBool,SetLogString}` (all static), `Config(string)` with `Overlay(string)` / `AppendProvider(string)` / `ClearProviders()` / `SetProviderOption(string,string,string)`, `Model(string)` and `Model(Config)` with `GetModelType()`, `Tokenizer(Model)` **and no other constructor**, `Tokenizer.ApplyChatTemplate(string template_str, string messages, string tools, bool add_generation_prompt)`, `Tokenizer.Encode(string) -> Sequences`, `Tokenizer.CreateStream()`, `TokenizerStream.Decode(int)`, `GeneratorParams(Model)` with **exactly two** `SetSearchOption` overloads — `(string,double)` and `(string,bool)` — plus `GetSearchNumber`/`GetSearchBool`, `Generator(Model,GeneratorParams)` with `AppendTokens(ReadOnlySpan<int>)`, `AppendTokenSequences(Sequences)`, `GenerateNextToken()`, `IsDone()`, `GetSequence(ulong)`, `TokenCount()`, `SetRuntimeOption(string,string)` and `RewindTo(ulong)`, and `OnnxRuntimeGenAIException`.

Four signature details the plan's literal code must respect:

- **`GeneratorParams.SetGuidance(string type, string data, bool enableFFTokens = false)`** — three parameters, the third defaulted. §9.6's two-argument call compiles; the parameter is named here so nobody "discovers" it and starts passing `true`.
- **`Generator.TokenCount()` returns `ulong`** and `GetSequence` takes `ulong`. Every comparison against a context or an output cap in §10 crosses `int`/`ulong` and must cast deliberately, once, at the boundary.
- **`Sequences`' indexer is `this[ulong]`** with a `NumSequences` property. `tokenizer.Encode(text)[0].Length` compiles because `0` is a constant, but a variable index needs a cast.
- **`Tokenizer` has no `Dispose`-free path and no path-based constructor**, which is why §16.2's fixture must be a real model directory. Confirmed, not assumed.

### The GenAI managed assets are indistinguishable by `TargetFrameworkAttribute` — measured

None of the five `lib/` assets carries a `TargetFrameworkAttribute` or an `AssemblyInformationalVersionAttribute`, and all five report `Version=0.0.0.0`. Their MVIDs, however, are distinct:

| Asset | Size | MVID | Distinguishing reference |
|---|---|---|---|
| `lib/net8.0` | 54,584 | `324d5b97-7f06-44d8-b891-c7c016bf320a` | `System.Runtime/8.0.0.0` |
| `lib/net9.0-android31.0` | 55,608 | `55ff0c26-68af-4221-a722-a6f886235964` | `_Microsoft.Android.Resource.Designer` |
| `lib/net9.0-ios15.4` | 55,608 | `9f386bb3-4703-4d05-ad01-48546f73ebcc` | `System.Runtime.InteropServices/9.0.0.0` |
| `lib/net9.0-maccatalyst14.0` | 55,608 | `c90a773f-14a8-44fb-94a6-6a4cec55074c` | *(identical reference set to iOS)* |
| `lib/netstandard2.0` | 55,608 | `59176d71-3b46-4c05-8436-341fd6fb98b8` | `Microsoft.Bcl.AsyncInterfaces` |

iOS and Mac Catalyst have **byte-different assemblies with identical referenced-assembly sets**, so only the MVID tells them apart at runtime. See adjustment 2.

### MEAI 10.10.0 surface — spot-checked against the shipped assemblies

Every type and member §6.6, §7, §10 and §11 name exists: `CitationAnnotation` (`Title`, `Url` as `Uri`, `FileId`, `Snippet`, `AnnotatedRegions` as `IList<AnnotatedRegion>`), `TextSpanAnnotatedRegion` (`StartIndex`/`EndIndex`, both **`int?`**), `AIContent.Annotations` as `IList<AIAnnotation>`, `ChatOptions.Clone()`, `ChatOptions.AdditionalProperties` as `AdditionalPropertiesDictionary`, `ConversationId`, `Instructions`, `Tools`, `ToolMode`, `ResponseFormat`, `StopSequences`, `Seed` (**`long?`**), `PresencePenalty`/`FrequencyPenalty` (`float?`), `AllowMultipleToolCalls`, `ContinuationToken`, `AllowBackgroundResponses`, `MaxOutputTokens`, `Temperature`, `TopP`, `TopK`; `ChatResponseExtensions.ToChatResponseAsync(IAsyncEnumerable<ChatResponseUpdate>, CancellationToken)`; `ChatClientBuilder.Build(IServiceProvider)`; `DelegatingChatClient`; `IChatReducer`; `ChatResponseFormatJson`; `ChatClientMetadata`; `FunctionInvokingChatClient`; `UsageContent`; `ChatFinishReason`; `ReducingChatClientBuilderExtensions.UseChatReducer`; `LoggingChatClientBuilderExtensions.UseLogging`; `OpenTelemetryChatClientBuilderExtensions.UseOpenTelemetry`.

**`AddChatClient` and `AddKeyedChatClient` live on `Microsoft.Extensions.DependencyInjection.ChatClientBuilderServiceCollectionExtensions`** — namespace `Microsoft.Extensions.DependencyInjection`, **not** `Microsoft.Extensions.AI`. §6.9's registration file needs that using directive.

### The two presets' real geometry — spec §19 item 2, CLOSED here

Both repos exist, both carry the exact file set §6.5 describes, and both `genai_config.json` files were fetched from a pinned commit SHA on 2026-09-11.

| | `Llama32_1BInstructInt4` | `Qwen3_600MInt4` |
|---|---|---|
| Repo | `Arm/llama-3-2-1b-instruct-onnx-genai-int4-kquantlast-emb-int8-vivo-x300` | `Arm/qwen3-0-6b-onnx-genai-int4-kquantlast-emb-int4` |
| Revision (full commit SHA) | `333c515af8b9355011c0b295c4356c9c24b463ff` | `c1d7bbbbb20630eef24c00d8ad18250bd57b232c` |
| `model.type` | `llama` | `qwen3` |
| `model.context_length` | **4096** | **40960** |
| `vocab_size` | 128256 | 151936 |
| `num_hidden_layers` | 16 | 28 |
| `num_key_value_heads` | 8 | 8 |
| `head_size` | 64 | 128 |
| `sliding_window` | *absent* | *absent* |
| `decoder.filename` | `model.onnx` | `model.onnx` |
| shipped `search` | `do_sample true, temperature 0.6, top_k 50, top_p 0.9, max_length 4096, past_present_share_buffer true` | `do_sample true, temperature 0.6, top_k 20, top_p 0.95, max_length 40960, past_present_share_buffer true` |
| `eos_token_id` | `[128001, 128008, 128009]` (an **array**) | `[151645, 151643]` (an **array**) |
| KV bytes/token @ 2 B/elem | **32,768** (32 KiB) | **114,688** (112 KiB) |
| Weights (`model.onnx` + `.data`) | 1,224,236,346 B = **1167.52 MiB** | 483,659,869 B = **461.25 MiB** |
| Bundle total (6 files) | 1,241,451,982 B (**1.241 GB** decimal) | 495,088,583 B (**495 MB** decimal) |
| SPDX | `LicenseRef-LLAMA-3.2-Community` | `Apache-2.0` |

Per-file sizes and digests, `paths-info` against those revisions, taking the **git-LFS `oid`** where present and hashing the rest by hand (SP2 adjustment 19's rule, reused):

| Preset | File | Bytes | SHA-256 | Source of digest |
|---|---|---|---|---|
| Llama | `model.onnx` | 141,626 | `742f2292f396bbc65a511c361e37e1b1bcef40ebb38003aab38461549e7493a4` | `lfs.oid` |
| Llama | `model.onnx.data` | 1,224,094,720 | `ab128a60ea1be913b37f91ab305c059cf36b88383d7e9b907bfcaf0460b859bd` | `lfs.oid` |
| Llama | `tokenizer.json` | 17,209,920 | `6b9e4e7fb171f92fd137b777cc2714bf87d11576700a1dcd7a399e7bbe39537b` | `lfs.oid` |
| Llama | `genai_config.json` | 1,536 | `359d47db9f4e3626bd51a4b5ccf839489dfbe099e46e039dc63c0ca0118f4f1c` | downloaded + hashed |
| Llama | `tokenizer_config.json` | 353 | `502d3a27e52540463d85a947099459fb0f8aea88dd1cf9889368576de89dd668` | downloaded + hashed |
| Llama | `chat_template.jinja` | 3,827 | `5816fce10444e03c2e9ee1ef8a4a1ea61ae7e69e438613f3b17b69d0426223a4` | downloaded + hashed |
| Qwen | `model.onnx` | 331,869 | `e9208ab7164ea6da0206eec2a2f0996a24e75d164f85823b02111f23046aab89` | `lfs.oid` |
| Qwen | `model.onnx.data` | 483,328,000 | `52640ca0d65e00d33dfb10b822c6a41e31bab1aaa6a49457b1dc5952c0dab0fb` | `lfs.oid` |
| Qwen | `tokenizer.json` | 11,422,650 | `be75606093db2094d7cd20f3c2f385c212750648bd6ea4fb2bf507a6a4c55506` | `lfs.oid` |
| Qwen | `genai_config.json` | 1,520 | `67348085a5b994a6b6d94e56e395e675d8ef4732c3c46ff75de30a541489fd3f` | downloaded + hashed |
| Qwen | `tokenizer_config.json` | 376 | `c56d783875898cebcf53c8797b61137efd52f1415be7531823b56f216f444527` | downloaded + hashed |
| Qwen | `chat_template.jinja` | 4,168 | `a55ee1b1660128b7098723e0abcd92caa0788061051c62d51cbe87d9cf1974d8` | downloaded + hashed |

### The budget arithmetic, computed from those numbers

`WorkspaceBytes` 192 MiB, `ReserveBytes` 192 MiB, `KvCacheBytesPerElement` 2, no sliding window:

| Rung | Llama KV | Llama `required` | Qwen KV | Qwen `required` |
|---|---|---|---|---|
| 4096 | 128.00 MiB | **1679.52 MiB** | 448.00 MiB | **1293.25 MiB** |
| 3072 | 96.00 MiB | 1647.52 MiB | 336.00 MiB | 1181.25 MiB |
| 2048 | 64.00 MiB | 1615.52 MiB | 224.00 MiB | 1069.25 MiB |
| 1536 | 48.00 MiB | 1599.52 MiB | 168.00 MiB | 1013.25 MiB |
| 1024 | 32.00 MiB | 1583.52 MiB | 112.00 MiB | 957.25 MiB |

§9.3's Llama table (1679 → 1583 MiB, ~96 MiB / 6% of leverage) is **exact**. Llama's measured 1280 MiB + 192 MiB reserve = 1472 MiB, below 1679.52, so `PreferMeasuredPeak` is inert for it — as §6.2 says. Qwen's ladder buys **336 MiB, 26%**, which is the KV-dominated shape §9.3 says the ladder exists for, now with numbers instead of an adjective.

And the jetsam scenario is no longer hypothetical: Qwen's declared `context_length` is 40960, so a generator built with no `max_length` allocates **4,480 MiB (4.375 GiB)** of KV cache on a phone. That is decision 1's defect, priced.

### Tier-2 fixture toolchain — resolves on this box

`uv venv --python 3.14` then a dry-run install of `onnxruntime-genai==0.15.2`, `onnxruntime==1.30.0`, `onnx==1.22.0`, `numpy`, `torch`, `transformers`, `sentencepiece` resolves **42 packages** for cp314 / win-x64: onnxruntime-genai 0.15.2, torch **2.14.0**, transformers **5.17.0**, tokenizers 0.23.2, numpy 2.5.3, protobuf 7.36.1, safetensors 0.8.0. `onnxruntime_genai.models.builder` is therefore runnable here. Whether the builder accepts a hand-authored 256-entry BPE `tokenizer.json` is Task 1.3's question, with §16.2's two written contingencies.

### Apple compilation on this Windows host — SP2's open question, CLOSED for SP4

`dotnet build embeddings\src\Qavren.Edge.Onnx\Qavren.Edge.Onnx.csproj -c Release -f net10.0-ios` **succeeds** on this box, and the four-TFM GenAI probe library builds for `net10.0`, `net10.0-android`, `net10.0-ios` and `net10.0-maccatalyst`. So every SP4 Apple leg is **compiled** locally; only linking, packaging and execution are CI's.

**Consequences.** Every host-lane verify in this plan runs here. Apple/Android device execution, every workflow run, the tier-3 nightly model lane and the §19 size measurements are CI-only (see **CI-only work**). Implementers **must not run git**. **Never use `cd`** in a shell command — it wipes PATH in this environment. PowerShell is the primary shell and every path is absolute.

---

## Spec adjustments

Every place this plan departs from the approved spec is enumerated here — whether because verified research contradicted the spec (**the facts win**) or because the spec's own wording is unimplementable as written and the plan does the nearest correct thing. Each names the task that implements it. If a reader diffing the spec against the plan finds a difference that is **not** in this list, that is a bug in the plan.

1. **`genAiVersion` cannot come from the assembly, and comes from the CPM pin instead.** §14.3 publishes `genAiVersion` as a diagnostics key and §6.8's `ChatBackendReport.GenAiVersion` carries it. Measured: `Microsoft.ML.OnnxRuntimeGenAI.dll` reports `AssemblyVersion` **0.0.0.0**, `FileVersion` **0.0.0.0**, and has no `AssemblyInformationalVersionAttribute`; there is no version API anywhere on the managed surface. SP2's `ortAssembly.GetName().Version` trick has no analogue. `Qavren.Edge.Chat.Onnx.csproj` therefore emits the pin as assembly metadata, read straight out of the CPM item that is the single source of truth (verified: `@(PackageVersion)` items are visible to every project, since `Directory.Packages.props` is auto-imported):

    ```xml
    <ItemGroup>
      <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
        <_Parameter1>OnnxRuntimeGenAIVersion</_Parameter1>
        <_Parameter2>@(PackageVersion->WithMetadataValue('Identity','Microsoft.ML.OnnxRuntimeGenAI')->'%(Version)')</_Parameter2>
      </AssemblyAttribute>
    </ItemGroup>
    ```

    A tier-1 test asserts the key reads `0.15.2`, so a CPM bump that forgets the README is loud. (Tasks 1.1, 4.1.)

2. **`genAiManagedAsset` cannot be a `TargetFrameworkAttribute`, and becomes an MVID plus a reference signature.** §14.3 defines the key as "the `TargetFrameworkAttribute` of the loaded `.Managed` assembly". Measured: **none of the five `lib/` assets carries one**. Worse, the iOS and Mac Catalyst assets have identical referenced-assembly sets, so references alone cannot separate the two the key exists to distinguish. The key is therefore published as `"<mvid> (System.Runtime/<v>[, android])"`, and the five MVIDs are in Environment ground truth and in `chat/README.md` so a reader can look one up. A tier-1 test asserts the `net10.0` host leg reports `324d5b97-7f06-44d8-b891-c7c016bf320a`. (Tasks 1.2, 4.1.)

3. **`GeneratorParams.SetGuidance` takes three parameters.** §9.6 writes `SetGuidance("json_schema", "{…}")`; the real signature is `SetGuidance(string type, string data, bool enableFFTokens = false)`. The two-argument call compiles against the default. The probe passes two arguments and **never** passes `enableFFTokens: true` — fast-forward tokens change what is emitted, and a positive-control probe that changes the thing it measures is not a control. (Task 4.1.)

4. **`Qwen3_600MInt4`'s declared context is 40960, not a phone-shaped number, and §6.5's size figure is MiB wearing a "MB" label.** Measured: `context_length` 40960, weights 461.25 **MiB** (483,659,869 B), bundle 495 MB decimal / 472 MiB. §6.5 says "~461 MB"; that number is the weights in MiB, and the bundle a consent sheet shows is 495 MB decimal. The generated catalogue carries the measured bytes and the README quotes the bundle. The declared 40960 is consequential rather than cosmetic: §9.1 step 4's `min(requested, shape.ContextLength, configDeclaredContextLength)` is what keeps a 4,480 MiB KV allocation from ever being requested, and Task 3.1 asserts that clamp directly for this preset. (Tasks 2.2, 3.1, 1.2.)

5. **§19 item 2's assumption about Qwen3's geometry is confirmed exactly, and §6.5's qualitative paragraph becomes a number.** 28 layers × 8 KV heads × head size 128 → **112 KiB/token**, 3.5× Llama's 32 KiB, against a third of the weights. §9.3's "the ladder earns its keep on the other shape" is now a measured 26% versus Llama's 6%. No design changes; the warning paragraph gets the figures. (Tasks 2.2, 3.1, 1.2.)

6. **§19 item 4's second half is answered from the shipped configs: both presets take the static KV path.** Both `genai_config.json` files set `"past_present_share_buffer": true`, which is what selects `kv_cache.cpp`'s `shape_[2] = max_length;` allocation. So writing the budget's `resolvedContext` into `search.max_length` genuinely bounds the cache rather than bounding a ceiling a dynamic cache would grow past. The *first* half — whether `KvCacheBytesPerElement` is 2 or 4 — is still the nightly's job (§16.3), and the plan changes no default. (Tasks 2.2, 6.1.)

7. **§5's "seven files outside `chat/`" is nine.** §17 requires `Qavren.Edge.Rag` to join the existing `trim-smoke` console, which means `embeddings/tools/Qavren.Edge.TrimSmoke/Qavren.Edge.TrimSmoke.csproj` (one `ProjectReference`) and its `Program.cs` (one added segment). Neither is in §5's table. Both are a published console, not a shipped package, and neither alters a type, a member, a signature or a default in any SP1 or SP2 package — so the two-edit rule survives; the table was just incomplete. (Task 4.2.)

8. **`foundation/tests/Qavren.Edge.DeviceTests`'s Apple floor rises to 15.4 unconditionally, not contingently.** §5 and §16.4 leave it as "if the merged manifest or the deployment target demands it". It does: the device host is currently `ios 15.0` / `maccatalyst 15.0`, GenAI's managed asset group is `net9.0-ios15.4`, and its xcframework slices are built at that floor. Android is already 24.0 from SP2, so that half needs nothing. Making it conditional would mean shipping a plan whose wave-7 outcome is unknown; making it definite costs one line and removes a decision from an implementer who cannot run a Mac link. (Task 7.1.)

9. **`TextSpanAnnotatedRegion.StartIndex` and `EndIndex` are `int?`.** §4.3's consumer snippet slices with `region.EndIndex - region.StartIndex`, which does not compile against nullables without a null check. `RagCitations.Build` always sets both, so the README's snippet null-checks once and says why; nothing in the library changes. (Tasks 1.2, 3.2.)

10. **`AddChatClient` / `AddKeyedChatClient` are in `Microsoft.Extensions.DependencyInjection`, not `Microsoft.Extensions.AI`.** §6.9 implies the latter. One using directive; recorded so an implementer does not conclude the method is missing. (Task 4.1.)

11. **`IsAotCompatible=true` is already set repo-wide and SP4 must not set it again.** §17 says "`Qavren.Edge.Chat.Onnx` sets `IsAotCompatible=true`". `Directory.Build.targets` already applies it to every `IsPackable` project on a net8.0-compatible TFM. Re-declaring it in the csproj would be a redundant line that hides where the policy lives. What SP4 *does* owe is the consequence: the build must be clean of IL2026/IL3050 with `TreatWarningsAsErrors`, and GenAI carries no trim annotations. If the two packages cannot be made clean without suppressing a real warning, the documented response is to record the suppression in ADR 0009 with the call site, never to silence the analyzer wholesale. (Tasks 1.1, 3.1, 4.1, 5.1.)

12. **Every verify command is `dotnet run --project <test csproj> -c Release -f net10.0 -p:TargetFrameworks=net10.0`, never `dotnet test`.** SP1 adjustment 36 / SP2 adjustment 12, inherited unchanged: the test projects multi-target for the device host, `dotnet run` refuses to pick a TFM, and `-f` alone does not stop restore walking the full `TargetFrameworks` list of the project *and everything it references*. `Qavren.Edge.Rag.Tests` is single-TFM, so it takes `-p:TargetFrameworks=net10.0` and no `-f`. **In a parallel phase every command also carries `-p:ArtifactsPath=D:\Local\Temp\qedge-sp4\w<wave>\t<task>`**; the close re-runs them with that switch removed. **Exception — a device-TFM build carries `-f <tfm>` and NO `-p:TargetFrameworks`**, because that global property would force every `net10.0`-only project in the closure (`Qavren.Edge.Rag`, `Qavren.Edge.VectorData`, `Qavren.Edge.Core`) to restore at a TFM it does not have. (Every task; the exception is Task 7.1.)

13. **`chat/tools/model-hashes/` and `chat/tests/fixtures/` are wave-1/wave-2 work whose output lands in a project a later wave compiles, and the plan states the consequence.** `ChatPresets.g.cs` is written by Task 2.2 into `chat/src/Qavren.Edge.Chat.Onnx/`, a project nothing in wave 2 builds. **`dotnet build QavrenEdge.slnx` therefore fails between Task 2.2 and Task 3.1**, exactly as SP2's `EmbeddingPresets.g.cs` did; the first compile of the generated file is Task 3.1's, and wave 2's close runs `dotnet restore` and the two test suites, never a solution build. (Tasks 2.2, 2.4, 3.1.)

14. **`Qavren.Edge.Chat.Tests` gains its `Qavren.Edge.Rag` `ProjectReference` in wave 6, not in the skeleton.** §16.1 requires the cross-package literal assertion (`ChatHistoryOptions.PinnedMessageKeys`'s default equals `RagCitations.ContextMessagePropertyKey`) "in the chat test project", which means that project must see both packages. Adding the edge in Task 1.1 would put `Qavren.Edge.Rag` in `Qavren.Edge.Chat.Tests`'s build graph from wave 3 onward, and wave 3 has one task *writing* `Qavren.Edge.Rag` while another builds `Qavren.Edge.Chat.Tests`. The edge is created by the task that needs it, and wave 6's close is where it is proved in the real tree — SP2's Task 5.3 pattern, copied. (Tasks 1.1, 6.1.)

15. **The preset defaults are the model's own shipped `search` values, not `ChatPreset`'s type defaults.** §6.5 declares `DefaultTemperature = 0.7f`, `DefaultTopP = 0.9f`, `DefaultTopK = 50` as the *record's* defaults, which is correct for the type. The generator emits each preset's shipped values instead — Llama `0.6 / 0.9 / 50`, Qwen `0.6 / 0.95 / 20` — because a preset that silently samples differently from the publisher's own `genai_config.json` is a quality regression nobody will attribute. The type defaults stay exactly as §6.5 declares them, for a preset somebody writes by hand. (Task 2.2.)

16. **ADRs live under `chat/docs/adr/` and continue the numbering.** SP1 holds 0001–0002 in `foundation/docs/adr/`, SP2 holds 0003–0008 in `embeddings/docs/adr/`; SP4 writes **0009–0013** in its own tree, matching §19 item 13's numbers exactly. (Task 1.2.)

17. **`foundation/docs/errors.md` exists and is amended, not created.** SP2 created it covering 1001–4001 and 5000–5299. SP4 appends a sub-project 4 section and two lines to the range list at the top; the 6000–6299 reservation for SP3 is recorded there because §5.2 says SP4 is the first sub-project in a position to notice. (Task 1.2.)

18. **Branch hygiene is Task 1.1 Step 0 and is not optional.** §17's closing paragraph asks for it; the measurement in Environment ground truth confirms `a3a3a2c` and `ab5fb26` are both absent from `feat/sp4-chat`. They are cherry-picked before `ci.yml` is touched, or Task 2.3 writes assertions against a workflow two fixes behind. (Task 1.1.)

19. **`Qavren.Edge.Rag.Tests` never references `Qavren.Edge.VectorData`, and that is an assertion rather than an omission.** §7's whole layering claim is that the RAG recipe runs over MEVD abstractions. The fake collection the retriever tests drive is written against `Microsoft.Extensions.VectorData.Abstractions` alone, which is what makes "point it at Qdrant and it behaves identically" a property of the test project rather than a sentence in a README. (Tasks 1.1, 3.2.)

20. **Every wave gains a close task, and wave 1 gains a prologue.** SP2 adjustment 25, inherited for the same two reasons. (Tasks 1.1, 1.6, 2.4, 3.3, 4.3, 5.2, 6.2, 7.3.)

21. **Tier 0 is Task 1.5 — the end of wave 1 — and not literally "the first task of the plan", because §16.0 contradicts itself.** §16.0 and §19 item 1 both require the five-target link-and-generate smoke to run before any SP4 code, and §16.0's own body makes "first" impossible: the smoke "loads the tier-2 fixture and generates one token", and the tier-2 fixture does not exist until **Task 1.3** builds it. Tier 0 therefore runs at the end of wave 1 — after the fixture, before one line of SP4 API — which is the property §16.0 actually cares about ("a TFM cut discovered after the API is written is a rewrite"). Four consequences, every one of them a task rather than a sentence:

    - **Task 1.5 owns the smoke.** One `net10.0` console (the Windows and Linux targets) and one MAUI device head targeting `net10.0-android;net10.0-ios;net10.0-maccatalyst;net10.0-windows10.0.19041.0`, both over the committed fixture, plus `.github/workflows/tier0-genai-smoke.yml` — six `workflow_dispatch` jobs, one per target, deliberately **outside** `ci.yml` so no tier-0 job can ever gate a PR.
    - **Wave 2 does not start until all six legs have reported.** Task 1.6 Step 3 pushes `feat/sp4-chat` and dispatches the workflow; Step 4 writes the three answers into `chat/README.md` and back into this section. That is the plan's one hard hand-off, and it is named rather than implied.
    - **The fallback is a literal edit** (Task 1.5 Step 6), not a contingency sentence: if 1(a) fails, `net10.0-maccatalyst` is deleted from `Qavren.Edge.Chat.Onnx.csproj`, `Qavren.Edge.Chat.Tests.csproj` and the tier-0 device head; `ci.yml`'s asset assertion and `assert-workflows.py` both drop their `lib/net9.0-maccatalyst14.0` row; `chat/README.md` states that Mac Catalyst has no chat in v1; and this list gains adjustment 21(a) recording it.
    - **What tier 0 does *not* close is stated rather than glossed.** §16.0 item 1(b) asks for "a packaged MAUI WinUI app", and this repo has **no signing identity** — `Qavren.Edge.DeviceTests` sets `WindowsPackageType=None` for exactly that reason. Tier 0 therefore proves the **unpackaged** WinUI resolution path and nothing about MSIX, and the README says that in those words.

    Answers **(b)-on-Windows-`net10.0`** and **(c)** are already closed on this box (Environment ground truth), and Task 1.5 Step 4 re-closes them over a real model load and a real generated token rather than an `OgaHandle` alone. (Tasks 1.3, 1.5, 1.6, 1.2, 2.3.)

22. **§15.3's `RagCollectionNotSearchable` (7203) has no reachable raise site in §7, so the plan adds one option rather than inheriting the ambiguity.** §15.3 reads "Hybrid requested, collection is not `IKeywordHybridSearchable<TRecord>` → `RagCollectionNotSearchable` (7203); or the vector lane when `PreferHybridSearch` left it optional" — but §7's `VectorStoreRetrieverOptions<TRecord>` carries only `PreferHybridSearch` (default **true**), whose documented meaning is "use the hybrid lane *when* the collection implements it and keywords are present", and §11 says the retriever takes "the vector lane otherwise". There is no "require hybrid" switch anywhere in the spec, so under §7 as declared 7203 can never be thrown — and §16.1's "every 7000-range member is thrown by at least one test" is then unsatisfiable by construction. The plan adds exactly one property to SP4's own options class:

    ```csharp
    /// <summary>
    /// Refuse rather than silently fall back. When true and the collection does not implement
    /// <c>IKeywordHybridSearchable&lt;TRecord&gt;</c>, retrieval throws
    /// <see cref="EdgeErrorCode.RagCollectionNotSearchable"/> (7203) naming the collection's CLR
    /// type and <see cref="PreferHybridSearch"/>, instead of taking the vector lane. Default
    /// <see langword="false"/>, which is section 7's declared behaviour unchanged:
    /// <see cref="PreferHybridSearch"/> on its own stays advisory, and an app that never sets this
    /// sees exactly the fallback section 11 describes.
    /// </summary>
    public bool RequireHybridSearch { get; set; }
    ```

    Deleting 7203 was the alternative and is worse: it is already enumerated in §15.1, written into `foundation/docs/errors.md` by Task 1.2 and into SP1's `EdgeErrorCode` by Task 1.4, and an app that points a hybrid-shaped retrieval at a keyword-less collection today gets a silently worse ranking rather than a diagnosable error. The refusal fires **only** on an explicit opt-in, so no existing behaviour moves. (Tasks 1.4, 3.2.)

23. **§16.1's error-code coverage assertion splits per assembly, because one test cannot observe a throw in another test process.** The draft phrased it as "every 7000-range member is thrown by at least one test in this project **or** `Qavren.Edge.Rag.Tests`", which is not implementable: those are two assemblies in two processes with no shared state, so the Chat suite has no way to know what the Rag suite threw. The rule becomes two tests, each a table the enum itself forces to be complete:

    - `ChatErrorCodeCoverageTests` in `Qavren.Edge.Chat.Tests`, over **7001–7108** (20 codes: 7001–7009, 7051–7053, 7101–7108);
    - `RagErrorCodeCoverageTests` in `Qavren.Edge.Rag.Tests`, over **7201–7204** (4 codes);

    each holding a `IReadOnlyDictionary<EdgeErrorCode, Func<Task>>` whose every entry is the minimal reproduction that raises that code — asserted by awaiting it, catching `EdgeException` and comparing `.ErrorCode` — plus a guard asserting the dictionary's key set is **exactly** `Enum.GetValues<EdgeErrorCode>()` filtered to that assembly's range. A code added to the enum later therefore turns that guard red rather than passing silently. The `foundation/docs/errors.md` anchor test stays whole and lives in `Qavren.Edge.Chat.Tests` over all 24 codes: it reads a file, not a throw, so the split does not apply to it. (Tasks 3.2, 5.1.)

24. **The tier-1 `genai_config.json` fixtures are generated and digest-checked, never hand-retyped.** §16.1 asks for "one committed text fixture: a ~700-byte `genai_config.json` as a `const string`". SP4 needs **two** — §16.1's own shape assertions name Qwen's 40960 context and Llama's 4096 — and a hand-retyped config that drifts from the shipped one makes every shape assertion assert the wrong thing, silently and forever. `fetch_chat_model_hashes.py` already downloads both files in order to hash them, so it emits a second output, `chat/tests/fixtures/GenAiConfigFixtures.g.cs`, carrying each body verbatim as a raw-string `const` beside its SHA-256 and its byte count as `const`s. Two mechanics make that exact rather than approximate:

    - the generator **asserts the downloaded bytes contain no `\r` and no `"""` sequence** before emitting, and fails loudly if either appears — the first is what makes a raw-string literal byte-faithful across a git checkout that might normalise line endings, the second is what makes the literal unambiguous;
    - the tier-1 test hashes `Encoding.UTF8.GetBytes(body.ReplaceLineEndings("\n"))` and asserts it equals `359d47db9f4e3626bd51a4b5ccf839489dfbe099e46e039dc63c0ca0118f4f1c` (Llama, 1,536 bytes) and `67348085a5b994a6b6d94e56e395e675d8ef4732c3c46ff75de30a541489fd3f` (Qwen, 1,520 bytes) — **the same digests `ChatPresets.g.cs` pins for provisioning**, so the fixture the shape parser is tested against and the file the provisioner will verify on a user's device cannot drift apart.

    (Tasks 2.2, 3.1.)

25. **§16.2's "byte counts asserted so an onnx or builder upgrade that moves them is loud" becomes a test, not a doc comment.** The draft put the byte counts in XML doc comments on the base64 consts and asserted only the 1 MB source cap plus a by-hand regeneration diff — and CI never runs the generator, so nothing anywhere would notice a moved byte count. `make_tiny_chat_model.py` therefore emits each file's length as a `const int` beside its base64 (`GenAiConfigJsonLength`, `ModelOnnxLength`, `TokenizerJsonLength`, `TokenizerConfigJsonLength`), and `Tier2/TinyChatModelFixtureTests` — which runs on every PR, every host and every device lane — asserts that each materialised file's on-disk length equals its `const`, that every base64 decode round-trips, and that the four lengths sum under the cap. That is the "loud" half, and it is the half that only exists if a *test* owns it. (Tasks 1.3, 6.1.)

26. **The tier-3 corpus is specified here, because "a committed 20-chunk corpus" is not a deterministic assertion.** §16.3 requires "every `[n]` marker in the answer maps to a retrieved chunk, and the answer contains a required token from the gold chunk" and names no question, no gold chunk, no token and no seed — the four things that make it deterministic. `Tier3/corpus.json` is a committed array of 20 objects `{ "id", "title", "text" }` describing a fictional appliance warranty, written so that exactly one chunk answers the question and no other chunk contains the required token:

    - **question**: `"How many years does the warranty cover the compressor?"`
    - **gold chunk**: `id = "warranty-compressor"`, whose text contains `"the sealed compressor is covered for seven years from the date of purchase"`;
    - **required token**: `"seven"`, matched case-insensitively, **or** the digit `"7"` — a 0.6B model rendering the number as a digit is a correct answer and must not read as a regression;
    - **decoding**: `do_sample = false`, `random_seed = 20260911`, `MaxOutputTokens = 96`, `RagOptions.Top = 5`, `MaxCharsPerSource = 1200`.

    Three assertions: every `[n]` in the answer is within the retrieved-source count and resolves to one of them; the gold chunk is among the five retrieved; the answer contains the required token. All three are hard. **If the third proves flaky across the first week of nightlies**, the documented response is to demote it to a recorded metric printed in the job summary and say so in `chat/README.md` — never to delete it quietly.

    **The corpus's defining invariant is the exclusivity clause, and prose cannot enforce it.** "No other chunk in the corpus contains either token" is what makes the third assertion mean *the model read the gold chunk* rather than *the model emitted a common word*. An edit that adds the word "seven" or a bare `7` to any of the other nineteen chunks makes that assertion pass vacuously, forever, with every nightly green. Two mechanics close it, and the plan does **both** rather than choosing:

    - **The twenty chunks are written literally in Task 6.1**, so `corpus.json` is an artifact this plan originates rather than one an implementer improvises against a description — which is what the **Convention for "literal code"** already requires of everything else the plan originates;
    - **`Tier3/CorpusInvariantTests` is a tier-1 test, not a tier-3 fact.** It reads the committed `corpus.json`, needs no model, no `SkipUnless` and no environment variable, and runs on **every PR, every host and every device lane**. It asserts exactly 20 chunks, unique non-empty ids, the gold id present and carrying the gold sentence verbatim, and — the clause that matters — that **exactly one** chunk matches `seven|\b7\b` case-insensitively across its `title` **and** its `text` concatenated, naming every offending id in the failure message. A corpus edit that breaks the invariant turns that test red in the PR that makes it, three months before the nightly it would otherwise have silently disarmed.

    (Task 6.1.)

27. **§14.4's Trace-only privacy rule gets an owner and a test.** "Prompt and completion text are logged at `Trace` **only** and never above … because a RAG prompt contains the user's private corpus" is a privacy contract, and the draft referenced §14.4 only for `LoggerMessage.Define` and the event-id ranges — so no task stated the rule and no test asserted it. Two owners, because two packages log text: `EdgeChatClient` and the turn pipeline (Task 5.1), and `RagChatClient` / `RagPrompts` / `RagCitations` (Task 3.2). Every `LoggerMessage.Define` in either package that carries a prompt, a message body, a retrieved chunk or a completion is declared at `LogLevel.Trace`, and each test project gains a `LoggingPrivacyTests` that drives a full turn through a recording `ILogger`: at `Trace` the distinctive prompt and answer strings appear **only** in `Trace` records; at `Debug` neither string appears in any record at all. (Tasks 3.2, 5.1, 1.2, 8.1.)

28. **§19 item 12 is a named tier-3 test, not something the nightly "can" do.** The item reads "Load a real preset, call `Overlay({\"search\":{\"max_length\":N}})` once, and read back whether the model's shipped `do_sample`, `temperature`, `top_k` and `top_p` survived — deep merge — or were replaced. Record the answer." Open risk 7 phrased that as an option; an option is not a task. `Tier3/ConfigOverlaySemanticsFacts` loads the Qwen preset's real `Config`, calls `Overlay` with exactly that one-key document, builds a `GeneratorParams` from it and reads all four values back through `GetSearchBool`/`GetSearchNumber`, printing `overlayMergeSemantics = deep-merge | replace` into the job summary and into the lane's artifact. It asserts **nothing** about which answer comes back — both are legal, and §9.1 composes one document precisely so neither matters — and fails only if the read itself throws. Open risk 7 is rewritten to point at it. (Task 6.1.)

29. **`session_options` lives under `model.decoder`, and `chatIntraOpNumThreads` gets its own source-generated reader rather than a field on `ChatModelShape`.** §14.3 publishes `chatIntraOpNumThreads` "read from the resolved `genai_config.json`'s `session_options`" and no task in the draft owned that read — which is precisely how a diagnostics key gets written as a stub, or how a second, reflection-based JSON parse gets bolted onto a load path whose whole point is that it is trim-safe. Two facts settle where it goes:

    - **`session_options` is not a top-level key.** In an ORT GenAI `genai_config.json` the top level is `model` and `search`, and the block sits at **`model.decoder.session_options`**, beside `filename`, `head_size`, `num_hidden_layers` and `num_key_value_heads`. §14.3's phrasing is shorthand; `model.decoder.session_options.intra_op_num_threads` is the path, and the reader hard-codes it.
    - **It is not geometry, so it does not belong on `ChatModelShape`.** Every field §6.2 declares is cross-checked field by field against the provisioned config, and a disagreement is `ChatModelShapeMismatch` (7008). A thread count is a runtime hint a publisher may change between revisions without changing the model, so cross-checking it would turn a harmless republish into a 7008 — and a preset would have to *declare* a thread count it has no business declaring. `ChatModelShape`'s source-generated context therefore continues to parse geometry only, and **that omission is deliberate rather than an oversight**: Task 3.1 states it in those words, so the next reader does not "fix" it.

    The owner is **Task 4.1's `Internal\ChatSessionOptionsReader.cs`**: an `internal static` reader over its own `[JsonSerializable]` `ChatSessionOptionsJsonContext`, source-generated exactly as `ChatModelShape`'s is — no reflection anywhere on the load path — returning `int?`: the declared value, or `null` when `session_options` is absent, when `intra_op_num_threads` is absent, or when it is not a number. It reads the **same config text the load path already has in hand**, once, and the value is carried on `ChatModelInfo` beside the shape rather than re-parsed for the diagnostics report. The contributor publishes `chatIntraOpNumThreads` as that value, or the literal string `"(not declared)"` when it is null — **never `0`**, which is ORT's "pick for me" sentinel and would read as a real answer. `DiagnosticsTests` asserts four arms against literal config bodies: a `model.decoder.session_options` declaring `4` reads `4`; a `model.decoder` with no `session_options` reads `"(not declared)"`; a `session_options` with no `intra_op_num_threads` reads `"(not declared)"`; and — the arm that would catch the **path** being wrong rather than the parser — both real bodies from `GenAiConfigFixtures` are read without throwing and return either `null` or a positive integer, whichever the two publishers actually shipped. (Tasks 4.1, 3.1.)

30. **One named carve-out to the integrator's root-file ownership: `.github/workflows/tier0-genai-smoke.yml`.** The **Branch and commit model** states that an implementer wanting to edit a root file "has found a plan bug, not a licence" — and Task 1.5 is an implementer task that creates a file under `.github/**`. Under the rule as written that is a silent violation, and a silent violation is the precedent a later wave cites to justify a real race. The rule is amended in the same breath rather than bent: the tier-0 workflow is **delegated by name**, on four checked conditions — a new file at a path no other task touches, a throwaway deleted in the closing PR, `workflow_dispatch`-only and outside `ci-gate`'s `needs` so it can gate nothing, and a **serial interlude** so there is no concurrent writer in `.github/**` at all. Wave 1 does contain two tasks that touch `.github/**` — Task 1.1's cherry-picks of `ci.yml` and Task 1.5's new file — but they sit in **different serial phases** on **disjoint paths**, so what was missing was the statement, not the safety. Task 1.6's close reviews the delegated file as part of the wave it commits, and Task 8.1 Step 6 asserts that no *second* carve-out appeared. (Tasks 1.1, 1.5, 1.6, 8.1.)

---

## File structure

```
qavren-edge-sp4/                                   # the worktree; branch feat/sp4-chat
  QavrenEdge.slnx                                  # T1.1 - four project entries appended
  Directory.Packages.props                         # T1.1 - ONE PackageVersion appended
  .gitignore                                       # T1.1 - two toolchain venvs
  .github/workflows/ci.yml                         # T1.1 (cherry-picks), T2.3 (two steps, two asserts, nightly job)
  .github/workflows/tier0-genai-smoke.yml          # T1.5 - six workflow_dispatch jobs, OUTSIDE ci.yml
  foundation/
    docs/errors.md                                 # T1.2 - AMENDED; 7000-7299 + the 6000-6299 reservation
    src/Qavren.Edge.Core/EdgeErrorCode.cs          # T1.4 - AMENDED (the one SP1 library edit)
    tests/Qavren.Edge.Core.Tests/                  # T1.4 - one added test
    tests/Qavren.Edge.DeviceTests/                 # T7.1 - one ProjectReference + the Apple floor
    samples/Qavren.Edge.Sample/                    # T7.2 - Chat + Ask pages, iOS entitlements
    tools/ci-checks/assert-workflows.py            # T2.3 - five added rules
  embeddings/
    docs/.../2026-09-11-sp2-...-design.md          # T1.2 - one sentence corrected (spec 8.2)
    tools/Qavren.Edge.TrimSmoke/                   # T4.2 - one ProjectReference + one Program.cs segment
  chat/
    README.md                                      # T1.2
    docs/adr/0009..0013-*.md                       # T1.2
    docs/superpowers/specs/                        # the approved SP4 spec
    docs/superpowers/plans/                        # this file
    src/
      Qavren.Edge.Chat.Onnx/                       # T1.1 csproj; T3.1 pure half; T4.1 hosting; T5.1 the client
        Internal/ChatSessionOptionsReader.cs       # T4.1 - model.decoder.session_options, source-generated (adj. 29)
        Platforms/Android/ , Platforms/iOS/        # T3.1 - device profile only
        ChatPresets.g.cs                           # T2.2 - generated, committed, literal digests
      Qavren.Edge.Rag/                             # T1.1 csproj; T2.1 types+prompts; T3.2 middleware
    tests/
      fixtures/make_tiny_chat_model.py             # T1.3 - regeneration path, NEVER a build step
      fixtures/requirements.txt                    # T1.3
      fixtures/TinyChatModel.g.cs                  # T1.3 - generated, committed, base64 + length consts
      fixtures/GenAiConfigFixtures.g.cs            # T2.2 - generated, committed, two raw-string configs
      Qavren.Edge.Chat.Tests/                      # T1.1 csproj; T3.1; T4.1; T5.1; T6.1
        Fakes/ , Tier2/ , Tier3/ , Platforms/      # T3.1 / T6.1
        Tier3/corpus.json                          # T6.1 - 20 LITERAL chunks, gold = warranty-compressor
        Tier3/CorpusInvariantTests.cs              # T6.1 - tier-1: exactly ONE chunk matches /seven|\b7\b/i
      Qavren.Edge.Rag.Tests/                       # T1.1 csproj; T2.1; T3.2
    tools/model-hashes/fetch_chat_model_hashes.py  # T2.2 - regeneration path, NEVER a build step
    tools/model-hashes/requirements.txt            # T2.2
    tools/tier0-genai-smoke/                       # T1.5 - THROWAWAY, not in the .slnx
      Qavren.Edge.Tier0Smoke/                      #        net10.0 console: Windows + Linux legs
      Qavren.Edge.Tier0Smoke.Device/               #        MAUI head: android/ios/maccatalyst/windows
      Tier0Smoke.cs                                #        the one shared smoke body, linked by both
```

Scratch, never committed: `chat/tests/fixtures/.venv`, `chat/tests/fixtures/_scratch`, `chat/tools/model-hashes/.venv` — all three gitignored by Task 1.1.

## Wave map

Wave membership obeys **disjoint folders AND disjoint build graphs**, plus the two rules **Branch and commit model** adds:

- **A task that mutates a file every project imports cannot share a wave with a task that builds anything.** `Directory.Packages.props` is imported by `Qavren.Edge.Core`, so Task 1.1 is a **serial prologue**. The only later root-file edit is Task 2.3, and it touches `.github/**` and `foundation/tools/ci-checks/` only.
- **A task's verify writes `obj/` and `bin/` for every project in its graph, and MSBuild holds no cross-process lock on either.** The "build graph" column is also a *write* set; any overlap is resolved by `-p:ArtifactsPath=D:\Local\Temp\qedge-sp4\w<wave>\t<task>`, never by hoping the timing works out.

Each wave: **prologue (if any) → parallel phase, at most three implementers → close**. The next wave starts only after the close has re-verified the whole wave sequentially and committed it.

| Wave | Phase | Tasks | Folders each task owns | Build graph each task compiles (= its write set) | Parallel-safe because |
|---|---|---|---|---|---|
| 1 | prologue | **1.1** branch hygiene + CPM + slnx + `.gitignore` + four csprojs *(integrator)* | root files + every SP4 csproj + `.github/**` (cherry-picks only) | **restore only** — but it rewrites `Directory.Packages.props` and `QavrenEdge.slnx`, which every project reads | it runs **alone**; nothing else in wave 1 may be running |
| 1 | parallel | **1.2** README/ADRs/`errors.md`/SP2 correction; **1.3** tier-2 fixture toolchain; **1.4** SP1 edit — `EdgeErrorCode` | `chat/README.md` + `chat/docs/adr` + `foundation/docs/errors.md` + one SP2 spec file / `chat/tests/fixtures` / `foundation/src/Qavren.Edge.Core` + `…Core.Tests` | none / none (python) / Core + Core.Tests | 1.2 and 1.3 compile nothing, so 1.4 is the only writer of any `obj/`; the CPM file is frozen by the prologue |
| 1 | **serial interlude — one implementer** | **1.5** tier 0 — the five-target link-and-generate smoke and its dispatch workflow | `chat/tools/tier0-genai-smoke` + `.github/workflows/tier0-genai-smoke.yml` **(the one delegated root file — adjustment 30)** | Tier0Smoke console + Tier0Smoke.Device (GenAI + MAUI heads; **no SP4 package**, no `Qavren.Edge.*` reference at all) | **it runs alone, and it runs after 1.3 — Step 0 asserts that rather than trusting it.** §16.0's smoke `<Compile Include>`s `chat/tests/fixtures/TinyChatModel.g.cs` by relative path, so it cannot precede the task that generates it (adjustment 21); starting it beside 1.3 yields `CS2001` on a half-written generated file, which reads as a broken csproj rather than a scheduling error. A MAUI head staging a 21.5 MB AAR is also not something to run beside another build. The `.github/**` file is a **named carve-out**, disjoint from Task 1.1's `ci.yml` cherry-picks and in a different serial phase |
| 1 | close | **1.6** *(integrator)* | none | Core.Tests, sequential, **then the tier-0 dispatch and its six answers** | it is the only thing running; wave 2 does not start until the six legs report |
| 2 | parallel | **2.1** `Qavren.Edge.Rag` part 1; **2.2** preset catalogue — script + `ChatPresets.g.cs`; **2.3** `ci.yml` + `assert-workflows.py` *(integrator)* | `chat/src/Qavren.Edge.Rag` + `chat/tests/…Rag.Tests` / `chat/tools/model-hashes` + the single file `chat/src/…Chat.Onnx/ChatPresets.g.cs` / `.github/workflows` + `foundation/tools/ci-checks` | Rag → Core / **none** (python + one generated `.cs` no wave-2 build compiles) / none (YAML + python) | Core is frozen after wave 1; 2.2 and 2.3 compile nothing, so 2.1 is the only `obj/` writer. **The tree does not `dotnet build` as a solution between 2.2 and 3.1 — adjustment 13** |
| 2 | close | **2.4** *(integrator)* | none | Rag.Tests, the generator diff, the workflow contract, sequential — **no solution build** | — |
| 3 | parallel | **3.1** `Qavren.Edge.Chat.Onnx` part 1 — ids, shape, device profile, budget, presets, options, reducer; **3.2** `Qavren.Edge.Rag` part 2 — retriever, `RagChatClient`, `UseRag`, extractive, registration | `chat/src/Qavren.Edge.Chat.Onnx` + `chat/tests/…Chat.Tests` / `chat/src/Qavren.Edge.Rag` + `chat/tests/…Rag.Tests` | Chat.Onnx → Onnx → Core / Rag → Core | SP2's `Qavren.Edge.Onnx` and SP1's Core are frozen; the two projects are disjoint and `Qavren.Edge.Chat.Tests` does **not** reference `Qavren.Edge.Rag` until wave 6 (adjustment 14); the shared Core `obj/` write is separated by `ArtifactsPath` |
| 3 | close | **3.3** *(integrator)* | none | Chat.Tests, Rag.Tests, sequential | — |
| 4 | parallel | **4.1** `Qavren.Edge.Chat.Onnx` part 2 — GenAI runtime, host, provisioning, probes, lifecycle, diagnostics, registration; **4.2** `trim-smoke` gains `Qavren.Edge.Rag` | `chat/src/Qavren.Edge.Chat.Onnx` + `chat/tests/…Chat.Tests` / `embeddings/tools/Qavren.Edge.TrimSmoke` | Chat.Onnx → **Onnx** → **Core** / TrimSmoke → VectorData + Embeddings.Onnx + Rag + Sqlite.Native → **Onnx** → **Core** | `Qavren.Edge.Rag` is frozen after wave 3, which is the only reason 4.2 can exist at all. **The two write sets overlap on `Qavren.Edge.Onnx` as well as `Qavren.Edge.Core`** — 4.2's closure reaches `Qavren.Edge.Embeddings.Onnx`, which references `Qavren.Edge.Onnx` — so **both** tasks carry `-p:ArtifactsPath` (4.1 → `w4\t41`, 4.2 → `w4\t42`) and neither writes the real tree. 4.2 additionally passes `-p:TargetFrameworks=net10.0 -p:TargetFramework=net10.0` globally, which would otherwise leave single-TFM `project.assets.json` / `nuget.g.props` behind for every multi-targeted project in that closure for Task 4.3's close to build over; the `ArtifactsPath` is what keeps that inside the scratch root |
| 4 | close | **4.3** *(integrator)* | none | Chat.Tests, the trim-smoke publish + run, sequential | this is where the new TrimSmoke→Rag edge is proved in the tree CI will use |
| 5 | **serial — one implementer** | **5.1** `Qavren.Edge.Chat.Onnx` part 3 — `EdgeChatClient`, the decode loop, the conversation cache, statistics, `GetService` | `chat/src/Qavren.Edge.Chat.Onnx` + `chat/tests/…Chat.Tests` | Chat.Onnx → Onnx → Core | **it runs alone.** It is one file-set in one project and the largest single body in the sub-project; there is no second task in this plan that does not touch either that project or its test project |
| 5 | close | **5.2** *(integrator)* | none | Chat.Tests, sequential | — |
| 6 | **serial — one implementer** | **6.1** tier 2 (real GenAI natives), tier 3 (nightly test half), device-lane assertions, the cross-package literal | `chat/tests/Qavren.Edge.Chat.Tests` (`Tier2/`, `Tier3/`, `Platforms/`) + its csproj | Chat.Tests → Chat.Onnx + **Rag** → Onnx → Core | all three bodies live in **one test project**, so a parallel split by sub-folder would have each build compiling the others' half-written files. It also creates the Chat.Tests→Rag edge (adjustment 14), which rewrites that project's assets graph |
| 6 | close | **6.2** *(integrator)* | none | Chat.Tests including the new Rag edge, sequential | — |
| 7 | **serial — one implementer at a time** | **7.1** device-host wiring, then **7.2** sample-app pages | `foundation/tests/…DeviceTests` → then `foundation/samples/…Sample` | DeviceTests → Maui + natives + Chat.Tests at platform TFMs / Sample → Maui + natives + both SP4 packages at the same TFMs | **they are not parallel-safe and are not run in parallel.** Two MAUI/platform builds over one shared reference closure, each staging the same native `.dll`s and now a 21.5 MB AAR, is the worst pairing available. SP2's wave 7, unchanged |
| 7 | close | **7.3** *(integrator)* | none | both platform heads, sequential | — |
| 8 | gate | **8.1** full-solution verification *(integrator)* | none (read-only) | the whole `.slnx` | runs alone; it is the gate that closes the plan |

---

## WAVE 1 — Skeleton, docs, fixtures, and the SP1 error range

### Task 1.1: SP4 skeleton — branch hygiene, CPM, `.slnx`, `.gitignore` and every csproj *(integrator; **wave-1 prologue — runs alone**)*

**Local-verifiable:** yes.

**Files:**
- Cherry-pick: `a3a3a2c`, `ab5fb26` (touch `.github/workflows/**`)
- Edit: `Directory.Packages.props`
- Edit: `QavrenEdge.slnx`
- Edit: `.gitignore`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\Qavren.Edge.Chat.Onnx.csproj`
- Create: `chat\src\Qavren.Edge.Rag\Qavren.Edge.Rag.csproj`
- Create: `chat\tests\Qavren.Edge.Chat.Tests\Qavren.Edge.Chat.Tests.csproj`
- Create: `chat\tests\Qavren.Edge.Rag.Tests\Qavren.Edge.Rag.Tests.csproj`

All paths are relative to `C:\Users\steve\projects\qavren-edge-sp4\`.

**Approach — read this before writing.** This task writes **only project files and root files**, and it is the wave-1 **prologue**: Tasks 1.2, 1.3 and 1.4 do not start until it finishes. Compiling nothing is *not* what makes a task safe to run beside another — this one rewrites `Directory.Packages.props`, which `Qavren.Edge.Core` imports and Task 1.4's verify builds. A stub csproj with no `.cs` files restores and later builds to an empty assembly; that is intended.

- [ ] **Step 0: Branch hygiene, before anything else**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
git -C $root fetch origin
git -C $root cherry-pick a3a3a2c ab5fb26
git -C $root log --oneline -3
```

Expected: two new commits on `feat/sp4-chat`. If either conflicts, resolve in favour of `origin/main`'s version — both are CI fixes SP4 has no opinion about. **This runs before `ci.yml` is read by anybody**, because Task 2.3 asserts against it.

- [ ] **Step 1: One package version**

Append to `Directory.Packages.props`, immediately before `</Project>`:

```xml
  <!-- Sub-project 4. Verified by restore AND build on 2026-09-11 (plan Environment ground truth):
       Microsoft.ML.OnnxRuntimeGenAI.Managed 0.15.2 resolves lib/net8.0 for net10.0 and
       lib/net9.0-{android31.0,ios15.4,maccatalyst14.0} for the three platform TFMs, and the NATIVE
       package resolves zero compile assets on every TFM. GenAI declares ORT >= 1.28.0 as a FLOOR,
       not a pin, so sub-project 2's 1.30.0 stands - ci.yml's ORT floor guard is what stops anyone
       "fixing" the skew by downgrading. Microsoft.ML.OnnxRuntimeGenAI.Managed is deliberately NOT
       pinned here: CentralPackageTransitivePinningEnabled is false precisely so a transitive is not
       promoted into the produced nuspec. -->
  <ItemGroup Label="SP4 runtime">
    <PackageVersion Include="Microsoft.ML.OnnxRuntimeGenAI" Version="0.15.2" />
  </ItemGroup>
```

- [ ] **Step 2: Ignore the two by-hand toolchains**

Append to `.gitignore`:

```gitignore
# Sub-project 4 fixture toolchain. The generator's OUTPUT (TinyChatModel.g.cs) is committed;
# its venv and any model directory it writes for debugging are not.
chat/tests/fixtures/.venv/
chat/tests/fixtures/_scratch/

# Sub-project 4 preset-hash toolchain. Same shape: ChatPresets.g.cs is committed, the venv is not.
# The script is run by hand and never by a build or by CI.
chat/tools/model-hashes/.venv/
```

- [ ] **Step 3: Four solution entries**

Append to `QavrenEdge.slnx`, immediately before `</Solution>`:

```xml
  <Folder Name="/chat/src/">
    <Project Path="chat/src/Qavren.Edge.Chat.Onnx/Qavren.Edge.Chat.Onnx.csproj" />
    <Project Path="chat/src/Qavren.Edge.Rag/Qavren.Edge.Rag.csproj" />
  </Folder>
  <Folder Name="/chat/tests/">
    <Project Path="chat/tests/Qavren.Edge.Chat.Tests/Qavren.Edge.Chat.Tests.csproj" />
    <Project Path="chat/tests/Qavren.Edge.Rag.Tests/Qavren.Edge.Rag.Tests.csproj" />
  </Folder>
```

- [ ] **Step 4: `Qavren.Edge.Chat.Onnx.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Four TFMs, not five. GenAI ships no Windows *platform* managed asset at all: a Windows
         consumer binds lib/net8.0 and takes runtimes/win-x64 or win-arm64 by RID, exactly as
         Qavren.Edge.Sqlite is already consumed. A fifth TFM would buy a build leg and nothing else.
         The multi-targeting that IS here exists for one reason: IEdgeChatDeviceProfile needs
         ActivityManager.MemoryInfo.TotalMem on Android and NSProcessInfo.PhysicalMemory on Apple,
         and the ABI check needs Android.OS.Build.SupportedAbis. -->
    <TargetFrameworks>net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst</TargetFrameworks>
    <!-- Restore evaluates EVERY entry of TargetFrameworks for every project in the graph, so the
         ios and maccatalyst SDK packs (which do not exist for linux-x64, NETSDK1178) have to be
         dropped on Linux hosts or the android device lane fails before a test can run. -->
    <TargetFrameworks Condition="$([MSBuild]::IsOSPlatform('Linux'))">net10.0;net10.0-android</TargetFrameworks>
    <IsPackable>true</IsPackable>
    <PackageId>Qavren.Edge.Chat.Onnx</PackageId>
    <Description>On-device chat for Qavren.Edge: a Microsoft.Extensions.AI IChatClient over ONNX Runtime GenAI, with a KV-cache-aware memory budget, consent-gated model provisioning, thermal-paced streaming and honest per-turn diagnostics.</Description>
    <RootNamespace>Qavren.Edge.Chat</RootNamespace>

    <!-- HIGHER than sub-project 2's 24.0 / 15.1 / 15.1, and these are GenAI's floors rather than
         ours. Android: the AAR's own manifest declares minSdkVersion 24 (measured), which .NET for
         Android merges into the app's - so 24.0 is unchanged from SP2.
         Apple: GenAI's managed asset group is net9.0-ios15.4 and its xcframework slices are built
         at that floor, so 15.1 would link a 15.4 slice into a lower deployment target. Mac Catalyst
         takes 15.4 too, not GenAI's nominal net9.0-maccatalyst14.0, because Catalyst resolves the
         iOS xcframework through the SDK RID graph and inherits whatever that slice was built at.
         SupportedOSPlatformVersion plays NO part in restore, so raising it adds no versioned TFM. -->
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'">24.0</SupportedOSPlatformVersion>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios'">15.4</SupportedOSPlatformVersion>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'maccatalyst'">15.4</SupportedOSPlatformVersion>
  </PropertyGroup>

  <!-- Plan adjustment 1. Microsoft.ML.OnnxRuntimeGenAI.dll reports AssemblyVersion 0.0.0.0,
       FileVersion 0.0.0.0 and carries no AssemblyInformationalVersionAttribute, and the managed
       surface has no version API - so spec 14.3's genAiVersion key cannot be read off the
       assembly. It is taken from the CPM item that is already the single source of truth. -->
  <ItemGroup>
    <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
      <_Parameter1>OnnxRuntimeGenAIVersion</_Parameter1>
      <_Parameter2>@(PackageVersion->WithMetadataValue('Identity','Microsoft.ML.OnnxRuntimeGenAI')->'%(Version)')</_Parameter2>
    </AssemblyAttribute>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.ML.OnnxRuntimeGenAI" />
    <PackageReference Include="Microsoft.Extensions.AI" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <ProjectReference Include="..\..\..\embeddings\src\Qavren.Edge.Onnx\Qavren.Edge.Onnx.csproj" />
  </ItemGroup>

  <!-- Same Platforms\** + GetTargetPlatformIdentifier pattern Qavren.Edge.Onnx already uses.
       iOS and Mac Catalyst share one implementation (NSProcessInfo.PhysicalMemory). -->
  <ItemGroup>
    <Compile Remove="Platforms\**\*.cs" />
  </ItemGroup>
  <ItemGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'">
    <Compile Include="Platforms\Android\**\*.cs" />
  </ItemGroup>
  <ItemGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios' or $([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'maccatalyst'">
    <Compile Include="Platforms\iOS\**\*.cs" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: `Qavren.Edge.Rag.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- net10.0 alone, for exactly Qavren.Edge.VectorData's reason: it touches no platform API and
         every platform TFM consumes a net10.0 library unchanged. Multi-targeting it would add three
         build legs for nothing. It references NO ONNX package - that is what lets UseRag() sit over
         an Azure OpenAI client, and it is asserted rather than asserted-about in
         Qavren.Edge.Rag.Tests. -->
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>true</IsPackable>
    <PackageId>Qavren.Edge.Rag</PackageId>
    <Description>A retrieval-augmented generation recipe for Microsoft.Extensions.AI: a DelegatingChatClient that retrieves through any Microsoft.Extensions.VectorData collection, assembles a numbered context block under a token budget, and resolves bracketed markers into CitationAnnotations - plus an extractive answer floor that needs no model at all.</Description>
    <RootNamespace>Qavren.Edge.Rag</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.AI" />
    <PackageReference Include="Microsoft.Extensions.VectorData.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <ProjectReference Include="..\..\..\foundation\src\Qavren.Edge.Core\Qavren.Edge.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: `Qavren.Edge.Chat.Tests.csproj`**

Copies `Qavren.Edge.Onnx.Tests`'s shape exactly, with SP4's floors. **No `Qavren.Edge.Rag` reference — Task 6.1 adds it (adjustment 14).**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- net10.0 is the HOST lane: an MTP test application, run with `dotnet run -f net10.0`.
         The four platform TFMs exist ONLY so this same assembly can be loaded by
         Qavren.Edge.DeviceTests and executed on a device. The windows TFM is present even though
         Qavren.Edge.Chat.Onnx has none: Qavren.Edge.DeviceTests targets net10.0-windows10.0.19041.0
         and must be able to reference this library. -->
    <TargetFrameworks>net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst;net10.0-windows10.0.19041.0</TargetFrameworks>
    <TargetFrameworks Condition="$([MSBuild]::IsOSPlatform('Linux'))">net10.0;net10.0-android</TargetFrameworks>
    <TargetFrameworks Condition="$([MSBuild]::IsOSPlatform('OSX'))">net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst</TargetFrameworks>
    <IsPackable>false</IsPackable>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'">24.0</SupportedOSPlatformVersion>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios'">15.4</SupportedOSPlatformVersion>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'maccatalyst'">15.4</SupportedOSPlatformVersion>
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

  <!-- LINKED, never copied: one task owns chat/tests/fixtures/TinyChatModel.g.cs. -->
  <ItemGroup>
    <Compile Include="..\fixtures\TinyChatModel.g.cs" Link="Fixtures\TinyChatModel.g.cs" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
    <ProjectReference Include="..\..\src\Qavren.Edge.Chat.Onnx\Qavren.Edge.Chat.Onnx.csproj" />
  </ItemGroup>

  <!-- Spec 16.4's device-only assertions (Task 6.1). An ActivityManager read or an
       NSProcessInfo.PhysicalMemory comparison can only be COMPILED under a platform TFM. -->
  <ItemGroup>
    <Compile Remove="Platforms\**\*.cs" />
  </ItemGroup>
  <ItemGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'">
    <Compile Include="Platforms\Android\**\*.cs" />
  </ItemGroup>
  <ItemGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios' or $([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'maccatalyst'">
    <Compile Include="Platforms\iOS\**\*.cs" />
  </ItemGroup>
</Project>
```

- [ ] **Step 7: `Qavren.Edge.Rag.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- net10.0 alone and host-only, exactly as Qavren.Edge.VectorData.Conformance.Tests is: it
         never reaches a device lane (spec 16.4). It references Qavren.Edge.Rag and the MEVD
         ABSTRACTIONS and nothing else - no Qavren.Edge.VectorData, no ONNX package - which is how
         spec 7's layering claim is tested rather than asserted (plan adjustment 19). -->
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
    <ProjectReference Include="..\..\src\Qavren.Edge.Rag\Qavren.Edge.Rag.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 8: Verify — restore, and assert the GenAI assets land where §17 claims**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
dotnet restore "$root\QavrenEdge.slnx"
if ($LASTEXITCODE -ne 0) { throw "solution restore failed" }

$assets = "$root\chat\src\Qavren.Edge.Chat.Onnx\obj\project.assets.json"
$a = Get-Content $assets -Raw | ConvertFrom-Json
$expected = [ordered]@{
  'net10.0'             = 'lib/net8.0/Microsoft.ML.OnnxRuntimeGenAI.dll'
  'net10.0-android'     = 'lib/net9.0-android31.0/Microsoft.ML.OnnxRuntimeGenAI.dll'
  'net10.0-ios'         = 'lib/net9.0-ios15.4/Microsoft.ML.OnnxRuntimeGenAI.dll'
  'net10.0-maccatalyst' = 'lib/net9.0-maccatalyst14.0/Microsoft.ML.OnnxRuntimeGenAI.dll'
}
foreach ($tfm in $expected.Keys) {
  $m = $a.targets.$tfm.'Microsoft.ML.OnnxRuntimeGenAI.Managed/0.15.2'
  if (-not $m) { throw "$tfm did not resolve Microsoft.ML.OnnxRuntimeGenAI.Managed 0.15.2" }
  $got = ($m.compile.PSObject.Properties.Name) -join ','
  if ($got -ne $expected[$tfm]) { throw "$tfm resolved '$got'; expected '$($expected[$tfm])'" }
  $n = $a.targets.$tfm.'Microsoft.ML.OnnxRuntimeGenAI/0.15.2'
  if ($n.compile) { throw "$tfm: the NATIVE package resolved compile assets; the assertion above is scanning the wrong id" }
  $ort = $a.targets.$tfm.PSObject.Properties.Name | Where-Object { $_ -like 'Microsoft.ML.OnnxRuntime/*' }
  if ($ort -ne 'Microsoft.ML.OnnxRuntime/1.30.0') { throw "$tfm resolved $ort; GenAI's 1.28.0 is a FLOOR and the repo pin must win" }
  Write-Host "OK $tfm -> $got (ORT 1.30.0)"
}
```

Expected: four `OK` lines. The native-package check is not decoration: SP2 adjustment 2 learned that an assertion written against `Microsoft.ML.OnnxRuntimeGenAI` rather than `…GenAI.Managed` passes vacuously forever.

---

### Task 1.2: `chat` README, ADRs 0009–0013, `foundation/docs/errors.md`, and the one SP2 spec correction

**Local-verifiable:** yes.

**Files:**
- Create: `chat\README.md`
- Create: `chat\docs\adr\0009-execution-providers-cut.md`
- Create: `chat\docs\adr\0010-upstream-ichatclient-not-shipped.md`
- Create: `chat\docs\adr\0011-guidance-unverified-not-unavailable.md`
- Create: `chat\docs\adr\0012-no-tool-calling-in-v1.md`
- Create: `chat\docs\adr\0013-apple-platform-floor-15-4.md`
- Edit: `foundation\docs\errors.md`
- Edit: `embeddings\docs\superpowers\specs\2026-09-11-sp2-embeddings-vectorstore-design.md`

**Approach.** Prose only; this task compiles nothing. Everything it states must already be true in this plan's Environment ground truth or in the spec — a README that claims a measurement nobody took is the failure mode §19 exists to prevent.

- [ ] **Step 1: `chat/README.md`**

Sections, in order, and each must be answerable from measured facts:

1. **What the two packages are**, with §4.3's four consumer snippets copied verbatim — including the citation snippet, **corrected to null-check `region.StartIndex`/`region.EndIndex`** (adjustment 9).
2. **There is no default preset**, and why (§1 decision 5). Both presets with their real numbers from Environment ground truth: Llama 1.241 GB bundle / 1167.52 MiB weights / 32 KiB per token / `LicenseRef-LLAMA-3.2-Community`; Qwen 495 MB bundle / 461.25 MiB weights / **112 KiB per token** / `Apache-2.0`. State plainly that the smaller download wants **more** resident memory at a long context.
3. **Nothing downloads implicitly**: `Plan()` → consent → `ProvisionAsync`, the App Store 4.2.3(ii) requirement, and the one-line metered-network hook.
4. **What the transfer does not do** (§12.2.1): foreground-only, resumable from the marker, no background transfer in v1, Android process death is the app's problem.
5. **Two iOS entitlements are a consumer requirement** (§12.4), named, with the note that the library does not write anyone's plist.
6. **What adopting chat costs an Android APK**, from the measured AAR: 21.5 MB compressed; per ABI `libonnxruntime-genai.so` 5.2 MB, `libonnxruntime-genai-jni.so` 1.9 MB and **`libmat.so` 32.7 MB**; only `arm64-v8a` and `x86_64` ship, so **an `armeabi-v7a` device gets the database and embeddings and has no chat at all**; and the merged manifest gains `INTERNET`, `ACCESS_NETWORK_STATE` and `ai.onnxruntime.genai.TelemetryInitializer`. `DisableTelemetry` defaults true. Whether that call prevents 1DS initialisation or only suppresses events afterwards is **unknown** — say "events are disabled at the API GenAI exposes", not "telemetry is off", until §19 item 9 is measured.
7. **Turns serialise.** One gate per model, one cached `Generator`; a second concurrent turn queues and a fifth is refused with 7105. On a phone that is the point; on a server it is a throughput ceiling.
8. **No tool calling, no structured output by default, no multimodal**, each pointing at its ADR.
9. **Diagnostics**: the key list, and the `ortEnvCreatedBeforeGenAi` × `ortEnvironmentPreexisting` table from §8.1 verbatim — it is the only way a reader joins the two components' halves of the ordering fact.
10. **The five GenAI managed-asset MVIDs** from Environment ground truth, as the lookup table `genAiManagedAsset` is read against (adjustment 2).
11. **What is proven and what is not**: trim-smoke proves what trim-smoke proves; there is **no published tokens/sec or peak-RSS figure for ORT GenAI on any Apple device**, and every phone number in this repo is a vivo X300 under Android 16. This section is also where **tier 0's six legs** are recorded — Task 1.6 Step 4 fills in pass/fail and the printed `key=value` block for `tier0-windows`, `tier0-linux`, `tier0-winui`, `tier0-android`, `tier0-ios` and `tier0-maccatalyst` — and it states in these words that **the packaged (MSIX) MAUI WinUI path is untested, because this repo has no signing identity**: tier 0 ran the unpackaged head. Write the heading and the six empty rows now; the wave-1 close fills them.
12. **Prompt and completion text are logged at `Trace` only** (§14.4). One sentence, because a consumer who turns `UseLogging()` up to `Debug` to debug a retrieval needs to know what they will and will not see — and because the rule is only credible if it is written down somewhere a reader looks.

- [ ] **Step 2: ADRs 0009–0013**

Each in SP1's ADR shape (Status / Context / Decision / Consequences / Revisit trigger), content exactly as §19 item 13 enumerates:

- **0009 execution providers cut** — GenAI has no CoreML and no NNAPI EP; its provider set is CPU/cuda/DML/QNN/WebGPU/OpenVINO/VitisAI/RyzenAI/NvTensorRtRtx; `WeakFrameworks="CoreML"` on the iOS `NativeReference` is a **link-time weak reference, not a provider** (quote the measured targets file); DirectML dead-ends at GenAI 0.14.1 / ORT-DML 1.23.0; QNN packaging was removed in 0.14.0 and `OgaRegisterExecutionProviderLibrary` exists in `ort_genai_c.h` with **no C# binding** (confirmed: the header ships in `build/native/include/`, and the managed surface dump has no such method). v1 is CPU everywhere; the only escape hatch is `ConfigOverlayJson`. Also records adjustment 11's suppression rule if the AOT analyzers force one.
- **0010 the upstream `IChatClient` is not shipped** — the five defects from §1 decision 1, with the measured corroboration: `OnnxRuntimeGenAIChatClientOptions` has exactly `EnableCaching`, `PromptFormatter`, `StopSequences` and nothing else, `GeneratorParams` is the only place `SetSearchOption` lives, and `Generator` exposes no way to change one after construction. `GetService(typeof(Model))` lets anyone build it themselves in one line.
- **0011 guidance is unverified, not unavailable** — `CreateGuidanceLogitsProcessor` returns `nullptr` with at most a log line at v0.15.2; the Apple full-framework build settings pass no `--use_guidance`; the desktop pipelines do. Hence `EdgeGuidancePolicy` with `Disabled` as the default and a **positive-control** probe that asserts output shape and never catches an exception. Records adjustment 3's third parameter and why the probe never sets it.
- **0012 no tool calling in v1** — a ~1B on-device model emits malformed tool calls often enough to violate the suite's "never looks broken" rule, and the grammar sampling that made the prior art survivable is what 0011 says is unavailable. `UseFunctionInvocation` above this client is inert and the README says so.
- **0013 Apple platform floor 15.4** — sourced from GenAI's own asset TFM (`lib/net9.0-ios15.4`, measured) rather than chosen; Mac Catalyst takes 15.4 too because it resolves the iOS xcframework through the SDK RID graph; the consequence is `Qavren.Edge.Chat.Onnx`, `Qavren.Edge.Chat.Tests` **and** `Qavren.Edge.DeviceTests` all move (adjustment 8), and the README's supported-minimum statement follows. §19 item 1(a) is the link that confirms it.

- [ ] **Step 3: `foundation/docs/errors.md`**

Add two lines to the range list at the top, immediately after the 5000–5299 entry:

```markdown
- **6000–6299** — reserved for sub-project 3 (ingestion). Unallocated.
- **7000–7299** — sub-project 4 (chat + RAG): `Qavren.Edge.Chat.Onnx`,
  `Qavren.Edge.Rag`.
```

Then append a `## Sub-project 4 — Qavren.Edge.Chat.Onnx / Qavren.Edge.Rag (7001–7204)` section with one `## <code>` heading per value in §15.1 — 7001–7009, 7051–7053, 7101–7108, 7201–7204 — each carrying **Name**, Meaning and Remediation in the shape 1001–5213 already use. The remediation text is §15.3's row for that code, and three of them are load-bearing rather than boilerplate:

- **7005** names, in order, the smaller preset, a lower `MaxContextTokens`, and the two iOS entitlements — and states that `WorkspaceBytes` and `ReserveBytes` are engineering estimates.
- **7006** says `MinTotalMemoryBytes` can be set to null to try anyway, at the risk of an OS kill.
- **7008** says re-run `fetch_chat_model_hashes.py`.

- [ ] **Step 4: The one SP2 spec correction (§8.2)**

In `embeddings/docs/superpowers/specs/2026-09-11-sp2-embeddings-vectorstore-design.md`, find the sentence claiming that a sub-project 4 chat model landing beside the embedding session would mean one shared thread pool, and replace it with the correct statement: ORT GenAI builds its session options **natively** from the `session_options` block inside `genai_config.json`, so `OnnxOptions.ShareThreadPool` does not reach the chat model and the two do not share a pool. One sentence, no code, no type, no default.

- [ ] **Step 5: Verify**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
$err  = Get-Content "$root\foundation\docs\errors.md" -Raw
$codes = 7001,7002,7003,7004,7005,7006,7007,7008,7009,7051,7052,7053,7101,7102,7103,7104,7105,7106,7107,7108,7201,7202,7203,7204
foreach ($c in $codes) { if ($err -notmatch "(?m)^## $c$") { throw "errors.md has no '## $c' heading" } }
if ($err -notmatch '6000–6299') { throw "errors.md does not record the sub-project 3 reservation" }
foreach ($n in 9,10,11,12,13) {
  if (-not (Get-ChildItem "$root\chat\docs\adr" -Filter "00$n-*.md")) { throw "ADR 00$n missing" }
}
$rm = Get-Content "$root\chat\README.md" -Raw
foreach ($token in 'tier0-maccatalyst','signing identity','Trace') {
  if ($rm -notmatch [regex]::Escape($token)) { throw "chat/README.md is missing the '$token' statement (plan adjustments 21, 27)" }
}
Write-Host 'OK: 24 error anchors, the 6000-6299 reservation, five ADRs, the tier-0 table and the Trace-only sentence'
```

Expected: the `OK` line. No build is run by this task.

---

### Task 1.3: Tier-2 fixture toolchain — pinned `uv` venv, generator, and the committed `TinyChatModel.g.cs`

**Local-verifiable:** yes.

**Files:**
- Create: `chat\tests\fixtures\requirements.txt`
- Create: `chat\tests\fixtures\make_tiny_chat_model.py`
- Create: `chat\tests\fixtures\TinyChatModel.g.cs` (generated by the script, committed)

**Approach — read this before writing.** §16.2's constraint is absolute and the reason is measured, not argued: `Tokenizer` has exactly one constructor, `Tokenizer(Model)`, and `OgaCreateTokenizerFromPath` exists only on unreleased `main`. **A "tokenizer-only fixture" is not possible** and must not be attempted. Anything that touches real ORT GenAI needs a real model *directory*.

The generator is run **by hand**. CI never runs it, exactly as SP2 treats `make_tiny_model.py`. Its output is a `.cs` file of base64 `const string`s — **nothing binary is committed** — and the test fixture materialises those bytes into a temp directory once per collection, because `Model(string)` needs a directory.

**Head size 16 is not arbitrary.** On the fp32 CPU path the builder emits `GroupQueryAttention` with fused RoPE, and ORT 1.30's `group_query_attention_helper.h` enforces `head_size % 8 == 0` and, when rotary cos/sin caches are present, `head_size % 16 == 0`. The obvious shortcut is a trap: `hf-internal-testing/tiny-random-LlamaForCausalLM` has head_size 4 and will not run through that path at all, and its 32,000-row vocabulary makes it 4 MB.

- [ ] **Step 1: Pin the venv**

`chat/tests/fixtures/requirements.txt`:

```
# Sub-project 4 tier-2 fixture toolchain. Run BY HAND; never a build step and never CI.
# Resolved and verified on this box 2026-09-11 for CPython 3.14.5 / win-x64 (42 packages).
# onnxruntime matches the .NET pin exactly so a fixture that loads here loads there; onnx and
# numpy are pinned for the same reason sub-project 2 pins them - they are what serialises the
# initializers whose exact byte counts Step 5 asserts.
onnxruntime-genai==0.15.2
onnxruntime==1.30.0
onnx==1.22.0
numpy==2.5.3
torch==2.14.0
transformers==5.17.0
```

Build it:

```powershell
$fx = "C:\Users\steve\projects\qavren-edge-sp4\chat\tests\fixtures"
uv venv --python 3.14 "$fx\.venv"
uv pip install --python "$fx\.venv\Scripts\python.exe" -r "$fx\requirements.txt"
```

**Contingency, decided here rather than discovered:** if `transformers` 5.17.0 breaks `onnxruntime_genai.models.builder` (the builder was written against 4.x), pin `transformers==4.57.1` and re-run. Record whichever version worked in a comment at the top of the generated file.

- [ ] **Step 2: `make_tiny_chat_model.py` — literal and complete**

**Why this one is written out in full rather than described.** The plan's **Convention for "literal code"** covers both Python generators, and for the hashing script (Task 2.2) a numbered description is defensible: every value it emits is already a literal in **Environment ground truth** and its verify is a byte-identical regeneration diff. This script is the opposite case. The one artifact §16.2 explicitly flags as **unverified** — "whether onnxruntime-extensions accepts a hand-made BPE `tokenizer.json`; every upstream fixture uses a full 2.1 MB GPT-2 one" — is exactly the artifact a numbered step described in half a sentence, and it is the artifact whose failure triggers the 3.50 MB vendoring contingency. An implementer improvising a "256-entry byte-level BPE tokenizer" from that description will produce a different file every time, and the contingency will fire on the improvisation rather than on the design. So the vocab construction, the tokenizer document and the chat template are literal here.

**Two decisions inside it that a reader would otherwise get wrong.**

- **The vocabulary is the 256 byte-level tokens in byte order, so token id == byte value, and there are no merges.** That is what makes `pad_token_id` 0, `bos_token_id` 1 and `eos_token_id` 2 safe: they are the byte-level characters for bytes `0x00`, `0x01` and `0x02`, which ordinary text never encodes to. Picking, say, id 2 out of a merged vocabulary would collide with a real token and the fixture would terminate mid-word.
- **The chat template is three constructs — a `for`, a dict index and an `if`** — because the fixture's job is *mechanics*, not template coverage. §19 item 6 (does minja parse a **real** preset's Jinja) is a tier-3 question and stays there; a fixture template that minja chokes on would fail the tier-2 suite for a reason that has nothing to do with what tier 2 tests.

```python
#!/usr/bin/env python3
"""Generate chat/tests/fixtures/TinyChatModel.g.cs - the tier-2 GenAI model fixture.

Run BY HAND, never from a build and never from CI:

    C:\\Users\\steve\\projects\\qavren-edge-sp4\\chat\\tests\\fixtures\\.venv\\Scripts\\python.exe \\
        make_tiny_chat_model.py --out TinyChatModel.g.cs --scratch _scratch

Spec 16.2. A "tokenizer-only fixture" is NOT possible: at v0.15.2 Tokenizer has exactly one
constructor, Tokenizer(Model), and OgaCreateTokenizerFromPath exists only on unreleased main.
Anything that touches real ORT GenAI needs a real model DIRECTORY, so this builds one and the
emitted .cs carries it as base64 - nothing binary is ever committed.

Head size 16 is not arbitrary: on the fp32 CPU path the builder emits GroupQueryAttention with
fused RoPE, and ORT 1.30's group_query_attention_helper.h enforces head_size % 8 == 0 and, when
rotary cos/sin caches are present, head_size % 16 == 0. hidden_size 64 / 4 heads = 16.
"""

import argparse
import base64
import json
import pathlib
import subprocess
import sys

SEED = 20260911
SOURCE_CAP_BYTES = 1024 * 1024          # spec 16.2's 1 MB cap on the generated source
VOCAB_SIZE = 256
TRANSFORMERS_PIN_NOTE = "transformers==5.17.0"   # amend if the 4.57.1 contingency was taken

# --- the model config, hand-authored ----------------------------------------------------------

MODEL_CONFIG = {
    "architectures": ["LlamaForCausalLM"],
    "model_type": "llama",
    "vocab_size": VOCAB_SIZE,
    "hidden_size": 64,
    "intermediate_size": 128,
    "num_hidden_layers": 2,
    "num_attention_heads": 4,           # -> head_size 16
    "num_key_value_heads": 2,
    "max_position_embeddings": 512,
    "rms_norm_eps": 1e-5,
    "rope_theta": 10000.0,
    "tie_word_embeddings": True,
    "torch_dtype": "float32",
    "use_cache": True,
    "bos_token_id": 1,
    "eos_token_id": 2,
    "pad_token_id": 0,
}

# --- the tokenizer ------------------------------------------------------------------------------

def bytes_to_unicode():
    """GPT-2's byte-level alphabet, verbatim in behaviour: a reversible byte -> printable-char map.

    Printable ASCII, Latin-1 letters and the Latin-1 supplement map to themselves; every other byte
    maps to U+0100 + n for a running n. This is the same table tokenizers' ByteLevel components use,
    so a vocabulary built from it is one they can round-trip.
    """
    bs = (list(range(ord("!"), ord("~") + 1))
          + list(range(ord("\u00a1"), ord("\u00ac") + 1))
          + list(range(ord("\u00ae"), ord("\u00ff") + 1)))
    cs = bs[:]
    n = 0
    for b in range(256):
        if b not in bs:
            bs.append(b)
            cs.append(256 + n)
            n += 1
    return {b: chr(c) for b, c in zip(bs, cs)}


def build_tokenizer_json():
    """A 256-entry byte-level BPE with NO merges, ordered so that token id == byte value.

    Ordering matters and is the whole reason this is literal. config.json names pad/bos/eos as ids
    0/1/2; with id == byte value those are the byte-level characters for 0x00, 0x01 and 0x02, which
    ordinary text never encodes to. On a merged or differently-ordered vocabulary those three ids
    would be real text tokens and the fixture would terminate mid-word.
    """
    b2u = bytes_to_unicode()
    vocab = {b2u[b]: b for b in range(256)}
    assert len(vocab) == VOCAB_SIZE, "the byte-level alphabet must be exactly 256 distinct chars"
    return {
        "version": "1.0",
        "truncation": None,
        "padding": None,
        "added_tokens": [],
        "normalizer": None,
        "pre_tokenizer": {
            "type": "ByteLevel",
            "add_prefix_space": False,
            "trim_offsets": True,
            "use_regex": True,
        },
        "post_processor": {
            "type": "ByteLevel",
            "add_prefix_space": True,
            "trim_offsets": False,
            "use_regex": True,
        },
        "decoder": {
            "type": "ByteLevel",
            "add_prefix_space": True,
            "trim_offsets": True,
            "use_regex": True,
        },
        "model": {
            "type": "BPE",
            "dropout": None,
            "unk_token": None,
            "continuing_subword_prefix": None,
            "end_of_word_suffix": None,
            "fuse_unk": False,
            "byte_fallback": False,
            "ignore_merges": True,
            "vocab": vocab,
            "merges": [],
        },
    }


# Three Jinja constructs and nothing else: a for, a dict index, an if. Deliberately minimal - the
# fixture tests MECHANICS, and whether minja parses a REAL preset's template is spec 19 item 6, a
# tier-3 question. A fixture template minja choked on would fail tier 2 for an unrelated reason.
CHAT_TEMPLATE = (
    "{% for message in messages %}"
    "<|{{ message['role'] }}|>\n{{ message['content'] }}\n"
    "{% endfor %}"
    "{% if add_generation_prompt %}<|assistant|>\n{% endif %}"
)


def build_tokenizer_config():
    b2u = bytes_to_unicode()
    return {
        "tokenizer_class": "PreTrainedTokenizerFast",
        "model_max_length": 512,
        "clean_up_tokenization_spaces": False,
        "pad_token": b2u[0],
        "bos_token": b2u[1],
        "eos_token": b2u[2],
        "chat_template": CHAT_TEMPLATE,
    }


# --- the pipeline -------------------------------------------------------------------------------

def write_source_model(scratch: pathlib.Path) -> None:
    import torch
    from transformers import LlamaConfig, LlamaForCausalLM

    scratch.mkdir(parents=True, exist_ok=True)
    (scratch / "config.json").write_text(
        json.dumps(MODEL_CONFIG, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")
    (scratch / "tokenizer.json").write_text(
        json.dumps(build_tokenizer_json(), indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8", newline="\n")
    (scratch / "tokenizer_config.json").write_text(
        json.dumps(build_tokenizer_config(), indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8", newline="\n")

    torch.manual_seed(SEED)
    model = LlamaForCausalLM(LlamaConfig(**MODEL_CONFIG))
    model.eval()
    model.save_pretrained(str(scratch), safe_serialization=True)


def run_builder(scratch: pathlib.Path, out: pathlib.Path) -> None:
    out.mkdir(parents=True, exist_ok=True)
    subprocess.run(
        [sys.executable, "-m", "onnxruntime_genai.models.builder",
         "-m", str(scratch), "-o", str(out), "-p", "fp32", "-e", "cpu"],
        check=True)


def assert_shape(out: pathlib.Path) -> None:
    """If the builder rewrote any of these, every budget assertion downstream is against the wrong
    shape - and it would fail as an arithmetic mismatch three waves later, not here."""
    cfg = json.loads((out / "genai_config.json").read_text(encoding="utf-8"))
    model, decoder = cfg["model"], cfg["model"]["decoder"]
    expected = {
        "context_length": (model["context_length"], 512),
        "vocab_size": (model["vocab_size"], VOCAB_SIZE),
        "head_size": (decoder["head_size"], 16),
        "num_hidden_layers": (decoder["num_hidden_layers"], 2),
        "num_key_value_heads": (decoder["num_key_value_heads"], 2),
    }
    bad = {k: v for k, (v, want) in expected.items() if v != want}
    if bad:
        raise SystemExit(f"the builder rewrote the shape: {bad}; expected "
                         f"{ {k: w for k, (_, w) in expected.items()} }")


FILES = [
    ("GenAiConfigJson", "genai_config.json"),
    ("ModelOnnx", "model.onnx"),
    ("TokenizerJson", "tokenizer.json"),
    ("TokenizerConfigJson", "tokenizer_config.json"),
]


def emit(out_dir: pathlib.Path, cs_path: pathlib.Path) -> None:
    blobs = {}
    for name, filename in FILES:
        data = (out_dir / filename).read_bytes()
        blobs[name] = (filename, data, base64.b64encode(data).decode("ascii"))

    lines = [
        "// <auto-generated />",
        "// Generated by chat/tests/fixtures/make_tiny_chat_model.py. DO NOT EDIT BY HAND.",
        f"// Seed {SEED}; {TRANSFORMERS_PIN_NOTE}; onnxruntime==1.30.0; onnx==1.22.0.",
        "// Spec 16.2: a tiny but REAL ORT GenAI model directory, carried as base64 so nothing",
        "// binary is committed. The Length consts are plan adjustment 25: CI never runs this",
        "// script, so a test - not a comment - is the only thing that can notice a moved byte",
        "// count after an onnx, torch, transformers or builder upgrade.",
        "",
        "namespace Qavren.Edge.Chat.Tests.Fixtures;",
        "",
        "internal static class TinyChatModel",
        "{",
    ]
    for name, (filename, data, b64) in blobs.items():
        lines += [
            f"    public const string {name}FileName = \"{filename}\";",
            f"    public const int {name}Length = {len(data)};",
            f"    public const string {name}Base64 =",
            f"        \"{b64}\";",
            f"    public static byte[] {name}Bytes() => System.Convert.FromBase64String({name}Base64);",
            "",
        ]
    lines += [
        "    /// <summary>Writes every fixture file into <paramref name=\"directory\"/>; Model(string) needs a directory.</summary>",
        "    public static string Materialise(string directory)",
        "    {",
        "        System.IO.Directory.CreateDirectory(directory);",
    ]
    for name, _ in FILES:
        lines.append(f"        System.IO.File.WriteAllBytes("
                     f"System.IO.Path.Combine(directory, {name}FileName), {name}Bytes());")
    lines += ["        return directory;", "    }", "}", ""]

    text = "\n".join(lines)
    if len(text.encode("utf-8")) > SOURCE_CAP_BYTES:
        raise SystemExit(f"generated source is {len(text.encode('utf-8'))} bytes, over the "
                         f"{SOURCE_CAP_BYTES} cap - take spec 16.2's vendoring contingency")
    cs_path.write_text(text, encoding="utf-8", newline="\n")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--scratch", required=True)
    args = ap.parse_args()

    scratch = pathlib.Path(args.scratch).resolve()
    built = scratch / "genai"
    write_source_model(scratch)
    run_builder(scratch, built)
    assert_shape(built)
    emit(built, pathlib.Path(args.out).resolve())
    print("OK: TinyChatModel.g.cs written")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
```

**The `Length` consts are `const int`s and not doc comments, and that is adjustment 25.** §16.2 asks for "byte counts asserted so an onnx or builder upgrade that moves them is loud". A comment asserts nothing, and CI never runs this generator — so the only thing that could notice a moved byte count is a test that runs on every PR. `Tier2/TinyChatModelFixtureTests` (Task 6.1) reads each materialised file's on-disk length and compares it to the `const` beside its base64. Put the number where a compiler and a test can reach it.

**Two written contingencies (§16.2), and the plan chooses between them rather than leaving it open.** If onnxruntime-extensions rejects the literal `build_tokenizer_json()` document above — this is the one thing in the fixture chain that is **unverified**, because every upstream fixture uses a full 2.1 MB GPT-2 tokenizer — vendor upstream's MIT-licensed `test/models/hf-internal-testing/tiny-random-gpt2-fp32/` wholesale: 3.50 MB, five files, a known-working GenAI folder with a matching tokenizer, and CI still downloads nothing; raise `SOURCE_CAP_BYTES` to 5 MB and say so in the file header. If **neither** fits, or the builder rejects `MODEL_CONFIG` outright, tier 2 moves wholesale to the nightly lane, Task 6.1's `Tier2` folder becomes a `Tier3` folder guarded by the same `SkipUnless`, and the per-PR guarantee drops to tier 1 plus Task 1.1's restore assertions. Whichever branch is taken is recorded in `chat/README.md` by the implementer in one sentence.


- [ ] **Step 3: Generate and commit the output**

```powershell
$fx = "C:\Users\steve\projects\qavren-edge-sp4\chat\tests\fixtures"
& "$fx\.venv\Scripts\python.exe" "$fx\make_tiny_chat_model.py" --out "$fx\TinyChatModel.g.cs" --scratch "$fx\_scratch"
```

- [ ] **Step 4: Verify — regenerate and diff, and assert the caps**

```powershell
$fx = "C:\Users\steve\projects\qavren-edge-sp4\chat\tests\fixtures"
Copy-Item "$fx\TinyChatModel.g.cs" "$env:TEMP\TinyChatModel.first.cs" -Force
& "$fx\.venv\Scripts\python.exe" "$fx\make_tiny_chat_model.py" --out "$fx\TinyChatModel.g.cs" --scratch "$fx\_scratch"
if ((Get-FileHash "$fx\TinyChatModel.g.cs").Hash -ne (Get-FileHash "$env:TEMP\TinyChatModel.first.cs").Hash) {
  throw "the generator is not deterministic"
}
$len = (Get-Item "$fx\TinyChatModel.g.cs").Length
if ($len -gt 1MB) { throw "generated source is $len bytes, over the 1 MB cap" }
$src = Get-Content "$fx\TinyChatModel.g.cs" -Raw
foreach ($n in 'GenAiConfigJson','ModelOnnx','TokenizerJson','TokenizerConfigJson') {
  if ($src -notmatch [regex]::Escape($n)) { throw "TinyChatModel.g.cs is missing $n" }
  if ($src -notmatch ("const int {0}Length = \d+;" -f $n)) { throw "TinyChatModel.g.cs is missing const int ${n}Length (plan adjustment 25)" }
}
Write-Host "OK: deterministic, $len bytes, every fixture const and every length const present"
```

Expected: the `OK` line. The determinism check is what turns "somebody re-ran the generator and the bytes moved" into a failure rather than a surprise — CI never runs this script, so nothing else would notice an onnx or builder upgrade.

---

### Task 1.4: SP1 edit — `EdgeErrorCode` gains 7000–7299

**Local-verifiable:** yes.

**Files:**
- Edit: `foundation\src\Qavren.Edge.Core\EdgeErrorCode.cs`
- Test: `foundation\tests\Qavren.Edge.Core.Tests\ChatErrorCodeRangeTests.cs`

**Approach.** Purely additive: SP1's 1001–4001 and SP2's 5001–5213 are untouched, 6000–6299 stays unallocated, and the new members are appended at the end of the enum with their explicit numeric values. Nothing else in `Qavren.Edge.Core` changes — no new type, no new member on an existing type, no default moved. This is the **one** SP1 library edit SP4 makes.

- [ ] **Step 1: Append the range**

Append inside the enum, after `ReservedColumnName = 5213,`, exactly §15.1's list with its section comments:

```csharp
    // ---- Sub-project 4: 7000-7299. 6000-6299 is reserved for sub-project 3 and stays unallocated. ----

    // Qavren.Edge.Chat.Onnx - runtime and model hosting
    ChatEnvironmentNotStarted        = 7001,
    ChatModelLoadFailed              = 7002,
    ChatModelNotRegistered           = 7003,
    /// <summary>No ORT GenAI native for this RID or Android ABI. armeabi-v7a has none.</summary>
    ChatUnsupportedRuntime           = 7004,
    ChatInsufficientMemory           = 7005,
    ChatDeviceTooSmall               = 7006,
    /// <summary>genai_config.json missing, unparseable or incoherent.</summary>
    ChatConfigurationInvalid         = 7007,
    /// <summary>The config disagrees with the preset's declared shape.</summary>
    ChatModelShapeMismatch           = 7008,
    ChatExecutionProviderUnsupported = 7009,

    // Qavren.Edge.Chat.Onnx - provisioning
    ChatModelNotProvisioned          = 7051,
    ChatInsufficientDiskSpace        = 7052,
    /// <summary>ChatProvisioningOptions.IsTransferPermitted said no.</summary>
    ChatDownloadNotPermitted         = 7053,

    // Qavren.Edge.Chat.Onnx - generation
    ChatTemplateUnsupported          = 7101,
    ChatPromptTooLong                = 7102,
    ChatGuidanceUnavailable          = 7103,
    ChatGenerationFailed             = 7104,
    ChatBusy                         = 7105,
    ChatThermalAbort                 = 7106,
    ChatToolCallingUnsupported       = 7107,
    ChatOptionUnsupported            = 7108,

    // Qavren.Edge.Rag
    RagRetrieverMissing              = 7201,
    RagRetrievalFailed               = 7202,
    RagCollectionNotSearchable       = 7203,
    RagContextBudgetTooSmall         = 7204,
```

**There is deliberately no "chat client missing" code.** §15.1 removed it because it had no reachable raise site, and re-adding one would be unsatisfiable under §16.1's rule that every 7000-range member is thrown by at least one test.

**`RagCollectionNotSearchable` (7203) stays, and adjustment 22 is why it is satisfiable.** As §7 declares `VectorStoreRetrieverOptions<TRecord>` there is no switch that can request hybrid search and mean it, so 7203 would have been the second unreachable code in this range — the thing §15.1 had just finished removing. Task 3.2 adds `RequireHybridSearch` (default `false`, so nothing existing moves) and owns 7203's only raise site. Do not delete the member here on the grounds that nothing throws it yet; wave 3 does.

- [ ] **Step 2: The test**

`ChatErrorCodeRangeTests.cs` asserts, over `Enum.GetValues<EdgeErrorCode>()`:

1. Every value in `[1001, 5299]` is exactly the set SP1 and SP2 already defined — no renumbering, asserted by explicit list.
2. **No value falls in `[6000, 6299]`** — the SP3 reservation, guarded so a later sub-project cannot quietly squat it.
3. The 24 SP4 values are present with exactly the numbers above.
4. Every value is distinct, and `HelpLink` on a constructed `EdgeConfigurationException` for one SP4 code ends in `#7001` — the `HelpLink` convention applies to the new codes for free, and this is the assertion that proves it.

- [ ] **Step 3: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp4\foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj" `
  -c Release -f net10.0 -p:TargetFrameworks=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp4\w1\t14
```

Expected: all tests pass, exit 0.

---

### Task 1.5: Tier 0 — the five-target link-and-generate smoke *(serial interlude; one implementer, runs ALONE and strictly AFTER Task 1.3)*

**Local-verifiable:** partly — the Windows console leg **runs here over the real fixture**, and all four device-head TFMs **compile** here. The Linux, Android, iOS and Mac Catalyst *executions* are the dispatched workflow's, and Task 1.6 Step 3 is what dispatches it.

**Files:**
- Create: `chat\tools\tier0-genai-smoke\Tier0Smoke.cs` (the one shared smoke body)
- Create: `chat\tools\tier0-genai-smoke\Qavren.Edge.Tier0Smoke\Qavren.Edge.Tier0Smoke.csproj` + `Program.cs`
- Create: `chat\tools\tier0-genai-smoke\Qavren.Edge.Tier0Smoke.Device\Qavren.Edge.Tier0Smoke.Device.csproj`, `MauiProgram.cs`, `Tier0SmokeFacts.cs`, `Platforms\**` (the four app heads, copied from `Qavren.Edge.DeviceTests`)
- Create: `.github\workflows\tier0-genai-smoke.yml` — **a root file under `.github/**`, delegated to this task by name (adjustment 30).** It is the plan's one carve-out from "the integrator owns every root file", and it is safe for four checked reasons: the file is new, no other task in the plan touches that path, it is a throwaway deleted in the closing PR, and it is `workflow_dispatch`-only and outside `ci-gate`'s `needs` so it can gate nothing. Task 1.1's cherry-picks also touch `.github/**`, in a **different serial phase** and on a **different file** (`ci.yml`), so the two never overlap. This task creates and edits **no other root file** — not `QavrenEdge.slnx` (its two projects are deliberately absent from it), not `Directory.Packages.props`, not `.gitignore`. Wanting to is a plan bug.

**Ordering is a hard dependency on Task 1.3's output file, and Step 0 checks it rather than trusting the schedule.** Both tier-0 projects `<Compile Include>` `chat/tests/fixtures/TinyChatModel.g.cs` by relative path. A dispatcher that reads task bodies rather than the wave map, and starts this task beside Task 1.3, gets `CS2001: Source file … could not be found` — or worse, a *half-written* generated file and a spray of `CS1002`/`CS0117` — and that reads as a broken csproj rather than as the scheduling error it is. Step 0 exists to turn that into one clear sentence.

**Approach — read this before writing.** This is §16.0 and §19 item 1, and it is scheduled at the **end of wave 1** rather than literally first because §16.0 contradicts itself: the smoke "loads the tier-2 fixture and generates one token", and Task 1.3 is what generates that fixture. Adjustment 21 records the departure. What §16.0 actually protects — "a TFM cut discovered after the API is written is a rewrite" — is protected exactly as well here, because **no line of `Qavren.Edge.Chat.Onnx` or `Qavren.Edge.Rag` exists yet**: wave 2 does not start until the six legs report.

**Nothing here references a Qavren package.** The two throwaway projects reference `Microsoft.ML.OnnxRuntimeGenAI` (the CPM pin from Task 1.1) and, for the device head, MAUI and DeviceRunners. They are **not** in `QavrenEdge.slnx`: a solution build, a `dotnet format` run and `dotnet pack` must never see them, and Task 8.1's gate walks the `.slnx`.

**The smoke body is one file, linked by both projects**, so the console leg and the device leg cannot drift into proving different things. It:

1. materialises `TinyChatModel.g.cs` into a temp directory (the same base64 the tier-2 fixture uses, linked from `chat/tests/fixtures/`);
2. constructs an `OgaHandle`, calls `Utils.DisableTelemetryEvents()`;
3. `new Config(dir)` → `new Model(config)` → `new Tokenizer(model)`;
4. encodes a three-token prompt, builds `GeneratorParams` with `max_length` = 8, constructs a `Generator`, calls `GenerateNextToken()` **once**, and decodes the token through a `TokenizerStream`;
5. prints, one `key=value` per line: `rid`, `abi` (android only), `managedAssemblyMvid`, `meaiAbstractionsVersion`, `ichatClientAssignable` (`typeof(IChatClient).IsAssignableFrom(typeof(OnnxRuntimeGenAIChatClient))`), `ortEnvIsCreatedAfterGenAi`, `modelLoadMs`, `firstTokenId`, `firstTokenText`;
6. disposes everything in reverse order and returns 0; any throw prints `TIER0 FAIL: <type>: <message>` and returns 1.

Point 5's `ichatClientAssignable` is §16.0's go/no-go (c) — the Managed assembly compiled against MEAI Abstractions 9.8.0 binding against this repo's 10.10.0 — asserted on **every** target rather than on Windows alone, because a `TypeLoadException` is a per-runtime fact.

**The device head is `Qavren.Edge.DeviceTests`'s csproj with the Qavren references deleted.** Same `UseMaui`/`SingleProject`/`DeviceRunners` shape, same `PublishTrimmed`/`MtouchLink`/`RunAOTCompilation` settings, same per-OS `TargetFrameworks` guards, same `WindowsPackageType=None`, and the MAUI icon and splash items **linked** from `foundation/tests/Qavren.Edge.DeviceTests/Resources/` rather than duplicated. It carries exactly one `[Fact]`, `Tier0SmokeFacts.LoadsAndGeneratesOneToken`, which calls the shared body and asserts it returned 0.

**`WindowsPackageType=None` means tier 0 proves the UNPACKAGED WinUI path.** §16.0 item 1(b) asks for "a packaged MAUI WinUI app"; this repo has no signing identity, which is why `Qavren.Edge.DeviceTests` is unpackaged too. That half is **not closed** by tier 0 and `chat/README.md` says so in those words (Task 1.2) rather than implying MSIX was tested. It is recorded in **CI-only work** as an open item, not as a passing one.

- [ ] **Step 0: Assert Task 1.3's fixture exists and is complete — BEFORE anything builds**

Nothing in this task may run until this prints its `OK` line. It is four checks and it costs nothing; without it, a scheduling mistake presents as a compiler error in a file this task wrote.

```powershell
$fx = "C:\Users\steve\projects\qavren-edge-sp4\chat\tests\fixtures\TinyChatModel.g.cs"
if (-not (Test-Path $fx)) {
  throw "PLAN ORDERING ERROR: $fx does not exist. Task 1.5 runs ALONE and strictly AFTER Task 1.3, which generates it (wave map, wave 1 'serial interlude'). Do not create a stub - wait for 1.3."
}
$src = Get-Content $fx -Raw
foreach ($n in 'GenAiConfigJson','ModelOnnx','TokenizerJson','TokenizerConfigJson') {
  if ($src -notmatch ("const int {0}Length = \d+;" -f $n)) {
    throw "PLAN ORDERING ERROR: $fx is missing 'const int ${n}Length' - Task 1.3 is incomplete or still writing. Do not proceed."
  }
  if ($src -notmatch ("const string {0}Base64 =" -f $n)) {
    throw "PLAN ORDERING ERROR: $fx is missing 'const string ${n}Base64' - Task 1.3 is incomplete or still writing. Do not proceed."
  }
}
if ($src -notmatch 'static string Materialise\(') { throw "PLAN ORDERING ERROR: $fx has no Materialise helper; the smoke body cannot run." }
Write-Host 'OK: Task 1.3''s fixture is present and carries all four Base64 + Length consts and Materialise'
```

Expected: the `OK` line. A failure here is **a scheduling error, never a csproj error** — the message says so in those words so nobody debugs the wrong file.

- [ ] **Step 1: The shared body and the console project**

`Qavren.Edge.Tier0Smoke.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- THROWAWAY. Deliberately NOT in QavrenEdge.slnx: it must not reach dotnet format, dotnet
         pack or the wave-8 gate. net10.0 alone - this leg is the Windows and Linux targets of
         spec 16.0, and the four platform targets are the sibling device head. -->
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
    <RootNamespace>Qavren.Edge.Tier0Smoke</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.ML.OnnxRuntimeGenAI" />
    <PackageReference Include="Microsoft.ML.OnnxRuntime" />
    <PackageReference Include="Microsoft.Extensions.AI" />
  </ItemGroup>
  <!-- The ONE smoke body, and the SAME fixture the tier-2 suite will use. Linked, never copied. -->
  <ItemGroup>
    <Compile Include="..\Tier0Smoke.cs" Link="Tier0Smoke.cs" />
    <Compile Include="..\..\..\tests\fixtures\TinyChatModel.g.cs" Link="TinyChatModel.g.cs" />
  </ItemGroup>
</Project>
```

`Program.cs` is one line: `return await Qavren.Edge.Tier0Smoke.Tier0Smoke.RunAsync(Console.Out);`

- [ ] **Step 2: The device head and its single fact**

Copy `foundation/tests/Qavren.Edge.DeviceTests/Qavren.Edge.DeviceTests.csproj`, then: delete every `ProjectReference`, delete the `AndroidNativeLibrary` / `NativeReference` / `_QedgeDropHostNativeFromMacCatalystBundle` blocks (they exist for SP1's SQLite native and have no analogue here), change `ApplicationId` to `app.qavren.edge.tier0smoke`, and add:

```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.ML.OnnxRuntimeGenAI" />
    <PackageReference Include="Microsoft.ML.OnnxRuntime" />
    <PackageReference Include="Microsoft.Extensions.AI" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="..\Tier0Smoke.cs" Link="Tier0Smoke.cs" />
    <Compile Include="..\..\..\tests\fixtures\TinyChatModel.g.cs" Link="TinyChatModel.g.cs" />
  </ItemGroup>
  <!-- Linked, not duplicated: a UseMaui app head does not build without an icon and a splash. -->
  <ItemGroup>
    <MauiIcon Include="..\..\..\..\foundation\tests\Qavren.Edge.DeviceTests\Resources\AppIcon\appicon.svg"
              ForegroundFile="..\..\..\..\foundation\tests\Qavren.Edge.DeviceTests\Resources\AppIcon\appiconfg.svg" Color="#512BD4" />
    <MauiSplashScreen Include="..\..\..\..\foundation\tests\Qavren.Edge.DeviceTests\Resources\Splash\splash.svg" Color="#512BD4" BaseSize="128,128" />
  </ItemGroup>
```

Keep `SupportedOSPlatformVersion` at android `24.0` and Apple **`15.4`** — GenAI's own floors (adjustment 8's evidence), and tier 0 is where a floor that is too low announces itself as a manifest-merge or linker failure rather than in wave 7.

- [ ] **Step 3: `.github/workflows/tier0-genai-smoke.yml`**

Six jobs, `workflow_dispatch` only, in **its own file** so that no tier-0 leg can gate a PR and so `assert-workflows.py`'s `ci.yml` rules are untouched. The android, ios and maccatalyst jobs are `ci.yml`'s existing device lanes with the project path and TFM swapped and the `natives` dependency and TRX conversion dropped — tier 0 has no SQLite native to download and publishes no JUnit.

```yaml
name: tier0-genai-smoke

# Spec 16.0 / spec 19 item 1. THROWAWAY and workflow_dispatch ONLY: it answers three go/no-go
# questions once, before sub-project 4 has any API, and it is deleted in the closing PR. It is not
# in ci.yml and it is not in ci-gate's needs, by construction - it cannot gate anything.
on: workflow_dispatch

permissions:
  contents: read

env:
  SMOKE: chat/tools/tier0-genai-smoke/Qavren.Edge.Tier0Smoke/Qavren.Edge.Tier0Smoke.csproj
  DEVICE: chat/tools/tier0-genai-smoke/Qavren.Edge.Tier0Smoke.Device/Qavren.Edge.Tier0Smoke.Device.csproj

jobs:
  tier0-windows:
    runs-on: windows-2025
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0, filter: 'tree:0' }
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - run: dotnet run --project ${{ env.SMOKE }} -c Release

  tier0-linux:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0, filter: 'tree:0' }
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - run: dotnet run --project ${{ env.SMOKE }} -c Release

  # Unpackaged WinUI. WindowsPackageType=None, because this repo has no signing identity - the
  # PACKAGED MSIX resolution path spec 16.0 item 1(b) also names is NOT covered here, and the
  # README says so rather than implying it was tested.
  tier0-winui:
    runs-on: windows-2025
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0, filter: 'tree:0' }
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - run: dotnet workload install maui-windows --version 10.0.201
      - run: dotnet tool restore
      - name: Run the smoke on the WinUI head
        run: >
          dotnet build ${{ env.DEVICE }}
          -f net10.0-windows10.0.19041.0 -c Release -t:VSTest
          -p:VSTestLogger=trx%3BLogFileName=tier0-winui.trx

  tier0-android:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0, filter: 'tree:0' }
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: actions/setup-java@v4
        with: { distribution: microsoft, java-version: '21' }
      - run: dotnet workload install maui-android --version 10.0.201
      - run: dotnet tool restore
      - name: Install Android SDK packages
        run: dotnet android sdk install --package 'platform-tools' --package 'emulator' --package 'system-images;android-36;google_apis;x86_64'
      - name: Set AVD environment variables
        run: |
          mkdir -p "$HOME/.android/avd"
          echo "ANDROID_AVD_HOME=$HOME/.android/avd" >> "$GITHUB_ENV"
          echo "ANDROID_EMULATOR_HOME=$HOME/.android" >> "$GITHUB_ENV"
          echo "ANDROID_EMULATOR_WAIT_TIME_BEFORE_KILL=1" >> "$GITHUB_ENV"
      - name: Enable KVM
        run: |
          echo 'KERNEL=="kvm", GROUP="kvm", MODE="0666", OPTIONS+="static_node=kvm"' | sudo tee /etc/udev/rules.d/99-kvm4all.rules
          sudo udevadm control --reload-rules
          sudo udevadm trigger --name-match=kvm
      - name: Create and start emulator
        run: |
          dotnet android avd create --name Tier0Emulator --sdk 'system-images;android-36;google_apis;x86_64' --force
          dotnet android avd start -p 5554 --name Tier0Emulator --no-window --gpu swiftshader_indirect \
            --no-snapshot --no-audio --no-boot-anim --wait --no-animations --cpu-threshold 3 --response-threshold 5
      # VSTest target, not `dotnet test` - DeviceRunners sets IsTestingPlatformApplication=false, so
      # MTP's `dotnet test` filters the project out and exits 1. ci.yml's device lanes say the same.
      - name: Run the smoke on the emulator
        run: >
          dotnet build ${{ env.DEVICE }}
          -f net10.0-android -c Release -t:VSTest
          -p:AndroidSdkDirectory=$ANDROID_SDK_ROOT
          -p:VSTestLogger=trx%3BLogFileName=tier0-android.trx

  tier0-ios:
    runs-on: macos-15-intel
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0, filter: 'tree:0' }
      - uses: maxim-lobanov/setup-xcode@v1
        with: { xcode-version: '26.2' }
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - run: dotnet workload install maui --version 10.0.201
      - run: dotnet tool restore
      - name: Create and boot a simulator
        run: |
          NAME="Tier0-$GITHUB_RUN_ID-ios"
          UDID=$(dotnet apple simulator create "$NAME" --device-type "iPhone 16" --format json | jq -r '.udid')
          echo "SIMULATOR_UDID=$UDID" >> "$GITHUB_ENV"
          dotnet apple simulator boot "$UDID" --wait
      - name: Run the smoke on the simulator
        run: >
          dotnet build ${{ env.DEVICE }}
          -f net10.0-ios -r iossimulator-x64 -c Release -t:VSTest
          -p:DeviceRunnersDevice=$SIMULATOR_UDID
          -p:VSTestLogger=trx%3BLogFileName=tier0-ios.trx

  # THE go/no-go. Spec 16.0 item 1(a): the native package's maccatalyst build folder is a
  # zero-byte `_._` placeholder (measured), so the natives can only arrive through the SDK's
  # RID-graph fallback to runtimes/ios/native/. The failure mode is a runtime DllNotFoundException,
  # which is why no restore assertion substitutes for this job.
  tier0-maccatalyst:
    runs-on: macos-15-intel
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0, filter: 'tree:0' }
      - uses: maxim-lobanov/setup-xcode@v1
        with: { xcode-version: '26.2' }
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - run: dotnet workload install maui --version 10.0.201
      - run: dotnet tool restore
      - name: Run the smoke on Mac Catalyst
        run: >
          dotnet build ${{ env.DEVICE }}
          -f net10.0-maccatalyst -r maccatalyst-x64 -c Release -t:VSTest
          -p:VSTestLogger=trx%3BLogFileName=tier0-maccatalyst.trx
```

- [ ] **Step 4: Verify — the Windows console leg runs here, over the real fixture**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
$out = dotnet run --project "$root\chat\tools\tier0-genai-smoke\Qavren.Edge.Tier0Smoke\Qavren.Edge.Tier0Smoke.csproj" `
  -c Release -p:ArtifactsPath=D:\Local\Temp\qedge-sp4\w1\t15
if ($LASTEXITCODE -ne 0) { throw "TIER 0 FAILED on win-x64 net10.0 - this is a go/no-go, not a flake" }
$out | Write-Host
foreach ($k in 'ichatClientAssignable=True','firstTokenId=','firstTokenText=','modelLoadMs=') {
  if (($out -join "`n") -notmatch [regex]::Escape($k)) { throw "tier 0 did not report $k" }
}
Write-Host 'OK: tier 0 (c) and (b)-on-Windows-net10.0 closed over a real model load and a real token'
```

Expected: the key/value block, a non-empty `firstTokenText`, and the `OK` line. This is §16.0's answer (c) closed for the second time — Environment ground truth closed it over an `OgaHandle` alone; this closes it over `Model`, `Tokenizer`, `GeneratorParams` and `Generator`, which is the surface SP4 actually binds to.

- [ ] **Step 5: Verify — all four device-head TFMs compile here**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
$dev = "$root\chat\tools\tier0-genai-smoke\Qavren.Edge.Tier0Smoke.Device\Qavren.Edge.Tier0Smoke.Device.csproj"
foreach ($tfm in 'net10.0-android','net10.0-ios','net10.0-maccatalyst','net10.0-windows10.0.19041.0') {
  dotnet build $dev -f $tfm -c Release -p:ArtifactsPath=D:\Local\Temp\qedge-sp4\w1\t15
  if ($LASTEXITCODE -ne 0) { throw "TIER 0 device head failed to COMPILE for $tfm" }
}
Write-Host 'OK: the tier-0 device head compiles for all four platform TFMs'
```

**`-f <tfm>` and no `-p:TargetFrameworks`** — adjustment 12's exception. An android manifest-merge failure here is GenAI's `minSdkVersion 24` answering the floor question before wave 7 has to.

- [ ] **Step 6: Write the fallback down, before anybody needs it**

Append to `chat/tools/tier0-genai-smoke/README.md` — literal, because a contingency an implementer has to invent under time pressure is not a contingency:

> **If `tier0-maccatalyst` fails with a `DllNotFoundException` (§16.0 item 1(a) says no):**
> 1. delete `net10.0-maccatalyst` from `<TargetFrameworks>` in `chat/src/Qavren.Edge.Chat.Onnx/Qavren.Edge.Chat.Onnx.csproj` and `chat/tests/Qavren.Edge.Chat.Tests/Qavren.Edge.Chat.Tests.csproj`, and from this device head;
> 2. delete the `'net10.0-maccatalyst' = 'lib/net9.0-maccatalyst14.0/…'` row from `ci.yml`'s GenAI asset assertion and its TFM from the ORT floor guard's list (Task 2.3);
> 3. delete `"lib/net9.0-maccatalyst14.0"` from `assert-workflows.py`'s SP4 token list (Task 2.3);
> 4. delete the `maccatalyst` `SupportedOSPlatformVersion` line from the two SP4 csprojs, and drop Mac Catalyst from Task 7.1's and Task 8.1's TFM loops;
> 5. state in `chat/README.md`: **"Mac Catalyst has no chat in v1 — ORT GenAI ships no Mac Catalyst native and the SDK's RID-graph fallback to the iOS slice does not load."**
> 6. record it here as adjustment 21(a) in the plan's **Spec adjustments**.
>
> **If `tier0-android` or `tier0-ios` fails**, the sub-project has no mobile chat and the whole TFM table is reopened — stop and escalate rather than editing anything.

- [ ] **Step 7: Verify — and this is where `pyyaml` is first installed on this box**

**The interpreter is named absolutely and `pyyaml` is installed into that same interpreter, in this step.** This is the first step in the plan that parses YAML, and it is in the one wave that gates every later wave — so it cannot borrow Task 2.3's install, which lives two waves downstream. A bare `python` here resolves to the Windows Store alias or dies on `ModuleNotFoundError: yaml`, which would fail wave 1 for a reason that has nothing to do with tier 0. Every later YAML step (Tasks 2.3, 2.4, 8.1) re-runs the same idempotent install against the same path.

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
$py   = "C:\Python314\python.exe"
if (-not (Test-Path $py)) { throw "expected CPython 3.14.5 at $py (Environment ground truth)" }
& $py -m pip install --quiet --disable-pip-version-check pyyaml
if ($LASTEXITCODE -ne 0) { throw "could not install pyyaml into $py" }
& $py -c "import yaml; print('pyyaml', yaml.__version__)"
$wf = "$root\.github\workflows\tier0-genai-smoke.yml"
if (-not (Test-Path $wf)) { throw "tier0-genai-smoke.yml was not created" }
& $py -c "import yaml,sys; d=yaml.safe_load(open(sys.argv[1],encoding='utf-8')); t=d.get('on', d.get(True)); assert t=='workflow_dispatch' or list(t or [])==['workflow_dispatch'], f'trigger is {t!r}, must be workflow_dispatch only'; assert len(d['jobs'])==6, f'expected 6 jobs, found {len(d[\"jobs\"])}'; print('OK: tier0 workflow parses, workflow_dispatch-only, 6 jobs')" $wf
if ($LASTEXITCODE -ne 0) { throw "tier0-genai-smoke.yml failed its parse/shape check" }
```

Expected: the `pyyaml <version>` line and the `OK:` line. Step 0's `OK`, Step 4's `OK` and Step 5's `OK` must all have printed as well. The remaining five legs are Task 1.6 Step 3's dispatch.

The `on:`-parses-as-`True` reading is YAML 1.1's boolean coercion, the same trap `assert-workflows.py` handles in Task 2.3 — it is written the same way in both places on purpose.

---

### Task 1.6: Wave 1 close — sequential re-verify, tier-0 dispatch, and commit *(integrator)*

**Local-verifiable:** Steps 1–2 and 4–5 yes; Step 3's **workflow run** is CI's, and it is the plan's one hard hand-off.

**Files:** none of its own.

**Approach.** The parallel phase ran under `-p:ArtifactsPath=D:\Local\Temp\qedge-sp4\w1\t…`, so no two tasks contended for one `obj/`. That isolation is exactly why the wave is **not** yet proved: nothing has built these projects in the tree the next wave, and CI, will use. And wave 1 has one more obligation no other wave has — **tier 0's five remaining legs have to report before wave 2 writes a line of API** (adjustment 21).

- [ ] **Step 1: Clear the wave's isolated outputs**

```powershell
Remove-Item -Recurse -Force "D:\Local\Temp\qedge-sp4\w1" -ErrorAction SilentlyContinue
```

- [ ] **Step 2: Re-run every verify sequentially, in the real tree**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
dotnet run --project "$root\foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0
if ($LASTEXITCODE -ne 0) { throw "FAILED: Core.Tests" }
dotnet run --project "$root\chat\tools\tier0-genai-smoke\Qavren.Edge.Tier0Smoke\Qavren.Edge.Tier0Smoke.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "FAILED: tier-0 windows console leg" }
dotnet restore "$root\QavrenEdge.slnx"
if ($LASTEXITCODE -ne 0) { throw "FAILED: solution restore" }
Write-Host 'OK: wave 1 re-verified sequentially'
```

Re-run Task 1.1 Step 8's asset assertion, Task 1.2 Step 5's anchor check, Task 1.5 Step 5's four device-head compiles and Task 1.5 Step 7's YAML parse as well.

**Review the one delegated root file before committing it (adjustment 30).** `.github/workflows/tier0-genai-smoke.yml` was written by Task 1.5's implementer under a named carve-out, and the integrator is still the one who commits it: confirm it is `workflow_dispatch`-only, that no `tier0-*` job appears in `ci.yml`, and that no *other* file under `.github/`, `QavrenEdge.slnx`, `Directory.Packages.props`, `Directory.Build.*` or `.gitignore` was touched by any implementer task in this wave.

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
$roots = git -C $root diff --name-only HEAD -- '.github/**' 'QavrenEdge.slnx' 'Directory.Packages.props' 'Directory.Build.props' 'Directory.Build.targets' '.gitignore'
$allowed = @('.github/workflows/tier0-genai-smoke.yml','QavrenEdge.slnx','Directory.Packages.props','.gitignore')
foreach ($f in $roots) { if ($allowed -notcontains $f) { throw "root file '$f' was modified and is not the integrator's own work nor the one named carve-out (plan adjustment 30)" } }
Write-Host 'OK: exactly the expected root files changed; no second carve-out appeared'
```

- [ ] **Step 3: Commit the wave, push, and dispatch tier 0**

One commit, Conventional Commits, no `Co-Authored-By` and no AI attribution, on `feat/sp4-chat`. Never on `main`. (The two cherry-picks from Task 1.1 Step 0 are already their own commits and keep their original messages.)

```
feat(sp4): skeleton, the GenAI pin, docs, tier-2 fixture, tier-0 smoke and the 7000-7299 error range

- chat/ projects, .slnx entries and one PackageVersion (ORT GenAI 0.15.2)
- ADRs 0009-0013, chat/README.md, foundation/docs/errors.md 7000-7299
- pinned uv venv + make_tiny_chat_model.py + the committed TinyChatModel.g.cs
- tier-0 link-and-generate smoke: console + MAUI head + tier0-genai-smoke.yml
- EdgeErrorCode gains 7000-7299 (the one SP1 library edit, additive)
```

Then push the branch and dispatch:

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
git -C $root push -u origin feat/sp4-chat
gh workflow run tier0-genai-smoke.yml --ref feat/sp4-chat -R qavren-oss/qavren-edge
gh run watch -R qavren-oss/qavren-edge (gh run list -R qavren-oss/qavren-edge -w tier0-genai-smoke.yml -L 1 --json databaseId -q '.[0].databaseId')
```

**This is the plan's one hard hand-off.** If the integrator cannot push — branch protection, credentials, anything — it is the owner's single click, and wave 2 waits for it. Do not start Task 2.1 with tier 0 unreported; the whole point of §16.0 is that a TFM cut found later is a rewrite.

- [ ] **Step 4: Record the six answers**

For each of `tier0-windows`, `tier0-linux`, `tier0-winui`, `tier0-android`, `tier0-ios`, `tier0-maccatalyst`, write pass/fail and the printed `key=value` block into `chat/README.md`'s "What is proven and what is not" section, and into this plan's **Spec adjustments** as adjustment 21's closing note. If `tier0-maccatalyst` failed, execute Task 1.5 Step 6's six-item fallback **now**, in this wave, before wave 2 starts.

- [ ] **Step 5: Verify**

Step 2 printed its `OK` line, `git status --short` is clean, and all six tier-0 legs are green **or** the fallback in Step 4 has been executed and recorded. Wave 2 may start.

---
## WAVE 2 — `Qavren.Edge.Rag` foundations, the preset catalogue, and CI

### Task 2.1: `Qavren.Edge.Rag` part 1 — sources, the retrieval seam, prompts and citations

**Local-verifiable:** yes.

**Files:**
- Create: `chat\src\Qavren.Edge.Rag\RagSource.cs` — `RetrievalScoreKind`, `RagSource`, `RetrievalRequest`
- Create: `chat\src\Qavren.Edge.Rag\IEdgeRetriever.cs` — `IEdgeRetriever`, `DelegateRetriever`
- Create: `chat\src\Qavren.Edge.Rag\RagOptions.cs` — `RetrievalQuerySource`, `RagOptions`, `ExtractiveChatOptions`
- Create: `chat\src\Qavren.Edge.Rag\RagPrompts.cs` — `RagPrompts.Format` / `Rank` and the three prompt constants
- Create: `chat\src\Qavren.Edge.Rag\RagCitations.cs` — the three property keys, `Build`, `AttachTo`
- Create: `chat\src\Qavren.Edge.Rag\EdgeRagEventIds.cs`
- Create: `chat\src\Qavren.Edge.Rag\RagExceptions.cs` — `EdgeRagException`
- Test: `chat\tests\Qavren.Edge.Rag.Tests\RagPromptsFormatTests.cs`
- Test: `chat\tests\Qavren.Edge.Rag.Tests\RagPromptsRankTests.cs`
- Test: `chat\tests\Qavren.Edge.Rag.Tests\RagCitationsBuildTests.cs`

**Approach — read this before writing.** Every public type here is declared as literal C# in spec §7; transcribe those blocks **verbatim, XML docs included**. The docs on `RagSource.ScoreKind`, on `RagOptions.ContextRole` and on `RagCitations.SourcesPropertyKey` are contract, not decoration — the first prevents an inverted ranking, the second is the entire prompt-injection posture, and the third is the channel `ExtractiveChatClient` depends on.

Three bodies are this task's real work and all three are pure functions, which is why they are tier-1 golden-string targets rather than integration tests.

**`RagPrompts.Rank`** normalises to best-first **without inventing a number**: a `Distance` list ascending, a `Relevance` list descending, nothing rescaled. There is no defensible scale to rescale onto, and a min-max normalisation over five results is a number that looks like a confidence and is not one.

**`RagPrompts.Format`** numbers, clamps, orders and renders. The block is byte-for-byte:

```
## Additional context
<options.ContextPrompt>

[1] Title: <title>
    Source: <uri>
    ---
    <clamped chunk text>

[2] …

<options.CitationsPrompt>
```

A source with no `Title` omits the `Title:` line; one with no `Uri` omits the `Source:` line; the `---` separator is always present. Clamping appends a visible `…(truncated)` so a reader can see the model was shown less than the whole chunk. `Ordinal` is assigned **here**, 1..n, after ranking and clamping — never by a projector and never by a retriever.

**`RagCitations.Build`** scans `\[(\d+)\]` over the complete answer and emits one `CitationAnnotation { Title, Url, FileId = source.Id, Snippet }` per **distinct** marker, each carrying a `TextSpanAnnotatedRegion(StartIndex, EndIndex)` **per occurrence**. It is a total function: it does not throw, and a marker with no matching source is left as plain text and counted, **never fabricated into a citation**. `StartIndex`/`EndIndex` are `int?` on the MEAI type (measured — adjustment 9); `Build` always sets both.

**Defaults that change behaviour.** `RagOptions.Top = 5`; `MaxContextTokens = 1024`; `MaxCharsPerSource = 1200`; `QuerySource = LastUserMessage`; **`ContextRole = ChatRole.User` and never `System`** — retrieved text is untrusted input and must not reach the model as instruction, and §7 documents that this role choice plus the instruction prefix is the whole of SP4's posture; `EmitCitations = true`; `AttachSourcesToResponse = true`; `ContinueOnRetrievalFailure = **true**` (a retrieval failure is logged and the turn continues *ungrounded and marked as such*, not thrown); `ShortCircuitOnNoContext = true`; a null `TokenCounter` uses a chars/4 estimate; a null `KeywordExtractor` splits on non-letters, drops a small stop list, keeps three-plus characters and caps at eight. `ExtractiveChatOptions.MaxSources = 3`, `MaxCharsPerSource = 600`.

- [ ] **Step 1: Write the failing tests first**

- `RagPromptsFormatTests`: a golden-string assertion on a three-source block, byte for byte, including the truncation suffix and the two missing-metadata variants; a budget so small that nothing fits → `RagContextBudgetTooSmall` (7204); a budget that fits two of three → the third is dropped **from the tail** and the survivors renumber 1 and 2.
- `RagPromptsRankTests`: a `Distance` list comes out ascending, a `Relevance` list descending, and the same input scores produce **opposite** orders under the two kinds — the test name states the inversion, because that is the bug the type exists to prevent. Assert that no `Score` value is modified by `Rank`.
- `RagCitationsBuildTests`: spans slice the accumulated answer back to the marker text exactly; a duplicate marker produces **one** annotation with **two** regions; a `[7]` against five sources is dropped and counted; a marker inside a code fence is treated like any other marker (documented behaviour, not an exception); an answer with no markers produces an empty list and does not throw.

- [ ] **Step 2: Implement the seven files**

Types exactly as §7 declares them. `EdgeRagEventIds` is 960–999 and is published **from this package**, because `Qavren.Edge.Rag` does not reference `Qavren.Edge.Chat.Onnx` and a constant declared there would be unreachable from the code that logs it.

- [ ] **Step 3: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp4\chat\tests\Qavren.Edge.Rag.Tests\Qavren.Edge.Rag.Tests.csproj" `
  -c Release -p:TargetFrameworks=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp4\w2\t21
```

Expected: all tests pass, exit 0. Single-TFM project, so `-p:TargetFrameworks=net10.0` and **no** `-f`.

---

### Task 2.2: The preset catalogue — the `paths-info` fetch script and the literal constants

**Local-verifiable:** yes.

**Files:**
- Create: `chat\tools\model-hashes\requirements.txt`
- Create: `chat\tools\model-hashes\fetch_chat_model_hashes.py`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\ChatPresets.g.cs` (generated, committed)
- Create: `chat\tests\fixtures\GenAiConfigFixtures.g.cs` (generated, committed — the tier-1 config fixtures, adjustment 24)

**Approach — read this before writing.** This task compiles nothing, and **that is load-bearing**: `ChatPresets.g.cs` lands in a project no wave-2 build touches, so `dotnet build QavrenEdge.slnx` fails between this task and Task 3.1 (adjustment 13). Wave 2's close runs `dotnet restore` and the two suites, never a solution build.

Every value the generator emits was fetched on this box on 2026-09-11 and is in **Environment ground truth**, so an implementer can write the file with no network at all and the verify is a regeneration diff.

Three mechanics, all inherited from SP2 and all of them things that bite silently if got wrong:

1. **`HuggingFaceRevision` is a full commit SHA, never `"main"`.** A moving revision silently changes the weights, and weights from two revisions are not the same model.
2. **A `paths-info` `oid` is the SHA-256 only when the file is LFS-backed.** Measured here: `model.onnx`, `model.onnx.data` and `tokenizer.json` carry an `lfs: { oid, size }` sub-object holding a 64-hex digest; `genai_config.json`, `tokenizer_config.json` and `chat_template.jinja` are plain git blobs whose top-level `oid` is a 40-hex **SHA-1**. Baking a SHA-1 into a manifest fails every provisioning verify at runtime with a hash nobody can debug. The script takes `lfs.oid` when present and **downloads and hashes** the file when it is not — 9 KB in total across both presets, paid once, by hand, never in CI.
3. **`GraphFile` names the decoder `.onnx`, not `genai_config.json`.** That is what keys the content-addressed directory `<Models>/<modelId>/<sha16>` on the **weights**, which is the correct identity (§5). Nominating the 1.5 KB config would key a digest that does not move when `model.onnx.data` does.

`requirements.txt` is deliberately **empty of third-party packages** — the script uses `urllib`, `hashlib` and `json` from the standard library only, so the venv exists to pin the interpreter and nothing else:

```
# Sub-project 4 preset-hash toolchain. Run BY HAND; never a build step and never CI.
# Deliberately EMPTY of third-party packages: the script uses urllib, hashlib and json from the
# standard library, so a pinned requests/huggingface_hub here would be three more supply-chain
# edges for two POSTs and six GETs. Python 3.14, matching the fixture venv.
```

- [ ] **Step 1: `fetch_chat_model_hashes.py`**

The script, per preset:

1. `GET https://huggingface.co/api/models/{repo}` → take `sha` as the revision, unless `--revision` pins one (it does, for reproducibility: the two SHAs are in Environment ground truth).
2. `POST https://huggingface.co/api/models/{repo}/paths-info/{rev}` with the six paths → sizes and oids.
3. For each returned entry: digest = `lfs.oid` when present, else `GET .../resolve/{rev}/{path}` and `sha256` the bytes. **Assert the downloaded length equals the `paths-info` size** before hashing — a truncated body that hashes cleanly is the one failure this check catches.
4. `GET .../resolve/{rev}/genai_config.json` → parse `model.type`, `model.context_length`, `model.vocab_size`, `model.decoder.{filename, num_hidden_layers, num_key_value_heads, head_size, sliding_window}`. `sliding_window` is emitted as `null` when absent. **No geometry value is ever hand-typed.**
5. Also read the shipped `search` block and emit `DefaultTemperature` / `DefaultTopP` / `DefaultTopK` from it (adjustment 15).
6. Emit `ChatPresets.g.cs`.
7. **Emit `chat/tests/fixtures/GenAiConfigFixtures.g.cs` — the tier-1 config fixtures (adjustment 24).** The script already has both `genai_config.json` bodies in hand from step 4, so it writes them out as C# rather than leaving §16.1's "one committed text fixture" to be retyped by hand into something that then drifts from the file the provisioner will verify on a user's device.

**The `--fixtures` output, and the three things that make it byte-exact.** The generator asserts, before emitting and failing loudly on any of them:

- the downloaded bytes decode as UTF-8 with **no BOM**;
- they contain **no `\r`** — that is what makes a C# raw-string literal byte-faithful across a git checkout that might normalise line endings, and both files are LF-only as fetched (measured);
- they contain **no `"""` sequence**, so the raw-string literal is unambiguous.

The emitted file, shape-literal:

```csharp
// <auto-generated />
// Generated by chat/tools/model-hashes/fetch_chat_model_hashes.py on 2026-09-11.
// DO NOT EDIT BY HAND. These are the two presets' genai_config.json bodies EXACTLY as published at
// the pinned revisions - the same bytes ChatPresets.g.cs pins for provisioning. The digests below
// are asserted by a tier-1 test (plan adjustment 24), so a hand-edit that "tidies" the JSON turns
// every section 16.1 shape assertion loud instead of silently wrong.

namespace Qavren.Edge.Chat.Tests.Fixtures;

internal static class GenAiConfigFixtures
{
    /// <summary>Arm/llama-3-2-1b-instruct-... at 333c515af8b9355011c0b295c4356c9c24b463ff.</summary>
    public const string LlamaConfigJson = """
{
  "model": { ... exactly as published ... }
}
""";
    public const string LlamaConfigSha256 = "359d47db9f4e3626bd51a4b5ccf839489dfbe099e46e039dc63c0ca0118f4f1c";
    public const int LlamaConfigLength = 1_536;

    /// <summary>Arm/qwen3-0-6b-... at c1d7bbbbb20630eef24c00d8ad18250bd57b232c.</summary>
    public const string QwenConfigJson = """
{
  "model": { ... exactly as published ... }
}
""";
    public const string QwenConfigSha256 = "67348085a5b994a6b6d94e56e395e675d8ef4732c3c46ff75de30a541489fd3f";
    public const int QwenConfigLength = 1_520;
}
```

The digests are the **same two literals** `ChatPresets.g.cs` carries for those files, and Task 3.1's `GenAiConfigFixtureTests` is what ties them together: it hashes `Encoding.UTF8.GetBytes(body.ReplaceLineEndings("\n"))` and asserts both the digest and the length. Fixture and manifest cannot drift.

Three assertions the script makes before it writes anything, each of which turns a silent wrong answer into a loud one:

- every revision is 40 hex characters and **not** the string `main`;
- no emitted digest is 40 hex characters (that would be a git SHA-1 where a SHA-256 belongs — the exact defect SP2 adjustment 19 found);
- the sum of the `Graph` and `GraphExternalData` sizes equals the `WeightsBytes` it emits, and that is **not** `TotalSizeBytes` — a 17 MB `tokenizer.json` is not resident weights, and counting it would over-report by tens of megabytes inside a user-visible refusal message.

- [ ] **Step 2: Run it and commit the output**

```powershell
$t = "C:\Users\steve\projects\qavren-edge-sp4\chat\tools\model-hashes"
uv venv --python 3.14 "$t\.venv"
& "$t\.venv\Scripts\python.exe" "$t\fetch_chat_model_hashes.py" `
  --out "C:\Users\steve\projects\qavren-edge-sp4\chat\src\Qavren.Edge.Chat.Onnx\ChatPresets.g.cs" `
  --fixtures "C:\Users\steve\projects\qavren-edge-sp4\chat\tests\fixtures\GenAiConfigFixtures.g.cs"
```

The file it must produce — every literal below was measured on this box, so a byte-identical regeneration is the verify:

```csharp
// <auto-generated />
// Generated by chat/tools/model-hashes/fetch_chat_model_hashes.py on 2026-09-11.
// DO NOT EDIT BY HAND. Re-run the script and commit the diff; a changed digest is a deliberate
// decision about which weights ship, never a merge conflict to resolve.
//
// Every geometry value below is read from the repo's own genai_config.json at the pinned commit,
// and every digest is the git-LFS oid where the file is LFS-backed and a downloaded-and-hashed
// SHA-256 where it is not. No value here is hand-typed.

using Qavren.Edge.Onnx;

namespace Qavren.Edge.Chat;

public static partial class ChatPresets
{
    /// <summary>
    /// <c>Arm/llama-3-2-1b-instruct-onnx-genai-int4-kquantlast-emb-int8-vivo-x300</c>.
    /// 1.241 GB decimal on disk across six files; 1167.52 MiB of resident weights; context 4096 as
    /// shipped (the publisher patched it down from Llama's stock 131072 so the KV cache fits a
    /// phone). Geometry: 16 layers, 8 KV heads, head size 64, vocab 128256 - <b>32 KiB of KV per
    /// token</b>. Measured on a vivo X300 (Android 16, ORT 1.27.0 CPU EP, 4 threads): 30.564 tok/s
    /// decode, TTFT 950 ms, peak RSS 1280 MiB, model load 1.61 s, MMLU 5-shot 44.2%.
    /// <b>No iOS figure exists for this or any other ORT GenAI model.</b>
    /// </summary>
    public static ChatPreset Llama32_1BInstructInt4 { get; } = new()
    {
        Id = "llama-3.2-1b-instruct-int4",
        DisplayName = "Llama 3.2 1B Instruct (int4)",
        LicenseUri = new Uri("https://huggingface.co/meta-llama/Llama-3.2-1B-Instruct/blob/main/LICENSE.txt"),
        Manifest = new OnnxModelManifest
        {
            ModelId = "llama-3.2-1b-instruct-int4",
            GraphFile = "model.onnx",
            SpdxLicense = "LicenseRef-LLAMA-3.2-Community",
            HuggingFaceRepo = "Arm/llama-3-2-1b-instruct-onnx-genai-int4-kquantlast-emb-int8-vivo-x300",
            HuggingFaceRevision = "333c515af8b9355011c0b295c4356c9c24b463ff",
            Files =
            [
                new OnnxModelFile("model.onnx", OnnxModelFileRole.Graph, 141_626,
                    "742f2292f396bbc65a511c361e37e1b1bcef40ebb38003aab38461549e7493a4"),
                new OnnxModelFile("model.onnx.data", OnnxModelFileRole.GraphExternalData, 1_224_094_720,
                    "ab128a60ea1be913b37f91ab305c059cf36b88383d7e9b907bfcaf0460b859bd"),
                new OnnxModelFile("genai_config.json", OnnxModelFileRole.Auxiliary, 1_536,
                    "359d47db9f4e3626bd51a4b5ccf839489dfbe099e46e039dc63c0ca0118f4f1c"),
                new OnnxModelFile("tokenizer.json", OnnxModelFileRole.Auxiliary, 17_209_920,
                    "6b9e4e7fb171f92fd137b777cc2714bf87d11576700a1dcd7a399e7bbe39537b"),
                new OnnxModelFile("tokenizer_config.json", OnnxModelFileRole.Auxiliary, 353,
                    "502d3a27e52540463d85a947099459fb0f8aea88dd1cf9889368576de89dd668"),
                new OnnxModelFile("chat_template.jinja", OnnxModelFileRole.Auxiliary, 3_827,
                    "5816fce10444e03c2e9ee1ef8a4a1ea61ae7e69e438613f3b17b69d0426223a4"),
            ],
        },
        Shape = new ChatModelShape
        {
            ModelType = "llama",
            ContextLength = 4096,
            VocabSize = 128_256,
            NumHiddenLayers = 16,
            NumKeyValueHeads = 8,
            HeadSize = 64,
            SlidingWindow = null,
            DecoderFileName = "model.onnx",
            WeightsBytes = 1_224_236_346L,
            MeasuredPeakBytes = 1_342_177_280L,   // 1280 MiB
            MeasuredOn = "vivo X300, Android 16, ORT 1.27.0 CPU EP, 4 threads",
        },
        StopSequences = ["<|eot_id|>", "<|eom_id|>", "<|end_of_text|>"],
        DefaultMaxContextTokens = 4096,
        DefaultMaxOutputTokens = 512,
        DefaultTemperature = 0.6f,
        DefaultTopP = 0.9f,
        DefaultTopK = 50,
    };

    /// <summary>
    /// <c>Arm/qwen3-0-6b-onnx-genai-int4-kquantlast-emb-int4</c>. 495 MB decimal on disk across six
    /// files; 461.25 MiB of resident weights; Apache-2.0 - the preset for an app that cannot take
    /// the Llama terms, and the model the nightly lane uses.
    /// <para>
    /// <b>Smaller on disk is not smaller in memory, and here are the numbers.</b> 28 layers, 8 KV
    /// heads, head size 128 - <b>112 KiB of KV per token</b>, 3.5x this catalogue's larger preset,
    /// against a third of the weights. At 4096 tokens its KV cache alone is 448 MiB. Its declared
    /// <c>context_length</c> is <b>40960</b>, so a generator built with no <c>max_length</c> would
    /// allocate <b>4480 MiB</b> of KV cache - which is exactly the jetsam scenario the memory budget
    /// and the belt-and-braces <c>search.max_length</c> exist to prevent.
    /// </para>
    /// <para>
    /// Its only published throughput is AWS Graviton (70.4 tok/s, peak 838 MB decimal), and that
    /// figure is deliberately <b>not</b> in <c>MeasuredPeakBytes</c>: the budget reads that field
    /// alone and has no way to discount a measurement by where it was taken, so a server peak
    /// entering it would silently become a phone budget.
    /// </para>
    /// </summary>
    public static ChatPreset Qwen3_600MInt4 { get; } = new()
    {
        Id = "qwen3-0.6b-int4",
        DisplayName = "Qwen3 0.6B (int4)",
        LicenseUri = new Uri("https://www.apache.org/licenses/LICENSE-2.0"),
        Manifest = new OnnxModelManifest
        {
            ModelId = "qwen3-0.6b-int4",
            GraphFile = "model.onnx",
            SpdxLicense = "Apache-2.0",
            HuggingFaceRepo = "Arm/qwen3-0-6b-onnx-genai-int4-kquantlast-emb-int4",
            HuggingFaceRevision = "c1d7bbbbb20630eef24c00d8ad18250bd57b232c",
            Files =
            [
                new OnnxModelFile("model.onnx", OnnxModelFileRole.Graph, 331_869,
                    "e9208ab7164ea6da0206eec2a2f0996a24e75d164f85823b02111f23046aab89"),
                new OnnxModelFile("model.onnx.data", OnnxModelFileRole.GraphExternalData, 483_328_000,
                    "52640ca0d65e00d33dfb10b822c6a41e31bab1aaa6a49457b1dc5952c0dab0fb"),
                new OnnxModelFile("genai_config.json", OnnxModelFileRole.Auxiliary, 1_520,
                    "67348085a5b994a6b6d94e56e395e675d8ef4732c3c46ff75de30a541489fd3f"),
                new OnnxModelFile("tokenizer.json", OnnxModelFileRole.Auxiliary, 11_422_650,
                    "be75606093db2094d7cd20f3c2f385c212750648bd6ea4fb2bf507a6a4c55506"),
                new OnnxModelFile("tokenizer_config.json", OnnxModelFileRole.Auxiliary, 376,
                    "c56d783875898cebcf53c8797b61137efd52f1415be7531823b56f216f444527"),
                new OnnxModelFile("chat_template.jinja", OnnxModelFileRole.Auxiliary, 4_168,
                    "a55ee1b1660128b7098723e0abcd92caa0788061051c62d51cbe87d9cf1974d8"),
            ],
        },
        Shape = new ChatModelShape
        {
            ModelType = "qwen3",
            ContextLength = 40_960,
            VocabSize = 151_936,
            NumHiddenLayers = 28,
            NumKeyValueHeads = 8,
            HeadSize = 128,
            SlidingWindow = null,
            DecoderFileName = "model.onnx",
            WeightsBytes = 483_659_869L,
            MeasuredPeakBytes = null,
            MeasuredOn = "AWS Graviton g4 (published 70.4 tok/s, peak 838 MB decimal) - a server "
                       + "measurement, recorded here and deliberately NOT in MeasuredPeakBytes",
        },
        StopSequences = ["<|im_end|>", "<|endoftext|>"],
        DefaultMaxContextTokens = 4096,
        DefaultMaxOutputTokens = 512,
        DefaultTemperature = 0.6f,
        DefaultTopP = 0.95f,
        DefaultTopK = 20,
    };

    private static readonly ChatPreset[] AllPresets = [Llama32_1BInstructInt4, Qwen3_600MInt4];
}
```

`ChatPresets`'s hand-written half — `All`, `ById` and the `partial` keyword's other side — is **Task 3.1's**, because both need types that do not exist yet.

- [ ] **Step 3: Verify — regenerate and diff**

```powershell
$t = "C:\Users\steve\projects\qavren-edge-sp4\chat\tools\model-hashes"
$out = "C:\Users\steve\projects\qavren-edge-sp4\chat\src\Qavren.Edge.Chat.Onnx\ChatPresets.g.cs"
$fix = "C:\Users\steve\projects\qavren-edge-sp4\chat\tests\fixtures\GenAiConfigFixtures.g.cs"
Copy-Item $out "$env:TEMP\ChatPresets.first.cs" -Force
Copy-Item $fix "$env:TEMP\GenAiConfigFixtures.first.cs" -Force
& "$t\.venv\Scripts\python.exe" "$t\fetch_chat_model_hashes.py" --out $out --fixtures $fix
foreach ($pair in @(@($out, "$env:TEMP\ChatPresets.first.cs"), @($fix, "$env:TEMP\GenAiConfigFixtures.first.cs"))) {
  if ((Get-FileHash $pair[0]).Hash -ne (Get-FileHash $pair[1]).Hash) {
    throw "regeneration of $($pair[0]) is not byte-identical - the upstream repo moved, or the script is not deterministic"
  }
}
$src = Get-Content $out -Raw
if ($src -match '"main"') { throw 'a revision is "main"' }
if ([regex]::Matches($src, '"[0-9a-f]{40}"').Count -ne 2) { throw "expected exactly two 40-hex strings (the two commit revisions) and no SHA-1 digests" }
if ([regex]::Matches($src, '"[0-9a-f]{64}"').Count -ne 12) { throw "expected twelve SHA-256 digests" }

# Adjustment 24: the tier-1 fixtures carry the SAME two config digests the manifest pins, and the
# bodies are LF-only so a raw-string literal is byte-faithful. Both are asserted again by a test in
# Task 3.1; this is the generator-side half.
$fsrc = Get-Content $fix -Raw
foreach ($d in '359d47db9f4e3626bd51a4b5ccf839489dfbe099e46e039dc63c0ca0118f4f1c',
               '67348085a5b994a6b6d94e56e395e675d8ef4732c3c46ff75de30a541489fd3f') {
  if ($fsrc -notmatch $d) { throw "GenAiConfigFixtures.g.cs does not carry the pinned digest $d" }
  if ($src  -notmatch $d) { throw "ChatPresets.g.cs does not carry the pinned digest $d" }
}
foreach ($n in 'LlamaConfigLength = 1_536','QwenConfigLength = 1_520') {
  if ($fsrc -notmatch [regex]::Escape($n)) { throw "GenAiConfigFixtures.g.cs is missing $n" }
}
if ((Get-Content $fix -Raw) -match "`r`n(?=[^/])") { Write-Host 'NOTE: the .g.cs file itself is CRLF on disk; the test normalises line endings before hashing, which is why that is safe' }
Write-Host 'OK: both generated files byte-identical, two commit revisions, twelve SHA-256 digests, the two config fixtures digest-pinned'
```

Expected: the `OK` line. **Do not run `dotnet build` in this task** — the generated file references types Task 3.1 has not written yet, and that is by design (adjustment 13).

---

### Task 2.3: `ci.yml` chat steps, the two Windows assertions, the nightly lane, and the workflow contract *(integrator)*

**Local-verifiable:** yes — everything except the workflow *running*.

**Files:**
- Edit: `.github\workflows\ci.yml`
- Edit: `foundation\tools\ci-checks\assert-workflows.py`

**Approach.** This task compiles nothing and touches no path a `dotnet build` reads. It runs after Task 1.1's cherry-picks, so the workflow it edits is `origin/main`'s, not one two fixes behind.

- [ ] **Step 1: Two test steps in the `test` job**

Insert after the existing `VectorData conformance` step, following the job's explicit-enumeration convention:

```yaml
      - name: Chat tests
        run: dotnet run --project chat/tests/Qavren.Edge.Chat.Tests/Qavren.Edge.Chat.Tests.csproj -c Release -f net10.0 -p:TargetFrameworks=net10.0

      # Single-TFM project, so no -f. It is host-only and never runs on a device lane (spec 16.4).
      - name: Rag tests
        run: dotnet run --project chat/tests/Qavren.Edge.Rag.Tests/Qavren.Edge.Rag.Tests.csproj -c Release -p:TargetFrameworks=net10.0
```

- [ ] **Step 2: Two assertions on the `windows-2025` leg**

Insert after the existing `Assert ONNX Runtime asset resolution` step — it already ran `dotnet restore QavrenEdge.slnx`, which is the only image with every workload:

```yaml
      # Assertion 1: the GenAI MANAGED package's asset per TFM. Written against
      # Microsoft.ML.OnnxRuntimeGenAI.Managed on purpose: the native Microsoft.ML.OnnxRuntimeGenAI
      # package resolves ZERO compile assets on every TFM (measured), so an assertion written
      # against that id would pass vacuously forever - sub-project 2 adjustment 2's lesson, and the
      # `if ($native.compile)` line below is what makes the trap itself a test.
      - name: Assert ONNX Runtime GenAI asset resolution
        if: matrix.os == 'windows-2025'
        shell: pwsh
        run: |
          $assets = 'chat/src/Qavren.Edge.Chat.Onnx/obj/project.assets.json'
          if (-not (Test-Path $assets)) { throw "no project.assets.json at $assets" }
          $a = Get-Content $assets -Raw | ConvertFrom-Json
          $expected = [ordered]@{
            'net10.0'             = 'lib/net8.0/Microsoft.ML.OnnxRuntimeGenAI.dll'
            'net10.0-android'     = 'lib/net9.0-android31.0/Microsoft.ML.OnnxRuntimeGenAI.dll'
            'net10.0-ios'         = 'lib/net9.0-ios15.4/Microsoft.ML.OnnxRuntimeGenAI.dll'
            'net10.0-maccatalyst' = 'lib/net9.0-maccatalyst14.0/Microsoft.ML.OnnxRuntimeGenAI.dll'
          }
          foreach ($tfm in $expected.Keys) {
            $m = $a.targets.$tfm.'Microsoft.ML.OnnxRuntimeGenAI.Managed/0.15.2'
            if (-not $m) { throw "$tfm did not resolve Microsoft.ML.OnnxRuntimeGenAI.Managed 0.15.2" }
            $got = ($m.compile.PSObject.Properties.Name) -join ','
            if ($got -ne $expected[$tfm]) { throw "$tfm resolved '$got'; expected '$($expected[$tfm])'" }
            $native = $a.targets.$tfm.'Microsoft.ML.OnnxRuntimeGenAI/0.15.2'
            if ($native.compile) { throw "$tfm: the native package resolved compile assets; this assertion is scanning the wrong package id" }
            Write-Host "OK $tfm -> $got"
          }

      # Assertion 2: the ORT floor guard. GenAI 0.15.2 declares Microsoft.ML.OnnxRuntime >= 1.28.0
      # - a FLOOR, not a pin - and its native links ORT dynamically, negotiating through
      # OrtApiBase::GetApi, which fails only on an OLDER runtime. This is the guard that stops
      # someone "fixing" the version skew by downgrading ORT to match GenAI's floor.
      - name: Assert the ONNX Runtime floor and pin
        if: matrix.os == 'windows-2025'
        shell: pwsh
        run: |
          $assets = 'chat/src/Qavren.Edge.Chat.Onnx/obj/project.assets.json'
          $a = Get-Content $assets -Raw | ConvertFrom-Json
          foreach ($tfm in 'net10.0','net10.0-android','net10.0-ios','net10.0-maccatalyst') {
            $id = $a.targets.$tfm.PSObject.Properties.Name | Where-Object { $_ -like 'Microsoft.ML.OnnxRuntime/*' }
            $v  = [version]($id -replace '^.*/','')
            if ($v -lt [version]'1.28.0') { throw "$tfm resolved ORT $v, below GenAI's 1.28.0 floor" }
            if ($v -ne [version]'1.30.0') { throw "$tfm resolved ORT $v; the repo pin is 1.30.0 and GenAI's floor must not move it" }
            Write-Host "OK $tfm -> ORT $v"
          }
```

- [ ] **Step 3: The nightly `chat-model-tests` job**

Cloned from the existing `model-tests` job, with the measured Qwen digests. **Not** in `ci-gate`'s `needs`:

```yaml
  # Spec 16.3. The real model, nightly only. Qwen3 0.6B int4 - the smallest genai-config-ready
  # model, Apache-2.0 so its presence in a CI cache is not a licensing question, and the one whose
  # KV-heavy geometry (112 KiB/token, measured) the budget most needs to be right about. Keyed on
  # the CONTENT hash, and sha256sum -c runs on EVERY run including cache hits: a poisoned cache
  # entry is exactly the failure this lane would otherwise report as a quality regression.
  chat-model-tests:
    if: github.event_name == 'schedule' || github.event_name == 'workflow_dispatch'
    runs-on: ubuntu-24.04
    timeout-minutes: 45
    env:
      QAVREN_EDGE_CHAT_MODEL_REPO: Arm/qwen3-0-6b-onnx-genai-int4-kquantlast-emb-int4
      QAVREN_EDGE_CHAT_MODEL_REV: c1d7bbbbb20630eef24c00d8ad18250bd57b232c
      QAVREN_EDGE_CHAT_MODEL_SHA256: 52640ca0d65e00d33dfb10b822c6a41e31bab1aaa6a49457b1dc5952c0dab0fb
      QAVREN_EDGE_CHAT_GRAPH_SHA256: e9208ab7164ea6da0206eec2a2f0996a24e75d164f85823b02111f23046aab89
      QAVREN_EDGE_CHAT_TOKENIZER_SHA256: be75606093db2094d7cd20f3c2f385c212750648bd6ea4fb2bf507a6a4c55506
      QAVREN_EDGE_CHAT_MODEL_DIR: ${{ github.workspace }}/.chat-model-cache
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0, filter: 'tree:0' }
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }

      - uses: actions/cache@v6.1.0
        id: chat-model-cache
        with:
          path: ${{ env.QAVREN_EDGE_CHAT_MODEL_DIR }}
          key: qavren-edge-chat-model-${{ env.QAVREN_EDGE_CHAT_MODEL_SHA256 }}

      - name: Fetch the pinned chat model
        if: steps.chat-model-cache.outputs.cache-hit != 'true'
        run: |
          set -euo pipefail
          mkdir -p "$QAVREN_EDGE_CHAT_MODEL_DIR"
          base="https://huggingface.co/$QAVREN_EDGE_CHAT_MODEL_REPO/resolve/$QAVREN_EDGE_CHAT_MODEL_REV"
          for f in genai_config.json model.onnx model.onnx.data tokenizer.json tokenizer_config.json chat_template.jinja; do
            curl -fsSL "$base/$f" -o "$QAVREN_EDGE_CHAT_MODEL_DIR/$f"
          done

      - name: Verify the model hashes
        run: |
          set -euo pipefail
          echo "$QAVREN_EDGE_CHAT_GRAPH_SHA256  $QAVREN_EDGE_CHAT_MODEL_DIR/model.onnx" | sha256sum -c -
          echo "$QAVREN_EDGE_CHAT_MODEL_SHA256  $QAVREN_EDGE_CHAT_MODEL_DIR/model.onnx.data" | sha256sum -c -
          echo "$QAVREN_EDGE_CHAT_TOKENIZER_SHA256  $QAVREN_EDGE_CHAT_MODEL_DIR/tokenizer.json" | sha256sum -c -

      # QAVREN_EDGE_CHAT_MODEL_DIR is what SkipUnless reads, at RUNTIME. It is set for the whole
      # job, so the tier-3 facts run; on every other lane it is unset and they skip after the class
      # constructor, which is why the model is loaded inside the test body (spec 16.3).
      - name: Tier-3 chat tests
        run: dotnet run --project chat/tests/Qavren.Edge.Chat.Tests/Qavren.Edge.Chat.Tests.csproj -c Release -f net10.0 -p:TargetFrameworks=net10.0

      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: chat-model-tests
          path: artifacts/chat-nightly/**
```

`ci-gate`'s `needs` is **unchanged** — `chat-model-tests` must never block a PR.

- [ ] **Step 4: Five rules in `assert-workflows.py`**

Append a `# --- Sub-project 4 ---` block:

```python
# --- Sub-project 4 ---
# Every test project under chat/tests/ must appear as an explicit ci.yml step, so a new test
# project cannot silently never run. Same rule as embeddings/tests/, same reason.
sp4_tests = sorted((root / "chat" / "tests").glob("*/*.csproj"))
if not sp4_tests:
    problems.append("no test projects found under chat/tests/")
for proj in sp4_tests:
    relp = proj.relative_to(root).as_posix()
    if relp not in ci_text:
        problems.append("ci.yml has no explicit step for " + relp)

# The GenAI managed-asset assertion. Written against the .Managed package on purpose: the native
# Microsoft.ML.OnnxRuntimeGenAI package has no compile assets for any TFM, so an assertion against
# that id would pass vacuously forever.
for token in ("Microsoft.ML.OnnxRuntimeGenAI.Managed/0.15.2", "lib/net9.0-android31.0",
              "lib/net9.0-ios15.4", "lib/net9.0-maccatalyst14.0"):
    if token not in ci_text:
        problems.append("ci.yml GenAI asset assertion is incomplete (" + token + ")")

# The ORT floor guard. GenAI declares >= 1.28.0; the repo pins 1.30.0. Both halves must be asserted
# or somebody "fixes" the skew by downgrading ORT.
for token in ("1.28.0", "the repo pin is 1.30.0"):
    if token not in ci_text:
        problems.append("ci.yml ORT floor guard is incomplete (" + token + ")")

# The tier-3 chat lane (spec 16.3): it must exist, be guarded to schedule/workflow_dispatch, stay
# OUT of ci-gate's needs, and key its cache on the model's content hash.
if "chat-model-tests" not in ci["jobs"]:
    problems.append("ci.yml missing job chat-model-tests (spec 16.3 tier 3)")
else:
    cmt = ci["jobs"]["chat-model-tests"]
    if "schedule" not in str(cmt.get("if", "")):
        problems.append("chat-model-tests is not guarded to schedule/workflow_dispatch; it would run on PRs")
    if "chat-model-tests" in ci["jobs"]["ci-gate"]["needs"]:
        problems.append("chat-model-tests must NOT gate ci-gate: spec 16.3 says never on a PR")
    if "52640ca0d65e00d33dfb10b822c6a41e31bab1aaa6a49457b1dc5952c0dab0fb" not in ci_text:
        problems.append("chat-model-tests does not pin the model sha256; the cache key must be the content hash")

# Tier 0 (Task 1.5) is a THROWAWAY workflow in its own file, on workflow_dispatch only. It must
# never migrate into ci.yml: a six-job emulator/simulator matrix on every PR is not a gate anyone
# wants, and spec 16.0 scopes it to one run before wave 2.
if any(j.startswith("tier0-") for j in ci["jobs"]):
    problems.append("a tier0-* job has been added to ci.yml; tier 0 is workflow_dispatch only (spec 16.0)")
tier0 = w / "tier0-genai-smoke.yml"
if tier0.exists():
    t0 = yaml.safe_load(tier0.read_text(encoding="utf-8"))
    # `on:` parses as the boolean True in YAML 1.1, which is why this reads both keys.
    trig = t0.get("on", t0.get(True))
    if trig != "workflow_dispatch" and list(trig or []) != ["workflow_dispatch"]:
        problems.append("tier0-genai-smoke.yml is not workflow_dispatch-only")
```

The existing `trx2junit.py` and `java-junit` counts **stay at 4** — SP4 adds no device lane, and tier 0 publishes no JUnit.

- [ ] **Step 5: Verify**

`assert-workflows.py` does `import yaml` and `yaml.safe_load`s `ci.yml`, and the rules above index `ci["jobs"]["chat-model-tests"]` — so the interpreter that runs it needs **pyyaml**, and it must be the **same** interpreter. **Task 1.5 Step 7 already installed it into `C:\Python314\python.exe`**, which is where the first YAML parse in this plan happens; the install below is the same command and is idempotent, repeated here so this task stands alone. Never a bare `python3 -m pip install` — on this box that installs into the Store alias or fails outright, and then the real run dies on `ModuleNotFoundError: yaml` three lines later:

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
$py   = "C:\Python314\python.exe"
& $py -m pip install --quiet --disable-pip-version-check pyyaml
if ($LASTEXITCODE -ne 0) { throw "could not install pyyaml into $py" }
& $py -c "import yaml; print('pyyaml', yaml.__version__)"
& $py "$root\foundation\tools\ci-checks\assert-workflows.py" $root
if ($LASTEXITCODE -ne 0) { throw "assert-workflows.py failed" }
& $py -c "import yaml,sys; yaml.safe_load(open(sys.argv[1],encoding='utf-8')); print('tier0 workflow parses')" "$root\.github\workflows\tier0-genai-smoke.yml"
```

Then run the two new test-step commands by hand (they will fail until wave 3 writes the code — run them at wave 8's gate instead and record here that the **commands themselves** were transcribed from this plan's adjustment 12 and not invented). The workflow's own execution is CI-only.

Expected: the `OK:` line from `assert-workflows.py`, exit 0.

---

### Task 2.4: Wave 2 close — sequential re-verify and commit *(integrator)*

**Local-verifiable:** yes.

**Files:** none of its own.

**Approach.** Wave 2 is the one wave whose close deliberately does **not** build the solution: `ChatPresets.g.cs` is on disk and references types Task 3.1 has not written (adjustment 13). Anyone who "helpfully" adds a `dotnet build QavrenEdge.slnx` here will find a red tree and conclude the wave is broken. It is not; the first compile of that file is Task 3.3's.

- [ ] **Step 1: Clear the wave's isolated outputs**

```powershell
Remove-Item -Recurse -Force "D:\Local\Temp\qedge-sp4\w2" -ErrorAction SilentlyContinue
```

- [ ] **Step 2: Re-run every verify sequentially, in the real tree**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
$py   = "C:\Python314\python.exe"
dotnet run --project "$root\chat\tests\Qavren.Edge.Rag.Tests\Qavren.Edge.Rag.Tests.csproj" -c Release -p:TargetFrameworks=net10.0
if ($LASTEXITCODE -ne 0) { throw "FAILED: Rag.Tests" }
& $py -m pip install --quiet --disable-pip-version-check pyyaml   # same interpreter, or yaml is missing
if ($LASTEXITCODE -ne 0) { throw "could not install pyyaml into $py" }
& $py "$root\foundation\tools\ci-checks\assert-workflows.py" $root
if ($LASTEXITCODE -ne 0) { throw "FAILED: workflow contract" }
dotnet restore "$root\QavrenEdge.slnx"
if ($LASTEXITCODE -ne 0) { throw "FAILED: solution restore" }
Write-Host 'OK: wave 2 re-verified sequentially (NO solution build - see plan adjustment 13)'
```

Also re-run Task 2.2 Step 3's regeneration diff.

- [ ] **Step 3: Commit the wave**

```
feat(sp4): RAG sources and prompts, the preset catalogue, and the chat CI lanes

- Qavren.Edge.Rag: RagSource, IEdgeRetriever, RagOptions, RagPrompts, RagCitations
- fetch_chat_model_hashes.py + the committed ChatPresets.g.cs (two presets, measured)
- ci.yml: two test steps, the GenAI asset assert, the ORT floor guard, chat-model-tests
- assert-workflows.py: five sub-project 4 rules (incl. the tier-0 containment rule)
```

- [ ] **Step 4: Verify**

Step 2 printed its `OK` line and `git status --short` is clean. Wave 3 may start.

---

## WAVE 3 — `Qavren.Edge.Chat.Onnx` part 1 (no GenAI type anywhere) and `Qavren.Edge.Rag` part 2

### Task 3.1: `Qavren.Edge.Chat.Onnx` part 1 — ids, model shape, device profile, the memory budget, presets, options and the reducer

**Local-verifiable:** yes.

**Files:**
- Create: `chat\src\Qavren.Edge.Chat.Onnx\EdgeChatStartupOrder.cs` — `EdgeChatStartupOrder`, `EdgeChatEventIds`, `EdgeChatProperties`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\ChatModelShape.cs` + `ChatModelShapeJsonContext.cs`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\EdgeChatDeviceProfile.cs` — `EdgeTotalMemorySource`, `EdgeMemoryBudgetKind`, `EdgeChatDeviceProfile`, `IEdgeChatDeviceProfileProvider`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\Internal\PortableChatDeviceProfileProvider.cs` (the `#else` leg)
- Create: `chat\src\Qavren.Edge.Chat.Onnx\Platforms\Android\AndroidChatDeviceProfileProvider.cs`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\Platforms\iOS\AppleChatDeviceProfileProvider.cs`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\ChatMemoryBudget.cs` — `ChatMemoryBudgetOptions`, `ChatMemoryVerdict`, `ChatBudgetRequest`, `ChatMemoryDecision`, `ChatMemoryBudget`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\ChatPreset.cs` — `ChatPreset`, and `ChatPresets`'s hand-written `partial` half (`All`, `ById`)
- Create: `chat\src\Qavren.Edge.Chat.Onnx\EdgeChatOptions.cs` — `EdgeGuidancePolicy`, `EdgeGuidanceProbeResult`, `ChatThermalOptions`, `ChatHistoryOptions`, `ChatProvisioningOptions`, `EdgeChatOptions`, `ChatPromptFormatter`, `IChatPromptContext`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\ChatTurnStatus.cs` — `EdgeChatStopReason`, `ChatTurnStatus`, `EdgeChat`, `ChatClientStatistics`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\EdgeChatTokenBudgetReducer.cs`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\ChatExceptions.cs` — `EdgeChatException`
- Edit: `chat\tests\Qavren.Edge.Chat.Tests\Qavren.Edge.Chat.Tests.csproj` — one linked compile item, beside the `TinyChatModel.g.cs` one Task 1.1 already wrote:
  ```xml
  <!-- LINKED, never copied: fetch_chat_model_hashes.py owns chat/tests/fixtures/GenAiConfigFixtures.g.cs
       and ChatPresets.g.cs pins the same two digests (plan adjustment 24). -->
  <Compile Include="..\fixtures\GenAiConfigFixtures.g.cs" Link="Fixtures\GenAiConfigFixtures.g.cs" />
  ```
- Tests under `chat\tests\Qavren.Edge.Chat.Tests\`: `Fakes\` (`StubResourceMonitor`, `StubDeviceProfile`, `FixedTimeProvider`), `ChatMemoryBudgetTests.cs`, `DeviceFloorTests.cs`, `GenAiConfigFixtureTests.cs`, `ChatModelShapeTests.cs`, `PortableDeviceProfileTests.cs`, `PresetCatalogueTests.cs`, `HistoryReducerTests.cs`, `EdgeChatOptionsPrecedenceDataTests.cs`

**Approach — read this before writing.** **No type in this task may name a `Microsoft.ML.OnnxRuntimeGenAI` type.** That is §8.1's rule, and it is what makes every assertion below a tier-1 unit test with no natives at all. `ChatMemoryBudget` in particular is pure, static, no ORT, no GenAI, no I/O.

Types exactly as §6.1–§6.7 declare them, transcribed verbatim with their XML docs. `ChatModelShape` parsing is **source-generated** (`JsonSerializerContext`), never reflection-based, so the load path stays trim-safe; `model.eos_token_id` is normalised on the way in, because the field is an `int` in some exports and — as both shipped presets prove — an **array** in others.

**`ChatModelShapeJsonContext` parses `model.*` geometry and deliberately does NOT read `model.decoder.session_options` (adjustment 29).** That block carries `intra_op_num_threads`, which §14.3 publishes as the diagnostics key `chatIntraOpNumThreads` — and it is **Task 4.1's `Internal\ChatSessionOptionsReader.cs`** that reads it, through its own source-generated context. Do not add the field here and do not add it to `ChatModelShape`: every field on that record is cross-checked field by field against the provisioned config and a mismatch is `ChatModelShapeMismatch` (7008), so a thread count — a runtime hint a publisher may change between revisions without changing the model — would turn a harmless republish into a hard 7008, and every `ChatPreset` would have to declare a value it has no business declaring. The omission is a decision; this paragraph exists so the next reader does not "fix" it.

**Defaults that change behaviour.**

- `ChatMemoryBudgetOptions`: `ContextLadder = [4096, 3072, 2048, 1536, 1024]`; `MinContextTokens = 1024`; `WorkspaceBytes = 192L*1024*1024`; `ReserveBytes = 192L*1024*1024`; `SystemWideMemoryFraction = 0.60`; `PreferMeasuredPeak = true`; `RefuseWhenUnknown = **false**` (a reading nobody can make is never a refusal); `MinTotalMemoryBytes` = the three-band function — **5,368,709,120** above 1 GiB of weights, **3,650,722,201** above 200 MiB, **null** at or below 200 MiB. Every byte constant is written binary, and the third band is what keeps the ~500 KB tier-2 fixture off a floor that a default Android AVD (about 2 GiB of `TotalMem`) would fail.
- `ChatThermalOptions`: `ThrottleAt = Serious`; `AbortAt = Critical`; `ThrottleHeadroom = 1.0f`; `ThrottledTokensPerSecond = 8.0`; `SampleInterval = 1 s`; `RefuseNewTurnsInLowPowerMode = false`; `RefuseWhenThermalUnknown = **false**`.
- `ChatHistoryOptions`: `MaxTurns = 8`; `MaxHistoryTokens = 1024`; `PreserveSystemMessages = true`; `MinimumPreservedMessages = 2`; `PinnedMessageKeys` defaults to the single entry **`"qavren.edge.rag.context"`**.
- `EdgeChatOptions`: `ReservedPromptTokens = 64`; `RequireChatTemplate = true`; `Guidance = Disabled`; `EnableConversationCache = true`; `DropConversationCacheOnSleep = true`; `DropOnMemoryPressure = true`; `UnloadOnSleeping = **false**`; `MaxQueuedTurns = 4`; `TurnQueueTimeout = 30 s`; `LoadTimeout = 2 min`.
- `ChatPreset` (the type's own defaults, which the generated catalogue overrides — adjustment 15): `GenAiConfigFile = "genai_config.json"`, `StopSequences = []`, `DefaultMaxContextTokens = 4096`, `DefaultMaxOutputTokens = 512`, `DefaultTemperature = 0.7f`, `DefaultTopP = 0.9f`, `DefaultTopK = 50`.
- `ChatModelShape.KvCacheBytesPerElement = 2`, and that default is calibrated rather than guessed: for `Llama32_1BInstructInt4` it puts the arithmetic within 1.2% of a measured 1280 MiB peak, where 4 would put it 11% over. **It is the one field the load-time cross-check cannot validate**, because `genai_config.json` states layers, KV heads and head size and says nothing about the KV dtype.

**The three device-profile legs are all specified, and the `#else` leg is not a stub** — it is the leg every hosted CI runner and every Windows consumer executes. `TotalMemoryBytes` from `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` **or null when that is 0**; `TotalMemorySource = GcMemoryInfo` (or `Unknown` when null); `AvailableMemoryKind = SystemWide` — the honest classification, not a fallback, because SP2's desktop monitor fills `AvailableMemoryBytes` from the same advisory figure; `IsLowRamDevice = null` and **not `false`**, because "not a low-RAM device" and "nobody asked" are different claims; `Abi = null`; `RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier`.

**The trap in the reducer, restated because a reader of this task alone would get it wrong.** An **eviction group** is a non-system, non-pinned message together with every message after it up to but excluding the next `ChatRole.User` message. On an ordinary history that is exactly a user/assistant pair. On a history whose tail is user-then-user — which is what the RAG recipe produces — the pinned message is in no group at all and the real user message opens the newest group. The floor is system + pinned + the newest `MinimumPreservedMessages`, and the reducer **stops there even if the budget is still exceeded**; what happens next is the turn's job (7102), never evicting the grounding.

- [ ] **Step 1: Write the failing tests first**

- **`ChatMemoryBudgetTests`** — the single most important target. Table-driven over 4/6/8/12 GB devices × `PerProcess`/`SystemWide`/`Unknown` × both preset shapes × every ladder rung, asserting the exact verdict and the exact `ContextTokens`, and that `SystemWideMemoryFraction` actually bites on Android. Plus the arithmetic anchors, as literals measured in this plan:
  - `Llama32_1BInstructInt4.Shape.KvCacheBytesPerToken == 32_768`;
  - `Qwen3_600MInt4.Shape.KvCacheBytesPerToken == 114_688`;
  - the Llama ladder walked once and asserted against **1,761,107,258 / 1,727,552,826 / 1,693,998,394 / 1,677,221,178 / 1,660,443,962** bytes, so the ~6% of leverage is a test rather than a claim;
  - the Qwen ladder asserted against **1,356,075,101 → 1,003,753,565**, so the 26% is too;
  - **the negative that matters**: `PreferMeasuredPeak` does **not** bind for either shipped preset at 4096 (Llama's 1,342,177,280 + 192 MiB = 1,543,503,872 < 1,761,107,258; Qwen has no measurement), with `UsedMeasuredPeak` false — so a future edit that makes it bind is visible;
  - `MeasuredPeakBytes` **does** bind, with `UsedMeasuredPeak` true, for a synthetic shape whose measured peak exceeds its arithmetic (the Gemma-shaped case the machinery exists for);
  - `Unknown` availability → `SkippedUnknown` and **not** a refusal, and `RefuseWhenUnknown = true` inverts exactly that one case.
- **`DeviceFloorTests`** — its own table, because one constant is compared against three different platform totals: a nominal-6 GB Android device reporting 5.6 GiB of `TotalMem` and a nominal-6 GB iPhone reporting 6.0 GiB both **pass** for the large preset; a nominal-4 GB Android device reporting 3.6 GiB **fails** it and **passes** the small one; a `GcMemoryInfo` total is **never** compared against the floor whatever its value; a preset under 200 MiB of weights has no floor at all; `IsLowRamDevice == true` refuses and `null` does not.
- **`GenAiConfigFixtureTests`** — the guard that makes every assertion below mean something (adjustment 24). Over `GenAiConfigFixtures.LlamaConfigJson` and `.QwenConfigJson`, both generated by `fetch_chat_model_hashes.py` in Task 2.2 and **never hand-typed**: `SHA256(Encoding.UTF8.GetBytes(body.ReplaceLineEndings("\n")))` equals `LlamaConfigSha256` / `QwenConfigSha256`, those two constants equal the digests `ChatPresets.g.cs` pins for the same two files (asserted as string equality against `ChatPresets.Llama32_1BInstructInt4.Manifest.Files` and Qwen's), and the normalised byte lengths equal 1,536 and 1,520. A body edited by hand — "tidied", re-indented, or updated from a newer revision without re-running the generator — fails here rather than silently moving what every shape assertion is asserting about.
- **`ChatModelShapeTests`** — parses **both real config shapes** (from `GenAiConfigFixtures`, linked into this project from `chat/tests/fixtures/`, ~1.5 KB each, reviewable in a diff and no binary) and asserts every field against the Environment ground truth table, including `SlidingWindow == null` and Qwen's `ContextLength == 40_960`. Then every malformed variant → 7007 **naming the offending field**: scalar vs array `eos_token_id` (both must parse), a missing `decoder.filename`, zero layers, zero head size, a non-numeric `context_length`. Then the cross-check: a deliberately wrong preset shape → 7008 naming the field, the declared value and the preset's.
- **`PortableDeviceProfileTests`** — the `net10.0` leg, asserted rather than assumed: `AvailableMemoryKind == SystemWide`, `IsLowRamDevice is null` (not `false`), `Abi is null`, `RuntimeIdentifier` non-empty, and `TotalMemoryBytes` is either a plausible figure or `null` — **never `0`**.
- **`PresetCatalogueTests`** — the generated catalogue's table, guarded: two presets, `ById` round-trips both and throws 7003 for an unknown id, `All.Count == 2`, both revisions are 40-hex and neither is `"main"`, every `Sha256` is 64-hex, `GraphFile` names the decoder `.onnx` for both (**not** `genai_config.json`), `WeightsBytes` equals the sum of the `Graph` and `GraphExternalData` sizes and is **less** than `Manifest.TotalSizeBytes`, and — adjustment 4 — `min(DefaultMaxContextTokens, Shape.ContextLength)` is 4096 for **both**, which is the clamp that keeps Qwen's declared 40960 from ever reaching a generator.
- **`HistoryReducerTests`** — system preserved, pinned preserved, whole eviction groups evicted oldest-first, the floor respected, `MessagesDropped` counted; a user-then-user tail reduces **without stranding an assistant message**; a budget too small to hold the pinned block leaves it in place (the turn's 7102 is Task 5.1's). Counts with an injected `Func<string,int>`, so no tokenizer is needed.
- **`EdgeChatOptionsPrecedenceDataTests`** — the data half of §6.6's precedence that needs no client: the effective stop-sequence set is the ordinal-distinct union of preset + options, sorted **longest-first**, and setting `EdgeChatOptions.StopSequences` does **not** delete the preset's. The per-call `ChatOptions` layer is Task 5.1's.

- [ ] **Step 2: Implement**

Transcribe §6.1–§6.7 verbatim, write the three profile legs behind `#if ANDROID` / `#if IOS || MACCATALYST` / `#else` exactly as SP2 splits `IEdgeResourceMonitor`, and add `ChatPresets`'s hand-written `partial` half so the generated file from Task 2.2 compiles for the first time.

- [ ] **Step 3: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp4\chat\tests\Qavren.Edge.Chat.Tests\Qavren.Edge.Chat.Tests.csproj" `
  -c Release -f net10.0 -p:TargetFrameworks=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp4\w3\t31
```

Expected: all tests pass, exit 0. This is the first build of `ChatPresets.g.cs` in any tree.

---

### Task 3.2: `Qavren.Edge.Rag` part 2 — the MEVD retriever, `RagChatClient`, `UseRag()`, the extractive floor and registration

**Local-verifiable:** yes.

**Files:**
- Create: `chat\src\Qavren.Edge.Rag\VectorStoreRetriever.cs` — `VectorStoreRetrieverOptions<TRecord>`, `VectorStoreRetriever<TKey,TRecord>`
- Create: `chat\src\Qavren.Edge.Rag\RagChatClient.cs`
- Create: `chat\src\Qavren.Edge.Rag\RagChatClientBuilderExtensions.cs` — `UseRag`
- Create: `chat\src\Qavren.Edge.Rag\ExtractiveChatClient.cs`
- Create: `chat\src\Qavren.Edge.Rag\EdgeRagBuilderExtensions.cs` — `AddEdgeRag`, `AddRetriever` ×2, `AddVectorStoreRetriever`, `AddExtractiveChat`
- Create: `chat\src\Qavren.Edge.Rag\Internal\RagDiagnosticsContributor.cs`
- Tests: `Fakes\FakeChatClient.cs`, `Fakes\FakeRetriever.cs`, `Fakes\FakeVectorCollection.cs`, `Fakes\RecordingLogger.cs`, `RagChatClientStreamingTests.cs`, `RagChatClientNonStreamingTests.cs`, `SourceChannelTests.cs`, `ExtractiveChatClientTests.cs`, `VectorStoreRetrieverTests.cs`, `ProjectorShapeTests.cs`, `RagRegistrationTests.cs`, `LoggingPrivacyTests.cs`, `RagErrorCodeCoverageTests.cs`

**Approach — read this before writing.** `RagChatClient` is **`DelegatingChatClient` middleware, not a façade**. It references no ONNX package, no `Qavren.Edge.Chat.Onnx`, no `Qavren.Edge.VectorData`, and the test project's own reference set is the proof (adjustment 19). `FakeVectorCollection` is written against `Microsoft.Extensions.VectorData.Abstractions` alone and implements `IKeywordHybridSearchable<TRecord>` for the hybrid-lane half.

Five bodies with traps in them, each restated because a reader of this task alone would get it wrong:

1. **The retriever, not the projector, supplies the scoring fields.** After calling `project` the retriever re-stamps `source with { Score = result.Score ?? 0d, ScoreKind = kind, Ordinal = 0 }`, where `kind` is `Relevance` on the hybrid lane and `Distance` on the vector lane. Anything the projector set on those three `init` properties is **overwritten**, deliberately: the polarity is a property of the lane the retriever chose at runtime, and a projector that guesses is the exact bug `RetrievalScoreKind` exists to prevent.
2. **`Top + Skip > MaxCandidates` (4096, vec0's `SQLITE_VEC_VEC0_K_MAX`) is refused here**, naming the limit, rather than surfacing SP2's `KnnLimitExceeded` three frames down.

   **And the lane choice gets a second, opt-in arm — `RequireHybridSearch` — because 7203 otherwise has no raise site at all (adjustment 22).** The order is exactly: if `RequireHybridSearch` is true **and** the collection is not `IKeywordHybridSearchable<TRecord>`, throw `RagCollectionNotSearchable` (7203) naming the collection's CLR type and `PreferHybridSearch`; otherwise if `PreferHybridSearch` is true **and** the collection is `IKeywordHybridSearchable<TRecord>` **and** keywords are present, take the hybrid lane; otherwise take the vector lane. Note what does **not** throw: `RequireHybridSearch` with a hybrid-capable collection and an empty keyword list still takes the **vector** lane without error — the option is about the collection's capability, which is static, not about whether this particular question produced keywords, which is not. That distinction is a named test, because the other reading turns a stop-word-only question into a hard failure.
3. **`RagChatClient` clones the caller's `ChatOptions` before injecting.** `options?.Clone() ?? new ChatOptions()`, then set `RagCitations.SourcesPropertyKey` on the **clone**. MEAI's contract permits an implementation to mutate what it is handed, which is exactly why this one does not; `Clone()` shallow-copies `AdditionalProperties` into a fresh dictionary, so the isolation is free.
4. **The sources go inward twice, in two shapes for two readers.** The rendered block as a single `ChatMessage(ContextRole, block)` injected immediately before the newest user message with `AdditionalProperties[RagCitations.ContextMessagePropertyKey] = true` — the **pin** — and the structured `IReadOnlyList<RagSource>` on the cloned options. `RagChatClient` does **not** type-test its inner client and has **no** special case for `ExtractiveChatClient`; the channel is a documented public key.
5. **The citations arrive once, on the final update, carried by one appended `TextContent(string.Empty)`**, and their offsets index the **concatenation of every text delta already yielded** — not any single update. `GetResponseAsync` is `GetStreamingResponseAsync(...).ToChatResponseAsync(ct)` plus **exactly one** fix-up: move the annotation list onto the aggregated assistant message's **first `TextContent`**, whose `Text` is that same concatenation, and drop the emptied carrier. The offsets are **not recomputed**, because the string does not change; the fix-up exists only because MEAI's aggregation may coalesce adjacent `TextContent`s.

`RagChatClient.GetService` returns `this`, `IEdgeRetriever` and `RagOptions`, then **delegates inward** — so `GetService<ChatClientMetadata>()` still reaches the leaf through the stack, which is what keeps Semantic Kernel and Agent Framework working over a RAG pipeline.

`UseRag(retriever: null)` resolves `IEdgeRetriever` as **required** from DI, matching `UseDistributedCache`'s convention, and a missing one is `RagRetrieverMissing` (7201) naming `AddVectorStoreRetriever`/`AddRetriever`.

**Defaults that change behaviour.** Task 2.1 declared `RagOptions`'s and `ExtractiveChatOptions`'s; these are this task's own, and every one of them decides something a caller would otherwise have to read the source to learn:

- `VectorStoreRetrieverOptions<TRecord>.PreferHybridSearch = **true**` — so a collection that implements `IKeywordHybridSearchable<TRecord>` silently gets a *different* scoring polarity (`Relevance`, descending) from one that does not (`Distance`, ascending). It is the reason `RetrievalScoreKind` exists, and the reason the retriever re-stamps it after projection.
- `VectorStoreRetrieverOptions<TRecord>.RequireHybridSearch = **false**` — **new in this plan, adjustment 22.** False is §7's declared behaviour exactly: `PreferHybridSearch` stays advisory and a keyword-less collection falls back to the vector lane. True converts that fallback into `RagCollectionNotSearchable` (7203), and it is the only raise site for that code anywhere in the sub-project.
- `VectorStoreRetrieverOptions<TRecord>.MaxCandidates = **4096**` — vec0's `SQLITE_VEC_VEC0_K_MAX`. `Top + Skip` above it is refused **here**, by this package, naming the limit, rather than surfacing SP2's `KnnLimitExceeded` from three frames down. A consumer pointing this retriever at Qdrant, which has no such limit, is the one case where the default is arbitrary rather than physical — so it is settable, and the refusal names it.
- `VectorStoreRetrieverOptions<TRecord>.ScoreThreshold = **null**` — no threshold. When set, it is applied **with the polarity of the lane actually taken**: on the vector lane a result survives when `Score <= threshold` (a distance), on the hybrid lane when `Score >= threshold` (a relevance). One number, two comparisons, and the test that asserts it says "inversion" in its name.
- `VectorStoreRetrieverOptions<TRecord>.Filter = **null**` — no server-side filter; the whole collection is a candidate.
- `RetrievalRequest.Top = **5**` and `RetrievalRequest.Skip = **0**` (§7, restated here because §7 declares them on a type Task 2.1 writes and this task is where they are *consumed*): `RagChatClient` fills `Top` from `RagOptions.Top`, which also defaults to 5, so the two defaults agree — and a caller who changes one and not the other still gets the caller's value, because `RagOptions.Top` wins on the path that builds the request.

**§14.4's Trace-only rule is this package's obligation as much as the chat package's (adjustment 27).** `RagChatClient` logs the assembled context block, the question and the answer; `RagPrompts` logs what it clamped; `RagCitations` logs a dropped marker with its surrounding text. **Every one of those `LoggerMessage.Define` declarations is `LogLevel.Trace`**, and nothing above `Trace` may carry a prompt, a retrieved chunk, a question or a completion — counts, ordinals, source ids, byte lengths and error codes are what the `Debug` and `Information` sites get. The reason is in §14.4 in one sentence: a RAG prompt contains the user's private corpus. `LoggingPrivacyTests` is the assertion, not the convention.

- [ ] **Step 1: Write the failing tests first**

- `RagChatClientStreamingTests`: the full §11 pipeline over a fake retriever and a fake `IChatClient` — updates forwarded verbatim; the annotations on **exactly one** update, the final one, carried by an empty `TextContent` and never on a token update; spans sliced out of the accumulated answer equal the marker text; a retrieval throw is contained, logged, and the turn continues with `grounded = false`; `ContinueOnRetrievalFailure = false` rethrows as 7202; `ShortCircuitOnNoContext` spends **zero** inner-client calls; `ShouldRetrieve` returning false is a straight passthrough.
- `RagChatClientNonStreamingTests`: the aggregated response carries the annotations on the **first `TextContent`** with the **same** offsets, asserted by slicing `ChatResponse.Text`; the empty carrier is gone; `ChatResponse.Text` is byte-identical to the streamed concatenation.
- `SourceChannelTests`: the key is set on the options passed **inward** and **not** on the instance the caller supplied — asserted by handing it a frozen options object and checking it afterwards; a fake inner client reads the list back with `Ordinal` stamped 1..n.
- `ExtractiveChatClientTests`: answers from the top `MaxSources` with synthesised `[n]` markers, `FinishReason.Stop` and the grounded flag set; invoked **bare** — no `UseRag()`, no property — returns `NoResultsAnswer` with the flag clear and does **not** throw; and **`UseRag()` over `ExtractiveChatClient` produces a cited answer with no model**, which is the no-LLM floor proven rather than asserted.
- `VectorStoreRetrieverTests`: the hybrid lane is taken when the collection implements `IKeywordHybridSearchable<TRecord>` **and** keywords are present, the vector lane otherwise; `ScoreKind` is stamped to match; `ScoreThreshold` is applied with the correct polarity **per lane**, in a test whose name states the inversion; `Top + Skip > MaxCandidates` → 7202 naming the limit; a projector that sets `Score`, `ScoreKind` or `Ordinal` has all three overwritten; the **same** projector yields `Distance` on one lane and `Relevance` on the other over the same fake collection. **Plus 7203's three arms (adjustment 22), which are this code's only raise site:** `RequireHybridSearch = true` over a fake collection that does **not** implement `IKeywordHybridSearchable<TRecord>` → `RagCollectionNotSearchable` (7203) whose message names the collection's CLR type and `PreferHybridSearch`; `RequireHybridSearch = true` over one that **does**, with keywords, takes the hybrid lane and does not throw; and `RequireHybridSearch = true` over one that **does**, with an **empty** keyword list, takes the **vector** lane and does **not** throw — the arm that keeps a stop-word-only question from becoming a hard failure. The default (`false`) over the non-hybrid collection falls back silently, as §11 says.
- **`ProjectorShapeTests` — §16.1's "Projector shape (§7)" bullet, whose first clause nothing else owns.** §16.1 reads: "one `Func<TRecord, RagSource>` signature reaches **both** `VectorStoreRetriever`'s constructor **and** `AddVectorStoreRetriever`". `VectorStoreRetrieverTests` owns the overwrite clause and the two-lane polarity clause; the **single-signature** clause is the half that would catch a two-argument projector overload — `Func<TRecord, int, RagSource>`, or a `Func<TRecord, RetrievalRequest, RagSource>` "for convenience" — creeping into `AddVectorStoreRetriever` and quietly making the package have two projector shapes. It is a reflection assertion, because that is the only thing a compile-time test cannot express:
  - over `typeof(VectorStoreRetriever<,>)`'s public constructors, **exactly one** parameter is a `Func<,>` and its closed form is `Func<TRecord, RagSource>`;
  - over `typeof(EdgeRagBuilderExtensions).GetMethods()` named `AddVectorStoreRetriever`, there is **exactly one** method, and its `project` parameter's generic type definition is `typeof(Func<,>)` with `[TRecord, RagSource]` as its arguments;
  - **no public member anywhere in `Qavren.Edge.Rag`** declares a parameter of type `Func<TRecord, …, RagSource>` with an arity other than two — enumerated over the assembly's exported types, so a new overload on a *different* extension class is caught too.

  The message on failure names the offending member, because "there is exactly one projector shape in this package" (§7's own words) is a claim a reader has no other way to check.
- **`AddVectorStoreRetriever`'s `storeName` arm, which §7 declares and no task in the draft tested.** SP2 registers **both** an unkeyed `VectorStore` (`AddVectorStore()` → `AddSingleton<VectorStore>`) and a keyed one (`AddVectorStore(name, …)` → `AddKeyedSingleton<VectorStore>`), and §7's `storeName` parameter exists precisely so an app with a named store has a first-class path here instead of dropping to `AddRetriever`. Three arms, over a `ServiceCollection` holding a fake MEVD `VectorStore` registered each way (`Qavren.Edge.VectorData` is **not** referenced — adjustment 19 — so the fake store is written against `Microsoft.Extensions.VectorData.Abstractions` like every other fake in this project): `storeName: null` resolves the **unkeyed** `VectorStore` and retrieves; `storeName: "notes-store"` resolves the **keyed** one and retrieves from *that* collection, asserted by giving the two stores different records so a silent fall-through to the unkeyed one is visible rather than green; and `storeName: "missing"` with no such keyed registration fails at **resolve** time with a message naming the key, not at `AddVectorStoreRetriever` time — because the registration is lazy and pretending otherwise would mis-describe when the app breaks.
- `LoggingPrivacyTests` (§14.4, adjustment 27): drive one full `RagChatClient` turn — a distinctive question, a distinctive retrieved chunk, a distinctive answer — through a `RecordingLogger`. At `LogLevel.Trace` each of the three strings appears **only** in records whose level is `Trace`. At `LogLevel.Debug` **none** of the three appears in any record, while the turn still logs its counts and its event ids. Then the same over `ExtractiveChatClient`, whose answer is assembled from the corpus itself and is therefore the easiest place to leak it.
- `RagErrorCodeCoverageTests` (adjustment 23): an `IReadOnlyDictionary<EdgeErrorCode, Func<Task>>` with **four** entries — 7201 `RagRetrieverMissing`, 7202 `RagRetrievalFailed`, 7203 `RagCollectionNotSearchable`, 7204 `RagContextBudgetTooSmall` — each awaited, each asserted by catching `EdgeException` and comparing `.ErrorCode`; plus the guard that the key set equals `Enum.GetValues<EdgeErrorCode>().Where(c => (int)c is >= 7200 and <= 7299)`. 7204's entry reuses Task 2.1's too-small-budget case, so the two tests cannot disagree about what raises it.
- `RagRegistrationTests`: `AddEdgeRag` on a bare `ServiceCollection` with no MAUI; `UseRag()` with no retriever → 7201; `AddExtractiveChat(pipeline: c => c.UseRag())` resolves an `IChatClient` that answers with citations; `GetService<ChatClientMetadata>()` through the `RagChatClient` stack still reaches the leaf.

- [ ] **Step 2: Implement**

Types exactly as §7 declares them, bodies per §11.

- [ ] **Step 3: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp4\chat\tests\Qavren.Edge.Rag.Tests\Qavren.Edge.Rag.Tests.csproj" `
  -c Release -p:TargetFrameworks=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp4\w3\t32
```

Expected: all tests pass, exit 0.

- [ ] **Step 4: Assert the layering, mechanically**

```powershell
$csproj = Get-Content "C:\Users\steve\projects\qavren-edge-sp4\chat\src\Qavren.Edge.Rag\Qavren.Edge.Rag.csproj" -Raw
foreach ($bad in 'Qavren.Edge.Chat.Onnx','Qavren.Edge.VectorData','Qavren.Edge.Onnx','OnnxRuntime') {
  if ($csproj -match [regex]::Escape($bad)) { throw "Qavren.Edge.Rag references $bad; spec 2 decision 2 forbids it" }
}
Write-Host 'OK: Qavren.Edge.Rag references no ONNX package and no Qavren vector store'
```

---

### Task 3.3: Wave 3 close — sequential re-verify and commit *(integrator)*

**Local-verifiable:** yes.

- [ ] **Step 1:** `Remove-Item -Recurse -Force "D:\Local\Temp\qedge-sp4\w3" -ErrorAction SilentlyContinue`
- [ ] **Step 2:** Re-run both suites sequentially without `ArtifactsPath`, plus Task 3.2 Step 4's layering check:

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
dotnet run --project "$root\chat\tests\Qavren.Edge.Chat.Tests\Qavren.Edge.Chat.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0
if ($LASTEXITCODE -ne 0) { throw "FAILED: Chat.Tests" }
dotnet run --project "$root\chat\tests\Qavren.Edge.Rag.Tests\Qavren.Edge.Rag.Tests.csproj" -c Release -p:TargetFrameworks=net10.0
if ($LASTEXITCODE -ne 0) { throw "FAILED: Rag.Tests" }
dotnet build "$root\chat\src\Qavren.Edge.Chat.Onnx\Qavren.Edge.Chat.Onnx.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "FAILED: Chat.Onnx all four TFMs" }
Write-Host 'OK: wave 3 re-verified sequentially, all four Chat.Onnx TFMs compiled'
```

The unqualified `dotnet build` of `Qavren.Edge.Chat.Onnx` is deliberate and is new at this wave: it compiles the **android, ios and maccatalyst** legs, including `Platforms\**`, which the `net10.0` test run never touches. Environment ground truth proves those legs compile on this Windows host; this is where SP4's own platform code proves it.

- [ ] **Step 3: Commit**

```
feat(sp4): the chat memory budget, model shape, device profile and the RAG middleware

- Qavren.Edge.Chat.Onnx: ids, ChatModelShape, EdgeChatDeviceProfile, ChatMemoryBudget,
  ChatPreset(s), the option classes, ChatTurnStatus and the token-budget reducer
- Qavren.Edge.Rag: VectorStoreRetriever, RagChatClient, UseRag, ExtractiveChatClient, registration
```

- [ ] **Step 4: Verify** — Step 2's `OK` line, clean `git status --short`.

---

## WAVE 4 — `Qavren.Edge.Chat.Onnx` part 2 (GenAI hosting) and the trim smoke

### Task 4.1: `Qavren.Edge.Chat.Onnx` part 2 — the GenAI runtime task, the RID/ABI gate, the model host, provisioning, the two probes, lifecycle and diagnostics

**Local-verifiable:** yes.

**Files:**
- Create: `chat\src\Qavren.Edge.Chat.Onnx\EdgeGenAiOptions.cs`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\Internal\ChatEnvironmentStartupTask.cs` (order 400)
- Create: `chat\src\Qavren.Edge.Chat.Onnx\Internal\GenAiRuntimeSupport.cs` (the RID/ABI allowlist)
- Create: `chat\src\Qavren.Edge.Chat.Onnx\IChatModelHost.cs` — `ChatBackendReport`, `ChatModelInfo`, `ChatModelLease`, `IChatModelHost`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\Internal\ChatModelHost.cs`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\Internal\GenAiConfigOverlay.cs`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\Internal\ChatSessionOptionsReader.cs` + `ChatSessionOptionsJsonContext` — **the owner of `chatIntraOpNumThreads` (adjustment 29)**
- Create: `chat\src\Qavren.Edge.Chat.Onnx\IChatModelProvisioner.cs` — `ChatModelPlan`, `IChatModelProvisioner`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\Internal\ChatModelProvisioner.cs`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\Internal\ChatTemplateProbe.cs`, `Internal\GuidanceProbe.cs`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\Internal\ChatProvisioningStartupTask.cs` (410), `Internal\ChatWarmUpStartupTask.cs` (420)
- Create: `chat\src\Qavren.Edge.Chat.Onnx\Internal\ChatLifecycleObserver.cs`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\Internal\ChatDiagnosticsContributor.cs`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\ChatOnnxEdgeBuilderExtensions.cs` — `AddOnnxChat` ×2, `RequireChatModelAtStartup`, `WarmUpChatAtStartup`
- Tests: `Fakes\FakeChatModelHost.cs`, `Fakes\RecordingLogger.cs`, `ChatEnvironmentOrderingTests.cs`, `RuntimeSupportTests.cs`, `LoadPathTests.cs`, `ChatTemplateProbeTests.cs`, `ProvisioningPlanTests.cs`, `ConfigOverlayTests.cs`, `ChatSessionOptionsReaderTests.cs`, `LifecycleTests.cs`, `DiagnosticsTests.cs`, `RegistrationTests.cs`

**Approach — read this before writing.** This is the first task in the sub-project permitted to name a `Microsoft.ML.OnnxRuntimeGenAI` type, and the rule about **where** is absolute:

> **No type in `Qavren.Edge.Chat.Onnx` touches any `Microsoft.ML.OnnxRuntimeGenAI` type during composition or before startup order 400.**

Concretely: no GenAI type appears in a constructor parameter, a field initialiser, or a static constructor of anything DI builds eagerly; `EdgeChatClient` (Task 5.1) resolves its model lazily on the first call; `ChatEnvironmentStartupTask` is the first code in the assembly to name `OgaHandle`. The reason is measured, not theoretical: creating an `OgaHandle` on this box left `OrtEnv.IsCreated` **false**, because GenAI creates ORT's process-wide environment from **native** code and SP2's managed guard cannot see it — so GenAI going first silently costs SP2 its log id, its severity and its `DOrtLoggingFunction` bridge, with no error anywhere.

**The order-400 task, in order. Steps 1–3 touch no GenAI type; step 4 is the first line that does.**

1. Read `IEdgeChatDeviceProfileProvider.Read()` **once** and cache it for the process.
2. Sample `OrtEnv.IsCreated` into `ortEnvCreatedBeforeGenAi`. It is a `public static bool` on `Microsoft.ML.OnnxRuntime`, already on the reference graph through `Qavren.Edge.Onnx`, and it is sampled **here** — after order 200, before step 4 — because that is the last moment the answer is still meaningful. `false` → log **903** at `Warning`, naming the two likely causes: a GenAI type touched during composition, or `AddOnnx()` not registered.
3. Check the RID and Android ABI against GenAI's actual native payload. Unsupported → `ChatUnsupportedRuntime` (7004) **at startup**, naming the RID or ABI and listing the ones that work, rather than a `DllNotFoundException` on the user's first message.
4. Create the single process-wide `OgaHandle` when `OwnRuntimeHandle`.
5. `Utils.DisableTelemetryEvents()` when `DisableTelemetry` (the default), logging 902.

**The RID/ABI allowlist is measured, not guessed** (Environment ground truth): `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-arm64`, plus Android `arm64-v8a` and `x86_64` and the iOS/Catalyst xcframework. **No `osx-x64`, no `win-x86`, no `armeabi-v7a`.** SP1's SQLite native ships `android-arm`, so an armeabi-v7a device gets the database and embeddings and **has no chat at all** — 7004 says so by name.

**The load path** is §9.1 step for step. Two bodies inside it carry traps:

- **Exactly one `Overlay` call, with the document composed in managed code.** `Overlay`'s native merge semantics are undocumented and SP4 will not bet the memory cap on them. `GenAiConfigOverlay` parses the provisioned config's own `search` object, parses `ConfigOverlayJson` when set (after refusing any non-CPU provider on a mobile TFM with 7009), deep-merges the first under the second, sets `search.max_length` to the budget's `resolvedContext` on the result — SP4's value wins, which is the point — and hands `Config.Overlay` **one** document. Because the model's own search block is re-emitted, its shipped `do_sample` / `temperature` / `top_k` / `top_p` survive whichever way the native call behaves. Both shipped presets set `past_present_share_buffer: true` (measured), which selects the **static** KV path where the cache is allocated once to `max_length` — so writing the budget's answer into the config genuinely bounds the allocation rather than bounding a ceiling.
- **There is no load-cancellation flag.** ORT's `SessionOptions.SetLoadCancellationFlag` has no GenAI equivalent, so a multi-second model load cannot be aborted once started. SP4 cancels *before* the load and refuses to start one while `LastPressure == Critical`. That is the whole mitigation, and it is documented rather than papered over.

**`ChatSessionOptionsReader` owns `chatIntraOpNumThreads`, and it is the only other reader of that config text (adjustment 29).** §14.3 publishes the key and the draft plan gave it no owner, which is how a diagnostics key becomes a stub — or, worse, how a second **reflection-based** JSON parse gets bolted onto a load path whose entire point is that it is trim-safe. The shape is literal:

```csharp
namespace Qavren.Edge.Chat.Internal;

/// <summary>
/// Reads <c>model.decoder.session_options.intra_op_num_threads</c> out of a genai_config.json body.
/// <para>
/// <b>The block is NOT at the top level.</b> Section 14.3 says "the resolved genai_config.json's
/// <c>session_options</c>", which is shorthand: an ORT GenAI config's top level is <c>model</c> and
/// <c>search</c>, and this block sits under <c>model.decoder</c> beside <c>filename</c>,
/// <c>head_size</c> and <c>num_hidden_layers</c>. The path is hard-coded here and nowhere else.
/// </para>
/// <para>
/// Source-generated, exactly as <see cref="ChatModelShape"/>'s parsing is, so the load path takes
/// no reflection. It is handed the config text the loader ALREADY read - it never opens the file a
/// second time - and the answer is cached on <c>ChatModelInfo</c> beside the shape, so the
/// diagnostics contributor re-parses nothing.
/// </para>
/// <para>
/// This is deliberately not a field on <see cref="ChatModelShape"/>: every field there is
/// cross-checked against the provisioned config and a mismatch is 7008, and a thread count is a
/// runtime hint a publisher may change between revisions without changing the model.
/// </para>
/// </summary>
internal static class ChatSessionOptionsReader
{
    /// <returns>
    /// The declared value, or <see langword="null"/> when <c>session_options</c> is absent, when
    /// <c>intra_op_num_threads</c> is absent, or when it is not a number. <b>Never 0</b> is
    /// substituted for absence: 0 is ORT's "pick for me" sentinel and a real, different answer.
    /// </returns>
    public static int? ReadIntraOpNumThreads(string genAiConfigJson);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ChatSessionOptionsDocument))]
internal sealed partial class ChatSessionOptionsJsonContext : JsonSerializerContext;
```

The document types mirror only the three nodes on the path — `{ model: { decoder: { session_options: { intra_op_num_threads } } } }` — every level nullable, so a config missing any of them deserialises to `null` rather than throwing. `ChatDiagnosticsContributor` publishes the key as that integer, or the literal string `"(not declared)"` when it is null.

**The guidance probe asserts output shape, never the absence of an exception.** At v0.15.2 `CreateGuidanceLogitsProcessor` on a `USE_GUIDANCE=OFF` build returns `nullptr` after an optional log line and **does not throw**, so an exception-based probe reports success on a build that enforces nothing. The probe generates at most eight tokens under `SetGuidance("json_schema", "{\"type\":\"string\",\"const\":\"qedge\"}")` — **two arguments; `enableFFTokens` is left at its default `false`** (adjustment 3), because a control that changes what is emitted is not a control — and compares the decoded output. Threw → `ProbeFailed`; ran and matched → `Enforced`; ran and did not match → `NotEnforced`, which means *not proven enforced* and **not** *proven absent*.

**The chat-template probe** exists because `TokenizerImpl::LoadChatTemplate` returns OK with only a warning when minja cannot parse a template, so the failure otherwise surfaces mid-sentence on a user's first message. It calls `ApplyChatTemplate` once with a two-message probe and records `chatTemplateSupported`.

**Drop discipline is SP2's `OnnxSessionHost`, line for line.** `UnloadAsync` sets `DropRequested` and disposes only at zero leases; the release path disposes when the last lease returns. Disposing a `Model` under a running `Generator` is a native access violation, and every GenAI wrapper type is `IDisposable` **with a finalizer**, so dropped-but-undisposed pins the whole native graph until GC — jetsam bait.

**`IChatModelHost.PreloadAsync` is public on purpose.** SP2 documents the "an `IEdgeStartupTask` cannot drive a load without deadlocking on its own completion" hole as unclosable from L1 because its equivalent is `internal` to another assembly. SP4's host and its warm-up task are in the **same** assembly, so SP4 does not inherit the hole — and does not fix SP2's.

**Registration goes through MEAI's own `services.AddChatClient`** (namespace `Microsoft.Extensions.DependencyInjection` — adjustment 10), not a hand-rolled `IChatClient` descriptor, because that is the descriptor Agent Framework and Semantic Kernel resolve and because its factory is `ChatClientBuilder.Build`, which is what makes the order of builder calls irrelevant. `AddOnnxChat` calls `AddOnnx()` for you and is idempotent. `WarmUpChatAtStartup` and `RequireChatModelAtStartup` are idempotent per registration **by scanning the `IServiceCollection` for a marker record** — *not* `TryAddEnumerable`, which on an enumerable service like `IEdgeStartupTask` would suppress every **other** package's task rather than a duplicate of this one. SP2's `WarmUpSessionAtStartup` registers with a bare `AddSingleton` and gets duplicate order-220 tasks when called twice; SP4 does not repeat that.

**Defaults that change behaviour.** `EdgeGenAiOptions.DisableTelemetry = **true**` (GenAI 0.15.0 made 1DS telemetry opt-OUT and its AAR merges `INTERNET`, `ACCESS_NETWORK_STATE` and a telemetry content provider into every consuming APK — measured); `OwnRuntimeHandle = true`; `ShutdownOnStopping = true` (safe since 0.15.0's transparent re-initialisation, **proved on this box**). `ChatProvisioningOptions.FreeDiskMarginBytes = 512L*1024*1024`, checked against the **bundle total** before the first byte — SP2's HTTP source checks per file against a 32 MiB margin, which is the right question for a 23 MiB embedding graph and the wrong one for a 1.241 GB folder; `IsTransferPermitted = null` permits.

- [ ] **Step 1: Write the failing tests first**

- `ChatEnvironmentOrderingTests` — **the composition half of §8.1's rule**: `AddOnnxChat` on a bare `ServiceCollection`, build the provider, resolve `IChatClient`, and assert `OrtEnv.IsCreated` is **still false**. Plus: orders 400/410/420 sort after SP2's 200; `AddOnnx` is registered exactly once when both SP2 and SP4 call it; `WarmUpChatAtStartup` twice registers **one** task and does not suppress SP2's order-220 task. **Plus 7001's raise site, which nothing else in the plan owned:** `IChatModelHost.AcquireAsync` (and `EdgeChatClient`'s first call) against a host whose order-400 `ChatEnvironmentStartupTask` has **not** run → `ChatEnvironmentNotStarted` (7001), with the message naming `AddOnnxChat()` and "resolve `IChatClient` from the container" exactly as §15.3's row does. Constructed directly rather than through `EnsureStartedAsync`, because that is precisely the mistake the code exists to catch.
- `RuntimeSupportTests` — the allowlist as a table over the measured RID/ABI set, including `armeabi-v7a` → 7004 naming the ABI and listing `arm64-v8a` and `x86_64`, and `osx-x64` → 7004 naming the RID.
- `LoadPathTests` (fakes, no natives) — absent model → 7051 carrying `Manifest.TotalSizeBytes` and naming `Plan()`/`ProvisionAsync`, and **never a download**; `RefusedDeviceTooSmall` → 7006; `RefusedInsufficientMemory` → 7005 carrying required, available, total, budget kind and the largest context that would have fit; `AllowedReduced` logs 921 with the rung; `SkippedUnknown` logs 923 at `Warning` and proceeds; `MaxContextTokens` set explicitly turns the budget from a cap into a **named refusal**; the requested context for `Qwen3_600MInt4` is clamped to 4096 and never 40960. **Plus 7002 and 7007, both raised here and nowhere else on the fake path:** a provisioned directory whose `genai_config.json` parses but whose model construction throws (the seam is an injected `Func<string, IDisposable>` model factory on the host, which the fake makes throw) → `ChatModelLoadFailed` (7002) carrying the directory and **preserving the inner message**, asserted by finding the inner exception's text in `.Message` or `.InnerException`; and the same again with `LoadTimeout` set to 1 ms against a factory that blocks → 7002 naming the timeout, which is §15.3's second arm for that code and is otherwise untested. A missing or unparseable `genai_config.json` → 7007 naming the file and the field.
- `ChatTemplateProbeTests` (fakes) — **7101's raise site (§9.5, §15.3), which no other task owned.** Against a fake tokenizer whose `ApplyChatTemplate` fails the probe: with `EdgeChatOptions.RequireChatTemplate = true` (the default) the warm-up throws `ChatTemplateUnsupported` (7101) naming the model and `PromptFormatter` as the remediation; with it `false`, the probe logs **951**, records `chatTemplateSupported = false` in diagnostics, names the fallback formatter, and start-up succeeds. Both arms, because the default is the throwing one and the option exists precisely for the app that would rather ship.
- `ConfigOverlayTests` — the composed document is **one** JSON object; the model's shipped `do_sample`/`temperature`/`top_k`/`top_p` survive; `search.max_length` equals the budget's `resolvedContext` and **beats** a consumer overlay that set it; a consumer overlay naming a non-CPU provider on a mobile TFM → 7009 naming the provider; `max_length` in `EdgeChatOptions.SearchOptions` → 7108 whatever its type.
- `ProvisioningPlanTests` — `Plan()` returns bytes total / already present / to transfer / free disk / `FitsOnDisk` / the SPDX string / the licence URI, with **no network I/O beyond a stat**; short disk → 7052 **before the first byte**; `IsTransferPermitted` returning false → 7053 **before a connection is opened**.
- `LifecycleTests` — each of the six events against `FakeChatModelHost`, including the **two-second drain bound** on `MemoryPressure(Critical)`, `DropOnMemoryPressure = false` still terminating, `Moderate` dropping the conversation `Generator` but not the model, `Sleeping` not unloading by default, `Resumed` pre-warming **nothing**, and exactly **one** `OgaShutdown` behind an `Interlocked` guard on `Stopping`.
- `DiagnosticsTests` — every key §14.3 lists is present **by name, asserted against a literal list of the key strings rather than against whatever the contributor happened to write** (a "present" check that reads the contributor's own dictionary keys proves nothing); `Safe(...)` turns a throwing read into `"(unavailable: <TypeName>)"` and a report **never** throws; the component name contains no `"Native"`, so SP1's `EdgeDiagnosticsReport.Native` still picks the SQLite block; **`genAiVersion` reads `0.15.2`** (adjustment 1) and **`genAiManagedAsset` reads the `net8.0` MVID `324d5b97-7f06-44d8-b891-c7c016bf320a`** on the host leg (adjustment 2).
- **`ChatSessionOptionsReaderTests` — `chatIntraOpNumThreads`'s four arms (adjustment 29), which nothing in the draft owned.** Three over literal config bodies written in the test file: a `model.decoder.session_options` declaring `"intra_op_num_threads": 4` → the reader returns `4` and the report's `chatIntraOpNumThreads` is `4`; a `model.decoder` with **no** `session_options` → `null` and `"(not declared)"`; a `session_options` present with **no** `intra_op_num_threads` → `null` and `"(not declared)"`. Plus a fourth arm that is the one that would catch the *path* being wrong rather than the parser: run the reader over **both real bodies** from `GenAiConfigFixtures` (Task 2.2's generated file, already linked into this project by Task 3.1) and assert it does not throw and returns either `null` or a **positive** integer — whichever the two publishers actually shipped. A fifth, negative arm asserts the reader **never returns `0`** for an absent key, because `0` is ORT's "pick for me" sentinel and would read as a declared answer. And a grep-style guard in the same file: `ChatModelShape` exposes no thread-count member, so nobody has quietly added the field to the geometry record the cross-check would then reject on (adjustment 29).
- `RegistrationTests` — keyed and unkeyed coexisting, one contributor each told apart by a `serviceKey` detail rather than a decorated component name; `AddOnnxChat(preset, pipeline: c => c.UseRag())` builds; the §4.3 snippet resolves from a real container **in both builder-call orders**.

- [ ] **Step 2: Implement**

Types exactly as §6.8 and §6.9 declare them; bodies per §8.1, §9.1, §9.2, §9.5, §9.6, §14.2, §14.3 **and §14.4**.

**§14.4 is a body reference here, not just an event-id range (adjustment 27).** Every log site in this task goes through `LoggerMessage.Define` — CA1848 is an error repo-wide — and the two sites that touch text the user owns are held to the Trace-only rule: the chat-template probe logs its **rendered probe output** and the guidance probe logs its **decoded eight tokens**, both at `LogLevel.Trace` and never above. Everything else this task logs is a count, a path, a byte figure, an error code or a verdict, which is exactly why 910/911/920/921/922/923 can sit at `Information` and `Warning`.

- [ ] **Step 3: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp4\chat\tests\Qavren.Edge.Chat.Tests\Qavren.Edge.Chat.Tests.csproj" `
  -c Release -f net10.0 -p:TargetFrameworks=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp4\w4\t41
```

Expected: all tests pass, exit 0. The suite loads the real GenAI natives on this box (`OgaHandle` constructs on win-x64, proved in Environment ground truth) but loads **no model** — that is Task 6.1's.

---

### Task 4.2: The `trim-smoke` console gains `Qavren.Edge.Rag`

**Local-verifiable:** yes.

**Files:**
- Edit: `embeddings\tools\Qavren.Edge.TrimSmoke\Qavren.Edge.TrimSmoke.csproj` (one `ProjectReference`)
- Edit: `embeddings\tools\Qavren.Edge.TrimSmoke\Program.cs` (one segment)

**Approach.** §17 says `Qavren.Edge.Rag` joins the existing console, and §5's file table forgot to list it (adjustment 7). The console is published rather than run as a test step, which is why it lives under `tools/` and why `assert-workflows.py`'s "every test project has an explicit `ci.yml` step" rule does not see it.

The added segment must use **`DelegateRetriever` over an in-memory list and a fake `IChatClient`**, never `VectorStoreRetriever` — that type carries `[RequiresDynamicCode]` and `[RequiresUnreferencedCode]` because MEVD reflects over `TRecord`, and putting it in a trimmed publish would prove only that the analyzer works. What the segment proves is the part that matters: `RagChatClient`, `RagPrompts.Format`, `RagCitations.Build` and `ExtractiveChatClient` are pure managed code and survive trimming, which makes the whole RAG recipe trim-safe for free.

- [ ] **Step 1: The reference**

```xml
    <ProjectReference Include="..\..\..\chat\src\Qavren.Edge.Rag\Qavren.Edge.Rag.csproj" />
```

- [ ] **Step 2: The segment**

Appended to `Program.cs` after the existing vec0 round-trip: build an `ExtractiveChatClient` behind `UseRag()` with a `DelegateRetriever` returning three hard-coded `RagSource`s, ask one question, assert the answer is non-empty and carries **at least one** `CitationAnnotation` whose `TextSpanAnnotatedRegion` slices back to a `[n]` marker, and print `OK: rag`. A failure is a non-zero exit, exactly as the existing segments do.

- [ ] **Step 3: Verify — publish trimmed and run**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
dotnet publish "$root\embeddings\tools\Qavren.Edge.TrimSmoke\Qavren.Edge.TrimSmoke.csproj" `
  -c Release -r win-x64 --self-contained true -p:PublishTrimmed=true `
  -p:TargetFrameworks=net10.0 -p:TargetFramework=net10.0 `
  -p:ArtifactsPath=D:\Local\Temp\qedge-sp4\w4\t42 `
  -o "D:\Local\Temp\qedge-sp4\w4\t42\trim-smoke"
if ($LASTEXITCODE -ne 0) { throw "trimmed publish failed" }
& "D:\Local\Temp\qedge-sp4\w4\t42\trim-smoke\Qavren.Edge.TrimSmoke.exe"
if ($LASTEXITCODE -ne 0) { throw "trimmed run failed" }
```

**`-o` redirects the publish output and nothing else; `-p:ArtifactsPath` is what redirects `obj/` and `bin/`, and this task needs it.** Without it this publish writes intermediate output for `Qavren.Edge.Core`, `Qavren.Edge.Onnx`, `Qavren.Edge.Embeddings.Onnx`, `Qavren.Edge.VectorData`, `Qavren.Edge.Sqlite.Native` and `Qavren.Edge.Rag` **in the real tree**, while Task 4.1 is building `Qavren.Edge.Chat.Onnx → Qavren.Edge.Onnx → Qavren.Edge.Core` in the same parallel phase — the two write sets overlap on **`Qavren.Edge.Onnx` as well as `Qavren.Edge.Core`**, because `Qavren.Edge.Embeddings.Onnx` references `Qavren.Edge.Onnx`. Worse than the race: the global `-p:TargetFrameworks=net10.0 -p:TargetFramework=net10.0` would leave a **single-TFM `project.assets.json` and `nuget.g.props`** behind for every multi-targeted project in that closure, which Task 4.3's close then builds over and Task 5.1 inherits. The scratch root contains both. Task 4.3 deliberately drops the switch, as every other close does — that is the point of a close.

Expected: the console's `OK:` lines including `OK: rag`, exit 0. The CI leg is `linux-x64`; this one is `win-x64` because that is the host. A trim **warning** that becomes an error here is a real finding: record it and, if it is in `Qavren.Edge.Rag`, fix it rather than suppress it — the package is pure managed code and has no excuse.

---

### Task 4.3: Wave 4 close — sequential re-verify and commit *(integrator)*

**Local-verifiable:** yes.

- [ ] **Step 1:** `Remove-Item -Recurse -Force "D:\Local\Temp\qedge-sp4\w4" -ErrorAction SilentlyContinue`
- [ ] **Step 2:** Re-run sequentially, in the real tree:

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
dotnet run --project "$root\chat\tests\Qavren.Edge.Chat.Tests\Qavren.Edge.Chat.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0
if ($LASTEXITCODE -ne 0) { throw "FAILED: Chat.Tests" }
dotnet publish "$root\embeddings\tools\Qavren.Edge.TrimSmoke\Qavren.Edge.TrimSmoke.csproj" -c Release -r win-x64 --self-contained true -p:PublishTrimmed=true -p:TargetFrameworks=net10.0 -p:TargetFramework=net10.0 -o "$root\artifacts\trim-smoke"
if ($LASTEXITCODE -ne 0) { throw "FAILED: trim-smoke publish" }
& "$root\artifacts\trim-smoke\Qavren.Edge.TrimSmoke.exe"
if ($LASTEXITCODE -ne 0) { throw "FAILED: trim-smoke run" }
dotnet build "$root\chat\src\Qavren.Edge.Chat.Onnx\Qavren.Edge.Chat.Onnx.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "FAILED: Chat.Onnx all four TFMs" }
Write-Host 'OK: wave 4 re-verified sequentially'
```

This is where the new `TrimSmoke → Qavren.Edge.Rag` edge is proved in the tree CI will use — the reason the wave map calls that edge out.

- [ ] **Step 3: Commit**

```
feat(sp4): ORT GenAI hosting, model provisioning, the two probes and the trim smoke

- the order-400 environment task, the RID/ABI gate and the OrtEnv ordering record
- ChatModelHost + ChatModelLease, the single composed Config.Overlay, IChatModelProvisioner
- the chat-template and positive-control guidance probes
- lifecycle observer, diagnostics contributor, AddOnnxChat
- trim-smoke now drives UseRag() over ExtractiveChatClient
```

- [ ] **Step 4: Verify** — Step 2's `OK` line, clean `git status --short`.

---
## WAVE 5 — `Qavren.Edge.Chat.Onnx` part 3: the client and the decode loop *(strictly serial — one implementer)*

### Task 5.1: `EdgeChatClient`, the decode loop, the conversation cache, statistics and `GetService`

**Local-verifiable:** yes.

**Files:**
- Create: `chat\src\Qavren.Edge.Chat.Onnx\EdgeChatClient.cs`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\Internal\ChatTurnPipeline.cs` (refuse → gate → reduce → format → params)
- Create: `chat\src\Qavren.Edge.Chat.Onnx\Internal\ChatDecodeLoop.cs` (the producer body)
- Create: `chat\src\Qavren.Edge.Chat.Onnx\Internal\StopSequenceMatcher.cs`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\Internal\ConversationCache.cs`
- Create: `chat\src\Qavren.Edge.Chat.Onnx\Internal\ChatPromptContext.cs` (`IChatPromptContext`'s implementation)
- Create: `chat\src\Qavren.Edge.Chat.Onnx\Internal\ChatStatistics.cs` (the bounded 64-sample ring)
- Tests: `Fakes\FakeGenerator.cs`, `OptionRefusalTests.cs`, `PrecedenceTests.cs`, `StopSequenceTests.cs`, `ThermalTests.cs`, `QueueingTests.cs`, `StreamingContractTests.cs`, `ConversationCacheUnitTests.cs`, `GetServiceTests.cs`, `PromptBudgetTests.cs`, `GenerationFailureTests.cs`, `LoggingPrivacyTests.cs`, `ChatErrorCodeCoverageTests.cs`

**Approach — read this before writing.** `GetResponseAsync` is **literally** `GetStreamingResponseAsync(messages, options, ct).ToChatResponseAsync(ct)`. Two independent implementations is how streaming and non-streaming drift, so there is one.

`GetStreamingResponseAsync` is an `async IAsyncEnumerable` with `[EnumeratorCancellation]`, and §10's eight steps are its structure. Six of them carry traps:

**(a) Refuse early, by name.** `Tools is { Count: > 0 }` → 7107; `ToolMode` is `RequireAny` or `RequireSpecific` → 7107 as well, because a required tool call that can never be emitted is a hard failure, not a footnote. `ResponseFormat is ChatResponseFormatJson` under policy `Disabled` → 7103 — **and only that case**. `ChatResponseFormat.Text` is a non-null value that asks for plain text, needs no constrained decoding, and is already what this client produces, so it is honoured by being ignored; a null `ResponseFormat` is likewise untouched. **Refusing a caller for requesting the default behaviour would be a bug**, and the negative test below exists because that over-fire is the easy mistake. Any non-`TextContent` in a message → 7108 naming the content type. `FrequencyPenalty`, `AllowMultipleToolCalls`, `ContinuationToken` and `AllowBackgroundResponses` are listed in `AdditionalProperties[EdgeChatProperties.UnhonouredOptions]` on the response — never silently dropped.

**(e) Params, and the `max_length` split.** A search option **cannot be changed after the generator is constructed** — `SetSearchOption` lives on `GeneratorParams`, which `Generator(Model, GeneratorParams)` consumes at construction, and the shipped `Generator` surface has no setter (measured). So:

- **Fresh generator** — caching off, a cache miss, or the first turn: `max_length = min(promptTokens + maxOutput, resolvedContext)`.
- **Cached generator** — a hit: the value is whatever it was built with, and it was built with `max_length = resolvedContext`. Nothing re-sets it, because nothing can.

In both cases the **memory** cap is `≤ resolvedContext`, which is the budget's answer and the only thing jetsam cares about; in both cases the **output** cap is a managed generated-token counter in the decode loop. Upstream's defect is the absence of the first, not staleness in the second.

`ChatOptions.AdditionalProperties` entries are forwarded as raw search options **only when the value is a `bool` or a `double`**; every other value is **skipped without error**. That is a contract, not an accident: it is what lets `Qavren.Edge.Rag` carry an `IReadOnlyList<RagSource>` on the same dictionary and have this leaf ignore it. `EdgeChatOptions.SearchOptions` is applied **last** and wins — except `max_length`, refused with 7108 — and its values are **converted or refused, never quietly dropped**: `bool` → the bool overload; `double` → the double overload; `int`/`long`/`short`/`byte`/`float`/`decimal` → the double overload **when the conversion round-trips exactly**, otherwise 7108 naming the key and the value; anything else → 7108 naming the key and the CLR type. That is deliberately the **opposite** rule to `ChatOptions.AdditionalProperties`, and the two are tested side by side so nobody "harmonises" them.

**(f) Decode — which generator, and the two checks.** With `EnableConversationCache` on and a **non-null** `options.ConversationId` ordinally equal to the cached one, the cached generator is a **candidate**. A **null id is never a hit** — not "matches the cached null", never a hit: two unrelated conversations that both left the field null would otherwise share one KV cache and therefore one history, which is a correctness bug wearing a performance optimisation's clothes. Null means "build fresh and mint an id" (a GUID string), stamped on **every** `ChatResponseUpdate.ConversationId` and therefore on the aggregated `ChatResponse.ConversationId`.

*Check one — does the cached prefix still hold?* Beside the cached generator SP4 keeps `cachedText`: the exact character sequence that generator has consumed and produced. If `fullText.StartsWith(cachedText, StringComparison.Ordinal)` the prefix holds and only `fullText[cachedText.Length..]` is encoded and appended. If not, it is a **miss**: dispose and rebuild. That one string test carries three invariants for free — an eviction that dropped a message the generator already consumed rebuilds automatically; an edited or reordered history rebuilds; and a template that is not append-only across turns rebuilds rather than silently duplicating the conversation.

*Check two — does the turn still fit?* On a hit, `generator.TokenCount() + maxOutput > resolvedContext` is a **miss, not a 7102**: dispose and rebuild from the reduced list, which step (c) has already trimmed. 7102 is for a prompt that does not fit a *fresh* generator. `TokenCount()` returns `ulong` (measured), so that comparison casts once, deliberately, at the boundary.

*What gets reported.* `ChatTurnStatus.PromptTokens` and `UsageDetails.InputTokenCount` are the **total** sequence length the model conditioned on — `generator.TokenCount()` after the append — and `PromptTokensAppended` is what was actually encoded this turn. Both are published, because "fewer prompt tokens on the second turn" is true of one and false of the other.

**The producer/consumer shape is the contract, not an illustration.** Production runs on a single `Task.Run` writing into a **bounded `Channel<ChatResponseUpdate>`** (capacity 64, `FullMode.Wait`); the iterator reads it. The decode loop is the **producer body** and never yields — a `yield return` there would put `GenerateNextToken()` back on the consumer's thread and undo the whole arrangement. The producer **always** calls `writer.TryComplete()` in a `finally`, on success, fault and cancel; the iterator's `finally` **awaits the producer task**, so a cancelled or faulted turn is never an unobserved exception.

Three details inside the loop are the whole reason SP4 wrote it rather than taking upstream's:

- **Stop sequences are matched over a rolling decoded-character buffer** sized to the longest configured stop string, so a multi-token stop (`<|eot_id|>` arriving as three fragments) actually fires. Upstream compares single decoded tokens by string equality, so a multi-token stop **never** fires. The effective set is the ordinal-distinct union of preset + `EdgeChatOptions` + `ChatOptions`, sorted longest-first so the matcher reports the longest match at a position.
- **Thermal is sampled mid-decode**, at the monitor's existing one-second cache boundary so it costs nothing beyond the cached read. A 400-token answer at 30 tok/s is thirteen seconds of sustained CPU, and a start-of-turn check would not notice the device crossing `Serious` until the *next* turn. **The comparison is written explicitly** as `thermal != EdgeThermalState.Unknown && thermal >= threshold`, because `Unknown = 0` sorts **below** `Nominal` and a bare `>=` would read "the platform reports nothing usable" as "better than fine" on every desktop, every hosted runner and every Android device below API 29. `ThermalHeadroom` gets the same rule for free: Android returns `NaN` when polled faster than ~1 Hz, and `NaN >= 1.0f` is `false`.
- **Throttling paces rather than stops.** A visibly slow answer beats a dead one.

**Cancellation is never converted.** `OperationCanceledException` propagates. The final update in a cancelled stream still carries `StopReason = Cancelled` and the real token counts before the exception surfaces. Cancellation is registered to call `generator.SetRuntimeOption("terminate_session", "1")`, so a cancel landing mid-`GenerateNextToken` aborts inside native code. Prefill (`Encode`, `AppendTokens`) is bracketed by explicit checks on both sides; **it is not itself interruptible**, which is stated rather than hidden.

**`GetService` is a contract, not an implementation detail.** It resolves, in order: `this` when `serviceKey is null && serviceType.IsInstanceOfType(this)`; `ChatClientMetadata` with `ProviderName` `"onnxruntime-genai"` and `DefaultModelId = ModelId`; `ChatModelInfo`; `ChatClientStatistics`; `IChatModelHost`; and, while a model is loaded, `Model`, `Tokenizer` and `Config`. Semantic Kernel's `GetModelId()` is literally `GetService<ChatClientMetadata>()?.DefaultModelId`, and Agent Framework's `ChatClientAgent` avoids double-wrapping by calling `GetService<FunctionInvokingChatClient>()`. Returning the raw `Model` is also what lets a consumer build `OnnxRuntimeGenAIChatClient`, `MultiModalProcessor` or LoRA `Adapters` themselves without SP4 wrapping any of it.

**Thread safety is honoured by SERIALISATION, not parallelism.** `ort_genai_c.h` says flatly "This API is not thread safe" while `IChatClient`'s contract requires every member to be safe for concurrent use. One async gate per model holds for a whole turn; a second concurrent caller **queues** rather than allocating a second `Generator` and therefore a second full KV cache.

- [ ] **Step 1: Write the failing tests first** (all tier 1, over `FakeGenerator` — no natives, no model)

- `OptionRefusalTests`: 7107 for `Tools` and for each `ToolMode` that requires a call; 7108 for non-`TextContent` and for `max_length` in `SearchOptions`; 7103 for a `ChatResponseFormatJson` under `Disabled`; `UnhonouredOptions` populated for `FrequencyPenalty`. **Plus the negative:** `ResponseFormat = ChatResponseFormat.Text` and `ResponseFormat = null` each complete a normal turn under **all three** guidance policies and never throw.
- `PrecedenceTests`: table-driven over the four overridable knobs — a `ChatOptions` value beats an `EdgeChatOptions` value beats a `ChatPreset` default; a `null` at a layer **falls through** rather than zeroing. Plus the `SearchOptions` value-typing table: a `bool` and a `double` applied, an `int` and a lossless `long` converted, a `long` past 2^53 and a `string` each → 7108 naming the key — asserted **alongside** the deliberate opposite, that the same `string` in `ChatOptions.AdditionalProperties` is ignored without error, which is what keeps a `RagSource` list inert on the model path.
- `StopSequenceTests`: **a multi-token stop sequence across a decode-chunk boundary fires** — the upstream bug this design exists to fix, asserted directly; the trimmed text excludes the stop string; the longest match at a position wins.
- `ThermalTests`: `StubResourceMonitor` + `FixedTimeProvider` over the fake generator — mid-decode sampling at the one-second boundary, pacing rather than stopping at `Serious`, `StopReason = Thermal` at `Critical`. **Plus the `Unknown` arm**, which the enum's numbering would otherwise decide by accident: `Unknown` neither throttles nor aborts, is published **verbatim** rather than normalised to `Nominal`, `RefuseWhenThermalUnknown = true` inverts it, and a `ThermalHeadroom` of `NaN` never throttles. **Plus 7106, the pre-turn refusal, which is a different code path from the mid-decode one and had no test:** §10(b) says thermal at or above `ThermalOptions.AbortAt` **before** a turn is `ChatThermalAbort` (7106) "before anything is allocated" — so the assertion is both halves, the throw **and** the negative: the fake generator records that `AppendTokens` was never called and the gate was never entered. The mid-decode arm stays what §15.3 says it is — the stream **completes** with `StopReason = Thermal`, never a throw — and the two are asserted side by side, because "abort" meaning two different things at two points in one turn is exactly the kind of thing a reader gets backwards.
- `PromptBudgetTests`: **7102's raise site (§15.3), which Task 3.1 explicitly deferred to here.** A prompt that is still over budget after `EdgeChatTokenBudgetReducer` has reduced to its floor — system + pinned + the newest `MinimumPreservedMessages` — throws `ChatPromptTooLong` (7102) carrying **both numbers** (the prompt's token count and the budget) and naming `MaxOutputTokens`, `ChatHistoryOptions` and `RagOptions.MaxContextTokens` as remediations. Two shapes, because they fail for different reasons: a single enormous user message that no reduction can help, and a pinned RAG context block bigger than the whole budget — the second is the case §16.1 calls out, where the reducer correctly **refuses to evict the grounding** and hands the turn a prompt it cannot fit. Plus the negative: a cached-generator turn whose `TokenCount() + maxOutput` exceeds `resolvedContext` is a **cache miss and a rebuild**, never a 7102.
- `GenerationFailureTests`: **7104's raise site.** `FakeGenerator` throws an `OnnxRuntimeGenAIException` from `GenerateNextToken()` after N tokens. §15.3's three obligations are three assertions: the partial text already streamed **stays streamed** (the consumer keeps those N updates), the final update says `StopReason = Error` and carries the real counts, and **then** `ChatGenerationFailed` (7104) surfaces from the iterator with the native message preserved. A 7104 that swallows the partial answer, or one that arrives before the final update, fails here.
- `QueueingTests`: a second concurrent turn **waits**; a fifth is refused with 7105 and logged 933; a gate timeout is 7105.
- `StreamingContractTests`: one `MessageId` across every update; exactly **one** final metadata-only update carrying `UsageContent` and `ChatTurnStatus`; `FinishReason` `Length` vs `Stop`; `GetResponseAsync` structurally equal to the aggregated stream; the bounded-channel producer completes its writer on success, fault **and** cancel, and the iterator observes the producer task (assert no unobserved-exception escape).
- `ConversationCacheUnitTests`: the four rules that need no natives — a null id mints one and stamps it everywhere; a different id rebuilds; a null id on turn two rebuilds rather than reusing turn one's cache; a mutated earlier message fails the `StartsWith` check and rebuilds.
- `GetServiceTests`: returns `this`; `ChatClientMetadata` with a **non-null** `DefaultModelId` (the assertion Semantic Kernel depends on); `ChatModelInfo`; `ChatClientStatistics`; **null for a non-null `serviceKey`**.
- `LoggingPrivacyTests` (§14.4, adjustment 27): the chat half of the privacy contract. One full turn through a `RecordingLogger` with a distinctive prompt and a distinctive scripted completion: at `LogLevel.Trace` both strings appear **only** in `Trace` records; at `LogLevel.Debug` neither appears anywhere, while 930/931/935/936 still log their counts and reasons. Also asserted: the **formatted prompt** the template produced, and the reduced message list, are `Trace`-only — those are the two places a RAG block would otherwise leak into a `Debug` log, and a `Debug`-level dump of "the prompt we sent" is the single most natural thing for a later contributor to add.
- `ChatErrorCodeCoverageTests` (adjustment 23 — the assertion split per assembly, because a test in this process cannot observe a throw in `Qavren.Edge.Rag.Tests`'s): an `IReadOnlyDictionary<EdgeErrorCode, Func<Task>>` with **twenty** entries, one per code in **7001–7108** — 7001, 7002, 7003, 7004, 7005, 7006, 7007, 7008, 7009, 7051, 7052, 7053, 7101, 7102, 7103, 7104, 7105, 7106, 7107, 7108 — each awaited, each asserted by catching `EdgeException` and comparing `.ErrorCode`. Every entry is the minimal reproduction already written by an earlier task in this project (Tasks 3.1, 4.1 and 5.1 between them own all of them; the entries **call those same helpers** rather than re-deriving a second way to raise each code). Plus the guard: the dictionary's key set equals `Enum.GetValues<EdgeErrorCode>().Where(c => (int)c is >= 7000 and <= 7199)` exactly — so a code added to the enum later turns this red instead of quietly passing. The `foundation/docs/errors.md` string test stays here and covers **all 24** codes including the 72xx range, asserting each has a `## <code>` heading with a non-empty remediation: it reads a file rather than observing a throw, so the per-assembly split does not apply to it.

- [ ] **Step 2: Implement**

`EdgeChatClient` exactly as §6.6 declares it; the pipeline per §10 (a)–(h) **and §14.4**.

**§14.4's Trace-only rule, stated where the code that breaks it lives (adjustment 27).** This task writes the only code in `Qavren.Edge.Chat.Onnx` that holds a whole prompt and a whole completion in a local variable, which makes it the only place the privacy contract can be broken: *prompt and completion text are logged at `LogLevel.Trace` only and never above, matching MEAI's own `UseLogging` contract, because a RAG prompt contains the user's private corpus*. Concretely — the formatted prompt, the reduced message list, `cachedText`, the rolling stop-sequence buffer's contents and every decoded token are `Trace`-declared `LoggerMessage.Define`s; 930 `TurnStarted`, 931 `TurnCompleted`, 932/933 queueing, 934 termination, 935 `HistoryReduced` and 936 `ThermalThrottled` carry **counts, reasons, ids and durations only**. `LoggingPrivacyTests` asserts it; this paragraph is why.

- [ ] **Step 3: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp4\chat\tests\Qavren.Edge.Chat.Tests\Qavren.Edge.Chat.Tests.csproj" `
  -c Release -f net10.0 -p:TargetFrameworks=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp4\w5\t51
```

Expected: all tests pass, exit 0.

---

### Task 5.2: Wave 5 close — sequential re-verify and commit *(integrator)*

**Local-verifiable:** yes.

- [ ] **Step 1:** `Remove-Item -Recurse -Force "D:\Local\Temp\qedge-sp4\w5" -ErrorAction SilentlyContinue`
- [ ] **Step 2:**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
dotnet run --project "$root\chat\tests\Qavren.Edge.Chat.Tests\Qavren.Edge.Chat.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0
if ($LASTEXITCODE -ne 0) { throw "FAILED: Chat.Tests" }
dotnet build "$root\chat\src\Qavren.Edge.Chat.Onnx\Qavren.Edge.Chat.Onnx.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "FAILED: Chat.Onnx all four TFMs" }
Write-Host 'OK: wave 5 re-verified sequentially'
```

- [ ] **Step 3: Commit**

```
feat(sp4): EdgeChatClient, the bounded-channel decode loop and the conversation cache

- rolling-buffer stop matching, mid-decode thermal pacing, cooperative termination
- the max_length memory cap vs the managed per-turn output counter
- GetService to contract, so Semantic Kernel and Agent Framework need no glue
```

- [ ] **Step 4: Verify** — Step 2's `OK` line, clean `git status --short`.

---

## WAVE 6 — Real natives, the nightly lane, and the device-only assertions *(strictly serial — one implementer)*

### Task 6.1: Tier 2 over the committed fixture, tier 3's test half, §16.4's device assertions, and the cross-package literal

**Local-verifiable:** yes — tier 2 runs here on real GenAI natives; tier 3's **skip path** runs here and its real-model path is CI's; the device assertions **compile** here and run on the four lanes.

**Files:**
- Edit: `chat\tests\Qavren.Edge.Chat.Tests\Qavren.Edge.Chat.Tests.csproj` (**adds the `Qavren.Edge.Rag` `ProjectReference`** — adjustment 14)
- Create: `chat\tests\Qavren.Edge.Chat.Tests\Tier2\TinyChatModelFixture.cs`, `Tier2\TinyChatModelFixtureTests.cs`
- Create: `chat\tests\Qavren.Edge.Chat.Tests\Tier2\GenAiRuntimeTests.cs`, `Tier2\LoadAndShapeTests.cs`, `Tier2\StreamingTurnTests.cs`, `Tier2\ConversationCacheTests.cs`, `Tier2\ConcurrencyAndUnloadTests.cs`, `Tier2\TemplateAndGuidanceTests.cs`, `Tier2\OrderingTests.cs`
- Create: `chat\tests\Qavren.Edge.Chat.Tests\Tier3\ChatModelAvailable.cs`, `Tier3\RealModelFacts.cs`, `Tier3\ConfigOverlaySemanticsFacts.cs`, `Tier3\corpus.json` (**the 20 literal chunks below — copy them, do not improvise**), `Tier3\CorpusInvariantTests.cs` (**a tier-1 test, no `SkipUnless`, no model — adjustment 26**)
- Create: `chat\tests\Qavren.Edge.Chat.Tests\Platforms\Android\AndroidChatDeviceTests.cs`
- Create: `chat\tests\Qavren.Edge.Chat.Tests\Platforms\iOS\AppleChatDeviceTests.cs`
- Create: `chat\tests\Qavren.Edge.Chat.Tests\CrossPackageLiteralTests.cs`

**Approach — read this before writing.** This task runs **alone** because all three bodies live in one test project: a split by sub-folder would have each build compiling the others' half-written files, and it additionally creates the `Chat.Tests → Qavren.Edge.Rag` edge whose restore rewrites that project's assets graph.

**Tier 2 asserts mechanics only, never text — the fixture's weights are random.** The fixture materialises `TinyChatModel.g.cs`'s base64 into a temp directory **once per collection**, because `Model(string)` needs a directory.

**The fixture preset takes the no-floor band by arithmetic, not by a special case.** `MinTotalMemoryBytes` returns null for any preset under 200 MiB of weights, and the fixture is about 500 KB. That is not a nicety: a default GitHub-hosted Android AVD reports roughly 2 GiB of `TotalMem`, and a blanket floor would turn the highest-value assertion in this sub-project — real GenAI natives loading on a real device — into a guaranteed 7006. The fixture preset also leaves `RefuseWhenUnknown` at its default `false`.

What tier 2 asserts, per §16.2:

- **`TinyChatModelFixtureTests` — the byte counts, which §16.2 asks to be "loud" and adjustment 25 makes a test.** For each of the four fixture files: the materialised file's on-disk length equals the `const int …Length` the generator emitted beside its base64; every base64 decodes without throwing; and the four lengths sum below the 1 MB source cap. CI never runs `make_tiny_chat_model.py`, so this is the **only** thing in the repo that notices an onnx, torch, transformers or builder upgrade moving the serialised bytes — Task 1.3's regeneration diff fires only on a box where somebody re-ran the generator by hand. It runs on every PR, every host and every device lane.
- `OgaHandle` constructs and disposes; `Utils.DisableTelemetryEvents()` does not throw; **a fresh load after `OgaShutdown` succeeds** (the 0.15.0 re-init claim — already proved for the handle in Environment ground truth, proved here for a model).
- Load → `ChatModelInfo` reports the geometry read from the fixture's `genai_config.json`; a deliberately-wrong preset shape → 7008.
- One streaming turn yields N text updates then **exactly one** final update carrying `UsageContent`, a `FinishReason` and a `ChatTurnStatus`; every update shares one `MessageId`; `ToChatResponseAsync` groups them into one message.
- `MaxOutputTokens` produces `FinishReason.Length` at exactly N generated tokens, with the conversation cache **off** (where `max_length` and the counter agree) **and on** (where only the counter can). Separately, and this is the upstream bug asserted from the outside: with the cache **on**, `ChatModelInfo` reports the cached generator's `max_length` equal to the budget's `resolvedContextTokens` and **not** the model's declared `context_length`.
- Cancellation mid-stream throws within one token; cancellation during prefill is bracketed.
- `TerminateActiveGeneration()` from another thread ends the turn within a bounded time — and, per §19 item 7, **under a stress loop**, because `ort_genai_c.h` says the API is not thread safe and this is the one GenAI call SP4 makes outside the turn gate. If the stress loop is unstable, the documented fallback is the volatile flag alone, the decode loop still aborts between tokens, a long prefill runs to completion, and that limit is stated in the README rather than hidden.
- The conversation cache, **all five rules of §10(f)** against real natives: a first turn with a null `ConversationId` **mints** one and stamps it on every update and on the aggregated response; echoing it back **hits**, with `PromptTokensAppended` on turn two strictly **less** than turn one's while `PromptTokens` is strictly **greater**; a different id rebuilds; a **null** id on turn two rebuilds rather than reusing turn one's cache; a mutated history fails the `StartsWith` check and rebuilds.
- **§19 item 11's seam check**, run here rather than deferred: for one conversation, compare the token sequence produced by the append path against `tokenizer.Encode(fullText)` and assert they are identical. If they differ on the fixture, record it and re-run per preset on the nightly; the one-line fallback is `EnableConversationCache = false` in the affected preset's own definition.
- A second concurrent turn **queues**; a fifth gets 7105.
- Unload with a live lease does not dispose until the lease returns, and **the process does not crash** — the access-violation guard.
- `ApplyChatTemplate` round-trips; a `PromptFormatter` override replaces it.
- **The guidance positive-control probe runs and its result is printed** whichever way it goes. This is the single test that answers whether the host's natives enforce constraints. It asserts **nothing** about the answer — that would make the suite red on a platform whose build settings SP4 does not control — and the value lands in the job summary.
- **The ordering assertion, phrased entirely in public signals**: after `EnsureStartedAsync` on a container built by `AddOnnxChat`, the `Qavren.Edge.Chat.Onnx` diagnostics block reports `ortEnvCreatedBeforeGenAi = true`, no event 903 was logged, and the `Qavren.Edge.Onnx` block **in the same report** reports `ortEnvironmentPreexisting = false` — §8.1's first table row, asserted from outside both packages, with no `internal` reached for.

**Tier 3** is gated by `QAVREN_EDGE_CHAT_MODEL_DIR` through a `SkipUnless` on its own `ChatModelAvailable` static class with `SkipType` set, evaluated at **runtime**, so a skipped test loads nothing — SP2's `ModelAvailable` pattern, copied, and the model is constructed **inside the test body** rather than in a constructor for exactly that reason. It asserts and records:

- the model loads inside a time budget and a real prompt produces non-empty, coherently-shaped output;
- **the chat template renders for a real model's Jinja** — the single biggest per-preset unknown, since minja parsing any specific template is unverified;
- the resolved `max_length` matches the budget's decision, and the decision's `Explanation` is printed;
- **peak RSS at the resolved context is recorded to the job summary and compared to the arithmetic**, printed rather than asserted tight. That is §19 item 4's measurement — whether `KvCacheBytesPerElement` is 2 or 4 — answered on the first nightly for the preset whose KV term dominates (112 KiB/token, 448 MiB at 4096);
- tokens/sec and TTFT recorded as **artifacts, not assertions**: there is no measured device baseline to compare a hosted-runner number against;
- **deterministic RAG quality with zero new package dependencies** — and "deterministic" needs four things §16.3 does not name, so the plan names them (adjustment 26). `Tier3/corpus.json` is a committed array of 20 objects `{ "id", "title", "text" }`, each 2–4 sentences of a fictional appliance warranty, indexed into an in-memory `DelegateRetriever` (no vector store, no embedding model — this measures the answer, not SP2):

  | | |
  |---|---|
  | question | `"How many years does the warranty cover the compressor?"` |
  | gold chunk | `id = "warranty-compressor"`, text containing `"the sealed compressor is covered for seven years from the date of purchase"` |
  | required token | `"seven"` (case-insensitive) **or** the digit `"7"` — a 0.6B model rendering the number as a digit is a correct answer, and no other chunk in the corpus contains either |
  | decoding | `do_sample = false`, `random_seed = 20260911`, `MaxOutputTokens = 96`, `RagOptions.Top = 5`, `MaxCharsPerSource = 1200` |

  **The twenty chunks are literal, and the exclusivity clause is a test (adjustment 26).** Describing nineteen of them — "delivery, registration, labour cover, exclusions, the returns window, and three deliberately near-miss neighbours" — is not a specification: two implementers writing to that description produce two different corpora, and either one may put the word "seven" or a bare `7` into a near-miss chunk. The moment that happens the third assertion passes on a word the model could have emitted without reading anything, and it passes **forever**, green, with nobody looking. So the corpus is committed here and the invariant is enforced by a test that runs on every PR.

  `chat/tests/Qavren.Edge.Chat.Tests/Tier3/corpus.json`, literal and complete — LF line endings, copied as-is:

  ```json
  [
    { "id": "warranty-compressor", "title": "Compressor cover",
      "text": "Under the Northwind Appliance Protection Plan the sealed compressor is covered for seven years from the date of purchase. Cover applies to the compressor unit and its sealed refrigerant circuit. Labour to fit a replacement compressor is billed separately under the labour terms." },
    { "id": "plan-overview", "title": "What the plan is",
      "text": "The Northwind Appliance Protection Plan is included with every refrigeration unit sold through an authorised dealer. It covers manufacturing defects in materials and workmanship. It is not an insurance policy and does not cover accidental damage." },
    { "id": "parts-general", "title": "General parts cover",
      "text": "Parts other than the sealed refrigeration circuit are covered for two years from the date of purchase. This includes shelving, door seals, the control board and the interior lighting assembly. Replacement parts carry the remainder of the original term." },
    { "id": "labour-term", "title": "Labour cover",
      "text": "Labour is covered for ninety days from the date of purchase. After that period an authorised engineer charges the standard call-out rate plus time on site. Labour cover cannot be purchased separately or extended." },
    { "id": "returns-window", "title": "Returns",
      "text": "An unopened unit may be returned within thirty days of delivery for a full refund. The original packaging and proof of purchase are required. A unit that has been installed and run is not returnable and falls under the protection plan instead." },
    { "id": "registration", "title": "Registering the unit",
      "text": "Registration is optional and does not change the cover period. Registering the serial number allows the service desk to look up the model and installation date without a receipt. Registration can be completed by the dealer at the point of sale." },
    { "id": "delivery", "title": "Delivery and installation",
      "text": "Standard delivery places the unit inside the ground-floor entrance. Installation, levelling and removal of the old appliance are a separate service. Damage noticed on delivery must be reported before the driver leaves." },
    { "id": "exclusions-misuse", "title": "Exclusions: misuse",
      "text": "The plan does not cover damage caused by misuse, unauthorised modification, or operation outside the stated ambient temperature range. It also excludes damage caused by power surges where no surge protection was fitted. Cosmetic marks arising from normal use are excluded." },
    { "id": "exclusions-commercial", "title": "Exclusions: commercial use",
      "text": "Units installed in a commercial kitchen, a rental property or any premises open to the public are outside the plan entirely. A commercial service contract is available separately from the dealer network. Domestic cover cannot be converted to commercial cover after purchase." },
    { "id": "transfer", "title": "Transferring cover",
      "text": "Remaining cover transfers once to a new owner at no charge. The transfer must be recorded with the service desk within sixty days of the sale. Cover cannot be transferred a second time." },
    { "id": "claim-process", "title": "Making a claim",
      "text": "Claims start with a call to the service desk, which triages the fault and books an engineer if needed. Proof of purchase or a registered serial number is required. The engineer decides on site whether a fault falls inside the plan." },
    { "id": "engineer-visit", "title": "The engineer visit",
      "text": "An authorised engineer attends within five working days in most postcodes. The engineer carries common parts and will order anything else. A visit that finds no fault is billed at the standard call-out rate." },
    { "id": "refrigerant", "title": "Refrigerant and the sealed system",
      "text": "The sealed system comprises the compressor, the condenser, the evaporator and the connecting pipework. Only an authorised engineer may open it. Refrigerant loss caused by a defect in the sealed system is a plan matter, not a chargeable service." },
    { "id": "noise", "title": "Normal operating noise",
      "text": "Clicking as the thermostat cycles, a low hum from the compressor and occasional gurgling from the refrigerant are normal. Noise on its own is not treated as a fault. An engineer will assess noise only alongside a temperature or performance complaint." },
    { "id": "temperature-fault", "title": "Temperature faults",
      "text": "A unit that will not hold its set temperature should be reported to the service desk. Check first that the door seals are clean and that the vents are not blocked by packed food. Persistent temperature loss usually points at the sealed system." },
    { "id": "ice-buildup", "title": "Ice build-up",
      "text": "Frost-free models should not accumulate ice on the rear wall. Repeated build-up suggests a defrost heater or drain fault, both of which are parts matters. Manual defrost models are expected to be defrosted by the owner." },
    { "id": "power-supply", "title": "Power supply",
      "text": "The unit must be connected to a dedicated earthed socket. Extension leads and multi-way adapters void cover for electrical faults. Voltage outside the stated range is an installation matter and not a defect." },
    { "id": "spares-availability", "title": "Spares availability",
      "text": "Functional spare parts are stocked for ten years after a model is discontinued. Cosmetic parts may be withdrawn earlier. Where a part is unavailable the service desk offers a comparable replacement unit." },
    { "id": "contact", "title": "Contacting the service desk",
      "text": "The service desk is open Monday to Saturday. Have the model number, the serial number and the purchase date to hand. The desk cannot advise on installation questions, which go to the dealer." },
    { "id": "law", "title": "Statutory rights",
      "text": "This plan is offered in addition to the buyer's statutory rights and does not limit them. Nothing in this document affects a claim against the seller for goods that are not as described. Local law prevails where it conflicts with these terms." }
  ]
  ```

  The nineteen non-gold chunks carry `two years`, `ninety days`, `thirty days`, `sixty days`, `five working days` and `ten years` — six durations, none of them seven, and **not one bare `7` or the word "seven" anywhere outside the gold chunk**. The near-misses are deliberate: a model that pattern-matches on "years" without reading has `parts-general` (two years) and `spares-availability` (ten years) to land on, and a wrong answer is therefore a *visible* wrong answer rather than a silence.

  Three assertions on the answer, all hard: every `[n]` in the answer is within the retrieved-source count and resolves to one of them; the gold chunk is among the five retrieved; the answer contains the required token. **If the third proves flaky across the first week of nightlies**, the documented response is to demote it to a metric printed in the job summary and say so in `chat/README.md` — never to delete it quietly. `Microsoft.Extensions.AI.Evaluation.NLP` is preview-only and is **not** taken; `.Quality` needs an LLM judge calibrated for frontier models; `.Safety` needs the Foundry service. None is usable here.

  **And a fourth assertion that is deliberately NOT a tier-3 fact: `Tier3/CorpusInvariantTests`.** It lives in the `Tier3` folder for locality but carries **no `SkipUnless`**, reads no environment variable and loads no model — it is a **tier-1 test that runs on every PR, every host lane and every device lane**, because the invariant it guards is what gives the three answer assertions their meaning. Over the committed `corpus.json`:

  - exactly **20** entries; every `id` non-empty, unique, and every `text` non-empty;
  - `warranty-compressor` is present and its `text` contains, verbatim, `"the sealed compressor is covered for seven years from the date of purchase"`;
  - **exactly one** entry matches `seven|\b7\b` (case-insensitive, `RegexOptions.IgnoreCase`) over `title + " " + text`, and it is `warranty-compressor`. The failure message lists **every** matching id, so an editor sees immediately which chunk they poisoned;
  - the question string the facts use and the `corpus.json` the facts load are the **same two constants** the tier-3 body reads — asserted by reference, not retyped — so the invariant cannot be checked against one corpus while the nightly answers over another.

  That last test is the whole fix: an edit that adds "seven" to `parts-general` turns a PR red in the wave that makes it, instead of silently disarming the nightly's third assertion for the life of the project.
- **`ConfigOverlaySemanticsFacts` — §19 item 12, a test rather than a maybe (adjustment 28).** Load the Qwen preset's real `Config`, call `Overlay("""{"search":{"max_length":2048}}""")` exactly once, build a `GeneratorParams` from that config, and read `do_sample`, `temperature`, `top_k`, `top_p` and `max_length` back through `GetSearchBool`/`GetSearchNumber`. Print `overlayMergeSemantics = deep-merge` (the shipped `0.6 / 20 / 0.95 / true` survived) or `= replace` (they did not) into the job summary and the lane's artifact. It asserts **nothing** about which answer comes back — both are legal, and §9.1 composes one document precisely so neither can hurt — and fails only if the read itself throws. Whichever way it lands is written into ADR 0009's revisit note and into open risk 7.

**§16.4's device assertions** are C# under `Platforms\**` compile-item guards, `[DeviceFact]`-gated so the host leg stays green:

- GenAI's natives actually load — a real Android `.so` from the AAR, a real iOS xcframework force-loaded into the app, and whatever Mac Catalyst's RID-graph fallback really does;
- `ortEnvCreatedBeforeGenAi` is true and SP2's `ortEnvironmentPreexisting` is false, both read out of the one `EdgeDiagnosticsReport`;
- `EdgeChatDeviceProfile.TotalMemoryBytes` is non-null and plausible, and `AvailableMemoryKind` is **`PerProcess` on iOS** and **`SystemWide` on Android** — the distinction the whole budget branches on;
- a simulated `MemoryPressure(Critical)` moves `LastPressure`, terminates the turn, unloads the model, and the next acquire reloads it;
- **the Android lane asserts the merged manifest** carries `INTERNET`, `ACCESS_NETWORK_STATE` and `ai.onnxruntime.genai.TelemetryInitializer` — so the AAR's contribution to every consumer's APK is documented by a test rather than discovered by a reviewer — and reports the running ABI and asserts `chatSupportedOnThisAbi`;
- the lane logs the full chat diagnostics block and publishes it as an artifact, which is also how §19 item 10's real `TotalMem` / `PhysicalMemory` readings accumulate.

**Each lane records the execution provider; none asserts one.** GenAI is CPU-only on every one of these lanes by construction, and both Apple lanes run `macos-15-intel` with x64 RIDs, so there is no Apple Neural Engine present to assert about even if a provider existed.

- [ ] **Step 1: Add the `Qavren.Edge.Rag` reference**

```xml
    <!-- Spec 16.1's cross-package literal assertion needs a project that sees BOTH packages. The
         edge is created HERE and not in the skeleton, because waves 3 and 4 had one task writing
         Qavren.Edge.Rag while another built this project (plan adjustment 14). Qavren.Edge.Rag is
         net10.0 only and this project multi-targets; a platform TFM referencing a net10.0 library
         is ordinary TFM compatibility, exactly as Qavren.Edge.VectorData.Tests already does. -->
    <ProjectReference Include="..\..\src\Qavren.Edge.Rag\Qavren.Edge.Rag.csproj" />
```

- [ ] **Step 2: `CrossPackageLiteralTests`**

Assert, as plain string equality, that `new ChatHistoryOptions().PinnedMessageKeys` contains exactly `RagCitations.ContextMessagePropertyKey`, and that the literal is `"qavren.edge.rag.context"`. The two constants are duplicated across packages on purpose — `Qavren.Edge.Rag` does not reference `Qavren.Edge.Chat.Onnx` and must not — and this is the same asserted-equal-duplicate guard SP2 already uses for `EdgeVectorData.QueryGeneratorServiceKey` and `EdgeEmbeddings.QueryServiceKey`.

- [ ] **Step 3: Tier 2**
- [ ] **Step 4: Tier 3 (the test half; the workflow half is already Task 2.3's) — including `corpus.json` copied literally and `CorpusInvariantTests`, which does NOT skip**
- [ ] **Step 5: The device assertions**

- [ ] **Step 6: Verify — tier 2 runs for real here, tier 3 skips**

```powershell
$env:QAVREN_EDGE_CHAT_MODEL_DIR = $null
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp4\chat\tests\Qavren.Edge.Chat.Tests\Qavren.Edge.Chat.Tests.csproj" `
  -c Release -f net10.0 -p:TargetFrameworks=net10.0 -p:ArtifactsPath=D:\Local\Temp\qedge-sp4\w6\t61
```

Expected: every tier-1 and tier-2 test passes on real GenAI natives; the tier-3 **facts** report **skipped**, having downloaded and loaded nothing — while `CorpusInvariantTests` **runs and passes**, because it is tier 1 and loads only the committed JSON. Confirm both in the output: a run where `CorpusInvariantTests` is reported skipped means it picked up the `SkipUnless` by mistake and the exclusivity invariant is unguarded. The guidance probe's result is printed whichever way it goes.

- [ ] **Step 7: Verify — the device legs compile**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
foreach ($tfm in 'net10.0-android','net10.0-ios','net10.0-maccatalyst','net10.0-windows10.0.19041.0') {
  dotnet build "$root\chat\tests\Qavren.Edge.Chat.Tests\Qavren.Edge.Chat.Tests.csproj" -c Release -f $tfm -p:ArtifactsPath=D:\Local\Temp\qedge-sp4\w6\t61
  if ($LASTEXITCODE -ne 0) { throw "FAILED to compile $tfm" }
}
Write-Host 'OK: all four device TFMs compile'
```

**`-f <tfm>` with NO `-p:TargetFrameworks`** — adjustment 12's exception. The global property would force `Qavren.Edge.Rag` (net10.0 only) to restore at a TFM it does not have, `NETSDK1005`.

---

### Task 6.2: Wave 6 close — sequential re-verify and commit *(integrator)*

**Local-verifiable:** yes.

- [ ] **Step 1:** `Remove-Item -Recurse -Force "D:\Local\Temp\qedge-sp4\w6" -ErrorAction SilentlyContinue`
- [ ] **Step 2:** Re-run Steps 6 and 7 sequentially without `ArtifactsPath`. This is where the new `Chat.Tests → Qavren.Edge.Rag` edge is proved in the tree CI will use.
- [ ] **Step 3:** Optionally exercise the tier-3 **opt-in** path once by hand — download the Qwen bundle into a scratch directory, set `QAVREN_EDGE_CHAT_MODEL_DIR`, and run the suite — so the lane is known to work before the first nightly rather than after it. 495 MB, paid once, by hand, never in CI on a PR. If skipped, say so here.
- [ ] **Step 4: Commit**

```
feat(sp4): tier-2 over the committed fixture, the tier-3 facts and the device assertions

- real ORT GenAI natives: load, stream, cache, cancel, terminate, unload, probe
- the OrtEnv ordering row asserted from two public diagnostics keys
- Tier3 SkipUnless + the 20-chunk deterministic RAG corpus
- Android manifest-merge and Apple/Android device-profile assertions
- the pinned-key literal asserted equal across the two packages
```

- [ ] **Step 5: Verify** — the `OK` lines, clean `git status --short`.

---

## WAVE 7 — Device host and sample app *(strictly serial — one implementer at a time)*

### Task 7.1: Device-host wiring — one `ProjectReference` and the Apple floor

**Local-verifiable:** yes for the android build and the Apple **compile**; the device **run** is CI's.

**Files:**
- Edit: `foundation\tests\Qavren.Edge.DeviceTests\Qavren.Edge.DeviceTests.csproj`

**Approach.** Two edits, both to a **test host** and neither to a shipped package.

1. One `ProjectReference` to `Qavren.Edge.Chat.Tests`. `SetTargetFramework` is not needed: it multi-targets the four device TFMs, so MSBuild resolves the matching one. `Qavren.Edge.Rag.Tests` is deliberately **not** referenced — it is host-only and `net10.0` alone, exactly as SP2's conformance project is.
2. The Apple floor rises from `15.0` to **`15.4`** on the `ios` and `maccatalyst` conditions (adjustment 8). Android is already `24.0` from SP2, because ORT's AAR already declared `minSdkVersion 24` and GenAI's declares the same (measured).

```xml
    <!-- Raised from 15.0 for sub-project 4: ORT GenAI's managed asset group is net9.0-ios15.4 and
         its xcframework slices are built at that floor, so linking one into a 15.0 deployment
         target is a linker warning at best and a load failure on a 15.0 device at worst. Android
         stays 24.0 - GenAI's AAR declares minSdkVersion 24, the same floor ORT's already did. A
         test host, not a shipped package; spec 5 named this as the one contingency and the plan
         makes it definite. -->
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios'">15.4</SupportedOSPlatformVersion>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'maccatalyst'">15.4</SupportedOSPlatformVersion>
```

**Two things this build proves by building at all**: the platform floors (an AAR declaring `minSdkVersion 24` merged into a project declaring less is a build failure) and the package-size numbers, since the lane produces an app package and §19 item 5 is `ls -l` and `size -m` on an artifact it creates anyway.

**One thing to watch.** The GenAI native package's `build/net8.0` targets carry a `Microsoft_ML_OnnxRuntimeGenAI_CheckPrerequisites` error target that fails an `Exe` whose `PlatformTarget` is neither `x64`, `arm64` nor `AnyCPU` (it exempts iOS explicitly). If the android or windows head trips it, the documented response is `-p:SuppressOnnxRuntimePlatformCompatibilityError=true` **on that head only**, recorded in the csproj with this paragraph as the reason — never repo-wide.

- [ ] **Step 1: Both edits**
- [ ] **Step 2: Verify — the android head builds here**

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge-sp4\foundation\tests\Qavren.Edge.DeviceTests\Qavren.Edge.DeviceTests.csproj" `
  -f net10.0-android -c Release
```

Expected: exit 0. **`-f` alone, no `-p:TargetFrameworks`** (adjustment 12's exception). A manifest-merge failure here is the floor question answering itself.

- [ ] **Step 3: Verify — the Apple and Windows heads compile here**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
foreach ($tfm in 'net10.0-ios','net10.0-maccatalyst','net10.0-windows10.0.19041.0') {
  dotnet build "$root\foundation\tests\Qavren.Edge.DeviceTests\Qavren.Edge.DeviceTests.csproj" -f $tfm -c Release
  if ($LASTEXITCODE -ne 0) { throw "FAILED to compile the device host for $tfm" }
}
Write-Host 'OK: the device host compiles for ios, maccatalyst and windows on this box'
```

**This throws; it does not print and continue.** Environment ground truth measured that Apple *compilation* succeeds on this Windows host — SP2's open question, closed for SP4 — and Task 1.5 Step 5 compiled the tier-0 device head for all four platform TFMs under the same conditions. A measured-green leg is an assertion, and a loop that prints `SKIPPED/FAILED` and exits 0 would leave the Apple heads of the device host ungated until a CI run nobody has scheduled. What genuinely cannot happen here is the **link**: it needs a Mac, so a *link* failure cannot appear on this box at all and stays in **CI-only work**. A **compile** failure is a real finding and stops the task.

---

### Task 7.2: Sample app — the Chat and Ask pages, and the iOS entitlements

**Local-verifiable:** yes for the Windows and Android heads; the Apple heads are CI's.

**Files:**
- Create: `foundation\samples\Qavren.Edge.Sample\Pages\ChatPage.xaml` + `.xaml.cs`
- Create: `foundation\samples\Qavren.Edge.Sample\Pages\AskPage.xaml` + `.xaml.cs`
- Create: `foundation\samples\Qavren.Edge.Sample\Platforms\iOS\Entitlements.plist`
- Edit: `foundation\samples\Qavren.Edge.Sample\Qavren.Edge.Sample.csproj` (two `ProjectReference`s, the iOS `CodesignEntitlements`)
- Edit: `foundation\samples\Qavren.Edge.Sample\AppShell.xaml` (two tabs)
- Edit: `foundation\samples\Qavren.Edge.Sample\MauiProgram.cs` (the two builder calls)

**Approach.** §18: the SP1 sample gains two pages rather than SP4 shipping a second sample app, because a second sample would double the workload installs on four device lanes to prove nothing new. The app already has an `EmbeddingsPage`, a `SearchPage` and a `DiagnosticsPage`; the third renders two more components **with no code change**, since `EdgeDiagnosticsReport` walks contributors generically.

**Chat page**, in order, and the ugly paths are the point:

1. **The consent sheet first**: model name, size, licence identifier and URL, free disk — all from `IChatModelProvisioner.Plan()` — and a download button wired to `IProgress<ModelProvisioningProgress>`.
2. **The metered-network hook in one line**, because that is how a reader discovers SP4 does not read connectivity for them and why:
   ```csharp
   o.Provisioning.IsTransferPermitted = () => Connectivity.Current.ConnectionProfile == ConnectionProfile.WiFi;
   ```
3. A streaming conversation showing tokens/sec, time-to-first-token, the resolved context and the stop reason **for every turn** — including, deliberately, the ugly ones: a `Suspended` turn after backgrounding the app, and a `ChatInsufficientMemory` refusal with its **full explanation rendered rather than swallowed**.

**Ask page**: the RAG recipe over the existing Search page's notes — question in, streamed grounded answer out, numbered sources beside it, each `[n]` resolving to its citation. A toggle swaps `EdgeChatClient` for `ExtractiveChatClient` **in place**, so a reader can see the no-LLM floor answer the same question through the same pipeline.

**`Platforms/iOS/Entitlements.plist`** is **new** — the sample has one for Mac Catalyst only — and sets the two keys §12.4 names:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <!-- iOS 15+: raises the OS-imposed resident limit. -->
  <key>com.apple.developer.kernel.increased-memory-limit</key><true/>
  <!-- iOS 14+: extends the process virtual address space - the one that matters for a
       multi-GB memory-mapped model. -->
  <key>com.apple.developer.kernel.extended-virtual-addressing</key><true/>
</dict>
</plist>
```

wired with `<CodesignEntitlements Condition="…== 'ios'">Platforms\iOS\Entitlements.plist</CodesignEntitlements>`. SP4 does not write anyone's entitlements file; the sample setting both is how a consumer discovers they need them.

The sample targets the four MAUI platform TFMs, as SP1 adjustment 31 established.

- [ ] **Step 1: The two `ProjectReference`s and the entitlements wiring**
- [ ] **Step 2: The two pages and the two tabs**
- [ ] **Step 3: `MauiProgram` — the §4.3 wiring verbatim**

```csharp
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddOnnxEmbeddings(o => o.Preset = EmbeddingPresets.MiniLmL6V2Int8)
    .AddVectorStore()
    .AddVectorCollectionMigration<string, Note>(version: 1, "notes")
    .AddOnnxChat(ChatPresets.Llama32_1BInstructInt4,
                 configure: o => o.Provisioning.IsTransferPermitted =
                     () => Connectivity.Current.ConnectionProfile == ConnectionProfile.WiFi,
                 pipeline: chat => chat.UseRag())
    .AddVectorStoreRetriever<string, Note>("notes",
        n => new RagSource(n.Key, n.Body) { Title = n.Title, Uri = n.Url }));
```

- [ ] **Step 4: Verify**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
dotnet build "$root\foundation\samples\Qavren.Edge.Sample\Qavren.Edge.Sample.csproj" -f net10.0-windows10.0.19041.0 -c Release
if ($LASTEXITCODE -ne 0) { throw "FAILED: sample windows head" }
dotnet build "$root\foundation\samples\Qavren.Edge.Sample\Qavren.Edge.Sample.csproj" -f net10.0-android -c Release
if ($LASTEXITCODE -ne 0) { throw "FAILED: sample android head" }
Write-Host 'OK: sample builds for windows and android'
```

Expected: the `OK` line. The two Apple heads are CI's.

---

### Task 7.3: Wave 7 close — sequential re-verify and commit *(integrator)*

**Local-verifiable:** yes.

- [ ] **Step 1:** Re-run Task 7.1 Steps 2–3 and Task 7.2 Step 4 sequentially.
- [ ] **Step 2: Commit**

```
feat(sp4): device-host wiring and the sample Chat and Ask pages

- DeviceTests references Qavren.Edge.Chat.Tests; Apple floor 15.4 (GenAI's own asset floor)
- sample: consent sheet, streaming turn stats, a rendered refusal, the RAG Ask page
- sample: iOS entitlements for increased memory limit and extended virtual addressing
```

- [ ] **Step 3: Verify** — clean `git status --short`.

---

## WAVE 8 — Closing gate

### Task 8.1: Full-solution verification *(integrator)*

**Local-verifiable:** yes, except the lanes named in **CI-only work**.

**Files:** none. This task is read-only and changes nothing.

- [ ] **Step 1: Restore, format**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
dotnet restore "$root\QavrenEdge.slnx"
if ($LASTEXITCODE -ne 0) { throw "FAILED: restore" }
dotnet format "$root\QavrenEdge.slnx" --verify-no-changes --no-restore
if ($LASTEXITCODE -ne 0) { throw "FAILED: format" }
```

`dotnet format` loads every project including the MAUI heads, which is why this box — with every workload installed — is where it runs.

- [ ] **Step 2: Every host-lane test project**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
$multi = @(
  "foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj",
  "foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj",
  "embeddings\tests\Qavren.Edge.Onnx.Tests\Qavren.Edge.Onnx.Tests.csproj",
  "embeddings\tests\Qavren.Edge.Embeddings.Tests\Qavren.Edge.Embeddings.Tests.csproj",
  "embeddings\tests\Qavren.Edge.VectorData.Tests\Qavren.Edge.VectorData.Tests.csproj",
  "chat\tests\Qavren.Edge.Chat.Tests\Qavren.Edge.Chat.Tests.csproj")
$single = @(
  "foundation\tests\Qavren.Edge.Provider.Tests\Qavren.Edge.Provider.Tests.csproj",
  "foundation\tests\Qavren.Edge.Sqlite.Cipher.Tests\Qavren.Edge.Sqlite.Cipher.Tests.csproj",
  "embeddings\tests\Qavren.Edge.VectorData.Conformance.Tests\Qavren.Edge.VectorData.Conformance.Tests.csproj",
  "chat\tests\Qavren.Edge.Rag.Tests\Qavren.Edge.Rag.Tests.csproj")
foreach ($p in $multi)  { dotnet run --project "$root\$p" -c Release -f net10.0 -p:TargetFrameworks=net10.0; if ($LASTEXITCODE -ne 0) { throw "FAILED: $p" } }
foreach ($p in $single) { dotnet run --project "$root\$p" -c Release -p:TargetFrameworks=net10.0;           if ($LASTEXITCODE -ne 0) { throw "FAILED: $p" } }
Write-Host 'OK: all ten host-lane suites passed'
```

**Never `dotnet test`** — SP1 adjustment 36.

- [ ] **Step 3: Pack and confirm the two new packages**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
dotnet pack "$root\QavrenEdge.slnx" -c Release -o "$root\artifacts\packages"
foreach ($id in 'Qavren.Edge.Chat.Onnx','Qavren.Edge.Rag') {
  $pkg = Get-ChildItem "$root\artifacts\packages\$id.*.nupkg" -ErrorAction SilentlyContinue |
         Where-Object { $_.Name -notmatch '\.symbols\.' } | Select-Object -First 1
  if (-not $pkg) { throw "$id did not pack" }
  Write-Host "OK $($pkg.Name)"
}
```

Then assert the **produced nuspec's dependency set**, which is the thing CPM's transitive-pinning switch exists to protect:

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$nupkg = Get-ChildItem "$root\artifacts\packages\Qavren.Edge.Chat.Onnx.*.nupkg" | Where-Object { $_.Name -notmatch '\.symbols\.' } | Select-Object -First 1
$zip = [IO.Compression.ZipFile]::OpenRead($nupkg.FullName)
$entry = $zip.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
$nuspec = (New-Object IO.StreamReader($entry.Open())).ReadToEnd(); $zip.Dispose()
if ($nuspec -match 'Microsoft\.ML\.OnnxRuntimeGenAI\.Managed') {
  throw "the produced nuspec names Microsoft.ML.OnnxRuntimeGenAI.Managed; a transitive was pinned and promoted into the public dependency set"
}
if ($nuspec -notmatch 'Microsoft\.ML\.OnnxRuntimeGenAI') { throw "the produced nuspec does not depend on Microsoft.ML.OnnxRuntimeGenAI" }
Write-Host 'OK: the nuspec depends on the native metapackage and does not widen to the managed one'
```

- [ ] **Step 4: Workflow contract**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
$py   = "C:\Python314\python.exe"
& $py -m pip install --quiet --disable-pip-version-check pyyaml   # assert-workflows.py imports yaml
if ($LASTEXITCODE -ne 0) { throw "could not install pyyaml into $py" }
& $py "$root\foundation\tools\ci-checks\assert-workflows.py" $root
if ($LASTEXITCODE -ne 0) { throw "assert-workflows.py failed" }
```

- [ ] **Step 5: Every device and Apple leg compiles**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp4"
dotnet build "$root\chat\src\Qavren.Edge.Chat.Onnx\Qavren.Edge.Chat.Onnx.csproj" -c Release
foreach ($tfm in 'net10.0-android','net10.0-ios','net10.0-maccatalyst','net10.0-windows10.0.19041.0') {
  dotnet build "$root\chat\tests\Qavren.Edge.Chat.Tests\Qavren.Edge.Chat.Tests.csproj" -f $tfm -c Release
  if ($LASTEXITCODE -ne 0) { throw "FAILED: Chat.Tests $tfm" }
}
Write-Host 'OK: every SP4 TFM compiles'
```

- [ ] **Step 6: Trace every spec requirement to a task**

Walk the spec and confirm each of the following exists in the tree. Anything missing means the plan is **not** closed:

- Two packages, no third "recipe" assembly (§4.1).
- **`Qavren.Edge.Rag` references no ONNX package and no `Qavren.Edge.VectorData`**, and `Qavren.Edge.Rag.Tests` references neither either (§2 decision 2, adjustment 19) — grep both csproj.
- The two packages do **not** reference each other (§4.1).
- Exactly **one** SP1 library edit (`EdgeErrorCode`), plus the enumerated non-API files: `.slnx`, `Directory.Packages.props`, `ci.yml`, `assert-workflows.py`, `DeviceTests`, the sample, the SP2 spec sentence, and the two trim-smoke files (§5 + adjustment 7).
- `EdgeChatStartupOrder` 400 / 410 / 420, all sorting after SP2's 200 (§14.1).
- **One** lifecycle observer in `Qavren.Edge.Chat.Onnx` and **none** in `Qavren.Edge.Rag` (§14.2).
- Two diagnostics contributors, **neither** component name containing "Native" (§14.3).
- `EdgeChatEventIds` 900–959 and `EdgeRagEventIds` 960–999, **published from the package that logs them**, every call site through `LoggerMessage.Define` (§14.4).
- Every `EdgeErrorCode` 7000–7299 is raised from somewhere **and** has an anchor in `foundation/docs/errors.md` (§15, §16.1) — proved by the **two** per-assembly coverage tests, `ChatErrorCodeCoverageTests` (20 codes, 7001–7108) and `RagErrorCodeCoverageTests` (4 codes, 7201–7204), each with the guard that its key set equals the enum's members in its range (adjustment 23). Grep both for the codes the draft plan had orphaned — **7001, 7002, 7101, 7102, 7104, 7106 and 7203** — and confirm each names a raise-site test rather than only appearing in a dictionary.
- **`VectorStoreRetrieverOptions<TRecord>.RequireHybridSearch` exists, defaults `false`, and is 7203's only raise site** (adjustment 22) — the option §7 did not declare and without which that code is unreachable.
- **§14.4's Trace-only rule holds in both packages** (adjustment 27): `LoggingPrivacyTests` passes in `Qavren.Edge.Chat.Tests` and in `Qavren.Edge.Rag.Tests`, and a grep of both `src` trees finds **no** `LoggerMessage.Define` above `Trace` whose message template carries a prompt, a message body, a retrieved chunk or a completion.
- **Tier 0 is recorded, not assumed** (adjustment 21): `chat/tools/tier0-genai-smoke/` and `.github/workflows/tier0-genai-smoke.yml` exist, the workflow is `workflow_dispatch`-only and no `tier0-*` job is in `ci.yml`, and `chat/README.md` carries all six legs' results — including the explicit statement that the **packaged** MAUI WinUI path was not tested because the repo has no signing identity. If `tier0-maccatalyst` failed, Task 1.5 Step 6's six-item fallback is visible in the tree and adjustment 21(a) records it.
- **The generated fixtures are digest-pinned, not retyped** (adjustments 24, 25): `GenAiConfigFixtureTests` ties both config bodies to the same SHA-256s `ChatPresets.g.cs` pins, and `TinyChatModelFixtureTests` asserts every fixture file's materialised length against its `const int`.
- The budget's golden tables, including the two ladder walks and the **negative** that `MeasuredPeakBytes` does not bind for either shipped preset (§16.1).
- The multi-token stop sequence across a decode-chunk boundary (§16.1).
- `ResponseFormat = Text` and `= null` complete a turn under **all three** guidance policies (§16.1).
- `OrtEnv.IsCreated` is still false after building the provider and resolving `IChatClient` (§8.1, §16.1).
- `GetService` returns `ChatClientMetadata` with a non-null `DefaultModelId`, **through** a `RagChatClient` stack (§16.1).
- The RAG golden block byte for byte; `Rank`'s two polarities; citation spans against the accumulated answer **and** against `ChatResponse.Text` with the same offsets; a duplicate marker → one annotation, two regions; a `[7]` with five sources dropped and counted (§16.1).
- The source-passing channel: set on the clone, **not** on the caller's instance; inert on the model path (§16.1).
- **`UseRag()` over `ExtractiveChatClient` produces a cited answer with no model** (§16.1).
- The two cross-package literals asserted equal (§16.1).
- Tier 2 against real natives, including the cached-generator `max_length` assertion and the five conversation-cache rules (§16.2).
- **The nightly `chat-model-tests` lane exists and is *not* in `ci-gate`'s `needs`**, with a `schedule` guard, `actions/cache@v6.1.0` keyed on the model's SHA-256, and `sha256sum -c` on cache hits too (§16.3).
- The tier-3 facts skip at **runtime** with no download, their model built inside the test body (§16.3).
- §16.4's device assertions exist and compile for every platform TFM (§16.4).
- Two new `ci.yml` test steps, the GenAI asset assertion, the ORT floor guard, the five `assert-workflows.py` rules, and the `trx2junit`/`java-junit` counts still at **4** (§17).
- `Qavren.Edge.Rag` in the `trim-smoke` console (§17, adjustment 7).
- Two sample pages plus the two iOS entitlement keys (§18).
- ADRs 0009–0013 (§19 item 13).
- **The tier-3 corpus is the one specified here, and its exclusivity invariant is a test** (adjustment 26): `Tier3/corpus.json` is byte-for-byte the twenty literal chunks in Task 6.1, with the gold chunk `warranty-compressor`, the question, the required token and the seed `20260911` — and `Tier3/CorpusInvariantTests` exists, carries **no** `SkipUnless`, **ran** (not skipped) in Step 2's host run, and asserts that exactly one chunk matches `seven|\b7\b` case-insensitively. Grep the file for `SkipUnless`: a hit there means the invariant is unguarded and the nightly's third assertion can pass vacuously. `ConfigOverlaySemanticsFacts` exists and prints `overlayMergeSemantics` (adjustment 28, §19 item 12).
- **`chatIntraOpNumThreads` has a real owner** (adjustment 29): `Internal/ChatSessionOptionsReader.cs` exists, is source-generated (`ChatSessionOptionsJsonContext`), reads `model.decoder.session_options.intra_op_num_threads`, and `ChatSessionOptionsReaderTests` covers all five arms. Grep `Qavren.Edge.Chat.Onnx` for `JsonDocument`, `JsonNode` and `JsonSerializer.Deserialize` **without** a context argument: the load path takes **no** reflection-based parse, and `ChatModelShape` still declares no thread-count member.
- **§16.1's "Projector shape (§7)" bullet is owned in all three clauses** (§16.1): `ProjectorShapeTests` asserts the single `Func<TRecord, RagSource>` signature reaches both `VectorStoreRetriever`'s constructor and the one and only `AddVectorStoreRetriever`; `VectorStoreRetrieverTests` owns the overwrite and the two-lane polarity clauses. And `AddVectorStoreRetriever`'s `storeName` arm — unkeyed, keyed, and a missing key failing at resolve — has tests, which §7 declares and the draft plan did not cover.
- **Exactly one root-file carve-out exists** (adjustment 30): `.github/workflows/tier0-genai-smoke.yml` is the only file under `.github/`, `QavrenEdge.slnx`, `Directory.Packages.props`, `Directory.Build.*` or `.gitignore` that an implementer task created, and `git log --format=%s -- <each root path>` on the branch shows every other change landing in an integrator's wave-close commit.
- **Every Python invocation in the tree and in this plan names `C:\Python314\python.exe`** — `grep -n "python3\? " ` over this plan's shell blocks finds no bare interpreter, and `pyyaml` is installed by Task 1.5 Step 7, which is the first step that parses YAML.
- **Every wave has a close commit on `feat/sp4-chat`** — seven of them (1.6, 2.4, 3.3, 4.3, 5.2, 6.2, 7.3; wave 8 is read-only and commits nothing), none on `main`, none carrying an AI attribution trailer.

- [ ] **Step 7: Verify**

Steps 1–6 all pass. The plan is closed; the branch `feat/sp4-chat` is ready for its PR.

---

## CI-only work

Everything below is authored and locally checked as far as this box allows, and executes for the first time in CI or on the Mac Mini. Nothing here is unverified *design* — it is verified design with a host this machine does not have.

**Every row names the task that writes it.** A row with no owning task would be work nobody does. CI does not write code.

| Item | Written by | Why its *execution* is CI-only |
|---|---|---|
| Every `ci.yml` run: the two chat test steps, the GenAI asset assertion, the ORT floor guard | **Task 2.3** | No workflow runs during implementation; implementers never push. The YAML is linted, `assert-workflows.py` is run, and both test commands are run by hand here |
| Tier 3: the `chat-model-tests` nightly — Qwen3 0.6B int4 at revision `c1d7bbbb…`, `actions/cache@v6.1.0` keyed on sha256 `52640ca0…`, `sha256sum -c` on every run | **Task 2.3** (the job, the cache, the hash verify) + **Task 6.1** (`ChatModelAvailable`, `RealModelFacts`, `corpus.json`) | Nightly schedule, never on a PR, and **not** in `ci-gate`'s `needs`. The skip path is exercised here (Task 6.1 Step 6); an opt-in real run is Task 6.2 Step 3, by hand, once |
| §19 item 4: **peak RSS at the resolved context, beside the arithmetic**, for the KV-dominated preset — the measurement that decides whether `KvCacheBytesPerElement` is 2 or 4 | **Task 6.1** (the test that prints it) | Needs the real 495 MB model, which only the nightly lane fetches |
| §16.0 item 1(a): **does Mac Catalyst actually link**, given the `_._` build folder and the RID-graph fallback to `runtimes/ios/native/` | **Task 1.5** writes the smoke and the `tier0-maccatalyst` job and the literal fallback; **Task 1.6 Step 3** dispatches it and **Step 4** records the answer; the standing guard afterwards is **Task 6.1**'s device assertions | The failure is a runtime `DllNotFoundException`, not a build error, and a restore assertion cannot catch it — `net10.0-maccatalyst` resolves `lib/net9.0-maccatalyst14.0` perfectly and tells you nothing. It runs on `macos-15-intel`, which this box does not have. **It gates wave 2** (adjustment 21): if it fails, Task 1.5 Step 6's six-item fallback runs in wave 1 — three TFMs, the `ci.yml` and `assert-workflows.py` rows dropped, the README's "Mac Catalyst has no chat in v1" sentence — before any API is written |
| §16.0 item 1(b): the four TFMs **get the natives** on a real device or emulator | **Task 1.5** (the five device/simulator tier-0 legs), then **Task 7.1**, **Task 7.2** | The restore and compile halves are closed here, and Task 1.5 Step 4 closes the Windows `net10.0` half over a real `Model` + `Generator`. Loading a real `.so`, a force-loaded xcframework or the WinUI head's `runtimes/win-x64` needs a device, a simulator or an emulator |
| §16.0 item 1(b), the **packaged** MAUI WinUI half | — **nobody; it is out of reach and said so** | The repo has no signing identity, which is why `Qavren.Edge.DeviceTests` and the tier-0 device head both set `WindowsPackageType=None`. Tier 0 proves the **unpackaged** WinUI resolution path; `chat/README.md` states in those words that MSIX packaging is untested (Task 1.2). This row exists so the gap is a recorded non-closure rather than a silently-passing claim |
| `device-tests-android` **execution** (emulator + KVM on `ubuntu-24.04`) | **Task 7.1** | No Android emulator here; the android **build** of the device host is Task 7.1 Step 2 |
| `device-tests-ios` and `device-tests-maccatalyst` picking up `Qavren.Edge.Chat.Tests` | **Task 7.1** | No macOS or Xcode on this box. The Apple **compile** succeeds here (measured); the **link** does not exist here at all |
| §16.4's device-only assertions: `AvailableMemoryKind` `PerProcess` on iOS vs `SystemWide` on Android, the merged-manifest check, the pressure latch, drop-and-reload, the published diagnostics block | **Task 6.1** | The code compiles here for all four device TFMs and skips on the host through `[DeviceFact]`; it can only *run* on a device or simulator |
| §19 item 5: the `.apk`/`.aab` delta with all ABIs and with `$(AndroidSupportedAbis)` trimmed to `arm64-v8a` (including what the measured 32.7 MB `libmat.so` contributes); the iOS `__TEXT` before and after against Apple's 80 MB cap; the same for Mac Catalyst if it links | — (measurement; README wording by **Task 1.2**) | The device lanes already produce app packages, so this is `ls -l` and `size -m` — but on artifacts built in CI |
| §19 item 3: **iOS memory, measured** — peak RSS at 2048 and 4096 context for both presets, with and without the two entitlements, plus tokens/sec and TTFT | — (measurement) | There is **no published tokens/sec or peak-RSS figure for ORT GenAI on any Apple device**. Needs the Mac Mini and a real device. Not gating; record in the vault, then in the README |
| §19 item 6: whether minja parses **each preset's** chat template | **Task 6.1** (the tier-3 assertion) | Per-model parse success needs the real model. The probe converts a mid-turn throw into a startup fact either way |
| §19 item 9: whether `Utils.DisableTelemetryEvents()` prevents 1DS initialisation or only suppresses events, and whether the `TelemetryInitializer` provider can be stripped with `tools:node="remove"` | **Task 6.1** (the manifest-merge assertion) + **Task 1.2** (the README wording) | Needs a running Android app and a network trace. Until then the README says "events are disabled at the API GenAI exposes", not "telemetry is off" |
| §19 item 10: the device floor's three totals, measured — real `ActivityManager.MemoryInfo.TotalMem` and `NSProcessInfo.PhysicalMemory` readings | **Task 6.1** (the lane publishes the diagnostics block) | Reading an artifact the device lanes create. The 0.85× constants stay until then |
| `trim-smoke`'s `linux-x64` publish and its AOT leg | **Task 4.2** | Published and run for `win-x64` here; the CI leg is `ubuntu-24.04` |
| Branch protection and the PR | — | The owner's, after the branch is pushed |

## Open risks

1. **`KvCacheBytesPerElement = 2` is calibrated against exactly one device measurement.** `genai_config.json` does not state the KV dtype, so the load-time cross-check cannot validate the one field that multiplies the entire KV term. The default lands within 1.2% of a measured 1280 MiB peak for `Llama32_1BInstructInt4`, where 4 would put it 11% over — but the preset whose KV term *dominates* is Qwen, at 112 KiB/token and 448 MiB at 4096, and nobody has measured it. §16.3's nightly prints measured peak RSS beside the arithmetic on its first run. §19 item 4.
2. **`WorkspaceBytes`, `ReserveBytes`, `SystemWideMemoryFraction`, the ladder and both `MinTotalMemoryBytes` floors are engineering estimates.** Guessing high refuses devices that would have worked; guessing low gets the app killed by jetsam or lmkd. The 0.85× floors in particular are set so that one constant means one device class across two platforms that measure total memory differently — Android's `TotalMem` excludes kernel-reserved memory, Apple's `PhysicalMemory` does not. §19 item 10.
3. **The ladder cannot rescue a weight-dominated preset, and the README must not imply it can.** Walked end to end, `Llama32_1BInstructInt4` buys **96 MiB, about 6%** (measured, in this plan's table). A device short by more than that is refused at every rung. For that shape the lever is a smaller preset, not a shorter context, and the 7005 remediation names it first.
4. **`SetRuntimeOption("terminate_session", "1")` from another thread is an inference, not a documented guarantee.** `src/ort_genai_c.h` says flatly "This API is not thread safe", and this is the one GenAI call SP4 makes outside the turn gate. Task 6.1 asserts it and runs it under a stress loop. **If it is unsafe**, the fallback is the volatile flag alone — the decode loop still aborts between tokens, a long prefill runs to completion — and that limit goes in the README. §19 item 7.
5. **There is no load-cancellation flag in GenAI.** A multi-second model load cannot be aborted once started; SP4 cancels *before* the load and refuses to start one while the pressure latch is `Critical`. That is the whole mitigation and §15.3 documents the gap rather than papering over it.
6. **The conversation cache's delta append is tokenised on its own, so a BPE merge spanning the join is lost.** The join is expected to fall immediately after a special token, which is atomic — an expectation about each preset's template, not a proof. Task 6.1 checks it on the fixture; the nightly checks it per preset. **If it differs**, the affected preset ships with `EnableConversationCache` defaulted off in its own definition, a one-line change. §19 item 11.
7. **`Config.Overlay`'s merge semantics are undocumented.** §9.1 composes one document in managed code precisely so it does not matter, but "we made it not matter" is weaker than "we know". Task 4.1's `ConfigOverlayTests` asserts SP4's own composed document; **Task 6.1's `Tier3/ConfigOverlaySemanticsFacts` answers the native question** — load the Qwen preset's real `Config`, `Overlay` one `{"search":{"max_length":2048}}` document, read `do_sample` / `temperature` / `top_k` / `top_p` back through `GetSearchBool`/`GetSearchNumber`, and print `overlayMergeSemantics = deep-merge | replace` into the job summary. It asserts nothing about the answer, because both are legal; it fails only if the read throws. That is §19 item 12 discharged by a named test rather than by something the nightly "can additionally" do (adjustment 28), and the answer lands in ADR 0009's revisit note.
8. **Guidance is unverified, not unavailable, and `NotEnforced` means "not proven enforced".** A guidance-enabled build could also fail the probe with an unusual model or template. The probe asserts **output shape**, never the absence of an exception, because at v0.15.2 `CreateGuidanceLogitsProcessor` returns `nullptr` with at most a log line on a `USE_GUIDANCE=OFF` build. ADR 0011.
9. **An armeabi-v7a Android device gets the database and embeddings and no chat at all.** GenAI's AAR ships `arm64-v8a` and `x86_64` only (measured); SP1's SQLite native ships `android-arm`. 7004 says so at startup by name, the README states it, and the device lane reports the running ABI — but it is a real product gap, not a defensive guess.
10. **Adopting chat roughly doubles a consumer's native payload.** The AAR is 21.5 MB compressed and each ABI carries a **32.7 MB `libmat.so`** whose purpose is documented nowhere upstream and which there is no known way to exclude. The merged manifest also gains `INTERNET`, `ACCESS_NETWORK_STATE` and a telemetry content provider. All of it is measured, in the README, and asserted by a device test — and none of it is avoidable inside v1.
11. **Apple's 80 MB `__TEXT` cap is the only ceiling with no workaround**, because the xcframework is force-loaded statically and SP2's ORT already spends part of that budget. §19 item 5(b) measures it from the device lane's own artifact. If it is exceeded, the sub-project has a problem no option flag solves.
12. **Nothing in CI re-runs `fetch_chat_model_hashes.py`.** A repo owner can repoint a branch; `paths-info` against a vanished commit SHA returns 404, which is the good failure. The bad one is subtler: a *newer* revision with better weights is invisible until somebody looks. Re-run the script before each release and diff — a changed digest is a deliberate decision about which weights ship, never a merge conflict to resolve.
13. **The tier-2 fixture generator is run by hand and CI never runs it.** An onnx, torch, transformers or builder upgrade that changes the serialised bytes is invisible until somebody re-runs it. Task 1.3's determinism check is what turns "somebody re-ran it and the bytes moved" into a failure rather than a surprise. The `transformers` 5.17.0 pin is the likeliest thing to break the builder, and the documented fallback is 4.57.1.
14. **The two packages take no trim or AOT annotations from GenAI.** `IsAotCompatible=true` applies repo-wide to packable projects, and what a trimmed publish actually does to `Qavren.Edge.Rag` is measured by `trim-smoke` and by nothing else. `Qavren.Edge.Chat.Onnx` is **not** in `trim-smoke` — it would need a model — so its AOT claim rests on the analyzer alone, and the README claims nothing more than that.
15. **A new `ci.yml` job forgotten in `ci-gate`'s `needs` does not block a merge.** `assert-workflows.py` now checks that `chat-model-tests` specifically is **not** gated and that `trim-smoke` is; it cannot know which *future* jobs ought to be. Adding a job means adding it to `needs:` in the same PR. Inherited from SP1 open risk 8.
16. **Turns serialise, and on a server that is a throughput ceiling.** One gate per model, one cached `Generator`; a fifth queued turn is refused by name. Two simultaneous KV caches on a nominal-6 GB phone is the jetsam scenario the whole design exists to avoid — but a consumer running this on a desktop will notice, and the README says so rather than letting it read as a bug.

