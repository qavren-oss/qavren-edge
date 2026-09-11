# Qavren.Edge — Sub-project 2: embeddings + vector store

Date: 2026-09-11
Status: draft for planning. Branch `feat/sp2-embeddings` in the worktree
`C:\Users\steve\projects\qavren-edge-sp2`.
Sub-project 1 is MERGED and is the contract: where this document and the SP1 code
disagree, the code wins. The two additive SP1 edits this sub-project needs are
enumerated in §5 and nowhere else.
Owner: Steve Ackley (Qavren Solutions LLC)

## 1. Summary

Sub-project 2 turns the SQLite foundation into a semantic-search stack: ONNX
Runtime hosting, a Microsoft.Extensions.AI embedding generator, and a
Microsoft.Extensions.VectorData store over `vec0` and FTS5 with reciprocal rank
fusion. Three packages, matching roadmap row 2 exactly.

Five decisions shape everything below.

1. **Filters are pushed into `vec0` as `rowid IN (SELECT "_rowid" FROM <data table> WHERE …)`.**
   `vec0BestIndex` assigns an `argvIndex` **and** sets `omit = 1` for a `rowid IN (…)`
   constraint via `sqlite3_vtab_in` (SQLite ≥ 3.38; SP1 pins 3.53.4), and
   `vec0Filter_knn_chunks_iter` AND-s that rowid bitmap into the chunk validity
   bitmap *before* distances are computed. So the whole MEVD `Filter` expression
   translates to ordinary SQL over the data table — `OR`, `LIKE`, `IS NULL`,
   indexes — and still behaves as a true pre-filter. That single choice deletes
   the entire `vec0` metadata/auxiliary column tier: the 16-column caps, the
   "boolean metadata accepts only `=`/`!=`" rule, the "`IN` only on INTEGER and
   TEXT" rule, the >12-character TEXT spill, and the hard error `vec0` raises
   when a KNN `WHERE` clause touches a `+` auxiliary column. No shipping MEVD
   provider does this.
2. **One INTEGER rowid joins all three tables.** The data table owns
   `"_rowid" INTEGER PRIMARY KEY`; the `vec0` table and the FTS5 external-content
   table are keyed on the same rowid. No duplicated user key, no `vec0` TEXT
   primary-key emulation, and the RRF `FULL OUTER JOIN` is integer-to-integer.
3. **Sessions are leased, not handed out.** `IOnnxSessionHost.AcquireAsync`
   returns a ref-counted lease; a memory-pressure drop marks the session and
   disposes it when the last lease returns. `InferenceSession` has a finalizer,
   so a dropped-but-undisposed session pins the whole native graph until GC —
   and disposing one under an in-flight `Run` is a native access violation.
   SP1's lifecycle hub makes both reachable from a platform callback.
4. **The OS is asked for a memory budget before a session is created.**
   `os_proc_available_memory()` on Apple, `ActivityManager.MemoryInfo` on
   Android. A model that will not fit produces a named exception with two byte
   counts and a remediation, not a jetsam kill.
5. **Multilingual is cut from v1, loudly.** `Microsoft.ML.Tokenizers` 2.0.0
   (stable) has no `tokenizer.json` loader, and its `SentencePieceTokenizer.Create`
   emits raw SentencePiece piece indices while every XLM-R/e5 graph expects the
   fairseq layout (`<s>`=0, `<pad>`=1, `</s>`=2, `<unk>`=3, `<mask>`=250001). The
   failure is silent — plausible, wrong embeddings. Waits for
   `CreateFromTokenizerJson` in Tokenizers 3.x.

We do **not** wrap `CommunityToolkit.VectorData.SqliteVec`. It constructs its own
`SqliteConnection` from a connection string and calls `connection.LoadExtension("vec0")`
on every open — impossible under SP1's `SQLITE_OMIT_LOAD_EXTENSION` build, and
incompatible with `IEdgeDatabase`'s pragma/cipher/pooling contract — and the
`sqlite-vec` NuGet ships natives for five desktop RIDs and nothing mobile. Its
schema and SQL shapes are MIT and battle-tested; those we copy. Its code path we
cannot use.

## 2. Suite decisions that apply

| # | Decision | How SP2 honours it |
|---|---|---|
| 2 | L0 → L1 → L2 layering | `Onnx` is L0; `Embeddings.Onnx` and `VectorData` are L1. `VectorData` does **not** reference `Embeddings.Onnx` — the generator arrives as the non-generic `IEmbeddingGenerator` through MEVD's own options, so the store works with Azure OpenAI embeddings and no ONNX at all. |
| 5 | Wrap `Microsoft.ML.OnnxRuntime`, never rebuild it | One `PackageReference` to the native package. No `.ort` conversion, no reduced-operator build, no custom EP. |
| 6 | TFMs | Exactly SP1's spelling: `net10.0`, `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst`, `net10.0-windows10.0.19041.0`. SP2 adds no versioned TFM and changes no SP1 csproj. It does set **higher `SupportedOSPlatformVersion` floors than SP1's** on its own projects — android `24.0`, ios and maccatalyst `15.1` — because ORT's shipped natives are built at those minimums; that property plays no part in restore, so it is not a TFM change (§4.1). Within that vocabulary each SP2 project takes the subset it needs: the two ONNX packages take `net10.0` + android/ios/maccatalyst (no windows — §4.1), `Qavren.Edge.VectorData` takes `net10.0` alone, the three device-hosted test projects take all five so the Windows device lane can reference them, and `Qavren.Edge.VectorData.Conformance.Tests` takes `net10.0` alone because it is host-only (§16.4). See §4.1 for why the unversioned form still resolves ORT's `net9.0-*` assets, and §19 items 1 and 2 for the checks that prove it. |
| 10 | Encryption ships in v1 | The vector store runs on whatever connection `IEdgeDatabase` hands it, SQLCipher included. One integration test proves it (§16). |
| 13 | MS-native hosting | `edge.AddOnnx()`, `edge.AddOnnxEmbeddings()`, `edge.AddVectorStore()` on SP1's `EdgeBuilder`; `IEdgeStartupTask` ordering; `IEdgeLifecycleObserver`; `IEdgeDiagnosticsContributor`; `EdgeException` subclasses with stable `EdgeErrorCode`s. |
| 14 | GitHub-hosted CI | New host-test steps on the existing three-OS matrix; the four existing device lanes pick SP2 up through `Qavren.Edge.DeviceTests`. No new device lane. |

Roadmap row 2 names three packages. SP2 ships exactly three. A fourth "recipe"
assembly wrapping `AddSqlite + AddOnnxEmbeddings + AddVectorStore` behind one call
was designed and **cut**: it would have saved two builder lines at the cost of an
assembly that must depend on both L1 packages and on SQLite, violating the
layering that decision 2 exists to protect. The happy path is four builder calls,
shown in §4.3.

## 3. Goals and non-goals

Goals:

- A consumer adds one package, writes four builder calls, and gets on-device
  semantic search: `UseSqliteNative().AddSqlite().AddOnnxEmbeddings().AddVectorStore()`.
- The embedding generator is a first-class `IEmbeddingGenerator<string, Embedding<float>>`.
  Semantic Kernel bridges it with `AsTextEmbeddingGenerationService()`; Microsoft
  Agent Framework consumes it through MEVD. Neither needs a line of Qavren code.
- The vector store passes `Microsoft.Extensions.VectorData.ConformanceTests` on
  both the typed and the dynamic record paths.
- Hybrid search — `vec0` KNN fused with FTS5 bm25 by RRF — behind
  `IKeywordHybridSearchable<TRecord>`, which no shipping MEVD provider implements
  over SQLite.
- Every runtime fact is inspectable: which execution provider ORT accepted, which
  it skipped and why, the model path and its SHA-256, the tokenizer and vocab
  size, the dimensions, the CoreML cache size, available memory and thermal state.
- Nothing is missed on iOS or Android: no `load_extension`, no backed-up model
  blobs, no unbounded CoreML recompilation, no session created into a memory
  budget that cannot hold it.

Non-goals:

- No chunkers, no extractors, no ingestion pipeline (sub-project 3).
- No `IChatClient`, no ORT GenAI (sub-project 4).
- No reranker. RRF over two lanes is the v1 ranking story.
- No thermal *policy*. SP2 reads and reports thermal state; the batch backoff that
  acts on it belongs with the long-running ingestion pipeline in SP3.
- No `int8`/`bit` `vec0` columns, no binary-quantization rescore, no Matryoshka
  truncation, no ANN index.

## 4. Architecture

### 4.1 Packages

| Package | TFMs | Depends on | Purpose |
|---|---|---|---|
| `Qavren.Edge.Onnx` | `net10.0`, `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst` | `Qavren.Edge.Core`; `Microsoft.ML.OnnxRuntime` 1.30.0 | L0. `OrtEnv` bootstrap wired to `ILogger`; ref-counted session host; execution-provider policy; `IEdgeModelPaths` (a no-backup model root); model provisioning (file / bundled asset / resumable verified HTTP); `IEdgeResourceMonitor`; lifecycle observer; diagnostics contributor. |
| `Qavren.Edge.Embeddings.Onnx` | same four | `Qavren.Edge.Onnx`; `Microsoft.Extensions.AI.Abstractions` 10.10.0; `Microsoft.Extensions.AI` 10.10.0; `Microsoft.ML.Tokenizers` 2.0.0 | L1. One public leaf generator implementing `IEmbeddingGenerator<string, Embedding<float>>`; the tokenizer abstraction; tensor assembly, pooling, normalisation, batching; the model presets. |
| `Qavren.Edge.VectorData` | net10.0 | `Qavren.Edge.Sqlite`; `Microsoft.Extensions.VectorData.Abstractions` 10.10.0 | L1. Clean-room MEVD provider: `EdgeVectorStore`, `EdgeVectorStoreCollection<TKey,TRecord>` (also `IKeywordHybridSearchable<TRecord>`), the dynamic collection, the collection model builder, the LINQ→SQL filter translator, the SQL emitter, RRF hybrid search. |

Dependency direction is strictly downward and matches SP1's: `Embeddings.Onnx` →
`Onnx` → `Core`; `VectorData` → `Sqlite` → `Core`. The two L1 packages do not
reference each other.

`Qavren.Edge.VectorData` is `net10.0` only on purpose. It touches no platform API
— `Microsoft.Data.Sqlite` and MEVD are platform-neutral — and every platform TFM
consumes a `net10.0` library unchanged, exactly as `Qavren.Edge.Sqlite` already
does. Multi-targeting it would add four build legs for nothing.

`Qavren.Edge.Onnx` multi-targets for two reasons and only two: the platform
model-path implementations (`Context.NoBackupFilesDir`, `NSURLIsExcludedFromBackupKey`)
and the platform memory/thermal reads. Both exist on android, ios and maccatalyst
and on none of the others, so the TFM list is `net10.0` plus those three — **no
`net10.0-windows`**. Windows has neither a distinct model-path implementation
(§6.2 puts it on the desktop `<Data>/models` path) nor a distinct memory read
(§6.4 gives it the same `GC.GetGCMemoryInfo()` path as `net10.0`), so a Windows
TFM would buy a build leg and nothing else. A `net10.0-windows10.0.19041.0`
consumer references the `net10.0` assembly, exactly as it already does for
`Qavren.Edge.Sqlite`. The three **device-hosted** test projects are the exception
and keep all five TFMs, because `Qavren.Edge.DeviceTests` targets
`net10.0-windows10.0.19041.0` and the Windows device lane has to run them.
`Qavren.Edge.VectorData.Conformance.Tests` is host-only and stays at `net10.0`
alone (§16.4).

The project uses the same `Platforms\**` + `GetTargetPlatformIdentifier`
compile-item pattern `Qavren.Edge.Maui` already uses, and the same per-OS
`TargetFrameworks` guards, because restore evaluates every listed TFM for every
project in the graph. The `SupportedOSPlatformVersion` block has the same *shape* as
SP1's and different *values* — the one deviation in this section, explained
immediately below it:

```xml
<TargetFrameworks>net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst</TargetFrameworks>
<TargetFrameworks Condition="$([MSBuild]::IsOSPlatform('Linux'))">net10.0;net10.0-android</TargetFrameworks>
<!-- macOS keeps all four. -->
<!-- HIGHER than SP1's 21.0 / 15.0 / 15.0. These are ORT's floors, not ours — see below. -->
<SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'">24.0</SupportedOSPlatformVersion>
<SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios'">15.1</SupportedOSPlatformVersion>
<SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'maccatalyst'">15.1</SupportedOSPlatformVersion>
```

**The one place SP2 deviates from SP1's merged platform block, and why it must.**
SP1's five csproj files declare `android 21.0`, `ios 15.0` and `maccatalyst 15.0`.
SP2's four ONNX-carrying projects declare `android 24.0` and `ios`/`maccatalyst`
`15.1`, because ORT's shipped natives are built at exactly those minimums and a
lower declaration is not a conservative choice — it is a broken one:

- **Android: `minSdk 24`.** ORT 1.30.0's AAR is produced from
  `tools/ci_build/github/android/default_full_aar_build_settings.json`, which sets
  `android_min_sdk_version: 24`. The AAR's own manifest therefore declares
  `minSdkVersion 24`, and .NET for Android merges library manifests into the app's.
  A project declaring `21.0` against it either fails the merge outright or has its
  effective floor silently raised to 24 — a floor the app's own manifest, store
  listing and `OperatingSystem.IsAndroidVersionAtLeast` checks would not reflect.
  Declaring `24.0` makes the real constraint visible at compile time instead of at
  merge time. Android 7.0 is the floor either way; SP2 only chooses whether the
  build says so.
- **iOS and Mac Catalyst: deployment target `15.1`.** ORT's Apple natives come from
  `tools/ci_build/github/apple/default_full_apple_framework_build_settings.json`,
  which builds `iphoneos` and `iphonesimulator` at `--apple_deploy_target=15.1`
  (its `macosx` slices are built at 14.0). Linking a static xcframework whose
  slices were built for 15.1 into an app declaring a 15.0 deployment target is a
  linker warning at best and a load failure on a 15.0 device at worst. `15.1` is
  the honest floor for both Apple TFMs. Mac Catalyst is the less certain of the
  two — the Apple build-settings file defines `iphoneos`, `iphonesimulator` and
  `macosx` archs and has no `maccatalyst` entry at all, so which slice the
  xcframework's `ios-arm64_x86_64-maccatalyst` entry is cut from, and therefore
  what its true deployment target is, is not established by that file.
  `15.1` matches the iOS-family numbering the xcframework is built alongside and is
  the conservative choice; **§19 item 2 owns confirming it from the built
  xcframework's own `LC_BUILD_VERSION` load command** rather than from the build
  settings.

None of this touches SP1. `SupportedOSPlatformVersion` is a per-project property,
not part of the TFM, so raising it in SP2's own csproj files changes no SP1 csproj
and adds no versioned TFM — decision 6 and §5's "two edits and no others" both
hold. A consumer app is free to declare a *higher* floor and is required to declare
at least SP2's; the README states `android 24 / iOS 15.1` as the supported
minimum, and §19 item 2 is the build that proves the manifest merge and the link
both succeed at those numbers.

**Why the unversioned TFM still resolves ORT's mobile assets.**
`Microsoft.ML.OnnxRuntime.Managed` 1.30.0 ships `lib/netstandard2.0`,
`lib/net8.0`, `lib/net9.0-android35.0`, `lib/net9.0-ios18.0`,
`lib/net9.0-maccatalyst18.0` — there is no `net10.0` asset and no Windows-specific
asset. NuGet selects a platform asset on the TFM's **`TargetPlatformVersion`**,
which is a different property from `SupportedOSPlatformVersion`: the first is the
SDK version compiled against and drives asset selection, the second is the minimum
OS the app declares at runtime and drives nothing in restore. It is not
inconsequential, though — it drives the Android manifest merge and the Apple
deployment target, which is the separate constraint the block above raises it for.
The two properties are independent and SP2 satisfies each on its own terms: asset
selection through the SDK's default `TargetPlatformVersion`, native compatibility
through the raised floors. A bare
`net10.0-ios` takes its `TargetPlatformVersion` from the installed platform SDK —
under this repo's pinned `maui` workload 10.0.201 and Xcode 26.2 that is far above
ORT's 18.0 floor, and the android SDK likewise sits above 35.0 — so
`lib/net9.0-ios18.0` and `lib/net9.0-android35.0` resolve from the same
unversioned TFMs SP1 already uses. `net10.0` resolves `lib/net8.0`, which is the
asset that brings `System.Numerics.Tensors`; falling back to `netstandard2.0`
would silently change the span hot path, so CI asserts against it (§17).

This is the one place where being wrong would be expensive, so it is
**verification item 1** (§19) and it runs before any SP2 code is written. If a
default `TargetPlatformVersion` ever drops below a floor, the documented fallback
is to pin `net10.0-ios18.0` / `net10.0-android35.0` / `net10.0-maccatalyst18.0`
**on SP2's projects only** — SP1's decision 6, its five merged csproj files and §5's
"two edits and no others" all stay untouched either way, because a versioned TFM in
SP2 is still consumable by SP1's unversioned projects.

Mac Catalyst needs **no** targets file from us. ORT's native package ships
`build/net9.0-maccatalyst18.0/_._`, and its own
`targets/net9.0-maccatalyst/README.md` says that blank file is deliberate: "for
Mac Catalyst platform, it directly will resolve the xcframework from the
runtimes/native/ios folder based on [the SDK RuntimeIdentifierGraph]". Hand-adding
a second `NativeReference` to the same xcframework risks a duplicate force-load.
The existing `device-tests-maccatalyst` lane is what exercises it — but note
exactly what that lane proves. `ci.yml` runs it `runs-on: macos-15-intel` with
`-r maccatalyst-x64`, and the iOS lane likewise runs `-r iossimulator-x64` on the
same Intel image. So both lanes validate the **x64** RID-graph resolution and the
x86_64 slices of ORT's xcframework; `maccatalyst-arm64`, which is what every
current Mac actually runs, is never built there. The README therefore claims
"Mac Catalyst x64, proven in CI" and nothing more until an arm64 run exists —
either by moving the lane to `macos-15` (an SP1 workflow change, out of SP2's
scope) or by a manual run on the Mac Mini. §19 item 5 carries the decision.

`osx-x64` has no ORT native at all — 1.30.0's `runtimes/` contains `osx-arm64`
only. That is detected at startup and reported as a named error, not as a
`DllNotFoundException` on first embed (§15).

### 4.2 Repository layout

`embeddings/` mirrors `foundation/`:

```
qavren-edge/
  QavrenEdge.slnx                       # SP2 projects added here
  Directory.Packages.props              # SP2 PackageVersions added here
  embeddings/
    README.md
    src/
      Qavren.Edge.Onnx/                            # net10.0 + android/ios/maccatalyst
        Platforms/Android/ , Platforms/iOS/        # model paths + resource monitor
      Qavren.Edge.Embeddings.Onnx/                 # same four TFMs
      Qavren.Edge.VectorData/                      # net10.0 only
    tests/
      Qavren.Edge.Onnx.Tests/                      # 5 TFMs (incl. windows), device-hosted
      Qavren.Edge.Embeddings.Tests/                # 5 TFMs (incl. windows), device-hosted
      Qavren.Edge.VectorData.Tests/                # 5 TFMs (incl. windows), device-hosted
      Qavren.Edge.VectorData.Conformance.Tests/    # net10.0 only
      fixtures/make_tiny_model.py                  # regeneration script, never a build step
    samples/                                       # none; the SP1 sample app gains pages
    docs/superpowers/{specs,plans}/ , docs/adr/
```

`foundation/samples/Qavren.Edge.Sample` gains three pages rather than SP2 adding a
second sample app (§18). `foundation/tests/Qavren.Edge.DeviceTests` gains three
`ProjectReference`s.

### 4.3 The consumer's four calls

```csharp
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddOnnxEmbeddings(o => o.Preset = EmbeddingPresets.MiniLmL6V2Int8)
    .AddVectorStore()
    .AddVectorCollectionMigration<string, Note>(version: 1, "notes"));
```

```csharp
public sealed class Note
{
    [VectorStoreKey] public string Key { get; set; } = "";
    [VectorStoreData(IsIndexed = true)] public string? Tag { get; set; }
    [VectorStoreData(IsFullTextIndexed = true)] public string Title { get; set; } = "";
    [VectorStoreData(IsFullTextIndexed = true)] public string Body { get; set; } = "";
    [VectorStoreVector(384, DistanceFunction = DistanceFunction.CosineDistance)]
    public string? Embedding => Body;          // string source -> the generator fills it
}

var notes = store.GetCollection<string, Note>("notes");
await notes.UpsertAsync(new Note { Key = "n1", Title = "Roof leak", Body = "…" });
await foreach (var hit in notes.HybridSearchAsync("water damage", ["roof", "leak"], top: 10))
    Console.WriteLine($"{hit.Record.Title} {hit.Score:F4}");
```

`AddOnnxEmbeddings` calls `AddOnnx()` for you; it is idempotent.

`Note.Embedding` is a `string` source property, so the store has to embed it — and
nothing above hands the store a generator. It does not have to: `AddVectorStore`'s
registered factory resolves the non-generic `IEmbeddingGenerator` from the container
(with `GetService`, so a store used with pre-computed vectors still works), asks that
instance for its query sibling with
`generator.GetService(typeof(IEmbeddingGenerator), EdgeVectorData.QueryGeneratorServiceKey)`,
passes both to `EdgeVectorStore`'s constructor, and uses
each only where the matching `EdgeVectorStoreOptions` property was left null.
`AddOnnxEmbeddings` registers both the closed generic and the non-generic descriptor
for exactly this reason, and the order of the two builder calls is irrelevant because
the factory runs at resolve time. §12.5 has the mechanism; §16.2 asserts this exact
snippet from a real container so it cannot regress into
`EmbeddingGeneratorMissing`.

## 5. Amendments to sub-project 1

Two edits, both additive, both in the same PR as the first SP2 commit. There are
no others, and no breaking changes to SP1's merged surface.

Two near-misses are worth naming, because each is a change SP2 could plausibly have
demanded and deliberately does not:

- **`EdgeMemoryPressure` is not touched.** It is `{ Low, Moderate, Critical }` with
  `Low` as the zero value. SP2 needs a "no pressure reported" state for
  `IEdgeResourceMonitor.LastPressure`, and the obvious move — adding `None = 0` —
  would renumber all three existing members and is therefore not additive. The latch
  is `EdgeMemoryPressure?` instead (§6.4), which costs nothing and matches SP1's own
  `EdgeLifecycleRecord.Level`.
- **No SP1 csproj is edited**, including for the raised platform floors. SP2's
  projects declare `android 24.0` and Apple `15.1` (§4.1) because ORT's natives are
  built there; `SupportedOSPlatformVersion` is a per-project property, not part of a
  TFM, so raising it in SP2's own files changes nothing in SP1's. The single
  contingency is `Qavren.Edge.DeviceTests` — SP1's *test host*, not a shipped
  package — which may need the same bump for the merged Android manifest to resolve.
  §19 item 2 is the build that decides; if it is needed it is a one-line change to a
  test project and no shipped SP1 package moves.

**5.1 `Qavren.Edge.Core` — `EdgeErrorCode` gains the 5000–5299 range.** Values
1001–4001 are untouched; the full list is in §15. `EdgeException`'s `HelpLink`
convention applies to the new codes for free.

**5.2 `Qavren.Edge.Sqlite` — `FtsTable` gains an options overload.** SP2 needs
`content_rowid` (its FTS5 sidecar keys on `"_rowid"`, not the implicit rowid alias
name) and `remove_diacritics 2` (unicode61's default of 1 has a documented
multi-diacritic bug). Rather than emit a second FTS5 DDL generator inside SP2, the
existing helper grows a record:

