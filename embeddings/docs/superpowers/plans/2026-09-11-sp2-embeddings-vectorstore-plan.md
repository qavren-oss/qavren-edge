# Qavren.Edge Sub-project 2 — Embeddings + Vector Store: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `Qavren.Edge.Onnx`, `Qavren.Edge.Embeddings.Onnx` and `Qavren.Edge.VectorData` — ONNX Runtime hosting, a `Microsoft.Extensions.AI` embedding generator, and a clean-room `Microsoft.Extensions.VectorData` provider over `vec0` + FTS5 with reciprocal rank fusion — composed on top of the merged sub-project 1 foundation.

**Architecture:** Three packages on two layers. `Qavren.Edge.Onnx` (L0) owns `OrtEnv`, the ref-counted session host, the execution-provider policy, the no-backup model root, model provisioning and the resource monitor. `Qavren.Edge.Embeddings.Onnx` (L1) owns the tokenizer abstraction, tensor assembly, pooling, normalisation, batching, the model presets and the single public `IEmbeddingGenerator<string, Embedding<float>>`. `Qavren.Edge.VectorData` (L1) owns the MEVD provider: the collection model builder, the LINQ→SQL filter translator, the SQL emitter, and RRF hybrid search over SP1's `IEdgeDatabase`. The two L1 packages never reference each other.

**Tech Stack (verified on this box, see Environment ground truth):** .NET SDK 10.0.401, C# 14, `Microsoft.ML.OnnxRuntime` 1.30.0, `Microsoft.ML.Tokenizers` 2.0.0, `Microsoft.Extensions.AI(.Abstractions)` 10.10.0, `Microsoft.Extensions.VectorData.Abstractions` 10.10.0, `Microsoft.Extensions.VectorData.ConformanceTests` 10.10.0, xunit.v3 3.2.2 on Microsoft.Testing.Platform, SQLite 3.53.4 + sqlite-vec v0.1.9 from SP1's natives, uv 0.12.1 + CPython 3.14.5 + onnx 1.22.0 + onnxruntime 1.30.0 for fixture generation.

**Worktree:** `C:\Users\steve\projects\qavren-edge-sp2`, branch **`feat/sp2-embeddings`**. The `main` checkout at `C:\Users\steve\projects\qavren-edge` is in use by another agent and **must never be touched**.

**Branch and commit model.** Implementers **never run git**. One **integrator** per wave owns every root file — `QavrenEdge.slnx`, `Directory.Packages.props`, `Directory.Build.props`, `Directory.Build.targets`, `.gitignore`, `.github/**` — and commits the wave onto `feat/sp2-embeddings`. Tasks marked **(integrator)** below are the integrator's own; every other task is an implementer's, and an implementer that finds itself wanting to edit a root file has found a plan bug, not a licence.

**Every wave has exactly three phases, and every wave has an integrator.** This is not ceremony; two concrete hazards make it necessary.

1. **Prologue (serial, integrator).** A task that mutates a file every project in the repo imports — `Directory.Packages.props`, `Directory.Build.props`, `QavrenEdge.slnx` — cannot share a wave with a task whose verify *builds* anything, because the disjoint-build-graph rule is about the **files a build reads**, not only about who compiles. Only wave 1 has a prologue (Task 1.1). No later wave edits a root file except Task 2.3, which edits `.github/**` only — a path no `dotnet build` reads.
2. **Parallel phase (implementers, at most three).** Folder-disjoint **and** build-graph-disjoint as the Wave map's columns show — plus **output-disjoint**, which folders alone do not buy. MSBuild takes no cross-process lock on a project's `obj/` or `bin/`, so two tasks that both build `Qavren.Edge.Core` will intermittently produce `MSB3021` or "the process cannot access the file … because it is being used by another process", which reads like a flaky test and is not. **Every verify command in a parallel phase therefore carries `-p:ArtifactsPath=<the task's own scratch root>`**, which sends both `obj/` and `bin/` to `…\artifacts\{obj,bin}\<ProjectName>\<Config>_<tfm>\` under a directory no other task touches. `ArtifactsPath` is an SDK 8+ property, the repo sets neither it nor `UseArtifactsOutput` (verified in `Directory.Build.props`), and it is per-project-named, which `BaseOutputPath` is not — passing `BaseOutputPath` as a global property would flatten every project in a graph into one directory and trade one collision for another.
3. **Close (serial, integrator).** The integrator deletes the parallel phase's scratch roots, re-runs **every** verify command in the wave **sequentially in the repo's real `obj/`/`bin/`** — which is what the next wave, and CI, will actually use — and only then commits. A wave that passes only under isolation has not passed.

The close task is where "the wave is committed" becomes a checkable event rather than an assumption, and it is why every wave below ends in one.

**Scratch roots.** `D:\Local\Temp\qedge-sp2\w<wave>\t<task>` — e.g. Task 3.2's verify appends `-p:ArtifactsPath=D:\Local\Temp\qedge-sp2\w3\t32`. On a box where `D:` is unavailable, `C:\Users\steve\AppData\Local\Temp\qedge-sp2\…` is the substitute; the path is arbitrary, the disjointness is not.

**Convention for "literal code".** Every artifact this plan *originates* is literal and complete: csproj files, the `Directory.Packages.props` block, the `.slnx` entries, the workflow YAML, the Python generators, the SP1 amendments, the emitted SQL, and every test assertion whose expected value was measured rather than guessed.

**Public API surfaces are adopted BY REFERENCE, not transcribed.** Where the spec declares a type as literal C# — §6.1–§6.5, §7, §8 — the plan says "exactly as §N declares it" and the implementer transcribes that block **verbatim, XML docs included**: the docs on `ExecutionProviderReport.Accepted`, on `IEdgeTokenizer.Encode`, on `OnnxEmbeddingOptions.PinnedSequenceLength` and on `EdgeVectorStoreCollection`'s two constructors are contract, not decoration. The spec is 3,191 lines of normative prose and re-typing its declarations here would create a second source of truth that can drift — a plan whose `EmbeddingPreset` and the spec's `EmbeddingPreset` disagree by one `init` default is worse than a plan that points at one. Two obligations come with the by-reference rule and both are load-bearing:

1. **Every public type the spec declares is NAMED by exactly one task**, so no type is ownerless. A type nobody names is a type nobody writes, and the way that failure presents is a compile error three waves later in a task that assumed it existed.
2. **Every default value that changes behaviour is restated in the owning task**, because a default is the one part of a declaration a reader never questions. Those are gathered under a **Defaults that change behaviour** heading in Tasks 2.2 (`OnnxOptions`), 3.1 (`HttpOnnxModelSourceOptions`), 3.2 (`EdgeVectorStoreOptions`, `RrfDefaults`) and 4.1 (`WordPieceTokenizerOptions`, `EmbeddingPreset`'s non-`required` `init` members, `OnnxEmbeddingOptions`), and each one is either asserted by a test in the same task or named in the README. Task 4.2 consumes the `RrfDefaults` values rather than declaring them, which is why its numbers appear inline in its search prose and its heading is absent.

Method **bodies** of the larger internal implementations are specified by their declared signature, their stated behaviour and the spec section that defines them, rather than transcribed. Where a body has a trap in it (the `200`-to-a-ranged-request restart, the bm25 sign, the `IncludeVectors` double bracket, the CoreML partition timing, the `VecBlob` encoding) the trap is restated inside the task, because that is exactly what a reader of the task alone would otherwise get wrong.

---

## Environment ground truth (this box)

Everything in this table was measured on 2026-09-11 in the `feat/sp2-embeddings` worktree, not assumed.

| Fact | Value |
|---|---|
| .NET SDK | 10.0.401 (`global.json`, `rollForward: latestFeature`), MTP runner selected repo-wide |
| Workloads | android, ios, maccatalyst, maui-windows installed; **restore** for `net10.0-ios` / `net10.0-maccatalyst` succeeds on this Windows host |
| Visual Studio | 2026 Community, toolset v145 |
| NuGet global packages | `D:\packages\nuget` (**not** `%USERPROFILE%\.nuget\packages`) |
| SP1 natives on disk | `foundation\native\artifacts\win-x64\{qedge_sqlite3.dll, qedge_sqlcipher.dll}` — `Qavren.Edge.Sqlite.Native` stages the host-RID copy flat into every referencing project's output |
| ORT CPU EP | runs locally; `Microsoft.ML.OnnxRuntime` 1.30.0 ships `runtimes/win-x64/native` |
| uv | 0.12.1 at `C:\Users\steve\.local\bin\uv.exe` |
| Python | `C:\Python314\python.exe`, CPython **3.14.5**. It already carries onnx 1.22.0 / onnxruntime **1.27.0** / numpy 2.4.4 — the wrong ORT. The plan builds its own pinned venv anyway (Task 1.3) |
| Fixture venv (built and used) | `uv venv --python 3.14` → onnx 1.22.0, onnxruntime **1.30.0**, numpy 2.5.3, protobuf 7.36.1, flatbuffers 25.12.19, ml-dtypes 0.6.0 — all resolve for cp314 on win-x64 |
| PyYAML | **6.0.3**, in `C:\Python314`. `python` on `PATH` does resolve to `C:\Python314\python.exe`, but a WindowsApps shim sits behind it, so every invocation in this plan spells the interpreter absolutely |
| Network | `huggingface.co` is reachable from this box. The four preset manifests in Task 3.3 were fetched here on 2026-09-11 over the documented `paths-info` API |
| Build isolation | `Directory.Build.props` sets neither `ArtifactsPath` nor `UseArtifactsOutput` (verified), so `-p:ArtifactsPath=…` is free for the parallel phases to use |
| NOT available | macOS / Xcode; Android emulator; any workflow run (implementers never push) |

**ORT asset resolution — §19 item 1, CLOSED here.** A throwaway class library targeting `net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst`, spelled exactly as SP1 spells it, referencing `Microsoft.ML.OnnxRuntime` 1.30.0, restored on this Windows box:

```
net10.0             -> lib/net8.0/Microsoft.ML.OnnxRuntime.dll                 (NOT netstandard2.0)
net10.0-android     -> lib/net9.0-android35.0/Microsoft.ML.OnnxRuntime.dll
net10.0-ios         -> lib/net9.0-ios18.0/Microsoft.ML.OnnxRuntime.dll
net10.0-maccatalyst -> lib/net9.0-maccatalyst18.0/Microsoft.ML.OnnxRuntime.dll
```

The same restore was run twice — once with SP2's `SupportedOSPlatformVersion` of `android 24.0` / `ios 15.1` / `maccatalyst 15.1`, once with SP1's `21.0` / `15.0` — and **the resolved asset paths were byte-identical**. That is the proof §4.1 rests on: `TargetPlatformVersion` drives asset selection, `SupportedOSPlatformVersion` drives nothing in restore. SP2 pins no versioned TFM.

Two details the spec's §17 wording needs to survive contact with the file: the entries live under **`Microsoft.ML.OnnxRuntime.Managed`** (the native `Microsoft.ML.OnnxRuntime` entry has no `compile`/`runtime` assets at all, only `build`/`buildTransitive`), and the Mac Catalyst `buildTransitive/net9.0-maccatalyst18.0/_._` placeholder is a real zero-byte file, exactly as ORT's own README claims.

**ORT 1.30.0 `runtimes/` directory listing:** `android`, `ios`, `linux-arm64`, `linux-x64`, `osx-arm64`, `win-arm64`, `win-x64`. There is **no `osx-x64`** — §15's `OnnxUnsupportedRuntime` is a real condition, not a defensive guess.

**Package coexistence — §19 item 7, CLOSED here.** The full `net10.0` closure of `OnnxRuntime 1.30.0 + Tokenizers 2.0.0 + Extensions.AI(.Abstractions) 10.10.0 + VectorData.Abstractions 10.10.0`:

```
Google.Protobuf/3.30.2                                Microsoft.Extensions.Primitives/10.0.12
Microsoft.Extensions.AI/10.10.0                       Microsoft.Extensions.VectorData.Abstractions/10.10.0
Microsoft.Extensions.AI.Abstractions/10.10.0          Microsoft.ML.OnnxRuntime/1.30.0
Microsoft.Extensions.Caching.Abstractions/10.0.12     Microsoft.ML.OnnxRuntime.Managed/1.30.0
Microsoft.Extensions.DependencyInjection.Abstractions/10.0.12   Microsoft.ML.Tokenizers/2.0.0
Microsoft.Extensions.Logging.Abstractions/10.0.12     System.Numerics.Tensors/10.0.12
```

Nothing promotes SP1's `Microsoft.Extensions.*` pins above 10.0.12. Two new transitives arrive and both are benign with central transitive pinning off (SP1 adjustment 20): `System.Numerics.Tensors` 10.0.12 and `Google.Protobuf` 3.30.2 (from `OnnxRuntime.Managed`).

**Conformance suite — §19 item 6, CLOSED here.** `Microsoft.Extensions.VectorData.ConformanceTests` 10.10.0 restores on `net10.0` and its closure pins **xunit.v3 3.2.2** — the repo's existing DeviceRunners-driven pin. No 4.0.0 demand, no separate pin, no dropped suite. It brings `xunit.v3.assert`, `xunit.v3.common` and `xunit.v3.extensibility.core` but **not** the `xunit.v3` metapackage, so the conformance project adds `xunit.v3` itself to get the Microsoft.Testing.Platform entry point.

**MEVD 10.10.0 surface — spot-checked against the shipped assembly.** Every provider-services symbol §8/§12 names exists in `Microsoft.Extensions.VectorData.Abstractions.dll`: `CollectionModelBuilder`, `CollectionModelBuildingOptions`, `ReservedKeyStorageName`, `EmbeddingGenerationDispatcher`(`s`), `FilterTranslatorBase`, `FilterPreprocessingOptions.SupportsParameterization`, `IKeywordHybridSearchable`, `VectorStoreException`, `VectorStoreCollectionOptions`, `HybridSearchOptions`, `VectorStoreCollectionDefinition`, `BuildDynamic`, `ResolveEmbeddingType`, `GetFullTextDataPropertyOrSingle`, `VectorStoreMetadata`, `VectorStoreCollectionMetadata`, `SupportsMultipleVectors`, `RequiresAtLeastOneVector`, `SupportsKeyAutoGeneration`, `IsDataPropertyTypeValid`, `IsVectorPropertyTypeValid`, `ValidateKeyProperty`, `ValidateProperty`, `RecordRetrievalOptions`, `FilteredRecordRetrievalOptions`, `VectorSearchResult`, and the `MEVD9001` experimental diagnostic id.

**Tokenizers 2.0.0 surface — spot-checked.** `BertTokenizer`, `BertOptions`, `LowerCaseBeforeTokenization`, `RemoveNonSpacingMarks`, `IndividuallyTokenizeCjk`, `EncodeToIds`, `GetSpecialTokensMask`, `BuildInputsWithSpecialTokens`, `CreateTokenTypeIdsFromSequences`, `WordPieceTokenizer`, `GetIndexByTokenCount`, `CountTokens` all present. **`CreateFromTokenizerJson` is absent** and the string `attention` does not occur anywhere in the assembly — §1's multilingual cut and §11's "the mask is synthesised here" are both confirmed facts, not cautious readings. The package ships `lib/net8.0` and `lib/netstandard2.0` only.

**ORT managed surface — spot-checked.** `AppendExecutionProvider`, `OrtEnv.CreateInstanceWithOptions`, `EnvironmentCreationOptions`, `DOrtLoggingFunction`, `OrtEnv.IsCreated`, `OrtEnv.DisableDllImportResolver`, `SessionOptions.AddFreeDimensionOverrideByName`, `SetLoadCancellationFlag`, `DisablePerSessionThreads`, `AddSessionConfigEntry`, `OrtValue.CreateTensorValueFromMemory`, `GetTensorDataAsSpan`, `RunOptions`, `OrtMemoryInfo` all present. `ProfileComputePlan` is **not** a managed member — it is a CoreML provider-option string, which is how §6.1 already treats it.

**Preset manifests — §10.2, CLOSED here.** Every per-file size and SHA-256 the four presets need was fetched on 2026-09-11 by the exact procedure §10.2 prescribes — one `POST https://huggingface.co/api/models/{repo}/paths-info/{rev}` per repo, resolved against the repo's current commit SHA, taking the **git-LFS `oid`** and never the `xetHash`. The four graph sizes match §7's stated totals to the byte, which is the independent check that the right files were named.

| Preset | Repo | Revision (full commit SHA) | Graph file | Bytes | SHA-256 |
|---|---|---|---|---|---|
| `MiniLmL6V2Int8` | `sentence-transformers/all-MiniLM-L6-v2` | `1110a243fdf4706b3f48f1d95db1a4f5529b4d41` | `onnx/model_qint8_arm64.onnx` | 23,026,053 | `4278337fd0ff3c68bfb6291042cad8ab363e1d9fbc43dcb499fe91c871902474` |
| `MiniLmL6V2Fp32` | `sentence-transformers/all-MiniLM-L6-v2` | `1110a243fdf4706b3f48f1d95db1a4f5529b4d41` | `onnx/model.onnx` | 90,405,214 | `6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452` |
| `BgeSmallEnV15` | `BAAI/bge-small-en-v1.5` | `5c38ec7c405ec4b44b94cc5a9bb96e735b38267a` | `onnx/model.onnx` | 133,093,490 | `828e1496d7fabb79cfa4dcd84fa38625c0d3d21da474a00f08db0f559940cf35` |
| `NomicEmbedTextV15Int8` | `nomic-ai/nomic-embed-text-v1.5` | `e9b6763023c676ca8431644204f50c2b100d9aab` | `onnx/model_quantized.onnx` | 137,296,292 | `b4342336debaea79de872370664b0aaeb67dea4605513d00ee236ea871a81f27` |

Three facts fell out of that fetch and all three change what the plan writes.

- **`onnx/model_qint8_arm64.onnx`, `onnx/model_qint8_avx512.onnx` and `onnx/model_qint8_avx512_vnni.onnx` return the identical SHA-256** — `4278337f…` for all three, 23,026,053 bytes each. §7's "ONE blob under three names" is confirmed rather than repeated, and it is why `MiniLmL6V2Int8` pins the `arm64` name and one download covers every RID.
- **All three repos ship a byte-identical `vocab.txt`:** 231,508 bytes, SHA-256 `07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3` (the 30,522-entry `bert-base-uncased` vocabulary). One constant serves all four presets.
- **`vocab.txt` is not LFS-backed, so its `oid` is *not* a SHA-256.** `paths-info` returns `fb140275c155a9c7c5a3b3e0e77a9e839594a938` for it — 40 hex characters, the git blob **SHA-1**. §10.2's "the `oid` … **is** the SHA-256" holds only for the `lfs` sub-object. Writing that SHA-1 into a manifest would make every provisioning verify fail at runtime with a hash mismatch nobody could debug, so it is plan adjustment 19 and the fetch script handles the two cases separately.

**Consequences.** Every host-lane verify in this plan runs here. Apple compilation, Apple/Android device execution, every workflow run, the tier-3 nightly model lane and the §19 size measurements are CI-only (see **CI-only work**). Implementers **must not run git**. **Never use `cd`** in a shell command — it wipes PATH in this environment. PowerShell is the primary shell and every path is absolute.

---

## Spec adjustments

Every place this plan departs from the approved spec is enumerated here — whether because verified research contradicted the spec (**the facts win**) or because the spec's own wording is unimplementable as written and the plan does the nearest correct thing. Each adjustment names the task that implements it. If a reader diffing the spec against the plan finds a difference that is **not** in this list, that is a bug in the plan, not a deliberate choice.

1. **§19 item 1 is already done, and it did not need the Mac Mini.** The spec schedules the ORT asset-resolution probe as pre-work on two machines. It was run on this Windows box for all four TFMs and at both platform-floor settings; the results are in Environment ground truth and are identical across the two settings. Nothing in restore is Mac-only. The standing CI guard (§17 assertion 2) is still written, because a future SDK or workload bump is exactly what it exists to catch. (Task 1.1, Task 2.3.)

2. **§17's ORT assertion must scan `Microsoft.ML.OnnxRuntime.Managed`, not `Microsoft.ML.OnnxRuntime`.** In `project.assets.json` the native package carries no `compile`/`runtime` assets for any TFM — only `build`/`buildTransitive`. An assertion written against the native package id would pass vacuously forever. (Task 2.3.)

3. **§16.1's fixture set contradicts itself, and the plan ships six fixtures rather than five.** The spec's base fixture pools *inside the graph* (`Gather → Cast → Unsqueeze → Mul → ReduceSum/ReduceSum → Div`, emitting `embedding[batch, dims]`) and then claims "one assertion covers the pooler, the mask plumbing and the tensor layout". It cannot cover *our* pooler: a graph that already emits `[batch, dims]` never exercises `EmbeddingPooler.MeanPool`, and no real preset has that shape — §11 says every preset emits `last_hidden_state[batch, seq, dim]` and pooling is always ours. So the plan adds a **`HiddenStates`** fixture — a single `Gather`, output `last_hidden_state[batch_size, sequence_length, dims]` — and that is the fixture `OnnxEmbeddingGenerator` consumes end-to-end. The spec's in-graph `MeanPool` fixture is kept and still earns its place: it proves the mask/`Cast`/`Unsqueeze`/`ReduceSum` tensor plumbing against real ORT with none of our C# in the path. `UnusedTokenTypeIds` and `NomicInputOrder` become hidden-states shaped, because their whole point is what the **generator** feeds and binds. (Task 1.3.)

4. **The fixtures are 533–831 bytes, not 747.** Measured with onnx 1.22.0, vocab 16 × dim 4, IR 10 / opset 17 / empty producer: `HiddenStates` **533**, `MeanPool` **772**, `ClsPool` **553**, `UnusedTokenTypeIds` **592**, `NomicInputOrder` **592**, `IrVersion14` **533**, `vocab.txt` **215** (64 entries). The spec's "747-byte" figure and its `vocab 16 × dim 4 = 471 bytes` row were both close but wrong; §16.1's *argument* (everything is reviewable in a PR diff, no binary in git, no 768-d fixture is affordable) is unaffected. The plan asserts the measured sizes so drift is caught. (Task 1.3.)

5. **onnx 1.22.0's checker refuses an `ir_version` above its own.** `onnx.checker.check_model` on the IR-14 fixture raises `ValidationError: Your model ir_version 14 is higher than the checker's (13)`. The generator therefore skips the checker for that one fixture only, guarded on `ir_version <= onnx.IR_VERSION`. This is also the direct confirmation of §16.1's load-bearing claim that onnx 1.22's IR ceiling is 13, which is exactly ORT 1.30.0's — and ORT rejects the fixture with `Unsupported model IR version: 14, max supported IR version: 13`, which is the message the error-path test asserts. (Task 1.3, Task 3.1.)

6. **Nothing binary is committed; the committed fixture is a `.cs` file.** §16.1 is explicit — "downloads nothing, ships no binary into git". The generator writes `embeddings/tests/fixtures/TinyModels.g.cs` holding base64 `const string`s, and that file is what git carries. Any `.onnx` it writes for debugging goes to a gitignored scratch directory. The file lives under `fixtures/` and is **linked** — never copied — into the **four** projects that consume it, with `<Compile Include="..\fixtures\TinyModels.g.cs" Link="Fixtures\TinyModels.g.cs" />`: `Qavren.Edge.Onnx.Tests` (T3.1), `Qavren.Edge.Embeddings.Tests` (T4.1), `Qavren.Edge.VectorData.Tests` (T5.1) and `Qavren.Edge.TrimSmoke` (T6.1, at a different relative depth). One task owns the file; no project holds a copy. (Tasks 1.1, 1.3.)

7. **§4.2's layout omits the `trim-smoke` project, and §17 requires one.** A job that publishes "a small `net10.0` console" needs a csproj. It is `embeddings/tools/Qavren.Edge.TrimSmoke/` — under `tools/`, **not** `tests/`, so that Task 2.3's new `assert-workflows.py` rule ("every test project under `embeddings/tests/` appears as an explicit `ci.yml` step") globs exactly the four test projects and is not confused by a project that is published rather than run as a test step. (Tasks 1.1, 6.1, 2.3.)

8. **`Qavren.Edge.VectorData.Tests` references `Qavren.Edge.Sqlite.Native`.** §4.2 lists no package references for it, but every tier-2 assertion needs `vec0` and FTS5, which arrive only with the native. This is exactly what SP1's `Qavren.Edge.Sqlite.Tests` already does, and the Native project's `QedgeHostRid` `None` item is what puts `qedge_sqlite3.dll` beside the test binary on this box. `Qavren.Edge.VectorData.Conformance.Tests` and `Qavren.Edge.TrimSmoke` need it for the same reason. (Task 1.1.)

9. **§5's "two edits and no others" is three plan tasks plus one new docs file.** The two library edits are exactly as specified (`EdgeErrorCode`, `FtsTable`). The third is the contingency §16.4 already names: `foundation/tests/Qavren.Edge.DeviceTests` gains three `ProjectReference`s and, if the merged Android manifest demands it, a `SupportedOSPlatformVersion` bump — a **test host**, not a shipped package — Task 7.1. The fourth item is not code at all: `foundation/docs/errors.md` **does not exist** in the merged SP1 tree even though `EdgeException.HelpLink` has always pointed at it, so SP2 creates it covering 1001–4001 and 5000–5299. Creating a missing docs file that SP1's own error contract already promises is additive by any reading. (Tasks 1.4, 2.1, 7.1, 1.2.)

10. **`FtsTable` stays non-`partial`.** §5.2 declares `public static partial class FtsTable`; the merged class is a plain `public static class` in one file, and SP2 edits that same file. `partial` would buy nothing and would be a gratuitous diff on a merged SP1 type. The new overloads take `FtsTableOptions` as a **required** parameter in the position where the old overload's optional `contentTable` sits, so `BuildCreateSql(name, cols, tokenizer, options)` and `BuildCreateSql(name, cols, tokenizer, contentTable)` bind unambiguously and SP1's golden-SQL tests keep passing byte for byte. (Task 2.1.)

11. **`EdgeMemoryPressure` is confirmed untouched.** The merged enum is `{ Low, Moderate, Critical }` with `Low = 0` and `EdgeLifecycleRecord.Level` is already `EdgeMemoryPressure?`. §6.4's nullable latch is therefore idiomatic and no renumbering is proposed. Recorded as a confirmation so a later reader does not re-open it.

12. **Every verify command is `dotnet run --project <test csproj> -c Release -f net10.0 -p:TargetFrameworks=net10.0`, never `dotnet test`.** SP1 adjustment 36: the test projects multi-target for the device host, `dotnet run` refuses to pick a TFM, and `-f` alone does not stop restore walking the full `TargetFrameworks` list of the project *and everything it references* — so the workload check fires for TFMs this host cannot build. The two single-TFM projects (`…Conformance.Tests`, `…TrimSmoke`) take `-p:TargetFrameworks=net10.0` and no `-f`. **In a parallel phase every one of them also carries `-p:ArtifactsPath=D:\Local\Temp\qedge-sp2\w<wave>\t<task>`** (plan adjustment 26); the wave's close task re-runs the identical commands with that switch removed. (Every task.)

13. **The Apple TFM legs of `Qavren.Edge.Onnx` are compiled in CI, and a local probe decides whether they can also be compiled here.** Restore for `net10.0-ios` / `net10.0-maccatalyst` demonstrably works on this Windows host; whether the *compile* of `Platforms/iOS/**` (which touches `NSUrl`, `NSFileManager` and `DllImport("__Internal")`) succeeds without a Mac is not established, and §19 item 4 asks the question. Task 2.2 Step 8 runs the probe, prints the answer, and — like SP1's ARM64-toolset probe — **skips with a printed reason** rather than failing if the host refuses. The Apple compile is a CI gate either way.

14. **`Microsoft.ML.Tokenizers` 2.0.0 has no `net9.0`/`net10.0` asset.** `net10.0` resolves `lib/net8.0`. Harmless and recorded only so nobody "fixes" it.

15. **`IEdgeTokenizer.IndexByTokenCount` is our own spelling.** The underlying library member is `GetIndexByTokenCount`. §7's signature is kept as designed; the implementation forwards. Recorded so a reviewer grepping the library for `IndexByTokenCount` does not conclude it was invented.

16. **§19 items 3, 5, 9, 10, 11, 12 and 13 are not plan tasks — but §16.3's tier-3 lane *is*.** The exemption below is about §19's *measurements*, and it has never covered §16.3. To say it explicitly, because an earlier draft of this plan left the lane ownerless: the nightly real-model job, its `actions/cache@v6.1.0` step keyed on the model's SHA-256, its `schedule:` trigger, the `SkipUnless` test class, the pinned reference-vector JSON and the 1e-3 cosine assertions are **Task 2.3 (the workflow half) and Task 6.2 (the test half)**; §16.4's device-only assertions are likewise a task, Task 6.3 (plan adjustment 24). Only their *execution* is CI-only.

    The seven §19 items named above genuinely are exempt, because each is a measurement that needs a real device, a real model or a shipped release: package-size deltas and iOS `__TEXT` (item 3), the Mac Catalyst arm64 question (item 5), per-preset tokenizer parity against Hugging Face (item 9), the memory-headroom calibration (item 10), the `chunk_size` benchmark (item 11), what a trimmed/AOT publish actually does (item 12) and whether `PinnedSequenceLength` wins (item 13). The plan ships the spec's stated defaults, records each as an **Open risk** with its trigger, and ships the `trim-smoke` job (item 12's decider) as a real task. Item 5's default — README claims "Mac Catalyst x64, proven in CI" plus a manual arm64 run on the Mac Mini recorded in the vault — is written into the README by Task 1.2.

17. **§19 item 2's sub-checks (b) and (c) stay CI/Mac-Mini work; (a) becomes Task 7.1's verify.** Building `Qavren.Edge.DeviceTests` for `net10.0-android` with SP2 referenced is runnable here (the android workload is installed) and is the only one of the three that decides a file in this repo — whether the device host needs a `SupportedOSPlatformVersion` bump to 24.0. The Apple deployment-target link check and the `LC_BUILD_VERSION` read from ORT's `ios-arm64_x86_64-maccatalyst` slice need a Mac.

18. **ADRs live under `embeddings/docs/adr/` and continue SP1's numbering.** SP1 holds 0001 and 0002 in `foundation/docs/adr/`; SP2 writes 0003–0008 in its own tree rather than reaching into foundation's. The numbers stay globally unique so a cross-reference is never ambiguous. (Task 1.2.)

19. **A Hugging Face `paths-info` `oid` is the SHA-256 only when the file is LFS-backed.** §10.2 says the `oid` "**is** the SHA-256". Measured on this box: for `onnx/*.onnx` it is — those are LFS pointers and the response carries an `lfs: { oid, size }` sub-object holding a 64-hex digest. For `vocab.txt`, which is a plain git blob in all three preset repos, the response carries only the top-level `oid`, and that is the 40-hex git **SHA-1** (`fb140275c155a9c7c5a3b3e0e77a9e839594a938`). Baking the SHA-1 into a manifest would fail every provisioning verify at runtime. `fetch_preset_hashes.py` therefore takes `lfs.oid` when present and **downloads and hashes** the file when it is not — 231 KB for `vocab.txt`, paid once, by hand, never in CI. (Task 3.3.)

20. **The preset constants are literal in this plan, and regenerated by a checked-in script.** §7 declares `EmbeddingPreset`'s `required` fields but no *values*, and §10.2 names a script that no earlier draft of this plan created. Both halves now exist: the script is `embeddings/tools/model-hashes/fetch_preset_hashes.py`, its output is the committed `embeddings/src/Qavren.Edge.Embeddings.Onnx/EmbeddingPresets.g.cs`, and the measured values are in **Environment ground truth** so an implementer can write the file with no network at all. The verify re-runs the script and diffs. (Task 3.3.)

21. **`vocab.txt` is one constant, not four.** All three preset repos ship the identical 231,508-byte `bert-base-uncased` vocabulary, SHA-256 `07eced…`. The generated catalogue emits one `OnnxModelFile` record for it and reuses it across all four manifests. (Task 3.3.)

22. **`OnnxSessionOptions` and the session-options factory are wave 2, not wave 3.** Task 2.2's own tests assert the `RequireStaticInputShapes` × `FreeDimensionOverrides` guard, and `FreeDimensionOverrides` is a member of `OnnxSessionOptions` (§6.5). An earlier draft put that type — and the `SessionOptionsFactory` that applies `AddFreeDimensionOverrideByName`, `AddSessionConfigEntry` and `DisablePerSessionThreads`, and raises `OnnxStaticShapesUnpinned` — in Task 3.1, which made Task 2.2's test uncompilable a whole wave before its dependency existed. Both move to Task 2.2, together with `SessionFactoryShapeTests.cs`. Task 3.1 consumes them. (Tasks 2.2, 3.1.)

23. **§16.1's `PinnedSequenceLength` half of the session-factory assertion is an L1 test, and it needed its own file.** The sentence is one clause — "`RequireStaticInputShapes` with empty `FreeDimensionOverrides` raises `OnnxStaticShapesUnpinned`, while `PinnedSequenceLength` populates both the override dictionary and the flag" — but its two halves live on opposite sides of the layer boundary: `FreeDimensionOverrides` is `OnnxSessionOptions` (L0) and `PinnedSequenceLength` is `OnnxEmbeddingOptions` (L1), which `Qavren.Edge.Onnx.Tests` cannot see. The first half is `SessionFactoryShapeTests` in Task 2.2; the second is `PinnedSequenceLengthTests` in `Qavren.Edge.Embeddings.Tests`, Task 4.1. (Tasks 2.2, 4.1.)

24. **§16.4's device-only assertions are a task, not a lane property.** "The exclusion flag reads back set on iOS", "`IEdgeResourceMonitor` returns a plausible non-null `AvailableMemoryBytes`", "a `LastPressure` that a simulated pressure event moves", "the lane logs `ExecutionProviderReport` and publishes it" are all C# somebody has to write, under `Platforms\**` compile-item guards, in `Qavren.Edge.Onnx.Tests`. Task 6.3 owns them. Only their *execution* is CI-only. (Task 6.3.)

25. **Every wave gains a close task, and wave 1 gains a prologue.** See **Branch and commit model**. The trigger was two real defects in an earlier draft: wave 1 ran a CPM edit beside a task that builds `Qavren.Edge.Core` (which imports that CPM file), and waves 3–7 had no integrator at all while the Wave map required one to have committed before the next wave starts. (Tasks 1.1, 1.5, 2.4, 3.4, 4.3, 5.4, 6.4, 7.3.)

26. **Every parallel-phase verify carries `-p:ArtifactsPath`, and the close task re-runs them without it.** Folder-disjointness says nothing about who writes `obj/` and `bin/`, and MSBuild holds no cross-process lock on either. Wave 5 is the clearest case — three tasks reading the same frozen `Qavren.Edge.VectorData` sources while all three *build* it — and Task 5.3 is worse still, because adding a `ProjectReference` from `Qavren.Edge.Sqlite.Cipher.Tests` to `Qavren.Edge.VectorData` rewrites that project's assets graph while two other tasks are building it. Isolation during the parallel phase, one sequential canonical run in the close. (All waves.)

---

## File structure

```
qavren-edge-sp2/                              # the worktree; branch feat/sp2-embeddings
  QavrenEdge.slnx                             # T1.1 - SP2 projects appended
  Directory.Packages.props                    # T1.1 - six PackageVersion entries appended
  .gitignore                                  # T1.1 - fixture venv + scratch
  .github/workflows/ci.yml                    # T2.3 - four test steps, two asserts, trim-smoke job
  foundation/
    docs/errors.md                            # T1.2 - NEW; 1001-4001 and 5000-5299
    src/Qavren.Edge.Core/EdgeErrorCode.cs      # T1.4 - AMENDED (SP1 edit 1 of 2)
    src/Qavren.Edge.Sqlite/Fts/FtsTable.cs     # T2.1 - AMENDED (SP1 edit 2 of 2)
    tests/Qavren.Edge.Core.Tests/              # T1.4 - one added test
    tests/Qavren.Edge.Sqlite.Tests/            # T2.1 - added golden-SQL tests
    tests/Qavren.Edge.Sqlite.Cipher.Tests/     # T5.3 - the SQLCipher collection-lifecycle test
    tests/Qavren.Edge.DeviceTests/             # T7.1 - three ProjectReferences (+ floor, if needed)
    samples/Qavren.Edge.Sample/                # T7.2 - Embeddings + Search pages, Diagnostics toggles
    tools/ci-checks/assert-workflows.py        # T2.3 - three added assertions
  embeddings/
    README.md                                  # T1.2
    docs/adr/0003..0008-*.md                   # T1.2
    docs/superpowers/specs/                    # the approved SP2 spec
    docs/superpowers/plans/                    # this file
    src/
      Qavren.Edge.Onnx/                        # T1.1 csproj; T2.2 policy+paths+monitor; T3.1 store+host
        Platforms/Android/**  Platforms/iOS/**
      Qavren.Edge.Embeddings.Onnx/             # T1.1 csproj; T4.1
      Qavren.Edge.VectorData/                  # T1.1 csproj; T3.2 model+SQL+filter; T4.2 collections+RRF
    tests/
      fixtures/make_tiny_model.py              # T1.3 - regeneration path, NEVER a build step
      fixtures/TinyModels.g.cs                 # T1.3 - generated, committed, linked into 3 projects
      Qavren.Edge.Onnx.Tests/                  # T1.1 csproj; T2.2; T3.1
      Qavren.Edge.Embeddings.Tests/            # T1.1 csproj; T4.1
      Qavren.Edge.VectorData.Tests/            # T1.1 csproj; T3.2; T4.2; T5.1
      Qavren.Edge.VectorData.Conformance.Tests/# T1.1 csproj; T5.2
    tools/
      Qavren.Edge.TrimSmoke/                   # T1.1 csproj; T6.1
      model-hashes/fetch_preset_hashes.py      # T3.3 - regeneration path, NEVER a build step
      model-hashes/requirements.txt            # T3.3
    src/Qavren.Edge.Embeddings.Onnx/
      EmbeddingPresets.g.cs                    # T3.3 - generated, committed, literal SHA-256s
    tests/
      Qavren.Edge.Onnx.Tests/Platforms/**      # T6.3 - device-only assertions (spec 16.4)
      Qavren.Edge.Embeddings.Tests/Tier3/**    # T6.2 - real-model lane + reference vectors
```

Scratch, never committed: `embeddings/tests/fixtures/.venv`, `embeddings/tests/fixtures/_scratch`, `embeddings/tools/model-hashes/.venv` — all three gitignored by Task 1.1.

## Wave map

Wave membership obeys SP1 adjustment 26 — **disjoint folders AND disjoint build graphs** — plus the two rules **Branch and commit model** adds, because folders and graphs alone do not cover either hazard:

- **A task that mutates a file every project imports cannot share a wave with a task that builds anything.** `Directory.Packages.props` is imported by `Qavren.Edge.Core`, so Task 1.1 is a **serial prologue** to wave 1, not a member of it. Nothing else in this plan touches a root file that a `dotnet build` reads.
- **A task's verify writes `obj/` and `bin/` for every project in its graph, and MSBuild holds no cross-process lock on either.** So the "build graph" column below is also a *write* set, and any overlap in it is resolved by `-p:ArtifactsPath=<scratch>\w<wave>\t<task>` on every parallel-phase verify — never by hoping the timing works out. The close task then re-runs each verify **sequentially, without `ArtifactsPath`**, in the real tree.

Each wave: **prologue (if any) → parallel phase, at most three implementers → close**. The next wave starts only after the close task has re-verified the whole wave sequentially and committed it.

| Wave | Phase | Tasks | Folders each task owns | Build graph each task compiles (= its write set) | Parallel-safe because |
|---|---|---|---|---|---|
| 1 | prologue | **1.1** skeleton + CPM + slnx + `.gitignore` *(integrator)* | root files + every SP2 csproj | **restore only** — but it rewrites `Directory.Packages.props` and `QavrenEdge.slnx`, which every project reads | it runs **alone**; nothing else in wave 1 may be running |
| 1 | parallel | **1.2** docs/ADRs/`errors.md`; **1.3** fixture toolchain; **1.4** SP1 edit — `EdgeErrorCode` | `embeddings/README.md` + `embeddings/docs/adr` + `foundation/docs/errors.md` / `embeddings/tests/fixtures` / `src/Qavren.Edge.Core` + `tests/…Core.Tests` | none / none (python) / Core + Core.Tests | 1.2 and 1.3 compile nothing, so 1.4 is the only writer of any `obj/`; the CPM file is frozen by the prologue |
| 1 | close | **1.5** *(integrator)* | none | Core + Core.Tests, sequential | it is the only thing running |
| 2 | parallel | **2.1** SP1 edit — `FtsTable` options; **2.2** `Qavren.Edge.Onnx` part 1 + session-options factory; **2.3** `ci.yml` + `assert-workflows.py` *(integrator)* | `src/…Sqlite` + `tests/…Sqlite.Tests` / `embeddings/src/Qavren.Edge.Onnx` + `embeddings/tests/…Onnx.Tests` / `.github/workflows` + `foundation/tools/ci-checks` | Sqlite→Core (+Native) / Onnx→Core / none (YAML + python) | Core is frozen after wave 1; 2.3 compiles nothing; 2.1 and 2.2 **both write Core's `obj/`**, which `ArtifactsPath` separates |
| 2 | close | **2.4** *(integrator)* | none | Sqlite.Tests, Onnx.Tests, the workflow contract, sequential | — |
| 3 | parallel | **3.1** `Qavren.Edge.Onnx` part 2 — provisioning + session host; **3.2** `Qavren.Edge.VectorData` part 1 — model, schema SQL, filter translator; **3.3** preset manifests — fetch script + literal constants | `embeddings/src/Qavren.Edge.Onnx` + `…Onnx.Tests` / `embeddings/src/Qavren.Edge.VectorData` + `…VectorData.Tests` / `embeddings/tools/model-hashes` + the single file `embeddings/src/Qavren.Edge.Embeddings.Onnx/EmbeddingPresets.g.cs` | Onnx→Core / VectorData→Sqlite→Core + Sqlite.Native / **none** (python + one generated `.cs` that no wave-3 build compiles) | Sqlite is frozen after wave 2; 3.3 builds nothing and writes into a project nothing in this wave compiles; 3.1 and 3.2 share only frozen Core, separated by `ArtifactsPath` |
| 3 | close | **3.4** *(integrator)* | none | Onnx.Tests, VectorData.Tests, the generator diff, sequential | — |
| 4 | parallel | **4.1** `Qavren.Edge.Embeddings.Onnx`; **4.2** `Qavren.Edge.VectorData` part 2 — collections, writes, search, RRF | `embeddings/src/Qavren.Edge.Embeddings.Onnx` + `…Embeddings.Tests` / `embeddings/src/Qavren.Edge.VectorData` + `…VectorData.Tests` | Embeddings.Onnx→Onnx→Core / VectorData→Sqlite→Core | Onnx is frozen after wave 3; `…VectorData.Tests` does **not** reference Embeddings.Onnx until wave 5; the shared Core/`obj` write is separated by `ArtifactsPath` |
| 4 | close | **4.3** *(integrator)* | none | Embeddings.Tests, VectorData.Tests, sequential | — |
| 5 | parallel | **5.1** tier-2 integration tests; **5.2** MEVD conformance suite; **5.3** SQLCipher collection-lifecycle test | `embeddings/tests/…VectorData.Tests` / `embeddings/tests/…Conformance.Tests` / `foundation/tests/…Sqlite.Cipher.Tests` | each → VectorData + Embeddings.Onnx + Sqlite(.Native/.Cipher) → Core, all **frozen sources** | frozen sources, but **three concurrent writers of the same `bin`/`obj`**, and 5.3 additionally creates a brand-new Cipher.Tests→VectorData edge whose restore rewrites an assets graph the other two are building. `ArtifactsPath` is load-bearing here, not decorative |
| 5 | close | **5.4** *(integrator)* | none | all three suites, sequential, real tree | this is where the new Cipher→VectorData edge is proved in the tree CI will use |
| 6 | parallel | **6.1** `trim-smoke` console; **6.2** tier-3 real-model lane (test half); **6.3** device-lane platform assertions (§16.4) | `embeddings/tools/Qavren.Edge.TrimSmoke` / `embeddings/tests/…Embeddings.Tests/Tier3` / `embeddings/tests/…Onnx.Tests/Platforms` | TrimSmoke→VectorData+Embeddings.Onnx / Embeddings.Tests→Embeddings.Onnx→Onnx / Onnx.Tests→Onnx | everything compiled here was frozen in wave 5; all three overlap on Onnx and Core `obj/`, separated by `ArtifactsPath`. **No MAUI or platform-head build is in this wave** |
| 6 | close | **6.4** *(integrator)* | none | TrimSmoke publish, Embeddings.Tests, Onnx.Tests, sequential | — |
| 7 | **serial — one implementer at a time** | **7.1** device-host wiring, then **7.2** sample-app pages | `foundation/tests/…DeviceTests` → then `foundation/samples/…Sample` | DeviceTests→Maui+Sqlite+natives+3 SP2 test libs at platform TFMs / Sample→Maui+Sqlite+natives+all three SP2 packages at the same platform TFMs | **they are not parallel-safe and are not run in parallel.** Two MAUI/platform builds over one shared reference closure, each staging the same native `.dll`s, is the worst pairing in the plan; and 7.1 Step 2 *conditionally edits its own csproj* depending on a build outcome, so a second builder over that closure would be reading a file mid-edit. Serial removes both problems and costs one build |
| 7 | close | **7.3** *(integrator)* | none | both platform heads, sequential | — |
| 8 | gate | **8.1** full-solution verification *(integrator)* | none (read-only) | the whole `.slnx` | runs alone; it is the gate that closes the plan |

## WAVE 1 — Skeleton, docs, fixtures, and SP1 edit 1 of 2

### Task 1.1: SP2 skeleton — CPM, `.slnx`, and every csproj *(integrator; **wave-1 prologue — runs alone**)*

**Local-verifiable:** yes.

**Files:**
- Edit: `Directory.Packages.props`
- Edit: `QavrenEdge.slnx`
- Edit: `.gitignore`
- Create: `embeddings\src\Qavren.Edge.Onnx\Qavren.Edge.Onnx.csproj`
- Create: `embeddings\src\Qavren.Edge.Embeddings.Onnx\Qavren.Edge.Embeddings.Onnx.csproj`
- Create: `embeddings\src\Qavren.Edge.VectorData\Qavren.Edge.VectorData.csproj`
- Create: `embeddings\tests\Qavren.Edge.Onnx.Tests\Qavren.Edge.Onnx.Tests.csproj`
- Create: `embeddings\tests\Qavren.Edge.Embeddings.Tests\Qavren.Edge.Embeddings.Tests.csproj`
- Create: `embeddings\tests\Qavren.Edge.VectorData.Tests\Qavren.Edge.VectorData.Tests.csproj`
- Create: `embeddings\tests\Qavren.Edge.VectorData.Conformance.Tests\Qavren.Edge.VectorData.Conformance.Tests.csproj`
- Create: `embeddings\tools\Qavren.Edge.TrimSmoke\Qavren.Edge.TrimSmoke.csproj`

All paths are relative to `C:\Users\steve\projects\qavren-edge-sp2\`.

**Approach — read this before writing.** This task writes **only project files**, and it is the wave-1 **prologue**: Tasks 1.2, 1.3 and 1.4 do not start until it has finished. That is not caution, it is correctness. Compiling nothing is *not* what makes a task safe to run beside another — this one rewrites `Directory.Packages.props` and `QavrenEdge.slnx`, and `Directory.Packages.props` is imported by every project in the repo including `Qavren.Edge.Core`, which Task 1.4's verify builds. Worse, Step 10's `dotnet restore QavrenEdge.slnx` and Task 1.4's `dotnet run --project …Core.Tests` both write `foundation/src/Qavren.Edge.Core/obj/project.assets.json`. Running them concurrently is a race over one file, whichever order they finish in. Its verify is `dotnet restore QavrenEdge.slnx`, which is precisely the check that matters here (CPM resolves, TFMs are legal, ORT's mobile assets land where §4.1 claims). A stub csproj with no `.cs` files restores and, later, builds to an empty assembly; that is intended. No test csproj yet carries the `<Compile Include="..\fixtures\TinyModels.g.cs" …>` line — Task 1.3 is still writing that file in this same wave, and each test project adds the link itself in the wave that first needs it.

- [ ] **Step 1: Add the six package versions**

Append to `Directory.Packages.props`, immediately before the closing `</Project>`:

```xml
  <!-- Sub-project 2. Versions verified by restore on 2026-09-11 (plan Environment ground truth):
       ORT 1.30.0's managed asset resolves lib/net8.0 for net10.0 and lib/net9.0-{android35.0,
       ios18.0,maccatalyst18.0} for the three unversioned platform TFMs; Extensions.AI 10.10.0
       coexists with SP1's Microsoft.Extensions.* 10.0.12 pins without promoting any of them;
       ConformanceTests 10.10.0 pins xunit.v3 3.2.2, which is already this repo's pin. -->
  <ItemGroup Label="SP2 runtime">
    <PackageVersion Include="Microsoft.ML.OnnxRuntime" Version="1.30.0" />
    <PackageVersion Include="Microsoft.ML.Tokenizers" Version="2.0.0" />
    <PackageVersion Include="Microsoft.Extensions.AI" Version="10.10.0" />
    <PackageVersion Include="Microsoft.Extensions.AI.Abstractions" Version="10.10.0" />
    <PackageVersion Include="Microsoft.Extensions.VectorData.Abstractions" Version="10.10.0" />
  </ItemGroup>

  <ItemGroup Label="SP2 test">
    <PackageVersion Include="Microsoft.Extensions.VectorData.ConformanceTests" Version="10.10.0" />
  </ItemGroup>
```

- [ ] **Step 2: Ignore the two by-hand toolchains' scratch, and the parallel-phase build isolation**

Append to `.gitignore`:

```gitignore
# Sub-project 2 fixture toolchain. The generator's OUTPUT (TinyModels.g.cs) is committed;
# its venv and any .onnx it writes for debugging are not.
embeddings/tests/fixtures/.venv/
embeddings/tests/fixtures/_scratch/

# Sub-project 2 preset-hash toolchain (Task 3.3). Same shape: EmbeddingPresets.g.cs is
# committed, the venv is not. The script is run by hand and never by a build or by CI.
embeddings/tools/model-hashes/.venv/

# Parallel-phase build isolation. Every verify in a wave's parallel phase runs under
# -p:ArtifactsPath=<scratch>, which normally lands outside the repo - this entry exists only so
# that a run pointed at an in-repo path cannot be committed by accident.
/artifacts/
```

The tier-3 model cache is **not** listed, and deliberately: `QAVREN_EDGE_MODEL_DIR` is set to a path outside the repo on this box and to `${{ github.workspace }}/.model-cache` in CI, where the checkout is discarded after the job. Adding a `.model-cache/` entry would imply a supported in-repo location for a 23 MB blob, which is the opposite of what §10.1 and §16.3 want.

- [ ] **Step 3: `Qavren.Edge.Onnx.csproj`**

`embeddings\src\Qavren.Edge.Onnx\Qavren.Edge.Onnx.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Four TFMs, not five. Windows has neither a distinct model-path implementation (spec 6.2
         puts it on the desktop <Data>/models path) nor a distinct memory read (spec 6.4 gives it
         the same GC.GetGCMemoryInfo() path as net10.0), so a net10.0-windows leg would buy a
         build and nothing else. A net10.0-windows10.0.19041.0 consumer references the net10.0
         assembly, exactly as it already does for Qavren.Edge.Sqlite. -->
    <TargetFrameworks>net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst</TargetFrameworks>
    <!-- Restore evaluates EVERY entry of TargetFrameworks for every project in the graph, so the
         ios and maccatalyst SDK packs (which do not exist for linux-x64, NETSDK1178) have to be
         dropped on Linux hosts or the android device lane fails before a test can run. -->
    <TargetFrameworks Condition="$([MSBuild]::IsOSPlatform('Linux'))">net10.0;net10.0-android</TargetFrameworks>
    <IsPackable>true</IsPackable>
    <PackageId>Qavren.Edge.Onnx</PackageId>
    <Description>ONNX Runtime hosting for Qavren.Edge: OrtEnv bootstrap, a ref-counted session host, execution-provider policy, a no-backup model root, verified model provisioning, and a device resource monitor.</Description>
    <RootNamespace>Qavren.Edge.Onnx</RootNamespace>

    <!-- HIGHER than SP1's 21.0 / 15.0 / 15.0, and these are ORT's floors rather than ours.
         Android: ORT 1.30.0's AAR is built from default_full_aar_build_settings.json with
         android_min_sdk_version 24, so its manifest declares minSdkVersion 24 and .NET for
         Android merges library manifests into the app's - declaring 21 either fails the merge or
         raises the effective floor without saying so.
         Apple: ORT's iphoneos/iphonesimulator slices are built at --apple_deploy_target=15.1, so
         linking them into a 15.0 deployment target is a linker warning at best and a load failure
         on a 15.0 device at worst. Mac Catalyst's true floor is read from the xcframework's own
         LC_BUILD_VERSION in spec 19 item 2(c); 15.1 is the conservative value until then.
         SupportedOSPlatformVersion is a per-project property and plays NO part in restore - the
         plan's Environment ground truth proves the ORT assets resolve identically at 21.0/15.0
         and at 24.0/15.1 - so raising it here changes no SP1 csproj and adds no versioned TFM. -->
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'">24.0</SupportedOSPlatformVersion>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios'">15.1</SupportedOSPlatformVersion>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'maccatalyst'">15.1</SupportedOSPlatformVersion>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.ML.OnnxRuntime" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <ProjectReference Include="..\..\..\foundation\src\Qavren.Edge.Core\Qavren.Edge.Core.csproj" />
  </ItemGroup>

  <!-- Same Platforms\** + GetTargetPlatformIdentifier pattern Qavren.Edge.Maui already uses.
       iOS and Mac Catalyst share one implementation (NSUrl / NSFileManager / ProcessInfo). -->
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

- [ ] **Step 4: `Qavren.Edge.Embeddings.Onnx.csproj`**

`embeddings\src\Qavren.Edge.Embeddings.Onnx\Qavren.Edge.Embeddings.Onnx.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst</TargetFrameworks>
    <TargetFrameworks Condition="$([MSBuild]::IsOSPlatform('Linux'))">net10.0;net10.0-android</TargetFrameworks>
    <IsPackable>true</IsPackable>
    <PackageId>Qavren.Edge.Embeddings.Onnx</PackageId>
    <Description>On-device text embeddings for Qavren.Edge: a Microsoft.Extensions.AI IEmbeddingGenerator over ONNX Runtime, with WordPiece tokenization, masked pooling, L2 normalisation, batching, and pinned model presets.</Description>
    <RootNamespace>Qavren.Edge.Embeddings.Onnx</RootNamespace>
    <!-- Same ORT native floors as Qavren.Edge.Onnx; this package links the same AAR and
         xcframework transitively. -->
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'">24.0</SupportedOSPlatformVersion>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios'">15.1</SupportedOSPlatformVersion>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'maccatalyst'">15.1</SupportedOSPlatformVersion>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.AI" />
    <PackageReference Include="Microsoft.Extensions.AI.Abstractions" />
    <PackageReference Include="Microsoft.ML.Tokenizers" />
    <ProjectReference Include="..\Qavren.Edge.Onnx\Qavren.Edge.Onnx.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: `Qavren.Edge.VectorData.csproj`**

`embeddings\src\Qavren.Edge.VectorData\Qavren.Edge.VectorData.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- net10.0 alone, on purpose. This package touches no platform API - Microsoft.Data.Sqlite
         and MEVD are platform-neutral - and every platform TFM consumes a net10.0 library
         unchanged, exactly as Qavren.Edge.Sqlite already does. Multi-targeting would add four
         build legs for nothing. It also does NOT reference Qavren.Edge.Embeddings.Onnx: the
         generator arrives as the non-generic IEmbeddingGenerator through MEVD's own options, so
         the store works with Azure OpenAI embeddings and no ONNX at all (spec 2 decision 2). -->
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>true</IsPackable>
    <PackageId>Qavren.Edge.VectorData</PackageId>
    <Description>A Microsoft.Extensions.VectorData provider over sqlite-vec and FTS5 for Qavren.Edge, with true rowid pre-filtering and reciprocal-rank-fusion hybrid search.</Description>
    <RootNamespace>Qavren.Edge.VectorData</RootNamespace>
    <!-- Every type in Microsoft.Extensions.VectorData.ProviderServices is
         [Experimental("MEVD9001")], and a provider cannot be written without them. ADR 0007
         records the churn exposure. -->
    <NoWarn>$(NoWarn);MEVD9001</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.VectorData.Abstractions" />
    <ProjectReference Include="..\..\..\foundation\src\Qavren.Edge.Sqlite\Qavren.Edge.Sqlite.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: the three device-hosted test projects**

All three copy `Qavren.Edge.Sqlite.Tests`'s shape exactly, with one mandatory deviation: the platform floors follow §4.1's raised values, not SP1's `21.0`/`15.0`. These projects reference `Qavren.Edge.Onnx` and therefore link ORT's AAR and xcframework, so they inherit exactly the constraints the product packages do — a device lane at the wrong floor is testing a configuration no consumer can ship.

`embeddings\tests\Qavren.Edge.Onnx.Tests\Qavren.Edge.Onnx.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- net10.0 is the HOST lane: an MTP test application, run with `dotnet run -f net10.0`.
         The four platform TFMs exist ONLY so this same assembly can be loaded by
         Qavren.Edge.DeviceTests and executed on a device. The windows TFM is present even though
         Qavren.Edge.Onnx has none: Qavren.Edge.DeviceTests targets net10.0-windows10.0.19041.0
         and must be able to reference this library, and a net10.0-windows test assembly
         referencing a net10.0 product assembly is ordinary TFM compatibility. -->
    <TargetFrameworks>net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst;net10.0-windows10.0.19041.0</TargetFrameworks>
    <TargetFrameworks Condition="$([MSBuild]::IsOSPlatform('Linux'))">net10.0;net10.0-android</TargetFrameworks>
    <TargetFrameworks Condition="$([MSBuild]::IsOSPlatform('OSX'))">net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst</TargetFrameworks>
    <IsPackable>false</IsPackable>
    <!-- Spec 16.4: these follow SP2's raised floors, NOT Qavren.Edge.Sqlite.Tests's 21.0/15.0. -->
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'">24.0</SupportedOSPlatformVersion>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios'">15.1</SupportedOSPlatformVersion>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'maccatalyst'">15.1</SupportedOSPlatformVersion>
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
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
    <ProjectReference Include="..\..\src\Qavren.Edge.Onnx\Qavren.Edge.Onnx.csproj" />
  </ItemGroup>
</Project>
```

`embeddings\tests\Qavren.Edge.Embeddings.Tests\Qavren.Edge.Embeddings.Tests.csproj` is byte-for-byte the file above with the single `ProjectReference` replaced by:

```xml
    <ProjectReference Include="..\..\src\Qavren.Edge.Embeddings.Onnx\Qavren.Edge.Embeddings.Onnx.csproj" />
```

`embeddings\tests\Qavren.Edge.VectorData.Tests\Qavren.Edge.VectorData.Tests.csproj` is the same file with two changes — the `MEVD9001` suppression, and two project references in place of the one. **`Qavren.Edge.Embeddings.Onnx` is deliberately absent until Task 5.1**; adding it now would put Embeddings.Onnx in this project's build graph while Task 4.1 is writing it.

```xml
    <NoWarn>$(NoWarn);CA2007;MEVD9001</NoWarn>
```
```xml
    <ProjectReference Include="..\..\src\Qavren.Edge.VectorData\Qavren.Edge.VectorData.csproj" />
    <!-- vec0 and FTS5 arrive only with the native. Qavren.Edge.Sqlite.Native's QedgeHostRid item
         stages qedge_sqlite3.dll flat beside the test binary, so the host lane runs on a dev box
         with no package installed. Same arrangement Qavren.Edge.Sqlite.Tests already uses. -->
    <ProjectReference Include="..\..\..\foundation\src\Qavren.Edge.Sqlite.Native\Qavren.Edge.Sqlite.Native.csproj" />
```

- [ ] **Step 7: `Qavren.Edge.VectorData.Conformance.Tests.csproj`**

`embeddings\tests\Qavren.Edge.VectorData.Conformance.Tests\Qavren.Edge.VectorData.Conformance.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- net10.0 ALONE, and that is the authoritative spelling (spec 16.4). This project is
         host-only: it is never referenced from Qavren.Edge.DeviceTests and never runs on a device
         lane. The MEVD conformance suite needs a file-system database and a full xunit host, it
         asserts nothing platform-specific, and four extra TFMs would add four build legs to prove
         nothing. -->
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <NoWarn>$(NoWarn);MEVD9001</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <!-- ConformanceTests 10.10.0 brings xunit.v3.assert / .common / .extensibility.core and pins
         them at 3.2.2 - this repo's existing pin - but NOT the xunit.v3 metapackage, which is
         what generates the Microsoft.Testing.Platform entry point. Hence both references. -->
    <PackageReference Include="Microsoft.Extensions.VectorData.ConformanceTests" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
    <ProjectReference Include="..\..\src\Qavren.Edge.VectorData\Qavren.Edge.VectorData.csproj" />
    <ProjectReference Include="..\..\..\foundation\src\Qavren.Edge.Sqlite.Native\Qavren.Edge.Sqlite.Native.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 8: `Qavren.Edge.TrimSmoke.csproj`**

`embeddings\tools\Qavren.Edge.TrimSmoke\Qavren.Edge.TrimSmoke.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Under tools/, not tests/: it is published by the trim-smoke CI job rather than run as a
         test step, and assert-workflows.py's "every test project has an explicit ci.yml step"
         rule globs embeddings/tests/*/*.csproj. -->
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <!-- PublishTrimmed / PublishAot are set on the command line by the CI job, not here: the job
         publishes this project twice, and the AOT variant is continue-on-error for one release
         cycle (spec 17). -->
    <NoWarn>$(NoWarn);MEVD9001</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Qavren.Edge.VectorData\Qavren.Edge.VectorData.csproj" />
    <ProjectReference Include="..\..\src\Qavren.Edge.Embeddings.Onnx\Qavren.Edge.Embeddings.Onnx.csproj" />
    <ProjectReference Include="..\..\..\foundation\src\Qavren.Edge.Sqlite.Native\Qavren.Edge.Sqlite.Native.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 9: Add every project to `QavrenEdge.slnx`**

Insert three `<Folder>` elements before the closing `</Solution>`:

```xml
  <Folder Name="/embeddings/src/">
    <Project Path="embeddings/src/Qavren.Edge.Onnx/Qavren.Edge.Onnx.csproj" />
    <Project Path="embeddings/src/Qavren.Edge.Embeddings.Onnx/Qavren.Edge.Embeddings.Onnx.csproj" />
    <Project Path="embeddings/src/Qavren.Edge.VectorData/Qavren.Edge.VectorData.csproj" />
  </Folder>
  <Folder Name="/embeddings/tests/">
    <Project Path="embeddings/tests/Qavren.Edge.Onnx.Tests/Qavren.Edge.Onnx.Tests.csproj" />
    <Project Path="embeddings/tests/Qavren.Edge.Embeddings.Tests/Qavren.Edge.Embeddings.Tests.csproj" />
    <Project Path="embeddings/tests/Qavren.Edge.VectorData.Tests/Qavren.Edge.VectorData.Tests.csproj" />
    <Project Path="embeddings/tests/Qavren.Edge.VectorData.Conformance.Tests/Qavren.Edge.VectorData.Conformance.Tests.csproj" />
  </Folder>
  <Folder Name="/embeddings/tools/">
    <Project Path="embeddings/tools/Qavren.Edge.TrimSmoke/Qavren.Edge.TrimSmoke.csproj" />
  </Folder>
```

- [ ] **Step 10: Restore and assert ORT's asset resolution**

```powershell
dotnet restore "C:\Users\steve\projects\qavren-edge-sp2\QavrenEdge.slnx"
```

Then assert the four resolutions §4.1 depends on:

```powershell
$assets = "C:\Users\steve\projects\qavren-edge-sp2\embeddings\src\Qavren.Edge.Onnx\obj\project.assets.json"
$a = Get-Content $assets -Raw | ConvertFrom-Json
$expected = @{
  'net10.0'             = 'lib/net8.0/Microsoft.ML.OnnxRuntime.dll'
  'net10.0-android'     = 'lib/net9.0-android35.0/Microsoft.ML.OnnxRuntime.dll'
  'net10.0-ios'         = 'lib/net9.0-ios18.0/Microsoft.ML.OnnxRuntime.dll'
  'net10.0-maccatalyst' = 'lib/net9.0-maccatalyst18.0/Microsoft.ML.OnnxRuntime.dll'
}
foreach ($tfm in $expected.Keys) {
  $e = $a.targets.$tfm.'Microsoft.ML.OnnxRuntime.Managed/1.30.0'
  $got = ($e.compile.PSObject.Properties.Name) -join ','
  if ($got -ne $expected[$tfm]) { throw "$tfm resolved '$got', expected '$($expected[$tfm])'" }
  Write-Host "OK $tfm -> $got"
}
```

Expected: four `OK` lines. A `lib/netstandard2.0` anywhere is the failure §4.1 names — it silently drops `System.Numerics.Tensors` and changes the span hot path. The documented response is to pin `net10.0-ios18.0` / `net10.0-android35.0` / `net10.0-maccatalyst18.0` **on SP2's projects only**; no SP1 csproj moves either way.

- [ ] **Step 11: Verify**

```powershell
dotnet restore "C:\Users\steve\projects\qavren-edge-sp2\QavrenEdge.slnx"
```

Expected: `Restored` for every project including the eight new ones, exit code 0, and the Step 10 script prints four `OK` lines.

---

### Task 1.2: `embeddings` README, ADRs 0003–0008, and `foundation/docs/errors.md`

**Local-verifiable:** yes.

**Files:**
- Create: `embeddings\README.md`
- Create: `embeddings\docs\adr\0003-pin-onnxruntime-1-30-0.md`
- Create: `embeddings\docs\adr\0004-cut-nnapi.md`
- Create: `embeddings\docs\adr\0005-absorb-iedgemodelpaths-into-sp1-at-1-0.md`
- Create: `embeddings\docs\adr\0006-defer-multilingual-to-tokenizers-3x.md`
- Create: `embeddings\docs\adr\0007-mevd9001-experimental-surface.md`
- Create: `embeddings\docs\adr\0008-raised-platform-floors.md`
- Create: `foundation\docs\errors.md`

**Approach.** Docs only; this task compiles nothing, which is why it can run beside Task 1.4. The ADRs follow the shape of `foundation/docs/adr/0002-own-sqlite-build-and-provider.md` — `## Status`, `## Context`, `## Decision`, `## Consequences` — and continue SP1's numbering so a cross-reference is never ambiguous.

- [ ] **Step 1: `foundation\docs\errors.md`**

`EdgeException` has always set `HelpLink` to `…/foundation/docs/errors.md#<numeric code>`, and the file does not exist in the merged tree. Create it with an anchor per code: SP1's `1001`–`4001` transcribed from `foundation/src/Qavren.Edge.Core/EdgeErrorCode.cs`, and SP2's `5001`–`5213` from spec §15.1. Each entry gets a one-line meaning and a one-line remediation. The heading for each is the bare number (`## 5005`) so the existing `HelpLink` anchors resolve.

- [ ] **Step 2: ADR 0003 — ORT 1.30.0 pin**

Context: 1.30.0 was one day old at design time. The four device lanes are the soak. The **only** rollback on NuGet is 1.29.0 — upstream's 1.29.1 was never published there, so there is no ladder between them. Consequence: a regression means a two-minor step back, and the pin is re-evaluated at 1.0.

- [ ] **Step 3: ADR 0004 — NNAPI cut**

The decisive reason is structural, not taste, and the ADR must say so: ORT documents the portable `AppendExecutionProvider(string, Dictionary<string,string>)` overload as accepting `"QNN"`, `"SNPE"`, `"XNNPACK"`, `"CoreML"` and `"AZURE"` — NNAPI is not among them. Reaching it needs `AppendExecutionProvider_Nnapi`, compiled inside `#if __ANDROID__` and throwing `NotSupportedException` everywhere else, which would destroy the "one EP implementation compiled for every TFM" property §9.2 is built on. Secondarily, Google deprecated NNAPI in Android 15 and expects most devices to fall back to CPU. Revisit trigger: ORT exposing NNAPI through the portable overload, or shipping a supported Android successor in the .NET package.

- [ ] **Step 4: ADR 0005 — `IEdgeModelPaths` absorbed into SP1 at 1.0**

It is a new interface in `Qavren.Edge.Onnx` because §5 forbids touching `IEdgePaths`. At 1.0 it moves to `Qavren.Edge.Core` with a `[TypeForwardedTo]` from `Qavren.Edge.Onnx`, so no consumer recompiles.

- [ ] **Step 5: ADR 0006 — multilingual deferred**

`Microsoft.ML.Tokenizers` 2.0.0 has **no** `CreateFromTokenizerJson` (verified: the symbol does not occur in the shipped assembly), and `SentencePieceTokenizer.Create` emits raw SentencePiece piece indices while every XLM-R/e5 graph expects the fairseq layout (`<s>`=0, `<pad>`=1, `</s>`=2, `<unk>`=3, `<mask>`=250001). The failure is silent — plausible, wrong embeddings. `EdgeTokenizerKind.UnigramTokenizerJson` exists in the enum and throws `TokenizerKindUnsupported` naming the 3.x requirement, so the shape is ready when the library is.

- [ ] **Step 6: ADR 0007 — MEVD9001 experimental surface**

Every type in `Microsoft.Extensions.VectorData.ProviderServices` is `[Experimental("MEVD9001")]` and a provider cannot be written without them. `Qavren.Edge.VectorData.csproj` carries `<NoWarn>$(NoWarn);MEVD9001</NoWarn>`. Consequence: a minor MEVD bump can break this package's compile, and the conformance suite is the detector.

- [ ] **Step 7: ADR 0008 — raised platform floors**

`android 24.0` and Apple `15.1` on SP2's projects against SP1's `21.0`/`15.0`, sourced from ORT's own `default_full_aar_build_settings.json` (`android_min_sdk_version: 24`) and `default_full_apple_framework_build_settings.json` (`--apple_deploy_target=15.1`) rather than chosen. Evidence still owed: the `LC_BUILD_VERSION` read from the xcframework's `ios-arm64_x86_64-maccatalyst` slice (§19 item 2c), which is the one number the build-settings file does not establish. Evidence already in hand: restore resolves ORT's managed assets identically at `21.0`/`15.0` and at `24.0`/`15.1`, so the raise is free in restore terms and its whole effect is on the manifest merge and the Apple deployment target. Consequence: the README states `android 24 / iOS 15.1` as the supported minimum.

- [ ] **Step 8: `embeddings\README.md`**

Must state, and nothing weaker:

- The four-call happy path from §4.3, verbatim.
- Supported minimums: **Android 24, iOS 15.1, Mac Catalyst 15.1** — ORT's floors, not ours.
- **Packaging:** bundle `MiniLmL6V2Int8` (23 MB) if you bundle anything; download everything larger. Play AAB base module 500 MB, legacy APK 100 MB (`BgeSmallEnV15` at 133 MB does **not** fit), iOS cellular-install prompt around 200 MB, and the one ceiling with no workaround — Apple's **80 MB** executable `__TEXT` cap, against which ORT's force-loaded static slice counts. Trim `$(AndroidSupportedAbis)` on the APK path; leave the AAB path alone. **No size number of our own until §19 item 3 is measured** — state the ceilings and the rule, not an estimate.
- **Mac Catalyst is claimed x64-only:** `ci.yml` runs `device-tests-maccatalyst` and `device-tests-ios` on `macos-15-intel` with `-r maccatalyst-x64` / `-r iossimulator-x64`, so a green lane proves the x64 RID-graph resolution and the x86_64 slices and says nothing about `maccatalyst-arm64` or any real device. This is §19 item 5, option (a) plus (c): the arm64 claim waits for a manual Mac Mini run recorded in the vault.
- **`macOS x64` is unsupported:** ORT 1.30.0 ships no `osx-x64` native (verified: `runtimes/` holds `osx-arm64` only), and `Qavren.Edge.Onnx` reports it as `OnnxUnsupportedRuntime` at startup rather than as a `DllNotFoundException` on first embed.
- **Score polarity, stated twice:** `SearchAsync` returns a vec0 **distance** — lower is better. `HybridSearchAsync` returns an **RRF score** — higher is better. MEVD has no way to declare direction.
- **Semantic Kernel caveat:** SK's `AsTextEmbeddingGenerationService` populates only `EndpointKey` and `ModelIdKey` from `EmbeddingGeneratorMetadata` and never `DimensionsKey`, so `GetDimensions()` returns null for a Qavren generator no matter what `DefaultModelDimensions` says. Upstream behaviour; recorded here so it is not filed against us.
- **No `CompactAsync` in v1:** vec0 reclaims space only when a whole chunk empties (~256 rows at the default), so a heavily churned collection grows; drop and rebuild is the remedy.

- [ ] **Step 9: Verify**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp2"
$required = @(
  "$root\embeddings\README.md",
  "$root\foundation\docs\errors.md",
  "$root\embeddings\docs\adr\0003-pin-onnxruntime-1-30-0.md",
  "$root\embeddings\docs\adr\0004-cut-nnapi.md",
  "$root\embeddings\docs\adr\0005-absorb-iedgemodelpaths-into-sp1-at-1-0.md",
  "$root\embeddings\docs\adr\0006-defer-multilingual-to-tokenizers-3x.md",
  "$root\embeddings\docs\adr\0007-mevd9001-experimental-surface.md",
  "$root\embeddings\docs\adr\0008-raised-platform-floors.md")
$missing = $required | Where-Object { -not (Test-Path $_) }
if ($missing) { throw "missing: $($missing -join ', ')" }
$err = Get-Content "$root\foundation\docs\errors.md" -Raw
$checked = 0
foreach ($code in 1001,1002,1003,1004,1005,2001,2002,3001,3002,4001,
                  5001,5002,5003,5004,5005,5006,5007,5051,5052,5053,5054,5055,5056,
                  5101,5102,5103,5104,5105,
                  5201,5202,5203,5204,5205,5206,5207,5208,5209,5210,5211,5212,5213) {
  if ($err -notmatch "(?m)^## $code\s*$") { throw "errors.md has no anchor for $code" }
  $checked++
}
if ($checked -ne 41) { throw "the loop checked $checked anchors, not 41" }
Write-Host "OK: 8 docs present, $checked error anchors"
```

The loop enumerates **41** codes, not 43: ten SP1 codes (1001–1005, 2001, 2002, 3001, 3002, 4001) plus 7 + 6 + 5 + 13 SP2 codes. Declare `$checked = 0` immediately before the `foreach`. The self-count is there because a hand-written total in a success message is exactly the kind of thing that drifts silently — an earlier draft of this plan printed `43` over a 41-entry list, and an implementer reading only the message would have believed two codes were covered that never were.

Expected: `OK: 8 docs present, 41 error anchors`, exit code 0.

---

### Task 1.3: Fixture toolchain — pinned `uv` venv, generator, and the committed `TinyModels.g.cs`

**Local-verifiable:** yes. Every byte below was produced and executed on this box on 2026-09-11.

**Files:**
- Create: `embeddings\tests\fixtures\make_tiny_model.py`
- Create: `embeddings\tests\fixtures\requirements.txt`
- Create: `embeddings\tests\fixtures\README.md`
- Create: `embeddings\tests\fixtures\TinyModels.g.cs` *(generated, committed)*

**Approach — read this before writing.** §16.1 is explicit that tier 1 "downloads nothing, ships no binary into git". The generator therefore writes a **C# file of base64 `const string`s**, and that file is what git carries; any `.onnx` written for debugging goes to the gitignored `_scratch/`. The script is documentation and a regeneration path — **CI never runs it**, and it is never a build step.

Three facts were measured rather than assumed and the script encodes all three. `onnx` 1.22.0's `IR_VERSION` is **13**, which is exactly ORT 1.30.0's ceiling, so `model.ir_version = 10` is set explicitly and the IR-14 fixture must **skip** `onnx.checker.check_model` — the checker refuses an `ir_version` above its own with `ValidationError: Your model ir_version 14 is higher than the checker's (13)`. `model.producer_name = ""` keeps the bytes stable across onnx upgrades. And `ReduceSum` and `Unsqueeze` both take axes as a second **input** from opset 13, while `ReduceMean` takes axes as an **attribute** through opset 17 — both conventions can appear in one graph at opset 17, which is why the script never relies on a default.

**Six fixtures, not five (plan adjustment 3).** `HiddenStates` is the one `OnnxEmbeddingGenerator` consumes: a single `Gather`, output `last_hidden_state[batch_size, sequence_length, dims]` — the shape every real preset has, and the only shape that puts `EmbeddingPooler` in the path. The spec's in-graph `MeanPool` fixture is kept because it proves the mask / `Cast` / `Unsqueeze` / `ReduceSum` tensor plumbing against real ORT with none of our C# involved.

- [ ] **Step 1: Write `embeddings\tests\fixtures\requirements.txt`**

```
# Pinned for reproducibility. onnxruntime matches the .NET pin exactly so a fixture that loads
# here loads there. All three have cp314-compatible wheels for win-x64 (verified 2026-09-11).
#
# numpy is pinned for the same reason as the other two, and it is NOT incidental: numpy is what
# serialises the Gather embedding-table initializer whose exact byte count Step 6 asserts. An
# unpinned numpy is an unpinned fixture size, and the drift check would fail for a reason that
# has nothing to do with the fixtures.
onnx==1.22.0
onnxruntime==1.30.0
numpy==2.5.3
```

- [ ] **Step 2: Write `embeddings\tests\fixtures\README.md`**

State the three-line regeneration path, that the script is never a build step, that `TinyModels.g.cs` is committed and `.onnx` files are not, and the measured byte counts from Step 5 so drift is visible in a diff.

- [ ] **Step 3: Write `embeddings\tests\fixtures\make_tiny_model.py`**

This is the exact script that produced the committed file. It is reproduced verbatim, not paraphrased.

```python
#!/usr/bin/env python3
"""Regenerate the tier-1 ONNX test fixtures for Qavren.Edge.Embeddings.

NEVER a build step. Run by hand, then commit the emitted TinyModels.g.cs.

    uv venv --python 3.14 .venv
    uv pip install --python .venv/Scripts/python.exe -r requirements.txt
    .venv/Scripts/python.exe make_tiny_model.py --out TinyModels.g.cs

Two pins are non-negotiable:
  * model.ir_version = 10 -- onnx 1.22's IR_VERSION is 13, which is exactly ORT 1.30.0's
    ceiling; the first onnx release that raises it would silently break every fixture that
    trusted the default.
  * model.producer_name = "" -- keeps the bytes stable across onnx upgrades.

Two per-operator traps this script encodes:
  * ReduceSum takes axes as a second INPUT from opset 13.
  * Unsqueeze takes axes as a second INPUT from opset 13.
  (ReduceMean, by contrast, takes axes as an ATTRIBUTE through opset 17 and as an input from
  18 -- both conventions live at opset 17, so nothing here relies on a default.)
"""

from __future__ import annotations

import argparse
import base64
import pathlib

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper

OPSET = 17
IR_VERSION = 10
VOCAB = 16
DIM = 4


def embedding_table() -> np.ndarray:
    """Row t = [t, t+0.5, t+0.25, t+0.75]. Hand-computable on purpose."""
    rows = [[float(t), t + 0.5, t + 0.25, t + 0.75] for t in range(VOCAB)]
    return np.array(rows, dtype=np.float32)


def _seq_input(name: str):
    return helper.make_tensor_value_info(
        name, TensorProto.INT64, ["batch_size", "sequence_length"])


def _hidden_nodes():
    return [helper.make_node(
        "Gather", ["emb", "input_ids"], ["last_hidden_state"], axis=0)], []


def _mean_nodes():
    axes1 = numpy_helper.from_array(np.array([1], dtype=np.int64), "axes1")
    axes2 = numpy_helper.from_array(np.array([2], dtype=np.int64), "axes2")
    nodes = [
        helper.make_node("Gather", ["emb", "input_ids"], ["h"], axis=0),
        helper.make_node("Cast", ["attention_mask"], ["maskf"], to=TensorProto.FLOAT),
        helper.make_node("Unsqueeze", ["maskf", "axes2"], ["mask3"]),
        helper.make_node("Mul", ["h", "mask3"], ["masked"]),
        helper.make_node("ReduceSum", ["masked", "axes1"], ["total"], keepdims=0),
        helper.make_node("ReduceSum", ["mask3", "axes1"], ["denom"], keepdims=0),
        helper.make_node("Div", ["total", "denom"], ["embedding"]),
    ]
    return nodes, [axes1, axes2]


def _cls_nodes():
    zero = numpy_helper.from_array(np.array(0, dtype=np.int64), "zero")
    nodes = [
        helper.make_node("Gather", ["emb", "input_ids"], ["h"], axis=0),
        helper.make_node("Gather", ["h", "zero"], ["embedding"], axis=1),
    ]
    return nodes, [zero]


_BUILDERS = {"hidden": _hidden_nodes, "mean": _mean_nodes, "cls": _cls_nodes}


def build(kind: str, inputs: list[str], ir_version: int = IR_VERSION) -> bytes:
    emb = numpy_helper.from_array(embedding_table(), "emb")
    nodes, extra = _BUILDERS[kind]()
    output = (
        helper.make_tensor_value_info(
            "last_hidden_state", TensorProto.FLOAT,
            ["batch_size", "sequence_length", "dims"])
        if kind == "hidden"
        else helper.make_tensor_value_info(
            "embedding", TensorProto.FLOAT, ["batch_size", "dims"])
    )
    graph = helper.make_graph(
        nodes, "tiny", [_seq_input(n) for n in inputs], [output], [emb, *extra])
    model = helper.make_model(
        graph, opset_imports=[helper.make_operatorsetid("", OPSET)])
    model.ir_version = ir_version
    model.producer_name = ""
    if ir_version <= onnx.IR_VERSION:
        # onnx 1.22.0's checker refuses an ir_version above its own (13). The IR-14 fixture
        # exists precisely to be refused by ORT, so it skips the checker rather than letting
        # the checker decide it cannot be built.
        onnx.checker.check_model(model)
    return model.SerializeToString()


VOCAB_TOKENS = (
    ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]"]
    + [chr(c) for c in range(ord("a"), ord("z") + 1)]
    + [str(d) for d in range(10)]
    + ["roof", "leak", "water", "damage", "note", "search", "query", "document",
       "the", "a", "of", "and", "to", "in", "is", "it", "##s", "##ing", "##ed",
       "##er", "##ly", "##tion", "edge"]
)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    fixtures = {
        "HiddenStates": build("hidden", ["input_ids", "attention_mask"]),
        "MeanPool": build("mean", ["input_ids", "attention_mask"]),
        "ClsPool": build("cls", ["input_ids", "attention_mask"]),
        "UnusedTokenTypeIds": build(
            "hidden", ["input_ids", "attention_mask", "token_type_ids"]),
        "NomicInputOrder": build(
            "hidden", ["input_ids", "token_type_ids", "attention_mask"]),
        "IrVersion14": build("hidden", ["input_ids", "attention_mask"], ir_version=14),
    }
    vocab = ("\n".join(VOCAB_TOKENS) + "\n").encode("utf-8")

    lines = [
        "// <auto-generated>",
        "// Regenerate with embeddings/tests/fixtures/make_tiny_model.py. NEVER a build step.",
        "// </auto-generated>",
        "",
        "namespace Qavren.Edge.Tests.Fixtures;",
        "",
        "/// <summary>Tier-1 ONNX fixtures, base64 so git holds no binary.</summary>",
        "internal static class TinyModels",
        "{",
    ]
    for name, payload in fixtures.items():
        lines.append(f"    /// <summary>{len(payload)} bytes.</summary>")
        lines.append(f"    public const string {name} =")
        lines.append(f'        "{base64.b64encode(payload).decode()}";')
        lines.append("")
    lines.append(
        f"    /// <summary>{len(VOCAB_TOKENS)}-entry vocab.txt, {len(vocab)} bytes.</summary>")
    lines.append("    public const string VocabTxt =")
    lines.append(f'        "{base64.b64encode(vocab).decode()}";')
    lines.append("}")

    out = pathlib.Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text("\n".join(lines) + "\n", encoding="utf-8")
    for name, payload in fixtures.items():
        print(f"{name}: {len(payload)} bytes, "
              f"{len(base64.b64encode(payload))} base64 chars")
    print(f"vocab.txt: {len(vocab)} bytes, {len(base64.b64encode(vocab))} base64 chars")


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: Create the pinned venv and generate**

```powershell
$uv = "C:\Users\steve\.local\bin\uv.exe"
$f  = "C:\Users\steve\projects\qavren-edge-sp2\embeddings\tests\fixtures"
& $uv venv --python 3.14 "$f\.venv"
& $uv pip install --python "$f\.venv\Scripts\python.exe" -r "$f\requirements.txt"
& "$f\.venv\Scripts\python.exe" "$f\make_tiny_model.py" --out "$f\TinyModels.g.cs"
```

`uv` is spelled **absolutely**, for the same reason `python` is everywhere in this plan: it is installed at `C:\Users\steve\.local\bin\uv.exe` (Environment ground truth) and that directory is on the interactive `PATH` but is not guaranteed on an implementer's — a bare `uv` that resolves to nothing fails on the first line of this step and takes the whole fixture with it.

Measured on this box (uv 0.12.1, CPython 3.14.5): the venv resolves `onnx 1.22.0`, `onnxruntime 1.30.0`, `numpy 2.5.3`, `protobuf 7.36.1`, `flatbuffers 25.12.19`, `ml-dtypes 0.6.0`, `packaging 26.3`, `typing-extensions 4.16.0`. The ambient `C:\Python314` interpreter already carries onnx 1.22.0 but **onnxruntime 1.27.0**, which is the wrong runtime — the venv exists for exactly that reason and the script must never be run against the ambient interpreter.

Expected output, byte-for-byte:

```
HiddenStates: 533 bytes, 712 base64 chars
MeanPool: 772 bytes, 1032 base64 chars
ClsPool: 553 bytes, 740 base64 chars
UnusedTokenTypeIds: 592 bytes, 792 base64 chars
NomicInputOrder: 592 bytes, 792 base64 chars
IrVersion14: 533 bytes, 712 base64 chars
vocab.txt: 215 bytes, 288 base64 chars
```

- [ ] **Step 5: Prove the fixtures against real ONNX Runtime before committing them**

Write `_scratch\check.py` (gitignored) and run it in the same venv:

```python
import base64, re
import numpy as np, onnxruntime as ort

src = open("../TinyModels.g.cs", encoding="utf-8").read()
c = dict(re.findall(r'public const string (\w+) =\s*\n\s*"([^"]+)";', src))
ids = np.array([[3, 1, 9]], dtype=np.int64)
mask = np.array([[1, 1, 0]], dtype=np.int64)
tti = np.zeros_like(ids)
for n in ("HiddenStates", "MeanPool", "ClsPool", "UnusedTokenTypeIds", "NomicInputOrder"):
    s = ort.InferenceSession(base64.b64decode(c[n]), providers=["CPUExecutionProvider"])
    names = [i.name for i in s.get_inputs()]
    feed = {"input_ids": ids, "attention_mask": mask}
    if "token_type_ids" in names:
        feed["token_type_ids"] = tti
    o = s.run(None, feed)[0]
    print(n, names, [x.name for x in s.get_outputs()], o.shape, o.tolist())
try:
    ort.InferenceSession(base64.b64decode(c["IrVersion14"]), providers=["CPUExecutionProvider"])
    print("IrVersion14 LOADED (unexpected)")
except Exception as e:
    print("IrVersion14 rejected:", str(e).splitlines()[0])
```

Measured output on this box, and these are the exact values the C# tests in Tasks 3.1 and 4.1 assert:

```
HiddenStates ['input_ids', 'attention_mask'] ['last_hidden_state'] (1, 3, 4)
    [[[3.0, 3.5, 3.25, 3.75], [1.0, 1.5, 1.25, 1.75], [9.0, 9.5, 9.25, 9.75]]]
MeanPool     ['input_ids', 'attention_mask'] ['embedding'] (1, 4) [[2.0, 2.5, 2.25, 2.75]]
ClsPool      ['input_ids', 'attention_mask'] ['embedding'] (1, 4) [[3.0, 3.5, 3.25, 3.75]]
UnusedTokenTypeIds ['input_ids', 'attention_mask', 'token_type_ids'] ['last_hidden_state'] (1, 3, 4)
    [[[3.0, 3.5, 3.25, 3.75], [1.0, 1.5, 1.25, 1.75], [9.0, 9.5, 9.25, 9.75]]]
NomicInputOrder    ['input_ids', 'token_type_ids', 'attention_mask'] ['last_hidden_state'] (1, 3, 4)
    [[[3.0, 3.5, 3.25, 3.75], [1.0, 1.5, 1.25, 1.75], [9.0, 9.5, 9.25, 9.75]]]
IrVersion14 rejected: [ONNXRuntimeError] : 1 : FAIL : ... Unsupported model IR version: 14,
    max supported IR version: 13
```

Read what each line buys. `MeanPool` returns exactly the mean of rows 3 and 1 with row 9 masked out — one assertion covering the mask plumbing, the `Cast`/`Unsqueeze` broadcast and the tensor layout, with no Qavren code in the path. `ClsPool` returns row 3 — the `[CLS]` position. `HiddenStates` returns all three rows unpooled, so `EmbeddingPooler.MeanPool` over it must produce `[2.0, 2.5, 2.25, 2.75]` and `ClsPool` over it must produce `[3.0, 3.5, 3.25, 3.75]`; those two are the end-to-end generator assertions. `NomicInputOrder` declares its inputs in nomic's order and still returns the same values **only if** the generator binds by name — a positional binding would feed the mask as token-type ids and the values would change. `UnusedTokenTypeIds` declares an input the graph never consumes, and ORT still demands it in the feed, which is what proves the generator feeds every **declared** input rather than every *used* one. `IrVersion14` is the error path, and its message is the one a user hits with somebody else's model.

- [ ] **Step 6: Verify**

```powershell
$f = "C:\Users\steve\projects\qavren-edge-sp2\embeddings\tests\fixtures"
& "$f\.venv\Scripts\python.exe" "$f\make_tiny_model.py" --out "$f\TinyModels.g.cs"
$src = Get-Content "$f\TinyModels.g.cs" -Raw
# The size regex is ANCHORED TO THE CONST NAME on purpose. HiddenStates and IrVersion14 are both
# 533 bytes, so an unanchored "<summary>533 bytes" search is satisfied by either one - and a drift
# in exactly one of them would pass. The generator emits the doc comment on the line immediately
# above the const, which is what makes the anchored form possible.
foreach ($pair in @(@('HiddenStates',533), @('MeanPool',772), @('ClsPool',553),
                    @('UnusedTokenTypeIds',592), @('NomicInputOrder',592),
                    @('IrVersion14',533), @('VocabTxt',215))) {
  $rx = "(?m)^[ \t]*///[ \t]*<summary>(?:\d+-entry vocab\.txt, )?$($pair[1]) bytes\.</summary>\r?\n" +
        "[ \t]*public const string $($pair[0]) =[ \t]*$"
  if ($src -notmatch $rx) {
    throw "$($pair[0]) is no longer $($pair[1]) bytes - the fixtures drifted"
  }
}
Write-Host 'OK: 7 consts, sizes unchanged'
```

Expected: the generator prints the seven-line size report from Step 4 and the script prints `OK: 7 consts, sizes unchanged`, exit code 0.

---

### Task 1.4: SP1 edit 1 of 2 — `EdgeErrorCode` gains 5000–5299

**Local-verifiable:** yes.

**Files:**
- Edit: `foundation\src\Qavren.Edge.Core\EdgeErrorCode.cs`
- Test: `foundation\tests\Qavren.Edge.Core.Tests\ErrorCodeRangeTests.cs`

**Approach.** This is one of the two additive SP1 library edits §5 permits, and the **only** wave-1 task that compiles anything — which is why Task 1.1 verifies with restore rather than build. Values 1001–4001 are untouched, so no existing `HelpLink` moves. `EdgeException`'s `HelpLink` convention applies to the new codes for free.

- [ ] **Step 1: Write the failing test**

`foundation\tests\Qavren.Edge.Core.Tests\ErrorCodeRangeTests.cs`:

```csharp
using Xunit;

namespace Qavren.Edge.Core.Tests;

public class ErrorCodeRangeTests
{
    [Fact]
    public void Sp1CodesAreUnchanged()
    {
        Assert.Equal(1001, (int)EdgeErrorCode.DuplicateDatabaseName);
        Assert.Equal(1002, (int)EdgeErrorCode.NoNativeProviderRegistered);
        Assert.Equal(1003, (int)EdgeErrorCode.MultipleNativeProvidersRegistered);
        Assert.Equal(1004, (int)EdgeErrorCode.EncryptionKeyWithoutCipherProvider);
        Assert.Equal(1005, (int)EdgeErrorCode.EncryptionKeyMissing);
        Assert.Equal(2001, (int)EdgeErrorCode.NativeLoadFailed);
        Assert.Equal(2002, (int)EdgeErrorCode.NativeVerificationFailed);
        Assert.Equal(3001, (int)EdgeErrorCode.MigrationFailed);
        Assert.Equal(3002, (int)EdgeErrorCode.MigrationVersionConflict);
        Assert.Equal(4001, (int)EdgeErrorCode.DatabaseKeyRejected);
    }

    [Theory]
    // Qavren.Edge.Onnx - runtime and sessions
    [InlineData(EdgeErrorCode.OnnxEnvironmentAlreadyCreated, 5001)]
    [InlineData(EdgeErrorCode.OnnxSessionCreationFailed, 5002)]
    [InlineData(EdgeErrorCode.OnnxModelSignatureMismatch, 5003)]
    [InlineData(EdgeErrorCode.OnnxExecutionProviderRequired, 5004)]
    [InlineData(EdgeErrorCode.OnnxInsufficientMemory, 5005)]
    [InlineData(EdgeErrorCode.OnnxUnsupportedRuntime, 5006)]
    [InlineData(EdgeErrorCode.OnnxStaticShapesUnpinned, 5007)]
    // Qavren.Edge.Onnx - model provisioning
    [InlineData(EdgeErrorCode.ModelNotRegistered, 5051)]
    [InlineData(EdgeErrorCode.ModelNotProvisioned, 5052)]
    [InlineData(EdgeErrorCode.ModelDownloadFailed, 5053)]
    [InlineData(EdgeErrorCode.ModelHashMismatch, 5054)]
    [InlineData(EdgeErrorCode.ModelAssetMissing, 5055)]
    [InlineData(EdgeErrorCode.ModelInsufficientDiskSpace, 5056)]
    // Qavren.Edge.Embeddings.Onnx
    [InlineData(EdgeErrorCode.TokenizerAssetMissing, 5101)]
    [InlineData(EdgeErrorCode.TokenizerKindUnsupported, 5102)]
    [InlineData(EdgeErrorCode.EmbeddingDimensionMismatch, 5103)]
    [InlineData(EdgeErrorCode.EmbeddingPresetNotFound, 5104)]
    [InlineData(EdgeErrorCode.EmbeddingInputTooLong, 5105)]
    // Qavren.Edge.VectorData
    [InlineData(EdgeErrorCode.VectorCollectionNotFound, 5201)]
    [InlineData(EdgeErrorCode.UnsupportedKeyType, 5202)]
    [InlineData(EdgeErrorCode.UnsupportedPropertyType, 5203)]
    [InlineData(EdgeErrorCode.UnsupportedDistanceFunction, 5204)]
    [InlineData(EdgeErrorCode.VectorDimensionMismatch, 5205)]
    [InlineData(EdgeErrorCode.FullTextPropertyMissing, 5206)]
    [InlineData(EdgeErrorCode.EmbeddingGeneratorMissing, 5207)]
    [InlineData(EdgeErrorCode.MultipleVectorPropertiesUnsupported, 5208)]
    [InlineData(EdgeErrorCode.NullableVectorProperty, 5209)]
    [InlineData(EdgeErrorCode.KnnLimitExceeded, 5210)]
    [InlineData(EdgeErrorCode.SqliteVersionTooOld, 5211)]
    [InlineData(EdgeErrorCode.VectorStoreOperationFailed, 5212)]
    [InlineData(EdgeErrorCode.ReservedColumnName, 5213)]
    public void Sp2CodeHasItsSpecifiedValue(EdgeErrorCode code, int expected)
        => Assert.Equal(expected, (int)code);

    [Fact]
    public void EveryValueIsDistinct()
    {
        var values = Enum.GetValues<EdgeErrorCode>().Select(v => (int)v).ToArray();
        Assert.Equal(values.Length, values.Distinct().Count());
    }
}
```

- [ ] **Step 2: Append the range to `EdgeErrorCode.cs`**

Insert immediately before the closing brace, leaving every existing member untouched:

```csharp

    // ---- Sub-project 2: 5000-5299. SP1's 1001-4001 above are untouched. ----

    // Qavren.Edge.Onnx - runtime and sessions
    /// <summary>Another library created the ORT environment first. Logged, never thrown.</summary>
    OnnxEnvironmentAlreadyCreated = 5001,
    OnnxSessionCreationFailed = 5002,
    OnnxModelSignatureMismatch = 5003,
    OnnxExecutionProviderRequired = 5004,
    OnnxInsufficientMemory = 5005,
    /// <summary>osx-x64 and any RID with no ORT native.</summary>
    OnnxUnsupportedRuntime = 5006,
    /// <summary>RequireStaticInputShapes set with no FreeDimensionOverrides.</summary>
    OnnxStaticShapesUnpinned = 5007,

    // Qavren.Edge.Onnx - model provisioning
    ModelNotRegistered = 5051,
    ModelNotProvisioned = 5052,
    ModelDownloadFailed = 5053,
    ModelHashMismatch = 5054,
    ModelAssetMissing = 5055,
    ModelInsufficientDiskSpace = 5056,

    // Qavren.Edge.Embeddings.Onnx
    TokenizerAssetMissing = 5101,
    TokenizerKindUnsupported = 5102,
    EmbeddingDimensionMismatch = 5103,
    EmbeddingPresetNotFound = 5104,
    EmbeddingInputTooLong = 5105,

    // Qavren.Edge.VectorData
    VectorCollectionNotFound = 5201,
    UnsupportedKeyType = 5202,
    UnsupportedPropertyType = 5203,
    UnsupportedDistanceFunction = 5204,
    VectorDimensionMismatch = 5205,
    FullTextPropertyMissing = 5206,
    EmbeddingGeneratorMissing = 5207,
    MultipleVectorPropertiesUnsupported = 5208,
    NullableVectorProperty = 5209,
    KnnLimitExceeded = 5210,
    SqliteVersionTooOld = 5211,
    VectorStoreOperationFailed = 5212,
    /// <summary>A property claims the reserved "_rowid" storage name.</summary>
    ReservedColumnName = 5213,
```

- [ ] **Step 3: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp2\foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0 `
  -p:ArtifactsPath="D:\Local\Temp\qedge-sp2\w1\t14"
```

Expected: `Passed!` with the pre-existing Core tests plus 35 new ones (1 + 33 theory cases + 1), exit code 0.

---

### Task 1.5: Wave 1 close — sequential re-verify and commit *(integrator)*

**Local-verifiable:** yes.

**Files:** none of its own. It reads what wave 1 wrote and commits it.

**Approach.** The parallel phase ran every verify under `-p:ArtifactsPath=D:\Local\Temp\qedge-sp2\w1\t…`, so no two tasks contended for one `obj/` or `bin/`. That isolation is exactly why the wave is **not** yet proved: nothing has built these projects in the tree the next wave, and CI, will use. This task deletes the scratch roots, re-runs every verify **sequentially and un-isolated**, and only then commits. Wave 1 has the extra property that Task 1.1 ran **alone** as a prologue; this task confirms that by restoring the whole solution after Task 1.4's edit, which is the combination the prologue could not test.

- [ ] **Step 1: Clear the wave's isolated outputs**

```powershell
Remove-Item -Recurse -Force "D:\Local\Temp\qedge-sp2\w1" -ErrorAction SilentlyContinue
```

- [ ] **Step 2: Re-run every verify in the wave, sequentially, in the real tree**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp2"
foreach ($p in @(
  "foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj")) {
  dotnet run --project "$root\$p" -c Release -f net10.0 -p:TargetFrameworks=net10.0
  if ($LASTEXITCODE -ne 0) { throw "FAILED: $p" }
}
dotnet restore "$root\QavrenEdge.slnx"
if ($LASTEXITCODE -ne 0) { throw "FAILED: solution restore" }
Write-Host 'OK: wave 1 re-verified sequentially'
```

Expected: `OK: wave 1 re-verified sequentially`, exit code 0. A failure here that did not appear in the parallel phase is real — the usual cause is a project that only restored because a sibling task had already written its assets file.

- [ ] **Step 3: Commit the wave**

One commit, Conventional Commits, no `Co-Authored-By` and no AI attribution, on `feat/sp2-embeddings`. Never on `main`.

```
feat(sp2): skeleton, CPM pins, docs, fixtures and the 5000-5299 error range

- embeddings/ projects, .slnx entries and six PackageVersion pins
- ADRs 0003-0008, embeddings/README.md, foundation/docs/errors.md
- pinned uv venv + make_tiny_model.py + the committed TinyModels.g.cs
- EdgeErrorCode gains 5000-5299 (SP1 edit 1 of 2, additive)
```

- [ ] **Step 4: Verify**

Step 2 printed its `OK` line and `git status --short` is clean. Wave 2 may start.

---

## WAVE 2 — SP1 edit 2 of 2, ONNX runtime policy, CI

### Task 2.1: SP1 edit 2 of 2 — `FtsTable` gains an options overload

**Local-verifiable:** yes.

**Files:**
- Edit: `foundation\src\Qavren.Edge.Sqlite\Fts\FtsTable.cs`
- Test: `foundation\tests\Qavren.Edge.Sqlite.Tests\FtsTableOptionsTests.cs`

**Approach — read this before writing.** SP2 needs two things the merged helper cannot express: `content_rowid` (its FTS5 sidecar keys on `"_rowid"`, not the implicit rowid alias name) and `remove_diacritics 2` (unicode61's default of 1 has a documented multi-diacritic bug). Rather than emit a second FTS5 DDL generator inside SP2, the existing helper grows a record.

Three constraints, all load-bearing:

1. **The existing four-argument overloads keep their behaviour byte for byte.** SP1's golden-SQL tests must continue to pass unchanged, and Step 4 re-runs them to prove it.
2. **`FtsTable` stays non-`partial`** (plan adjustment 10). §5.2 writes `public static partial class`; the merged class is a plain `public static class` in one file, and SP2 edits that same file, so `partial` would be a gratuitous diff on a merged SP1 type.
3. **The new overload must not be ambiguous with the old one.** The old `BuildCreateSql(string, IReadOnlyList<string>, FtsTokenizer = …, string? contentTable = null)` has an optional `string?` in the slot where the new one takes `FtsTableOptions`. Making `options` **required** and non-nullable keeps `BuildCreateSql(name, cols, tokenizer, options)` and `BuildCreateSql(name, cols, tokenizer, "notes")` unambiguous at every call site.

`RemoveDiacritics` is emitted **only** for `unicode61` and `trigram` — the only two tokenizers that accept it. Emitting it for `porter unicode61` or `ascii` is a `CREATE VIRTUAL TABLE` constructor error.

- [ ] **Step 1: Write the failing tests**

`foundation\tests\Qavren.Edge.Sqlite.Tests\FtsTableOptionsTests.cs`:

```csharp
using Qavren.Edge.Sqlite.Fts;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

public class FtsTableOptionsTests
{
    [Fact]
    public void ExternalContentWithRowIdAndDiacritics()
    {
        var sql = FtsTable.BuildCreateSql(
            "notes_fts",
            ["Title", "Body"],
            FtsTokenizer.Unicode61,
            new FtsTableOptions { ContentTable = "notes", ContentRowId = "_rowid" });

        Assert.Equal(
            "CREATE VIRTUAL TABLE IF NOT EXISTS \"notes_fts\" USING fts5(" +
            "Title, Body, content='notes', content_rowid='_rowid', " +
            "tokenize='unicode61 remove_diacritics 2')",
            sql);
    }

    [Fact]
    public void PrefixIsEmittedWhenSet()
    {
        var sql = FtsTable.BuildCreateSql(
            "t_fts", ["Body"], FtsTokenizer.Unicode61,
            new FtsTableOptions { ContentTable = "t", ContentRowId = "_rowid", Prefix = "2 3" });

        Assert.Contains("prefix='2 3'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveDiacriticsIsOmittedForTokenizersThatRejectIt()
    {
        var ascii = FtsTable.BuildCreateSql("t_fts", ["Body"], FtsTokenizer.Ascii, new FtsTableOptions());
        var porter = FtsTable.BuildCreateSql("t_fts", ["Body"], FtsTokenizer.Porter, new FtsTableOptions());

        Assert.DoesNotContain("remove_diacritics", ascii, StringComparison.Ordinal);
        Assert.DoesNotContain("remove_diacritics", porter, StringComparison.Ordinal);
        Assert.Contains("tokenize='ascii'", ascii, StringComparison.Ordinal);
    }

    [Fact]
    public void TriggersUseTheSuppliedContentRowId()
    {
        var triggers = FtsTable.BuildSyncTriggerSql("notes_fts", "notes", ["Title", "Body"], "_rowid");

        Assert.Equal(3, triggers.Count);
        Assert.All(triggers, t => Assert.Contains("\"_rowid\"", t, StringComparison.Ordinal));
        Assert.Contains("AFTER INSERT ON \"notes\"", triggers[0], StringComparison.Ordinal);
        Assert.Contains("VALUES (new.\"_rowid\", new.Title, new.Body)", triggers[0], StringComparison.Ordinal);
        Assert.Contains("'delete', old.\"_rowid\"", triggers[1], StringComparison.Ordinal);
        Assert.Contains("'delete', old.\"_rowid\"", triggers[2], StringComparison.Ordinal);
    }

    [Fact]
    public void TheFourArgumentOverloadIsUnchanged()
    {
        // SP1's merged behaviour, asserted here as well so an edit to the shared emitter that
        // breaks it fails in SP2's own test file too, not only in SP1's.
        Assert.Equal(
            "CREATE VIRTUAL TABLE IF NOT EXISTS \"notes_fts\" USING fts5(" +
            "Title, Body, content='notes', tokenize='unicode61')",
            FtsTable.BuildCreateSql("notes_fts", ["Title", "Body"], FtsTokenizer.Unicode61, "notes"));
    }
}
```

- [ ] **Step 2: Add `FtsTableOptions`**

Insert into `foundation\src\Qavren.Edge.Sqlite\Fts\FtsTable.cs`, above the `FtsTable` class:

```csharp
/// <summary>
/// The FTS5 options a plain column list cannot express. Added for sub-project 2's vector-store
/// sidecar, which keys on an explicit <c>"_rowid"</c> column rather than the implicit rowid alias.
/// </summary>
public sealed record FtsTableOptions
{
    /// <summary>External-content table. Null omits <c>content=</c> entirely.</summary>
    public string? ContentTable { get; init; }

    /// <summary>Emitted as <c>content_rowid=</c>. Only meaningful with <see cref="ContentTable"/>.</summary>
    public string ContentRowId { get; init; } = "rowid";

    /// <summary>
    /// 0 | 1 | 2. Default 2: unicode61's own default of 1 has a known multi-diacritic bug.
    /// Emitted only for <c>unicode61</c> and <c>trigram</c>, the only tokenizers that accept it.
    /// </summary>
    public int RemoveDiacritics { get; init; } = 2;

    /// <summary>Emitted as <c>prefix='…'</c>, e.g. <c>"2 3"</c>. Null omits the option.</summary>
    public string? Prefix { get; init; }
}
```

- [ ] **Step 3: Add the three overloads**

Add to `FtsTable`, leaving every existing member untouched:

```csharp
    /// <summary>
    /// Options-taking overload. <paramref name="options"/> is required and non-nullable so this
    /// never becomes ambiguous with the <c>string? contentTable</c> overload above.
    /// </summary>
    public static string BuildCreateSql(
        string name,
        IReadOnlyList<string> columns,
        FtsTokenizer tokenizer,
        FtsTableOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(options);
        if (columns.Count == 0)
        {
            throw new ArgumentException("An FTS5 table needs at least one column.", nameof(columns));
        }

        if (options.RemoveDiacritics is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.RemoveDiacritics, "remove_diacritics must be 0, 1 or 2.");
        }

        var parts = new List<string>(columns);
        if (!string.IsNullOrWhiteSpace(options.ContentTable))
        {
            parts.Add($"content='{options.ContentTable}'");
            parts.Add($"content_rowid='{options.ContentRowId}'");
        }

        if (!string.IsNullOrWhiteSpace(options.Prefix))
        {
            parts.Add($"prefix='{options.Prefix}'");
        }

        var token = TokenizerToken(tokenizer);
        // remove_diacritics is accepted by unicode61 and trigram only; anywhere else it is a
        // CREATE VIRTUAL TABLE constructor error.
        if (tokenizer is FtsTokenizer.Unicode61 or FtsTokenizer.Trigram)
        {
            token += " remove_diacritics " +
                     options.RemoveDiacritics.ToString(CultureInfo.InvariantCulture);
        }

        parts.Add($"tokenize='{token}'");
        return $"CREATE VIRTUAL TABLE IF NOT EXISTS \"{name}\" USING fts5({string.Join(", ", parts)})";
    }

    public static async Task CreateAsync(
        SqliteConnection connection,
        string name,
        IReadOnlyList<string> columns,
        FtsTokenizer tokenizer,
        FtsTableOptions options,
        CancellationToken cancellationToken = default)
    {
        await connection.ExecuteAsync(
            BuildCreateSql(name, columns, tokenizer, options),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sync triggers for an external-content table whose rowid alias is named
    /// <paramref name="contentRowId"/> rather than <c>rowid</c>.
    /// </summary>
    public static IReadOnlyList<string> BuildSyncTriggerSql(
        string ftsTable,
        string contentTable,
        IReadOnlyList<string> columns,
        string contentRowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ftsTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRowId);
        ArgumentNullException.ThrowIfNull(columns);

        var rowid = $"\"{contentRowId}\"";
        var columnList = string.Join(", ", columns);
        var newValues = string.Join(", ", columns.Select(c => "new." + c));
        var oldValues = string.Join(", ", columns.Select(c => "old." + c));

        return
        [
            $"CREATE TRIGGER IF NOT EXISTS \"{ftsTable}_ai\" AFTER INSERT ON \"{contentTable}\" BEGIN " +
            $"INSERT INTO \"{ftsTable}\"(rowid, {columnList}) VALUES (new.{rowid}, {newValues}); END",

            $"CREATE TRIGGER IF NOT EXISTS \"{ftsTable}_ad\" AFTER DELETE ON \"{contentTable}\" BEGIN " +
            $"INSERT INTO \"{ftsTable}\"(\"{ftsTable}\", rowid, {columnList}) VALUES ('delete', old.{rowid}, {oldValues}); END",

            $"CREATE TRIGGER IF NOT EXISTS \"{ftsTable}_au\" AFTER UPDATE ON \"{contentTable}\" BEGIN " +
            $"INSERT INTO \"{ftsTable}\"(\"{ftsTable}\", rowid, {columnList}) VALUES ('delete', old.{rowid}, {oldValues}); " +
            $"INSERT INTO \"{ftsTable}\"(rowid, {columnList}) VALUES (new.{rowid}, {newValues}); END",
        ];
    }
```

Add `using System.Globalization;` to the file's usings if it is not already there.

- [ ] **Step 4: Verify — including that SP1's existing golden SQL is unmoved**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp2\foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0 `
  -p:ArtifactsPath="D:\Local\Temp\qedge-sp2\w2\t21"
```

Expected: `Passed!` with every pre-existing Sqlite test still green plus 5 new ones, exit code 0. A failure in SP1's own FTS golden-SQL tests means the four-argument overload moved and the edit is not additive — revert and re-do.

---

### Task 2.2: `Qavren.Edge.Onnx` part 1 — EP policy, model paths, resource monitor, `OrtEnv`, `AddOnnx`

**Local-verifiable:** yes for `net10.0`; the `net10.0-android` compile is local, the two Apple compiles are probed and may be CI-only.

**Files:**
- Create: `embeddings\src\Qavren.Edge.Onnx\EdgeAiEventIds.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\EdgeAiStartupOrder.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\OnnxExceptions.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\ExecutionProviders.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\OnnxOptions.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\OnnxSessionOptions.cs` *(moved here from Task 3.1 — plan adjustment 22)*
- Create: `embeddings\src\Qavren.Edge.Onnx\Internal\SessionOptionsFactory.cs` *(plan adjustment 22)*
- Create: `embeddings\src\Qavren.Edge.Onnx\IEdgeModelPaths.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\IEdgeResourceMonitor.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\Internal\DefaultEdgeResourceMonitor.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\Internal\ExecutionProviderPolicyResolver.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\Internal\OnnxEnvironmentStartupTask.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\Internal\OnnxLifecycleObserver.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\Internal\OnnxDiagnosticsContributor.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\OnnxEdgeBuilderExtensions.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\Platforms\Android\AndroidEdgeModelPaths.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\Platforms\Android\AndroidResourceMonitor.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\Platforms\iOS\AppleEdgeModelPaths.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\Platforms\iOS\AppleResourceMonitor.cs`
- Test: `embeddings\tests\Qavren.Edge.Onnx.Tests\ExecutionProviderPolicyTests.cs`
- Test: `embeddings\tests\Qavren.Edge.Onnx.Tests\SessionFactoryShapeTests.cs` *(moved here from Task 3.1 — plan adjustment 22)*
- Test: `embeddings\tests\Qavren.Edge.Onnx.Tests\ModelPathsTests.cs`
- Test: `embeddings\tests\Qavren.Edge.Onnx.Tests\ResourceMonitorTests.cs`
- Test: `embeddings\tests\Qavren.Edge.Onnx.Tests\OnnxRegistrationTests.cs`
- Test: `embeddings\tests\Qavren.Edge.Onnx.Tests\Stubs\StubResourceMonitor.cs`

**Approach — read this before writing.** Everything goes through `AppendExecutionProvider(string providerName, Dictionary<string,string> providerOptions)`. The typed helpers are never called: `AppendExecutionProvider_CoreML` calls a native entry point deprecated in ORT 1.20.0, and `AppendExecutionProvider_Nnapi` is compiled inside `#if __ANDROID__` and throws `NotSupportedException` on every other build. The string overload is the only EP API that is not TFM-gated, which is why SP2's EP code is **one implementation compiled for every TFM** — and why `EdgeExecutionProvider` is exactly `{ Cpu, XnnPack, CoreMl }`. ORT documents that overload as accepting `"QNN"`, `"SNPE"`, `"XNNPACK"`, `"CoreML"` and `"AZURE"`; NNAPI is not among them, so it does not exist here. ADR 0004 has the full argument.

The single highest-risk mistake in this task is `RequireStaticInputShapes`. CoreML partitions the graph **at session-creation time from the shapes the graph declares**, not from the shapes fed at Run time. All four presets declare `[batch_size, sequence_length]` — symbolic. Setting the flag against symbolic dims means CoreML takes few or no nodes and the whole graph silently runs on CPU, which `ExecutionProviderAttempt.Accepted` cannot detect because `Accepted` only records that `AppendExecutionProvider` did not throw. The flag is therefore never set on its own: the resolver turns it on **only** when `OnnxSessionOptions.FreeDimensionOverrides` is non-empty, and throws `EdgeOnnxException(OnnxStaticShapesUnpinned)` if a caller sets it without them.

**`OnnxSessionOptions` and `SessionOptionsFactory` live in this task, not Task 3.1.** An earlier draft put both in wave 3 while leaving the guard's test here in wave 2, which made this task's own test uncompilable: `FreeDimensionOverrides` is a member of `OnnxSessionOptions` (§6.5) and there is no way to assert a guard over a type that does not exist yet. `SessionOptionsFactory` is the single highest-risk internal in the package — it is the one place `AddFreeDimensionOverrideByName`, `AddSessionConfigEntry`, `DisablePerSessionThreads`, `GraphOptimizationLevel`, `IntraOpNumThreads` and the EP append loop all meet — and it now has an owning file and an owning test. Task 3.1's session host **consumes** it and adds nothing to it.

- [ ] **Step 1: Write the failing tests**

`ExecutionProviderPolicyTests.cs` covers, with no ORT session created:

- Per-RID default order: ios / iossimulator / maccatalyst → `[CoreMl, Cpu]`; android → `[XnnPack, Cpu]`; win-x64, win-arm64, linux-*, osx-arm64 → `[Cpu]`; `osx-x64` → resolver reports the RID unsupported.
- CoreML option dictionary is `ModelFormat=MLProgram`, `MLComputeUnits=CPUAndNeuralEngine`, `ModelCacheDirectory=<OrtCache>/<modelId>/<sha16>`, and **contains no `RequireStaticInputShapes` key** with the default options.
- `EnableModelCache = false` **omits `ModelCacheDirectory` entirely** — it does not emit an empty value. The default (`true`) emits it.
- `FastPrediction = true` emits `SpecializationStrategy=FastPrediction`; the default (`false`) emits no such key. §6.1 marks it iOS 18+, so the key is emitted whenever the caller asks and the OS decides, which is ORT's own behaviour — SP2 does not version-gate it.
- `ProfileComputePlan = true` emits `ProfileComputePlan=1` and is the only truthful per-node EP signal ORT exposes.
- XNNPACK emits `intra_op_num_threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4)` and forces `SessionOptions.IntraOpNumThreads = 1`, logging a warning when the caller set it otherwise.
- Fail-soft: an appender stub that throws for `"CoreML"` yields `Accepted == EdgeExecutionProvider.Cpu` and an `Attempts` entry with `Accepted == false` and a non-null `Failure`.
- A provider named in `Required` that fails throws `EdgeOnnxException(OnnxExecutionProviderRequired)` instead of falling through.
- `Overrides["CoreML"]` merges over the computed dictionary; `Overrides["NNAPI"]` is rejected as an unknown provider name.

`SessionFactoryShapeTests.cs` covers the L0 half of §16.1's session-factory sentence, over a real ORT `SessionOptions` and **no session**:

- `RequireStaticInputShapes = true` with an **empty** `FreeDimensionOverrides` throws `EdgeOnnxException` with `Code == EdgeErrorCode.OnnxStaticShapesUnpinned`, and the message names `PinnedSequenceLength` as the supported way to get there.
- With a non-empty `FreeDimensionOverrides`, the same configuration emits `RequireStaticInputShapes=1` into the CoreML dictionary and calls `AddFreeDimensionOverrideByName` once per entry.
- `SessionConfigEntries` are applied through `AddSessionConfigEntry` and are **not** confused with `FreeDimensionOverrides` — two different ORT APIs, asserted separately, because §6.5's doc comment says so and a reader skimming the option names would reasonably guess wrong.
- `OnnxOptions.ShareThreadPool` true → `DisablePerSessionThreads()` is called; null with one registered model → not called; null with two → called.

The L1 half — "`PinnedSequenceLength` populates both the override dictionary and the flag" — cannot be asserted from this project: `PinnedSequenceLength` is a member of `OnnxEmbeddingOptions` in `Qavren.Edge.Embeddings.Onnx`, which `Qavren.Edge.Onnx.Tests` does not and must not reference. It is `PinnedSequenceLengthTests` in Task 4.1 (plan adjustment 23).

`ModelPathsTests.cs` covers `DefaultEdgeModelPaths`: `Models` is `<IEdgePaths.Data>/models`, `OrtCache` is `<IEdgePaths.Data>/ort-cache`, both created on first access, and the path is recomputed from `IEdgePaths` on every access rather than cached.

`ResourceMonitorTests.cs` covers the latch: `LastPressure` is **`null`** before any event — the whole reason §6.4 makes it nullable — `SetPressure(Moderate)` latches, `SetPressure(null)` clears, and `Read()` on the desktop path returns a non-null `AvailableMemoryBytes` from `GC.GetGCMemoryInfo()` with `Thermal == EdgeThermalState.Unknown`. Also: reads are cached for one second (drive `TimeProvider` and assert the second read inside the window returns the identical instance).

`OnnxRegistrationTests.cs` covers `AddOnnx()` idempotence: calling it twice registers one `IEdgeModelPaths`, one `IEdgeResourceMonitor`, one startup task at order 200, one lifecycle observer and one diagnostics contributor.

- [ ] **Step 2: `EdgeAiEventIds` and `EdgeAiStartupOrder`**

`EdgeEventIds` is a non-partial static class in SP1, so SP2 publishes its own, continuing the numbering. Every one of these is consumed through `LoggerMessage.Define` — this repo's `TreatWarningsAsErrors` plus `latest-recommended` makes CA1848 an error, as SP1's `EdgeHost` already discovered.

```csharp
namespace Qavren.Edge.Onnx;

/// <summary>Continues SP1's EdgeEventIds numbering. 600-899 is sub-project 2's range.</summary>
public static class EdgeAiEventIds
{
    public const int OrtEnvironmentCreated = 600;
    public const int OrtEnvironmentPreexisting = 601;
    public const int OrtLog = 602;

    public const int SessionLoaded = 620;
    public const int SessionLoadFailed = 621;
    public const int SessionDropped = 622;
    public const int ExecutionProviderSkipped = 623;
    public const int ExecutionProviderAccepted = 624;

    public const int ModelProvisioned = 640;
    public const int ModelDownloadStarted = 641;
    public const int ModelDownloadResumed = 642;
    public const int ModelDownloadRestarted = 643;
    public const int ModelHashMismatch = 644;
    public const int ModelDownloadFailed = 645;
    public const int OrtCachePurged = 646;

    public const int EmbeddingBatchCompleted = 700;
    public const int EmbeddingInputTruncated = 701;
    public const int EmbeddingWarmUpCompleted = 702;
    public const int EmbeddingBatchShrunk = 703;

    public const int CollectionCreated = 800;
    public const int CollectionDropped = 801;
    public const int HybridSearchExecuted = 802;
    public const int FtsMergeCompleted = 803;
    public const int IndexKindIgnored = 804;
}
```

`EdgeAiStartupOrder` is spec §6.5 verbatim: `OnnxEnvironment = 200`, `ModelProvisioning = 210`, `SessionWarmUp = 220`, `VectorSchema = 300`. The constants are published from L0 because that is the lowest package all three share; publishing an `int` is not a dependency. `EdgeStartupOrder` in SP1 is **not** changed — 200/210/220/300 already fit between `Migrations = 100` and `ConsumerDefault = 1000`.

- [ ] **Step 3: Exceptions**

`EdgeOnnxException` and `EdgeModelProvisioningException` exactly as §6.5 declares them, both deriving from SP1's `EdgeException` so the `HelpLink` convention applies for free.

- [ ] **Step 4: EP policy types, the resolver, `OnnxSessionOptions` and `SessionOptionsFactory`**

`EdgeExecutionProvider`, `ExecutionProviderAttempt`, `ExecutionProviderReport`, `CoreMlProviderOptions`, `XnnPackProviderOptions`, `OnnxExecutionProviderPolicy` exactly as §6.1 declares them, XML docs included — the doc comments on `RequireStaticInputShapes` and `Accepted` are part of the contract, not decoration, and the honesty clause ("`Accepted` means `AppendExecutionProvider` returned without throwing; it does **not** mean this provider executed every node") must appear on the member.

`ExecutionProviderPolicyResolver` is `internal`, takes an appender delegate so the tests can drive it without an ORT session, and applies one provider at a time, each in its own try/catch: a throw is recorded as `ExecutionProviderAttempt(provider, Accepted: false, options, Failure: message)`, logged at `ExecutionProviderSkipped` (623), and the loop continues. CPU is appended last whenever `FallBackToCpu`. Only a provider named in `Required` turns a failure into `EdgeOnnxException(OnnxExecutionProviderRequired)`.

Per-RID defaults, from §9.2:

| RID family | Order | Options |
|---|---|---|
| `ios`, `iossimulator`, `maccatalyst` | CoreML → CPU | `ModelFormat=MLProgram`, `MLComputeUnits=CPUAndNeuralEngine`, `ModelCacheDirectory=<OrtCache>/<modelId>/<sha16>` |
| `android` | XNNPACK → CPU | `intra_op_num_threads=clamp(ProcessorCount/2, 1, 4)` **and** `SessionOptions.IntraOpNumThreads = 1` |
| `win-x64`, `win-arm64`, `linux-*`, `osx-arm64` | CPU | `IntraOpNumThreads` from the policy |
| `osx-x64` | — | `EdgeOnnxException(OnnxUnsupportedRuntime)`; ORT 1.30.0's `runtimes/` has no `osx-x64` (verified) |

`ModelFormat=MLProgram` is a requirement, not a preference: the NeuralNetwork format's supported-op table lacks `LayerNormalization`, `Gelu` and `Erf`, so a BERT encoder fragments into CPU-fallback partitions with a CPU↔ANE round trip per block.

`OnnxSessionOptions` is §6.5 verbatim: `ExecutionProviders`, `DropOnMemoryPressure`, `MemoryHeadroomFactor` (2.5), `MemoryHeadroomBytes` (48 MB), `FreeDimensionOverrides` and `SessionConfigEntries`. The two dictionaries are separate properties over two separate ORT APIs and the XML doc on each must say so.

`SessionOptionsFactory` is `internal`, and it is the one place ORT's `SessionOptions` is configured:

```csharp
namespace Qavren.Edge.Onnx.Internal;

/// <summary>
/// Builds a configured ORT <see cref="SessionOptions"/>. The single place
/// AddFreeDimensionOverrideByName, AddSessionConfigEntry, DisablePerSessionThreads,
/// GraphOptimizationLevel, IntraOpNumThreads and the execution-provider append loop meet, so the
/// OnnxStaticShapesUnpinned guard has exactly one home. Internal: a consumer configures this
/// through OnnxSessionOptions and never touches SessionOptions directly.
/// </summary>
internal static class SessionOptionsFactory
{
    /// <summary>
    /// Order is load-bearing. Free-dimension overrides are applied BEFORE the providers are
    /// appended, because CoreML partitions from the declared shapes at append time; applying them
    /// afterwards would leave the graph symbolic for exactly the decision they exist to change.
    /// Throws <see cref="EdgeOnnxException"/> with
    /// <see cref="EdgeErrorCode.OnnxStaticShapesUnpinned"/> when
    /// <c>CoreMlProviderOptions.RequireStaticInputShapes</c> is set and
    /// <c>options.FreeDimensionOverrides</c> is empty.
    /// </summary>
    public static (SessionOptions Options, ExecutionProviderReport Report) Build(
        string modelId,
        OnnxSessionOptions options,
        string? ortCacheDirectory = null,
        string? graphSha256 = null,
        ILogger? logger = null);
}
```

The guard lives here rather than in `ExecutionProviderPolicyResolver` because the resolver sees only the EP dictionaries and the factory is the only thing that sees `FreeDimensionOverrides` as well. Task 3.1's `OnnxSessionHost` calls `Build` and adds nothing to it.

`ModelCacheDirectory` is the single highest-value setting in this package. Unset, CoreML recompiles the captured subgraph on every session creation and leaks the artefact into the iOS tmp directory. ORT does not track model changes or evict entries, so **the cache key is made content-addressed by the path**: `<OrtCache>/<modelId>/<sha16>/`, where `sha16` is the first 16 hex characters of the graph SHA-256. No `CACHE_KEY` metadata_props entry is written — that would mean shipping an ONNX protobuf writer for no additional guarantee.

- [ ] **Step 5: `IEdgeModelPaths` and the platform implementations**

Interface and `DefaultEdgeModelPaths` exactly as §6.2. `Platforms/Android/AndroidEdgeModelPaths.cs` uses `Context.NoBackupFilesDir/qavren-edge/{models,ort-cache}` (auto-excluded, no consumer manifest edit). `Platforms/iOS/AppleEdgeModelPaths.cs` uses `Library/Application Support/qavren-edge/{models,ort-cache}` and sets `NSURLIsExcludedFromBackupKey` on each directory **after** creation. Neither caches an absolute path across launches: the iOS sandbox path carries an app GUID that changes on every clean install, which is the same reason SP1's `MauiEdgePaths` reads `FileSystem.Current` each time.

Why not `IEdgePaths.Data`: on Android that is `Context.FilesDir`, whose Auto Backup quota is **25 MB per app**, so a 23 MB int8 model consumes essentially all of it and an fp32 one triggers `onQuotaExceeded()` and stops backing up the user's actual SQLite database. Why not `IEdgePaths.Cache`: the OS may purge it mid-session, converting a purge into a re-download.

- [ ] **Step 6: `IEdgeResourceMonitor`**

Three public types, all §6.4: `EdgeThermalState`, `EdgeResourceSnapshot` and `IEdgeResourceMonitor` — transcribed verbatim, including the nullable `LastPressure` latch, its whole XML doc (the paragraph explaining *why* it is nullable is the thing that stops a later reader "tidying" it), and `SetPressure`. The snapshot record is restated here because it is the one §6.4 type no other task names and because every one of its six members is read by a caller in Task 4.1 or Task 6.3:

```csharp
namespace Qavren.Edge.Onnx;

public enum EdgeThermalState { Unknown = 0, Nominal, Fair, Serious, Critical }

public sealed record EdgeResourceSnapshot(
    long? AvailableMemoryBytes,
    bool? IsLowMemory,
    EdgeThermalState Thermal,
    float? ThermalHeadroom,
    bool? IsLowPowerMode,
    EdgeMemoryPressure? LastPressure);
```

Every nullable member above means **unknown**, never "no" — `AvailableMemoryBytes` null on a desktop where `GC.GetGCMemoryInfo()` reports nothing usable is not a refusal, and Task 3.1's memory pre-flight is required to skip its check rather than fail it. `DefaultEdgeResourceMonitor` (net10.0 and Windows) reads `GC.GetGCMemoryInfo()` and reports `Thermal = Unknown`, advisory only. Android reads `ActivityManager.GetMemoryInfo` (availMem, lowMemory), `PowerManager.CurrentThermalStatus` and `GetThermalHeadroom(10)`. Apple reads `os_proc_available_memory()` via `DllImport("__Internal")` — **0 means unknown or already over, and is treated as unknown, never as a refusal** — plus `ProcessInfo.ThermalState` and `IsLowPowerModeEnabled`.

Reads are cached for one second: Android's `GetThermalHeadroom` is rate-limited to ~1 Hz and returns `NaN` when polled faster. The cache is driven by the injected `TimeProvider` so the test can assert it.

- [ ] **Step 7: `OrtEnv` startup task, lifecycle observer, diagnostics, `AddOnnx`**

The startup task at order 200 must run **before anything can construct a `SessionOptions`**: ORT creates the environment implicitly on the first `SessionOptions`, and `CreateInstanceWithOptions` then throws `"OrtEnv singleton instance already exists"`. The task builds `EnvironmentCreationOptions { logId, logLevel, loggingFunction }` where `loggingFunction` is a `DOrtLoggingFunction` forwarding into `ILogger` under `EdgeAiEventIds.OrtLog` and **held in a static field so it is never collected**, calls `CreateInstanceWithOptions`, and — if `OrtEnv.IsCreated` was already true — logs `OrtEnvironmentPreexisting` (601), records `ortEnvironmentPreexisting` in diagnostics and continues. Another library winning the race is not our failure to crash on. When `OnnxOptions.DisableOrtDllImportResolver` is set, `OrtEnv.DisableDllImportResolver = true` is the **first statement** of the task.

If the host RID is `osx-x64`, the task throws `EdgeOnnxException(OnnxUnsupportedRuntime)` naming the RID and stating that ORT ships no Intel-macOS native — a named error at startup rather than a `DllNotFoundException` on first embed.

`OnnxLifecycleObserver` registers with `TryAddEnumerable`, exactly as SP1's SQLite observer does, and derives from SP1's no-op `EdgeLifecycleObserver`. In this task it implements only the pressure latch and `Stopping`; the `DropAsync` wiring arrives with the session host in Task 3.1:

- `MemoryPressure(Moderate)` → `IEdgeResourceMonitor.SetPressure(Moderate)` and **nothing else**. It does not touch a batch size: that lives in `OnnxEmbeddingOptions` in L1, which this L0 observer cannot see. Dropping a 23 MB session that is about to be needed again would be a worse trade than shrinking a batch.
- `Resumed` → `SetPressure(null)`. `null`, not a `None` member, because SP1's merged `EdgeMemoryPressure` has none and adding one would renumber the existing three.
- `Sleeping` → nothing. The CoreML cache is already on disk.
- `Stopping` → drops every session (Task 3.1 completes this). `OrtEnv` is **not** disposed: it is a process-wide singleton with a one-shot options hook, and tearing it down would silently break a second Edge host in the same process.

`OnnxDiagnosticsContributor` reports `ComponentName = "Qavren.Edge.Onnx"` — note that no SP2 component name contains "Native", so SP1's `EdgeDiagnostics.Report()` keeps picking the SQLite native block for `EdgeDiagnosticsReport.Native`. In this task it reports `ortVersion`, `ortManagedAsset`, `runtimeIdentifier`, `unsupportedRuntime`, `ortEnvironmentPreexisting`, `sharedThreadPool`, `modelsDirectory`, `modelsDirectoryExcludedFromBackup`, `ortCacheDirectory`, `ortCacheBytes`, and the live snapshot (`availableMemoryBytes`, `isLowMemory`, `thermalState`, `thermalHeadroom`, `isLowPowerMode`, `lastMemoryPressure`). Per-session keys arrive in Task 3.1.

`AddOnnx(Action<OnnxOptions>?)` is **idempotent** and registers all of the above. `UseExecutionProviderPolicy` and `UseModelPaths` are the two configuration hooks this task ships; `AddOnnxModel`, `AddModelSource*`, `ProvisionModelAtStartup` and `WarmUpSessionAtStartup` arrive in Task 3.1.

**Defaults that change behaviour — `OnnxOptions`, §6.5, transcribed because every one of these five is a behaviour switch a reader would otherwise skim past:**

```csharp
public sealed class OnnxOptions
{
    public OrtLoggingLevel LogSeverity { get; set; } = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;
    public string LogId { get; set; } = "qavren.edge";
    /// <summary>Bridges ORT's native logger into ILogger through DOrtLoggingFunction.</summary>
    public bool BridgeNativeLogging { get; set; } = true;
    /// <summary>Calls SessionOptions.DisablePerSessionThreads() so every session shares one process
    /// pool. Default true once more than one model is registered.</summary>
    public bool? ShareThreadPool { get; set; }
    /// <summary>Sets OrtEnv.DisableDllImportResolver before any ORT type is touched. Default false;
    /// the escape hatch if SP1's provider resolver ever collides with ORT's.</summary>
    public bool DisableOrtDllImportResolver { get; set; }
}
```

`LogSeverity` defaults to `ORT_LOGGING_LEVEL_WARNING` and `BridgeNativeLogging` to **true** together, and the pair is the reason a consumer sees ORT's native warnings in their own `ILogger` with no configuration: `LogSeverity` is what the environment passes to `EnvironmentCreationOptions`, and `BridgeNativeLogging` is what installs the `DOrtLoggingFunction` that forwards them. Turning the bridge off leaves ORT logging to its own stderr sink, which on a device is nowhere. Both are asserted in `OnnxRegistrationTests.cs` against the resolved `IOptions<OnnxOptions>` and the `EnvironmentCreationOptions` the startup task *builds* — never by creating a real `OrtEnv`, which is a process-wide singleton whose second creation throws and would make the test order-dependent. Two facts: the built creation options carry `ORT_LOGGING_LEVEL_WARNING` and the log id `qavren.edge`; and with `BridgeNativeLogging` left at its default the task supplies a non-null `DOrtLoggingFunction`, while setting it false supplies none.

- [ ] **Step 8: Probe whether the Apple TFMs compile on this host**

§19 item 4 asks whether `Mono.Android` and `Microsoft.iOS` bindings are reachable from a non-MAUI class library — `Android.App.Application.Context`, `ActivityManager`, `PowerManager`, `NSUrl`/`NSFileManager`, `DllImport("__Internal")` — with **no** `UseMaui`. Android is answerable here; Apple may not be.

```powershell
$p = "C:\Users\steve\projects\qavren-edge-sp2\embeddings\src\Qavren.Edge.Onnx\Qavren.Edge.Onnx.csproj"
dotnet build $p -c Release -f net10.0-android -p:TargetFrameworks=net10.0-android `
  -p:ArtifactsPath="D:\Local\Temp\qedge-sp2\w2\t22"
foreach ($tfm in 'net10.0-ios','net10.0-maccatalyst') {
  Write-Host "--- probing $tfm"
  dotnet build $p -c Release -f $tfm -p:TargetFrameworks=$tfm `
  -p:ArtifactsPath="D:\Local\Temp\qedge-sp2\w2\t22"
  if ($LASTEXITCODE -ne 0) {
    Write-Host "SKIPPED: $tfm does not compile on this Windows host; it is a CI gate (see CI-only work)."
  }
}
```

Expected: the android leg **builds**. The two Apple legs either build or print the `SKIPPED` line — like SP1's ARM64-toolset probe, a host that cannot do it says so rather than failing the task. Either way both are gated in CI by `ci.yml`'s solution restore plus the Apple device lanes.

- [ ] **Step 9: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp2\embeddings\tests\Qavren.Edge.Onnx.Tests\Qavren.Edge.Onnx.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0 `
  -p:ArtifactsPath="D:\Local\Temp\qedge-sp2\w2\t22"
```

Expected: `Passed! - Failed: 0`, exit code 0, and Step 8's android build succeeded.

---

### Task 2.3: `ci.yml` test steps, ORT asset assertions, `trim-smoke`, the nightly tier-3 lane, and the workflow contract *(integrator)*

**Local-verifiable:** yes — the YAML is linted and the contract check is run here. **Every workflow *run* is CI-only.**

**Files:**
- Edit: `.github\workflows\ci.yml`
- Edit: `foundation\tools\ci-checks\assert-workflows.py`

**Approach.** `native.yml` and `release.yml` are untouched. SP2's projects live under `embeddings/**`, which no native path filter matches, so a managed-only SP2 PR still takes `native.yml`'s `reuse` job — zero macOS minutes, zero compilation. The device lanes are unchanged in number, so `assert-workflows.py`'s `trx2junit.py` and `java-junit` counts stay at 4.

**This task owns the *workflow* half of §16.3's tier-3 lane.** The test half — the `SkipUnless` class, the pinned reference-vector JSON and the 1e-3 cosine assertions — is Task 6.2, and it cannot be written before the generator exists in wave 4. Splitting it that way is what lets `ci.yml` reach its final shape in one task, in the only wave whose integrator owns `.github/**`: no later wave touches a root file, so the nightly job's YAML has to land here or it never lands at all. A job whose test project is still an empty stub in wave 2 is harmless — it is scheduled, not gated, and `ci-gate` does **not** take it in `needs` (a nightly job must never block a PR).

**The spend guardrails hold.** The repo's Actions budget rules say scan workflows run on `pull_request` only and hosted minutes on private repos are the invoice. The tier-3 lane is therefore `schedule` **plus** `workflow_dispatch` and nothing else — no `push`, no `pull_request` — and it runs on one image, not the three-OS matrix.

- [ ] **Step 1: Four test steps in the `test` job**

Insert immediately after the existing `Cipher tests` step, before `Pack`. `-p:TargetFrameworks=net10.0` is on every one, for SP1's documented reason: `-f` alone does not stop restore walking the full `TargetFrameworks` list of the project *and everything it references*, so the workload check fires on hosts without the mobile workloads.

```yaml
      - name: Onnx tests
        run: dotnet run --project embeddings/tests/Qavren.Edge.Onnx.Tests/Qavren.Edge.Onnx.Tests.csproj -c Release -f net10.0 -p:TargetFrameworks=net10.0

      - name: Embeddings tests
        run: dotnet run --project embeddings/tests/Qavren.Edge.Embeddings.Tests/Qavren.Edge.Embeddings.Tests.csproj -c Release -f net10.0 -p:TargetFrameworks=net10.0

      - name: VectorData tests
        run: dotnet run --project embeddings/tests/Qavren.Edge.VectorData.Tests/Qavren.Edge.VectorData.Tests.csproj -c Release -f net10.0 -p:TargetFrameworks=net10.0

      # Single-TFM project, so no -f. It is host-only and never runs on a device lane (spec 16.4).
      - name: VectorData conformance
        run: dotnet run --project embeddings/tests/Qavren.Edge.VectorData.Conformance.Tests/Qavren.Edge.VectorData.Conformance.Tests.csproj -c Release -p:TargetFrameworks=net10.0
```

- [ ] **Step 2: Two ORT asset assertions on the `windows-2025` leg**

Insert after the existing `Assert no SQLitePCLRaw bundle package is in the graph` step, which already runs `dotnet restore QavrenEdge.slnx` there because `windows-2025` is the only image with every workload. Both new asserts scan the **`Microsoft.ML.OnnxRuntime.Managed`** entries — the native `Microsoft.ML.OnnxRuntime` package carries no `compile`/`runtime` assets for any TFM, so an assertion written against it would pass vacuously forever (plan adjustment 2).

```yaml
      # Assertion 1: net10.0 must land on lib/net8.0, never lib/netstandard2.0. A silent fall-back
      # to netstandard2.0 drops System.Numerics.Tensors and changes the span hot path with no error
      # anywhere. Assertion 2: the three UNVERSIONED platform TFMs must resolve ORT's versioned
      # mobile assets. That is the standing regression guard for the claim that the SDK's default
      # TargetPlatformVersion clears ORT's 18.0/35.0 floors without SP2 pinning a versioned TFM -
      # if a future SDK or workload bump lowers a default, this is what catches it, and the
      # documented response is to pin net10.0-ios18.0 / -android35.0 / -maccatalyst18.0 on SP2's
      # projects only.
      - name: Assert ONNX Runtime asset resolution
        if: matrix.os == 'windows-2025'
        shell: pwsh
        run: |
          $assets = 'embeddings/src/Qavren.Edge.Onnx/obj/project.assets.json'
          if (-not (Test-Path $assets)) { throw "no project.assets.json at $assets" }
          $a = Get-Content $assets -Raw | ConvertFrom-Json
          $expected = [ordered]@{
            'net10.0'             = 'lib/net8.0/Microsoft.ML.OnnxRuntime.dll'
            'net10.0-android'     = 'lib/net9.0-android35.0/Microsoft.ML.OnnxRuntime.dll'
            'net10.0-ios'         = 'lib/net9.0-ios18.0/Microsoft.ML.OnnxRuntime.dll'
            'net10.0-maccatalyst' = 'lib/net9.0-maccatalyst18.0/Microsoft.ML.OnnxRuntime.dll'
          }
          foreach ($tfm in $expected.Keys) {
            $entry = $a.targets.$tfm.'Microsoft.ML.OnnxRuntime.Managed/1.30.0'
            if (-not $entry) { throw "$tfm did not resolve Microsoft.ML.OnnxRuntime.Managed 1.30.0" }
            $got = ($entry.compile.PSObject.Properties.Name) -join ','
            if ($got -ne $expected[$tfm]) {
              throw "$tfm resolved '$got'; expected '$($expected[$tfm])'"
            }
            Write-Host "OK $tfm -> $got"
          }
```

- [ ] **Step 3: The `trim-smoke` job**

Add as a top-level job. Publishing the **dynamic** path is the point: it is what turns §12.6's "the only trim/AOT-safe path" from an assertion into a measured claim, and it is only compilable at all because of the non-annotated `CollectionModel`-taking base constructor in §8. Neither ORT's nor `Microsoft.ML.Tokenizers`'s managed assemblies carry `IsAotCompatible`, trim-analysis attributes or ILLink descriptors, so the README claims whatever these two publishes prove and nothing more (§19 item 12).

```yaml
  # Spec 17. Publishes the trim-smoke console trimmed and runs it: tokenize a fixed string, embed
  # it with the base64 fixture, and round-trip one vec0 upsert + search THROUGH
  # EdgeDynamicVectorStoreCollection - the reflection-free path, reached with GetDynamicCollection
  # and a VectorStoreCollectionDefinition, never GetCollection<TKey,TRecord>. The PublishAot
  # variant is continue-on-error for one release cycle and then either becomes required or the
  # AOT claim is dropped from the README.
  trim-smoke:
    needs: natives
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0, filter: 'tree:0' }
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: actions/download-artifact@v4
        with: { pattern: native-*, merge-multiple: true, path: foundation/native/artifacts }

      - name: Publish trimmed
        run: >
          dotnet publish embeddings/tools/Qavren.Edge.TrimSmoke/Qavren.Edge.TrimSmoke.csproj
          -c Release -r linux-x64 --self-contained true
          -p:PublishTrimmed=true -p:TargetFrameworks=net10.0
          -o artifacts/trim-smoke

      - name: Run trimmed
        run: ./artifacts/trim-smoke/Qavren.Edge.TrimSmoke

      - name: Publish AOT
        continue-on-error: true
        run: >
          dotnet publish embeddings/tools/Qavren.Edge.TrimSmoke/Qavren.Edge.TrimSmoke.csproj
          -c Release -r linux-x64 --self-contained true
          -p:PublishAot=true -p:TargetFrameworks=net10.0
          -o artifacts/aot-smoke

      - name: Run AOT
        continue-on-error: true
        run: ./artifacts/aot-smoke/Qavren.Edge.TrimSmoke
```

- [ ] **Step 3b: The nightly tier-3 real-model job**

`ci.yml`'s `on:` block gains a `schedule` trigger. The existing `push`/`pull_request` triggers are untouched — adding a schedule does not make any other job run nightly, because every other job keeps whatever conditions it has today and `model-tests` is the only job with an `if` that requires the schedule:

```yaml
on:
  push:
    branches: [main]
  pull_request:
  workflow_dispatch:
  schedule:
    # 07:00 UTC daily. Tier 3 only - see the `if` on model-tests. Nothing else keys off this.
    - cron: '0 7 * * *'
```

Then one job. It is **not** in `ci-gate`'s `needs`, deliberately: a nightly lane that gates merges would red every PR the moment Hugging Face has a bad hour, and §16.3's whole framing is "opt-in, nightly, never on a PR".

```yaml
  # Spec 16.3. Real int8 MiniLM, pinned to one HF revision, SHA-256 verified, cached on the
  # sha256 itself - not a URL, not a date, because a URL and a date both survive a content change
  # and the cache key is the only thing standing between a silently-swapped model and a green
  # nightly. Never on a PR: schedule and workflow_dispatch only. NOT in ci-gate's needs.
  model-tests:
    if: github.event_name == 'schedule' || github.event_name == 'workflow_dispatch'
    runs-on: ubuntu-24.04
    timeout-minutes: 30
    env:
      QAVREN_EDGE_MODEL_REPO: sentence-transformers/all-MiniLM-L6-v2
      QAVREN_EDGE_MODEL_REV: 1110a243fdf4706b3f48f1d95db1a4f5529b4d41
      QAVREN_EDGE_MODEL_SHA256: 4278337fd0ff3c68bfb6291042cad8ab363e1d9fbc43dcb499fe91c871902474
      QAVREN_EDGE_VOCAB_SHA256: 07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3
      QAVREN_EDGE_MODEL_DIR: ${{ github.workspace }}/.model-cache
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0, filter: 'tree:0' }
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: actions/download-artifact@v4
        with: { pattern: native-*, merge-multiple: true, path: foundation/native/artifacts }

      # Keyed on the CONTENT hash. A key containing the revision would still be stale if the
      # revision were ever repointed; a key containing a date would miss every day.
      - uses: actions/cache@v6.1.0
        id: model-cache
        with:
          path: ${{ env.QAVREN_EDGE_MODEL_DIR }}
          key: qavren-edge-model-${{ env.QAVREN_EDGE_MODEL_SHA256 }}

      - name: Fetch the pinned model
        if: steps.model-cache.outputs.cache-hit != 'true'
        run: |
          set -euo pipefail
          mkdir -p "$QAVREN_EDGE_MODEL_DIR/onnx"
          base="https://huggingface.co/$QAVREN_EDGE_MODEL_REPO/resolve/$QAVREN_EDGE_MODEL_REV"
          curl -fsSL "$base/onnx/model_qint8_arm64.onnx" -o "$QAVREN_EDGE_MODEL_DIR/onnx/model_qint8_arm64.onnx"
          curl -fsSL "$base/vocab.txt" -o "$QAVREN_EDGE_MODEL_DIR/vocab.txt"

      # Verified on EVERY run, cache hit included. A poisoned or truncated cache entry is exactly
      # the failure this lane would otherwise report as a numerical regression.
      - name: Verify the model hashes
        run: |
          set -euo pipefail
          echo "$QAVREN_EDGE_MODEL_SHA256  $QAVREN_EDGE_MODEL_DIR/onnx/model_qint8_arm64.onnx" | sha256sum -c -
          echo "$QAVREN_EDGE_VOCAB_SHA256  $QAVREN_EDGE_MODEL_DIR/vocab.txt" | sha256sum -c -

      # QAVREN_EDGE_MODEL_DIR is what SkipUnless reads. It is set for the whole job, so the tier-3
      # facts run; on every other lane it is unset and they skip at RUNTIME, after the class
      # constructor, which is why the session is built inside the test body (spec 16.3).
      - name: Tier-3 embedding tests
        run: dotnet run --project embeddings/tests/Qavren.Edge.Embeddings.Tests/Qavren.Edge.Embeddings.Tests.csproj -c Release -f net10.0 -p:TargetFrameworks=net10.0
```

- [ ] **Step 4: Add `trim-smoke` to `ci-gate`'s `needs`**

```yaml
  ci-gate:
    needs: [natives, test, trim-smoke, device-tests-android, device-tests-ios, device-tests-maccatalyst, device-tests-windows]
```

Nothing else in `ci-gate` changes: it already reports `always()` and is red if any dependency failed or was cancelled.

- [ ] **Step 5: Three assertions in `assert-workflows.py`**

Insert before the final `print`, after the existing native-cache block:

```python
# --- Sub-project 2 ---
# Every test project under embeddings/tests/ must appear as an explicit ci.yml step, so a new
# test project cannot silently never run. The trim-smoke console deliberately lives under
# embeddings/tools/ instead, because it is published rather than run as a test step.
sp2_tests = sorted((root / "embeddings" / "tests").glob("*/*.csproj"))
if not sp2_tests:
    problems.append("no test projects found under embeddings/tests/")
for proj in sp2_tests:
    rel = proj.relative_to(root).as_posix()
    if rel not in ci_text:
        problems.append("ci.yml has no explicit step for " + rel)

# The ORT managed-asset assertion. Written against Microsoft.ML.OnnxRuntime.Managed on purpose:
# the native Microsoft.ML.OnnxRuntime package has no compile/runtime assets for any TFM, so an
# assertion against that id would pass vacuously forever.
for token in ("Microsoft.ML.OnnxRuntime.Managed/1.30.0", "lib/net9.0-android35.0",
              "lib/net9.0-ios18.0", "lib/net9.0-maccatalyst18.0", "lib/net8.0"):
    if token not in ci_text:
        problems.append("ci.yml ORT asset assertion is incomplete (" + token + ")")

if "trim-smoke" not in ci["jobs"]:
    problems.append("ci.yml missing job trim-smoke")
elif "trim-smoke" not in ci["jobs"]["ci-gate"]["needs"]:
    problems.append("trim-smoke is not in ci-gate's needs, so its failure would not block a merge")

# The tier-3 nightly lane (spec 16.3). Three properties, each of which has a way of quietly
# regressing: the schedule trigger can be dropped in a merge, the job can lose its event guard
# and start running on every PR (a 23 MB download per push), and the cache key can drift off the
# content hash onto a URL or a date - at which point a swapped model is invisible.
if "schedule" not in (ci.get(True) or ci.get("on") or {}):
    problems.append("ci.yml has no schedule trigger, so the tier-3 model lane never runs")
if "model-tests" not in ci["jobs"]:
    problems.append("ci.yml missing job model-tests (spec 16.3 tier 3)")
else:
    mt = ci["jobs"]["model-tests"]
    if "schedule" not in str(mt.get("if", "")):
        problems.append("model-tests is not guarded to schedule/workflow_dispatch; it would run on PRs")
    if "model-tests" in ci["jobs"]["ci-gate"]["needs"]:
        problems.append("model-tests must NOT gate ci-gate: spec 16.3 says never on a PR")
    sha = "4278337fd0ff3c68bfb6291042cad8ab363e1d9fbc43dcb499fe91c871902474"
    if sha not in ci_text:
        problems.append("model-tests does not pin the model sha256; the cache key must be the content hash")
    if "actions/cache@v6.1.0" not in ci_text:
        problems.append("model-tests does not cache the model with actions/cache@v6.1.0")
```

`ci.get(True)` is not a typo. PyYAML parses the bare key `on:` as the boolean `True` under the YAML 1.1 rules it implements, so a lookup by the string `"on"` silently misses and the assertion would pass vacuously — the same class of bug as asserting against the wrong ORT package id. Checking both spellings costs nothing and survives a PyYAML that fixes it.

- [ ] **Step 6: Verify**

```powershell
$py = "C:\Python314\python.exe"     # PyYAML 6.0.3 lives here; see Environment ground truth
& $py "C:\Users\steve\projects\qavren-edge-sp2\foundation\tools\ci-checks\assert-workflows.py" "C:\Users\steve\projects\qavren-edge-sp2"
if ($LASTEXITCODE -ne 0) { throw "assert-workflows.py failed" }
& $py -c "import yaml; d=yaml.safe_load(open(r'C:\Users\steve\projects\qavren-edge-sp2\.github\workflows\ci.yml', encoding='utf-8')); assert 'model-tests' in d['jobs']; print('ci.yml parses, model-tests present')"
if ($LASTEXITCODE -ne 0) { throw "ci.yml did not parse" }
```

**The interpreter is spelled absolutely, and that is not pedantry.** `python` does resolve on this box, but it resolves through a `WindowsApps` shim that sits on `PATH` behind `C:\Python314`, and `assert-workflows.py` needs **PyYAML**, which only the real interpreter has (6.0.3, verified). A bare `python` that lands on the shim fails with `ModuleNotFoundError: yaml` — or, worse, with the Store's install prompt and exit code 0. Every other task in this plan already spells its tools absolutely for the same reason.

Expected: the existing `OK: gates, 4 device lanes, JUnit, win-arm64, native artifact cache + reuse, SBOM and native release assets all present …` line with no added problems, exit code 0, and `ci.yml parses, model-tests present`.

**Note for the integrator.** This task's four test steps — and the nightly job's fifth — name projects that are still empty stubs in wave 2. That is deliberate and harmless — an MTP application with no tests exits 0 — and it is what lets `assert-workflows.py`'s "every test project has a step" rule be true from the moment the projects exist rather than three waves later.

---

### Task 2.4: Wave 2 close — sequential re-verify and commit *(integrator)*

**Local-verifiable:** yes.

**Files:** none of its own. It reads what wave 2 wrote and commits it.

**Approach.** The parallel phase ran every verify under `-p:ArtifactsPath=D:\Local\Temp\qedge-sp2\w2\t…`, so no two tasks contended for one `obj/` or `bin/`. That isolation is exactly why the wave is **not** yet proved: nothing has built these projects in the tree the next wave, and CI, will use. This task deletes the scratch roots, re-runs every verify **sequentially and un-isolated**, and only then commits.

- [ ] **Step 1: Clear the wave's isolated outputs**

```powershell
Remove-Item -Recurse -Force "D:\Local\Temp\qedge-sp2\w2" -ErrorAction SilentlyContinue
```

- [ ] **Step 2: Re-run every verify in the wave, sequentially, in the real tree**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp2"
foreach ($p in @(
  "foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj",
  "embeddings\tests\Qavren.Edge.Onnx.Tests\Qavren.Edge.Onnx.Tests.csproj")) {
  dotnet run --project "$root\$p" -c Release -f net10.0 -p:TargetFrameworks=net10.0
  if ($LASTEXITCODE -ne 0) { throw "FAILED: $p" }
}
& "C:\Python314\python.exe" "$root\foundation\tools\ci-checks\assert-workflows.py" $root
if ($LASTEXITCODE -ne 0) { throw "FAILED: assert-workflows.py" }
Write-Host 'OK: wave 2 re-verified sequentially'
```

Expected: `OK: wave 2 re-verified sequentially`, exit code 0. A failure here that did not appear in the parallel phase is real — the usual cause is a project that only restored because a sibling task had already written its assets file.

- [ ] **Step 3: Commit the wave**

One commit, Conventional Commits, no `Co-Authored-By` and no AI attribution, on `feat/sp2-embeddings`. Never on `main`.

```
feat(sp2): ONNX runtime hosting policy, session options factory and CI wiring

- FtsTable gains an options overload (SP1 edit 2 of 2, additive)
- Qavren.Edge.Onnx: EP policy, model paths, resource monitor, OrtEnv, AddOnnx
- OnnxSessionOptions + SessionOptionsFactory with the OnnxStaticShapesUnpinned guard
- ci.yml: four test steps, two ORT asset asserts, trim-smoke, the nightly tier-3 lane
```

- [ ] **Step 4: Verify**

Step 2 printed its `OK` line and `git status --short` is clean. Wave 3 may start.

---

## WAVE 3 — Model provisioning + session host; vector-store model, SQL and filters

### Task 3.1: `Qavren.Edge.Onnx` part 2 — model store, sources, and the leased session host

**Local-verifiable:** yes.

**Files:**
- Create: `embeddings\src\Qavren.Edge.Onnx\OnnxModelManifest.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\IOnnxModelSource.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\Internal\FileOnnxModelSource.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\Internal\BundledOnnxModelSource.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\Internal\HttpOnnxModelSource.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\Internal\OnnxModelStore.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\IOnnxSessionHost.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\Internal\OnnxSessionHost.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\Internal\OnnxProvisioningStartupTask.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\Internal\OnnxWarmUpStartupTask.cs`
- Create: `embeddings\src\Qavren.Edge.Onnx\Properties\AssemblyInfo.cs` *(`InternalsVisibleTo`)*
- Edit: `embeddings\src\Qavren.Edge.Onnx\OnnxEdgeBuilderExtensions.cs`
- Edit: `embeddings\src\Qavren.Edge.Onnx\Internal\OnnxLifecycleObserver.cs`
- Edit: `embeddings\src\Qavren.Edge.Onnx\Internal\OnnxDiagnosticsContributor.cs`
- Edit: `embeddings\tests\Qavren.Edge.Onnx.Tests\Qavren.Edge.Onnx.Tests.csproj` *(add the fixture link)*
- Test: `embeddings\tests\Qavren.Edge.Onnx.Tests\ModelStoreTests.cs`
- Test: `embeddings\tests\Qavren.Edge.Onnx.Tests\HttpModelSourceTests.cs`
- Test: `embeddings\tests\Qavren.Edge.Onnx.Tests\ModelUrlTests.cs` *(the `HttpOnnxModelSourceOptions` defaults — Step 3)*
- Test: `embeddings\tests\Qavren.Edge.Onnx.Tests\SessionHostTests.cs`

`OnnxSessionOptions.cs`, `Internal/SessionOptionsFactory.cs` and `SessionFactoryShapeTests.cs` were in this task in an earlier draft and are now Task 2.2's (plan adjustment 22), because Task 2.2's own tests assert the `OnnxStaticShapesUnpinned` guard and could not compile without them. This task **consumes** `SessionOptionsFactory.Build` and adds no configuration of its own.

**Approach — read this before writing.** Three traps, each of which produces silent corruption rather than an exception if got wrong.

**Trap 1 — a `200` response to a ranged request means restart, not append.** A server that ignores `Range` returns the whole body with a `200`; appending that to a partially-downloaded `.part` file silently corrupts it and the SHA-256 check is the only thing standing between that and a session created over garbage. Only a `206` may be appended to. Log `ModelDownloadRestarted` (643) and truncate.

**Trap 2 — sessions must always be created from a file path, with exactly one scoped exception.** A `byte[]` overload holds the managed array and ORT's copy of the initializers simultaneously during creation (≈180 MB transient for fp32 MiniLM), and it degrades ORT's CoreML cache key from the model-URL hash to "hash of graph inputs and node outputs", which collides across quantization variants of one architecture. `IOnnxSessionHost` exposes **no** byte-array entry point, so a consumer cannot take the bad path. The exception is the tier-1 fixture, reached through an `InternalsVisibleTo`-scoped factory hook on `OnnxSessionHost` — both costs are measured against model size, and a 2× transient on 533 bytes is about a kilobyte.

**Trap 3 — a dropped-but-undisposed session pins the whole native graph until GC, and disposing one under an in-flight `Run` is a native access violation.** The lease is what makes both impossible. `OnnxSessionLease.Dispose()` decrements; at zero with `dropRequested` set, the session is disposed **there**.

- [ ] **Step 1: Link the fixtures into the test project**

Add to `Qavren.Edge.Onnx.Tests.csproj`:

```xml
  <ItemGroup>
    <Compile Include="..\fixtures\TinyModels.g.cs" Link="Fixtures\TinyModels.g.cs" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing tests**

`ModelStoreTests.cs`:

- Layout is `<Models>/<modelId>/<sha16>/<relativePath>` plus `<modelId>/<sha16>/.qavren-model.json` carrying each file's expected size and SHA-256, the resolved paths, the total bytes and the timestamp.
- `EnsureAsync` is idempotent and concurrency-safe (one `SemaphoreSlim` per model id); eight concurrent callers produce one provisioning and one `ProvisionedModel`.
- The fast path returns `ModelProvisioningSource.AlreadyPresent` when the marker exists and every file's length matches — full re-hashing on every launch is a startup tax paid for a case the provisioning-time verify already covers.
- `RemoveAsync` deletes the model directory **and** its `<OrtCache>/<modelId>/<sha16>` subtree, logging `OrtCachePurged` (646). Re-provisioning at a new SHA deletes the whole stale `<modelId>/<sha16>` pair.

`HttpModelSourceTests.cs` drives a loopback `HttpMessageHandler` and covers: resume-on-`206`; **restart-on-`200`** (the `.part` file is truncated, not appended to, and 643 is logged); SHA-256 mismatch (file deleted, one retry, then `EdgeModelProvisioningException(ModelHashMismatch)` carrying **both** digests); free-disk refusal before the download starts (`ModelInsufficientDiskSpace`); retry only on 5xx / 408 / 429 / `HttpRequestException` / `IOException`, `MaxAttempts` 4 with exponential backoff and jitter; `AllowDownload = false` turning a missing model into `ModelNotProvisioned` rather than a download; and `IProgress<ModelProvisioningProgress>` reporting `Resumed` correctly.

`SessionHostTests.cs` uses the `InternalsVisibleTo` byte-array hook with `TinyModels.HiddenStates` and covers: lease ref-counting; drop-while-leased (the session survives until the last lease returns, then is disposed); `MemoryPressure(Critical)` dropping and the next acquire reloading; the memory pre-flight against `StubResourceMonitor` including the **0-means-unknown** rule (0 or null skips the check and never refuses); `EdgeOnnxException(OnnxInsufficientMemory)` carrying `RequiredBytes`, `AvailableBytes`, the model id and a remediation naming the int8 preset; and signature validation — `TinyModels.NomicInputOrder` must validate (names, not positions) while a preset naming an input the graph does not declare throws `EdgeOnnxException(OnnxModelSignatureMismatch)` listing **both** name sets.

The IR-14 error path, asserted verbatim against the measured ORT message:

```csharp
[Fact]
public void IrVersionAboveOrtsCeilingFailsWithTheMessageAUserActuallyHits()
{
    var bytes = Convert.FromBase64String(TinyModels.IrVersion14);

    var ex = Assert.ThrowsAny<Exception>(() => OnnxSessionHost.CreateSessionForTests(bytes));

    Assert.Contains("Unsupported model IR version: 14", ex.Message, StringComparison.Ordinal);
    Assert.Contains("max supported IR version: 13", ex.Message, StringComparison.Ordinal);
}
```

`SessionFactoryShapeTests.cs`:

```csharp
[Fact]
public void RequireStaticInputShapesWithNoFreeDimensionOverridesIsRefused()
{
    var options = new OnnxSessionOptions();
    options.ExecutionProviders.CoreMl.RequireStaticInputShapes = true;

    var ex = Assert.Throws<EdgeOnnxException>(() => SessionOptionsFactory.Build("m", options));

    Assert.Equal(EdgeErrorCode.OnnxStaticShapesUnpinned, ex.Code);
}
```

and its sibling proving that `FreeDimensionOverrides` populated with `sequence_length` and `batch_size` both emits `AddFreeDimensionOverrideByName` twice **and** turns the flag on.

- [ ] **Step 3: Manifest, sources and the store**

`OnnxModelFileRole`, `OnnxModelFile`, `OnnxModelManifest`, `ModelProvisioningSource`, `ProvisionedModel`, `ModelProvisioningProgress`, `IOnnxModelSource`, `FileOnnxModelSource`, `BundledOnnxModelSource`, `HttpOnnxModelSourceOptions`, `HttpOnnxModelSource` and `IOnnxModelStore` exactly as §6.3 declares them.

`OnnxModelManifest.Files` is a **list**, not one entry: some ONNX mirrors split weights into external data (a 56 KB `model.onnx` beside a 90 MB `model.onnx_data`) and the sidecar must land next to the graph under its exact filename. No shipped preset does, but the type must allow it.

`Sha256` is lowercase hex, and on Hugging Face that is the git-LFS **`oid`** — never the `xetHash` those Xet-backed repos also return. Preset hashes are baked in as constants, so the runtime never asks the Hub what the hash should be.

`BundledOnnxModelSource` takes the asset opener as a delegate — in MAUI that is `FileSystem.OpenAppPackageFileAsync` — so this package never references MAUI. A copy out to disk is unavoidable: on Android a `MauiAsset` is an `AssetManager` stream with no path and no length, and §9.1 requires a real path.

The download, precisely, in `HttpOnnxModelSource`:

1. `GET` with `HttpCompletionOption.ResponseHeadersRead`, the body read under its **own** linked `CancellationTokenSource(BodyTimeout)` — `HttpClient.Timeout` stops applying once the headers are read.
2. Check free disk against the remaining bytes plus `FreeDiskMarginBytes`; short → `ModelInsufficientDiskSpace` rather than filling the device.
3. Record `ETag` and `Content-Length`; write to `<file>.part`.
4. On resume send `Range: bytes=<len>-` plus `If-Range: <etag>`. **A `200` means restart: truncate the `.part` file.** Only a `206` may be appended to.
5. `SHA256.HashDataAsync(stream, ct)` over the completed `.part` file **on disk**, never the in-flight buffer.
6. `File.Move(part, final, overwrite: true)` on the same volume, then re-assert the platform no-backup attribute — after the rename, never on the `.part` file.
7. Bounded retry with backoff and jitter. **No `Microsoft.Extensions.Http.Resilience` dependency**: a transitive Polly in a mobile embedding package to replace forty lines of bounded retry is a bad trade for consumers.

**Defaults that change behaviour — `HttpOnnxModelSourceOptions`, §6.3, transcribed in full because two of these defaults are the difference between "downloads a model" and "throws":**

```csharp
public sealed class HttpOnnxModelSourceOptions
{
    public Uri BaseAddress { get; set; } = new("https://huggingface.co/");
    /// <summary><c>{repo}/resolve/{revision}/{path}</c>.</summary>
    public string UrlTemplate { get; set; } = "{repo}/resolve/{revision}/{path}";
    public int MaxAttempts { get; set; } = 4;
    public TimeSpan BodyTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public long FreeDiskMarginBytes { get; set; } = 32L * 1024 * 1024;
    public string? BearerToken { get; set; }
    /// <summary>False turns a missing model into ModelNotProvisioned instead of a download —
    /// the switch an app flips on cellular.</summary>
    public bool AllowDownload { get; set; } = true;
}
```

`BaseAddress` and `UrlTemplate` together are the **only** place a Hugging Face URL is constructed; nothing else in SP2 concatenates one, and a consumer pointing at a mirror or a corporate proxy changes exactly these two properties. The three placeholders are positional-by-name — `{repo}`, `{revision}`, `{path}` — and `{revision}` is filled from the manifest's pinned commit SHA, never `"main"`. `ModelUrlTests.cs` asserts the composed URL for `MiniLmL6V2Int8` is `https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/1110a243fdf4706b3f48f1d95db1a4f5529b4d41/onnx/model_qint8_arm64.onnx`, and asserts a custom `BaseAddress` plus `UrlTemplate` compose correctly — which is what proves the template is honoured rather than hard-coded past. `AllowDownload = false` is asserted to raise `ModelNotProvisioned` without opening a socket.

- [ ] **Step 4: The session host**

`OnnxSessionSignature`, `OnnxSessionInfo`, `OnnxSessionLease`, `IOnnxSessionHost`, `OnnxSessionOptions` exactly as §6.5.

`OnnxSessionHost` keeps a `ConcurrentDictionary<string, SessionSlot>`; a slot holds the `InferenceSession`, the `OnnxSessionInfo`, a lease count, a `dropRequested` flag and a `SemaphoreSlim(1)` load gate. **The dictionary key is the model id plus, when `FreeDimensionOverrides` is non-empty, a canonical rendering of those overrides** — a pinned shape is baked into the session, so two different pinned shapes are two different sessions and the key has to say so rather than handing the second caller a session pinned to the first one's shape. With the default (empty) overrides the key is just the model id.

`AcquireAsync(modelId)`:

1. `await host.EnsureStartedAsync(ct)` — SP1's contract, so a startup failure surfaces here with its real cause.
2. Fast path: under the slot lock, if loaded and not marked for drop, increment the lease and return.
3. Slow path, under the load gate so N concurrent first-callers produce one session: `IOnnxModelStore.EnsureAsync` → memory pre-flight → build `SessionOptions` → apply the EP policy → `new InferenceSession(graphPath, sessionOptions)` → validate the signature → publish → lease.

**Memory pre-flight.** Before `new InferenceSession`, read `IEdgeResourceMonitor.Read().AvailableMemoryBytes`. When it is non-null and below `manifest.TotalSizeBytes * MemoryHeadroomFactor + MemoryHeadroomBytes`, throw `EdgeOnnxException(OnnxInsufficientMemory)`. A null or zero reading means "unknown" and skips the check — it is never used to refuse. Apple publishes no jetsam table and the `com.apple.developer.kernel.increased-memory-limit` entitlement moves the ceiling per app, so there are no hard-coded device tiers. `MemoryHeadroomFactor = 2.5` and `MemoryHeadroomBytes = 48 MB` are engineering estimates anchored on the ~2× transient cost of session creation, **not measurements** — Open risk 3.

**Signature validation is eager.** After creation, read `InputMetadata`/`OutputMetadata`. Every name the preset references must exist; every input the graph declares must be one we can supply (ORT requires every declared input to be fed, so an unrecognised one can never be satisfied); the output must exist and, when its last dimension is static, equal the declared dimensions.

`SetLoadCancellationFlag(true)` is wired to **both** the `AcquireAsync` cancellation token **and** `MemoryPressure(Critical)` arriving mid-load, so a multi-second CoreML compile aborts cooperatively rather than running to completion into a kill.

`ShareThreadPool` (default true once more than one model is registered) calls `SessionOptions.DisablePerSessionThreads()`.

Deliberately unused, and a reviewer should expect to see none of them: `IOBinding`, `RunAsync`, `PrePackedWeightsContainer`, `CreateTensorValueFromSystemNumericsTensorObject` (`[Experimental("SYSLIB5001")]`, which would leak to consumers) and `.ort` format models.

- [ ] **Step 5: `InternalsVisibleTo` and the test-only factory hook**

`Properties\AssemblyInfo.cs`:

```csharp
using System.Runtime.CompilerServices;

// The tier-1 fixtures are 533-831-byte base64 graphs created with new InferenceSession(byte[]).
// That constructor is deliberately absent from IOnnxSessionHost (spec 9.1) so no consumer can
// take the path that doubles transient memory and weakens the CoreML cache key; both costs are
// measured against model size and vanish at this size. The hook is internal and stays internal.
[assembly: InternalsVisibleTo("Qavren.Edge.Onnx.Tests")]
[assembly: InternalsVisibleTo("Qavren.Edge.Embeddings.Tests")]
```

- [ ] **Step 6: Builder extensions, observer and diagnostics completion**

Add `AddOnnxModel`, `AddModelSource<TSource>`, `AddModelSource(factory)`, `AddHuggingFaceModelSource`, `AddBundledModelSource`, `ProvisionModelAtStartup` and `WarmUpSessionAtStartup` exactly as §6.5 declares them. `AddOnnx` registers `FileOnnxModelSource` **last** as the fallback; sources are probed in registration order.

`ProvisionModelAtStartup` (order 210) verifies **presence** and faults startup if the model is missing. It is off by default and **refused for HTTP-only models**: blocking `IEdgeHost.Started` on a download also blocks every `IEdgeDatabase.OpenConnectionAsync`.

`WarmUpSessionAtStartup` (order 220) creates the session and **runs no inference** — a batch needs a tokenizer and a generator and both live in L1. Its load-plus-batch sibling is `WarmUpEmbeddingsAtStartup`, registered by `Qavren.Edge.Embeddings.Onnx` at the same order 220 (Task 4.1). Both off by default, because either one forces provisioning and §10 keeps that lazy.

Complete `OnnxLifecycleObserver`: `MemoryPressure(Critical)` → `IOnnxSessionHost.DropAsync()` and `SetLoadCancellationFlag(true)` on any load in flight, waiting **at most two seconds** for the drain and returning regardless — the hub awaits observers, and an iOS memory warning is not a place to block. `Stopping` → drops every session.

Complete `OnnxDiagnosticsContributor` with the per-session keys: `session[<id>].{graphPath, sha256, executionProviderAccepted, executionProviderAttempts, inputNames, outputNames, loadMs, loadCount, leases, loaded}`, plus `provisionedModels`.

- [ ] **Step 7: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp2\embeddings\tests\Qavren.Edge.Onnx.Tests\Qavren.Edge.Onnx.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0 `
  -p:ArtifactsPath="D:\Local\Temp\qedge-sp2\w3\t31"
```

Expected: `Passed! - Failed: 0`, exit code 0. This run creates real ORT CPU sessions over the base64 fixtures on this box.

---

### Task 3.2: `Qavren.Edge.VectorData` part 1 — collection model, `EdgeVectorSchema`, filter translator

**Local-verifiable:** yes.

**Files:**
- Create: `embeddings\src\Qavren.Edge.VectorData\EdgeVectorData.cs`
- Create: `embeddings\src\Qavren.Edge.VectorData\EdgeVectorStoreOptions.cs`
- Create: `embeddings\src\Qavren.Edge.VectorData\VectorDataExceptions.cs`
- Create: `embeddings\src\Qavren.Edge.VectorData\EdgeVectorSchema.cs`
- Create: `embeddings\src\Qavren.Edge.VectorData\Internal\EdgeCollectionModelBuilder.cs`
- Create: `embeddings\src\Qavren.Edge.VectorData\Internal\EdgeFilterTranslator.cs`
- Create: `embeddings\src\Qavren.Edge.VectorData\Internal\SqliteTypeMap.cs`
- Test: `embeddings\tests\Qavren.Edge.VectorData.Tests\GoldenSqlTests.cs`
- Test: `embeddings\tests\Qavren.Edge.VectorData.Tests\CollectionModelTests.cs`
- Test: `embeddings\tests\Qavren.Edge.VectorData.Tests\FilterTranslatorTests.cs`
- Test: `embeddings\tests\Qavren.Edge.VectorData.Tests\Records\Note.cs`

**Approach — read this before writing.** Everything in this task is pure string and model work: no connection is opened, no SQL is executed. That is what makes it a golden-SQL task, and the golden strings below are the **expected values**, not illustrations.

The one decision that shapes all of it: **filters are pushed into `vec0` as `rowid IN (SELECT "_rowid" FROM <data table> WHERE …)`.** `vec0BestIndex` assigns an `argvIndex` **and** sets `omit = 1` for a `rowid IN (…)` constraint via `sqlite3_vtab_in` (SQLite ≥ 3.38; SP1 pins 3.53.4), and `vec0Filter_knn_chunks_iter` AND-s that rowid bitmap into the chunk validity bitmap *before* distances are computed. So the whole MEVD `Filter` expression becomes ordinary SQL over the data table — `OR`, `LIKE`, `IS NULL`, indexes — and still behaves as a true pre-filter. That single choice deletes the entire `vec0` metadata/auxiliary column tier, and it is why **no vec0 metadata, auxiliary or partition columns are ever emitted**. SP1's `VecTable` keeps its support for them; SP2 simply does not use it.

It never degrades to a client-side post-filter. With a genuine pre-filter available, degrading would change *which* k rows come back, not merely how many.

- [ ] **Step 1: Write the record used by every golden test**

`embeddings\tests\Qavren.Edge.VectorData.Tests\Records\Note.cs` — the §4.3 record verbatim, because every golden string below is derived from it:

```csharp
using Microsoft.Extensions.VectorData;

namespace Qavren.Edge.VectorData.Tests.Records;

public sealed class Note
{
    [VectorStoreKey] public string Key { get; set; } = "";
    [VectorStoreData(IsIndexed = true)] public string? Tag { get; set; }
    [VectorStoreData(IsFullTextIndexed = true)] public string Title { get; set; } = "";
    [VectorStoreData(IsFullTextIndexed = true)] public string Body { get; set; } = "";
    [VectorStoreVector(384, DistanceFunction = DistanceFunction.CosineDistance)]
    public string? Embedding => Body;          // string source -> the generator fills it
}
```

- [ ] **Step 2: Write the failing golden-SQL tests**

`GoldenSqlTests.cs` asserts `EdgeVectorSchema.BuildCreateSql()` byte for byte. For the `Note` record above, collection `notes`, the emitted statements are, in order:

```sql
CREATE TABLE IF NOT EXISTS "notes" (
  "_rowid" INTEGER PRIMARY KEY,
  "Key"    TEXT NOT NULL,
  "Tag"    TEXT,
  "Title"  TEXT,
  "Body"   TEXT
)
```
```sql
CREATE UNIQUE INDEX IF NOT EXISTS "notes_Key_index" ON "notes"("Key")
```
```sql
CREATE INDEX IF NOT EXISTS "notes_Tag_index" ON "notes"("Tag")
```
```sql
CREATE VIRTUAL TABLE IF NOT EXISTS "notes_vec" USING vec0(embedding float[384] distance_metric=cosine, chunk_size=256)
```
```sql
CREATE VIRTUAL TABLE IF NOT EXISTS "notes_fts" USING fts5(Title, Body, content='notes', content_rowid='_rowid', tokenize='unicode61 remove_diacritics 2')
```

then the three FTS sync triggers from `FtsTable.BuildSyncTriggerSql("notes_fts", "notes", ["Title","Body"], "_rowid")` (Task 2.1), then the vec0 cascade, which is a trigger and not provider code because **vec0 has no foreign keys**:

```sql
CREATE TRIGGER IF NOT EXISTS "notes_vec_ad" AFTER DELETE ON "notes" BEGIN
  DELETE FROM "notes_vec" WHERE rowid = old."_rowid"; END
```

The vec0 DDL comes from SP1's `VecTable.BuildCreateSql`, unchanged, and the FTS5 DDL from SP1's `FtsTable` with the new options overload — SP2 emits neither by hand. The vec0 table is keyed on its **implicit** rowid; there is no declared `rowid` column in the DDL, and rows are written with `INSERT INTO "notes_vec"(rowid, embedding)`. `INTEGER PRIMARY KEY` on the data table is a true rowid alias, stable across `ON CONFLICT DO UPDATE`, so `content_rowid='_rowid'` is the same integer FTS5 already indexes on.

`BuildKnnSql(hasFilter, hasScoreThreshold, includeVectors)` — all eight combinations asserted. With everything on:

```sql
SELECT d."_rowid", d."Key", d."Tag", d."Title", d."Body", v.distance, v."embedding"
FROM "notes_vec" v
JOIN "notes" d ON d."_rowid" = v.rowid
WHERE v."embedding" MATCH $query AND v.k = $k
  AND v.rowid IN (SELECT "_rowid" FROM "notes" WHERE <filter>)
  AND v.distance <= $scoreThreshold
ORDER BY v.distance
```

`BuildHybridRrfSql(hasFilter, includeVectors)` — all four combinations asserted. With both on:

```sql
WITH vec AS (
  SELECT v.rowid AS id,
         ROW_NUMBER() OVER (ORDER BY v.distance) AS rank,
         v.distance AS distance
  FROM "notes_vec" v
  WHERE v."embedding" MATCH $query AND v.k = $cand
    AND v.rowid IN (SELECT "_rowid" FROM "notes" WHERE <filter>)
),
fts AS (
  SELECT f.rowid AS id,
         ROW_NUMBER() OVER (ORDER BY f.rank) AS rank,
         f.rank AS bm25
  FROM "notes_fts" f
  WHERE f MATCH $keywords
    AND f.rowid IN (SELECT "_rowid" FROM "notes" WHERE <filter>)
  ORDER BY f.rank
  LIMIT $cand
)
SELECT d."_rowid", d."Key", d."Tag", d."Title", d."Body",
       COALESCE(1.0 / ($rrfK + fts.rank), 0.0) * $wKeyword
     + COALESCE(1.0 / ($rrfK + vec.rank), 0.0) * $wVector AS score,
       vec.distance, fts.bm25, nv."embedding"
FROM fts
FULL OUTER JOIN vec ON vec.id = fts.id
JOIN "notes" d ON d."_rowid" = COALESCE(fts.id, vec.id)
LEFT JOIN "notes_vec" nv ON nv.rowid = d."_rowid"
ORDER BY score DESC
LIMIT $top OFFSET $skip
```

Four things in that query are traps and each has its own test:

1. **`f MATCH $keywords`, not `"notes_fts" MATCH $keywords`.** FTS5's `<table> MATCH <expr>` form names the table, and once a table is aliased in SQLite the original name is out of scope — so inside a CTE that says `FROM "notes_fts" f` the alias is the only spelling that parses.
2. **The `IncludeVectors` bracket appears twice and both halves are required.** The `LEFT JOIN` makes `nv` available and the `, nv."embedding"` projects it. A join with no matching projection costs a join, returns no vectors, and throws nothing — because `VectorSearchResult` simply leaves the vector property at its default. The golden test covers `IncludeVectors` on and off; Task 5.1's behavioural test is what actually makes a half-applied bracket fail.
3. **Both lanes rank ascending.** FTS5's `bm25()` is the standard score multiplied by −1, so better matches are numerically **lower** and `ORDER BY rank` is ascending-best-first; vec0's `distance` is a distance, so it is ascending too. The hidden `rank` column is used rather than calling `bm25(f)` directly, which SQLite's own docs say is faster.
4. **The fused score is a similarity — higher is better — the opposite polarity to `SearchAsync`.** Asserted in a test whose *name* states the inversion.

`BuildDropSql()` — asserted byte for byte and **in this order**, which is the reverse of `BuildCreateSql`'s. The triggers go first because a trigger on a dropped table is an error on some paths and a silent orphan on others; the FTS5 sidecar goes before its content table because dropping an external-content table first leaves FTS5's shadow tables referring to nothing:

```sql
DROP TRIGGER IF EXISTS "notes_vec_ad"
```
```sql
DROP TRIGGER IF EXISTS "notes_fts_ad"
```
```sql
DROP TRIGGER IF EXISTS "notes_fts_au"
```
```sql
DROP TRIGGER IF EXISTS "notes_fts_ai"
```
```sql
DROP TABLE IF EXISTS "notes_fts"
```
```sql
DROP TABLE IF EXISTS "notes_vec"
```
```sql
DROP TABLE IF EXISTS "notes"
```

The collection without a full-text property emits the same list minus the three `notes_fts_*` triggers and `DROP TABLE IF EXISTS "notes_fts"`, and that variant is asserted too — `FullTextTable` is nullable in §8 and an unconditional `DROP TABLE IF EXISTS ""` would be a syntax error rather than a no-op. Dropping the `vec0` virtual table also drops its four shadow tables; SP2 names none of them, because `DROP TABLE` on the virtual table is what `vec0`'s own destructor is for.

`BuildUpsertSql()`:

```sql
INSERT INTO "notes"("Key","Tag","Title","Body") VALUES ($Key,$Tag,$Title,$Body)
  ON CONFLICT("Key") DO UPDATE SET "Tag"=excluded."Tag","Title"=excluded."Title","Body"=excluded."Body"
  RETURNING "_rowid"
```

`BuildMatchExpression` — keyword escaping is ours and is an injection surface. Each keyword becomes a double-quoted FTS5 string literal with internal `"` doubled, joined with ` OR ` (or ` AND `). Quoting makes FTS5 treat the token as a phrase, so `AND`, `OR`, `NOT`, `NEAR`, `*`, `^`, `:` and parentheses inside a keyword are inert rather than operators:

```csharp
[Fact]
public void KeywordsAreQuotedSoFts5OperatorsInsideThemAreInert()
{
    Assert.Equal(
        "\"a\"\"b\" OR \"OR\" OR \"x*\"",
        EdgeVectorSchema.BuildMatchExpression(["a\"b", "OR", "x*"], KeywordCombinator.Or));
}

[Fact]
public void ColumnFilterNarrowsTheExpression()
{
    Assert.Equal(
        "{Body} : (\"term one\" OR \"term two\")",
        EdgeVectorSchema.BuildMatchExpression(["term one", "term two"], KeywordCombinator.Or, "Body"));
}
```

- [ ] **Step 3: `EdgeVectorData`, options and exceptions**

`EdgeVectorData.QueryGeneratorServiceKey` is `public const string` = `"qavren.edge.query"`. It is a **deliberate duplicate** of `EdgeEmbeddings.QueryServiceKey`: §2 decision 2 forbids this package from referencing `Qavren.Edge.Embeddings.Onnx`, so the symbol is not visible here, and a shared constant would need a third assembly or an SP1 edit — §5 allows neither. Task 5.1 asserts the two literals are equal from the one test project that references both. The XML doc must say all of this, because the next reader's instinct will be to "de-duplicate" it.

`EdgeVectorStoreOptions`, `RrfDefaults`, `EdgeVectorStoreCollectionOptions`, `EdgeHybridSearchOptions<TRecord>` exactly as §8.

**Defaults that change behaviour.** Two option types carry defaults that silently decide what SQL is emitted and how results are ordered, so both are transcribed. The FTS trio composes into one `tokenize=` clause and the RRF quartet composes into the fused score, and in each case getting one member wrong produces a working query with wrong results:

```csharp
public sealed class EdgeVectorStoreOptions
{
    public string? DatabaseName { get; set; }
    public IEmbeddingGenerator? EmbeddingGenerator { get; set; }
    public IEmbeddingGenerator? QueryEmbeddingGenerator { get; set; }
    /// <summary><c>{0}</c> = collection name.</summary>
    public string VectorTableNameFormat { get; set; } = "{0}_vec";
    public string FullTextTableNameFormat { get; set; } = "{0}_fts";
    public int ChunkSize { get; set; } = 256;
    public FtsTokenizer FullTextTokenizer { get; set; } = FtsTokenizer.Unicode61;
    public int FullTextRemoveDiacritics { get; set; } = 2;
    public KeywordCombinator KeywordCombinator { get; set; } = KeywordCombinator.Or;
    public RrfDefaults Rrf { get; } = new();
    /// <summary>Counting rows is one query per collection; off by default.</summary>
    public bool IncludeRowCountsInDiagnostics { get; set; }
}

public sealed class RrfDefaults
{
    /// <summary>The RRF smoothing constant. 60 is the value sqlite-vec's own reference uses.</summary>
    public int K { get; set; } = 60;
    public double VectorWeight { get; set; } = 1.0;
    public double KeywordWeight { get; set; } = 1.0;
    /// <summary>Each lane fetches (top + skip) * this, capped at 4096.</summary>
    public int CandidateMultiplier { get; set; } = 4;
}
```

`FullTextTokenizer = Unicode61` and `FullTextRemoveDiacritics = 2` are one decision in two properties: they are what `FtsTable`'s new options overload (Task 2.1) renders as `tokenize='unicode61 remove_diacritics 2'` in the golden DDL above, and `remove_diacritics 2` is the only value that folds diacritics correctly across the full Unicode range — `1` is the legacy mode that mishandles several Latin-1 ranges, and `0` leaves `café` and `cafe` as different tokens. The golden-SQL test asserts the rendered clause, so changing either default breaks a byte-for-byte assertion rather than quietly changing what matches.

`VectorWeight` and `KeywordWeight` at `1.0` each are what make the emitted `$wVector` / `$wKeyword` parameters an unweighted fusion — the neutral starting point every RRF paper uses, and the two knobs a consumer turns to bias toward semantic or lexical matching. `CandidateMultiplier = 4` is what fills `$cand = min((top + skip) * 4, 4096)`: fewer candidates per lane and a row that ranks 5th in one lane and 40th in the other never enters the fusion at all, which is the failure mode RRF exists to avoid. `GoldenSqlTests.cs` asserts the three parameter **values** bound for a default-configured store (`$rrfK = 60`, `$wVector = $wKeyword = 1.0`, `$cand = (top + skip) * 4` capped at 4096) alongside the SQL text, because the SQL is parameterised and a wrong default would not change a single character of it.

`EdgeVectorStoreException` derives from **MEVD's `VectorStoreException`**, not from `EdgeException` — the one deliberate break in the suite's hierarchy. The conformance suite asserts the MEVD type, and Semantic Kernel, Agent Framework and generic retry middleware all catch it; an Edge-only hierarchy would be invisible to every one of them. It still carries the `EdgeErrorCode` and sets the docs `HelpLink` explicitly, so Qavren's error contract holds on both sides. `VectorStoreSystemName` is `"sqlite"` (the OTel `db.system.name` value, matching upstream so a trace looks the same either side of a migration) and `OperationName` comes from a fixed vocabulary: `CreateCollection`, `DeleteCollection`, `Get`, `Upsert`, `Delete`, `VectorSearch`, `HybridSearch`, `ListCollectionNames`. Note .NET MEVD 10.x has no `VectorStoreOperationException`; that type exists only in Semantic Kernel's Python SDK.

`EdgeVectorModelException` derives from `EdgeException` and is raised while **building** the model or validating the schema, **before any SQL runs**.

- [ ] **Step 4: `EdgeCollectionModelBuilder`**

`CollectionModelBuildingOptions { SupportsMultipleVectors = false, RequiresAtLeastOneVector = true, ReservedKeyStorageName = "_rowid" }`, implementing `IsDataPropertyTypeValid` and `IsVectorPropertyTypeValid` and overriding `ValidateKeyProperty`, `ValidateProperty`, `SupportsKeyAutoGeneration` and **`EmbeddingGenerationDispatchers`**.

That last override is not bookkeeping. `CollectionModelBuilder` exposes `protected virtual IReadOnlyList<EmbeddingGenerationDispatcher> EmbeddingGenerationDispatchers { get; }`, and it is the seam by which a provider declares which `Embedding` subtypes it can store. SP2 returns exactly one — `EmbeddingGenerationDispatcher.Create<Embedding<float>>()` — which is what makes §4.3's `string` source property resolve to `Embedding<float>`, and what makes a generator producing `Embedding<sbyte>` or `BinaryEmbedding` fail at **model-build** time with a named error rather than at the vec0 insert. The single-entry list is the mechanical expression of "float32 only": vec0's `int8` and `bit` element types are cut from v1, and this is the one place the cut is enforced rather than merely documented.

**`"_rowid"` is reserved against every property, not just the key.** It is emitted into the same namespace as every user property's storage name, so a record with a property called `_rowid` — or with `[VectorStoreData(StorageName = "_rowid")]` — would otherwise produce a duplicate-column `CREATE TABLE` at runtime, which is exactly the failure class §15.2 promises to catch before any SQL runs. Two guards: MEVD's own `ReservedKeyStorageName` option, which reserves it against the *key*; and an explicit ordinal-ignore-case check in `ValidateProperty` over key, data **and** vector properties. Either raises `EdgeVectorModelException(ReservedColumnName)` naming the property and the reserved name. The name is not configurable — it appears in `content_rowid='_rowid'`, in four trigger bodies and in every query above, and a configurable one would buy nothing but a second thing to get wrong.

Type map (`SqliteTypeMap`):

| MEVD / CLR | SQLite |
|---|---|
| `int`, `long`, `short`, `bool` | `INTEGER` |
| `float`, `double` | `REAL` |
| `string`, `Guid` | `TEXT` (Guid upper-cased, matching Microsoft.Data.Sqlite) |
| `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly` | `TEXT`, ISO 8601 |
| `byte[]` | `BLOB` |
| anything else | `EdgeVectorModelException(UnsupportedPropertyType)` naming the property and the supported set |

Keys: `int`, `long`, `string`, `Guid`. Auto-generated keys: `Guid` via `Guid.CreateVersion7()` client-side (time-ordered, so TEXT keys cluster); `int`/`long` left to SQLite and read back with `RETURNING`.

Vector property CLR types: `ReadOnlyMemory<float>`, `ReadOnlyMemory<float>?`, `Embedding<float>`, `float[]`, and `string` with a generator configured. float32 only. A **nullable** vector property is rejected at model-build time with `NullableVectorProperty`: vec0 overloads SQL `NULL` on a vector column to mean "no change", so `UPDATE … SET embedding = NULL` is a silent no-op rather than an error — the trap is made unreachable, not reported.

Distance functions: `CosineDistance` (default) → `cosine`, `EuclideanDistance` → `l2`, `ManhattanDistance` → `l1`. Everything else — `CosineSimilarity`, both dot products, `HammingDistance`, `EuclideanSquaredDistance` — throws `UnsupportedDistanceFunction` naming the three that work. `IndexKind` is accepted and **ignored with a logged warning** (`IndexKindIgnored`, 804): vec0 is brute-force and flat, and silently accepting `Hnsw` while scanning linearly is worse than saying so. `SupportsMultipleVectors = false` throws `MultipleVectorPropertiesUnsupported` rather than silently picking one.

**The dimension ceiling is SP1's, and SP2 writes no check of its own.** §12.2: "Declared dimensions above 8192 are rejected up front by SP1's `VecTable` (`SQLITE_VEC_VEC0_MAX_DIMENSIONS`)." That is a statement about *ownership*, and it is the reason §15.1 allocates this package thirteen error codes and none of them means "too many dimensions". `VecTable.BuildCreateSql` already opens with `ArgumentOutOfRangeException.ThrowIfLessThan(dims, 1)` and `ArgumentOutOfRangeException.ThrowIfGreaterThan(dims, MaxDimensions)` against its own `private const int MaxDimensions = 8192; // SQLITE_VEC_VEC0_MAX_DIMENSIONS` (verified in the merged `foundation\src\Qavren.Edge.Sqlite\Vec\VecTable.cs`). `EdgeVectorSchema.BuildCreateSql` calls straight through to it with the model's declared width and **does not pre-validate, re-message or wrap** — an implementer who adds a dimension guard to `EdgeCollectionModelBuilder`, or converts the `ArgumentOutOfRangeException` into an `EdgeVectorModelException`, has created a second place the ceiling is written down and the two will drift the first time sqlite-vec raises it.

The one thing this task does owe is proof that the ceiling is actually reached — that the declared width flows into `VecTable` rather than being truncated, clamped or ignored on the way. That is a test, not code:

```csharp
[Fact]
public void ADeclaredWidthAbove8192IsRejectedBySp1sVecTableAndNotByUs()
{
    // SQLITE_VEC_VEC0_MAX_DIMENSIONS. Deliberately an ArgumentOutOfRangeException from
    // Qavren.Edge.Sqlite and NOT an EdgeVectorModelException: spec 12.2 puts this ceiling in
    // SP1's VecTable, and spec 15.1 allocates this package no code for it. If this test ever
    // starts seeing an EdgeVectorModelException, someone has written a second ceiling.
    var ex = Assert.Throws<ArgumentOutOfRangeException>(
        () => EdgeVectorSchema.BuildCreateSql(ModelFor<WideVector>(), "wide", new EdgeVectorStoreOptions()));

    Assert.Equal("dims", ex.ParamName);
    Assert.Contains("8192", ex.Message, StringComparison.Ordinal);
}

[Fact]
public void ADeclaredWidthOfExactly8192IsAccepted()
{
    var sql = EdgeVectorSchema.BuildCreateSql(ModelFor<EdgeWidthVector>(), "edge", new EdgeVectorStoreOptions());

    Assert.Contains("float[8192]", string.Join("\n", sql), StringComparison.Ordinal);
}
```

where `WideVector` declares `[VectorStoreVector(8193)]` and `EdgeWidthVector` declares `[VectorStoreVector(8192)]`. The pair matters: the accept case is what proves the rejection is the ceiling rather than an off-by-one or a blanket refusal of anything wide.

`CollectionModelTests.cs` asserts every rejection above by its `EdgeErrorCode` — `UnsupportedPropertyType`, `UnsupportedKeyType`, `UnsupportedDistanceFunction`, `NullableVectorProperty`, `MultipleVectorPropertiesUnsupported`, `ReservedColumnName` — plus the two `ArgumentOutOfRangeException` dimension cases above, and specifically the two `_rowid` cases — a record with a `_rowid` property and one with `[VectorStoreData(StorageName = "_rowid")]` — raise `ReservedColumnName` naming the property rather than reaching SQL.

- [ ] **Step 5: `EdgeFilterTranslator`**

Derives from MEVD's `FilterTranslatorBase` with `FilterPreprocessingOptions { SupportsParameterization = true }`. Supported: `== != < <= > >=`, `&& || !`, `is null` / `is not null`, member access bound to model properties, `Contains` over an inline array or a captured `IEnumerable` (→ `IN (@p1, @p2, …)`), `string.StartsWith`/`EndsWith`/`Contains` (→ `LIKE` with escaped `%` and `_`), and `Convert` unwrapping. Captured values become parameters; constants are inlined with SQLite literal formatting.

Anything else — an unmodelled method call, `Enumerable.Any`, a property mapping to no column — throws `NotSupportedException` **naming the node and the property**, which is what the conformance suite expects. `FilterTranslatorTests.cs` asserts every supported node's emitted SQL and every rejected node's message.

- [ ] **Step 6: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp2\embeddings\tests\Qavren.Edge.VectorData.Tests\Qavren.Edge.VectorData.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0 `
  -p:ArtifactsPath="D:\Local\Temp\qedge-sp2\w3\t32"
```

Expected: `Passed! - Failed: 0`, exit code 0.

---

### Task 3.3: Preset manifests — the `paths-info` fetch script and the literal constants

**Local-verifiable:** yes. Every value below was produced on this box on 2026-09-11 by running the script that follows.

**Files:**
- Create: `embeddings\tools\model-hashes\fetch_preset_hashes.py`
- Create: `embeddings\tools\model-hashes\requirements.txt`
- Create: `embeddings\tools\model-hashes\README.md`
- Create: `embeddings\src\Qavren.Edge.Embeddings.Onnx\EmbeddingPresets.g.cs` *(generated, committed)*

**Approach — read this before writing.** §10.2 says "Preset hashes are baked in as constants and regenerated by a checked-in script, so the runtime never asks the Hub what the hash should be." Both halves of that sentence were missing from an earlier draft of this plan: no task created the script, and neither the spec nor the plan contained a single per-file SHA-256, size or revision. §7 declares `EmbeddingPreset`'s fields `required` — which is a schema, not data — so an implementer handed only §7 would have had to invent four manifests or go to the network with no stated procedure. This task is the missing half.

It is in **wave 3 and not wave 4** for a scheduling reason and a safety one. Scheduling: Task 4.1 consumes the generated file, so it has to exist a wave earlier. Safety: this task compiles nothing — it writes one Python program and one `.cs` file into a project that **nothing in wave 3 builds** — so it is the only kind of task that can sit beside two compiling tasks with no `ArtifactsPath` of its own.

**Why one `POST …/paths-info/…` per repo and not a `HEAD` per file.** §10.2 is explicit about the bucket: the anonymous API bucket is 500 requests per five minutes against 3,000 for resolvers. `paths-info` answers for every file in one request and returns the LFS metadata directly; the resolver would answer with headers only and would be the wrong bucket for metadata.

**The trap the spec does not name, and the reason this task has a script rather than a table.** §10.2 says the SHA-256 "comes from the git-LFS `oid` (which **is** the SHA-256)". Measured: that holds for `onnx/*.onnx`, which are LFS pointers whose response carries `lfs: { oid, size }` with a 64-hex digest. It does **not** hold for `vocab.txt`, which is a plain git blob in all three repos — `paths-info` reports `fb140275c155a9c7c5a3b3e0e77a9e839594a938`, which is 40 hex characters and is the git blob **SHA-1**. Baking that into a manifest would make `IOnnxModelStore.EnsureAsync` fail every provisioning with `ModelHashMismatch` against a digest that was never a SHA-256 in the first place. The script branches on the presence of the `lfs` sub-object and downloads-and-hashes when it is absent. That is plan adjustment 19.

- [ ] **Step 1: `requirements.txt`**

```
# Deliberately EMPTY of third-party packages. The script uses urllib, hashlib and json from the
# standard library only, so the venv exists to pin the interpreter and nothing else. A pinned
# requests/huggingface_hub here would be three more supply-chain edges for one POST and one GET.
# Python 3.14, matching the fixture venv.
```

- [ ] **Step 2: `README.md`**

Three lines: the regeneration command, that the script is **never** a build step and CI never runs it, and that `EmbeddingPresets.g.cs` is committed. Plus the one warning worth repeating: **a revision is a full commit SHA, never `main`** — a moving revision silently changes the vectors, and nothing in the type system or the test suite would notice.

- [ ] **Step 3: Write `embeddings\tools\model-hashes\fetch_preset_hashes.py`**

Verbatim. This is the exact program that produced the committed file.

```python
#!/usr/bin/env python3
"""Regenerate EmbeddingPresets.g.cs from Hugging Face.

NEVER a build step. Run by hand, then commit the emitted file.

    uv venv --python 3.14 .venv
    uv pip install --python .venv/Scripts/python.exe -r requirements.txt
    .venv/Scripts/python.exe fetch_preset_hashes.py \
        --out ../../src/Qavren.Edge.Embeddings.Onnx/EmbeddingPresets.g.cs

Two rules this script exists to enforce, both from spec 10.2:

  * ONE `POST /api/models/{repo}/paths-info/{rev}` per repo, never one HTTP request per file.
    The anonymous API bucket is 500 per five minutes; the resolver bucket is 3,000. Asking the
    resolver for headers per file would work and would be the wrong bucket.
  * The SHA-256 comes from the git-LFS `oid`, never from `xetHash`. These repos are Xet-backed
    and return both.

And the trap that is NOT in the spec (plan adjustment 19): `oid` is the SHA-256 only when the
response carries an `lfs` sub-object. A plain git blob -- which is what vocab.txt is in all three
repos -- reports the 40-hex git SHA-1 in the top-level `oid`. Writing that into a manifest makes
every provisioning verify fail at runtime with a mismatch nobody can debug, so a non-LFS file is
downloaded and hashed here instead. vocab.txt is 231 KB and this script runs by hand.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import urllib.request

API = "https://huggingface.co/api/models"
RESOLVE = "https://huggingface.co/{repo}/resolve/{rev}/{path}"

# Revision is a FULL commit SHA, never "main": a moving revision silently changes the vectors.
PRESETS = [
    {
        "field": "MiniLmL6V2Int8",
        "model_id": "all-minilm-l6-v2-int8",
        "repo": "sentence-transformers/all-MiniLM-L6-v2",
        "rev": "1110a243fdf4706b3f48f1d95db1a4f5529b4d41",
        "graph": "onnx/model_qint8_arm64.onnx",
        "vocab": "vocab.txt",
        "license": "Apache-2.0",
    },
    {
        "field": "MiniLmL6V2Fp32",
        "model_id": "all-minilm-l6-v2-fp32",
        "repo": "sentence-transformers/all-MiniLM-L6-v2",
        "rev": "1110a243fdf4706b3f48f1d95db1a4f5529b4d41",
        "graph": "onnx/model.onnx",
        "vocab": "vocab.txt",
        "license": "Apache-2.0",
    },
    {
        "field": "BgeSmallEnV15",
        "model_id": "bge-small-en-v1.5",
        "repo": "BAAI/bge-small-en-v1.5",
        "rev": "5c38ec7c405ec4b44b94cc5a9bb96e735b38267a",
        "graph": "onnx/model.onnx",
        "vocab": "vocab.txt",
        "license": "MIT",
    },
    {
        "field": "NomicEmbedTextV15Int8",
        "model_id": "nomic-embed-text-v1.5-int8",
        "repo": "nomic-ai/nomic-embed-text-v1.5",
        "rev": "e9b6763023c676ca8431644204f50c2b100d9aab",
        "graph": "onnx/model_quantized.onnx",
        "vocab": "vocab.txt",
        "license": "Apache-2.0",
    },
]


def paths_info(repo: str, rev: str, paths: list[str]) -> dict[str, dict]:
    body = json.dumps({"paths": paths}).encode("utf-8")
    req = urllib.request.Request(
        f"{API}/{repo}/paths-info/{rev}",
        data=body,
        headers={"Content-Type": "application/json", "User-Agent": "qavren-edge-preset-hashes/1"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=60) as response:
        entries = json.load(response)
    return {e["path"]: e for e in entries}


def digest(repo: str, rev: str, entry: dict) -> tuple[int, str]:
    """(size, sha256). LFS: straight from the oid. Plain blob: download and hash."""
    lfs = entry.get("lfs")
    if lfs:
        oid = lfs["oid"]
        if len(oid) != 64:
            raise SystemExit(f"{entry['path']}: lfs.oid is not a sha256: {oid}")
        return int(lfs["size"]), oid
    url = RESOLVE.format(repo=repo, rev=rev, path=entry["path"])
    req = urllib.request.Request(url, headers={"User-Agent": "qavren-edge-preset-hashes/1"})
    with urllib.request.urlopen(req, timeout=120) as response:
        payload = response.read()
    if len(payload) != entry["size"]:
        raise SystemExit(f"{entry['path']}: got {len(payload)} bytes, API said {entry['size']}")
    return len(payload), hashlib.sha256(payload).hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    resolved = []
    cache: dict[tuple[str, str, str], tuple[int, str]] = {}
    for preset in PRESETS:
        info = paths_info(preset["repo"], preset["rev"], [preset["graph"], preset["vocab"]])
        files = []
        for path, role in ((preset["graph"], "Graph"), (preset["vocab"], "Vocabulary")):
            key = (preset["repo"], preset["rev"], path)
            if key not in cache:
                cache[key] = digest(preset["repo"], preset["rev"], info[path])
            size, sha = cache[key]
            files.append((path, role, size, sha))
        resolved.append((preset, files))

    lines = [
        "// <auto-generated>",
        "// Regenerate with embeddings/tools/model-hashes/fetch_preset_hashes.py.",
        "// NEVER a build step. Every SHA-256 below is the git-LFS oid for an LFS-backed file and",
        "// a locally computed digest for a plain git blob -- see the script's docstring.",
        "// </auto-generated>",
        "",
        "namespace Qavren.Edge.Embeddings.Onnx;",
        "",
        "/// <summary>The pinned manifests behind <see cref=\"EmbeddingPresets\"/>.</summary>",
        "internal static class EmbeddingPresetManifests",
        "{",
    ]
    for preset, files in resolved:
        lines.append(f"    public static readonly OnnxModelManifest {preset['field']} = new()")
        lines.append("    {")
        lines.append(f"        ModelId = \"{preset['model_id']}\",")
        lines.append(f"        GraphFile = \"{preset['graph']}\",")
        lines.append(f"        SpdxLicense = \"{preset['license']}\",")
        lines.append(f"        HuggingFaceRepo = \"{preset['repo']}\",")
        lines.append(f"        HuggingFaceRevision = \"{preset['rev']}\",")
        lines.append("        Files =")
        lines.append("        [")
        for path, role, size, sha in files:
            lines.append(
                f"            new(\"{path}\", OnnxModelFileRole.{role}, {size}, \"{sha}\"),")
        lines.append("        ],")
        lines.append("    };")
        lines.append("")
    lines.append("}")

    out = pathlib.Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text("\n".join(lines) + "\n", encoding="utf-8")
    for preset, files in resolved:
        for path, role, size, sha in files:
            print(f"{preset['field']:<24} {path:<32} {size:>10} {sha}")


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: Create the venv and generate**

```powershell
$uv = "C:\Users\steve\.local\bin\uv.exe"
$m  = "C:\Users\steve\projects\qavren-edge-sp2\embeddings\tools\model-hashes"
& $uv venv --python 3.14 "$m\.venv"
& $uv pip install --python "$m\.venv\Scripts\python.exe" -r "$m\requirements.txt"
& "$m\.venv\Scripts\python.exe" "$m\fetch_preset_hashes.py" `
  --out "C:\Users\steve\projects\qavren-edge-sp2\embeddings\src\Qavren.Edge.Embeddings.Onnx\EmbeddingPresets.g.cs"
```

Measured output on this box, and these eight lines are the record a reviewer checks the committed file against:

```
MiniLmL6V2Int8           onnx/model_qint8_arm64.onnx        23026053 4278337fd0ff3c68bfb6291042cad8ab363e1d9fbc43dcb499fe91c871902474
MiniLmL6V2Int8           vocab.txt                            231508 07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3
MiniLmL6V2Fp32           onnx/model.onnx                    90405214 6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452
MiniLmL6V2Fp32           vocab.txt                            231508 07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3
BgeSmallEnV15            onnx/model.onnx                   133093490 828e1496d7fabb79cfa4dcd84fa38625c0d3d21da474a00f08db0f559940cf35
BgeSmallEnV15            vocab.txt                            231508 07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3
NomicEmbedTextV15Int8    onnx/model_quantized.onnx         137296292 b4342336debaea79de872370664b0aaeb67dea4605513d00ee236ea871a81f27
NomicEmbedTextV15Int8    vocab.txt                            231508 07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3
```

Three things that output settles, each of which a reader should check rather than take on trust. The four graph sizes are 23,026,053 / 90,405,214 / 133,093,490 / 137,296,292 and §7 states exactly those four numbers — an independent confirmation that the right file was named in each repo, which matters most for nomic, where eight ONNX variants sit side by side and only `model_quantized.onnx` is the int8 one §7 means. The three MiniLM `qint8_*` files return one digest (`4278337f…`), confirming §7's "ONE blob under three names" rather than restating it. And all three repos ship the identical `vocab.txt` — 231,508 bytes, `07eced37…`, the 30,522-entry `bert-base-uncased` vocabulary — so the generated file emits one `OnnxModelFile` per manifest with the same two values, and `EmbeddingPresets.ById` needs no special case.

- [ ] **Step 5: The committed result**

`embeddings\src\Qavren.Edge.Embeddings.Onnx\EmbeddingPresets.g.cs`, verbatim as generated. Task 4.1 wraps these four manifests in `EmbeddingPreset` records and **must not retype a digest**:

```csharp
// <auto-generated>
// Regenerate with embeddings/tools/model-hashes/fetch_preset_hashes.py.
// NEVER a build step. Every SHA-256 below is the git-LFS oid for an LFS-backed file and
// a locally computed digest for a plain git blob -- see the script's docstring.
// </auto-generated>

namespace Qavren.Edge.Embeddings.Onnx;

/// <summary>The pinned manifests behind <see cref="EmbeddingPresets"/>.</summary>
internal static class EmbeddingPresetManifests
{
    public static readonly OnnxModelManifest MiniLmL6V2Int8 = new()
    {
        ModelId = "all-minilm-l6-v2-int8",
        GraphFile = "onnx/model_qint8_arm64.onnx",
        SpdxLicense = "Apache-2.0",
        HuggingFaceRepo = "sentence-transformers/all-MiniLM-L6-v2",
        HuggingFaceRevision = "1110a243fdf4706b3f48f1d95db1a4f5529b4d41",
        Files =
        [
            new("onnx/model_qint8_arm64.onnx", OnnxModelFileRole.Graph, 23026053, "4278337fd0ff3c68bfb6291042cad8ab363e1d9fbc43dcb499fe91c871902474"),
            new("vocab.txt", OnnxModelFileRole.Vocabulary, 231508, "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"),
        ],
    };

    public static readonly OnnxModelManifest MiniLmL6V2Fp32 = new()
    {
        ModelId = "all-minilm-l6-v2-fp32",
        GraphFile = "onnx/model.onnx",
        SpdxLicense = "Apache-2.0",
        HuggingFaceRepo = "sentence-transformers/all-MiniLM-L6-v2",
        HuggingFaceRevision = "1110a243fdf4706b3f48f1d95db1a4f5529b4d41",
        Files =
        [
            new("onnx/model.onnx", OnnxModelFileRole.Graph, 90405214, "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452"),
            new("vocab.txt", OnnxModelFileRole.Vocabulary, 231508, "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"),
        ],
    };

    public static readonly OnnxModelManifest BgeSmallEnV15 = new()
    {
        ModelId = "bge-small-en-v1.5",
        GraphFile = "onnx/model.onnx",
        SpdxLicense = "MIT",
        HuggingFaceRepo = "BAAI/bge-small-en-v1.5",
        HuggingFaceRevision = "5c38ec7c405ec4b44b94cc5a9bb96e735b38267a",
        Files =
        [
            new("onnx/model.onnx", OnnxModelFileRole.Graph, 133093490, "828e1496d7fabb79cfa4dcd84fa38625c0d3d21da474a00f08db0f559940cf35"),
            new("vocab.txt", OnnxModelFileRole.Vocabulary, 231508, "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"),
        ],
    };

    public static readonly OnnxModelManifest NomicEmbedTextV15Int8 = new()
    {
        ModelId = "nomic-embed-text-v1.5-int8",
        GraphFile = "onnx/model_quantized.onnx",
        SpdxLicense = "Apache-2.0",
        HuggingFaceRepo = "nomic-ai/nomic-embed-text-v1.5",
        HuggingFaceRevision = "e9b6763023c676ca8431644204f50c2b100d9aab",
        Files =
        [
            new("onnx/model_quantized.onnx", OnnxModelFileRole.Graph, 137296292, "b4342336debaea79de872370664b0aaeb67dea4605513d00ee236ea871a81f27"),
            new("vocab.txt", OnnxModelFileRole.Vocabulary, 231508, "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"),
        ],
    };

}
```

`EmbeddingPresetManifests` is a separate `internal static class` rather than a `partial` half of `EmbeddingPresets` on purpose: §7 declares `public static class EmbeddingPresets`, and making it `partial` to accommodate a generator would be a deviation from the spec's literal surface for no gain. The generated class is internal, so it is not part of the public API and `EnablePackageValidation` never sees it.

- [ ] **Step 6: Verify**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp2"
$m = "$root\embeddings\tools\model-hashes"
$out = "$root\embeddings\src\Qavren.Edge.Embeddings.Onnx\EmbeddingPresets.g.cs"
$before = Get-FileHash $out -Algorithm SHA256
& "$m\.venv\Scripts\python.exe" "$m\fetch_preset_hashes.py" --out $out
if ($LASTEXITCODE -ne 0) { throw "the fetch script failed" }
if ((Get-FileHash $out -Algorithm SHA256).Hash -ne $before.Hash) {
  throw "regenerating EmbeddingPresets.g.cs changed it - a pinned revision moved, or the committed file was hand-edited"
}
$src = Get-Content $out -Raw
foreach ($sha in '4278337fd0ff3c68bfb6291042cad8ab363e1d9fbc43dcb499fe91c871902474',
                 '6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452',
                 '828e1496d7fabb79cfa4dcd84fa38625c0d3d21da474a00f08db0f559940cf35',
                 'b4342336debaea79de872370664b0aaeb67dea4605513d00ee236ea871a81f27',
                 '07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3') {
  if ($src -notmatch [regex]::Escape($sha)) { throw "missing digest $sha" }
}
# Every revision is a 40-hex commit SHA and none of them is "main".
$revs = [regex]::Matches($src, 'HuggingFaceRevision = "([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
if ($revs.Count -ne 4) { throw "expected 4 revisions, found $($revs.Count)" }
foreach ($r in $revs) { if ($r -notmatch '^[0-9a-f]{40}$') { throw "revision is not a commit SHA: $r" } }
# The git SHA-1 of vocab.txt must NEVER appear - that is plan adjustment 19's whole point.
if ($src -match 'fb140275c155a9c7c5a3b3e0e77a9e839594a938') {
  throw "a git blob SHA-1 leaked into a manifest where a SHA-256 belongs"
}
Write-Host 'OK: EmbeddingPresets.g.cs regenerates byte-identically, 5 digests, 4 pinned revisions'
```

Expected: `OK: EmbeddingPresets.g.cs regenerates byte-identically, 5 digests, 4 pinned revisions`, exit code 0.

**If the network is unavailable to the implementer**, the four manifests are reproduced literally in Step 5 and in **Environment ground truth**; type them and skip Steps 4 and 6's regeneration, leaving the byte-identity check for the wave-3 close. That is a documented fallback, not the happy path — the script still has to be written, because §10.2 requires it to exist and because the next revision bump needs it.

---

### Task 3.4: Wave 3 close — sequential re-verify and commit *(integrator)*

**Local-verifiable:** yes.

**Files:** none of its own. It reads what wave 3 wrote and commits it.

**Approach.** The parallel phase ran every verify under `-p:ArtifactsPath=D:\Local\Temp\qedge-sp2\w3\t…`, so no two tasks contended for one `obj/` or `bin/`. That isolation is exactly why the wave is **not** yet proved: nothing has built these projects in the tree the next wave, and CI, will use. This task deletes the scratch roots, re-runs every verify **sequentially and un-isolated**, and only then commits. Wave 3 adds one wrinkle: Task 3.3 wrote a `.cs` file into a project no wave-3 verify compiles, so Step 2 builds `Qavren.Edge.Embeddings.Onnx` on its own — the generated file's first compile, one wave before Task 4.1 depends on it.

- [ ] **Step 1: Clear the wave's isolated outputs**

```powershell
Remove-Item -Recurse -Force "D:\Local\Temp\qedge-sp2\w3" -ErrorAction SilentlyContinue
```

- [ ] **Step 2: Re-run every verify in the wave, sequentially, in the real tree**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp2"
foreach ($p in @(
  "embeddings\tests\Qavren.Edge.Onnx.Tests\Qavren.Edge.Onnx.Tests.csproj",
  "embeddings\tests\Qavren.Edge.VectorData.Tests\Qavren.Edge.VectorData.Tests.csproj")) {
  dotnet run --project "$root\$p" -c Release -f net10.0 -p:TargetFrameworks=net10.0
  if ($LASTEXITCODE -ne 0) { throw "FAILED: $p" }
}
# Task 3.3 compiled nothing, so this is the first build of EmbeddingPresets.g.cs in any tree.
dotnet build "$root\embeddings\src\Qavren.Edge.Embeddings.Onnx\Qavren.Edge.Embeddings.Onnx.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0
if ($LASTEXITCODE -ne 0) { throw "FAILED: Embeddings.Onnx does not yet compile with the generated manifests" }
Write-Host 'OK: wave 3 re-verified sequentially'
```

Expected: `OK: wave 3 re-verified sequentially`, exit code 0. A failure here that did not appear in the parallel phase is real — the usual cause is a project that only restored because a sibling task had already written its assets file.

- [ ] **Step 3: Commit the wave**

One commit, Conventional Commits, no `Co-Authored-By` and no AI attribution, on `feat/sp2-embeddings`. Never on `main`.

```
feat(sp2): model provisioning, leased session host, vector-store SQL and pinned presets

- Qavren.Edge.Onnx: model store, file/bundled/HTTP sources, ref-counted session host
- Qavren.Edge.VectorData: collection model, EdgeVectorSchema golden SQL, filter translator
- EmbeddingPresets.g.cs generated from one paths-info call per repo; four pinned revisions
```

- [ ] **Step 4: Verify**

Step 2 printed its `OK` line and `git status --short` is clean. Wave 4 may start.

---

## WAVE 4 — The embedding generator; the vector-store collections

### Task 4.1: `Qavren.Edge.Embeddings.Onnx` — tokenizer, pooler, presets, generator

**Local-verifiable:** yes.

**Files:**
- Create: `embeddings\src\Qavren.Edge.Embeddings.Onnx\EmbeddingExceptions.cs` *(`EdgeEmbeddingException`)*
- Create: `embeddings\src\Qavren.Edge.Embeddings.Onnx\IEdgeTokenizer.cs`
- Create: `embeddings\src\Qavren.Edge.Embeddings.Onnx\EdgeTokenizer.cs`
- Create: `embeddings\src\Qavren.Edge.Embeddings.Onnx\Internal\BertEdgeTokenizer.cs`
- Create: `embeddings\src\Qavren.Edge.Embeddings.Onnx\Internal\EdgeTokenizerProvider.cs`
- Create: `embeddings\src\Qavren.Edge.Embeddings.Onnx\EmbeddingPooler.cs`
- Create: `embeddings\src\Qavren.Edge.Embeddings.Onnx\EmbeddingPreset.cs`
- Create: `embeddings\src\Qavren.Edge.Embeddings.Onnx\EmbeddingPresets.cs` *(the hand-written half: `All`, `ById`, and the four properties that return the generated records)*
- **Already present**, written and committed by Task 3.3 in wave 3 — **do not regenerate, do not edit**: `embeddings\src\Qavren.Edge.Embeddings.Onnx\EmbeddingPresets.g.cs`, which holds every `OnnxModelManifest`, per-file size, per-file SHA-256 and HF revision as literal constants
- Create: `embeddings\src\Qavren.Edge.Embeddings.Onnx\OnnxEmbeddingOptions.cs`
- Create: `embeddings\src\Qavren.Edge.Embeddings.Onnx\OnnxEmbeddingGenerator.cs`
- Create: `embeddings\src\Qavren.Edge.Embeddings.Onnx\EdgeEmbeddings.cs`
- Create: `embeddings\src\Qavren.Edge.Embeddings.Onnx\OnnxEmbeddingsEdgeBuilderExtensions.cs`
- Create: `embeddings\src\Qavren.Edge.Embeddings.Onnx\Internal\EmbeddingsDiagnosticsContributor.cs`
- Create: `embeddings\src\Qavren.Edge.Embeddings.Onnx\Internal\EmbeddingsWarmUpStartupTask.cs`
- Edit: `embeddings\tests\Qavren.Edge.Embeddings.Tests\Qavren.Edge.Embeddings.Tests.csproj` *(add the fixture link)*
- Test: `embeddings\tests\Qavren.Edge.Embeddings.Tests\TokenizerTests.cs`
- Test: `embeddings\tests\Qavren.Edge.Embeddings.Tests\PoolerTests.cs`
- Test: `embeddings\tests\Qavren.Edge.Embeddings.Tests\PresetCatalogueTests.cs`
- Test: `embeddings\tests\Qavren.Edge.Embeddings.Tests\GeneratorTests.cs`
- Test: `embeddings\tests\Qavren.Edge.Embeddings.Tests\PinnedSequenceLengthTests.cs`
- Test: `embeddings\tests\Qavren.Edge.Embeddings.Tests\GeneratorRegistrationTests.cs`
- Test: `embeddings\tests\Qavren.Edge.Embeddings.Tests\Fakes\FixedTimeProvider.cs` *(five lines; no CPM edit — Step 3)*

**Approach — read this before writing.** Five traps, all of which produce plausible, wrong vectors rather than an exception.

1. **Hold the concrete `BertTokenizer`, never the `Tokenizer` base.** `BertTokenizer.EncodeToIds` is declared `new`, so a base-typed field silently binds the base method and drops `[CLS]` and `[SEP]`.
2. **Bind ORT inputs by name, never by position.** nomic's graph declares `(input_ids, token_type_ids, attention_mask)` while MiniLM declares `(input_ids, attention_mask, token_type_ids)`. The `NomicInputOrder` fixture exists to catch exactly this.
3. **Synthesise the attention mask.** There is no attention-mask API in `Microsoft.ML.Tokenizers` — a search for "attention" across the whole assembly returns nothing — and `GetSpecialTokensMask` is not one: on its default `alreadyHasSpecialTokens: false` path it ignores the ids entirely and emits `1, 0…0, 1`; on the other path it marks special tokens. Either way it is the inverse of what ONNX wants. The mask here is 1 for a real token, 0 for padding.
4. **Pooling mode comes from the preset, never a default.** bge-small is **CLS** and everything else here is mean; getting it wrong is a silent ~10-point retrieval regression with no exception anywhere.
5. **`PostPoolLayerNorm` is not Matryoshka truncation.** nomic-embed-text-v1.5's `modules.json` carries only Transformer + Pooling with no Normalize module, and its documented pipeline is mean-pool → `F.layer_norm` over the 768 dim → (optional truncate) → L2. Omitting the layer-norm ships vectors that look fine, sit on the unit sphere, and do not match the model's reference implementation.

- [ ] **Step 1: Link the fixtures**

```xml
  <ItemGroup>
    <Compile Include="..\fixtures\TinyModels.g.cs" Link="Fixtures\TinyModels.g.cs" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing pooler tests — the values are hand-computed**

`PoolerTests.cs`. Using the fixture embedding table (row *t* = `[t, t+0.5, t+0.25, t+0.75]`), `last_hidden_state` for `input_ids = [[3,1,9]]` is `[[3,3.5,3.25,3.75],[1,1.5,1.25,1.75],[9,9.5,9.25,9.75]]` — the exact tensor ORT returned for `TinyModels.HiddenStates` in Task 1.3 Step 5:

```csharp
[Fact]
public void MeanPoolIgnoresMaskedPositions()
{
    ReadOnlySpan<float> hidden = [3f, 3.5f, 3.25f, 3.75f, 1f, 1.5f, 1.25f, 1.75f, 9f, 9.5f, 9.25f, 9.75f];
    ReadOnlySpan<long> mask = [1, 1, 0];
    Span<float> destination = stackalloc float[4];

    EmbeddingPooler.MeanPool(hidden, mask, sequenceLength: 3, dimensions: 4, destination);

    Assert.Equal([2f, 2.5f, 2.25f, 2.75f], destination.ToArray());
}

[Fact]
public void ClsPoolTakesRowZero()
{
    ReadOnlySpan<float> hidden = [3f, 3.5f, 3.25f, 3.75f, 1f, 1.5f, 1.25f, 1.75f, 9f, 9.5f, 9.25f, 9.75f];
    Span<float> destination = stackalloc float[4];

    EmbeddingPooler.ClsPool(hidden, sequenceLength: 3, dimensions: 4, destination);

    Assert.Equal([3f, 3.5f, 3.25f, 3.75f], destination.ToArray());
}

[Fact]
public void AnAllPaddingRowDividesByOneRatherThanZero()
{
    ReadOnlySpan<float> hidden = [3f, 3.5f, 3.25f, 3.75f];
    ReadOnlySpan<long> mask = [0];
    Span<float> destination = stackalloc float[4];

    EmbeddingPooler.MeanPool(hidden, mask, sequenceLength: 1, dimensions: 4, destination);

    Assert.Equal([0f, 0f, 0f, 0f], destination.ToArray());   // sum is 0, denominator max(1, 0)
}
```

§16.1 names three pooling assertions — "mean/CLS/**L2** against a hand-computed reference" — and L2 is the one an earlier draft of this plan left out. It is hand-computable from the same `MeanPool` result, which is why it belongs here rather than being left to the end-to-end generator test where a wrong norm would still look plausible:

```csharp
[Fact]
public void L2NormalizeDividesByTheEuclideanNorm()
{
    // The MeanPool result above. |v| = sqrt(4 + 6.25 + 5.0625 + 7.5625) = sqrt(22.875).
    Span<float> v = [2f, 2.5f, 2.25f, 2.75f];
    const float norm = 4.783303f;                    // sqrt(22.875), to float precision

    EmbeddingPooler.L2Normalize(v);

    Assert.Equal(2f / norm,    v[0], 5);
    Assert.Equal(2.5f / norm,  v[1], 5);
    Assert.Equal(2.25f / norm, v[2], 5);
    Assert.Equal(2.75f / norm, v[3], 5);
    var lengthSquared = 0f;
    foreach (var x in v) { lengthSquared += x * x; }
    Assert.Equal(1f, lengthSquared, 5);
}

[Fact]
public void L2NormalizeLeavesAZeroVectorAlone()
{
    // The all-padding MeanPool guard produces this, so the two guards have to compose: dividing
    // by a zero norm here would turn a defined zero vector into four NaNs one call later.
    Span<float> v = [0f, 0f, 0f, 0f];

    EmbeddingPooler.L2Normalize(v);

    Assert.Equal([0f, 0f, 0f, 0f], v.ToArray());
}
```

Plus `LayerNorm` against a hand-computed mean/variance reference including the epsilon path, and an **ordering** test proving pool → layer-norm → L2 and not pool → L2 → layer-norm.

`PinnedSequenceLengthTests.cs` closes §16.1's session-factory sentence. Task 2.2's `SessionFactoryShapeTests` asserts the L0 half — raw `FreeDimensionOverrides` against the `OnnxStaticShapesUnpinned` guard — from a project that cannot see `OnnxEmbeddingOptions`. This is the other half, and it is the only place in the suite where both types are visible:

```csharp
[Fact]
public void PinnedSequenceLengthPopulatesBothTheOverrideDictionaryAndTheFlag()
{
    var options = new OnnxEmbeddingOptions
    {
        Preset = EmbeddingPresets.MiniLmL6V2Int8,
        MaxBatchSize = 16,
        PinnedSequenceLength = 128,      // one of the preset's SequenceBuckets
    };

    OnnxEmbeddingOptions.ApplyPinning(options);   // what AddOnnxEmbeddings calls at registration

    Assert.Equal(128, options.Session.FreeDimensionOverrides["sequence_length"]);
    Assert.Equal(16,  options.Session.FreeDimensionOverrides["batch_size"]);
    Assert.True(options.Session.ExecutionProviders.CoreMl.RequireStaticInputShapes);
}

[Fact]
public void LeavingItNullLeavesTheGraphSymbolicAndCoreMlPartitioningNormally()
{
    var options = new OnnxEmbeddingOptions { Preset = EmbeddingPresets.MiniLmL6V2Int8 };

    OnnxEmbeddingOptions.ApplyPinning(options);

    Assert.Empty(options.Session.FreeDimensionOverrides);
    Assert.False(options.Session.ExecutionProviders.CoreMl.RequireStaticInputShapes);
}

[Fact]
public void APinnedLengthThatIsNotOneOfThePresetsBucketsIsRejected()
{
    var options = new OnnxEmbeddingOptions
    {
        Preset = EmbeddingPresets.MiniLmL6V2Int8,
        PinnedSequenceLength = 100,
    };

    // ArgumentOutOfRangeException, NOT an EdgeEmbeddingException: 15.1 allocates this package
    // exactly five codes and none of them means "that is not one of the buckets". Inventing a
    // sixth would be an SP1 EdgeErrorCode edit, and 5 permits two edits, both already spent.
    // This is a registration-time argument error anyway - it is raised from AddOnnxEmbeddings,
    // before a container is built, before any model exists.
    var ex = Assert.Throws<ArgumentOutOfRangeException>(() => OnnxEmbeddingOptions.ApplyPinning(options));
    Assert.Contains("SequenceBuckets", ex.Message, StringComparison.Ordinal);
}
```

`ApplyPinning` is an `internal static` on `OnnxEmbeddingOptions` called by `AddOnnxEmbeddings` — a method rather than a setter side effect, because a setter that mutates three other properties is invisible to a reader of the options object and untestable without a container.

- [ ] **Step 3: Write the failing tokenizer, preset and generator tests**

`TokenizerTests.cs`, over the 64-entry `TinyModels.VocabTxt`: `[CLS]` and `[SEP]` present **asserted by id**, truncation inclusive of the specials (the truncating `EncodeToIds` overload accounts for the two itself — it delegates with `maxTokenCount - 2` and returns empty when `maxTokenCount < 2`, so `maxTokens: 256` yields at most 256 ids **including** them), padding to the correct bucket, mask correctness, and `IndexByTokenCount` versus `CountTokens`.

`PresetCatalogueTests.cs` is a table test guarding against a copy-paste regression in dimensions, pooling, **`PostPoolLayerNorm`**, prefixes or licence, with an explicit assertion that `NomicEmbedTextV15Int8` is the **only** preset setting the flag:

```csharp
[Fact]
public void NomicIsTheOnlyPresetThatLayerNorms()
{
    var flagged = EmbeddingPresets.All.Where(p => p.PostPoolLayerNorm).Select(p => p.Id).ToArray();
    Assert.Equal([EmbeddingPresets.NomicEmbedTextV15Int8.Id], flagged);
}
```

`GeneratorTests.cs` drives `OnnxEmbeddingGenerator` over `TinyModels.HiddenStates` through the internal factory hook and asserts: one embedding per input **in input order** across a ragged batch; the empty-input short-circuit returns an empty collection without touching ORT; eight concurrent `GenerateAsync` calls all succeed; `GetService` returns, in order, `this` when assignable, `EmbeddingGeneratorMetadata`, `OnnxEmbeddingGeneratorInfo`, `EmbeddingPreset`, `OnnxSessionInfo`, `IEdgeTokenizer`, and — for serviceKey `EdgeEmbeddings.QueryServiceKey` — the query-prefixed sibling **for a `serviceType` of either the non-generic `IEmbeddingGenerator` or the closed generic**, because the vector store asks with the non-generic type while a direct caller usually asks with the closed one; `options.Dimensions` other than the preset's throws `EdgeEmbeddingException(EmbeddingDimensionMismatch)` naming the fixed width; a mismatched `options.ModelId` throws rather than being ignored; and `NomicInputOrder` produces the same vector as `HiddenStates`, which is the name-binding proof.

Two more assertions belong to `GeneratorTests.cs` and to nothing else in the suite, because §11's **Emit** contract (Step 6) is per-result metadata that no other test in this plan looks at. Both are one test:

```csharp
[Fact]
public async Task EveryEmbeddingCarriesModelIdAndCreatedAtAndUsageCountsUnpaddedTokens()
{
    var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
    using var generator = CreateOverHiddenStatesFixture(clock);   // preset dims 4, buckets [8]

    // Two inputs of different lengths, so the padded width and the real token count differ and a
    // Usage computed from BatchSize * SequenceLength would be visibly wrong rather than plausible.
    var result = await generator.GenerateAsync(["alpha", "alpha beta gamma"]);

    Assert.Equal(2, result.Count);
    Assert.All(result, e => Assert.Equal(generator.Info.ModelId, e.ModelId));
    Assert.All(result, e => Assert.Equal(clock.GetUtcNow(), e.CreatedAt));

    // 1 + [CLS] + [SEP] = 3, and 3 + [CLS] + [SEP] = 5. Unpadded: the batch was padded to 8 per
    // row, so a padded count would report 16 and a per-row max would report 10.
    Assert.Equal(8, result.Usage!.InputTokenCount);
    Assert.Null(result.Usage.OutputTokenCount);
}

[Fact]
public async Task AdditionalPropertiesAndRawRepresentationFactoryAreIgnoredRatherThanHonouredOrRejected()
{
    using var generator = CreateOverHiddenStatesFixture(TimeProvider.System);
    var invoked = false;

    var options = new EmbeddingGenerationOptions
    {
        AdditionalProperties = new AdditionalPropertiesDictionary { ["temperature"] = 0.7f },
        RawRepresentationFactory = _ => { invoked = true; return new object(); },
    };

    var result = await generator.GenerateAsync(["alpha"], options);

    // Spec 11: "AdditionalProperties and RawRepresentationFactory are ignored." Ignored means
    // exactly this - no throw, no behaviour change, and the factory is never called. A raw
    // representation here would be a SessionOptions and three OrtValues disposed before this
    // method returns; handing a consumer disposed native memory is worse than handing them none.
    Assert.Single(result);
    Assert.False(invoked);
    Assert.Null(result[0].RawRepresentation);
}
```

`FixedTimeProvider` is **this task's own five-line fake**, not `Microsoft.Extensions.TimeProvider.Testing` — that package is **not** in `Directory.Packages.props` (verified in the merged tree), SP1's own `LifecycleHubTests` passes `TimeProvider.System` rather than faking one, and adding a CPM entry in wave 4 would be an integrator-owned root-file edit a whole wave after the prologue that owns them:

```csharp
namespace Qavren.Edge.Embeddings.Tests.Fakes;

/// <summary>A TimeProvider frozen at one instant. Five lines, no package, no CPM edit.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
```

The `CreateOverHiddenStatesFixture` helper is the `InternalsVisibleTo`-scoped factory hook from Task 3.1 Step 5, configured with a four-dimension preset over `TinyModels.HiddenStates` and a single `[8]` sequence bucket so the padded and unpadded counts differ by a number a reader can check by hand.

`GeneratorRegistrationTests.cs` asserts MEAI DI registers **both** descriptors — the closed generic `IEmbeddingGenerator<string, Embedding<float>>` and the non-generic `IEmbeddingGenerator` — because `AddOnnxEmbeddings` delegates to `services.AddEmbeddingGenerator` rather than hand-registering. Hand-registering would satisfy only one and break half of MEVD and Semantic Kernel.

- [ ] **Step 4: The tokenizer, the provider and the pooler**

Eight public types, all §7, transcribed verbatim: the four enums `EdgeTokenizerKind`, `EmbeddingPooling`, `EmbeddingInputKind` and **`EmbeddingTruncation`**, the record **`TokenizedBatch`**, the interface `IEdgeTokenizer`, the options class `WordPieceTokenizerOptions`, the static `EdgeTokenizer` factory, `IEdgeTokenizerProvider` and `EmbeddingPooler`.

**`TokenizedBatch` is restated here in full** because it is the single hand-off between the tokenizer and the tensor assembler, no other task names it, and five of its seven members have a layout contract a reader would otherwise have to infer:

```csharp
public enum EmbeddingTruncation { Truncate = 0, Throw = 1 }

/// <summary>One padded batch, row-major, ready to wrap in OrtValues.</summary>
public sealed record TokenizedBatch(
    long[] InputIds,          // [BatchSize * SequenceLength]
    long[] AttentionMask,     // 1 = real token, 0 = padding
    long[] TokenTypeIds,      // all zeros for a single-sequence encoder
    int BatchSize,
    int SequenceLength,
    int[] TokenCounts,        // per input, pre-padding, including [CLS]/[SEP]
    bool[] Truncated);
```

Four facts about that record that the assembler, the pooler and the `Usage` accounting in Step 6 all depend on. The three `long[]` are **row-major and flat**, length `BatchSize * SequenceLength` each — row *b* occupies `[b * SequenceLength, (b+1) * SequenceLength)`, which is exactly the layout `OrtValue.CreateTensorValueFromMemory<long>(…, [batch, seq])` expects, so no reshape happens anywhere. `AttentionMask` is the mask **we synthesise** (1 real, 0 padding), never anything `Microsoft.ML.Tokenizers` returned — see trap 3 above. `TokenCounts[b]` is the **unpadded** count including `[CLS]` and `[SEP]`, and it is the only source for the `GeneratedEmbeddings.Usage` token count in Step 6; summing `AttentionMask` would give the same number today and would silently stop doing so the moment anything masks a non-padding position. `Truncated[b]` is what raises `EmbeddingInputTooLong` (5105) when `OnnxEmbeddingOptions.Truncation` is `EmbeddingTruncation.Throw`, and what logs `EmbeddingInputTruncated` (701) when it is `Truncate` — which is the default, so the library's out-of-the-box behaviour on an over-long input is a logged truncation, never an exception. `IEdgeTokenizer.EncodeBatch(texts, maxSequenceLength, buckets)` is the one method that produces this record: it encodes, truncates, right-pads to the smallest bucket that fits the batch maximum, and synthesises the mask.

**Defaults that change behaviour — `WordPieceTokenizerOptions`, §7:**

```csharp
public sealed class WordPieceTokenizerOptions
{
    public bool LowerCase { get; set; } = true;
    public int MaxSequenceLength { get; set; } = 512;
    public string UnknownToken { get; set; } = "[UNK]";
    public string PaddingToken { get; set; } = "[PAD]";
    public string ClassificationToken { get; set; } = "[CLS]";
    public string SeparatorToken { get; set; } = "[SEP]";
    public bool RemoveNonSpacingMarks { get; set; }
}
```

The four special-token strings are the `bert-base-uncased` spellings and are **looked up in the vocabulary by string to get their ids** — all four presets share the identical 30,522-entry vocab (Task 3.3), so all four resolve to the same four ids. Getting one wrong does not throw: a `[UNK]`-token name absent from the vocab resolves to no id and the tokenizer emits a sequence with no unknown marker, which is a silent quality bug of exactly the class every `required` field on `EmbeddingPreset` exists to prevent. `TokenizerTests.cs` therefore asserts `[CLS]` and `[SEP]` **by id** — not by presence — against `TinyModels.VocabTxt`, and asserts that `PadTokenId` is the `[PAD]` id and that padded positions carry it. `LowerCase` defaults true and is **overridden per preset** from `EmbeddingPreset.LowerCase`, never left at the default: a cased model fed lower-cased text produces plausible, wrong vectors. `RemoveNonSpacingMarks` defaults false and matches `bert-base-uncased`, which strips accents through its own normaliser rather than this flag.

`IEdgeTokenizer.Encode`'s XML doc must keep its honesty clause: this is **not** allocation-free on the pinned tokenizer version. Every `EncodeToIds` overload in `Microsoft.ML.Tokenizers` 2.0.0 returns `IReadOnlyList<int>`, and the only span-destination members in the library — `BuildInputsWithSpecialTokens`, `GetSpecialTokensMask`, `CreateTokenTypeIdsFromSequences` — all take ids that already exist. The implementation calls the truncating `EncodeToIds` and copies into `destination`: one short-lived list per input, and no allocation in the batch assembler, which is where the pooled `long[]` buffers that actually matter live. The span signature is kept because it is the shape the assembler wants and because Tokenizers 3.x may add a span overload this method can then adopt without a surface change.

`IEdgeTokenizerProvider` exists rather than a directly-injected `IEdgeTokenizer` because the vocab file is `OnnxModelFileRole.Vocabulary` inside the same manifest as the graph and §10 keeps provisioning lazy: on a first launch there is no vocab path at the moment DI constructs the generator, and DI factories cannot await a download. The first `GenerateAsync` awaits `provider.GetAsync(preset, ct)`, which awaits `IOnnxModelStore.EnsureAsync` for the same manifest the session is about to load, builds the tokenizer under a `SemaphoreSlim(1)`, and caches it for the process. `Current` is what diagnostics read, so reporting a vocab size never forces a download.

`EdgeTokenizerKind.UnigramTokenizerJson` throws `TokenizerKindUnsupported` with a message naming the Tokenizers 3.x requirement and linking ADR 0006.

- [ ] **Step 5: Presets**

`EmbeddingPreset` is §7 verbatim. **`EmbeddingPresets` is only half hand-written**: `EmbeddingPresets.g.cs` already exists in this folder, generated and committed by Task 3.3 in wave 3, and it holds every `OnnxModelManifest` — repo, full commit SHA, per-file `RelativePath`/`Role`/`SizeBytes`/`Sha256`, SPDX licence. Do not retype those values and do not regenerate the file; §7 declares `required` *fields*, not values, and the values come from one `POST /api/models/{repo}/paths-info/{rev}` per repo, which is Task 3.3's whole job. This task writes the hand-written half — the four `EmbeddingPreset` records that wrap those manifests with dimensions, pooling, prefixes, buckets and `PostPoolLayerNorm`, plus `All` and `ById` — and nothing else. The four digests, for a reviewer reading only this task: `MiniLmL6V2Int8` `4278337f…`, `MiniLmL6V2Fp32` `6fd5d72f…`, `BgeSmallEnV15` `828e1496…`, `NomicEmbedTextV15Int8` `b4342336…`, and the one shared `vocab.txt` `07eced37…`. Never `"main"` as a revision: a moving revision silently changes the vectors.

`MiniLmL6V2Int8` is the default: 23,026,053 B, 384-d, mean + L2, 256 tokens, no prefixes, Apache-2.0. Its `qint8_arm64` / `qint8_avx512` / `qint8_avx512_vnni` files are **one blob under three names**, so a single download covers every RID — which is why it is the one artifact small enough to bundle. 384 matches SP1's own `VecTable` example, so the sample needs no schema change.

**Defaults that change behaviour — the nine `EmbeddingPreset` members declared `init` rather than `required`.** Everything that changes the numbers is `required` and therefore impossible to forget; these nine (seven with a value, two defaulting to `null`) are the ones a preset author *can* forget, and each one silently changes what ORT is fed or what comes back:

```csharp
    public float LayerNormEpsilon { get; init; } = 1e-5f;
    public bool Normalize { get; init; } = true;
    public string? QueryPrefix { get; init; }
    public string? DocumentPrefix { get; init; }
    public string InputIdsName { get; init; } = "input_ids";
    public string AttentionMaskName { get; init; } = "attention_mask";
    public string? TokenTypeIdsName { get; init; } = "token_type_ids";
    public string OutputName { get; init; } = "last_hidden_state";
    public IReadOnlyList<int> SequenceBuckets { get; init; } = [64, 128, 256, 512];
```

`LayerNormEpsilon = 1e-5f` is PyTorch's default and what nomic's reference implementation uses; it is read **only** when `PostPoolLayerNorm` is set, so three of the four presets never touch it, and a wrong epsilon on the fourth moves every component of a 768-d vector by a small amount that no test outside tier 3 would notice. The three ONNX input names plus `OutputName` are the **binding keys** — §11 binds ORT inputs by name, never by position, and these four defaults are the names all four shipped presets actually declare, which is why no preset overrides them. `TokenTypeIdsName` is `string?` and its default is non-null: setting it to `null` is how a preset says "this graph declares no `token_type_ids`", and the generator then binds two tensors instead of three. That is the `UnusedTokenTypeIds` fixture's whole purpose. `SequenceBuckets` `[64, 128, 256, 512]` is what the batch assembler rounds up to and what `PinnedSequenceLength` is validated against in Step 2's `APinnedLengthThatIsNotOneOfThePresetsBucketsIsRejected`.

`PresetCatalogueTests.cs` asserts all four presets carry the four default binding names verbatim and that `NomicEmbedTextV15Int8.LayerNormEpsilon` is `1e-5f`, so a copy-paste that renames an input while adding a fifth preset fails the table test rather than the model.

- [ ] **Step 6: The generator**

`EdgeEmbeddingException`, `OnnxEmbeddingOptions`, `OnnxEmbeddingGeneratorInfo`, `OnnxEmbeddingGenerator`, `EdgeEmbeddings`, `OnnxEmbeddingsEdgeBuilderExtensions` exactly as §7.

`EmbeddingExceptions.cs` is listed first because this task's own tests throw it and an earlier draft of this plan gave it no owning file at all — the same defect `OnnxExceptions.cs` avoids in Task 2.2. §7 declares it literally and it derives from SP1's `EdgeException`, so the `HelpLink` convention applies for free:

```csharp
namespace Qavren.Edge.Embeddings.Onnx;

/// <summary>Every failure this package raises. <see cref="Expected"/> and <see cref="Actual"/> are
/// the two numbers a dimension or vocabulary mismatch needs and nothing else populates.</summary>
public sealed class EdgeEmbeddingException : EdgeException
{
    public EdgeEmbeddingException(EdgeErrorCode code, string presetId, string message,
        Exception? innerException = null);
    public string PresetId { get; }
    public int? Expected { get; init; }
    public int? Actual { get; init; }
}
```

It carries exactly the five codes §15.1 allocates to this package — `TokenizerAssetMissing` (5101), `TokenizerKindUnsupported` (5102), `EmbeddingDimensionMismatch` (5103), `EmbeddingPresetNotFound` (5104), `EmbeddingInputTooLong` (5105) — each of which must have an anchor in `foundation/docs/errors.md` (Task 1.2) and a raise site here. It invents none: §15.1 is a closed list and a sixth code would be an SP1 edit this plan does not have. `OnnxEmbeddingGenerator` implements the **literal** closed generic MEVD pattern-matches; a generator typed to any other input type is silently unresolvable by a vector store.

Pipeline, per §11: prefix (the unkeyed generator is the *document* generator; the query sibling is a second instance constructed with `EmbeddingInputKind.Query`) → tokenise → batch shape (`MaxBatchSize` 16, sequence length the batch maximum rounded up to the smallest entry of `SequenceBuckets` `[64,128,256,512]` that fits, capped at the preset's maximum) → three `OrtValue.CreateTensorValueFromMemory<long>` over pooled `long[]` from `ArrayPool<long>.Shared` → `session.Run(runOptions, inputNames, inputValues, [OutputName])` synchronously on a `Task.Run` worker under `SemaphoreSlim(MaxConcurrency)` → pool → optional layer-norm → L2 → **emit**.

**Emit — §11's last paragraph, and the one stage with no natural test of its own, which is why it is spelled out here.** The stage is three sentences in the spec and four obligations in the code:

1. **One `Embedding<float>` per input, in input order.** Across batches, not just within one — a 40-string call at `MaxBatchSize = 16` is three `Run`s whose results are appended to a single `GeneratedEmbeddings<Embedding<float>>` in the order the inputs arrived, never in completion order. Under a latched `Moderate` pressure the effective batch size halves mid-call and the batch boundaries move; the output order must not.
2. **Every embedding carries `ModelId`.** `Embedding<float>.ModelId` is set to `Info.ModelId` — the preset's model id, the same string `OnnxSessionInfo.ModelId` carries and the same one a mismatched `options.ModelId` is compared against. Left null, a consumer storing embeddings from two presets into one collection has no way to tell them apart after the fact, and MEVD does not add one.
3. **Every embedding carries `CreatedAt`.** `Embedding<float>.CreatedAt` is set from the injected **`TimeProvider`** (`timeProvider.GetUtcNow()`), never `DateTimeOffset.UtcNow` — the constructor already takes one, SP1's convention is that nothing reads the ambient clock, and a test that asserts `CreatedAt` needs to control it. One timestamp is taken per batch and shared by every embedding in that batch; per-embedding clock reads would buy nothing and cost a syscall each.
4. **`GeneratedEmbeddings.Usage` carries the unpadded token count.** `Usage` is an `Microsoft.Extensions.AI.UsageDetails` whose `InputTokenCount` is the sum of `TokenizedBatch.TokenCounts` over **every** batch in the call — unpadded, including `[CLS]` and `[SEP]`. Not the padded `BatchSize * SequenceLength`, which is 512 for a one-word input and would make ingestion cost look ~50× worse than it is; and not a re-count, because the tokenizer already produced the number. §11's phrase for it is exact: it "is free and makes ingestion cost visible" — an app estimating the cost of indexing a corpus reads this and nothing else. `OutputTokenCount` and `TotalTokenCount` are left null: an embedding model emits no output tokens and reporting a total equal to the input would be a fabricated third number.

The one-per-input contract is enforced upstream too, and the enforcement is worth knowing about because it changes what a bug looks like: MEAI's single-value `GenerateAsync` extension throws when `Count != 1`, and `GenerateAndZipAsync` throws on a count mismatch — but it short-circuits an empty input sequence **without calling the generator at all**, which is why the empty-input test in Step 3 asserts ORT was never touched rather than asserting a particular exception.

**Options handling — all four clauses of §11, because three of them are implemented and the fourth is implemented by deliberately doing nothing.** `EmbeddingGenerationOptions.Dimensions` is accepted only when it equals the preset's; anything else throws `EdgeEmbeddingException(EmbeddingDimensionMismatch)` naming the fixed width, with `Expected` and `Actual` populated. Matryoshka truncation is not implemented and could not be the source of truth for storage width anyway, since MEVD never passes options at all. A mismatched `options.ModelId` throws rather than being ignored. And **`options.AdditionalProperties` and `options.RawRepresentationFactory` are ignored** — both are read by nothing, neither reaches ORT, and neither is echoed back onto the result. That is a decision, not an omission, and it needs to be visible in the code or the next reader will "wire them up": `AdditionalProperties` is an untyped bag with no meaning for a fixed local graph — there is no vendor parameter to forward it to — and `RawRepresentationFactory` exists so a consumer can attach a provider's native request object, which for an in-process ORT `Run` over pooled `long[]` buffers is a `SessionOptions` and three `OrtValue`s that are disposed before `GenerateAsync` returns. Handing a consumer a reference to disposed native memory is worse than handing them nothing. The two properties are named in a comment at the top of `GenerateAsync`'s options-validation block saying exactly that.

Three lifetime rules that are not optional. `CreateTensorValueFromMemory` **pins** the managed memory for the `OrtValue`'s lifetime, so every one is disposed in a `finally` before the arrays return to the pool; a long-lived undisposed `OrtValue` pins a GC segment. Values are copied out of `GetTensorDataAsSpan<float>()` into a fresh `float[dim]` **before** the output `OrtValue` is disposed — that span points at native memory the `OrtValue` owns. And `token_type_ids` is an all-zero `long[]` allocated once per shape and reused, bound only when the graph declares it.

Bucketing is about the number of **distinct runtime shapes** and nothing else. It does not make `RequireStaticInputShapes=1` viable: CoreML partitions from the shapes the graph *declares*, which stay symbolic no matter what is fed at Run time. What it buys is that CoreML's per-shape compiled artefacts and ORT's memory-pattern arena see four shapes instead of hundreds. Pinning the declared dims is the separate, opt-in mechanism — `PinnedSequenceLength` emits `AddFreeDimensionOverrideByName("sequence_length", N)` plus `("batch_size", MaxBatchSize)` and **only then** turns `RequireStaticInputShapes` on, at the cost of one session, one shape, and a full-length run for every batch including a batch of one short string. Whether that wins is unmeasured (Open risk 5).

`Microsoft.ML.Tokenizers` states no thread-safety guarantee, so the tokenizer is guarded by the **same** semaphore as ORT `Run` until a stress test proves it reentrant (Open risk 4). Cancellation is honoured *between* batches; a single `Run` is not interruptible and the signature does not pretend otherwise.

**Memory pressure: the generator reads, it does not observe.** It already takes `IEdgeResourceMonitor`, and consults `LastPressure` before each batch. With `ShrinkBatchUnderMemoryPressure` (default true), a latched `Moderate` halves the effective batch size until `Resumed` clears the latch back to `null`, logging `EmbeddingBatchShrunk` (703). `Qavren.Edge.Embeddings.Onnx` registers **no** lifecycle observer — the observer lives in L0 and cannot see `OnnxEmbeddingOptions`, which is why the flag sits in L0 and the reaction in L1.

**Defaults that change behaviour — `OnnxEmbeddingOptions`, §7.** `Preset = EmbeddingPresets.MiniLmL6V2Int8`, `ModelSource = null` (meaning `HttpOnnxModelSource` over the preset's manifest), `MaxBatchSize = 16`, `PinnedSequenceLength = null` (symbolic shapes, CoreML partitions normally), `MaxConcurrency = 1`, `Truncation = EmbeddingTruncation.Truncate`, `ShrinkBatchUnderMemoryPressure = true`, and `Session = new OnnxSessionOptions()` as a get-only property.

The one that decides *what vectors come out* is **`DefaultInputKind = EmbeddingInputKind.Document`**:

```csharp
    /// <summary>Which prefix the unkeyed generator applies. The query sibling is reached through
    /// <see cref="EdgeEmbeddings.QueryServiceKey"/>.</summary>
    public EmbeddingInputKind DefaultInputKind { get; set; } = EmbeddingInputKind.Document;
```

`Document` is the right default because the **unkeyed** generator is the one MEVD's upsert path resolves, and upsert embeds documents. It is the constructor's `inputKind` argument for the instance registered without a service key; the sibling registered under `EdgeEmbeddings.QueryServiceKey` is constructed with `EmbeddingInputKind.Query` regardless of this option, so flipping the default to `Query` gives an app whose *documents* get the query prefix and whose queries still get the query prefix — two wrong halves, no exception, and a retrieval quality drop that looks like a bad model. It matters for exactly the two presets that declare prefixes (`BgeSmallEnV15`, query-only; `NomicEmbedTextV15Int8`, both mandatory) and is inert for the two MiniLM presets, which is precisely why it can be got wrong for a long time before anyone notices. `GeneratorTests.cs` asserts that with `BgeSmallEnV15` the unkeyed generator's tokenised input carries **no** prefix and the keyed sibling's carries `QueryPrefix`.

`WarmUpEmbeddingsAtStartup` registers at `EdgeAiStartupOrder.SessionWarmUp` (220) — the same order as L0's `WarmUpSessionAtStartup`, because they are two halves of one idea split along the layer boundary. Off by default.

- [ ] **Step 7: Diagnostics**

`ComponentName = "Qavren.Edge.Embeddings.Onnx"` reporting `preset`, `presetLicense`, `modelFile`, `modelSha256`, `dimensions`, `pooling`, `normalize`, `maxSequenceLength`, `sequenceBuckets`, `queryPrefix`, `documentPrefix`, `tokenizerKind`, `tokenizerFile`, `vocabSize` (from `IEdgeTokenizerProvider.Current`, `null` until the first embed or a warm-up, so reporting it never forces provisioning), `pinnedSequenceLength`, `maxBatchSize`, `effectiveBatchSize`, `maxConcurrency`, plus rolling counters `embeddingsGenerated`, `batchesRun`, `tokensEncoded`, `truncatedInputs`, `runMsP50`, `runMsP95`.

- [ ] **Step 8: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp2\embeddings\tests\Qavren.Edge.Embeddings.Tests\Qavren.Edge.Embeddings.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0 `
  -p:ArtifactsPath="D:\Local\Temp\qedge-sp2\w4\t41"
```

Expected: `Passed! - Failed: 0`, exit code 0.

---

### Task 4.2: `Qavren.Edge.VectorData` part 2 — store, collections, writes, search, RRF

**Local-verifiable:** yes.

**Files:**
- Create: `embeddings\src\Qavren.Edge.VectorData\EdgeVectorStore.cs`
- Create: `embeddings\src\Qavren.Edge.VectorData\EdgeVectorStoreCollection.cs`
- Create: `embeddings\src\Qavren.Edge.VectorData\EdgeDynamicVectorStoreCollection.cs`
- Create: `embeddings\src\Qavren.Edge.VectorData\Internal\RecordMapper.cs`
- Create: `embeddings\src\Qavren.Edge.VectorData\Internal\EdgeVectorCollectionMigration.cs`
- Create: `embeddings\src\Qavren.Edge.VectorData\Internal\VectorDataLifecycleObserver.cs`
- Create: `embeddings\src\Qavren.Edge.VectorData\Internal\VectorDataDiagnosticsContributor.cs`
- Create: `embeddings\src\Qavren.Edge.VectorData\EdgeVectorDataBuilderExtensions.cs`
- Test: `embeddings\tests\Qavren.Edge.VectorData.Tests\CollectionLifecycleTests.cs`
- Test: `embeddings\tests\Qavren.Edge.VectorData.Tests\ListCollectionNamesTests.cs`
- Test: `embeddings\tests\Qavren.Edge.VectorData.Tests\Records\RawVec.cs` *(pre-computed `ReadOnlyMemory<float>` vector — the `VecBlob` proof and the no-generator path)*
- Test: `embeddings\tests\Qavren.Edge.VectorData.Tests\Fakes\DeterministicEmbeddingGenerator.cs`

**Approach — read this before writing.** This task opens real connections against SP1's `IEdgeDatabase` and the win-x64 natives already on disk, so `vec0` and FTS5 are live from the first test. The embedding generator here is a deterministic ~20-line fake producing a hash-seeded unit vector, which keeps every store assertion exact; the real generator only meets the store in Task 5.1.

**The vector encoding is SP1's `VecBlob`, and SP2 writes no blob code at all.** §12.2 is explicit: "Storage is the raw little-endian float32 blob through SP1's `VecBlob.From`/`ToFloats` — exactly vec0's wire format, and zero new blob code in SP2." That sentence is a constraint on `Internal\RecordMapper.cs`, which is the only file in either VectorData task that touches a vector's bytes, so it is stated here rather than left to be inferred:

- **Writing.** `RecordMapper` converts whatever the model's vector property holds — `ReadOnlyMemory<float>`, `Embedding<float>` (through `.Vector.Span`), `float[]`, or the `Embedding<float>` MEVD's dispatcher produced from a `string` source — to a `ReadOnlySpan<float>` and hands it to `Qavren.Edge.Sqlite.Vec.VecBlob.From(span)`. The `byte[]` that comes back is bound to `$embedding` as-is.
- **Reading.** The `IncludeVectors` path reads the blob column with `GetFieldValue<byte[]>` and passes it to `VecBlob.ToFloats(blob)`, whose `ArgumentException` on a length that is not a multiple of four is the only length validation SP2 needs.
- **Forbidden, and this is the whole point:** no `BitConverter.GetBytes` loop, no `MemoryMarshal.AsBytes`/`Cast<float, byte>`, no `BinaryPrimitives.WriteSingleLittleEndian` and no `unsafe` reinterpret anywhere under `embeddings\src\`. Several of those are *correct* on every RID this suite targets, which is exactly why the prohibition has to be written down — an implementer who hand-rolls one gets green tests, a green CI run, and a second definition of vec0's wire format that nobody will diff against SP1's the day a big-endian or `Half` variant appears. `MemoryMarshal.Cast` in particular is a one-liner that looks like an optimisation and silently encodes host-endian rather than little-endian.

`CollectionLifecycleTests.cs` owns the proof, and it is an assertion about the **bytes in the database**, not about a round-trip — a hand-rolled encoder would pass a round-trip test against its own decoder:

```csharp
[Fact]
public async Task AStoredVectorIsTheRawLittleEndianFloat32BlobSp1sVecBlobProduces()
{
    // A record with a PRE-COMPUTED ReadOnlyMemory<float> vector, not Note's string source: no
    // generator in the path, so the bytes on disk are the bytes RecordMapper wrote and nothing else.
    var vector = new[] { 1f, -2.5f, 0f, float.MaxValue };
    await collection.UpsertAsync(new RawVec { Key = "k", Embedding = vector });

    // Read the vec0 column as bytes, with no SP2 code in the path.
    var blob = await ReadRawVectorBlobAsync("notes_vec", rowid: 1);

    Assert.Equal(VecBlob.From(vector), blob);                    // byte-for-byte, SP1's encoder
    Assert.Equal(vector.Length * sizeof(float), blob.Length);
    Assert.Equal(vector, VecBlob.ToFloats(blob));                // and SP1's decoder reads it back
}
```

`RawVec` is a four-line record in `Records\RawVec.cs` — `[VectorStoreKey] string Key` and `[VectorStoreVector(4, DistanceFunction = DistanceFunction.CosineDistance)] ReadOnlyMemory<float> Embedding` — and it is the only record in the suite with a pre-computed vector property, which is also what makes it the record §12.5's "a store used only with pre-computed vectors needs no generator" claim is asserted against in Step 4.

**Writes are delete-then-insert on the vec0 side, not `UPDATE`,** for two reasons both true of sqlite-vec 0.1.9: a working `INSERT OR REPLACE` only arrives in 0.1.10-alpha, and vec0 overloads SQL `NULL` on a vector column to mean "no change". FTS5 is trigger-maintained off the data table, so it **never appears in the write path**. Everything runs inside one `IEdgeDatabase.ExecuteInTransactionAsync`, and the batch `UpsertAsync` generates every embedding in **one** `GenerateAsync` call before opening the transaction — which is why MEVD leaves batch `UpsertAsync` abstract with no default and why it is overridden here rather than inherited.

Delete is `DELETE FROM "notes" WHERE "Key" = $key`; the four triggers clean vec0 and FTS5, so no orphan survives even when an app deletes rows with its own SQL against the same `IEdgeDatabase`.

- [ ] **Step 1: The deterministic fake generator**

```csharp
using Microsoft.Extensions.AI;

namespace Qavren.Edge.VectorData.Tests.Fakes;

/// <summary>Hash-seeded unit vectors. Deterministic, so every store assertion is exact.</summary>
public sealed class DeterministicEmbeddingGenerator(int dimensions)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            var rng = new Random(value.GetHashCode(StringComparison.Ordinal));
            var v = new float[dimensions];
            double norm = 0;
            for (var i = 0; i < dimensions; i++) { v[i] = (float)(rng.NextDouble() - 0.5); norm += v[i] * v[i]; }
            var inv = (float)(1.0 / Math.Sqrt(norm));
            for (var i = 0; i < dimensions; i++) { v[i] *= inv; }
            result.Add(new Embedding<float>(v));
        }

        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this
           : serviceType == typeof(EmbeddingGeneratorMetadata)
               ? new EmbeddingGeneratorMetadata("fake", defaultModelDimensions: dimensions)
               : null;

    public void Dispose() { }
}
```

- [ ] **Step 2: `EdgeVectorStore`**

Exactly as §8, with the constructor's `embeddingGenerator` / `queryEmbeddingGenerator` parameters applied **only** where the matching `EdgeVectorStoreOptions` property is null — an explicitly-set option always wins.

`ListCollectionNamesAsync` returns data tables only, and **the exclusion is structural, not by name**. A virtual table is recognised from its own DDL in `sqlite_master.sql` (`USING vec0` / `USING fts5`), and FTS5's shadow tables are recognised by being the `_data`/`_idx`/`_content`/`_docsize`/`_config` children of a name that is itself an fts5 virtual table. `sqlite_%` is excluded as well. Name-prefix filtering would be wrong in **both** directions, because `VectorTableNameFormat` and `VectorTableName` let a sidecar be called anything, and a consumer's own data table may legitimately be called `foo_vec`. Upstream's connector leaks its vec0 tables here; this one does not.

`ListCollectionNamesTests.cs` asserts both directions, which is the only way to catch a name-prefix implementation — it would pass one and fail the other:

```csharp
[Fact]
public async Task ASidecarWithNoConventionalSuffixIsStillHidden() { /* VectorTableName = "zzz" */ }

[Fact]
public async Task AUserTableLiterallyCalledFooVecIsStillListed() { /* CREATE TABLE "foo_vec" */ }
```

plus `sqlite_%` and the five FTS5 shadow tables.

- [ ] **Step 3: `EdgeVectorStoreCollection<TKey, TRecord>` and the dynamic collection**

Exactly as §8 — and the **two-constructor arrangement is load-bearing, not stylistic**. The public reflecting constructor carries `[RequiresDynamicCode]` and `[RequiresUnreferencedCode]`. The `protected` constructor takes an already-built `MEVD.ProviderServices.CollectionModel` and carries **no** trim annotations, and it is the only one `EdgeDynamicVectorStoreCollection` chains to. Without it the "dynamic is the AOT-safe path" claim would be unverifiable: a derived ctor chaining to an annotated base ctor inherits IL2026 and IL3050, this repo sets `TreatWarningsAsErrors` true, and the only way to compile would be a suppression — which is exactly what Task 6.1's `trim-smoke` job exists to decide. `EdgeVectorStore.GetDynamicCollection` builds the model with `CollectionModelBuilder.BuildDynamic`, which reflects over nothing (the reflection-based `Build` throws for `Dictionary<string, object?>` by design).

`EnsureCollectionExistsAsync` runs every DDL statement from `EdgeVectorSchema.BuildCreateSql()` in **one** transaction, and before any of it:

1. Reads `sqlite_version()` once and throws `EdgeVectorModelException(SqliteVersionTooOld)` below 3.39 — `FULL OUTER JOIN` needs 3.39, `rowid IN (…)` push-down needs 3.38, `RETURNING` needs 3.35. SP1's native pins 3.53.4, so this only fires for a consumer who pointed `Qavren.Edge.Sqlite` at a system SQLite, and it fires with a sentence instead of a raw syntax error from the hybrid query.
2. Validates the configured generator's dimensions against the declared vector width and throws `VectorDimensionMismatch` naming both. The generator's width is read from `EmbeddingGeneratorMetadata.DefaultModelDimensions` via `GetService`; a third-party generator may publish none (and Semantic Kernel's own adapter never propagates it), in which case the check **defers to the first upsert**, where the actual vector length is compared instead. The README promises the deferred check, not a universal startup-time catch.

`SearchAsync`: `$k = top + Skip`, capped at 4096 (`SQLITE_VEC_VEC0_K_MAX`); over the cap throws `KnnLimitExceeded` naming the limit **before SQLite sees it**. `LIMIT` is never used in place of `k =`, following SP1's `Knn.BuildSql`. **`Skip` is applied client-side** by discarding the first N rows while reading, because `SQLITE_INDEX_CONSTRAINT_OFFSET` is skipped in every loop of `vec0BestIndex` and never given an `argvIndex`; that is documented on the member and never silently dropped. `ScoreThreshold` maps to `v.distance <= $t` and `VectorSearchResult.Score` carries the distance verbatim.

`HybridSearchAsync` emits `BuildHybridRrfSql`. Defaults `$rrfK = 60`, `$wVector = $wKeyword = 1.0`, `$cand = min((top + skip) * 4, 4096)`, all overridable per query through `EdgeHybridSearchOptions<TRecord>`. `HybridSearchOptions.ScoreThreshold` is applied to the RRF score as a **client-side `>=`** after the query — the opposite of vector search's pushed-down `distance <=` — and both facts sit on the XML docs. With several full-text columns, `HybridSearchOptions.AdditionalProperty` (resolved through `CollectionModel.GetFullTextDataPropertyOrSingle`) narrows the expression with FTS5's column filter. An empty or all-whitespace keyword collection **short-circuits the FTS lane and degenerates to plain KNN** rather than emitting a malformed `MATCH`. With no `IsFullTextIndexed` property at all the FTS5 table is not created and `HybridSearchAsync` throws `EdgeVectorModelException(FullTextPropertyMissing)` naming the attribute to add; `AlwaysCreateFullTextIndex` is the opt-in for creating it anyway.

Any `SqliteException` in a store operation is wrapped in `EdgeVectorStoreException` with all four MEVD fields populated.

- [ ] **Step 4: Builder extensions**

`AddVectorStore`, its keyed overload, `AddVectorCollectionMigration<TKey,TRecord>`, the definition-taking `AddVectorCollectionMigration`, and `AddVectorCollection<TKey,TRecord>` exactly as §8.

`AddVectorStore`'s registered factory is the wiring that makes §4.3's four calls work, and §12.5 is normative about every step:

1. `sp.GetService<IEmbeddingGenerator>()` — **`GetService`, never `GetRequiredService`**: a store used only with pre-computed `ReadOnlyMemory<float>` vectors needs no generator, and demanding one would break that. The keyed overload tries the keyed generator under the same name first, then the unkeyed one.
2. The result goes to `EdgeVectorStore`'s `embeddingGenerator` parameter, applied only where the option is still null.
3. The query generator is resolved **from the generator itself, by service key, with no reference to `Qavren.Edge.Embeddings.Onnx`**:

```csharp
var query = generator.GetService(typeof(IEmbeddingGenerator),
                                 EdgeVectorData.QueryGeneratorServiceKey)
            as IEmbeddingGenerator
            ?? generator;
```

   Three details are deliberate. The key is **this package's own constant**, because §2 decision 2 forbids referencing `Embeddings.Onnx` — Task 5.1 asserts the two literals are equal. It is **`GetService`, not `AsQueryGenerator()`**, which is a convenience for direct consumers of the ONNX package and is not on this path. And it asks for **`typeof(IEmbeddingGenerator)`, the non-generic type**, because a third-party generator may be typed `IEmbeddingGenerator<DataContent, Embedding<float>>` and asking for a closed generic it does not implement would return null and quietly lose the query lane.
4. If, at the first upsert or search of a `string` source property, both are still null, throw `EdgeVectorStoreException(EmbeddingGeneratorMissing)` naming the property and pointing at `AddOnnxEmbeddings`.

Builder-call order does not matter, because the factory runs at **resolve** time.

`AddVectorCollectionMigration` is the **recommended** path: it emits the same statements as an SP1 `IEdgeMigration`, so the DDL runs under the existing migrator at order 100 with `PRAGMA user_version` bookkeeping and SP1's failure semantics. `AddVectorCollection` (startup order 300) and bare `EnsureCollectionExistsAsync` remain for ad-hoc use. `EdgeVectorSchema.BuildCreateSql` is the single source for all three.

- [ ] **Step 5: Lifecycle observer and diagnostics**

`VectorDataLifecycleObserver` registers with `TryAddEnumerable` and derives from SP1's `EdgeLifecycleObserver`.

- `Sleeping` → per collection with an FTS table, one `INSERT INTO "notes_fts"("notes_fts", rank) VALUES('merge', 500)`, bounded to **four iterations and a two-second budget**, each inside its own try/catch, abandoned rather than failed when the budget expires; logs `FtsMergeCompleted` (803). The full `'optimize'` form merges every b-tree into one and SQLite's docs warn it "can take a long time to run" — precisely wrong to do inline on a phone, which is why only the incremental form runs and only on this event. SP1's `SqliteLifecycleObserver` already checkpoints WAL on the same event; registration order puts the merge **first** so its pages land in that checkpoint.
- Everything else → nothing. Pool clearing is SP1's job and it already does it.

`VectorDataDiagnosticsContributor` reports `ComponentName = "Qavren.Edge.VectorData"` with `database`, `sqliteVersion`, `vecVersion` (note `vec_version()` returns `"v0.1.9"` **with a leading `v`**, as SP1 already documents), per collection `{dataTable, vecTable, ftsTable, dimensions, distanceFunction, chunkSize, ftsTokenizer, keyType, generator, generatorDimensions, rrfK, weights}`, and, when `IncludeRowCountsInDiagnostics` is set, `rowCount` / `vecRowCount` / `ftsRowCount`. The first two diverging means an upsert transaction was interrupted and is the first thing to look at in a bug report; counting rows is one query per collection, so it is off by default.

- [ ] **Step 6: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp2\embeddings\tests\Qavren.Edge.VectorData.Tests\Qavren.Edge.VectorData.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0 `
  -p:ArtifactsPath="D:\Local\Temp\qedge-sp2\w4\t42"
```

Expected: `Passed! - Failed: 0`, exit code 0. This run opens real SQLite connections over `foundation\native\artifacts\win-x64\qedge_sqlite3.dll` and creates real `vec0` and FTS5 tables.

---

### Task 4.3: Wave 4 close — sequential re-verify and commit *(integrator)*

**Local-verifiable:** yes.

**Files:** none of its own. It reads what wave 4 wrote and commits it.

**Approach.** The parallel phase ran every verify under `-p:ArtifactsPath=D:\Local\Temp\qedge-sp2\w4\t…`, so no two tasks contended for one `obj/` or `bin/`. That isolation is exactly why the wave is **not** yet proved: nothing has built these projects in the tree the next wave, and CI, will use. This task deletes the scratch roots, re-runs every verify **sequentially and un-isolated**, and only then commits.

- [ ] **Step 1: Clear the wave's isolated outputs**

```powershell
Remove-Item -Recurse -Force "D:\Local\Temp\qedge-sp2\w4" -ErrorAction SilentlyContinue
```

- [ ] **Step 2: Re-run every verify in the wave, sequentially, in the real tree**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp2"
foreach ($p in @(
  "embeddings\tests\Qavren.Edge.Embeddings.Tests\Qavren.Edge.Embeddings.Tests.csproj",
  "embeddings\tests\Qavren.Edge.VectorData.Tests\Qavren.Edge.VectorData.Tests.csproj")) {
  dotnet run --project "$root\$p" -c Release -f net10.0 -p:TargetFrameworks=net10.0
  if ($LASTEXITCODE -ne 0) { throw "FAILED: $p" }
}
Write-Host 'OK: wave 4 re-verified sequentially'
```

Expected: `OK: wave 4 re-verified sequentially`, exit code 0. A failure here that did not appear in the parallel phase is real — the usual cause is a project that only restored because a sibling task had already written its assets file.

- [ ] **Step 3: Commit the wave**

One commit, Conventional Commits, no `Co-Authored-By` and no AI attribution, on `feat/sp2-embeddings`. Never on `main`.

```
feat(sp2): ONNX embedding generator and the MEVD collections

- Qavren.Edge.Embeddings.Onnx: tokenizer, pooler, presets, IEmbeddingGenerator leaf
- Qavren.Edge.VectorData: store, typed and dynamic collections, writes, KNN, RRF hybrid
- PinnedSequenceLength closes spec 16.1's session-factory assertion across the layer boundary
```

- [ ] **Step 4: Verify**

Step 2 printed its `OK` line and `git status --short` is clean. Wave 5 may start.

---

## WAVE 5 — Integration, conformance, cipher

### Task 5.1: Tier-2 integration tests — KNN vs brute force, pre-filter, RRF, and the §4.3 happy path

**Local-verifiable:** yes.

**Files:**
- Edit: `embeddings\tests\Qavren.Edge.VectorData.Tests\Qavren.Edge.VectorData.Tests.csproj` *(add the `Embeddings.Onnx` reference and the fixture link)*
- Test: `embeddings\tests\Qavren.Edge.VectorData.Tests\KnnVsBruteForceTests.cs`
- Test: `embeddings\tests\Qavren.Edge.VectorData.Tests\PreFilterTests.cs`
- Test: `embeddings\tests\Qavren.Edge.VectorData.Tests\RrfFusionTests.cs`
- Test: `embeddings\tests\Qavren.Edge.VectorData.Tests\IncludeVectorsTests.cs`
- Test: `embeddings\tests\Qavren.Edge.VectorData.Tests\IntegrityTests.cs`
- Test: `embeddings\tests\Qavren.Edge.VectorData.Tests\HappyPathTests.cs`
- Test: `embeddings\tests\Qavren.Edge.VectorData.Tests\ServiceKeyParityTests.cs`

- [ ] **Step 1: Add the reference that makes this task possible**

```xml
    <!-- Added in wave 5, not wave 1, on purpose: until Task 4.1 froze Embeddings.Onnx this
         reference would have put a project another task was writing into this project's build
         graph. This is also the ONLY test project in the suite that sees all three SP2 assemblies
         at once, which is why the service-key parity assertion lives here. -->
    <ProjectReference Include="..\..\src\Qavren.Edge.Embeddings.Onnx\Qavren.Edge.Embeddings.Onnx.csproj" />
```
```xml
  <ItemGroup>
    <Compile Include="..\fixtures\TinyModels.g.cs" Link="Fixtures\TinyModels.g.cs" />
  </ItemGroup>
```

- [ ] **Step 2: KNN equals brute force, at 384-d and at 768-d**

The same shape SP1's own vector tests already use: 1,000 random vectors, k ∈ {1, 10, 100}, the store's ordered result compared against a brute-force cosine ranking computed in C#. Then **the same assertion repeated at 768-d**, which is where the wide-vector path is exercised — tier 1 has no 768-d fixture and cannot cheaply have one (the fixture's output width is the width of the `Gather` initializer, so the smallest conceivable 768-wide float32 table is already 6,144 bytes of initializer, six times the fixture budget). The deterministic fake generator makes the width free here.

```csharp
[Theory]
[InlineData(384, 1)] [InlineData(384, 10)] [InlineData(384, 100)]
[InlineData(768, 1)] [InlineData(768, 10)] [InlineData(768, 100)]
public async Task KnnMatchesBruteForceCosine(int dimensions, int k) { /* … */ }
```

- [ ] **Step 3: The pre-filter is a pre-filter**

`k = 10` with a filter matching **12 of 500** rows returns the 10 nearest **matching** rows — not the 10 nearest overall then filtered down to 3. Includes an **`OR`** filter, which no vec0 metadata column could express and which is the whole reason §13.1 pushes the filter as `rowid IN (…)` rather than using the metadata tier.

- [ ] **Step 4: RRF ordering, against a hand-computed fusion table**

A corpus where the two lanes **disagree**, with the expected fused order computed by hand from `1/(k+rank)` per lane, plus a test asserting that `RrfK` and the per-lane weights actually move the result. This is the only way to catch the bm25 sign trap: FTS5's `rank` is the standard bm25 score multiplied by −1, so a "sort descending because higher is better" mistake produces a plausible-looking but inverted keyword lane.

One test's **name** states the polarity inversion, per §13.3:

```csharp
[Fact]
public async Task HybridScoreIsASimilarityWhereHigherIsBetter_TheOppositeOfSearchAsyncsDistance() { /* … */ }
```

- [ ] **Step 5: `IncludeVectors` round-trips a non-empty vector**

On **both** `SearchAsync` and `HybridSearchAsync`, the returned vector is non-empty and equal to what was upserted. The hybrid query needs two coordinated fragments for this — the `LEFT JOIN` and the `, nv."embedding"` projection — and only one of them is caught by a golden-SQL string comparison if the other is written and the expected value is updated to match. This behavioural assertion is what makes a half-applied bracket fail.

- [ ] **Step 6: Integrity, `Skip`, thresholds, limits, triggers**

After 200 upserts and 50 deletes — **including deletes issued as raw SQL against the same `IEdgeDatabase`**, which is what proves the cascade is a trigger rather than provider code — `COUNT(*)` on all three tables agrees. Plus `Skip` semantics, `ScoreThreshold` polarity, `k > 4096` rejection with `KnnLimitExceeded`, empty-keyword degradation to plain KNN, and FTS trigger sync on insert / update / delete.

- [ ] **Step 7: The §4.3 happy path, resolved from a real container, in both call orders**

```csharp
[Theory]
[InlineData(true)]   // AddOnnxEmbeddings before AddVectorStore
[InlineData(false)]  // and after - the factory resolves at resolve time, so order must not matter
public async Task TheFourCallHappyPathResolvesTheGeneratorAndItsQuerySibling(bool embeddingsFirst)
{
    // builder.UseQavrenEdge(edge => edge.UseSqliteNative().AddSqlite(...)
    //        .AddOnnxEmbeddings(...).AddVectorStore()
    //        .AddVectorCollectionMigration<string, Note>(version: 1, "notes"));
    // then GetCollection<string, Note>("notes"), upsert, hybrid search.
    // Asserts the store received the DI-registered generator AND its query sibling, rather than
    // throwing EmbeddingGeneratorMissing on the first upsert of the string source property.
}
```

This is the test that stops §4.3's flagship snippet from regressing into `EmbeddingGeneratorMissing`. It runs with the generator backed by `TinyModels.HiddenStates` through the internal factory hook, so it downloads nothing.

- [ ] **Step 8: The two service-key constants are equal**

One line, and it is the entire defence against the duplicated literal drifting:

```csharp
[Fact]
public void TheQueryGeneratorServiceKeysAreTheSameLiteral()
{
    // The literal is duplicated because spec 2 decision 2 forbids Qavren.Edge.VectorData from
    // referencing Qavren.Edge.Embeddings.Onnx. This project is the only place in the suite where
    // both symbols are visible at once.
    Assert.Equal(Qavren.Edge.Embeddings.Onnx.EdgeEmbeddings.QueryServiceKey,
                 Qavren.Edge.VectorData.EdgeVectorData.QueryGeneratorServiceKey);
}
```

- [ ] **Step 9: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp2\embeddings\tests\Qavren.Edge.VectorData.Tests\Qavren.Edge.VectorData.Tests.csproj" -c Release -f net10.0 -p:TargetFrameworks=net10.0 `
  -p:ArtifactsPath="D:\Local\Temp\qedge-sp2\w5\t51"
```

Expected: `Passed! - Failed: 0`, exit code 0.

---

### Task 5.2: `Microsoft.Extensions.VectorData.ConformanceTests` against both record paths

**Local-verifiable:** yes.

**Files:**
- Create: `embeddings\tests\Qavren.Edge.VectorData.Conformance.Tests\EdgeTestStore.cs`
- Create: `embeddings\tests\Qavren.Edge.VectorData.Conformance.Tests\Fixtures\*.cs`
- Create: `embeddings\tests\Qavren.Edge.VectorData.Conformance.Tests\SkipList.md`
- Create: `embeddings\tests\Qavren.Edge.VectorData.Conformance.Tests\*Tests.cs` *(one per conformance suite)*

**Approach.** The conformance suite is **the gate**, not a nice-to-have: §3 lists "passes `Microsoft.Extensions.VectorData.ConformanceTests` on both the typed and the dynamic record paths" as a goal, and this project is the only place that claim is tested. Run it against **both** `EdgeVectorStoreCollection<TKey,TRecord>` and `EdgeDynamicVectorStoreCollection` — the dynamic path is first-class, not a subset.

The suite's xunit pin is 3.2.2, matching this repo (verified; §19 item 6 is closed). No separate pin is needed and the suite is not dropped.

- [ ] **Step 1: The test store**

Implement the suite's `TestStore` abstraction over a temp-file `IEdgeDatabase` built with `UseSqliteNative().AddSqlite(...)`, so every conformance run exercises the real `vec0` and FTS5 natives rather than a mock.

- [ ] **Step 2: Derive one class per conformance suite**

For each suite the package exposes, derive a typed fixture and a dynamic fixture. Where a suite needs an embedding generator, hand it the deterministic fake from Task 4.2.

- [ ] **Step 3: The skip-list is the honest record of the gap**

Every skipped test is enumerated **with a comment naming the v1 cut that causes it**, and the same list is mirrored in `SkipList.md` so a reader does not have to grep attributes. Expected entries, from §20: multiple vector properties per collection; `int8` / `bit` vector element types; binary-quantization rescore; ANN index kinds; Matryoshka dimension truncation; `CompactAsync`; cursor-based pagination; down-migrations. A skip with no named cut is a bug in this task, not a cut.

- [ ] **Step 4: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp2\embeddings\tests\Qavren.Edge.VectorData.Conformance.Tests\Qavren.Edge.VectorData.Conformance.Tests.csproj" -c Release -p:TargetFrameworks=net10.0 `
  -p:ArtifactsPath="D:\Local\Temp\qedge-sp2\w5\t52"
```

Expected: `Passed!` with zero failures and a skip count equal to the number of entries in `SkipList.md`, exit code 0. No `-f`: the project is single-TFM.

---

### Task 5.3: The SQLCipher collection-lifecycle test

**Local-verifiable:** yes — `qedge_sqlcipher.dll` for win-x64 is already at `foundation\native\artifacts\win-x64`.

**Files:**
- Edit: `foundation\tests\Qavren.Edge.Sqlite.Cipher.Tests\Qavren.Edge.Sqlite.Cipher.Tests.csproj` *(add the `VectorData` reference)*
- Test: `foundation\tests\Qavren.Edge.Sqlite.Cipher.Tests\EncryptedVectorStoreTests.cs`

**Approach.** This is the thing the upstream connector structurally cannot do, and it is the proof that SP2's provider rides on `IEdgeDatabase` rather than on a connection string. `CommunityToolkit.VectorData.SqliteVec` constructs its own `SqliteConnection` from a connection string and calls `connection.LoadExtension("vec0")` on every open — impossible under SP1's `SQLITE_OMIT_LOAD_EXTENSION` build, and incompatible with `IEdgeDatabase`'s pragma/cipher/pooling contract.

It lives in `Qavren.Edge.Sqlite.Cipher.Tests` because that project is already the one SP1 keeps **out** of the device lanes: it needs `Qavren.Edge.Sqlite.Native.Cipher`, and referencing both native packages in one app is a configuration error the startup pipeline rejects.

- [ ] **Step 1: Add the reference**

```xml
    <ProjectReference Include="..\..\..\embeddings\src\Qavren.Edge.VectorData\Qavren.Edge.VectorData.csproj" />
```

- [ ] **Step 2: One test, the full lifecycle over a keyed connection**

Open a SQLCipher database through `UseSqliteNativeCipher()` with a raw key, then run create → upsert → KNN → hybrid → delete against it, asserting the same results the plain-SQLite tests get. Then reopen with the **wrong** key and assert `EdgeDatabaseKeyException` — so the test proves the database was genuinely encrypted rather than silently plain.

- [ ] **Step 3: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp2\foundation\tests\Qavren.Edge.Sqlite.Cipher.Tests\Qavren.Edge.Sqlite.Cipher.Tests.csproj" -c Release -p:TargetFrameworks=net10.0 `
  -p:ArtifactsPath="D:\Local\Temp\qedge-sp2\w5\t53"
```

Expected: `Passed!` with the pre-existing cipher tests still green plus the new ones, exit code 0.

---

### Task 5.4: Wave 5 close — sequential re-verify and commit *(integrator)*

**Local-verifiable:** yes.

**Files:** none of its own. It reads what wave 5 wrote and commits it.

**Approach.** The parallel phase ran every verify under `-p:ArtifactsPath=D:\Local\Temp\qedge-sp2\w5\t…`, so no two tasks contended for one `obj/` or `bin/`. That isolation is exactly why the wave is **not** yet proved: nothing has built these projects in the tree the next wave, and CI, will use. This task deletes the scratch roots, re-runs every verify **sequentially and un-isolated**, and only then commits. Wave 5 is the wave where isolation mattered most, and it is also the one with a **new project edge**: Task 5.3 added a `ProjectReference` from `Qavren.Edge.Sqlite.Cipher.Tests` to `Qavren.Edge.VectorData` while Tasks 5.1 and 5.2 were building that same project. Step 2 therefore ends with a full solution restore — the first time the new edge is resolved against an assets graph nobody else is writing.

- [ ] **Step 1: Clear the wave's isolated outputs**

```powershell
Remove-Item -Recurse -Force "D:\Local\Temp\qedge-sp2\w5" -ErrorAction SilentlyContinue
```

- [ ] **Step 2: Re-run every verify in the wave, sequentially, in the real tree**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp2"
foreach ($p in @(
  "embeddings\tests\Qavren.Edge.VectorData.Tests\Qavren.Edge.VectorData.Tests.csproj")) {
  dotnet run --project "$root\$p" -c Release -f net10.0 -p:TargetFrameworks=net10.0
  if ($LASTEXITCODE -ne 0) { throw "FAILED: $p" }
}
foreach ($p in @(
  "embeddings\tests\Qavren.Edge.VectorData.Conformance.Tests\Qavren.Edge.VectorData.Conformance.Tests.csproj",
  "foundation\tests\Qavren.Edge.Sqlite.Cipher.Tests\Qavren.Edge.Sqlite.Cipher.Tests.csproj")) {
  dotnet run --project "$root\$p" -c Release -p:TargetFrameworks=net10.0
  if ($LASTEXITCODE -ne 0) { throw "FAILED: $p" }
}
# The two new project edges this wave created, restored once in the real tree.
dotnet restore "$root\QavrenEdge.slnx"
if ($LASTEXITCODE -ne 0) { throw "FAILED: solution restore after the new Cipher.Tests -> VectorData edge" }
Write-Host 'OK: wave 5 re-verified sequentially'
```

Expected: `OK: wave 5 re-verified sequentially`, exit code 0. A failure here that did not appear in the parallel phase is real — the usual cause is a project that only restored because a sibling task had already written its assets file.

- [ ] **Step 3: Commit the wave**

One commit, Conventional Commits, no `Co-Authored-By` and no AI attribution, on `feat/sp2-embeddings`. Never on `main`.

```
feat(sp2): tier-2 integration, MEVD conformance and the SQLCipher lifecycle test

- KNN vs brute force at 384-d and 768-d, OR pre-filter, RRF fusion table, IncludeVectors
- the spec 4.3 happy path from a real container, in both builder-call orders
- conformance on the typed and dynamic paths with an enumerated skip-list
- the full collection lifecycle over a keyed SQLCipher connection
```

- [ ] **Step 4: Verify**

Step 2 printed its `OK` line and `git status --short` is clean. Wave 6 may start.

---

## WAVE 6 — Trim smoke, tier 3, device-lane assertions

### Task 6.1: The `trim-smoke` console

**Local-verifiable:** yes — published and run here for `win-x64`; CI publishes `linux-x64`.

**Files:**
- Create: `embeddings\tools\Qavren.Edge.TrimSmoke\Program.cs`
- Edit: `embeddings\tools\Qavren.Edge.TrimSmoke\Qavren.Edge.TrimSmoke.csproj` *(add the fixture `<Compile Include>` link — Step 2)*

**No fixture file is created here.** Plan adjustment 6: `embeddings\tests\fixtures\TinyModels.g.cs` is **linked** into the four projects that consume it and **never copied**, and this project is the fourth — at a different relative depth from the three test projects, which is the only reason its `Include` path differs. A `TinyModelFixture.cs` of its own would be a second copy of a generated file, which is exactly what adjustment 6 exists to forbid.

**Approach.** §19 item 12 is a measured fact, not an opinion: **neither ORT's nor `Microsoft.ML.Tokenizers`'s managed assemblies carry `IsAotCompatible`, trim-analysis attributes or ILLink descriptors.** What a trimmed or AOT publish actually does is therefore untested and cannot be inferred, and this program is what decides what the README claims.

It must go through `EdgeDynamicVectorStoreCollection` — reached with `GetDynamicCollection` and a `VectorStoreCollectionDefinition`, **never** `GetCollection<TKey,TRecord>`. Publishing the dynamic path is the point: it is what turns §12.6's "the only trim/AOT-safe path" from an assertion into a measured claim, and it is only compilable at all because of the non-annotated `CollectionModel`-taking base constructor Task 4.2 shipped.

- [ ] **Step 1: `Program.cs`**

Tokenize a fixed string, embed it with the base64 fixture, then round-trip one vec0 upsert and one search through the dynamic collection. Print each stage and exit non-zero on any mismatch — a trimmed publish that silently returns an empty result must fail the job, not pass it quietly. Nothing in the program may use reflection over a record type, an anonymous type, or `SqliteConnectionExtensions.ToParameters(object)` (which SP1 annotates `RequiresUnreferencedCode` precisely because the trimmer deletes what it reflects over).

- [ ] **Step 2: Link the fixture**

```xml
  <ItemGroup>
    <Compile Include="..\..\tests\fixtures\TinyModels.g.cs" Link="Fixtures\TinyModels.g.cs" />
  </ItemGroup>
```

- [ ] **Step 3: Verify**

```powershell
dotnet publish "C:\Users\steve\projects\qavren-edge-sp2\embeddings\tools\Qavren.Edge.TrimSmoke\Qavren.Edge.TrimSmoke.csproj" `
  -c Release -r win-x64 --self-contained true -p:PublishTrimmed=true -p:TargetFrameworks=net10.0 `
  -p:ArtifactsPath="D:\Local\Temp\qedge-sp2\w6\t61" `
  -o "C:\Users\steve\AppData\Local\Temp\qedge-trimsmoke"
& "C:\Users\steve\AppData\Local\Temp\qedge-trimsmoke\Qavren.Edge.TrimSmoke.exe"
```

Expected: the publish succeeds with no IL2026/IL3050 **errors** (warnings from ORT and Tokenizers are expected and are exactly what §19 item 12 is measuring — record them), and the program prints its stages and exits 0. Repeat with `-p:PublishAot=true` and record the outcome; the AOT leg is `continue-on-error` in CI for one release cycle and then either becomes required or the AOT claim is dropped from the README.

---

### Task 6.2: Tier 3 — the real-model facts, the pinned reference vectors, and `SkipUnless`

**Local-verifiable:** yes for the skip path, which is the one that runs on every other lane. The opt-in real-model run is also runnable here (this box has network); its scheduled execution is CI-only.

**Files:**
- Create: `embeddings\tests\Qavren.Edge.Embeddings.Tests\Tier3\ModelAvailable.cs`
- Create: `embeddings\tests\Qavren.Edge.Embeddings.Tests\Tier3\RealModelFacts.cs`
- Create: `embeddings\tests\Qavren.Edge.Embeddings.Tests\Tier3\reference-vectors.json` *(generated once, committed)*
- Create: `embeddings\tests\Qavren.Edge.Embeddings.Tests\Tier3\README.md`
- Edit: `embeddings\tests\Qavren.Edge.Embeddings.Tests\Qavren.Edge.Embeddings.Tests.csproj` *(copy the JSON to output)*

**Approach — read this before writing.** §16.3 is four sentences and every one of them is a constraint somebody gets wrong. Task 2.3 already shipped the workflow half in wave 2 — the `schedule` trigger, the `model-tests` job, the `actions/cache@v6.1.0` step keyed on the model's SHA-256, and the env vars this task reads. This task is the C#.

**The trap that gives the section its first sentence: `SkipUnless` is evaluated at *runtime*.** xunit.v3 evaluates the named static property **after** the test class has been constructed, after every `BeforeAfterTestAttribute` has run, and it disposes the instance afterwards. So a test class that builds its session in its constructor **loads a 23 MB model on every lane that "skips" it** — four device lanes, three host legs, every PR. The session, the tokenizer and the model store are therefore all built **inside the test body**, after the skip decision has already been taken:

```csharp
namespace Qavren.Edge.Embeddings.Tests.Tier3;

/// <summary>
/// The SkipUnless gate. Evaluated at RUNTIME, after the class constructor and after every
/// BeforeAfterTestAttribute - which is exactly why nothing in this class's constructor may touch
/// a model. A "skipped" tier-3 test that loaded 23 MB first would cost every device lane and
/// every PR leg the download this tier exists to keep off them.
/// </summary>
public static class ModelAvailable
{
    /// <summary>Set only by ci.yml's nightly `model-tests` job, and by a developer opting in.</summary>
    public static bool Yes =>
        Environment.GetEnvironmentVariable("QAVREN_EDGE_MODEL_DIR") is { Length: > 0 } dir
        && File.Exists(Path.Combine(dir, "onnx", "model_qint8_arm64.onnx"))
        && File.Exists(Path.Combine(dir, "vocab.txt"));
}
```

- [ ] **Step 1: The facts**

```csharp
public sealed class RealModelFacts
{
    // No constructor. Nothing is loaded until a test body runs - see ModelAvailable's remarks.

    [Fact(Skip = "QAVREN_EDGE_MODEL_DIR not set", SkipUnless = nameof(ModelAvailable.Yes))]
    public async Task EmbeddingsMatchThePinnedReferenceVectors()
    {
        await using var host = Tier3Host.Build();          // builds the container HERE, not in a ctor
        var generator = host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        var reference = ReferenceVectors.Load();           // the committed JSON, beside the assembly

        var produced = await generator.GenerateAsync(reference.Select(r => r.Text));

        Assert.Equal(reference.Count, produced.Count);
        for (var i = 0; i < reference.Count; i++)
        {
            var cosine = Cosine(reference[i].Vector, produced[i].Vector.Span);
            // 1e-3, and NOT exact equality. ORT CPU bit-determinism across windows-2025 /
            // ubuntu-24.04 / macos-15 and across x64/arm64 is not established, so an exact
            // comparison would be a flake generator rather than a regression detector.
            Assert.True(1.0 - cosine < 1e-3,
                $"'{reference[i].Text}': cosine {cosine:F6} against the pinned reference");
        }
    }

    [Fact(Skip = "QAVREN_EDGE_MODEL_DIR not set", SkipUnless = nameof(ModelAvailable.Yes))]
    public async Task TheRealGraphsSignatureIsWhatThePresetClaims()
    {
        await using var host = Tier3Host.Build();
        var info = host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()
                       .GetService(typeof(OnnxEmbeddingGeneratorInfo)) as OnnxEmbeddingGeneratorInfo;

        Assert.NotNull(info);
        Assert.Equal(384, info.Dimensions);
        Assert.Equal(EmbeddingPooling.Mean, info.Pooling);
        Assert.Equal(30_522, info.VocabularySize);
        // The provisioned graph's digest is the one the generated manifest pins. If these ever
        // disagree, the cache was poisoned or the revision moved - the two failures the content-
        // addressed cache key exists to make loud.
        Assert.Equal(
            "4278337fd0ff3c68bfb6291042cad8ab363e1d9fbc43dcb499fe91c871902474",
            info.GraphSha256);
    }

    [Fact(Skip = "QAVREN_EDGE_MODEL_DIR not set", SkipUnless = nameof(ModelAvailable.Yes))]
    public async Task NormalisedEmbeddingsAreUnitLength()
    {
        await using var host = Tier3Host.Build();
        var generator = host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        var produced = await generator.GenerateAsync(["a roof leak after the storm"]);

        var lengthSquared = 0f;
        foreach (var x in produced[0].Vector.Span) { lengthSquared += x * x; }
        Assert.Equal(1f, lengthSquared, 4);
    }

    private static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b) { /* dot / (|a||b|) */ }
}
```

`Tier3Host.Build()` is four builder calls over `QAVREN_EDGE_MODEL_DIR` — `UseSqliteNative().AddSqlite(…).AddOnnxEmbeddings(o => o.Preset = EmbeddingPresets.MiniLmL6V2Int8)` with a `FileOnnxModelSource` pointed at that directory and `AllowDownload = false`, because the workflow already fetched and hash-verified the files and a test that could silently re-download would defeat the cache.

- [ ] **Step 2: Generate `reference-vectors.json` once, by hand, and commit it**

§16.3: "Reference vectors are generated once from a known-good run and pinned as a small JSON." Twelve short strings — the same sentences the sample app's Search page uses, so a human can compare the two — each with its 384 floats at `R9` round-trip precision. About 90 KB, which is text and diffable, and it is the **only** committed artifact in this plan that is not reviewable by reading: a reviewer checks that it was regenerated by the documented command and that the tests move when it does, not that float 200 is right.

Generate it with the same run the tests assert against, on this box, then never again unless the pinned revision moves:

```powershell
$env:QAVREN_EDGE_MODEL_DIR = "C:\Users\steve\AppData\Local\Temp\qedge-model"
$env:QAVREN_EDGE_WRITE_REFERENCE = "1"
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp2\embeddings\tests\Qavren.Edge.Embeddings.Tests\Qavren.Edge.Embeddings.Tests.csproj" `
  -c Release -f net10.0 -p:TargetFrameworks=net10.0 `
  -p:ArtifactsPath="D:\Local\Temp\qedge-sp2\w6\t62"
```

`QAVREN_EDGE_WRITE_REFERENCE` makes `EmbeddingsMatchThePinnedReferenceVectors` **write** the JSON instead of asserting against it, and it fails loudly if the file already exists — a regeneration that silently overwrites the baseline turns a real regression into a green run, which is the one way a pinned-baseline test can be worse than no test.

Download the model into that directory first, with the same two `curl` commands and the same `sha256sum -c` check `ci.yml`'s `model-tests` job runs. The digests are `4278337f…` for `onnx/model_qint8_arm64.onnx` and `07eced37…` for `vocab.txt`.

- [ ] **Step 3: Copy the JSON to the test output**

```xml
  <ItemGroup>
    <None Update="Tier3\reference-vectors.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

An `EmbeddedResource` would work too and is worse here: the device lanes carry this assembly, and 90 KB of baked-in floats travels to four devices to be skipped. `CopyToOutputDirectory` keeps it a file the lane can simply not read.

- [ ] **Step 4: `Tier3\README.md`**

Four lines: what sets `QAVREN_EDGE_MODEL_DIR`, that the lane is nightly and never on a PR, that the cache key is the model's SHA-256 and not a URL or a date, and the regeneration command from Step 2 with its "delete the file first, on purpose" warning.

- [ ] **Step 5: Verify — the skip path, which is the one every other lane takes**

```powershell
Remove-Item Env:QAVREN_EDGE_MODEL_DIR -ErrorAction SilentlyContinue
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp2\embeddings\tests\Qavren.Edge.Embeddings.Tests\Qavren.Edge.Embeddings.Tests.csproj" `
  -c Release -f net10.0 -p:TargetFrameworks=net10.0 `
  -p:ArtifactsPath="D:\Local\Temp\qedge-sp2\w6\t62"
```

Expected: `Passed!` with **Failed: 0** and **Skipped: 3**, exit code 0, and — the assertion that actually matters here — **no model directory is created and nothing is downloaded**. Confirm the second half by checking that the run took no longer than the wave-4 run of the same project; a tier-3 test that loaded its model in a constructor would be visible as tens of seconds and a 23 MB write.

- [ ] **Step 6: Verify — the opt-in real run**

With `QAVREN_EDGE_MODEL_DIR` set to the Step 2 directory, the same command reports **Skipped: 0** and all three facts green. Run it once here and record the result; its scheduled execution is CI-only.

---

### Task 6.3: Device-lane assertions — §16.4's platform-only tests

**Local-verifiable:** partly. The `net10.0` host leg and the `net10.0-android` **compile** run here; every device **execution** is CI-only, and the iOS compile follows Task 2.2 Step 8's probe.

**Files:**
- Create: `embeddings\tests\Qavren.Edge.Onnx.Tests\DeviceFactAttribute.cs`
- Create: `embeddings\tests\Qavren.Edge.Onnx.Tests\Platforms\Android\AndroidDeviceFacts.cs`
- Create: `embeddings\tests\Qavren.Edge.Onnx.Tests\Platforms\iOS\AppleDeviceFacts.cs`
- Create: `embeddings\tests\Qavren.Edge.Onnx.Tests\DeviceDiagnosticsFacts.cs`
- Edit: `embeddings\tests\Qavren.Edge.Onnx.Tests\Qavren.Edge.Onnx.Tests.csproj` *(the `Platforms\**` compile-item guards)*

**Approach — read this before writing.** §16.4 lists what the device lanes "prove and nothing else can", and an earlier draft of this plan treated that paragraph as a property of the lanes rather than as code somebody writes. It is code. Task 2.2's `ModelPathsTests` covers `DefaultEdgeModelPaths` — the desktop `<Data>/models` path — and its `ResourceMonitorTests` covers the desktop `GC.GetGCMemoryInfo()` path. Neither can run on a device and neither asserts a single platform API. Everything below can only be written under a platform TFM, which is why it lives behind the same `Platforms\**` + `GetTargetPlatformIdentifier` compile-item pattern `Qavren.Edge.Onnx` itself uses:

```xml
  <ItemGroup>
    <Compile Remove="Platforms\**\*.cs" />
  </ItemGroup>
  <ItemGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'">
    <Compile Include="Platforms\Android\**\*.cs" />
  </ItemGroup>
  <ItemGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios'
                     or $([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'maccatalyst'">
    <Compile Include="Platforms\iOS\**\*.cs" />
  </ItemGroup>
```

`DeviceFactAttribute` is a `FactAttribute` subclass that skips unless `OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst()`. It exists so the `net10.0` host leg and the Windows device lane run the whole assembly green without a single conditional `#if` inside a test body.

- [ ] **Step 1: iOS — the backup-exclusion flag reads back set**

§16.4's exact words: "`IEdgeModelPaths` lands in the real no-backup directory and **the exclusion flag reads back set on iOS**". Reading it back is the whole point — setting `NSURLIsExcludedFromBackupKey` and never re-reading it is how a directory ends up in iCloud with a passing test:

```csharp
[DeviceFact]
public void TheModelRootIsInsideApplicationSupportAndReadsBackExcludedFromBackup()
{
    var paths = new AppleEdgeModelPaths(new TestEdgePaths());

    var models = paths.Models;

    Assert.Contains("/Library/Application Support/qavren-edge", models, StringComparison.Ordinal);
    Assert.True(Directory.Exists(models));

    // The read-back. NSUrl.GetResourceValue is the only thing that proves the attribute survived
    // directory creation - which is why 6.2 sets it AFTER creating, not before.
    using var url = NSUrl.FromFilename(models);
    Assert.True(url.TryGetResource(NSUrl.IsExcludedFromBackupKey, out var value, out var error),
        error?.LocalizedDescription);
    Assert.True(((NSNumber)value).BoolValue);

    // Same for the sibling ORT cache: it is ours to purge, and it must never be backed up either.
    using var cacheUrl = NSUrl.FromFilename(paths.OrtCache);
    Assert.True(cacheUrl.TryGetResource(NSUrl.IsExcludedFromBackupKey, out var cacheValue, out _));
    Assert.True(((NSNumber)cacheValue).BoolValue);
}
```

§16.4 also says the lanes "assert the directory exists and is excluded from backup, and nothing about its timing" — so there is **no** CoreML compile-cache timing assertion here, and the reason is in the spec: the device lanes create sessions from the 533-byte base64 fixture, which has neither a meaningful compile cost nor the strong `ModelCacheDirectory` key, so a timing measurement would be noise dressed as evidence.

- [ ] **Step 2: Android — the no-backup root, and a plausible memory read**

```csharp
[DeviceFact]
public void TheModelRootIsUnderNoBackupFilesDir()
{
    var paths = new AndroidEdgeModelPaths();
    var expected = Path.Combine(
        Application.Context.NoBackupFilesDir!.AbsolutePath, "qavren-edge", "models");

    Assert.Equal(expected, paths.Models);
    Assert.True(Directory.Exists(paths.Models));
}
```

- [ ] **Step 3: Both platforms — `IEdgeResourceMonitor` returns something real**

§16.4: "`IEdgeResourceMonitor` returns a plausible non-null `AvailableMemoryBytes` and a `LastPressure` that a simulated pressure event moves (and that reads `null` before any event, which is the whole reason it is nullable — §6.4)". "Plausible" has to be a number, not a shrug — a stub that returns `long.MaxValue` would satisfy a null check:

```csharp
[DeviceFact]
public void TheResourceMonitorReportsAPlausibleMemoryBudget()
{
    var monitor = TestHost.Services.GetRequiredService<IEdgeResourceMonitor>();

    var snapshot = monitor.Read();

    Assert.NotNull(snapshot.AvailableMemoryBytes);
    // Between 1 MB and 64 GB. On Apple, os_proc_available_memory() returning 0 means "unknown or
    // already over" and 6.4 treats it as unknown, never as a refusal - so 0 is mapped to null by
    // the monitor and would fail the NotNull above rather than slip through this range.
    Assert.InRange(snapshot.AvailableMemoryBytes!.Value, 1L << 20, 64L << 30);
    Assert.NotEqual(EdgeThermalState.Unknown, snapshot.Thermal);   // both platforms report one
}

[DeviceFact]
public async Task ASimulatedPressureEventMovesTheLatchAndResumedClearsIt()
{
    var monitor = TestHost.Services.GetRequiredService<IEdgeResourceMonitor>();
    var hub = TestHost.Services.GetRequiredService<IEdgeLifecycleHub>();

    Assert.Null(monitor.LastPressure);                       // null BEFORE any event - spec 6.4
    await hub.PublishAsync(EdgeLifecycleEvent.MemoryPressure(EdgeMemoryPressure.Moderate));
    Assert.Equal(EdgeMemoryPressure.Moderate, monitor.LastPressure);
    await hub.PublishAsync(EdgeLifecycleEvent.Resumed());
    Assert.Null(monitor.LastPressure);                       // cleared, not set to Low
}

[DeviceFact]
public async Task CriticalPressureDropsTheSessionAndTheNextAcquireReloadsIt()
{
    var hostSessions = TestHost.Services.GetRequiredService<IOnnxSessionHost>();
    using (var first = await hostSessions.AcquireAsync(TinyModelId)) { }
    var loadsBefore = hostSessions.Describe(TinyModelId)!.LoadCount;

    await TestHost.Services.GetRequiredService<IEdgeLifecycleHub>()
        .PublishAsync(EdgeLifecycleEvent.MemoryPressure(EdgeMemoryPressure.Critical));

    Assert.False(hostSessions.Describe(TinyModelId)!.IsLoaded);
    using var second = await hostSessions.AcquireAsync(TinyModelId);
    Assert.Equal(loadsBefore + 1, hostSessions.Describe(TinyModelId)!.LoadCount);
}
```

- [ ] **Step 4: The execution-provider report is logged and published — and nothing about its value is asserted**

§16.4's last paragraph is a *prohibition* as much as a requirement: "Each lane records the execution provider; none asserts a specific value … The lane logs `ExecutionProviderReport` and publishes it; the specific-EP claim waits for real hardware." Both Apple lanes run `macos-15-intel` with x64 RIDs, so there is no Neural Engine anywhere in them and an EP assertion would be asserting Rosetta behaviour. The test therefore asserts **shape, not value**, and writes the report where a human can read it:

```csharp
[DeviceFact]
public async Task TheExecutionProviderReportIsPublishedForTheLaneToRecord()
{
    using var lease = await TestHost.Services.GetRequiredService<IOnnxSessionHost>()
        .AcquireAsync(TinyModelId);

    var report = lease.Info.ExecutionProviders;

    Assert.NotEmpty(report.Attempts);
    foreach (var attempt in report.Attempts)
    {
        // Accepted == false must always carry a reason. A silently-skipped provider is the one
        // thing this report exists to make impossible.
        Assert.True(attempt.Accepted ^ attempt.Failure is not null);
    }
    // Recorded, never asserted: no Assert.Equal(CoreMl, report.Accepted) here or anywhere.
    TestContext.Current.TestOutputHelper?.WriteLine(
        JsonSerializer.Serialize(report, EdgeDiagnosticsJson.Indented));
}
```

The lane picks that output up through the `trx`/JUnit conversion `ci.yml` already runs for all four device lanes, so "publishes it" needs no new workflow step — which is why this task touches no root file and can sit in wave 6 rather than wave 2.

- [ ] **Step 5: Verify — host leg**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp2\embeddings\tests\Qavren.Edge.Onnx.Tests\Qavren.Edge.Onnx.Tests.csproj" `
  -c Release -f net10.0 -p:TargetFrameworks=net10.0 `
  -p:ArtifactsPath="D:\Local\Temp\qedge-sp2\w6\t63"
```

Expected: `Passed!` with **Failed: 0** and a skip count equal to the number of `[DeviceFact]`s, exit code 0. `DeviceDiagnosticsFacts` is the file that must compile for **every** TFM including `net10.0`; only its `[DeviceFact]`s skip.

- [ ] **Step 6: Verify — android compile**

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge-sp2\embeddings\tests\Qavren.Edge.Onnx.Tests\Qavren.Edge.Onnx.Tests.csproj" `
  -c Release -f net10.0-android -p:TargetFrameworks=net10.0-android `
  -p:ArtifactsPath="D:\Local\Temp\qedge-sp2\w6\t63"
```

Expected: succeeds. The iOS and Mac Catalyst compiles follow Task 2.2 Step 8's probe — if this host refused them there, it refuses them here, and both are CI gates either way. **Execution** on any device is CI-only.

---

### Task 6.4: Wave 6 close — sequential re-verify and commit *(integrator)*

**Local-verifiable:** yes.

**Files:** none of its own.

**Approach.** Three tasks in wave 6 all build `Qavren.Edge.Onnx` and `Qavren.Edge.Core`, and one of them publishes self-contained, staging the same native `.dll`s into a third output tree. They ran isolated; this task proves them in the real one.

- [ ] **Step 1: Clear the wave's isolated outputs**

```powershell
Remove-Item -Recurse -Force "D:\Local\Temp\qedge-sp2\w6" -ErrorAction SilentlyContinue
```

- [ ] **Step 2: Re-run every verify in the wave, sequentially, in the real tree**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp2"
Remove-Item Env:QAVREN_EDGE_MODEL_DIR -ErrorAction SilentlyContinue
foreach ($p in @(
  "embeddings\tests\Qavren.Edge.Onnx.Tests\Qavren.Edge.Onnx.Tests.csproj",
  "embeddings\tests\Qavren.Edge.Embeddings.Tests\Qavren.Edge.Embeddings.Tests.csproj")) {
  dotnet run --project "$root\$p" -c Release -f net10.0 -p:TargetFrameworks=net10.0
  if ($LASTEXITCODE -ne 0) { throw "FAILED: $p" }
}
dotnet build "$root\embeddings\tests\Qavren.Edge.Onnx.Tests\Qavren.Edge.Onnx.Tests.csproj" -c Release -f net10.0-android -p:TargetFrameworks=net10.0-android
if ($LASTEXITCODE -ne 0) { throw "FAILED: Onnx.Tests android compile" }
dotnet publish "$root\embeddings\tools\Qavren.Edge.TrimSmoke\Qavren.Edge.TrimSmoke.csproj" `
  -c Release -r win-x64 --self-contained true -p:PublishTrimmed=true -p:TargetFrameworks=net10.0 `
  -o "C:\Users\steve\AppData\Local\Temp\qedge-trimsmoke"
if ($LASTEXITCODE -ne 0) { throw "FAILED: trim-smoke publish" }
& "C:\Users\steve\AppData\Local\Temp\qedge-trimsmoke\Qavren.Edge.TrimSmoke.exe"
if ($LASTEXITCODE -ne 0) { throw "FAILED: trim-smoke run" }
Write-Host 'OK: wave 6 re-verified sequentially'
```

Expected: `OK: wave 6 re-verified sequentially`, exit code 0, with tier-3 **skipped** (no `QAVREN_EDGE_MODEL_DIR`) and the trim publish's IL2026/IL3050 **warnings** recorded for the README's trimming claim.

- [ ] **Step 3: Commit the wave**

```
feat(sp2): trim smoke, the nightly tier-3 facts and the device-lane assertions

- Qavren.Edge.TrimSmoke publishes and runs the dynamic collection path trimmed
- tier-3 SkipUnless facts with pinned reference vectors and a 1e-3 cosine tolerance
- spec 16.4 device assertions: iOS backup-exclusion read-back, resource monitor,
  pressure latch, session drop-and-reload, EP report published and never asserted
```

- [ ] **Step 4: Verify**

Step 2 printed its `OK` line and `git status --short` is clean. Wave 7 may start.

---

## WAVE 7 — Device host and sample app *(strictly serial — one implementer at a time)*

**Read this before starting either task.** These two are **not** run in parallel, and the reason is concrete rather than cautious. Task 7.1 builds `Qavren.Edge.DeviceTests` for `net10.0-android` and `net10.0-windows10.0.19041.0`, pulling `Qavren.Edge.Maui`, `Qavren.Edge.Sqlite`, both native packages and all three SP2 test libraries at platform TFMs. Task 7.2 builds `Qavren.Edge.Sample` for the same two TFMs, pulling `Qavren.Edge.Maui`, `Qavren.Edge.Sqlite`, the same natives and all three SP2 packages. That is two MAUI/platform builds over one shared reference closure, each staging the same native `.dll`s, each running the Android resource and manifest-merge pipeline. `ArtifactsPath` would separate the outputs but not the `$(AndroidSdkDirectory)` locks, the `aapt2` daemon or the shared workload targets, and MAUI builds are the slowest and least reproducible in this repo.

There is a second, decisive reason. **Task 7.1 Step 2 conditionally edits its own csproj depending on a build outcome** — whether ORT's `minSdkVersion 24` AAR merges into a project declaring `21.0`. A wave whose file contents depend on a build result is non-deterministic by construction, and a second task reading that closure mid-edit would make it worse. Serial removes both problems and costs one build.

7.1 first, then 7.2, then 7.3 closes the wave.

### Task 7.1: Device-host wiring — three `ProjectReference`s and the Android floor question

**Local-verifiable:** partly. The `net10.0-android` and `net10.0-windows10.0.19041.0` builds run here; the two Apple builds and **every device execution** are CI-only.

**Files:**
- Edit: `foundation\tests\Qavren.Edge.DeviceTests\Qavren.Edge.DeviceTests.csproj`

**Approach — read this before writing.** This is the third and last SP1 edit, and §16.4 already anticipates it: `Qavren.Edge.DeviceTests` is SP1's **test host**, not a shipped package, and it is **not edited beyond three added `ProjectReference`s** unless the merged Android manifest demands a floor bump.

The question is §19 item 2(a). `Qavren.Edge.Onnx.Tests`, `Qavren.Edge.Embeddings.Tests` and `Qavren.Edge.VectorData.Tests` declare `android 24.0` because they link ORT's AAR, whose own manifest declares `minSdkVersion 24`. `Qavren.Edge.DeviceTests` declares `21.0`, and .NET for Android merges library manifests into the app's. A project at 21 either fails the merge outright or has its effective floor silently raised to 24 — a floor the app's manifest, store listing and `OperatingSystem.IsAndroidVersionAtLeast` checks would not reflect. **Step 2 is the build that decides which**, and it is the only local check in this plan whose *outcome* changes a file.

No CI assertion is added for the platform floors, deliberately. A `SupportedOSPlatformVersion` that is too low produces no restore artefact to scan — it produces a manifest-merge failure on Android and a deployment-target warning on Apple, both at *build* time, both inside device lanes that already run. A green `device-tests-android` lane is the assertion that ORT's `minSdkVersion 24` AAR merges; green `device-tests-ios` / `device-tests-maccatalyst` lanes are the assertion that a 15.1-built static xcframework links. Adding a string check over csproj files would restate what those lanes already fail on.

- [ ] **Step 1: Add the three references**

```xml
  <!-- Spec 16.4: the device lanes run the SAME tier-1 assertions and the vec0/FTS5 half of tier 2
       against the real Android .so, the real iOS xcframework and the Mac Catalyst RID-graph
       resolution. SetTargetFramework is not needed: all three multi-target the four device TFMs,
       so MSBuild resolves the matching one. Qavren.Edge.VectorData.Conformance.Tests is
       deliberately NOT here - it is host-only and net10.0 alone. -->
  <ItemGroup>
    <ProjectReference Include="..\..\..\embeddings\tests\Qavren.Edge.Onnx.Tests\Qavren.Edge.Onnx.Tests.csproj" />
    <ProjectReference Include="..\..\..\embeddings\tests\Qavren.Edge.Embeddings.Tests\Qavren.Edge.Embeddings.Tests.csproj" />
    <ProjectReference Include="..\..\..\embeddings\tests\Qavren.Edge.VectorData.Tests\Qavren.Edge.VectorData.Tests.csproj" />
  </ItemGroup>
```

- [ ] **Step 2: Build for android and read the answer**

```powershell
$p = "C:\Users\steve\projects\qavren-edge-sp2\foundation\tests\Qavren.Edge.DeviceTests\Qavren.Edge.DeviceTests.csproj"
dotnet build $p -c Release -f net10.0-android -p:TargetFrameworks=net10.0-android
```

**If it succeeds:** nothing else changes. `Qavren.Edge.DeviceTests` keeps `21.0`, §5's "two edits and no others" holds for every shipped package, and the third edit is three lines of `ProjectReference`.

**If the manifest merge fails:** bump exactly one line in `Qavren.Edge.DeviceTests.csproj`:

```xml
    <!-- Raised from 21.0: ORT 1.30.0's AAR declares minSdkVersion 24 and .NET for Android merges
         library manifests into the app's. A test host, not a shipped SP1 package - spec 16.4
         names this as the one contingency, and spec 19 item 2(a) is the build that decided it. -->
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'">24.0</SupportedOSPlatformVersion>
```

Record which branch was taken in `embeddings/docs/adr/0008-raised-platform-floors.md` — that ADR's evidence section is the place this answer lives.

- [ ] **Step 3: Build for Windows**

```powershell
dotnet build $p -c Release -f net10.0-windows10.0.19041.0 -p:TargetFrameworks=net10.0-windows10.0.19041.0
```

Expected: succeeds. This proves the three five-TFM test libraries are referenceable from the Windows device head, which is the whole reason they carry `net10.0-windows10.0.19041.0` at all.

- [ ] **Step 4: Assert all three test assemblies reach the runner's output**

```powershell
$out = Get-ChildItem "C:\Users\steve\projects\qavren-edge-sp2\foundation\tests\Qavren.Edge.DeviceTests\bin\Release\net10.0-windows10.0.19041.0" -Recurse -Filter '*.dll'
foreach ($name in 'Qavren.Edge.Onnx.Tests.dll','Qavren.Edge.Embeddings.Tests.dll','Qavren.Edge.VectorData.Tests.dll') {
  if (-not ($out.Name -contains $name)) { throw "$name did not reach the device runner output" }
  Write-Host "OK $name"
}
```

- [ ] **Step 5: Verify**

Steps 2, 3 and 4 all pass, with Step 2's branch recorded. Apple builds and every device **execution** are CI-only.

---

### Task 7.2: Sample app — Embeddings and Search pages, Diagnostics toggles

**Local-verifiable:** yes for `net10.0-windows10.0.19041.0`, which builds **and runs** on this box. Android, iOS and Mac Catalyst compile-or-CI as with SP1.

**Files:**
- Edit: `foundation\samples\Qavren.Edge.Sample\Qavren.Edge.Sample.csproj`
- Edit: `foundation\samples\Qavren.Edge.Sample\MauiProgram.cs`
- Edit: `foundation\samples\Qavren.Edge.Sample\AppShell.xaml`
- Create: `foundation\samples\Qavren.Edge.Sample\Pages\EmbeddingsPage.xaml` + `.xaml.cs`
- Create: `foundation\samples\Qavren.Edge.Sample\Pages\SearchPage.xaml` + `.xaml.cs`
- Edit: `foundation\samples\Qavren.Edge.Sample\Pages\DiagnosticsPage.xaml` + `.xaml.cs`

**Approach.** §18: the SP1 sample gains three pages rather than SP2 shipping a second sample app. The sample targets the four MAUI platform TFMs (SP1 adjustment 31 — `net10.0` is not a MAUI application head).

- [ ] **Step 1: Reference the three SP2 packages and wire the builder**

Add `ProjectReference`s to `Qavren.Edge.Onnx`, `Qavren.Edge.Embeddings.Onnx` and `Qavren.Edge.VectorData`, and extend `MauiProgram.cs`'s existing `UseQavrenEdge` chain with `.AddOnnxEmbeddings(...)`, `.AddVectorStore()` and `.AddVectorCollectionMigration<string, Note>(...)` — the §4.3 shape, so the sample and the README show the same four calls.

**The sample's `Cipher` configuration must keep working.** SP1's sample swaps `Qavren.Edge.Sqlite.Native` for `Qavren.Edge.Sqlite.Native.Cipher` by build configuration and defines `QEDGE_CIPHER`; none of the three SP2 references depends on either native package, so the swap is unaffected. Do not add a native reference here.

- [ ] **Step 2: Embeddings page**

Pick a preset; show provisioning progress bound to `IProgress<ModelProvisioningProgress>` (determinate, because the download reports `BytesTotal`); embed a string; show dimensions, elapsed time, **the execution provider accepted and the ones skipped with their reasons**. The skipped list is the point — it is where `ExecutionProviderAttempt.Failure` becomes visible to a human.

- [ ] **Step 3: Search page**

Insert notes, then run vector, keyword and hybrid search **side by side with all three scores visible**, so the distance-versus-RRF inversion is something a reader *sees* rather than reads about. Label the vector column "distance (lower is better)" and the hybrid column "RRF score (higher is better)" in the UI itself.

- [ ] **Step 4: Diagnostics page**

The existing page renders three more components with **no code change** — `EdgeDiagnosticsReport` walks contributors generically. Add only two toggles: `CoreMlProviderOptions.ProfileComputePlan` (the one truthful signal for per-node EP assignment, off by default) and `EdgeVectorStoreOptions.IncludeRowCountsInDiagnostics` (one query per collection, off by default).

- [ ] **Step 5: Verify**

```powershell
$p = "C:\Users\steve\projects\qavren-edge-sp2\foundation\samples\Qavren.Edge.Sample\Qavren.Edge.Sample.csproj"
dotnet build $p -c Release -f net10.0-windows10.0.19041.0 -p:TargetFrameworks=net10.0-windows10.0.19041.0
dotnet build $p -c Release -f net10.0-android -p:TargetFrameworks=net10.0-android
```

Expected: both succeed. Then launch the Windows head and confirm the Embeddings page reports an accepted execution provider and the Search page shows three score columns. Apple heads are CI-only.

---

### Task 7.3: Wave 7 close — sequential re-verify and commit *(integrator)*

**Local-verifiable:** yes for the Windows and Android heads; the Apple heads are CI-only.

**Files:** none of its own.

**Approach.** Wave 7 was already serial, so there is no isolation to unwind. What this task adds is the one thing neither 7.1 nor 7.2 could do alone: build **both** platform heads back to back over one shared reference closure, which is the configuration CI actually runs, and record which branch Task 7.1 Step 2 took.

- [ ] **Step 1: Both heads, both TFMs, sequentially**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp2"
$heads = @(
  "foundation\tests\Qavren.Edge.DeviceTests\Qavren.Edge.DeviceTests.csproj",
  "foundation\samples\Qavren.Edge.Sample\Qavren.Edge.Sample.csproj")
foreach ($h in $heads) {
  foreach ($tfm in 'net10.0-windows10.0.19041.0','net10.0-android') {
    dotnet build "$root\$h" -c Release -f $tfm -p:TargetFrameworks=$tfm
    if ($LASTEXITCODE -ne 0) { throw "FAILED: $h / $tfm" }
  }
}
Write-Host 'OK: wave 7 re-verified sequentially'
```

Expected: `OK: wave 7 re-verified sequentially`, exit code 0.

- [ ] **Step 2: Record the Android floor decision**

Task 7.1 Step 2 either left `Qavren.Edge.DeviceTests` at `21.0` or bumped it to `24.0`. Whichever happened is written into `embeddings/docs/adr/0008-raised-platform-floors.md`'s evidence section, and §19 item 2(a) is closed. Confirm the ADR says which, and that no **shipped** SP1 package moved either way:

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp2"
$adr = Get-Content "$root\embeddings\docs\adr\0008-raised-platform-floors.md" -Raw
if ($adr -notmatch '19 item 2\(a\)') { throw "ADR 0008 does not record the Android floor decision" }
foreach ($p in 'Qavren.Edge.Core','Qavren.Edge.Sqlite','Qavren.Edge.Sqlite.Native',
               'Qavren.Edge.Sqlite.Native.Cipher','Qavren.Edge.Maui') {
  $proj = Get-ChildItem "$root\foundation\src\$p" -Filter '*.csproj' | Select-Object -First 1
  if ($proj -and (Get-Content $proj.FullName -Raw) -match '24\.0') {
    throw "$p declares a raised floor - no SHIPPED SP1 package may move (spec 5)"
  }
}
Write-Host 'OK: the floor decision is recorded and no shipped SP1 package moved'
```

- [ ] **Step 3: Commit the wave**

```
feat(sp2): device-host wiring and the sample app's Embeddings and Search pages

- Qavren.Edge.DeviceTests gains three ProjectReferences (test host, not a shipped package)
- the sample gains Embeddings and Search pages plus two Diagnostics toggles
```

- [ ] **Step 4: Verify**

Steps 1 and 2 printed their `OK` lines and `git status --short` is clean. Wave 8 may start.

---

## WAVE 8 — Closing gate

### Task 8.1: Full-solution verification *(integrator)*

**Local-verifiable:** yes, except the lanes named in **CI-only work**.

**Files:** none. This task is read-only and changes nothing.

- [ ] **Step 1: Restore, build and format the whole solution**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp2"
dotnet restore "$root\QavrenEdge.slnx"
dotnet format "$root\QavrenEdge.slnx" --verify-no-changes --no-restore
```

Expected: both exit 0. `dotnet format` loads every project including the MAUI heads, which is why this box — with every workload installed — is where it runs.

- [ ] **Step 2: Run every host-lane test project**

```powershell
$root = "C:\Users\steve\projects\qavren-edge-sp2"
$multi = @(
  "foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj",
  "foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj",
  "embeddings\tests\Qavren.Edge.Onnx.Tests\Qavren.Edge.Onnx.Tests.csproj",
  "embeddings\tests\Qavren.Edge.Embeddings.Tests\Qavren.Edge.Embeddings.Tests.csproj",
  "embeddings\tests\Qavren.Edge.VectorData.Tests\Qavren.Edge.VectorData.Tests.csproj")
$single = @(
  "foundation\tests\Qavren.Edge.Provider.Tests\Qavren.Edge.Provider.Tests.csproj",
  "foundation\tests\Qavren.Edge.Sqlite.Cipher.Tests\Qavren.Edge.Sqlite.Cipher.Tests.csproj",
  "embeddings\tests\Qavren.Edge.VectorData.Conformance.Tests\Qavren.Edge.VectorData.Conformance.Tests.csproj")
foreach ($p in $multi) {
  dotnet run --project "$root\$p" -c Release -f net10.0 -p:TargetFrameworks=net10.0
  if ($LASTEXITCODE -ne 0) { throw "FAILED: $p" }
}
foreach ($p in $single) {
  dotnet run --project "$root\$p" -c Release -p:TargetFrameworks=net10.0
  if ($LASTEXITCODE -ne 0) { throw "FAILED: $p" }
}
Write-Host 'OK: all eight host-lane suites passed'
```

Expected: `OK: all eight host-lane suites passed`, exit code 0. **Never `dotnet test`** — SP1 adjustment 36.

- [ ] **Step 3: Pack and confirm the three new packages**

```powershell
dotnet pack "C:\Users\steve\projects\qavren-edge-sp2\QavrenEdge.slnx" -c Release -o "C:\Users\steve\projects\qavren-edge-sp2\artifacts\packages"
$expected = 'Qavren.Edge.Onnx','Qavren.Edge.Embeddings.Onnx','Qavren.Edge.VectorData'
foreach ($id in $expected) {
  $pkg = Get-ChildItem "C:\Users\steve\projects\qavren-edge-sp2\artifacts\packages\$id.*.nupkg" -ErrorAction SilentlyContinue |
         Where-Object { $_.Name -notmatch '\.symbols\.' } | Select-Object -First 1
  if (-not $pkg) { throw "$id did not pack" }
  Write-Host "OK $($pkg.Name)"
}
```

Expected: three `OK` lines. Package validation is on from day one (`EnablePackageValidation`), and `PackageValidationBaselineVersion` stays unset until 1.0.0 is on nuget.org — with no baseline the validator fails the pack.

- [ ] **Step 4: Workflow contract**

```powershell
& "C:\Python314\python.exe" "C:\Users\steve\projects\qavren-edge-sp2\foundation\tools\ci-checks\assert-workflows.py" "C:\Users\steve\projects\qavren-edge-sp2"
if ($LASTEXITCODE -ne 0) { throw "assert-workflows.py failed" }
```

Expected: the `OK:` line with no problems, exit code 0.

- [ ] **Step 5: Trace every spec requirement to a task**

Walk the spec and confirm each of the following exists in the tree. Anything missing means the plan is **not** closed:

- Three packages, no fourth "recipe" assembly (§2).
- `Qavren.Edge.VectorData` has **no** reference to `Qavren.Edge.Embeddings.Onnx` (§2 decision 2) — grep the csproj.
- The two SP1 library edits and only those two (`EdgeErrorCode`, `FtsTable`), plus the `DeviceTests` test-host edit §16.4 anticipates (§5).
- `EdgeAiStartupOrder` 200 / 210 / 220 / 300 and **two** tasks at 220, one per layer (§14.1).
- **Two** lifecycle observers, not three — `Embeddings.Onnx` implements none (§14.2).
- Three diagnostics contributors, none of whose component names contains "Native" (§14.3).
- `EdgeAiEventIds` 600–899, every call site through `LoggerMessage.Define` (§14.4).
- Every `EdgeErrorCode` 5000–5299 is raised from somewhere, and every one has an anchor in `foundation/docs/errors.md` (§15).
- Golden SQL for `BuildCreateSql` / `BuildKnnSql` / `BuildHybridRrfSql` / `BuildUpsertSql` with and without filter, threshold and `IncludeVectors` (§16.1).
- KNN-vs-brute-force at **both** 384-d and 768-d (§16.2).
- The conformance suite on **both** the typed and the dynamic paths, with an enumerated skip-list (§16.2).
- The SQLCipher lifecycle test (§16.2).
- The §4.3 happy path asserted from a real container **in both builder-call orders** (§16.2).
- `ListCollectionNamesAsync` asserted in **both** directions (§16.2).
- The two service-key constants asserted equal (§16.2).
- Four new `ci.yml` test steps, two ORT asset assertions, the `trim-smoke` job in `ci-gate`'s `needs`, and the `assert-workflows.py` assertions (§17).
- **The nightly tier-3 lane exists and is *not* in `ci-gate`'s `needs`** — a `schedule` trigger, a `model-tests` job guarded to `schedule`/`workflow_dispatch`, `actions/cache@v6.1.0` keyed on the model's SHA-256, and a `sha256sum -c` that runs on cache hits too (§16.3).
- The tier-3 facts skip at **runtime** with no model download, and their session is built inside the test body rather than a constructor (§16.3).
- **`EmbeddingPresets.g.cs` regenerates byte-identically**, carries four 40-hex commit revisions and no `"main"`, and contains no 40-hex digest where a SHA-256 belongs (§10.2, plan adjustments 19–21).
- The §16.4 device assertions exist and compile for every platform TFM: the iOS backup-flag **read-back**, a bounded `AvailableMemoryBytes`, the `null` → `Moderate` → `null` pressure latch, drop-and-reload, and an `ExecutionProviderReport` that is published and never value-asserted (§16.4).
- `SessionOptionsFactory` and `EdgeEmbeddingException` each have exactly one owning file, and `EdgeEmbeddingException` raises only the five codes §15.1 allocates (§6.5, §7, §15.1).
- `BuildDropSql` is in the golden-SQL assertions, with and without a full-text sidecar (§8, §16.1).
- `EmbeddingPooler.L2Normalize` has a hand-computed assertion, and `PinnedSequenceLength` populates both the override dictionary and the flag (§16.1).
- Three sample pages (§18).
- ADRs 0003–0008 (§19 item 14).
- **Every wave has a close commit on `feat/sp2-embeddings`** — eight of them, none on `main`, none carrying an AI attribution trailer.

- [ ] **Step 6: Verify**

Steps 1–5 all pass. The plan is closed; the branch `feat/sp2-embeddings` is ready for its PR.

---

## CI-only work

Everything below is authored and locally checked as far as this box allows, and executes for the first time in CI or on the Mac Mini. Nothing here is unverified *design* — it is verified design with a host this machine does not have.

**Every row names the task that writes it.** A row with no owning task would be work nobody does, which is exactly how §16.3's tier-3 lane and §16.4's device assertions went missing from an earlier draft of this plan: both appeared in this table as things "CI runs" while no task created the job, the test class, the reference vectors or the platform-conditional code. CI does not write code. If a row below has no task column entry, that is a plan bug.

| Item | Written by | Why its *execution* is CI-only |
|---|---|---|
| Every `ci.yml` run: the four new test steps, the two ORT asset assertions on `windows-2025`, the `trim-smoke` job | **Task 2.3** (YAML, asserts) + **Task 6.1** (the console) | No workflow runs during implementation; implementers never push. The YAML is linted, `assert-workflows.py` is run, and every test step's command is run by hand here |
| `net10.0-ios` and `net10.0-maccatalyst` **compilation** of `Qavren.Edge.Onnx`'s `Platforms/iOS` files, and of `Qavren.Edge.Onnx.Tests`'s | **Task 2.2** (product) + **Task 6.3** (tests) | Needs a Mac unless Task 2.2 Step 8's probe says otherwise; restore demonstrably works here, compilation is the open half of §19 item 4 |
| `device-tests-ios` and `device-tests-maccatalyst` picking up the three SP2 test libraries | **Task 7.1** | No macOS or Xcode on this box |
| `device-tests-android` **execution** (emulator + KVM on `ubuntu-24.04`) | **Task 7.1** | No Android emulator here; the android **build** of the device host is Task 7.1 Step 2 |
| §19 item 2(b): Apple deployment-target link check at `15.1` | — (measurement) | Needs a Mac and a real link of the force-loaded static xcframework |
| §19 item 2(c): `LC_BUILD_VERSION` of the xcframework's `ios-arm64_x86_64-maccatalyst` slice (`otool -l` / `vtool -show`) | — (measurement; ADR 0008 by **Task 1.2**) | Needs a Mac. If it returns something other than a 15.1-family value, SP2's maccatalyst floor moves and §4.1's block, §16.4 and the README all follow |
| §19 item 3: `.apk` / `.aab` deltas with four ABIs and with `$(AndroidSupportedAbis)` trimmed to `arm64-v8a`; iOS and Mac Catalyst `__TEXT` totals via `size -m` | — (measurement) | The device lanes already produce app packages, so this is `ls -l` and `size -m` on artifacts that exist — but the artifacts are built in CI |
| §19 item 5: `maccatalyst-arm64` RID-graph resolution | — (measurement; README wording by **Task 1.2**) | Both Apple lanes run `macos-15-intel` with x64 RIDs, so the arm64 claim needs a manual Mac Mini run recorded in the vault |
| Tier 3: the real-model lane — int8 MiniLM at HF revision `1110a243fdf4706b3f48f1d95db1a4f5529b4d41`, `actions/cache@v6.1.0` keyed on the **sha256** `4278337f…`, cosine tolerance 1e-3 | **Task 2.3** (the `schedule` trigger, the `model-tests` job, the cache and hash-verify steps) + **Task 6.2** (`ModelAvailable`, `RealModelFacts`, `reference-vectors.json`) | Nightly schedule, never on a PR, and **not** in `ci-gate`'s `needs`. `SkipUnless` is evaluated at **runtime**, after the test class constructor, so the session is built inside the test body or a "skipped" test still loads a 23 MB model. The skip path and an opt-in real run were both exercised here (Task 6.2 Steps 5 and 6); only the *scheduled* run is CI's |
| §16.4's device-only assertions: the iOS `NSURLIsExcludedFromBackupKey` **read-back**, a bounded non-null `AvailableMemoryBytes`, the `null` → `Moderate` → `null` pressure latch, drop-and-reload, and the published `ExecutionProviderReport` | **Task 6.3** | The code compiles here for `net10.0` and `net10.0-android` and skips on the host through `[DeviceFact]`; it can only *run* on a device or simulator, which is what the four existing lanes are for. No EP **value** is asserted anywhere — both Apple lanes are `macos-15-intel` with x64 RIDs, so there is no Neural Engine to assert about |
| `trim-smoke`'s `linux-x64` publishes | **Task 6.1** | Published and run for `win-x64` here; the CI leg is `ubuntu-24.04` |
| Branch protection and the PR | — | The owner's, after the branch is pushed |

## Open risks

1. **ORT 1.30.0 was one day old at design time and the only NuGet rollback is 1.29.0.** Upstream's 1.29.1 was never published there, so there is no ladder between them. The four device lanes are the soak. ADR 0003.
2. **`Microsoft.Extensions.VectorData.ProviderServices` is entirely `[Experimental("MEVD9001")]`.** A minor MEVD bump can break `Qavren.Edge.VectorData`'s compile. The conformance suite is the detector, and it is pinned at 10.10.0 alongside the abstractions. ADR 0007.
3. **`MemoryHeadroomFactor = 2.5` and `MemoryHeadroomBytes = 48 MB` are engineering estimates, not measurements.** Guessing high refuses sessions that would have worked; guessing low gets the app killed. Calibrate with a device run reading `os_proc_available_memory()` around a real session load before 1.0. §19 item 10.
4. **`Microsoft.ML.Tokenizers` states no concurrency guarantee.** PACKAGE.md says to cache the instance and nothing more, so the tokenizer is serialised behind the same semaphore as ORT `Run`. A stress test decides whether that can be relaxed; the same test measures the per-input `IReadOnlyList<int>` that `IEdgeTokenizer.Encode` is forced to allocate, and if it turns out to matter the fix is a Tokenizers 3.x span overload, not a different signature here. §19 item 8.
5. **Nobody has measured whether `PinnedSequenceLength` wins.** The default keeps the graph's declared symbolic dims, so CoreML partitions normally and bucketing only limits the number of distinct runtime shapes; pinning emits `AddFreeDimensionOverrideByName` and enables `RequireStaticInputShapes`, at the cost of one session, one shape, and a full-length run for every batch. Measure both on the Mac Mini with `ProfileComputePlan=1` for a batch of short strings and a batch of long ones before writing any number, or any recommendation, into the README. Until then the default stays unpinned — the option that cannot silently disable CoreML. §19 item 13.
6. **`ChunkSize = 256` is reasoned, not benchmarked.** vec0's write and scan performance is undocumented upstream (`site/guides/performance.md` is a 64-byte stub), and the `<tbl>_chunks` shadow table carries no index on its partition columns. Benchmark before any rows-per-second number goes in the README. §19 item 11.
7. **Tokenizer parity with Hugging Face is unverified per preset.** `BertOptions.RemoveNonSpacingMarks` and `IndividuallyTokenizeCjk` versus HF's `strip_accents` / `tokenize_chinese_chars` is not established for each model, and a divergence is silent. A golden-id test per preset — pinned ids for a fixed sentence, generated once from HF — is the only thing that catches it. Tier 3. §19 item 9.
8. **Neither ORT nor Tokenizers carries trim or AOT annotations.** What a trimmed or AOT publish does is measured by `trim-smoke` and by nothing else; the README claims whatever those two publishes prove. The AOT leg is `continue-on-error` for one release cycle and then either becomes required or the claim is dropped. §19 item 12.
9. **The Apple CI lanes prove x64 and nothing else.** `device-tests-ios` and `device-tests-maccatalyst` run `macos-15-intel` with `-r iossimulator-x64` and `-r maccatalyst-x64`, so there is no Apple Neural Engine anywhere in them and no EP or CoreML-cache claim can come from them. Each lane records `ExecutionProviderReport` and asserts nothing about its value. Moving the lanes to `macos-15` arm64 is an SP1 workflow change and needs its own PR. §19 item 5.
10. **A new `ci.yml` job that is forgotten in `ci-gate`'s `needs` does not block a merge.** `assert-workflows.py` now checks that `trim-smoke` specifically is gated, but it cannot know which *future* jobs ought to be. Adding a job means adding it to `needs:` in the same PR. Inherited from SP1 open risk 8.
11. **`vec0` reclaims space only when a whole chunk empties.** At `chunk_size = 256` a heavily churned collection grows without bound; v1 ships no `CompactAsync` and the README documents drop-and-rebuild as the remedy.
12. **A pinned Hugging Face revision can be repointed, and only a regeneration would notice.** `EmbeddingPresets.g.cs` pins four full commit SHAs, which is the protection §10.2 asks for — but a repo owner can force-push a branch, and `paths-info` against a commit SHA that no longer exists returns a 404 rather than a wrong answer, which is the good failure. The bad one is subtler: nothing in CI ever re-runs `fetch_preset_hashes.py`, so a *newer* revision with better weights is invisible until somebody looks. Re-run the script before each release and diff; a changed digest is a deliberate decision about vectors, never a merge conflict to resolve. Task 3.3's verify is what makes the diff appear at all.

13. **The tier-3 reference vectors are a baseline, and a baseline can be regenerated into agreement with a bug.** `reference-vectors.json` is the only committed artifact in this plan a reviewer cannot read. Task 6.2's `QAVREN_EDGE_WRITE_REFERENCE` path refuses to overwrite an existing file for exactly that reason, but the file can still be deleted and rewritten by anyone who decides the nightly lane is "just flaky". The rule, and it belongs in the Tier3 README: a regeneration is a PR of its own, with the reason in the body, never a line in a PR that also changes the pooling, the tokenizer or a preset.

14. **The fixture generator's output must be regenerated by hand.** CI never runs `make_tiny_model.py`, so an onnx or protobuf upgrade that changes the serialised bytes is invisible until somebody reruns it. Task 1.3's verify asserts the six measured sizes, which is what turns "somebody reran it and the bytes moved" into a failure rather than a surprise.