```csharp
namespace Qavren.Edge.Sqlite.Fts;

public sealed record FtsTableOptions
{
    public string? ContentTable { get; init; }
    public string ContentRowId { get; init; } = "rowid";
    /// <summary>0 | 1 | 2. Default 2: unicode61's default of 1 has a known multi-diacritic bug.</summary>
    public int RemoveDiacritics { get; init; } = 2;
    /// <summary><c>prefix='2 3'</c>. Null omits the option.</summary>
    public string? Prefix { get; init; }
}

public static partial class FtsTable
{
    public static string BuildCreateSql(string name, IReadOnlyList<string> columns,
        FtsTokenizer tokenizer, FtsTableOptions options);
    public static Task CreateAsync(SqliteConnection connection, string name,
        IReadOnlyList<string> columns, FtsTokenizer tokenizer, FtsTableOptions options,
        CancellationToken cancellationToken = default);
    public static IReadOnlyList<string> BuildSyncTriggerSql(string ftsTable, string contentTable,
        IReadOnlyList<string> columns, string contentRowId);
}
```

The existing four-argument overloads keep their behaviour byte for byte; SP1's
golden-SQL tests continue to pass unchanged. `RemoveDiacritics` is emitted only for
`unicode61` and `trigram`, which are the only tokenizers that accept it.

`EdgeStartupOrder` is **not** changed: SP2's orders (200, 210, 220, 300) already
fit between `Migrations = 100` and `ConsumerDefault = 1000`, and they are published
from SP2's own `EdgeAiStartupOrder`. `EdgeEventIds` is likewise not changed; SP2
publishes `EdgeAiEventIds` in the 600–899 range (§14). `IEdgePaths` is **not**
changed: the model root is a new interface in `Qavren.Edge.Onnx` (§6.2), which
SP1 should absorb at 1.0 with a type-forward — recorded as ADR 0005, not left to
drift.

## 6. `Qavren.Edge.Onnx`

### 6.1 Execution-provider policy

```csharp
namespace Qavren.Edge.Onnx;

/// <summary>
/// The providers SP2 can actually append. NNAPI is deliberately absent — see §9.2 and §20:
/// ORT's portable <c>AppendExecutionProvider(string, …)</c> overload documents support for
/// "QNN", "SNPE", "XNNPACK", "CoreML" and "AZURE" only, so NNAPI is not reachable through the
/// one code path this package uses.
/// </summary>
public enum EdgeExecutionProvider { Cpu = 0, XnnPack = 1, CoreMl = 2 }

/// <summary>
/// One attempt. <see cref="Accepted"/> means <c>AppendExecutionProvider</c> returned without
/// throwing — it does NOT mean this provider executed every node. ORT exposes no managed API for
/// per-node EP assignment; <see cref="CoreMlProviderOptions.ProfileComputePlan"/> is the only
/// truthful signal for that.
/// </summary>
public sealed record ExecutionProviderAttempt(
    EdgeExecutionProvider Provider,
    bool Accepted,
    IReadOnlyDictionary<string, string> Options,
    string? Failure);

public sealed record ExecutionProviderReport(
    EdgeExecutionProvider Accepted,
    IReadOnlyList<ExecutionProviderAttempt> Attempts);

public sealed class CoreMlProviderOptions
{
    /// <summary>MLProgram, not ORT's NeuralNetwork default: NeuralNetwork's supported-op table
    /// lacks LayerNormalization, Gelu and Erf, so a BERT encoder fragments into CPU-fallback
    /// partitions and pays a CPU-to-ANE round trip per block.</summary>
    public string ModelFormat { get; set; } = "MLProgram";
    public string MLComputeUnits { get; set; } = "CPUAndNeuralEngine";

    /// <summary>
    /// Emits <c>RequireStaticInputShapes=1</c>. <b>Default false, and that is load-bearing.</b>
    /// CoreML partitions the graph at SESSION-CREATION time from the graph's DECLARED shapes, not
    /// from the shapes fed at Run time. All four presets declare
    /// <c>input_ids</c>/<c>attention_mask</c>/<c>token_type_ids</c> as
    /// <c>[batch_size, sequence_length]</c> — symbolic. Setting this to 1 against symbolic dims
    /// means CoreML takes few or no nodes and the whole graph silently falls back to CPU, which
    /// <see cref="ExecutionProviderAttempt.Accepted"/> cannot detect. Turning it on is therefore
    /// only correct together with <see cref="OnnxSessionOptions.FreeDimensionOverrides"/> (or
    /// <c>OnnxEmbeddingOptions.PinnedSequenceLength</c>, which sets both), and the session factory
    /// refuses the combination without them.
    /// </summary>
    public bool RequireStaticInputShapes { get; set; }

    public bool EnableModelCache { get; set; } = true;
    /// <summary>iOS 18+. Emits SpecializationStrategy=FastPrediction.</summary>
    public bool FastPrediction { get; set; }
    /// <summary>Diagnostics only: logs the hardware each operator was dispatched to.</summary>
    public bool ProfileComputePlan { get; set; }
}

public sealed class XnnPackProviderOptions
{
    /// <summary>XNNPACK's own pool. Default Math.Clamp(ProcessorCount / 2, 1, 4): saturating every
    /// core of a big.LITTLE phone is a thermal-throttle generator, not a speedup.</summary>
    public int? IntraOpNumThreads { get; set; }
}

public sealed class OnnxExecutionProviderPolicy
{
    /// <summary>Tried in order; the first that appends without throwing wins. CPU is appended last
    /// whenever <see cref="FallBackToCpu"/> is true. Null uses the per-RID default in §9.2.</summary>
    public IReadOnlyList<EdgeExecutionProvider>? Order { get; set; }
    public bool FallBackToCpu { get; set; } = true;
    /// <summary>Failure of a required provider throws instead of falling through.</summary>
    public IReadOnlyList<EdgeExecutionProvider> Required { get; set; } = [];

    public CoreMlProviderOptions CoreMl { get; } = new();
    public XnnPackProviderOptions XnnPack { get; } = new();

    /// <summary>0 lets ORT choose. Forced to 1 whenever XNNPACK is accepted, per ORT's own
    /// anti-contention guidance.</summary>
    public int IntraOpNumThreads { get; set; }
    public GraphOptimizationLevel GraphOptimization { get; set; } = GraphOptimizationLevel.ORT_ENABLE_ALL;
    /// <summary>Merged over the computed options, keyed by ORT's provider name
    /// ("CoreML", "XNNPACK").</summary>
    public IDictionary<string, IReadOnlyDictionary<string, string>> Overrides { get; }
}
```

### 6.2 Model paths

```csharp
/// <summary>
/// Where a 23-137 MB re-derivable blob belongs. Deliberately NOT <see cref="IEdgePaths.Data"/>
/// (backed up on all three platforms; Android's Auto Backup quota is 25 MB per app, so one model
/// there consumes the whole quota and stops the user's real database being backed up) and
/// deliberately NOT <see cref="IEdgePaths.Cache"/> (the OS may purge it mid-session).
/// </summary>
public interface IEdgeModelPaths
{
    /// <summary>Durable, excluded from cloud backup, never OS-purged. Created on first access.</summary>
    string Models { get; }

    /// <summary>Sibling of <see cref="Models"/>. Owned and purged by this library, never by the OS.</summary>
    string OrtCache { get; }
}

/// <summary>net10.0 and Windows: <c>&lt;IEdgePaths.Data&gt;/models</c> and <c>/ort-cache</c>.</summary>
public sealed class DefaultEdgeModelPaths : IEdgeModelPaths
{
    public DefaultEdgeModelPaths(IEdgePaths paths);
    public string Models { get; }
    public string OrtCache { get; }
}
// Platforms/Android: Context.NoBackupFilesDir/qavren-edge/{models,ort-cache}  (auto-excluded)
// Platforms/iOS:     Library/Application Support/qavren-edge/{models,ort-cache},
//                    NSURLIsExcludedFromBackupKey set on each directory AFTER creation.
// Registered by AddOnnx() under the platform compile-item guards; never cached as an absolute
// path across launches, because the iOS sandbox path carries an app GUID that changes on reinstall.
```

### 6.3 Model description and provisioning

```csharp
public enum OnnxModelFileRole { Graph, GraphExternalData, Vocabulary, TokenizerModel, Auxiliary }

/// <summary><paramref name="Sha256"/> is lowercase hex. On Hugging Face that is the git-LFS
/// <c>oid</c> — never the <c>xetHash</c> those Xet-backed repos also return.</summary>
public sealed record OnnxModelFile(string RelativePath, OnnxModelFileRole Role, long SizeBytes, string Sha256);

public sealed record OnnxModelManifest
{
    public required string ModelId { get; init; }
    /// <summary>A LIST, not one entry: some ONNX mirrors split weights into external data
    /// (a 56 KB model.onnx beside a 90 MB model.onnx_data) and the sidecar must land next to the
    /// graph under its exact filename. No shipped preset does, but the type must allow it.</summary>
    public required IReadOnlyList<OnnxModelFile> Files { get; init; }
    public required string GraphFile { get; init; }
    public required string SpdxLicense { get; init; }
    public string? HuggingFaceRepo { get; init; }
    /// <summary>A full commit SHA. Never "main": a moving revision silently changes the vectors.</summary>
    public string? HuggingFaceRevision { get; init; }
    public long TotalSizeBytes { get; }
}

public enum ModelProvisioningSource { AlreadyPresent, File, Bundled, Downloaded }

public sealed record ProvisionedModel(
    string ModelId,
    string Directory,                                        // <Models>/<modelId>/<sha16>
    string GraphPath,
    string GraphSha256,
    IReadOnlyDictionary<string, string> Files,               // RelativePath -> absolute path
    ModelProvisioningSource Source,
    TimeSpan Duration);

public readonly record struct ModelProvisioningProgress(
    string ModelId, string RelativePath, long BytesCompleted, long? BytesTotal, bool Resumed);

public interface IOnnxModelSource
{
    string Name { get; }
    bool CanProvide(OnnxModelManifest manifest);
    ValueTask<ProvisionedModel> EnsureAsync(OnnxModelManifest manifest,
        IProgress<ModelProvisioningProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>Files already on disk, in the model root or an explicit directory.</summary>
public sealed class FileOnnxModelSource : IOnnxModelSource
{
    public FileOnnxModelSource(IEdgeModelPaths paths, string? directoryOverride = null);
}

/// <summary>An app-packaged asset, copied out once. The opener is the consumer's — in MAUI
/// <c>FileSystem.OpenAppPackageFileAsync</c> — so this package never references MAUI. A copy is
/// unavoidable: on Android a MauiAsset is an AssetManager stream with no path and no length.</summary>
public sealed class BundledOnnxModelSource : IOnnxModelSource
{
    public BundledOnnxModelSource(IEdgeModelPaths paths,
        Func<string, CancellationToken, ValueTask<Stream>> openAsset);
}

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

public sealed class HttpOnnxModelSource : IOnnxModelSource
{
    public HttpOnnxModelSource(HttpClient httpClient, IEdgeModelPaths paths,
        IOptions<HttpOnnxModelSourceOptions> options, ILogger<HttpOnnxModelSource> logger);
}

public interface IOnnxModelStore
{
    /// <summary>Idempotent, concurrency-safe. Sources are probed in registration order.</summary>
    ValueTask<ProvisionedModel> EnsureAsync(OnnxModelManifest manifest,
        IProgress<ModelProvisioningProgress>? progress = null, CancellationToken cancellationToken = default);
    bool IsProvisioned(OnnxModelManifest manifest);
    ProvisionedModel? TryGet(string modelId);
    /// <summary>Deletes the model directory and its CoreML cache subtree.</summary>
    ValueTask RemoveAsync(string modelId, CancellationToken cancellationToken = default);
    IReadOnlyList<string> ProvisionedModelIds { get; }
}
```

### 6.4 Resource monitor

```csharp
public enum EdgeThermalState { Unknown = 0, Nominal, Fair, Serious, Critical }

public sealed record EdgeResourceSnapshot(
    long? AvailableMemoryBytes,
    bool? IsLowMemory,
    EdgeThermalState Thermal,
    float? ThermalHeadroom,
    bool? IsLowPowerMode,
    EdgeMemoryPressure? LastPressure);

/// <summary>
/// Apple: <c>os_proc_available_memory()</c> (0 means unknown or already over — treated as unknown,
/// never as a refusal) and <c>ProcessInfo.ThermalState</c>/<c>IsLowPowerModeEnabled</c>.
/// Android: <c>ActivityManager.GetMemoryInfo</c> (availMem, lowMemory), <c>PowerManager.CurrentThermalStatus</c>
/// and <c>GetThermalHeadroom(10)</c>. Desktop: <c>GC.GetGCMemoryInfo()</c>, advisory only.
/// Reads are cached for one second: Android's GetThermalHeadroom is rate-limited to ~1 Hz and
/// returns NaN when polled faster.
/// </summary>
public interface IEdgeResourceMonitor
{
    EdgeResourceSnapshot Read();

    /// <summary>
    /// The most recent level SP1's lifecycle hub reported, latched here by
    /// <c>OnnxLifecycleObserver</c> (§14.2) and cleared to <c>null</c> on <c>Resumed</c>.
    /// <para>
    /// <b>Nullable, and that is load-bearing.</b> SP1's merged
    /// <c>EdgeMemoryPressure</c> is <c>{ Low, Moderate, Critical }</c> with no <c>None</c> member
    /// and <c>Low</c> as the zero value, so a non-nullable latch could not express "nothing has
    /// been reported" or "cleared" — it would read as <c>Low</c> from the moment the container is
    /// built. Adding a <c>None = 0</c> member would renumber <c>Low</c>/<c>Moderate</c>/
    /// <c>Critical</c> and is therefore not an additive change, which §5 forbids. <c>null</c>
    /// costs nothing and matches SP1's own idiom: <c>EdgeLifecycleRecord.Level</c> is already
    /// <c>EdgeMemoryPressure?</c>.
    /// </para>
    /// This is the shared pressure signal <c>Qavren.Edge.Embeddings.Onnx</c> reads to shrink its
    /// batch size: the batch size lives in the L1 package and the lifecycle observer lives in L0,
    /// so the flag has to sit in L0 or the dependency direction in §4.1 breaks. It is a latch, not
    /// an event stream — a reader gets the current level and nothing else.
    /// </summary>
    EdgeMemoryPressure? LastPressure { get; }

    /// <summary>Called by <c>OnnxLifecycleObserver</c> only. Not part of the consumer surface.
    /// <c>null</c> clears the latch.</summary>
    void SetPressure(EdgeMemoryPressure? level);
}
```

### 6.5 Session hosting

```csharp
public sealed record OnnxSessionSignature(
    IReadOnlyList<string> InputNames,
    IReadOnlyList<string> OutputNames,
    IReadOnlyDictionary<string, string> InputTypes,
    IReadOnlyDictionary<string, IReadOnlyList<long>> OutputShapes);

public sealed record OnnxSessionInfo(
    string ModelId,
    string GraphPath,
    string GraphSha256,
    OnnxSessionSignature Signature,
    ExecutionProviderReport ExecutionProviders,
    TimeSpan LoadDuration,
    DateTimeOffset LoadedAtUtc,
    int LoadCount,
    int ActiveLeases,
    bool IsLoaded);

/// <summary>A borrowed session. Dispose releases the lease; the underlying
/// <see cref="InferenceSession"/> is disposed only when the last lease returns after a drop.</summary>
public sealed class OnnxSessionLease : IDisposable
{
    public InferenceSession Session { get; }
    public OnnxSessionInfo Info { get; }
    public void Dispose();
}

public interface IOnnxSessionHost
{
    /// <summary>Awaits <c>IEdgeHost.EnsureStartedAsync</c>, provisions the model if needed, checks the
    /// memory budget, creates the session on first call, and returns a lease. Concurrent callers for
    /// one model share one session and one in-flight load.</summary>
    ValueTask<OnnxSessionLease> AcquireAsync(string modelId, CancellationToken cancellationToken = default);

    OnnxSessionInfo? Describe(string modelId);
    IReadOnlyList<OnnxSessionInfo> Sessions { get; }

    /// <summary>Marks matching sessions for drop and disposes the idle ones. Returns the count
    /// actually released; leased sessions die as their leases return.</summary>
    Task<int> DropAsync(bool includePinned = false, CancellationToken cancellationToken = default);
}

public sealed class OnnxSessionOptions
{
    public OnnxExecutionProviderPolicy ExecutionProviders { get; } = new();
    /// <summary>Dispose this session on MemoryPressure(Critical) and reload lazily.</summary>
    public bool DropOnMemoryPressure { get; set; } = true;
    /// <summary>Refuse a session when the OS reports less than
    /// modelBytes * factor + <see cref="MemoryHeadroomBytes"/>. 0 disables the pre-flight.
    /// The default is an engineering estimate anchored on the ~2x transient cost of session
    /// creation, NOT a measurement — see §19.</summary>
    public double MemoryHeadroomFactor { get; set; } = 2.5;
    public long MemoryHeadroomBytes { get; set; } = 48L * 1024 * 1024;

    /// <summary>
    /// Symbolic dimension name -> fixed value, applied with
    /// <c>SessionOptions.AddFreeDimensionOverrideByName</c> before the session is created. This is
    /// the ONLY mechanism that turns a graph's declared <c>[batch_size, sequence_length]</c> into
    /// static shapes, and therefore the only thing that makes
    /// <see cref="CoreMlProviderOptions.RequireStaticInputShapes"/> meaningful — padding the tensors
    /// fed at Run time does not, because CoreML partitions from the declared shapes at session
    /// creation. A session pinned this way serves exactly one shape: every batch is padded to it,
    /// and a second pinned shape would need a second session, which v1 does not do.
    /// Empty by default, so the default session is dynamic-shaped and CoreML partitions normally.
    /// </summary>
    public IDictionary<string, long> FreeDimensionOverrides { get; }

    /// <summary>Escape hatch for <c>SessionOptions.AddSessionConfigEntry</c>. Unrelated to
    /// <see cref="FreeDimensionOverrides"/>, which is a different ORT API.</summary>
    public IDictionary<string, string> SessionConfigEntries { get; }
}

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

/// <summary>
/// The order registry for every SP2 startup task. The constants are published from L0 because
/// that is the lowest package all three share; the task that USES each order is registered by the
/// package named in the comment, and §14.1 is the authority on which is which.
/// </summary>
public static class EdgeAiStartupOrder
{
    /// <summary>OrtEnv.CreateInstanceWithOptions. MUST precede any SessionOptions construction.
    /// Registered by <c>Qavren.Edge.Onnx</c>.</summary>
    public const int OnnxEnvironment = 200;

    /// <summary>Opt-in. Registered by <c>Qavren.Edge.Onnx</c>.</summary>
    public const int ModelProvisioning = 210;

    /// <summary>Opt-in. Two tasks share this order: <c>Qavren.Edge.Onnx</c>'s
    /// <c>WarmUpSessionAtStartup</c> (loads the session; needs no tokenizer) and
    /// <c>Qavren.Edge.Embeddings.Onnx</c>'s <c>WarmUpEmbeddingsAtStartup</c> (load plus one dummy
    /// batch; needs the tokenizer and the generator, both of which live in L1).</summary>
    public const int SessionWarmUp = 220;

    /// <summary>Opt-in; the migration path (order 100) is preferred. Registered by
    /// <c>Qavren.Edge.VectorData</c>.</summary>
    public const int VectorSchema = 300;
}

public static class OnnxEdgeBuilderExtensions
{
    /// <summary>Idempotent. Registers the session host, the model store, the resource monitor,
    /// <see cref="IEdgeModelPaths"/>, the OrtEnv startup task, the lifecycle observer and the
    /// diagnostics contributor.</summary>
    public static EdgeBuilder AddOnnx(this EdgeBuilder builder, Action<OnnxOptions>? configure = null);

    /// <summary>Declares a model. Provisioning and session creation stay lazy.</summary>
    public static EdgeBuilder AddOnnxModel(this EdgeBuilder builder, OnnxModelManifest manifest,
        Action<OnnxSessionOptions>? configure = null);

    /// <summary>Sources are probed in registration order. AddOnnx registers
    /// <see cref="FileOnnxModelSource"/> last as the fallback.</summary>
    public static EdgeBuilder AddModelSource<TSource>(this EdgeBuilder builder) where TSource : class, IOnnxModelSource;
    public static EdgeBuilder AddModelSource(this EdgeBuilder builder, Func<IServiceProvider, IOnnxModelSource> factory);
    public static EdgeBuilder AddHuggingFaceModelSource(this EdgeBuilder builder,
        Action<HttpOnnxModelSourceOptions>? configure = null);
    public static EdgeBuilder AddBundledModelSource(this EdgeBuilder builder,
        Func<string, CancellationToken, ValueTask<Stream>> openAsset);

    /// <summary>Startup order 210. Verifies presence and faults startup if the model is missing.
    /// Off by default, and refused for HTTP-only models: blocking IEdgeHost.Started on a download
    /// also blocks every IEdgeDatabase.OpenConnectionAsync.</summary>
    public static EdgeBuilder ProvisionModelAtStartup(this EdgeBuilder builder, string modelId);

    /// <summary>Startup order 220. Creates the session so the first real call does not pay
    /// graph optimisation or a CoreML compile. Loads only — it runs no inference, because a batch
    /// needs a tokenizer and a generator and both live in <c>Qavren.Edge.Embeddings.Onnx</c>.
    /// The load-plus-batch variant is <c>WarmUpEmbeddingsAtStartup</c> there. Off by default.</summary>
    public static EdgeBuilder WarmUpSessionAtStartup(this EdgeBuilder builder, string modelId);

    public static EdgeBuilder UseExecutionProviderPolicy(this EdgeBuilder builder,
        Action<OnnxExecutionProviderPolicy> configure);

    /// <summary>Replaces <see cref="IEdgeModelPaths"/>. Tests point it at a temp directory.</summary>
    public static EdgeBuilder UseModelPaths(this EdgeBuilder builder, IEdgeModelPaths paths);
}

public sealed class EdgeOnnxException : EdgeException
{
    public EdgeOnnxException(EdgeErrorCode code, string message, Exception? innerException = null);
    public string? ModelId { get; init; }
    public string? RuntimeIdentifier { get; init; }
    public IReadOnlyList<ExecutionProviderAttempt>? ExecutionProviders { get; init; }
    public long? RequiredBytes { get; init; }
    public long? AvailableBytes { get; init; }
    public string? Remediation { get; init; }
}

public sealed class EdgeModelProvisioningException : EdgeException
{
    public EdgeModelProvisioningException(EdgeErrorCode code, string modelId, string relativePath,
        string message, Exception? innerException = null);
    public string ModelId { get; }
    public string RelativePath { get; }
    public string? ExpectedSha256 { get; init; }
    public string? ActualSha256 { get; init; }
    public long? ExpectedBytes { get; init; }
    public long? ActualBytes { get; init; }
    public Uri? Source { get; init; }
}
```

## 7. `Qavren.Edge.Embeddings.Onnx`

```csharp
namespace Qavren.Edge.Embeddings.Onnx;

public enum EdgeTokenizerKind { WordPieceVocabTxt = 0, UnigramTokenizerJson = 1 }
public enum EmbeddingPooling { Mean = 0, Cls = 1 }
public enum EmbeddingInputKind { Document = 0, Query = 1 }
public enum EmbeddingTruncation { Truncate = 0, Throw = 1 }

/// <summary>One padded batch, row-major, ready to wrap in OrtValues.</summary>
public sealed record TokenizedBatch(
    long[] InputIds,          // row-major; the first BatchSize * SequenceLength elements
                              // carry the batch (the buffers are pool-rented and may be longer)
    long[] AttentionMask,     // 1 = real token, 0 = padding
    long[] TokenTypeIds,      // all zeros for a single-sequence encoder
    int BatchSize,
    int SequenceLength,
    int[] TokenCounts,        // per input, pre-padding, including [CLS]/[SEP]
    bool[] Truncated);

/// <summary>
/// The one abstraction over Microsoft.ML.Tokenizers SP2 exposes. Implementations hold the CONCRETE
/// tokenizer type, never the <c>Tokenizer</c> base: <c>BertTokenizer.EncodeToIds</c> is declared
/// <c>new</c>, so a base-typed field silently drops [CLS] and [SEP].
/// </summary>
public interface IEdgeTokenizer : IDisposable
{
    EdgeTokenizerKind Kind { get; }
    int VocabularySize { get; }
    int MaxSequenceLength { get; }
    int PadTokenId { get; }

    /// <summary>
    /// Single encode into a caller-owned buffer. Writes ids into <paramref name="destination"/>,
    /// adding the special tokens and truncating so the total never exceeds
    /// <paramref name="maxTokens"/>. Returns the id count; throws
    /// <see cref="ArgumentException"/> when <paramref name="destination"/> is shorter than
    /// <paramref name="maxTokens"/>.
    /// <para>
    /// <b>This is NOT allocation-free on the pinned tokenizer version, and the contract must not
    /// claim otherwise.</b> Every <c>EncodeToIds</c> overload in
    /// <c>Microsoft.ML.Tokenizers</c> 2.0.0 returns <c>IReadOnlyList&lt;int&gt;</c>; the only
    /// span-destination members in the library — <c>BuildInputsWithSpecialTokens</c>,
    /// <c>GetSpecialTokensMask</c>, <c>CreateTokenTypeIdsFromSequences</c> — all take ids that
    /// already exist. The implementation therefore calls the truncating <c>EncodeToIds</c>
    /// overload and copies the result into <paramref name="destination"/>: one short-lived list
    /// per input, and no allocation in the batch assembler, which is where the pooled
    /// <c>long[]</c> buffers that actually matter live. The span signature is kept because it is
    /// the shape the assembler wants, because it keeps the per-input allocation an implementation
    /// detail rather than a public one, and because Tokenizers 3.x may add a span overload that
    /// this method can then adopt without a surface change. §19 item 8's stress test measures the
    /// per-input list; if it ever matters, the fix is upstream, not a new signature here.
    /// </para>
    /// </summary>
    int Encode(ReadOnlySpan<char> text, int maxTokens, Span<int> destination, out int charsConsumed);

    /// <summary>Encodes, truncates, right-pads to the smallest configured bucket that fits the
    /// batch maximum, and synthesises the attention mask.</summary>
    TokenizedBatch EncodeBatch(IReadOnlyList<string> texts, int maxSequenceLength, IReadOnlyList<int> buckets);

    int CountTokens(ReadOnlySpan<char> text);

    /// <summary>Index into <paramref name="text"/> at which <paramref name="maxTokens"/> tokens are
    /// consumed. Exists so SP3's token-window chunker never writes a second token counter.</summary>
    int IndexByTokenCount(string text, int maxTokens, out int tokenCount);
}

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

public static class EdgeTokenizer
{
    public static IEdgeTokenizer CreateWordPiece(string vocabFilePath, WordPieceTokenizerOptions? options = null);
    public static IEdgeTokenizer CreateWordPiece(Stream vocabTxt, WordPieceTokenizerOptions? options = null);
    public static Task<IEdgeTokenizer> CreateWordPieceAsync(string vocabFilePath,
        WordPieceTokenizerOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Builds the tokenizer on first use and caches it for the process. A provider rather than a
/// directly-injected <see cref="IEdgeTokenizer"/>, because the vocab file is
/// <see cref="OnnxModelFileRole.Vocabulary"/> inside the same manifest as the graph, and §10 keeps
/// provisioning LAZY: on a first launch there is no vocab path at the moment DI constructs the
/// generator, and DI factories cannot await a download. So the generator takes this, and the first
/// <c>GenerateAsync</c> awaits it — the same call that already awaits
/// <c>IOnnxSessionHost.AcquireAsync</c>, which provisions the same manifest. Construction parses
/// the whole vocab, so it happens exactly once, behind a <c>SemaphoreSlim(1)</c>, and the built
/// instance is reused for the process lifetime.
/// </summary>
public interface IEdgeTokenizerProvider
{
    ValueTask<IEdgeTokenizer> GetAsync(EmbeddingPreset preset, CancellationToken cancellationToken = default);

    /// <summary>Null until the first <see cref="GetAsync"/> completes. Diagnostics read this rather
    /// than forcing provisioning to report a vocab size.</summary>
    IEdgeTokenizer? Current { get; }
}

/// <summary>Allocation-free pooling over a session output span. Public because it is worth testing alone.</summary>
public static class EmbeddingPooler
{
    public static void MeanPool(ReadOnlySpan<float> lastHiddenState, ReadOnlySpan<long> attentionMask,
        int sequenceLength, int dimensions, Span<float> destination);
    public static void ClsPool(ReadOnlySpan<float> lastHiddenState, int sequenceLength, int dimensions,
        Span<float> destination);

    /// <summary>
    /// Mean/variance normalisation over the pooled vector, matching PyTorch
    /// <c>F.layer_norm(x, (dim,))</c> with no learned weight or bias: subtract the mean, divide by
    /// <c>sqrt(variance + epsilon)</c>. Required by nomic-embed-text-v1.5 and by nothing else here;
    /// see <see cref="EmbeddingPreset.PostPoolLayerNorm"/>.
    /// </summary>
    public static void LayerNorm(Span<float> vector, float epsilon);

    public static void L2Normalize(Span<float> vector);
}

/// <summary>Everything the ONNX graph does not tell you. A wrong preset is a silent quality bug, so
/// every field that changes the numbers is <c>required</c>.</summary>
public sealed record EmbeddingPreset
{
    public required string Id { get; init; }
    public required OnnxModelManifest Manifest { get; init; }
    public required string ModelFile { get; init; }
    public required string TokenizerFile { get; init; }
    public required EdgeTokenizerKind TokenizerKind { get; init; }
    public required bool LowerCase { get; init; }
    public required int Dimensions { get; init; }
    public required int MaxSequenceLength { get; init; }
    public required EmbeddingPooling Pooling { get; init; }

    /// <summary>
    /// Apply <see cref="EmbeddingPooler.LayerNorm"/> between pooling and L2 normalisation.
    /// <b>Not</b> Matryoshka truncation, which is cut (§20) — this is an unconditional step in
    /// nomic-embed-text-v1.5's reference pooling. Its <c>modules.json</c> carries only Transformer +
    /// Pooling (no Normalize module) and its documented pipeline is mean-pool →
    /// <c>F.layer_norm</c> over the 768 dim → (optional truncate) → L2. Omitting it ships
    /// plausible-but-off vectors with no error anywhere, which is exactly what the <c>required</c>
    /// fields on this record exist to prevent. False for the three MiniLM/BGE presets.
    /// </summary>
    public bool PostPoolLayerNorm { get; init; }

    /// <summary>Epsilon for <see cref="PostPoolLayerNorm"/>. 1e-5 is PyTorch's default and what
    /// nomic's reference uses.</summary>
    public float LayerNormEpsilon { get; init; } = 1e-5f;

    public bool Normalize { get; init; } = true;
    public string? QueryPrefix { get; init; }
    public string? DocumentPrefix { get; init; }
    public string InputIdsName { get; init; } = "input_ids";
    public string AttentionMaskName { get; init; } = "attention_mask";
    /// <summary>Null when the graph declares no such input. Bound BY NAME, never by position:
    /// nomic declares (input_ids, token_type_ids, attention_mask) while MiniLM declares
    /// (input_ids, attention_mask, token_type_ids).</summary>
    public string? TokenTypeIdsName { get; init; } = "token_type_ids";
    public string OutputName { get; init; } = "last_hidden_state";
    public IReadOnlyList<int> SequenceBuckets { get; init; } = [64, 128, 256, 512];
}

public static class EmbeddingPresets
{
    /// <summary>DEFAULT. all-MiniLM-L6-v2 int8, 23,026,053 B, 384-d, mean + L2, 256 tokens, no
    /// prefixes, Apache-2.0. Its qint8_arm64 / qint8_avx512 / qint8_avx512_vnni files are ONE blob
    /// under three names, so a single download covers every RID. 384 matches SP1's own
    /// VecTable example, so the sample needs no schema change.</summary>
    public static EmbeddingPreset MiniLmL6V2Int8 { get; }

    /// <summary>all-MiniLM-L6-v2 fp32, 90,405,214 B. Same numbers, no quantization loss.</summary>
    public static EmbeddingPreset MiniLmL6V2Fp32 { get; }

    /// <summary>bge-small-en-v1.5, 133,093,490 B fp32, 384-d, <b>CLS</b> pooling, 512 tokens, MIT.
    /// BAAI publishes no quantized ONNX. QueryPrefix is set and applied to queries only.</summary>
    public static EmbeddingPreset BgeSmallEnV15 { get; }

    /// <summary>OPT-IN. nomic-embed-text-v1.5 int8, 137,296,292 B, <b>768-d</b>, Apache-2.0,
    /// mandatory "search_query: " / "search_document: " prefixes. Doubles vec0 storage.
    /// Pooling is mean + <b><see cref="EmbeddingPreset.PostPoolLayerNorm"/> = true</b> + L2: this
    /// model's modules.json has no Normalize module and its reference pipeline inserts
    /// <c>F.layer_norm</c> over the 768 dim between pooling and L2. It is the only shipped preset
    /// that sets the flag, and the preset-catalogue table test (§16.1) guards it.</summary>
    public static EmbeddingPreset NomicEmbedTextV15Int8 { get; }

    public static IReadOnlyList<EmbeddingPreset> All { get; }
    public static EmbeddingPreset ById(string id);
}

public sealed class OnnxEmbeddingOptions
{
    public EmbeddingPreset Preset { get; set; } = EmbeddingPresets.MiniLmL6V2Int8;
    /// <summary>Null uses <see cref="HttpOnnxModelSource"/> over the preset's manifest.</summary>
    public IOnnxModelSource? ModelSource { get; set; }
    public int MaxBatchSize { get; set; } = 16;

    /// <summary>
    /// Null (the default) uses <see cref="EmbeddingPreset.SequenceBuckets"/>: the graph keeps its
    /// declared symbolic <c>sequence_length</c>, CoreML partitions normally, and each batch is
    /// padded to the smallest bucket that fits it.
    /// <para>
    /// Set to one of the buckets to pin the shape instead. That emits
    /// <c>AddFreeDimensionOverrideByName("sequence_length", N)</c> and
    /// <c>("batch_size", MaxBatchSize)</c> into
    /// <see cref="OnnxSessionOptions.FreeDimensionOverrides"/> and turns
    /// <see cref="CoreMlProviderOptions.RequireStaticInputShapes"/> on. One pinned shape means one
    /// session and one CoreML compile, and every batch — including a batch of one short string —
    /// pays a full N-token run. It is a measured trade, not a default; §19 item 13 owns the
    /// measurement.
    /// </para>
    /// </summary>
    public int? PinnedSequenceLength { get; set; }

    /// <summary>Concurrent ORT Runs. 1 by default: ORT already parallelises intra-op, and a second
    /// inference doubles peak native memory on a phone.</summary>
    public int MaxConcurrency { get; set; } = 1;
    public EmbeddingTruncation Truncation { get; set; } = EmbeddingTruncation.Truncate;
    /// <summary>Which prefix the unkeyed generator applies. The query sibling is reached through
    /// <see cref="EdgeEmbeddings.QueryServiceKey"/>.</summary>
    public EmbeddingInputKind DefaultInputKind { get; set; } = EmbeddingInputKind.Document;
    /// <summary>Halve the effective batch size after MemoryPressure(Moderate) until Resumed.</summary>
    public bool ShrinkBatchUnderMemoryPressure { get; set; } = true;
    public OnnxSessionOptions Session { get; } = new();
}

public sealed record OnnxEmbeddingGeneratorInfo(
    string PresetId, string ModelId, int Dimensions, int MaxSequenceLength,
    EmbeddingPooling Pooling, bool Normalize, string? QueryPrefix, string? DocumentPrefix,
    EdgeTokenizerKind TokenizerKind, int VocabularySize, string GraphPath, string GraphSha256,
    ExecutionProviderReport ExecutionProviders);

/// <summary>
/// The single public leaf. It implements the LITERAL closed generic MEVD pattern-matches; a
/// generator typed to any other input type is silently unresolvable by a vector store.
/// Thread-safe: concurrent calls serialise on one semaphore around ORT Run.
/// </summary>
public sealed class OnnxEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public OnnxEmbeddingGenerator(
        IOnnxSessionHost sessionHost,
        IEdgeTokenizerProvider tokenizers,
        IEdgeResourceMonitor resources,
        IOptions<OnnxEmbeddingOptions> options,
        EmbeddingInputKind inputKind,
        ILogger<OnnxEmbeddingGenerator> logger,
        TimeProvider timeProvider);

    public int Dimensions { get; }
    public OnnxEmbeddingGeneratorInfo Info { get; }

    /// <summary>Exactly one embedding per input, in input order. An empty sequence returns an empty
    /// collection without touching ORT.</summary>
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns, in order: <c>this</c> when assignable; <see cref="EmbeddingGeneratorMetadata"/>;
    /// <see cref="OnnxEmbeddingGeneratorInfo"/>; <see cref="EmbeddingPreset"/>;
    /// <see cref="OnnxSessionInfo"/>; <see cref="IEdgeTokenizer"/>; and, for serviceKey
    /// <see cref="EdgeEmbeddings.QueryServiceKey"/>, the query-prefixed sibling generator —
    /// returned for <c>serviceType</c> of either the non-generic <see cref="IEmbeddingGenerator"/>
    /// or the closed generic, because the vector store asks with the non-generic type
    /// (§12.5) while a direct caller usually asks with the closed one.</summary>
    public object? GetService(Type serviceType, object? serviceKey = null);

    /// <summary>Releases this generator's session lease. Does NOT dispose the shared session — the
    /// session host owns it — so wrapping this in a DelegatingEmbeddingGenerator (which disposes its
    /// inner by default) is safe.</summary>
    public void Dispose();
}

public static class EdgeEmbeddings
{
    /// <summary>
    /// The service key under which a generator exposes its query-prefixed sibling through
    /// <c>GetService</c>. A named convention, not a private protocol — and deliberately a bare
    /// string constant rather than a shared type, because
    /// <c>Qavren.Edge.VectorData</c> cannot reference this package (§2 decision 2) and must
    /// therefore carry its own copy of the same literal in
    /// <c>EdgeVectorData.QueryGeneratorServiceKey</c>. §16.2 asserts the two constants are equal
    /// from a test project that references both, so they cannot drift.
    /// </summary>
    public const string QueryServiceKey = "qavren.edge.query";

    /// <summary>
    /// Resolves the query sibling, falling back to <paramref name="generator"/> when it exposes
    /// none (a third-party generator, or a preset that declares no prefixes). Receives and returns
    /// the NON-generic <see cref="IEmbeddingGenerator"/> on purpose: the vector store holds
    /// generators as the non-generic interface (§2 decision 2), a third-party generator may be
    /// typed <c>IEmbeddingGenerator&lt;DataContent, Embedding&lt;float&gt;&gt;</c>, and a
    /// closed-generic receiver would not bind to either without a cast.
    /// </summary>
    public static IEmbeddingGenerator AsQueryGenerator(this IEmbeddingGenerator generator);
}

public static class OnnxEmbeddingsEdgeBuilderExtensions
{
    /// <summary>Calls AddOnnx + AddOnnxModel, then delegates to
    /// <c>services.AddEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c>, which registers BOTH
    /// the closed generic and the non-generic forwarding descriptor. Hand-registering would satisfy
    /// only one and break half of MEVD and Semantic Kernel.</summary>
    public static EdgeBuilder AddOnnxEmbeddings(this EdgeBuilder builder,
        Action<OnnxEmbeddingOptions>? configure = null);

    /// <summary>Same, plus the MEAI pipeline for UseLogging / UseOpenTelemetry / UseDistributedCache.
    /// SP2 ships no middleware of its own; those three already exist upstream.</summary>
    public static EdgeBuilder AddOnnxEmbeddings(this EdgeBuilder builder,
        Action<OnnxEmbeddingOptions>? configure,
        Action<EmbeddingGeneratorBuilder<string, Embedding<float>>> pipeline);

    /// <summary>Keyed registration, mirroring SP1's named-database convention. Delegates to
    /// <c>AddKeyedEmbeddingGenerator</c>.</summary>
    public static EdgeBuilder AddOnnxEmbeddings(this EdgeBuilder builder, string name,
        Action<OnnxEmbeddingOptions>? configure = null,
        Action<EmbeddingGeneratorBuilder<string, Embedding<float>>>? pipeline = null);

    /// <summary>
    /// Startup order <see cref="EdgeAiStartupOrder.SessionWarmUp"/> (220). Builds the tokenizer,
    /// acquires the session and embeds one short string, so the first user-visible call pays
    /// neither vocab parsing nor a CoreML compile. Registered HERE, not in
    /// <c>Qavren.Edge.Onnx</c>: a dummy batch needs <see cref="IEdgeTokenizerProvider"/> and the
    /// generator, and both are L1. The load-only sibling is
    /// <c>OnnxEdgeBuilderExtensions.WarmUpSessionAtStartup</c>. Off by default, because it forces
    /// provisioning and §10 keeps that lazy.
    /// </summary>
    public static EdgeBuilder WarmUpEmbeddingsAtStartup(this EdgeBuilder builder, string? name = null);
}

public sealed class EdgeEmbeddingException : EdgeException
{
    public EdgeEmbeddingException(EdgeErrorCode code, string presetId, string message,
        Exception? innerException = null);
    public string PresetId { get; }
    public int? Expected { get; init; }
    public int? Actual { get; init; }
}
```

## 8. `Qavren.Edge.VectorData`

```csharp
namespace Qavren.Edge.VectorData;

using MEVD = Microsoft.Extensions.VectorData;

public enum KeywordCombinator { Or = 0, And = 1 }

public static class EdgeVectorData
{
    /// <summary>
    /// The <c>GetService</c> service key under which an embedding generator may expose a
    /// query-prefixed sibling. The store asks every generator it is handed for one and falls back
    /// to the generator itself (§12.5).
    /// <para>
    /// This is a duplicate of <c>Qavren.Edge.Embeddings.Onnx</c>'s
    /// <c>EdgeEmbeddings.QueryServiceKey</c>, and duplicated on purpose: §2 decision 2 keeps this
    /// package free of any ONNX reference so the store works with Azure OpenAI embeddings and no
    /// ONNX at all, which means the symbol over there is not visible here. A shared constant would
    /// need a third assembly or an SP1 edit, and §5 allows neither. The two literals are asserted
    /// equal by §16.2, from the one test project that references both packages.
    /// </para>
    /// </summary>
    public const string QueryGeneratorServiceKey = "qavren.edge.query";
}

public sealed class EdgeVectorStoreOptions
{
    /// <summary>The SP1 keyed-database name. Null = the unnamed database.</summary>
    public string? DatabaseName { get; set; }
    /// <summary>
    /// Store-level default; overridable per collection and per vector property. Left null (the
    /// default), <c>AddVectorStore</c> fills it from DI with the registered non-generic
    /// <see cref="IEmbeddingGenerator"/> — see §12.5. Set it explicitly only to override that, or
    /// when constructing <see cref="EdgeVectorStore"/> by hand outside a container.
    /// </summary>
    public IEmbeddingGenerator? EmbeddingGenerator { get; set; }

    /// <summary>
    /// Store-level default for the generator that embeds a search <c>searchValue</c>. Left null,
    /// <c>AddVectorStore</c> fills it with the document generator's
    /// <see cref="EdgeVectorData.QueryGeneratorServiceKey"/> sibling, falling back to the document
    /// generator when it exposes none. Overridable per collection.
    /// </summary>
    public IEmbeddingGenerator? QueryEmbeddingGenerator { get; set; }
    /// <summary><c>{0}</c> = collection name.</summary>
    public string VectorTableNameFormat { get; set; } = "{0}_vec";
    public string FullTextTableNameFormat { get; set; } = "{0}_fts";
    /// <summary>vec0 chunk_size: 0 &lt; N &lt;= 4096 and N % 8 == 0 (SP1's VecTable enforces both).
    /// 256, not sqlite-vec's 1024: chunk_size is BOTH the storage unit and the KNN scan allocation
    /// (chunk_size * dims * 4 bytes per chunk read), and a phone corpus is thousands of rows.
    /// Reasoned, not benchmarked — see §19.</summary>
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

public sealed class EdgeVectorStoreCollectionOptions : MEVD.VectorStoreCollectionOptions
{
    public string? VectorTableName { get; set; }
    public string? FullTextTableName { get; set; }
    public int? ChunkSize { get; set; }
    public FtsTokenizer? FullTextTokenizer { get; set; }
    public KeywordCombinator? KeywordCombinator { get; set; }
    public RrfDefaults? Rrf { get; set; }
    /// <summary>Create the FTS5 sidecar even with no full-text property. Default false.</summary>
    public bool AlwaysCreateFullTextIndex { get; set; }
    /// <summary>Embeds the <c>searchValue</c> of a search when it must differ from the generator that
    /// embedded the stored records. Null resolves the document generator's
    /// <see cref="EdgeVectorData.QueryGeneratorServiceKey"/> sibling. The only correct way to drive a model
    /// with asymmetric prefixes, because MEVD always calls GenerateAsync with NO options.</summary>
    public IEmbeddingGenerator? QueryEmbeddingGenerator { get; set; }

    // Also present, deliberately NOT public: `internal int? RemoveDiacritics`, which carries
    // EdgeVectorStoreOptions.FullTextRemoveDiacritics down from the store when EdgeVectorStore
    // builds a collection. Internal because it is a store-level decision, not a per-collection
    // one; no public-surface or golden-API impact.
}

/// <summary>Per-query RRF tuning. A caller passing the plain MEVD
/// <c>HybridSearchOptions&lt;TRecord&gt;</c> gets the store defaults and never sees this type.</summary>
public sealed class EdgeHybridSearchOptions<TRecord> : MEVD.HybridSearchOptions<TRecord>
{
    public int? RrfK { get; set; }
    public double? VectorWeight { get; set; }
    public double? KeywordWeight { get; set; }
    /// <summary>Rows pulled from each lane before fusion. Null = (top + Skip) * CandidateMultiplier.</summary>
    public int? CandidateCount { get; set; }
    public KeywordCombinator? KeywordCombinator { get; set; }
}

public sealed class EdgeVectorStore : MEVD.VectorStore
{
    /// <summary>
    /// <paramref name="embeddingGenerator"/> and <paramref name="queryEmbeddingGenerator"/> are the
    /// DI seam: <c>AddVectorStore</c> passes the container's registered
    /// <see cref="IEmbeddingGenerator"/> and its query sibling here, and each is used only when the
    /// matching property on <paramref name="options"/> is null. Without this the flagship four-call
    /// path in §4.3 would register a generator that the store could never see, and a <c>string</c>
    /// source property would fail with <c>EmbeddingGeneratorMissing</c> on the first upsert.
    /// </summary>
    public EdgeVectorStore(IEdgeDatabase database, EdgeVectorStoreOptions? options = null,
        IEmbeddingGenerator? embeddingGenerator = null,
        IEmbeddingGenerator? queryEmbeddingGenerator = null,
        ILoggerFactory? loggerFactory = null);

    [RequiresDynamicCode("Reflects over TRecord. Use GetDynamicCollection in trimmed or AOT apps.")]
    [RequiresUnreferencedCode("Reflects over TRecord. Use GetDynamicCollection in trimmed or AOT apps.")]
    public override EdgeVectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(
        string name, MEVD.VectorStoreCollectionDefinition? definition = null);

    public override EdgeDynamicVectorStoreCollection GetDynamicCollection(
        string name, MEVD.VectorStoreCollectionDefinition definition);

    /// <summary>
    /// Data tables only. The exclusion is <b>structural, not by name</b>: a virtual table is
    /// recognised from its own DDL in <c>sqlite_master.sql</c>
    /// (<c>USING vec0</c> / <c>USING fts5</c>), and FTS5's shadow tables are recognised by being
    /// the <c>_data</c>/<c>_idx</c>/<c>_content</c>/<c>_docsize</c>/<c>_config</c> children of a
    /// name that is itself an fts5 virtual table. vec0's shadow tables are recognised the same
    /// way — a KNOWN child name under a parent that is itself a vec0 virtual table:
    /// <c>_info</c>, <c>_chunks</c>, <c>_rowids</c>, <c>_auxiliary</c>, plus the numbered
    /// <c>_vector_chunksNN</c> / <c>_metadatachunksNN</c> / <c>_metadatatextNN</c> families
    /// (sqlite-vec 0.1.9). The child set is enumerated rather than treating ANY child of a
    /// vec0 table as a shadow, because the broader rule silently hides a consumer's own
    /// <c>notes_vec_archive</c> sitting next to vec0 <c>notes_vec</c>.
    /// Name-prefix filtering would be wrong in both
    /// directions, because <see cref="EdgeVectorStoreOptions.VectorTableNameFormat"/> and
    /// <see cref="EdgeVectorStoreCollectionOptions.VectorTableName"/> let a sidecar be called
    /// anything, and a consumer's own data table may legitimately be called <c>foo_vec</c>.
    /// <c>sqlite_%</c> is excluded as well. §16.2 asserts both directions.
    /// Upstream's connector leaks its vec0 tables here; this one does not.
    /// </summary>
    public override IAsyncEnumerable<string> ListCollectionNamesAsync(CancellationToken cancellationToken = default);

    public override Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default);
    public override Task EnsureCollectionDeletedAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Returns <c>VectorStoreMetadata</c> (system name "sqlite"), the
    /// <see cref="IEdgeDatabase"/>, and <c>this</c>.</summary>
    public override object? GetService(Type serviceType, object? serviceKey = null);
}

public class EdgeVectorStoreCollection<TKey, TRecord>
    : MEVD.VectorStoreCollection<TKey, TRecord>, MEVD.IKeywordHybridSearchable<TRecord>
    where TKey : notnull where TRecord : class
{
    /// <summary>Reflects over <typeparamref name="TRecord"/> through
    /// <c>CollectionModelBuilder.Build</c>.</summary>
    [RequiresDynamicCode("…")] [RequiresUnreferencedCode("…")]
    public EdgeVectorStoreCollection(IEdgeDatabase database, string name,
        EdgeVectorStoreCollectionOptions? options = null);

    /// <summary>
    /// The trim/AOT-clean constructor, and the ONLY one
    /// <see cref="EdgeDynamicVectorStoreCollection"/> chains to. It takes a model that is already
    /// built and carries no trim annotations, so a derived ctor chaining to it inherits neither
    /// IL2026 nor IL3050. Without it the "dynamic is the AOT-safe path" claim would be
    /// unverifiable: a derived ctor chaining to an annotated base ctor inherits the warnings, the
    /// repo sets <c>TreatWarningsAsErrors</c> true, and the only way to compile would be a
    /// suppression — which is exactly what §17's <c>trim-smoke</c> job is supposed to be deciding.
    /// The caller (<c>EdgeVectorStore.GetDynamicCollection</c>) builds the model with
    /// <c>CollectionModelBuilder.BuildDynamic</c>, which reflects over nothing.
    /// </summary>
    protected EdgeVectorStoreCollection(IEdgeDatabase database, string name,
        MEVD.ProviderServices.CollectionModel model, EdgeVectorStoreCollectionOptions options);

    public override string Name { get; }
    public EdgeVectorSchema Schema { get; }

    public override Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates the data table, its indexes, the vec0 table, the FTS5 sidecar and all four
    /// triggers in one transaction. Asserts the SQLite floor and validates the generator's
    /// dimensions against the declared vector width first.</summary>
    public override Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default);

    public override Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default);

    public override Task<TRecord?> GetAsync(TKey key, MEVD.RecordRetrievalOptions? options = default,
        CancellationToken cancellationToken = default);
    public override IAsyncEnumerable<TRecord> GetAsync(IEnumerable<TKey> keys,
        MEVD.RecordRetrievalOptions? options = default, CancellationToken cancellationToken = default);
    public override IAsyncEnumerable<TRecord> GetAsync(Expression<Func<TRecord, bool>> filter, int top,
        MEVD.FilteredRecordRetrievalOptions<TRecord>? options = null, CancellationToken cancellationToken = default);

    public override Task DeleteAsync(TKey key, CancellationToken cancellationToken = default);
    public override Task DeleteAsync(IEnumerable<TKey> keys, CancellationToken cancellationToken = default);

    public override Task UpsertAsync(TRecord record, CancellationToken cancellationToken = default);
    /// <summary>Overridden, not inherited: one embedding call for the whole batch, one transaction.</summary>
    public override Task UpsertAsync(IEnumerable<TRecord> records, CancellationToken cancellationToken = default);

    /// <summary><paramref name="searchValue"/> may be <c>string</c> (embedded via the query generator),
    /// <c>ReadOnlyMemory&lt;float&gt;</c>, <c>float[]</c> or <c>Embedding&lt;float&gt;</c>.
    /// <c>VectorSearchResult.Score</c> is the vec0 DISTANCE: LOWER is better.</summary>
    public override IAsyncEnumerable<MEVD.VectorSearchResult<TRecord>> SearchAsync<TInput>(
        TInput searchValue, int top, MEVD.VectorSearchOptions<TRecord>? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>vec0 KNN fused with FTS5 bm25 by reciprocal rank fusion.
    /// <c>VectorSearchResult.Score</c> here is the RRF score: HIGHER is better — the OPPOSITE of
    /// <see cref="SearchAsync"/>. Pass <see cref="EdgeHybridSearchOptions{TRecord}"/> to tune rrf_k,
    /// the lane weights and the candidate count.</summary>
    public IAsyncEnumerable<MEVD.VectorSearchResult<TRecord>> HybridSearchAsync<TInput>(
        TInput searchValue, ICollection<string> keywords, int top,
        MEVD.HybridSearchOptions<TRecord>? options = default, CancellationToken cancellationToken = default)
        where TInput : notnull;

    /// <summary>Returns <c>VectorStoreCollectionMetadata</c>, the <see cref="IEdgeDatabase"/>,
    /// the <c>CollectionModel</c>, the <see cref="EdgeVectorSchema"/>, and <c>this</c>.</summary>
    public override object? GetService(Type serviceType, object? serviceKey = null);
}

/// <summary>
/// The trim/AOT-safe path, and a first-class one: the conformance suite runs against it too, and
/// <c>trim-smoke</c> (§17) publishes it. Its constructor carries NO trim annotations, because it
/// chains to the <c>CollectionModel</c>-taking base ctor rather than the reflecting one.
/// </summary>
public sealed class EdgeDynamicVectorStoreCollection
    : EdgeVectorStoreCollection<object, Dictionary<string, object?>>
{
    /// <summary><paramref name="options"/>.<c>Definition</c> must be non-null; the model is built
    /// with <c>CollectionModelBuilder.BuildDynamic</c> before the base ctor runs.</summary>
    public EdgeDynamicVectorStoreCollection(IEdgeDatabase database, string name,
        EdgeVectorStoreCollectionOptions options);
}

/// <summary>The DDL and the queries as plain strings, so a consumer can read, log or hand-execute
/// exactly what the provider runs — the same visible-SQL contract as SP1's helpers.</summary>
public sealed class EdgeVectorSchema
{
    public string CollectionName { get; }
    public string DataTable { get; }
    public string VectorTable { get; }
    public string? FullTextTable { get; }
    public string KeyColumn { get; }
    public string RowIdColumn { get; }          // "_rowid"
    public string VectorColumn { get; }
    public int Dimensions { get; }
    public IReadOnlyList<string> FullTextColumns { get; }

    public IReadOnlyList<string> BuildCreateSql();
    public IReadOnlyList<string> BuildDropSql();
    public string BuildKnnSql(bool hasFilter, bool hasScoreThreshold, bool includeVectors);
    public string BuildHybridRrfSql(bool hasFilter, bool includeVectors);
    public string BuildUpsertSql();
    /// <summary>Quotes each keyword and doubles embedded quotes, then joins. FTS5 operators inside a
    /// keyword are inert, never honoured.</summary>
    public static string BuildMatchExpression(ICollection<string> keywords, KeywordCombinator combinator,
        string? columnFilter = null);
}

public static class EdgeVectorDataBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="EdgeVectorStore"/> and <c>MEVD.VectorStore</c> over the named SP1
    /// database, plus the diagnostics contributor and the lifecycle observer.
    /// <para>
    /// <b>Embedding generator resolution, which is what makes §4.3 work.</b> The registered factory
    /// resolves <see cref="IEmbeddingGenerator"/> from the container with
    /// <c>GetService</c> (never <c>GetRequiredService</c>: a store used with pre-computed vectors
    /// needs none) and passes it to the <see cref="EdgeVectorStore"/> constructor, which uses it
    /// only when <see cref="EdgeVectorStoreOptions.EmbeddingGenerator"/> was left null. It then
    /// asks that generator for its query sibling with
    /// <c>generator.GetService(typeof(IEmbeddingGenerator), EdgeVectorData.QueryGeneratorServiceKey)</c>,
    /// falling back to the generator itself, and passes the result as the query generator. The key
    /// is this package's own constant, not <c>EdgeEmbeddings.QueryServiceKey</c>: §2 decision 2
    /// forbids a reference to <c>Qavren.Edge.Embeddings.Onnx</c>, so the literal is duplicated and
    /// §16.2 asserts the two copies are equal. <c>AddOnnxEmbeddings</c> registers BOTH the closed generic and
    /// the non-generic descriptor precisely so this resolution finds one; the order of the two
    /// builder calls does not matter, because the factory runs at resolve time.
    /// </para>
    /// </summary>
    public static EdgeBuilder AddVectorStore(this EdgeBuilder builder,
        Action<EdgeVectorStoreOptions>? configure = null);

    /// <summary>Keyed variant. Resolves the KEYED <see cref="IEmbeddingGenerator"/> under the same
    /// <paramref name="name"/> first, then the unkeyed one, so a named store pairs with a named
    /// generator by convention.</summary>
    public static EdgeBuilder AddVectorStore(this EdgeBuilder builder, string name,
        Action<EdgeVectorStoreOptions>? configure = null);

    /// <summary>RECOMMENDED. Emits the collection DDL as an SP1 <c>IEdgeMigration</c>, so the schema is
    /// versioned by the existing migrator at startup order 100 with <c>PRAGMA user_version</c>
    /// bookkeeping, instead of appearing on first use.</summary>
    [RequiresDynamicCode("…")] [RequiresUnreferencedCode("…")]
    public static EdgeBuilder AddVectorCollectionMigration<TKey, TRecord>(this EdgeBuilder builder,
        int version, string collectionName, string? databaseName = null,
        Action<EdgeVectorStoreCollectionOptions>? configure = null)
        where TKey : notnull where TRecord : class;

    public static EdgeBuilder AddVectorCollectionMigration(this EdgeBuilder builder,
        int version, string collectionName, MEVD.VectorStoreCollectionDefinition definition,
        string? databaseName = null, Action<EdgeVectorStoreCollectionOptions>? configure = null);

    /// <summary>Ad-hoc alternative: an <c>IEdgeStartupTask</c> at order 300 calling
    /// <c>EnsureCollectionExistsAsync</c>. Same SQL, no version bookkeeping.</summary>
    [RequiresDynamicCode("…")] [RequiresUnreferencedCode("…")]
    public static EdgeBuilder AddVectorCollection<TKey, TRecord>(this EdgeBuilder builder,
        string collectionName, Action<EdgeVectorStoreCollectionOptions>? configure = null,
        string? storeName = null)
        where TKey : notnull where TRecord : class;
}

/// <summary>
/// Derives from MEVD's <c>VectorStoreException</c>, not from <c>EdgeException</c> — deliberately.
/// The conformance suite and every MEVD consumer (Semantic Kernel, Agent Framework, generic retry
/// middleware) catch <c>VectorStoreException</c>, and an Edge-only hierarchy would be invisible to
/// them. It still carries the <c>EdgeErrorCode</c> and the docs HelpLink, so Qavren's error contract
/// holds on both sides of the line. Note .NET MEVD 10.x has no <c>VectorStoreOperationException</c>;
/// that type exists only in Semantic Kernel's Python SDK.
/// </summary>
public sealed class EdgeVectorStoreException : MEVD.VectorStoreException
{
    public EdgeVectorStoreException(EdgeErrorCode code, string message, Exception? innerException = null);
    public EdgeErrorCode Code { get; }
}

/// <summary>Raised while BUILDING the model or validating the schema, before any SQL runs.</summary>
public sealed class EdgeVectorModelException : EdgeException
{
    public EdgeVectorModelException(EdgeErrorCode code, string collectionName, string? propertyName,
        string message, Exception? innerException = null);
    public string CollectionName { get; }
    public string? PropertyName { get; }
}
```

## 9. ONNX session hosting and execution-provider policy

### 9.1 Hosting

`OnnxSessionHost` keeps a `ConcurrentDictionary<string, SessionSlot>`. A slot holds
the `InferenceSession`, the `OnnxSessionInfo`, a lease count, a `dropRequested`
flag and a `SemaphoreSlim(1)` load gate. The dictionary key is the model id plus,
when `OnnxSessionOptions.FreeDimensionOverrides` is non-empty, a canonical rendering
of those overrides — a pinned shape is baked into the session, so two different
pinned shapes are two different sessions, and the key has to say so rather than
handing the second caller a session pinned to the first one's shape. With the
default (empty) overrides the key is just the model id.

`AcquireAsync(modelId)`:

1. `await host.EnsureStartedAsync(ct)` — SP1's contract, so a startup failure
   surfaces here with its real cause.
2. Fast path: under the slot lock, if loaded and not marked for drop, increment
   the lease and return.
3. Slow path, under the load gate so N concurrent first-callers produce one
   session: `IOnnxModelStore.EnsureAsync` → memory pre-flight → build
   `SessionOptions` → apply the EP policy → `new InferenceSession(graphPath, sessionOptions)`
   → validate the signature → publish → lease.

`OnnxSessionLease.Dispose()` decrements; at zero with `dropRequested` set, the
session is disposed there. Disposal is always explicit. `InferenceSession` has a
finalizer, so a dropped-but-undisposed session pins the entire native graph until
GC — jetsam bait on iOS — while disposing one under an in-flight `Run` is a native
access violation. The lease is what makes both impossible.

**Always from a file path in the product, with exactly one stated exception.** A
`byte[]` overload holds the managed array and ORT's copy of the initializers
simultaneously during creation (≈180 MB transient for fp32 MiniLM), and it degrades
ORT's CoreML cache key from the model-URL hash to "hash of graph inputs and node
outputs", which collides across quantization variants of one architecture. This is
why `IOnnxModelSource` is contractually required to produce a real path even on
Android, where a MAUI asset is a pathless `AssetManager` stream. `IOnnxSessionHost`
exposes no byte-array entry point at all, so a consumer cannot take the bad path.

The exception is the tier-1 test fixture (§16.1), which is a **747-byte** graph held
as a base64 `const` and created with `new InferenceSession(byte[])` through an
`InternalsVisibleTo`-scoped factory hook. Both costs are measured against the size
of the model: a 2× transient on 747 bytes is 1.5 KB, and the weak cache key is
irrelevant because the fixture never runs under CoreML with caching enabled. The
hook is internal, never part of the public surface, and it is why §16.4's device
lanes assert nothing about the CoreML compile cache — a byte-array session cannot
prove anything about it.

**Memory pre-flight.** Before `new InferenceSession`, read
`IEdgeResourceMonitor.Read().AvailableMemoryBytes`. When it is non-null and below
`manifest.TotalSizeBytes * MemoryHeadroomFactor + MemoryHeadroomBytes`, throw
`EdgeOnnxException(OnnxInsufficientMemory)` carrying `RequiredBytes`,
`AvailableBytes`, the model id and a remediation naming the int8 preset. A null or
zero reading means "unknown" and skips the check — it is never used to refuse.
Apple publishes no jetsam table and the
`com.apple.developer.kernel.increased-memory-limit` entitlement moves the ceiling
per app, so there are no hard-coded device tiers.

**Signature validation is eager.** After creation, `InputMetadata`/`OutputMetadata`
are read. Every name the preset references must exist; every input the graph
declares must be one we can supply (ORT requires every declared input to be fed,
so an unrecognised one can never be satisfied); the output must exist and, when
its last dimension is static, equal the declared dimensions. A mismatch throws
`EdgeOnnxException(OnnxModelSignatureMismatch)` listing both sets of names. This
turns "somebody pointed a preset at a different export" from silently wrong
vectors into a message naming the file.

**`OrtEnv` is created by startup task order 200**, before anything can construct a
`SessionOptions`: ORT creates the environment implicitly on the first
`SessionOptions`, and `CreateInstanceWithOptions` then throws
`"OrtEnv singleton instance already exists"`. The task builds
`EnvironmentCreationOptions { logId, logLevel, loggingFunction }` where
`loggingFunction` is a `DOrtLoggingFunction` forwarding into `ILogger` under
`EdgeAiEventIds.OrtLog` (held in a static field so it is never collected), calls
`CreateInstanceWithOptions`, and — if `OrtEnv.IsCreated` was already true — logs a
warning, records `ortEnvironmentPreexisting` in diagnostics and continues. Another
library winning the race is not our failure to crash on. When
`OnnxOptions.DisableOrtDllImportResolver` is set, `OrtEnv.DisableDllImportResolver = true`
is the first statement of the task.

`ShareThreadPool` (default true once more than one model is registered) calls
`SessionOptions.DisablePerSessionThreads()`. With one embedding session it changes
nothing; the moment SP4's chat model lands beside it, it is one thread pool on a
six-core phone instead of two.

`SetLoadCancellationFlag(true)` is wired to **both** the `AcquireAsync`
cancellation token **and** `MemoryPressure(Critical)` arriving mid-load, so a
multi-second CoreML compile aborts cooperatively rather than running to completion
into a kill.

Deliberately unused: `IOBinding` (buys nothing for CPU/CoreML embeddings, adds
`OrtValue` lifetime hazards), `RunAsync` (caller-allocated outputs; the sync `Run`
on a worker thread honours cancellation between batches just as well),
`PrePackedWeightsContainer` (only pays off with several sessions over one model),
`CreateTensorValueFromSystemNumericsTensorObject` (`[Experimental("SYSLIB5001")]`,
which would leak to consumers), and `.ort` format models.

### 9.2 Execution providers

Everything goes through `AppendExecutionProvider(string providerName, Dictionary<string,string> providerOptions)`.
The typed helpers are never called: `AppendExecutionProvider_CoreML` calls a native
entry point Microsoft deprecated in ORT 1.20.0, and `AppendExecutionProvider_Nnapi`
is compiled inside `#if __ANDROID__` and throws `NotSupportedException` on every
other build. The string overload is the only EP API that is not TFM-gated, which is
why SP2's EP code is one implementation compiled for every TFM — and it is why the
provider set is exactly what that overload accepts. ORT documents it as supporting
"QNN", "SNPE", "XNNPACK", "CoreML" and "AZURE"; of those, CoreML and XNNPACK are the
two that are compiled into the shipped mobile natives and relevant to an embedding
encoder, so `EdgeExecutionProvider` is `{ Cpu, XnnPack, CoreMl }` and nothing else.
NNAPI's absence follows from this list, not from taste — see below.

| RID family | Order | Options |
|---|---|---|
| ios, iossimulator, maccatalyst | CoreML → CPU | `ModelFormat=MLProgram`, `MLComputeUnits=CPUAndNeuralEngine`, `ModelCacheDirectory=<OrtCache>/<modelId>/<sha16>`. **`RequireStaticInputShapes` is NOT set** unless the session also carries free-dimension overrides — see below |
| android | XNNPACK → CPU | `intra_op_num_threads=clamp(ProcessorCount/2, 1, 4)` **and** `SessionOptions.IntraOpNumThreads = 1` |
| win-x64, win-arm64, linux-*, osx-arm64 | CPU | `IntraOpNumThreads` from the policy |
| osx-x64 | — | Startup fault: ORT ships no `osx-x64` native (§15) |

`ModelFormat=MLProgram` is a requirement, not a preference: the NeuralNetwork
format's supported-op table lacks `LayerNormalization`, `Gelu` and `Erf`, so a
BERT encoder fragments into CPU-fallback partitions with a CPU↔ANE round trip per
block.

Two separate floors meet here and neither is "comfortable"; both are exactly met.
MLProgram needs Core ML 5, i.e. **iOS 15.0** — and ORT's own Apple natives are built
at `--apple_deploy_target=15.1`, which is the higher of the two and therefore the
one that decides. §4.1 sets `SupportedOSPlatformVersion` to `15.1` for both Apple
TFMs for that reason: it is the native's build floor, not a rounding of MLProgram's
15.0. Had SP2 kept SP1's 15.0 it would be declaring support for an OS version whose
devices cannot load the static library it links. The CoreML EP itself requires only
iOS 13, which is below both and never the binding constraint.

**`RequireStaticInputShapes` defaults to OFF, and turning it on without pinning the
graph's free dimensions would disable CoreML entirely.** CoreML partitions the graph
at session-creation time from the shapes the graph *declares*, not from the shapes
fed at Run time. All four presets declare `input_ids`, `attention_mask` and
`token_type_ids` as `[batch_size, sequence_length]` — both symbolic. With
`RequireStaticInputShapes=1` against symbolic dims, CoreML takes few or no nodes and
the graph silently runs on CPU; padding every batch to a bucket does not change
that, because bucketing happens after the partition decision. Worse, nothing would
notice: `ExecutionProviderAttempt.Accepted` only records that
`AppendExecutionProvider` did not throw.

So the flag is never set on its own. The session factory turns it on **only** when
`OnnxSessionOptions.FreeDimensionOverrides` is non-empty, and throws
`EdgeOnnxException(OnnxStaticShapesUnpinned)` if a caller sets it without them. The
override itself is `SessionOptions.AddFreeDimensionOverrideByName("sequence_length", N)`
plus `("batch_size", MaxBatchSize)`, which rewrites the declared dims before the
partition runs. `OnnxEmbeddingOptions.PinnedSequenceLength` is the one-property way
to ask for all of it. The consequences are real and are why it is opt-in: one pinned
shape is one session and one compiled CoreML model, so every batch — a batch of one
short string included — pays a full N-token run, and a second pinned shape means a
second session with its own memory budget and its own cache entry. The default path
keeps the dynamic declared shapes, lets CoreML partition normally, and uses
`SequenceBuckets` to keep the number of distinct runtime shapes small (which still
helps, because CoreML caches per shape it actually sees). §19 item 13 owns the
measurement that decides which is faster on a real device.

`ModelCacheDirectory` is the single highest-value setting in this package. Unset,
CoreML recompiles the captured subgraph on every session creation — ORT's docs say
"may cost significant time (even minutes)" — and leaks the artefact into the iOS
tmp directory, where it accumulates. We own the directory. ORT explicitly does not
track model changes or evict cache entries, so **the cache key is made
content-addressed by the path**: models live at `<Models>/<modelId>/<sha16>/` and
their cache at `<OrtCache>/<modelId>/<sha16>/`, where `sha16` is the first 16 hex
characters of the graph SHA-256. ORT's key preference order is a `CACHE_KEY`
metadata_props entry, then the hash of the model URL, then the graph-name hash;
creating from a path that already carries the content hash puts us on the second
rung with the properties of the first, and `IOnnxModelStore` deletes the whole
stale `<modelId>/<sha16>` pair when a model is re-provisioned at a new SHA. Writing
a `CACHE_KEY` into `metadata_props` would mean shipping an ONNX protobuf writer for
no additional guarantee, and is not done.

XNNPACK is the Android accelerator, and **NNAPI is not offered at all**. Two
independent reasons, and the second is decisive. First, Google deprecated NNAPI as of
Android 15 and says it expects most devices to fall back to the CPU backend; ORT
ships no successor Android EP in the .NET package, and `Microsoft.ML.OnnxRuntime.QNN`
is Windows-only at 1.24.4. Second and structurally: **NNAPI is not reachable through
the string overload.** ORT documents
`AppendExecutionProvider(string, Dictionary<string,string>)` as supporting "QNN",
"SNPE", "XNNPACK", "CoreML" and "AZURE" — NNAPI is not in that list. Appending it
requires `AppendExecutionProvider_Nnapi`, which is compiled inside `#if __ANDROID__`
and throws `NotSupportedException` on every other build. Shipping it would mean an
`#if ANDROID` call site, which would destroy the "one EP implementation compiled for
every TFM" property the paragraph above depends on — for an EP that Google says is
heading to CPU fallback anyway. So `EdgeExecutionProvider` has no `Nnapi` member,
there is no `NnapiProviderOptions`, and `"NNAPI"` is not a valid `Overrides` key.
ADR 0004 records the cut and the trigger for revisiting it: ORT exposing NNAPI
through the portable overload, or shipping a supported Android successor.

`SessionOptions.IntraOpNumThreads = 1` alongside XNNPACK is ORT's own explicit
anti-contention guidance; a caller who sets it otherwise while XNNPACK is active gets
a warning and an override. XNNPACK is compiled into the shipped AAR.

Application is fail-soft, one provider at a time: each append sits in its own
try/catch, a throw is recorded as `ExecutionProviderAttempt(provider, Accepted: false, …, Failure: message)`,
logged at Warning, and the loop continues. CPU is appended last whenever
`FallBackToCpu` is true. Only a provider named in `Required` turns a failure into
`EdgeOnnxException(OnnxExecutionProviderRequired)`.

**Honesty clause, stated in the XML docs and in the diagnostics payload.**
`Accepted` means `AppendExecutionProvider` returned without throwing. It does not
mean that provider partitioned the graph: ORT exposes no managed API for per-node
EP assignment. The only truthful signal is `CoreMl.ProfileComputePlan = true`,
which logs the hardware each operator was dispatched to — a diagnostics flag, off
by default, surfaced on the sample app's Diagnostics page.

`SetEpSelectionPolicy` / `GetEpDevices` / plugin EPs, QNN, WebGPU, CUDA and
DirectML are all out of scope for v1.

## 10. Model provisioning

Download at first use is the default; bundling is an explicit opt-in. The
asymmetry forces it: Android has `MauiAsset AssetPack="models" DeliveryType="FastFollow"`
(aab only) and iOS has no MAUI equivalent, so an asset-pack-first design still
needs the download path for half the fleet. One mechanism, several sources.

### 10.1 What this costs an app, before a single model is bundled

A consumer choosing `BundledOnnxModelSource` needs the numbers, and a consumer
choosing the default download path still pays for the ORT native. Both are stated
here, and §19 item 3 is the measurement that replaces the estimates with facts from
the device lanes' own artifacts.

**The ORT native is the fixed cost and it is not small.**

- **Android:** the AAR carries `jni/{armeabi-v7a,arm64-v8a,x86,x86_64}/libonnxruntime.so`
  plus `libonnxruntime4j_jni.so` and an unused `classes.jar`. Four ABIs ship
  whether or not an app wants them; `$(AndroidSupportedAbis)` trimmed to
  `arm64-v8a` (plus `armeabi-v7a` if the app still supports 32-bit) is the lever,
  and an AAB splits per-ABI at install time anyway, so the *download* an Android
  user sees is one ABI even when the bundle holds four. The README says to trim the
  APK path and to leave the AAB path alone.
- **iOS and Mac Catalyst:** the xcframework is a **static** library that ORT's own
  targets link with `ForceLoad=True`, so every byte of the selected slice lands in
  the app's `__TEXT` segment rather than in a separately-counted framework. That is
  the one ceiling with no workaround: **Apple caps the executable's total `__TEXT`
  sections at 80 MB**, and a force-loaded ORT slice consumes a real fraction of it
  before the app's own code. An app that already links other large static libraries
  should measure `size -m` on its binary before adopting SP2, and `--strip` /
  `MtouchLink` settings are the only levers. There is nothing SP2 can do about this
  from a library, so it is documented rather than mitigated.

**The ceilings a bundled model runs into.** SP2 states them once and does not try
to enforce them — they are the consumer's packaging constraints, not a library's:

| Platform | Limit | What it means here |
|---|---|---|
| Google Play, AAB base module | **500 MB** compressed download | `BgeSmallEnV15` at 133 MB fits with room; two bundled presets do not leave much |
| Google Play, legacy APK | **100 MB** | `BgeSmallEnV15` does **not** fit. Ship an AAB, or download |
| Google Play, any delivery | large-download notification above ~200 MB | a bundled 133 MB model plus the app pushes into the band where users are warned |
| iOS App Store | **4 GB** uncompressed app | never the binding constraint here |
| iOS, cellular install | user is asked above ~200 MB | the practical ceiling: a bundled fp32 preset makes an otherwise-small app cross it |
| iOS, executable `__TEXT` | **80 MB** | the ORT static slice counts against this; the model does not (it is a resource, not code) |

The guidance that follows from the table, and what the README says: **bundle
`MiniLmL6V2Int8` (23 MB) if you bundle anything; download everything larger.** The
int8 preset is the default for exactly this reason — it is the one artifact that is
simultaneously the arm64, avx512 and avx512-vnni build, so one 23 MB file covers
every RID, and it is small enough to bundle on a legacy APK without approaching any
of these numbers. `BgeSmallEnV15` at 133 MB is a download-only preset in practice;
`BundledOnnxModelSource` accepts it and the README says not to.

Neither Play asset packs nor iOS background `NSUrlSession` transfers are in v1
(§20), which is precisely why the size story has to be stated rather than deferred
to them: the two mechanisms an app would otherwise reach for to carry a large
bundled model are both out, so the honest answer is "download it, and keep the
bundled option for the 23 MB preset."

### 10.2 Where models live, and how they get there

**Where models live.** `IEdgePaths.Data` is backed up on all three platforms —
on Android that is `Context.FilesDir`, whose Auto Backup quota is **25 MB per app**,
so a 23 MB int8 model consumes essentially all of it and a fp32 one triggers
`onQuotaExceeded()` and stops backing up the user's actual SQLite database.
`IEdgePaths.Cache` can be purged mid-session, converting a purge into a
re-download. Hence `IEdgeModelPaths` (§6.2): `Context.NoBackupFilesDir` on Android
(auto-excluded, no manifest edit required by the consumer),
`Library/Application Support/qavren-edge` with `NSURLIsExcludedFromBackupKey` set
on the directory after creation on Apple, `<Data>/models` elsewhere. The absolute
path is rebuilt from the platform API on every access, never persisted: the iOS
sandbox path carries an app GUID that changes on every clean install — the same
reason SP1's `MauiEdgePaths` reads `FileSystem.Current` each time.

**Layout.** `<Models>/<modelId>/<sha16>/<relativePath>`, plus
`<modelId>/<sha16>/.qavren-model.json` — the provisioning marker carrying each
file's expected size and SHA-256, the resolved paths, the total bytes and the
timestamp.

**The download, precisely.**

1. `GET` with `HttpCompletionOption.ResponseHeadersRead`, and the body read under
   its own linked `CancellationTokenSource(BodyTimeout)`: `HttpClient.Timeout`
   stops applying once the headers are read.
2. Check free disk against the remaining bytes plus `FreeDiskMarginBytes`; short
   → `ModelInsufficientDiskSpace` rather than filling the device.
3. Record `ETag` and `Content-Length`; write to `<file>.part`.
4. On resume, send `Range: bytes=<len>-` plus `If-Range: <etag>`. **A `200`
   response to a ranged request means "restart": truncate the `.part` file.** Only
   a `206` may be appended to. A server that ignores `Range` returns the whole body
   with a `200`, and appending it silently corrupts the file.
5. `SHA256.HashDataAsync(stream, ct)` over the completed `.part` file **on disk**,
   never the in-flight buffer. Mismatch → delete the file, then
   `ModelHashMismatch` carrying both digests.
6. `File.Move(part, final, overwrite: true)` on the same volume, then re-assert the
   platform no-backup attribute — after the rename, never on the `.part` file.
7. Retry: `MaxAttempts` (4) with exponential backoff and jitter, only on 5xx /
   408 / 429 / `HttpRequestException` / `IOException`. No
   `Microsoft.Extensions.Http.Resilience` dependency: a transitive Polly in a
   mobile embedding package to replace forty lines of bounded retry is a bad trade
   for consumers.

URLs are `https://huggingface.co/{repo}/resolve/{revision}/{path}` with a full
commit SHA. Preset hashes are baked in as constants and regenerated by a
checked-in script, so the runtime never asks the Hub what the hash should be. When
one is fetched, it comes from the git-LFS `oid` (which **is** the SHA-256) via one
`POST /api/models/{repo}/paths-info/{rev}` — the anonymous API bucket is 500 per
five minutes against 3,000 for resolvers — and never from the `xetHash` field
those Xet-backed repos also return.

`EnsureAsync` is idempotent and concurrency-safe (one `SemaphoreSlim` per model
id). The fast path returns `AlreadyPresent` when the marker exists and every file's
length matches; full re-hashing on every launch is a startup tax paid for a case
the provisioning-time verify already covers.

Provisioning stays lazy by default. `ProvisionModelAtStartup` moves the *presence
check* to order 210 for apps that want a loud launch-time failure; it never
downloads, because blocking `IEdgeHost.Started` on a 23 MB download also blocks
every `IEdgeDatabase.OpenConnectionAsync`.

Background transfers are out of scope for v1: an iOS download that survives
backgrounding needs `NSUrlSessionConfiguration.CreateBackgroundSessionConfiguration`
plus `HandleEventsForBackgroundUrl`, which `HttpClient` does not map onto. v1 is
documented as foreground-only and fully resumable, with
`IProgress<ModelProvisioningProgress>` for a determinate bar.

`.onnx` is shipped, not `.ort`: the stock natives are full builds that load
`.onnx` directly, and `.ort` only pays off in a reduced-operator minimal build,
which decision 5 forbids.

## 11. Embedding pipeline

**Prefix.** When the preset declares prefixes and they apply, the generator
prepends its own. The unkeyed generator is the *document* generator (MEVD's upsert
path uses it); a second instance constructed with `EmbeddingInputKind.Query`
applies `QueryPrefix` and is reached through
`GetService(typeof(IEmbeddingGenerator<string, Embedding<float>>), EdgeEmbeddings.QueryServiceKey)`
or the `AsQueryGenerator()` extension — and, from `Qavren.Edge.VectorData`, which can
reference neither symbol, by the same key spelled `EdgeVectorData.QueryGeneratorServiceKey`
against `typeof(IEmbeddingGenerator)` (§12.5). That is MEAI's own service-key mechanism, so
no new protocol exists, and the vector store asks for it before embedding a search
string. This is the answer to a real hole: **MEVD calls
`generator.GenerateAsync(values, cancellationToken: ct)` with no options**, so
nothing about query-versus-document can travel through `EmbeddingGenerationOptions`.

**Tokenise.** `BertTokenizer.Create(vocabPath, new BertOptions { LowerCaseBeforeTokenization = preset.LowerCase, … })`,
held as the **concrete** `BertTokenizer`. Through a `Tokenizer`-typed field,
`EncodeToIds` binds the base method and silently omits `[CLS]`/`[SEP]`. Encoding
uses the truncating overload, which accounts for the two special tokens itself
(it delegates with `maxTokenCount - 2` and returns empty when `maxTokenCount < 2`),
so `maxTokens: 256` yields at most 256 ids **including** them.

Construction parses the whole vocab, so it happens exactly once — but **not at
startup, and not in a DI factory**. The vocab is `OnnxModelFileRole.Vocabulary`
inside the same manifest as the graph, and §10 keeps provisioning lazy, so on a
first launch there is no vocab path at the moment the container constructs the
generator, and `CreateWordPieceAsync` is async while DI factories are not. The
generator therefore takes `IEdgeTokenizerProvider`, not a built `IEdgeTokenizer`.
The first `GenerateAsync` awaits `provider.GetAsync(preset, ct)`, which awaits
`IOnnxModelStore.EnsureAsync` for the same manifest the session is about to load,
builds the tokenizer under a `SemaphoreSlim(1)`, and caches it for the process.
Concurrent first callers share one build. `WarmUpEmbeddingsAtStartup` (order 220,
off by default) is how an app moves that cost to launch deliberately;
Diagnostics read `IEdgeTokenizerProvider.Find(preset.Id)` — the tokenizer for the
reporting registration's own preset — so reporting a vocab size never forces a download
and a keyed multi-preset app does not report its neighbour's vocabulary. `Current` is the
most recently built tokenizer process-wide and is not registration-scoped; for the same
reason each `OnnxEmbeddingGenerator` latches its own tokenizer on its first embed and
reports that, rather than following `Current`.

**There is no attention-mask API in the library, and `GetSpecialTokensMask` is not
one.** A grep for "attention" across the whole of `Microsoft.ML.Tokenizers` returns
nothing. On its default `alreadyHasSpecialTokens: false` path,
`GetSpecialTokensMask` ignores the ids entirely and emits `1, 0…0, 1`; on the other
path it marks special tokens. Either way it is the inverse of what ONNX wants. The
mask is synthesised here: 1 for a real token, 0 for padding. `token_type_ids` is an
all-zero `long[]` allocated once per shape and reused, and is bound only when the
graph declares it.

**Batch shape.** Inputs are chunked into `MaxBatchSize` (16) groups. Within a
batch, sequence length is the batch maximum rounded up to the smallest entry of
`SequenceBuckets` (`[64, 128, 256, 512]`) that fits, capped at the preset's maximum.

Bucketing is about the number of **distinct runtime shapes**, and nothing else. It
does not make `RequireStaticInputShapes=1` viable: CoreML partitions from the
shapes the graph *declares*, which stay symbolic no matter what is fed at Run time
(§9.2). What bucketing does buy is that CoreML's per-shape compiled artefacts, and
ORT's own memory-pattern arena, see four shapes instead of hundreds — real, and
smaller than the claim it replaces. Pinning the declared dims is the separate,
opt-in mechanism: `OnnxEmbeddingOptions.PinnedSequenceLength` emits
`AddFreeDimensionOverrideByName` and only then turns `RequireStaticInputShapes` on,
at the cost of one session, one shape, and a full-length run for every batch.

**Tensors.** Three `OrtValue.CreateTensorValueFromMemory<long>(OrtMemoryInfo.DefaultInstance, memory, [batch, seq])`
over pooled `long[]` buffers from `ArrayPool<long>.Shared`.
`CreateTensorValueFromMemory` pins the managed memory for the `OrtValue`'s
lifetime, so every one is disposed in a `finally` before the arrays return to the
pool; a long-lived undisposed `OrtValue` pins a GC segment. **Inputs are bound by
name, never by position** — nomic's graph declares
`(input_ids, token_type_ids, attention_mask)` while MiniLM declares
`(input_ids, attention_mask, token_type_ids)`, and a positional binding would
produce embeddings that are plausible and wrong.

**Run.** `session.Run(runOptions, inputNames, inputValues, [OutputName])`,
synchronously, on a `Task.Run` worker, under `SemaphoreSlim(MaxConcurrency)`
(default 1). MEAI documents that all members must be safe for concurrent use;
`Microsoft.ML.Tokenizers` states no thread-safety guarantee, so the tokenizer is
guarded by the same semaphore until a stress test proves it reentrant.
Cancellation is honoured *between* batches; a single `Run` is not interruptible,
and the signature does not pretend otherwise.

**Pool.** The output is `last_hidden_state`, `float32`, `[batch, seq, dim]`. None
of the four presets exposes `sentence_embedding` or `pooler_output`, so pooling is
always ours. Mean is the masked mean, `sum(h[b,t,:] * mask[b,t]) / max(1, sum(mask[b]))`;
CLS copies row 0. The mode comes from the preset, never a default — bge-small is
CLS and everything else here is mean, and getting it wrong is a silent ~10-point
retrieval regression with no exception anywhere.

A graph whose declared output is already **rank-2** `[batch, dim]` — one that pooled
in its own head — is accepted as well: that row is taken verbatim and
`EmbeddingPreset.Pooling` is **not** applied to it. None of the four presets ships
such a graph, so the rank-3 path above is what this release runs; the rank-2 path
exists so that a consumer-supplied pre-pooled graph does not fault on a stride
computed for three ranks. It is declared on `EmbeddingPreset.Pooling`'s XML doc and
pinned by `GeneratorTests.AGraphThatPoolsInItsOwnDeclaredOutputIsUsedVerbatim`.

**Layer-norm, when the preset says so.** `EmbeddingPooler.LayerNorm(vector, eps)`
runs between pooling and L2 when `preset.PostPoolLayerNorm` is set — true for
`NomicEmbedTextV15Int8` and false for the other three. It is mean/variance
normalisation over the pooled vector with no learned weight or bias, matching
PyTorch `F.layer_norm(x, (dim,))`. This is **not** Matryoshka truncation (cut, §20):
nomic-embed-text-v1.5's `modules.json` carries only Transformer + Pooling, with no
Normalize module, and its documented pipeline is mean-pool → `F.layer_norm` over the
768 dim → (optional truncate) → L2. Skipping it produces vectors that look fine, sit
on the unit sphere, and do not match the model's reference implementation — the
silent-quality-bug class every `required` field on `EmbeddingPreset` exists to
prevent. The preset-catalogue table test (§16.1) asserts the flag per preset, and a
tier-3 golden-vector test against the reference implementation is what proves the
stage itself.

**Normalise.** L2 in place when `preset.Normalize` (true for all four). Values are
copied out of `GetTensorDataAsSpan<float>()` into a fresh `float[dim]` **before**
the output `OrtValue` is disposed; that span points at native memory the `OrtValue`
owns.

**Emit.** One `Embedding<float>` per input, in input order, each carrying
`ModelId` and `CreatedAt`. `GeneratedEmbeddings.Usage` carries the unpadded token
count, which is free and makes ingestion cost visible. The one-per-input contract
is enforced upstream too: MEAI's single-value `GenerateAsync` throws when
`Count != 1`, and `GenerateAndZipAsync` throws on a count mismatch (it
short-circuits an empty input sequence without calling the generator at all).

**Options handling.** `EmbeddingGenerationOptions.Dimensions` is accepted only when
it equals the preset's; anything else throws
`EdgeEmbeddingException(EmbeddingDimensionMismatch)` naming the fixed width.
Matryoshka truncation is not implemented, and could not be the source of truth for
storage width anyway, since MEVD never passes options. A mismatched `options.ModelId`
throws rather than being ignored. `AdditionalProperties` and
`RawRepresentationFactory` are ignored.

**Memory pressure.** The generator **reads** the level; it does not observe it.
`OnnxLifecycleObserver` (L0) latches the level SP1's hub reports into
`IEdgeResourceMonitor.LastPressure`, and the generator — which already takes
`IEdgeResourceMonitor` — consults it before each batch. That direction matters:
the batch size is `OnnxEmbeddingOptions.MaxBatchSize` in `Qavren.Edge.Embeddings.Onnx`
(L1) and the observer is in `Qavren.Edge.Onnx` (L0), which cannot see it, so the flag
has to live in L0 or §4.1's strictly-downward dependency rule breaks. No second
lifecycle observer is registered, and `Embeddings.Onnx` implements none.

With `ShrinkBatchUnderMemoryPressure` (default true), a latched `Moderate` halves the
effective batch size until `Resumed` clears the latch back to `null`. The latch is
`EdgeMemoryPressure?` rather than an enum with a `None` member precisely so that
"nothing reported yet" and `Low` are distinguishable (§6.4): a non-nullable latch
would read as `Low` — the enum's zero value — before any event had occurred.
`Critical` additionally marks
the session for drop through the observer's own call to `IOnnxSessionHost.DropAsync`;
the in-flight batch finishes on its lease and the next batch reloads, so a memory
warning never corrupts a result.

**Presets.** All four pin a repo, a commit SHA, per-file size and SHA-256, and an
SPDX licence. `MiniLmL6V2Int8` is the default at 23 MB / 384-d / mean / 256 tokens
/ Apache-2.0; `BgeSmallEnV15` is the quality/desktop choice at 133 MB with **CLS**
pooling and a query-only instruction prefix; `NomicEmbedTextV15Int8` is opt-in at
768-d with mandatory task prefixes (drive it with two keyed generators and
`QueryEmbeddingGenerator`). `EdgeTokenizerKind.UnigramTokenizerJson` exists in the
enum and throws `TokenizerKindUnsupported` with a message naming the
Tokenizers 3.x requirement, so the multilingual shape is ready when the library is.

## 12. Vector store mapping

### 12.1 Schema

Three tables per collection, all joined on one INTEGER rowid. For a collection
`notes` with key `string Key`, data `string? Tag` (`IsIndexed`), `string Title` and
`string Body` (both `IsFullTextIndexed`), and a 384-d cosine vector:

```sql
-- data table: the collection name, verbatim. Never WITHOUT ROWID.
CREATE TABLE IF NOT EXISTS "notes" (
  "_rowid" INTEGER PRIMARY KEY,          -- rowid alias; the join key for all three tables
  "Key"    TEXT NOT NULL,
  "Tag"    TEXT,
  "Title"  TEXT,
  "Body"   TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS "notes_Key_index" ON "notes"("Key");
CREATE INDEX IF NOT EXISTS "notes_Tag_index" ON "notes"("Tag");     -- one per IsIndexed property

-- vector table: emitted by SP1's VecTable.BuildCreateSql, unchanged
CREATE VIRTUAL TABLE IF NOT EXISTS "notes_vec" USING vec0(
  embedding float[384] distance_metric=cosine, chunk_size=256
);

-- full-text sidecar: emitted by SP1's FtsTable with the new options overload
CREATE VIRTUAL TABLE IF NOT EXISTS "notes_fts" USING fts5(
  "Title", "Body", content='notes', content_rowid='_rowid',
  tokenize='unicode61 remove_diacritics 2'
);
-- FtsTable.BuildSyncTriggerSql("notes_fts", "notes", ["Title","Body"], contentRowId: "_rowid")
--   emits notes_fts_ai / _ad / _au.

-- vec0 has no foreign keys, so the cascade is a trigger, not provider code:
CREATE TRIGGER IF NOT EXISTS "notes_vec_ad" AFTER DELETE ON "notes" BEGIN
  DELETE FROM "notes_vec" WHERE rowid = old."_rowid"; END;
```

The vec0 table is keyed on its **implicit** rowid — there is no declared `rowid`
column in the DDL — and rows are written with `INSERT INTO "notes_vec"(rowid, embedding)`.
`INTEGER PRIMARY KEY` on the data table is a true rowid alias, stable across
`ON CONFLICT DO UPDATE`, so `content_rowid='_rowid'` is the same integer FTS5
already indexes on.

**`"_rowid"` is a reserved storage name.** It is emitted into the same namespace as
every user property's storage name, so a record with a property called `_rowid` — or
with `[VectorStoreData(StorageName = "_rowid")]` — would otherwise produce a
duplicate-column `CREATE TABLE` at runtime, which is exactly the failure class
§15.2 promises to catch "before any SQL runs". Two guards, both at model build:
`CollectionModelBuildingOptions.ReservedKeyStorageName = "_rowid"`, which is the hook
MEVD provides for a provider that owns a fixed column name; and an explicit
ordinal-ignore-case check in `ValidateProperty` over key, data and vector properties,
because MEVD's option reserves the name against the *key* property and SP2 needs it
reserved against all of them. Either raises
`EdgeVectorModelException(ReservedColumnName)` naming the property and the reserved
name. The name is not configurable: it appears in `content_rowid='_rowid'`, in four
trigger bodies and in every query in §13, and a configurable one would buy nothing but
a second thing to get wrong.

**Why not duplicate the user key into vec0.** Upstream's connector copies the
(often TEXT) key into the vec0 table and joins on it, which forces vec0's TEXT
primary-key emulation and — decisively — puts the filter constraint on an emulated
text column rather than on `rowid`, which is the only form `vec0BestIndex`
consumes with `omit = 1`. The whole pre-filter story in §13 depends on this choice.

**No vec0 metadata, auxiliary or partition columns are emitted.** Metadata columns
cap at 16, accept only AND-ed comparisons and `IN` (booleans `=`/`!=` only), spill
TEXT over 12 characters to a side table, and cannot express `OR`; an auxiliary
column touched in a KNN `WHERE` fails the entire query; a partition key cannot be
UPDATEd and over-shards a phone-sized corpus. The rowid pre-filter is strictly more
expressive than all three. SP1's `VecTable` keeps its support for them; SP2 simply
does not use it.

### 12.2 Model mapping

`EdgeCollectionModelBuilder : CollectionModelBuilder` with
`CollectionModelBuildingOptions { SupportsMultipleVectors = false, RequiresAtLeastOneVector = true, ReservedKeyStorageName = "_rowid" }`,
implementing `IsDataPropertyTypeValid` and `IsVectorPropertyTypeValid` and
overriding `ValidateKeyProperty`, `ValidateProperty` (the `_rowid` reservation above),
`SupportsKeyAutoGeneration` and **`EmbeddingGenerationDispatchers`**. That last one is
not optional bookkeeping: `CollectionModelBuilder` exposes
`protected virtual IReadOnlyList<EmbeddingGenerationDispatcher> EmbeddingGenerationDispatchers { get; }`,
and it is the seam by which a provider declares which `Embedding` subtypes it can
store. SP2 returns exactly one — `EmbeddingGenerationDispatcher.Create<Embedding<float>>()`
— which is what makes §4.3's `string` source property resolve to
`Embedding<float>` (through `VectorPropertyModel.ResolveEmbeddingType<TEmbedding>`
pattern-matching the configured generator as `IEmbeddingGenerator<string, TEmbedding>`
or `IEmbeddingGenerator<DataContent, TEmbedding>`), and what makes a generator that
produces `Embedding<sbyte>` or `BinaryEmbedding` fail at model-build time with a
named error rather than at the vec0 insert. The single-entry list is the mechanical
expression of "float32 only" below: vec0's `int8` and `bit` element types are cut
from v1 (§20), and this is the one place the cut is enforced rather than merely
documented. Every type in
`Microsoft.Extensions.VectorData.ProviderServices` is `[Experimental("MEVD9001")]`,
so the csproj carries `<NoWarn>$(NoWarn);MEVD9001</NoWarn>` and ADR 0003 records the
churn exposure.

| MEVD / CLR | SQLite |
|---|---|
| `int`, `long`, `short`, `bool` | `INTEGER` |
| `float`, `double` | `REAL` |
| `string`, `Guid` | `TEXT` (Guid upper-cased, matching Microsoft.Data.Sqlite) |
| `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly` | `TEXT`, ISO 8601 |
| `byte[]` | `BLOB` |
| anything else | `EdgeVectorModelException(UnsupportedPropertyType)` naming the property and the supported set |

Keys: `int`, `long`, `string`, `Guid` — enforced in the collection constructor for
`TKey` and again in `ValidateKeyProperty`. Auto-generated keys: `Guid` via
`Guid.CreateVersion7()` client-side (time-ordered, so TEXT keys cluster);
`int`/`long` left to SQLite and read back with `RETURNING`.

Vector property CLR types: `ReadOnlyMemory<float>`, `ReadOnlyMemory<float>?`,
`Embedding<float>`, `float[]`, and `string` with a generator configured. float32
only. A **nullable** vector property is rejected at model-build time: vec0 overloads
SQL `NULL` on a vector column to mean "no change", so `UPDATE … SET embedding = NULL`
is a silent no-op rather than an error. Storage is the raw little-endian float32
blob through SP1's `VecBlob.From`/`ToFloats` — exactly vec0's wire format, and zero
new blob code in SP2.

Distance functions: `CosineDistance` (default) → `cosine`, `EuclideanDistance` →
`l2`, `ManhattanDistance` → `l1`. Everything else — `CosineSimilarity`, both dot
products, `HammingDistance`, `EuclideanSquaredDistance` — throws
`EdgeVectorModelException(UnsupportedDistanceFunction)` naming the three that work.
`IndexKind` is accepted and **ignored with a logged warning**: vec0 is brute-force
and flat, and silently accepting `Hnsw` while scanning linearly is worse than
saying so. Declared dimensions above 8192 are rejected up front by SP1's
`VecTable` (`SQLITE_VEC_VEC0_MAX_DIMENSIONS`).

`SupportsMultipleVectors = false` is deliberate: a second vector property means a
second vec0 table and a materially worse hybrid query, and it throws
`MultipleVectorPropertiesUnsupported` rather than silently picking one.

### 12.3 Writes

```sql
INSERT INTO "notes"("Key","Tag","Title","Body") VALUES ($Key,$Tag,$Title,$Body)
  ON CONFLICT("Key") DO UPDATE SET "Tag"=excluded."Tag","Title"=excluded."Title","Body"=excluded."Body"
  RETURNING "_rowid";
DELETE FROM "notes_vec" WHERE rowid = $rowid;
INSERT INTO "notes_vec"(rowid, embedding) VALUES ($rowid, $vector);
```

Delete-then-insert rather than `UPDATE`, for two reasons both true of sqlite-vec
0.1.9: a working `INSERT OR REPLACE` only arrives in 0.1.10-alpha, and the
NULL-means-no-change overload above. FTS5 is trigger-maintained off the data
table, so it never appears in the write path. Everything runs inside one
`IEdgeDatabase.ExecuteInTransactionAsync`; the batch overload generates every
embedding in **one** `GenerateAsync` call before opening the transaction, which is
why MEVD leaves batch `UpsertAsync` abstract with no default.

Delete is `DELETE FROM "notes" WHERE "Key" = $key`; the four triggers clean vec0
and FTS5, so no orphan survives even when an app deletes rows with its own SQL
against the same `IEdgeDatabase`. vec0 reclaims space only when a whole chunk
empties (~256 rows at our default), so a heavily churned collection grows; v1 ships
no `CompactAsync` and the README documents drop-and-rebuild as the remedy.

### 12.4 Schema creation

`EnsureCollectionExistsAsync` runs every statement above in one transaction, and
before any of it:

1. Reads `sqlite_version()` once and throws `EdgeVectorModelException(SqliteVersionTooOld)`
   below 3.39 — `FULL OUTER JOIN` needs 3.39, `rowid IN (…)` push-down needs 3.38,
   `RETURNING` needs 3.35. SP1's native pins 3.53.4, so this only fires for a
   consumer who pointed `Qavren.Edge.Sqlite` at a system SQLite, and it fires with
   a sentence instead of a raw syntax error from the hybrid query.
2. Validates the configured generator's dimensions against the declared vector
   width and throws `VectorDimensionMismatch` naming both. The generator's width
   is read from `EmbeddingGeneratorMetadata.DefaultModelDimensions` via
   `GetService`; a third-party generator may publish none (and Semantic Kernel's
   own adapter never propagates it), in which case the check **defers to the first
   upsert**, where the actual vector length is compared instead. The README
   promises the deferred check, not a universal startup-time catch.

`AddVectorCollectionMigration<TKey,TRecord>(version, name)` emits the same
statements as an SP1 `IEdgeMigration`, so the DDL runs under the existing migrator
at order 100 with `PRAGMA user_version` bookkeeping and SP1's failure semantics.
That is the documented path; `AddVectorCollection` (startup order 300) and bare
`EnsureCollectionExistsAsync` remain for ad-hoc use. `EdgeVectorSchema.BuildCreateSql`
is the single source for all three.

### 12.5 How the store gets its embedding generator

This is the wiring that makes §4.3's four calls work, and it is worth being explicit
about because the shape (`IEmbeddingGenerator` on an options object) does not by
itself say who fills it in.

`Note.Embedding` in §4.3 is a `string` source property, so the store must embed it on
upsert, and `EdgeVectorStoreOptions.EmbeddingGenerator` is null by default. The
resolution happens in `AddVectorStore`'s registered factory, at resolve time:

1. `sp.GetService<IEmbeddingGenerator>()` — `GetService`, never
   `GetRequiredService`: a store used only with pre-computed `ReadOnlyMemory<float>`
   vectors needs no generator, and demanding one would break that. For the keyed
   overload, the keyed generator under the same name is tried first, then the
   unkeyed one.
2. The result is passed to `EdgeVectorStore`'s `embeddingGenerator` parameter, which
   is applied only where `EdgeVectorStoreOptions.EmbeddingGenerator` is still null.
   An explicitly-set option always wins.
3. The query generator is resolved **from the generator itself, by service key, with
   no reference to `Qavren.Edge.Embeddings.Onnx`**:

   ```csharp
   var query = generator.GetService(typeof(IEmbeddingGenerator),
                                    EdgeVectorData.QueryGeneratorServiceKey)
               as IEmbeddingGenerator
               ?? generator;
   ```

   That is what makes an asymmetric-prefix model (nomic, e5) correct through MEVD,
   which always calls `GenerateAsync` with no options (§11). Three details are
   deliberate and none is optional:

   - **`EdgeVectorData.QueryGeneratorServiceKey` is declared in
     `Qavren.Edge.VectorData`,** as a `public const string` holding the same literal
     `"qavren.edge.query"` as `EdgeEmbeddings.QueryServiceKey`. §2 decision 2 forbids
     this package from referencing `Embeddings.Onnx`, so the symbol is not visible
     here and the store cannot call `EdgeEmbeddings.AsQueryGenerator()` either. A
     duplicated literal is the price of the layering; §16.2 asserts the two constants
     are equal from a test project that references both packages, which is the only
     place all three assemblies are visible at once, so they cannot drift silently.
   - **`GetService`, not the extension method.** `AsQueryGenerator` is a convenience
     for direct consumers of the ONNX package and is not on this path.
   - **`typeof(IEmbeddingGenerator)`, the non-generic type.** The store holds
     generators as `IEmbeddingGenerator` throughout (§2 decision 2), and a
     third-party generator may be typed `IEmbeddingGenerator<DataContent, Embedding<float>>`
     — asking for a closed generic it does not implement would return null and quietly
     lose the query lane. Asking for the non-generic interface works for every
     generator, and the `?? generator` fallback covers every generator that exposes
     no sibling at all, which is all of them except SP2's own.
4. If, at the first upsert or search of a `string` source property, both are still
   null, the store throws `EdgeVectorStoreException(EmbeddingGeneratorMissing)`
   naming the property and pointing at `AddOnnxEmbeddings`.

`AddOnnxEmbeddings` registers **both** the closed generic
`IEmbeddingGenerator<string, Embedding<float>>` and the non-generic
`IEmbeddingGenerator` descriptor, because it delegates to
`services.AddEmbeddingGenerator` rather than hand-registering; step 1 resolves the
non-generic one, and MEVD's own property model pattern-matches the closed generic.
Builder-call order does not matter — the factory runs when the store is resolved,
not when it is registered — so `.AddVectorStore()` before `.AddOnnxEmbeddings()`
works identically. §16.2's end-to-end test asserts exactly the §4.3 sequence,
resolved from a real container, precisely so this cannot regress into a
`EmbeddingGeneratorMissing` on the happy path.

### 12.6 Dynamic records

`EdgeDynamicVectorStoreCollection` maps `Dictionary<string, object?>` with
`TKey = object`, requires a non-null `Definition`, and is built through
`CollectionModelBuilder.BuildDynamic` (the reflection-based `Build` throws for
`Dictionary<string, object?>` by design). It is the only trim/AOT-safe path —
`VectorStore.GetCollection<TKey,TRecord>` carries `[RequiresDynamicCode]` and
`[RequiresUnreferencedCode]` upstream — so it is first-class: the full conformance
suite runs against it, not a subset, and §17's `trim-smoke` job publishes it.

That claim only holds because of the constructor arrangement in §8. A derived
constructor chaining to an annotated base constructor inherits IL2026 and IL3050,
and this repo sets `TreatWarningsAsErrors` true, so a naive
`EdgeDynamicVectorStoreCollection : EdgeVectorStoreCollection<object, Dictionary<string, object?>>`
would not compile at all without a suppression — and a suppression would make the
AOT claim unverifiable, which is precisely what `trim-smoke` exists to decide.
`EdgeVectorStoreCollection<TKey,TRecord>` therefore declares a second, `protected`,
**unannotated** constructor taking an already-built `CollectionModel`, and the
dynamic collection chains to that one. `EdgeVectorStore.GetDynamicCollection` builds
the model with `CollectionModelBuilder.BuildDynamic`, which reflects over nothing.

## 13. Hybrid search and reciprocal rank fusion

### 13.1 Filters

`vec0BestIndex` assigns an `argvIndex` and sets `omit = 1` for a `rowid IN (…)`
constraint via `sqlite3_vtab_in`, and `vec0Filter_knn_chunks_iter` AND-s that
bitmap into the chunk validity bitmap before any distance is computed. So:

> Translate the whole MEVD `Filter` expression to SQL over the **data** table and
> push it in as `v.rowid IN (SELECT "_rowid" FROM "notes" WHERE <sql>)`.

`EdgeFilterTranslator` derives from MEVD's `FilterTranslatorBase` with
`FilterPreprocessingOptions { SupportsParameterization = true }` and supports
`== != < <= > >=`, `&& || !`, `is null` / `is not null`, member access bound to
model properties, `Contains` over an inline array or a captured `IEnumerable`
(→ `IN (@p1, @p2, …)`), `string.StartsWith`/`EndsWith`/`Contains` (→ `LIKE` with
escaped `%` and `_`), and `Convert` unwrapping. Captured values become parameters;
constants are inlined with SQLite literal formatting. Anything else — an unmodelled
method call, `Enumerable.Any`, a property mapping to no column — throws
`NotSupportedException` naming the node and the property, which is what the
conformance suite expects.

It never degrades to a client-side post-filter. With a genuine pre-filter
available, degrading would change *which* k rows come back, not merely how many.

### 13.2 Vector search

```sql
SELECT d."_rowid", d."Key", d."Tag", d."Title", d."Body", v.distance
       [, v."embedding"]                                   -- only when IncludeVectors
FROM "notes_vec" v
JOIN "notes" d ON d."_rowid" = v.rowid
WHERE v."embedding" MATCH $query AND v.k = $k
  [AND v.rowid IN (SELECT "_rowid" FROM "notes" WHERE <filter>)]
  [AND v.distance <= $scoreThreshold]
ORDER BY v.distance;
```

`$k = top + Skip`, capped at 4096 (`SQLITE_VEC_VEC0_K_MAX`); over the cap throws
`KnnLimitExceeded` naming the limit before SQLite sees it. `LIMIT` is never used in
place of `k =`, following SP1's `Knn.BuildSql`. **`Skip` is applied client-side** by
discarding the first N rows while reading, because `SQLITE_INDEX_CONSTRAINT_OFFSET`
is skipped in every loop of `vec0BestIndex` and never given an `argvIndex`; that is
documented on `VectorSearchOptions.Skip` and never silently dropped.
`ScoreThreshold` maps to `v.distance <= $t` — sqlite-vec produces distances, lower
is more similar — and `VectorSearchResult.Score` carries that distance verbatim.

### 13.3 Hybrid

No shipping MEVD provider implements `IKeywordHybridSearchable` over SQLite;
upstream's own connector carries the comment "Once HybridSearch supports get
implemented by SqliteCollection…". The shape below comes from sqlite-vec's only
real reference artefact, `examples/nbc-headlines/3_search.ipynb` cell 12 at tag
v0.1.9 — the documentation page everyone cites, `site/guides/hybrid-search.md`, is
a zero-byte stub, so the notebook is what the spec cites.

```sql
WITH vec AS (
  SELECT v.rowid AS id,
         ROW_NUMBER() OVER (ORDER BY v.distance) AS rank,
         v.distance AS distance
  FROM "notes_vec" v
  WHERE v."embedding" MATCH $query AND v.k = $cand
    [AND v.rowid IN (SELECT "_rowid" FROM "notes" WHERE <filter>)]
),
fts AS (
  SELECT f.rowid AS id,
         ROW_NUMBER() OVER (ORDER BY f.rank) AS rank,
         f.rank AS bm25
  FROM "notes_fts" f
  WHERE f."notes_fts" MATCH $keywords
    [AND f.rowid IN (SELECT "_rowid" FROM "notes" WHERE <filter>)]
  ORDER BY f.rank
  LIMIT $cand
)
SELECT d."_rowid", d."Key", d."Tag", d."Title", d."Body",
       COALESCE(1.0 / ($rrfK + fts.rank), 0.0) * $wKeyword
     + COALESCE(1.0 / ($rrfK + vec.rank), 0.0) * $wVector AS score,
       vec.distance, fts.bm25
       [, nv."embedding"]                                 -- only when IncludeVectors
FROM fts
FULL OUTER JOIN vec ON vec.id = fts.id
JOIN "notes" d ON d."_rowid" = COALESCE(fts.id, vec.id)
[LEFT JOIN "notes_vec" nv ON nv.rowid = d."_rowid"]        -- only when IncludeVectors
ORDER BY score DESC
LIMIT $top OFFSET $skip;
```

Note `f."notes_fts" MATCH $keywords` — the alias **qualifying** the table-named hidden
column, not the bare alias. FTS5 gives every table a hidden column named after the
TABLE, and `<x> MATCH <expr>` is an ordinary comparison against a column — so inside
a CTE that says `FROM "notes_fts" f`, the bare alias `f MATCH ...` resolves as a
column reference and SQLite answers `no such column: f`. The spelling that parses is
the alias qualifying the table-named hidden column, `f."notes_fts" MATCH $keywords`.
Reproduced twice independently against SQLite 3.50.4 and 3.53.4 + FTS5 on 2026-09-11.
`BuildHybridRrfSql` emits exactly that, and §16.1's golden-SQL test asserts this query
byte for byte, so the text above is the normative expected value rather than an
illustration.

Note that the `IncludeVectors` bracket appears **twice** and both halves are
required: the `LEFT JOIN` makes `nv` available and the `, nv."embedding"` projects
it. A join with no matching projection is the failure mode this pairing exists to
prevent — it costs a join and returns no vectors, and because `VectorSearchResult`
simply leaves the vector property at its default, nothing throws. §16.1's golden-SQL
test covers `IncludeVectors` on and off for the hybrid query as well as the vector
one, and §16.2 asserts the round-tripped vector is non-empty and equal to what was
upserted, so a half-applied bracket fails a behavioural test and not only a string
comparison.

Defaults: `$rrfK = 60`, `$wVector = $wKeyword = 1.0`,
`$cand = min((top + skip) * 4, 4096)`. All four are store-level defaults and
per-query overridable through `EdgeHybridSearchOptions<TRecord>`.

Three polarity traps, each handled explicitly:

1. **FTS5's `bm25()` is the standard score multiplied by −1**, so better matches
   are numerically lower and `ORDER BY rank` is ascending-best-first. RRF therefore
   ranks by `ROW_NUMBER() OVER (ORDER BY f.rank)` ascending, never by raw score
   magnitude. The hidden `rank` column is used rather than calling `bm25(f)`
   directly, which SQLite's own docs say is faster.
2. **vec0's `distance` is a distance**, so the vector lane's `ROW_NUMBER()` is also
   ascending.
3. **The fused score is a similarity — higher is better — the opposite polarity to
   `SearchAsync`.** MEVD has no way to declare direction. It is documented on the
   member, surfaced in diagnostics, and asserted in a test whose *name* states the
   inversion. Consistently, `HybridSearchOptions.ScoreThreshold` is applied to the
   RRF score as a client-side `>=` after the query, while vector-search
   `ScoreThreshold` is pushed down as `distance <=`; both facts sit on the XML docs.

**Keyword escaping is ours and is an injection surface.** Each keyword becomes a
double-quoted FTS5 string literal with internal `"` doubled, joined with ` OR `
(or ` AND `). Quoting makes FTS5 treat the token as a phrase, so `AND`, `OR`,
`NOT`, `NEAR`, `*`, `^`, `:` and parentheses inside a keyword are inert rather than
operators. With several full-text columns, `HybridSearchOptions.AdditionalProperty`
(resolved through `CollectionModel.GetFullTextDataPropertyOrSingle`) narrows the
expression with FTS5's column filter, `{Body} : ("term one" OR "term two")`. An
empty or all-whitespace keyword collection short-circuits the FTS lane and
degenerates to plain KNN rather than emitting a malformed `MATCH`.

With no `IsFullTextIndexed` property at all, the FTS5 table is not created and
`HybridSearchAsync` throws `EdgeVectorModelException(FullTextPropertyMissing)`
naming the attribute to add; `AlwaysCreateFullTextIndex` is the opt-in for creating
it anyway.

**Index maintenance is a lifecycle hook, not an inline cost.**
`INSERT INTO fts(fts) VALUES('optimize')` merges every b-tree into one and SQLite's
docs warn it "can take a long time to run" — precisely wrong to do inline on a
phone. The incremental form runs on `Sleeping` instead (§14).

## 14. Lifecycle and diagnostics

### 14.1 Startup

| Order | Task | Package |
|---|---|---|
| 0 | `NativeProviderInstall` | Sqlite.Native (SP1) |
| 10 | `DatabaseOpen` | Sqlite (SP1) |
| 100 | `Migrations` — **including collection DDL** via `AddVectorCollectionMigration` | Sqlite (SP1) |
| 200 | `OnnxEnvironment` — `OrtEnv.CreateInstanceWithOptions` + the `ILogger` bridge | Onnx |
| 210 | `ModelProvisioning` — presence verification (opt-in) | Onnx |
| 220 | `WarmUpSessionAtStartup` — creates the session, runs no inference (opt-in) | Onnx |
| 220 | `WarmUpEmbeddingsAtStartup` — builds the tokenizer, then one dummy 1×64 batch (opt-in) | Embeddings.Onnx |
| 300 | `VectorSchema` — `EnsureCollectionExistsAsync` (opt-in, ad-hoc alternative to 100) | VectorData |
| 1000 | `ConsumerDefault` | — |

Two tasks share order 220 because they are two halves of the same idea split along
the layer boundary. `Qavren.Edge.Onnx` can create a session — it owns
`IOnnxSessionHost` — but it cannot run a batch, because a batch needs
`IEdgeTokenizerProvider` and `OnnxEmbeddingGenerator`, both of which are L1 and
invisible to L0. So the L0 extension warms the session only, the L1 extension warms
the session *and* the tokenizer with one dummy embed, and an app calls whichever it
wants. `EdgeAiStartupOrder.SessionWarmUp` is the shared constant, published from L0
because that is the lowest package both share; publishing an `int` is not a
dependency. Both are off by default: either one forces provisioning, and §10 keeps
that lazy.

Every SP2 entry point begins with `await host.EnsureStartedAsync(ct)`, matching
`IEdgeDatabase`, so a provisioning failure surfaces on the first `GenerateAsync`
with its real cause rather than as a `FileNotFoundException` from inside ORT.

### 14.2 Lifecycle observers

`OnnxLifecycleObserver` registers with `TryAddEnumerable`, exactly as SP1's SQLite
observer does. `VectorDataLifecycleObserver` is **inserted at the head of the observer
list, idempotently** — not appended — because the `Sleeping` ordering below requires the
FTS merge to run before SP1's `SqliteLifecycleObserver`, and `AddSqlite` has already
registered that one by the time `AddVectorStore` runs; `TryAddEnumerable` appends, so it
cannot express that requirement. Both derive from SP1's no-op `EdgeLifecycleObserver`.

`OnnxLifecycleObserver`

- `MemoryPressure(Critical)` → `IOnnxSessionHost.DropAsync()` and
  `SetLoadCancellationFlag(true)` on any load in flight. Idle sessions die
  immediately; leased ones are marked and die as their leases return. The observer
  waits at most two seconds for the drain and returns regardless: the hub awaits
  observers, and an iOS memory warning is not a place to block.
- `MemoryPressure(Moderate)` → latches the level into
  `IEdgeResourceMonitor.SetPressure(Moderate)` and does nothing else. It does **not**
  touch a batch size: that lives in `OnnxEmbeddingOptions` in L1, which this observer
  in L0 cannot see. The generator reads `IEdgeResourceMonitor.LastPressure` before
  each batch and halves its own effective size (§11). Keeping the signal in L0 and the
  reaction in L1 is what keeps §4.1's dependency direction true, and it is why SP2
  registers exactly two lifecycle observers rather than three —
  `Qavren.Edge.Embeddings.Onnx` implements none. Dropping a 23 MB session that is
  about to be needed again would be a worse trade than shrinking a batch, so nothing
  is dropped at this level.
- `Resumed` → `SetPressure(null)`, which clears the latch the generator reads. `null`
  rather than a `None` member because SP1's merged `EdgeMemoryPressure` has no such
  member and adding one would renumber the existing three (§6.4). Does not pre-warm.
- `Stopping` → drops every session. `OrtEnv` is **not** disposed: it is a
  process-wide singleton with a one-shot options hook, and tearing it down would
  silently break a second Edge host in the same process.
- `Sleeping` → nothing. The CoreML cache is already on disk.

`VectorDataLifecycleObserver`

- `Sleeping` → per collection with an FTS table, one
  `INSERT INTO "notes_fts"("notes_fts", rank) VALUES('merge', 500)`, bounded to
  four iterations and a two-second budget, each inside its own try/catch,
  abandoned rather than failed when the budget expires. SP1's `SqliteLifecycleObserver`
  already checkpoints WAL on the same event; registration order puts the merge
  first so its pages land in that checkpoint.
- Everything else → nothing. Pool clearing is SP1's job and it already does it.

### 14.3 Diagnostics

Three `IEdgeDiagnosticsContributor` implementations. None of their component names
contains "Native", so SP1's `EdgeDiagnostics.Report()` keeps picking the SQLite
native block for `EdgeDiagnosticsReport.Native`.

`"Qavren.Edge.Onnx"` — `ortVersion`, `ortManagedAsset` (the `lib/` folder actually
resolved), `runtimeIdentifier`, `unsupportedRuntime`, `ortEnvironmentPreexisting`,
`sharedThreadPool`, `modelsDirectory`, `modelsDirectoryExcludedFromBackup`,
`ortCacheDirectory`, `ortCacheBytes`, `provisionedModels`; per session
`session[<id>].{graphPath, sha256, executionProviderAccepted, executionProviderAttempts,
inputNames, outputNames, loadMs, loadCount, leases, loaded}`; and the live snapshot
`availableMemoryBytes`, `isLowMemory`, `thermalState`, `thermalHeadroom`,
`isLowPowerMode`, `lastMemoryPressure` (the latch §14.2 sets and §11 reads).

`"Qavren.Edge.Embeddings.Onnx"` — the bare literal for **every** registration, keyed or
not; a keyed one is told apart by the `serviceKey` detail (`null` for the unkeyed one)
rather than by a decorated component name, so a consumer matching on the name keeps
working when an app goes keyed. Details: `serviceKey`, `preset`, `presetLicense`, `modelFile`,
`modelSha256`, `dimensions`, `pooling`, `postPoolLayerNorm`, `normalize`, `maxSequenceLength`,
`sequenceBuckets`, `queryPrefix`, `documentPrefix`, `truncation`, `defaultInputKind`,
`tokenizerKind`, `tokenizerFile`, `vocabSize` (from `IEdgeTokenizerProvider.Find(preset.Id)`
— **this registration's** preset, NOT `.Current`, which in a keyed multi-preset app is
whichever tokenizer was built last and belongs to some other generator; reporting it still
never forces provisioning, and it is `null` until the first embed or a warm-up),
`pinnedSequenceLength`, `maxBatchSize`, `effectiveBatchSize`, `maxConcurrency`, plus rolling counters `embeddingsGenerated`, `batchesRun`,
`tokensEncoded`, `truncatedInputs`, `runMsP50`, `runMsP95`. Every one of those is emitted on
every path: a generator that has never run reports real zeros for the four counts and `null`
for the two percentiles, never an abbreviated block.

`"Qavren.Edge.VectorData"` — `database`, `sqliteVersion`, `vecVersion` (note
`vec_version()` returns `"v0.1.9"` **with a leading v**, as SP1 already documents);
per collection `{dataTable, vecTable, ftsTable, dimensions, distanceFunction,
chunkSize, ftsTokenizer, keyType, generator, generatorDimensions, rrfK, weights}`
and, when `IncludeRowCountsInDiagnostics` is set, `rowCount` / `vecRowCount` /
`ftsRowCount`. The first two diverging means an upsert transaction was interrupted
and is the first thing to look at in a bug report.

All three render on the sample app's existing Diagnostics page with no page
changes: `EdgeDiagnosticsReport` walks contributors generically.

### 14.4 Log event ids

`EdgeEventIds` is a non-partial static class in SP1, so SP2 publishes its own
`EdgeAiEventIds`, continuing the numbering:

| Range | Meaning |
|---|---|
| 600–619 | `OrtEnvironmentCreated` 600, `OrtEnvironmentPreexisting` 601, `OrtLog` 602 |
| 620–639 | `SessionLoaded` 620, `SessionLoadFailed` 621, `SessionDropped` 622, `ExecutionProviderSkipped` 623, `ExecutionProviderAccepted` 624 |
| 640–659 | `ModelProvisioned` 640, `ModelDownloadStarted` 641, `ModelDownloadResumed` 642, `ModelDownloadRestarted` 643, `ModelHashMismatch` 644, `ModelDownloadFailed` 645, `OrtCachePurged` 646 |
| 700–719 | `EmbeddingBatchCompleted` 700, `EmbeddingInputTruncated` 701, `EmbeddingWarmUpCompleted` 702, `EmbeddingBatchShrunk` 703 |
| 800–819 | `CollectionCreated` 800, `CollectionDropped` 801, `HybridSearchExecuted` 802, `FtsMergeCompleted` 803, `IndexKindIgnored` 804 |

All through `LoggerMessage.Define`: this repo's `TreatWarningsAsErrors` plus
`latest-recommended` makes CA1848 an error, as SP1's `EdgeHost` already discovered.

## 15. Error handling

### 15.1 New `EdgeErrorCode` values

SP1's 1001–4001 are untouched. SP2 adds 5000–5299:

```
// Qavren.Edge.Onnx — runtime and sessions
OnnxEnvironmentAlreadyCreated  = 5001   // logged, never thrown
OnnxSessionCreationFailed      = 5002
OnnxModelSignatureMismatch     = 5003
OnnxExecutionProviderRequired  = 5004
OnnxInsufficientMemory         = 5005
OnnxUnsupportedRuntime         = 5006   // osx-x64 and any RID with no ORT native
OnnxStaticShapesUnpinned       = 5007   // RequireStaticInputShapes without FreeDimensionOverrides

// Qavren.Edge.Onnx — model provisioning
ModelNotRegistered             = 5051
ModelNotProvisioned            = 5052
ModelDownloadFailed            = 5053
ModelHashMismatch              = 5054
ModelAssetMissing              = 5055
ModelInsufficientDiskSpace     = 5056

// Qavren.Edge.Embeddings.Onnx
TokenizerAssetMissing          = 5101
TokenizerKindUnsupported       = 5102
EmbeddingDimensionMismatch     = 5103
EmbeddingPresetNotFound        = 5104
EmbeddingInputTooLong          = 5105

// Qavren.Edge.VectorData
VectorCollectionNotFound            = 5201
UnsupportedKeyType                  = 5202
UnsupportedPropertyType             = 5203
UnsupportedDistanceFunction         = 5204
VectorDimensionMismatch             = 5205
FullTextPropertyMissing             = 5206
EmbeddingGeneratorMissing           = 5207
MultipleVectorPropertiesUnsupported = 5208
NullableVectorProperty              = 5209
KnnLimitExceeded                    = 5210
SqliteVersionTooOld                 = 5211
VectorStoreOperationFailed          = 5212
ReservedColumnName                  = 5213   // a property claims the "_rowid" storage name
```

Every one inherits `EdgeException`'s `HelpLink` convention
(`…/foundation/docs/errors.md#<code>`) — including `EdgeVectorStoreException`,
which sets it explicitly because it derives from MEVD's base rather than
`EdgeException`.

### 15.2 Exception types

| Type | Base | Carries | Raised from |
|---|---|---|---|
| `EdgeOnnxException` | `EdgeException` | model id, RID, EP attempts, required/available bytes, remediation | session host, EP policy, startup 200 |
| `EdgeModelProvisioningException` | `EdgeException` | model id, relative path, expected/actual sha + bytes, source URI | model sources, startup 210 |
| `EdgeEmbeddingException` | `EdgeException` | preset id, expected/actual | generator construction, first batch |
| `EdgeVectorModelException` | `EdgeException` | collection, property | model build and schema validation, **before any SQL runs** |
| `EdgeVectorStoreException` | **`VectorStoreException`** | `EdgeErrorCode`, plus MEVD's `VectorStoreSystemName` / `VectorStoreName` / `CollectionName` / `OperationName` | every store operation |
| `NotSupportedException` | — | the expression node and property, or the fixed dimension | filter translation, `options.Dimensions` |

`EdgeVectorStoreException` deriving from MEVD's base rather than `EdgeException` is
the one deliberate break in the suite's hierarchy. The conformance suite asserts
the MEVD type, and Semantic Kernel, Agent Framework and generic retry middleware
catch it; an Edge-only hierarchy would be invisible to all of them. It still
carries the code and the HelpLink, so Qavren's contract holds on both sides.
`VectorStoreSystemName` is `"sqlite"` (the OTel `db.system.name` value, matching
upstream so a trace looks the same either side of a migration);
`OperationName` comes from a fixed vocabulary: `CreateCollection`,
`DeleteCollection`, `Get`, `Upsert`, `Delete`, `VectorSearch`, `HybridSearch`,
`ListCollectionNames`.

### 15.3 Failure catalogue

| Situation | Behaviour |
|---|---|
| ORT env already created by another library | Warning (601), recorded in diagnostics, startup continues |
| CoreML / XNNPACK append throws | Warning (623), recorded in `Attempts`, fall through to CPU. Never fatal unless listed in `Required` |
| Host RID is `osx-x64` | `EdgeOnnxException(OnnxUnsupportedRuntime)` at startup 200, naming the RID and that ORT ships no Intel-macOS native |
| Available memory below the budget | `EdgeOnnxException(OnnxInsufficientMemory)` with both byte counts and a remediation naming the int8 preset — before `new InferenceSession` |
| Even CPU failed to append / native missing | `EdgeOnnxException(OnnxSessionCreationFailed)` with RID, path and every attempt, in SP1's `EdgeNativeException` shape |
| Graph declares an input we cannot supply | `EdgeOnnxException(OnnxModelSignatureMismatch)` listing both name sets |
| `RequireStaticInputShapes` set with no `FreeDimensionOverrides` | `EdgeOnnxException(OnnxStaticShapesUnpinned)` before the session is created — the combination would silently move the whole graph to CPU and `Accepted` could not detect it (§9.2) |
| Server ignores `Range` (200, not 206) | Truncate the `.part` file, restart, log 643 |
| SHA-256 mismatch | Delete the file, one retry, then `ModelHashMismatch` with both digests |
| Free disk short of the remaining bytes + margin | `ModelInsufficientDiskSpace` before the download starts |
| `MemoryPressure(Critical)` during a `Run` | Session marked; the in-flight `Run` completes; the last lease disposes it |
| `MemoryPressure(Critical)` during a load | `SetLoadCancellationFlag(true)`; the load throws, the acquire faults, the next acquire retries |
| Generator dims ≠ declared vector dims | `VectorDimensionMismatch` at collection create, or at first upsert when the generator publishes no metadata |
| `top + Skip > 4096` | `KnnLimitExceeded` naming `SQLITE_VEC_VEC0_K_MAX`, before SQLite sees it |
| `Skip` on a vector search | Honoured client-side over `k = top + skip`; documented, never silently dropped |
| `IndexKind = Hnsw` | Warning (804), ignored |
| Hybrid search with no full-text property | `FullTextPropertyMissing` naming `[VectorStoreData(IsFullTextIndexed = true)]` |
| Nullable vector property | `NullableVectorProperty` at model build — the NULL-means-no-change trap is made unreachable, not reported |
| A property whose storage name is `_rowid` | `ReservedColumnName` at model build, naming the property — not a duplicate-column SQL error at first use (§12.1) |
| A `string` source property with no generator resolvable | `EmbeddingGeneratorMissing` naming the property and `AddOnnxEmbeddings`, after DI resolution has been tried (§12.5) |
| Multilingual preset requested | `TokenizerKindUnsupported` naming the Tokenizers 3.x requirement and linking ADR 0006 |
| Unsupported filter construct | `NotSupportedException` naming the node. Never a silent post-filter |
| Any `SqliteException` in a store operation | Wrapped in `EdgeVectorStoreException` with all four MEVD fields populated |

Lifecycle observers never propagate: SP1's hub isolates each one, and both SP2
observers additionally bound their own work with a timeout and abandon rather than
fail.

## 16. Testing

Four tiers. Tier 1 downloads nothing, ships no binary into git, and runs on every
device lane.

### 16.1 Tier 1 — unit, no network, no binary in git

The ONNX fixture is a **747-byte** masked-mean-pool graph
(`Gather → Cast → Unsqueeze → Mul → ReduceSum/ReduceSum → Div`) emitting
`embedding[batch, dims]`. It is generated **once** by a checked-in
`embeddings/tests/fixtures/make_tiny_model.py`, base64-encoded, and pasted into the
test source as a `const string` consumed via
`new InferenceSession(Convert.FromBase64String(TinyModel.Base64))`. ~1,000
characters: reviewable in a PR diff, no `.gitattributes` rule, no LFS question, no
binary asset to go stale.

That byte-array constructor is the single stated exception to §9.1's file-path rule,
and it is scoped rather than assumed. It is reached through an
`InternalsVisibleTo`-scoped factory hook on `OnnxSessionHost`, never through
`IOnnxSessionHost`, so no consumer can take it. Both of §9.1's objections are
measured against model size and vanish here: a 2× transient on 747 bytes is 1.5 KB,
and the weak CoreML cache key is moot because the fixture never runs with CoreML
caching on. Its one real consequence is recorded in §16.4: a byte-array session
cannot demonstrate anything about the compile cache, so the device lanes assert
nothing about it. The script is documentation and a regeneration path, and
CI never runs it. The environment is verified: `uv venv --python 3.14` with
`onnx 1.22.0` (a `cp312-abi3` wheel) and `onnxruntime 1.30.0` (a real `cp314`
wheel).

Two pins in the script are non-negotiable. `model.ir_version = 10` and opset 17
are set **explicitly**: onnx 1.22's default IR is 13, which is exactly ORT 1.30.0's
ceiling, so the first onnx release that raises `IR_VERSION` silently breaks every
fixture that trusted the default. `model.producer_name = ""` keeps the bytes stable
across onnx upgrades. Two per-operator traps the script encodes: `ReduceSum` takes
axes as a second **input** from opset 13, while `ReduceMean` takes axes as an
**attribute** through opset 17 and as an input from 18 — both forms appear in one
graph at opset 17.

The embedding table is hand-computable: row *t* = `[t, t+0.5, t+0.25, t+0.75]`. With
`input_ids=[[3,1,9]]` and `attention_mask=[[1,1,0]]` the output is exactly
`[[2.0, 2.5, 2.25, 2.75]]` — the mean of rows 3 and 1 with row 9 masked out. One
assertion covers the pooler, the mask plumbing and the tensor layout.

Four more variants, **all dim-4 like the base fixture and all under 1 KB**: a
CLS-pool one; one that **declares `token_type_ids` and never consumes it** (ORT
still demands it in the feed, which is what proves the generator feeds every
declared input); one whose inputs are declared in nomic's order (which proves
name-binding); and one serialised at IR 14, to assert the error path produces the
message a user hits with somebody else's model.

**There is no 768-dim fixture, and there cannot be a small one.** The fixture's
output width is the width of the `Gather` embedding-table initializer, so the graph
size is dominated by `vocab × dim × 4` bytes. Measured for exactly this graph shape:
vocab 4 × dim 4 = 277 bytes, vocab 16 × dim 4 = 471 bytes, vocab 8 × dim 384 =
12,505 bytes, vocab 32 × dim 384 = 49,372 bytes. The 747-byte base fixture is a
dim-4 build; the smallest conceivable 768-wide float32 table (two rows) is already
6,144 bytes of initializer before any graph overhead, which is six times the stated
budget and roughly 8,200 base64 characters pasted into a `.cs` file. Dimension width
is not what any tier-1 assertion actually exercises — the pooler, the mask, the
name-binding and the error paths are all width-agnostic — so the width that matters
is covered where it is free: §16.2 runs the real 384-d and 768-d shapes through the
store against the deterministic fake generator, and tier 3 runs a real 384-d model.
The vocab fixture is a ~64-entry `vocab.txt`, likewise a base64 const.

Tier 1 covers: EP policy resolution per RID; fail-soft fallback (append a bogus
provider name → `Accepted == Cpu` plus the recorded failure); a `Required` provider
failing; lease ref-counting and drop-while-leased; `MemoryPressure(Critical)`
dropping then reloading; the memory pre-flight against a stubbed monitor (including
the 0-means-unknown rule); signature mismatch; the download protocol against a
loopback `HttpMessageHandler` (resume-on-206, **restart-on-200**, hash mismatch,
disk-space refusal, idempotent re-provision, eviction purging the ORT cache);
tokenizer `[CLS]`/`[SEP]` presence asserted by id, truncation inclusive of
specials, padding to the correct bucket, mask correctness, `IndexByTokenCount` vs
`CountTokens`; mean/CLS/L2 against a hand-computed reference including the
all-padding divide-by-zero guard; batch order across a ragged batch; the empty-input
short-circuit; concurrent `GenerateAsync` from eight threads; `GetService` returning
metadata, info, preset and the query sibling; MEAI DI registering **both**
descriptors; golden SQL for `BuildCreateSql`/`BuildKnnSql`/`BuildHybridRrfSql`/`BuildUpsertSql`
with and without filter, threshold and `IncludeVectors`; every supported and every
rejected filter node with its message; keyword escaping (`["a\"b", "OR", "x*"]` →
`"a""b" OR "OR" OR "x*"`); a preset-catalogue table test guarding against a
copy-paste regression in dimensions, pooling, **`PostPoolLayerNorm`**, prefixes or
licence (with an explicit assertion that `NomicEmbedTextV15Int8` is the only preset
setting the flag); `EmbeddingPooler.LayerNorm` against a hand-computed
mean/variance reference including the epsilon path, plus an ordering test proving
pool → layer-norm → L2 and not pool → L2 → layer-norm; a model-build test that a
record with a `_rowid` property, and one with
`[VectorStoreData(StorageName = "_rowid")]`, both raise `ReservedColumnName` naming
the property rather than reaching SQL; and a session-factory test that
`RequireStaticInputShapes` with empty `FreeDimensionOverrides` raises
`OnnxStaticShapesUnpinned`, while `PinnedSequenceLength` populates both the override
dictionary and the flag.

### 16.2 Tier 2 — integration against the real natives

`ci.yml` already downloads the `native-*` artifacts into
`foundation/native/artifacts` on all three host OSes, so vec0 and FTS5 are present
at zero marginal cost. The embedding generator here is a deterministic 20-line fake
(`IEmbeddingGenerator<string, Embedding<float>>` producing a hash-seeded unit
vector), which keeps every store assertion exact.

- **`Microsoft.Extensions.VectorData.ConformanceTests` 10.10.0 is the gate**, run
  against both `EdgeVectorStoreCollection<TKey,TRecord>` and
  `EdgeDynamicVectorStoreCollection`, with an explicitly enumerated and commented
  skip-list for the v1 cuts. The skip-list is the honest record of the gap.
- KNN equals brute-force cosine on 1,000 random 384-d vectors for k ∈ {1, 10, 100}
  — the same shape SP1 already uses — and the same assertion repeated at **768-d**,
  which is where the wide-vector path is exercised. Tier 1 has no 768-d fixture and
  cannot cheaply have one (§16.1); the fake generator makes the width free here.
- `IncludeVectors` round-trips a **non-empty** vector equal to what was upserted, on
  both `SearchAsync` and `HybridSearchAsync`. The hybrid query needs two coordinated
  fragments for this — the `LEFT JOIN` and the `, nv."embedding"` projection (§13.3)
  — and only one of them is caught by a golden-SQL string comparison if the other is
  written and the expected value is updated to match. This behavioural assertion is
  what makes a half-applied bracket fail.
- **The two query-generator service-key constants are equal.**
  `Assert.Equal(EdgeEmbeddings.QueryServiceKey, EdgeVectorData.QueryGeneratorServiceKey)`.
  The literal is duplicated because §2 decision 2 forbids `Qavren.Edge.VectorData`
  from referencing `Qavren.Edge.Embeddings.Onnx` (§12.5), and
  `Qavren.Edge.VectorData.Tests` — which already references both, because the §4.3
  happy-path test below calls `AddOnnxEmbeddings` — is the only place in the suite
  where both symbols are visible at once. One line, and it is the entire defence
  against the two drifting apart.
- Pre-filter correctness: `k = 10` with a filter matching 12 of 500 rows returns the
  10 nearest **matching** rows, not 10 nearest then filtered to 3. Includes an `OR`
  filter, which no vec0 metadata column could express.
- RRF ordering against a hand-computed fusion table over a corpus where the two
  lanes disagree, plus a test asserting `RrfK` and the weights actually move it.
  This is the only way to catch the bm25 sign trap.
- Integrity: after 200 upserts and 50 deletes — including deletes issued as raw SQL
  against the same `IEdgeDatabase` — `COUNT(*)` on all three tables agrees.
- `Skip` semantics, `ScoreThreshold` polarity, `k > 4096` rejection, hybrid score
  direction (in a test whose name states the inversion), empty-keyword degradation,
  FTS trigger sync on insert/update/delete.
- `ListCollectionNamesAsync` in **both** directions, because the exclusion is
  structural and a name-prefix implementation would pass only one: a collection whose
  `VectorTableName`/`FullTextTableName` are set to names with no `_vec`/`_fts` suffix
  must still be hidden, and an ordinary user table literally called `foo_vec` must
  still be listed. Plus `sqlite_%` and the five FTS5 shadow tables.
- End-to-end with the tiny fixture generator wired into a real `EdgeVectorStore`,
  proving MEVD resolves `IEmbeddingGenerator<string, Embedding<float>>` and that a
  `string` source property is embedded on upsert.
- **The §4.3 happy path, resolved from a real `ServiceCollection`**: the exact four
  builder calls, then `GetCollection<string, Note>("notes")`, upsert, hybrid search.
  It asserts the store received the DI-registered generator and its query sibling
  (§12.5) rather than throwing `EmbeddingGeneratorMissing`, and it runs twice with the
  two builder calls in each order, because the factory resolves at resolve time and
  order must not matter.
- **Cipher:** one test opens a SQLCipher database through
  `Qavren.Edge.Sqlite.Native.Cipher` and runs the full collection lifecycle —
  create, upsert, KNN, hybrid, delete — over a keyed connection. It lives in
  `Qavren.Edge.Sqlite.Cipher.Tests`, which SP1 already keeps out of the device
  lanes. This is the thing the upstream connector structurally cannot do, and it is
  the proof that SP2's provider rides on `IEdgeDatabase` rather than a connection
  string.

### 16.3 Tier 3 — real model, opt-in, nightly

`[Fact(Skip = "QAVREN_EDGE_MODEL_DIR not set", SkipUnless = nameof(ModelAvailable))]`.
**`SkipUnless` is evaluated at runtime**, after the test class constructor,
`BeforeAfterTestAttribute`s and disposal have all run — so the session is built
inside the test body, never in the constructor, or a "skipped" test still loads a
23 MB model. The model is the int8 MiniLM pinned to HF revision
`1110a243fdf4706b3f48f1d95db1a4f5529b4d41`, fetched over plain HTTPS, SHA-256
verified, and cached with `actions/cache@v6.1.0` keyed on **that sha256** — not a
URL, not a date. Reference vectors are generated once from a known-good run and
pinned as a small JSON; assertions use a cosine tolerance of 1e-3, because ORT CPU
bit-determinism across `windows-2025` / `ubuntu-24.04` / `macos-15` and across
x64/arm64 is not established and exact equality would be a flake generator. This
lane runs on a nightly schedule, never on a PR.

### 16.4 Tier 4 — device lanes

No new lane. `Qavren.Edge.Onnx.Tests`, `Qavren.Edge.Embeddings.Tests` and
`Qavren.Edge.VectorData.Tests` copy `Qavren.Edge.Sqlite.Tests`'s shape exactly:
`net10.0` is an MTP application (the `xunit.v3` metapackage, `OutputType=Exe`); the
four platform TFMs are plain class libraries referencing only
`xunit.v3.extensibility.core` and `xunit.v3.assert` with
`IsTestingPlatformApplication=false` and the same `NoWarn` set, and the same per-OS
`TargetFrameworks` guards.

**One deviation from that project, and it is mandatory:
`SupportedOSPlatformVersion` follows §4.1's raised floors — `android 24.0`,
`ios 15.1`, `maccatalyst 15.1` — not `Qavren.Edge.Sqlite.Tests`'s `21.0`/`15.0`.**
These test projects reference `Qavren.Edge.Onnx` and therefore link ORT's AAR and
xcframework, so they inherit exactly the constraints the product packages do: an
AAR declaring `minSdkVersion 24` merged into a project declaring 21 is a
manifest-merge failure, and a 15.1-built static slice linked into a 15.0 deployment
target is a link-time warning and a runtime risk. The floors have to match or the
device lane is testing a configuration no consumer can ship.
`Qavren.Edge.DeviceTests` itself is SP1's project and is **not** edited beyond
three added `ProjectReference`s — which means SP1's own app-level floors govern the
merged manifest, and §19 item 2 is the build that proves the combination resolves.
If it does not, the documented response is a one-line `SupportedOSPlatformVersion`
bump in `Qavren.Edge.DeviceTests` — a test-host change, not a change to any shipped
SP1 package, and the one place §5's "two edits and no others" would gain a third
that is not a library edit at all.

Note the asymmetry with §4.1: the three **device-hosted test** projects carry all
five TFMs
(`net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst;net10.0-windows10.0.19041.0`)
while `Qavren.Edge.Onnx` and `Qavren.Edge.Embeddings.Onnx` carry four. That is not a
mismatch. `Qavren.Edge.DeviceTests` targets `net10.0-windows10.0.19041.0` and the
Windows device lane runs it, so the test libraries must offer that TFM to be
referenced from it; and a `net10.0-windows` test assembly referencing a `net10.0`
product assembly is ordinary TFM compatibility, the same relationship
`Qavren.Edge.Sqlite.Tests` already has with `Qavren.Edge.Sqlite`.
`Qavren.Edge.VectorData` is `net10.0` only and is referenced from all five.

`Qavren.Edge.VectorData.Conformance.Tests` is the exception to all of the above and
is **`net10.0` alone**: it is host-only, never referenced from
`Qavren.Edge.DeviceTests`, and never runs on a device lane. The MEVD conformance
suite needs a file-system database and a full xunit host, it asserts nothing that is
platform-specific, and giving it four extra TFMs would add four build legs to prove
nothing. §2's decision-6 row says the same; this is the authoritative spelling —
three test projects at five TFMs, one at one. xunit stays pinned at **3.2.2**, matching DeviceRunners
preview.12 — `SkipUnless`/`SkipWhen` have been on `FactAttribute` since xunit v3
1.0, so nothing needs 4.0.0.

Device lanes run the tier-1 assertions and the vec0/FTS5 half of tier 2, with **no
model download**. What they prove and nothing else can: ORT's native actually
loaded (real Android `.so`, real iOS xcframework, the Mac Catalyst RID-graph
resolution), `IEdgeModelPaths` lands in the real no-backup directory and the
exclusion flag reads back set on iOS, `IEdgeResourceMonitor` returns a plausible
non-null `AvailableMemoryBytes` and a `LastPressure` that a simulated pressure event
moves (and that reads `null` before any event, which is the whole reason it is
nullable — §6.4), and a simulated `MemoryPressure(Critical)` drops the session and
the next acquire reloads it.

Two things the lanes prove by **building at all**, and which therefore need no
assertion beyond a green lane. First, the platform floors: the Android lane packages
an app whose manifest is merged with ORT's `minSdkVersion 24` AAR, and the Apple
lanes link a 15.1-built static xcframework into a 15.1 deployment target, so a
regression in either floor is a build failure rather than a silent runtime one
(§4.1, §19 item 2). Second, the size numbers: each lane already produces an app
package, so §19 item 3's measurement is `ls -l` on an artifact these lanes create
anyway — the ORT native's real contribution to an `.apk`/`.aab` and to an iOS
binary's `__TEXT`, recorded in the vault and then in the README, rather than
estimated in §10.1.

**No CoreML compile-cache assertion.** An earlier draft had the lanes assert that a
second session load is materially faster than the first. It cannot: the device lanes
create sessions from the 747-byte base64 fixture through the byte-array hook
(§16.1), and a byte-array session has neither a meaningful compile cost nor the
strong `ModelCacheDirectory` key, so the measurement would be noise dressed as
evidence. The cache is exercised by tier 3 on a real model and reported through
`ortCacheBytes` in diagnostics; the device lanes assert the directory exists and is
excluded from backup, and nothing about its timing.

**Each lane records the execution provider; none asserts a specific value.** Two
reasons. CoreML behaviour under the iOS Simulator is asserted everywhere and
documented nowhere by Apple or Microsoft, and ORT's own iOS build page is stale
enough (it still claims x86_64-only simulators) to be useless either way. And, from
§4.1: `device-tests-ios` and `device-tests-maccatalyst` both run on
`macos-15-intel` with `-r iossimulator-x64` and `-r maccatalyst-x64`, so there is no
Apple Neural Engine anywhere in those lanes and an EP assertion would be asserting
Rosetta behaviour. The lane logs `ExecutionProviderReport` and publishes it; the
specific-EP claim waits for real hardware.

## 17. CI changes

New projects go into `QavrenEdge.slnx` under `/embeddings/src/` and
`/embeddings/tests/` folders; new package versions into `Directory.Packages.props`
(`Microsoft.ML.OnnxRuntime` 1.30.0, `Microsoft.ML.Tokenizers` 2.0.0,
`Microsoft.Extensions.AI` and `.AI.Abstractions` 10.10.0,
`Microsoft.Extensions.VectorData.Abstractions` 10.10.0,
`Microsoft.Extensions.VectorData.ConformanceTests` 10.10.0). Central transitive
pinning stays off, per SP1 adjustment 20.

`ci.yml`'s `test` job gains four steps, following its explicit-enumeration
convention with `-p:TargetFrameworks=net10.0` on every one (SP1's documented
reason: `-f` alone does not stop restore walking the full `TargetFrameworks` list
of the project *and everything it references*, so the workload check fires on hosts
without the mobile workloads):

```yaml
      - name: Onnx tests
        run: dotnet run --project embeddings/tests/Qavren.Edge.Onnx.Tests/Qavren.Edge.Onnx.Tests.csproj -c Release -f net10.0 -p:TargetFrameworks=net10.0

      - name: Embeddings tests
        run: dotnet run --project embeddings/tests/Qavren.Edge.Embeddings.Tests/Qavren.Edge.Embeddings.Tests.csproj -c Release -f net10.0 -p:TargetFrameworks=net10.0

      - name: VectorData tests
        run: dotnet run --project embeddings/tests/Qavren.Edge.VectorData.Tests/Qavren.Edge.VectorData.Tests.csproj -c Release -f net10.0 -p:TargetFrameworks=net10.0

      - name: VectorData conformance
        run: dotnet run --project embeddings/tests/Qavren.Edge.VectorData.Conformance.Tests/Qavren.Edge.VectorData.Conformance.Tests.csproj -c Release -p:TargetFrameworks=net10.0
```

Two new assertions on the `windows-2025` leg, both mirroring SP1's existing
"no SQLitePCLRaw bundle" assert, which already runs `dotnet restore QavrenEdge.slnx`
there because it is the only image with every workload:

1. **ORT asset resolution.** Scan `project.assets.json` and fail if
   `Microsoft.ML.OnnxRuntime.Managed` resolved to `lib/netstandard2.0` for any TFM.
   `net10.0` must land on `lib/net8.0`. A silent fall-back to netstandard2.0 drops
   `System.Numerics.Tensors` and changes the span hot path with no error anywhere.
2. **Mobile asset resolution from the unversioned TFMs.** Assert that the
   `net10.0-android`, `net10.0-ios` and `net10.0-maccatalyst` entries resolve
   `lib/net9.0-android35.0`, `lib/net9.0-ios18.0` and `lib/net9.0-maccatalyst18.0`
   respectively. This is the standing regression guard for §4.1's claim that the
   default `TargetPlatformVersion` clears ORT's floors without SP2 pinning a
   versioned TFM — if a future SDK or workload bump lowers a default, this assert is
   what catches it, and the documented response is to pin the versioned TFM on SP2's
   projects only.

**No CI assertion is added for the raised platform floors, deliberately.** A
`SupportedOSPlatformVersion` that is too low does not produce a restore artefact to
scan — it produces a manifest-merge failure on Android and a deployment-target
warning on Apple, both at *build* time, both inside the four device lanes that
already run. A green `device-tests-android` lane is the assertion that ORT's
`minSdkVersion 24` AAR merges, and a green `device-tests-ios` /
`device-tests-maccatalyst` lane is the assertion that a 15.1-built static
xcframework links into a 15.1 deployment target. Adding a string check over csproj
files would restate what those lanes already fail on. §19 item 2 is the one-time
confirmation before any code is written; the lanes are the standing guard.

One new job, `trim-smoke`, on `ubuntu-24.04`: `dotnet publish` a small `net10.0`
console that tokenizes a fixed string, embeds it with the base64 fixture and
round-trips one vec0 upsert + search **through `EdgeDynamicVectorStoreCollection`**
— the reflection-free path, reached with `GetDynamicCollection` and a
`VectorStoreCollectionDefinition`, never `GetCollection<TKey,TRecord>` — with
`PublishTrimmed=true`, then runs it. Publishing the dynamic path is the point: it is
what turns §12.6's "the only trim/AOT-safe path" from an assertion into a measured
claim, and it is only compilable at all because of the non-annotated
`CollectionModel`-taking base constructor in §8. A
second variant with `PublishAot=true` runs `continue-on-error: true` for one
release cycle and then either becomes required or the claim is dropped. Neither
ORT's nor `Microsoft.ML.Tokenizers`'s managed assemblies carry `IsAotCompatible`,
trim-analysis attributes or ILLink descriptors, so the README claims whatever these
two publishes prove and nothing more. `trim-smoke` joins `ci-gate`'s `needs`.

`foundation/tools/ci-checks/assert-workflows.py` gains three assertions: every test
project under `embeddings/tests/` appears as an explicit step in `ci.yml` (so a new
test project cannot silently never run); the ORT asset assert exists; `trim-smoke`
exists and is in `ci-gate`'s `needs`. The `trx2junit.py` and `java-junit` counts
stay at 4 — the device lanes are unchanged in number.

`native.yml` and `release.yml` are untouched. Path filters: the SP2 projects live
under `embeddings/**`, which no native path filter matches, so a managed-only SP2
PR still takes `native.yml`'s `reuse` job.

## 18. Sample app

`foundation/samples/Qavren.Edge.Sample` gains three pages rather than SP2 shipping
a second sample:

- **Embeddings** — pick a preset, show provisioning progress against
  `IProgress<ModelProvisioningProgress>`, embed a string, show dimensions, elapsed
  time, the execution provider accepted and the ones skipped with their reasons.
- **Search** — insert notes, then run vector, keyword and hybrid search side by
  side with all three scores visible, so the distance-versus-RRF inversion is
  something a reader sees rather than reads about.
- **Diagnostics** — the existing page, now rendering three more components with no
  code change, plus toggles for `ProfileComputePlan` and
  `IncludeRowCountsInDiagnostics`.

The sample targets the four MAUI platform TFMs, as SP1 adjustment 31 established.

## 19. Verification items for the plan

Each carries a stated assumption; the plan confirms or adjusts, and records the
result.

1. **ORT asset resolution from the repo's UNVERSIONED TFMs — do this first, before
   any SP2 code.** A throwaway class library targeting
   `net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst`, spelled exactly as
   SP1's merged csproj files spell it, referencing `Microsoft.ML.OnnxRuntime` 1.30.0,
   plus `dotnet restore -v:n`. Assert `net10.0-android` → `lib/net9.0-android35.0`,
   `net10.0-ios` → `lib/net9.0-ios18.0`, `net10.0-maccatalyst` →
   `lib/net9.0-maccatalyst18.0`, and `net10.0` → `lib/net8.0` (not
   `lib/netstandard2.0`). Assumption: yes — a bare `net10.0-ios` takes its
   `TargetPlatformVersion` from the installed platform SDK, which under this repo's
   pinned `maui` 10.0.201 and Xcode 26.2 is well above ORT's 18.0/35.0 floors, and
   `SupportedOSPlatformVersion` plays no part in asset selection at all — which is
   exactly why SP2 is free to raise it to ORT's native floors (item 2) without
   touching a TFM. Run the throwaway at both settings, SP1's 21.0/15.0 and SP2's
   24.0/15.1, and confirm the resolved asset paths are identical; that is the
   cheapest possible proof that the two properties are independent, and it is the
   claim §4.1 rests on. Run it on Windows for android and on the Mac Mini for ios and
   maccatalyst. **If it fails**, SP2 pins `net10.0-ios18.0` /
   `net10.0-android35.0` / `net10.0-maccatalyst18.0` on its own projects and nowhere
   else — SP1's decision 6, its five merged csproj files and §5's "two edits and no
   others" stay untouched either way. This is also what §17 assertion 2 then guards
   forever.
2. **The raised platform floors actually build, link and merge — android 24.0,
   ios/maccatalyst 15.1.** This is the *other* platform-version question and it is
   independent of item 1: item 1 is about restore picking the right managed asset,
   this is about the native being loadable by the OS versions the app declares.
   Three sub-checks, all cheap, all before the platform files are written:
   (a) build `Qavren.Edge.DeviceTests` for `net10.0-android` with SP2 referenced and
   confirm the merged manifest resolves — ORT's AAR declares `minSdkVersion 24` per
   `default_full_aar_build_settings.json`, and a project at 21 either fails the merge
   or has its floor raised without saying so;
   (b) build and link for `net10.0-ios` and `net10.0-maccatalyst` at `15.1` and
   confirm no deployment-target warning from the force-loaded static xcframework,
   whose iphoneos/iphonesimulator slices are built at `--apple_deploy_target=15.1`;
   (c) read the xcframework's **`ios-arm64_x86_64-maccatalyst` slice's own
   `LC_BUILD_VERSION`** (`otool -l` or `vtool -show`) to establish Mac Catalyst's
   true minimum, because ORT's Apple build-settings file defines only iphoneos,
   iphonesimulator and macosx archs and says nothing about which one the Catalyst
   entry is cut from. Assumption: 24.0 and 15.1 are correct and sufficient. **If (c)
   returns something other than a 15.1-family value**, SP2's maccatalyst floor moves
   to whatever it returns and §4.1's block, §16.4 and the README all follow. If (a)
   fails even at 24.0, the fallback is a `SupportedOSPlatformVersion` bump in
   `Qavren.Edge.DeviceTests` (a test host, not a shipped package). Run on the Mac
   Mini for (b) and (c); (a) runs anywhere with the android workload.
3. **What SP2 costs an app, measured rather than estimated.** §10.1 states the
   ceilings from the platform docs and estimates nothing it can avoid estimating, but
   the one number that matters — what ORT's native adds to a real package — has to be
   measured. From the device lanes' own artifacts (they already build app packages, so
   this is `ls -l` and `size -m`, not new CI):
   (a) the `.apk`/`.aab` delta from adding `Qavren.Edge.Onnx`, both with all four ABIs
   and with `$(AndroidSupportedAbis)` trimmed to `arm64-v8a`;
   (b) the iOS binary's `__TEXT` total before and after, against Apple's **80 MB**
   executable cap — this is the only ceiling with no workaround, because ORT's iOS
   targets link the xcframework statically with `ForceLoad=True`;
   (c) the same for Mac Catalyst.
   Record all three in the vault and put the arm64 Android number and the iOS
   `__TEXT` number in the README, alongside §10.1's rule: bundle the 23 MB int8
   preset if you bundle anything, download everything larger. Until these exist the
   README states the ceilings and the rule, and no size number of our own.
4. **Mono.Android and Microsoft.iOS bindings from a non-MAUI class library.**
   `Qavren.Edge.Onnx` targets `net10.0-android` / `net10.0-ios` /
   `net10.0-maccatalyst` **without** `UseMaui`, and needs
   `Android.App.Application.Context`, `ActivityManager`, `PowerManager`,
   `NSUrl`/`NSFileManager` and `DllImport("__Internal")`.
   Assumption: available, same as any platform class library. Verify with a compile
   before writing the platform files.
5. **Mac Catalyst and iOS native wiring — and what the CI lanes actually prove.**
   ORT's `_._` placeholder is documented as deliberate, but "deliberate" is not
   "proven". Do **not** hand-write a `buildTransitive` maccatalyst targets file; run
   the existing lanes first. Then record the limit honestly: `ci.yml` runs
   `device-tests-maccatalyst` and `device-tests-ios` on `runs-on: macos-15-intel`
   with `-r maccatalyst-x64` and `-r iossimulator-x64`, so a green lane proves the
   **x64** RID-graph resolution and the x86_64 slices of ORT's xcframework, and says
   nothing about `maccatalyst-arm64` or a real arm64 device — which is every current
   Mac and every iPhone. It also means no Neural Engine is present, so no EP or
   CoreML-cache claim can come from those lanes (§16.4). The plan decides between
   three options and writes the answer down: (a) README claims "x64, proven in CI"
   only; (b) an SP1 workflow change moving the lanes to `macos-15` arm64, which is
   out of SP2's scope and needs its own PR; (c) a manual arm64 run on the Mac Mini
   recorded in the vault. Default is (a) plus (c).
6. **`Microsoft.Extensions.VectorData.ConformanceTests` 10.10.0's xunit pin.**
   Assumption: xunit.v3 3.2.2, matching the repo's DeviceRunners-driven pin. If it
   demands 4.0.0, `Qavren.Edge.VectorData.Conformance.Tests` is already `net10.0`
   and host-only (§16.4), so it can carry its own pin without touching the three
   device-hosted projects — or the suite is dropped and the gap documented. A
   planning decision, not a design assumption.
7. **`Microsoft.Extensions.AI` 10.10.0 (dotnet/extensions) alongside SP1's
   `Microsoft.Extensions.*` 10.0.12 (dotnet/runtime) pins.** Assumption: they
   coexist. Verify with `dotnet list package --include-transitive` on the SP1
   packable projects before and after, because a silent promotion of
   `Logging.Abstractions` would widen every SP1 nuspec.
8. **`Microsoft.ML.Tokenizers` thread safety, and the per-input allocation.**
   PACKAGE.md says to cache the instance; it states no concurrency guarantee. The
   design serialises behind the run semaphore. A stress test decides whether that can
   ever be relaxed. The same test measures the per-input `IReadOnlyList<int>` that
   `IEdgeTokenizer.Encode` is forced to allocate (§7: 2.0.0 exposes no
   span-destination `EncodeToIds`, so the implementation encodes and copies). If it
   turns out to matter, the fix is a Tokenizers 3.x overload, not a different
   signature here.
9. **Tokenizer parity with Hugging Face, per preset.** `BertOptions.RemoveNonSpacingMarks`
   and `IndividuallyTokenizeCjk` versus HF's `strip_accents` / `tokenize_chinese_chars`
   is unverified for each model. A golden-id test per preset — pinned ids for a
   fixed sentence, generated once from HF — is the only thing that catches a silent
   divergence. Tier 3.
10. **`MemoryHeadroomFactor = 2.5` and `MemoryHeadroomBytes = 48 MB`.** Engineering
    estimates, not measurements. Calibrate with a device run reading
    `os_proc_available_memory()` around a real session load before 1.0. Guessing high
    refuses sessions that would have worked; guessing low gets the app killed.
11. **`ChunkSize = 256`.** Reasoned from chunk size being both the storage unit and
    the KNN scan allocation, not benchmarked — vec0's write and scan performance is
    undocumented upstream (`site/guides/performance.md` is a 64-byte stub). Benchmark
    before any rows-per-second number goes in the README. Note also that the
    `<tbl>_chunks` shadow table carries no index on its partition columns.
12. **`PublishTrimmed` and `PublishAot` behaviour.** Measured fact: neither ORT nor
    Tokenizers carries trim or AOT annotations. What a trimmed or AOT publish
    actually does is untested and cannot be inferred. The `trim-smoke` job decides
    what the README claims.
13. **`PinnedSequenceLength` — does pinning actually win?** The default keeps the
    graph's declared symbolic dims, so CoreML partitions normally and bucketing only
    limits the number of distinct runtime shapes. Pinning emits
    `AddFreeDimensionOverrideByName` and enables `RequireStaticInputShapes`, which
    ORT's docs say CoreML prefers — at the cost of one session, one shape, and a
    full-length run for every batch including short ones. Nobody has measured which
    wins for a 6-layer 384-d encoder on a real device. Measure both on the Mac Mini
    with `ProfileComputePlan=1` for a batch of short strings and a batch of long ones
    before writing any number, or any recommendation, into the README. Until then the
    default stays unpinned, which is the option that cannot silently disable CoreML.
14. **ADRs to write during planning:** 0003 ORT 1.30.0 pin (one day old at design
    time; the four device lanes are the soak, and the *only* rollback on NuGet is
    1.29.0 — upstream's 1.29.1 was never published there, so there is no ladder
    between them); 0004 **NNAPI cut** — not "deprecated but available": ORT's portable
    `AppendExecutionProvider(string, …)` overload does not accept it, so offering it
    would require an `#if ANDROID` call site to `AppendExecutionProvider_Nnapi` and
    would destroy the one-EP-implementation-per-TFM property; the ADR records the
    revisit trigger (ORT exposing NNAPI through the portable overload, or shipping a
    supported Android successor); 0005
    `IEdgeModelPaths` absorbed into SP1 at 1.0 with a type-forward; 0006 multilingual
    deferred to `Microsoft.ML.Tokenizers` 3.x; 0007 MEVD9001 experimental-surface
    exposure; **0008 raised platform floors** — android `24.0` and Apple `15.1` on
    SP2's projects against SP1's `21.0`/`15.0`, sourced from ORT's own AAR and
    xcframework build settings rather than chosen, with item 2's
    `LC_BUILD_VERSION` reading as the evidence and the README's supported-minimum
    statement as the consequence.

## 20. Out of scope for sub-project 2

Chunkers, extractors, content-hash incremental re-index, the ingestion pipeline
(SP3). `IChatClient`, ORT GenAI, the RAG recipe (SP4). Docs site, benchmarks,
NuGet 1.0 (SP5).

Also deliberately out, with the reason:

- **Multilingual and SentencePiece presets** — the raw-piece-index trap (§1).
- **Thermal and low-power *policy*** — the sensor ships and is reported; batch
  backoff belongs with SP3's long-running pipeline.
- **QNN, WebGPU, CUDA, DirectML, `SetEpSelectionPolicy`/`GetEpDevices`** — none is
  reachable on the platforms this suite targets, and the last is desktop plugin-EP
  machinery that would replace a five-line testable switch with an opaque one.
- **NNAPI entirely** — not "registered but off". ORT documents the portable
  `AppendExecutionProvider(string, Dictionary<string,string>)` overload as accepting
  "QNN", "SNPE", "XNNPACK", "CoreML" and "AZURE"; NNAPI is not among them, so it is
  unreachable through the single code path §9.2 is built on. Reaching it needs the
  TFM-gated `AppendExecutionProvider_Nnapi`, compiled inside `#if __ANDROID__` and
  throwing `NotSupportedException` everywhere else — an `#if` in the EP policy, for an
  EP Google deprecated in Android 15 and expects to fall back to CPU. There is no
  `EdgeExecutionProvider.Nnapi`, no `NnapiProviderOptions` and no `"NNAPI"` override
  key. ADR 0004 records the revisit trigger.
- **`IOBinding`, `RunAsync`, `PrePackedWeightsContainer`, `.ort` format** — §9.1.
- **Android Play asset packs and iOS background `NSUrlSession` downloads** — §10.
  Their absence is why §10.1 states the packaging ceilings explicitly and why the
  README's rule is "bundle the 23 MB int8 preset if you bundle anything, download
  everything larger": the two mechanisms an app would otherwise reach for to carry a
  large bundled model are both out of v1, so the size story cannot be deferred to
  them.
- **vec0 `int8`/`bit` columns, binary-quantization rescore, ANN indexes, partition
  keys, metadata/auxiliary columns** — §12.1.
- **Matryoshka dimension truncation** — MEVD never passes options, so it could only
  ever create two sources of truth for storage width.
- **Multiple vector properties per collection, `CompactAsync`, cursor-based
  pagination, down-migrations** — each is a real feature with a real API cost and no
  caller yet.
- **Qavren-branded logging, caching or telemetry middleware** — `UseLogging`,
  `UseOpenTelemetry` and `UseDistributedCache` already exist upstream and compose
  because registration goes through `services.AddEmbeddingGenerator`.
- **A Semantic Kernel adapter package** — SK's `AsTextEmbeddingGenerationService`
  already bridges a plain MEAI generator, and Agent Framework defines no embedding
  abstraction at all. One documented caveat instead: SK's adapter populates only
  `EndpointKey` and `ModelIdKey` from `EmbeddingGeneratorMetadata` and never
  `DimensionsKey`, so `GetDimensions()` returns null for a Qavren generator no
  matter what `DefaultModelDimensions` says. Upstream behaviour; it goes in the
  README so it is not filed against us.
- **A fourth "recipe" package** — §2.
