# Qavren.Edge — Sub-project 1: SQLite foundation + hosting core

Date: 2026-09-10
Status: approved; implementation in progress on `feat/sp1-foundation`. Where the plan's
"Spec adjustments" section (`foundation/docs/superpowers/plans/2026-09-10-sp1-foundation-plan.md`)
contradicts this document, the plan wins: it folded in verified upstream facts.
Owner: Steve Ackley (Qavren Solutions LLC)

## 1. Summary

Qavren.Edge is a free, MIT-licensed, open-source NuGet suite that makes the
**SQLite + sqlite-vec + ONNX Runtime** stack a first-class citizen in .NET MAUI
and plain .NET 10. It borrows Shiny.NET's hosting ergonomics (one builder call,
DI-first, lifecycle-aware, platform-split packages) without becoming a
framework: every package is plain `Microsoft.Extensions.*` wiring, MAUI is
sugar on top, and everything runs and tests on a bare `ServiceCollection`.

Sub-project 1 delivers the foundation everything else stands on: the hosting
core, the MAUI lifecycle bridge, and a SQLite provider with sqlite-vec compiled
into the native library for iOS, Android, Mac Catalyst, Windows, and
Linux/macOS desktop. iOS cannot load SQLite extensions at runtime; compiling vec
in and registering it at library init is the whole reason this suite has a
native build.

## 2. Suite-level decisions (apply to every sub-project)

| # | Decision | Choice |
|---|---|---|
| 1 | What "on par with Shiny" means | Hosting model + DX only. No BLE/GPS/Push/Jobs. |
| 2 | API layering | L0 primitives (Sqlite, Onnx) → L1 Microsoft abstractions (MEAI `IEmbeddingGenerator`/`IChatClient`, MEVD `VectorStore`) → L2 recipes (ingestion, RAG). Each layer independently usable and testable. |
| 3 | Native SQLite | Own build: amalgamation + sqlite-vec compiled in, shipped as a SQLitePCLRaw provider. |
| 4 | Native binding | Own generated `ISQLite3Provider` (checked-in code), not `dynamic_cdecl`, not an `e_sqlite3` drop-in. |
| 5 | ONNX | Wrap `Microsoft.ML.OnnxRuntime` (never rebuild ORT). v1 ships embeddings **and** ORT GenAI chat. |
| 6 | TFMs | `net10.0`, `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst`, `net10.0-windows10.0.19041.0`. |
| 7 | Name | `Qavren.Edge.*`. Repo `qavren-oss/qavren-edge`. |
| 8 | Reference consumer | In-repo MAUI sample + device test runner only. Real apps adopt after 1.0. |
| 9 | Ingestion inputs | Text + Markdown chunkers, plus PDF/DOCX extraction. Images later. |
| 10 | Encryption | SQLCipher variant (`Qavren.Edge.Sqlite.Native.Cipher`) ships in v1. |
| 11 | License | MIT. |
| 12 | Repo home | GitHub org `qavren-oss` (created 2026-09-10; `qavren` was squatted). Reserve the `Qavren.` NuGet ID prefix. |
| 13 | Hosting model | MS-native core (`IServiceCollection` + `IOptions<T>` + hosted services) with `Qavren.Edge.Maui` as a lifecycle bridge and `UseQavrenEdge()` sugar. |
| 14 | CI runners | Public repo → GitHub-hosted macOS/Ubuntu/Windows runners are free. The Mac Mini is not on the critical path. |

## 3. Suite roadmap

Each sub-project gets its own spec → plan → implementation cycle.

| # | Sub-project | Packages | Depends on |
|---|---|---|---|
| 1 | **SQLite foundation + hosting core** (this spec) | `Core`, `Maui`, `Sqlite`, `Sqlite.Provider`, `Sqlite.Native`, `Sqlite.Native.Cipher`, meta `Qavren.Edge` | — |
| 2 | Embeddings + vector store | `Onnx` (session factory, EP policy, model provisioning), `Embeddings.Onnx` (MEAI `IEmbeddingGenerator` + `Microsoft.ML.Tokenizers`), `VectorData` (MEVD `VectorStore` over vec0 + FTS5 hybrid with RRF) | 1 |
| 3 | Ingestion | `Ingestion`: chunkers (plain, markdown heading-scoped, token window), extractors (PDF, DOCX), content-hash incremental re-index, cancellable + checkpointed pipeline | 2 |
| 4 | Chat | `Chat.Onnx`: MEAI `IChatClient` over ORT GenAI, memory budgets, iOS jetsam guards; RAG recipe | 2 |
| 5 | Docs + 1.0 | docs site, benchmarks, NuGet 1.0 | 1–4 |

## 4. Sub-project 1 goals and non-goals

Goals:

- A consumer adds two packages, writes one builder call, and gets a
  pragma-tuned SQLite connection with `vec0` and FTS5 available on every
  supported platform.
- Microsoft.Data.Sqlite, EF Core Sqlite (`.Core` package), sqlite-net-pcl, and
  Dapper all work unchanged on top of the provider.
- Every runtime fact is inspectable: which native library loaded, from where,
  which SQLite/vec/SQLCipher versions, which lifecycle events fired.
- Failures are detected and logged at app launch with a remediation; any later
  use rethrows that same fault instead of a cryptic `DllNotFoundException`.

Non-goals for sub-project 1:

- No ORM, no LINQ, no repository pattern. Helpers emit visible SQL.
- No ONNX, no embeddings, no vector store abstraction (sub-project 2).
- No background job scheduling. Lifecycle events are exposed; scheduling is the
  consumer's business.
- No down-migrations.

## 5. Architecture

### 5.1 Packages

| Package | TFMs | Depends on | Purpose |
|---|---|---|---|
| `Qavren.Edge.Core` | net10.0 | `Microsoft.Extensions.DependencyInjection.Abstractions`, `Options`, `Logging.Abstractions`, `Hosting.Abstractions` | `AddQavrenEdge()` → `EdgeBuilder`; `IEdgeHost`, `IEdgeLifecycle`, `IEdgeStartupTask`, `IEdgePaths`, `IEdgeDiagnostics`, exception types |
| `Qavren.Edge.Maui` | android, ios, maccatalyst, windows | Core, `Microsoft.Maui.Controls` | `MauiAppBuilder.UseQavrenEdge()`; maps platform lifecycle and memory warnings into `IEdgeLifecycle`; `IEdgePaths` over `FileSystem` |
| `Qavren.Edge.Sqlite` | net10.0 | Core, `Microsoft.Data.Sqlite.Core` | `AddSqlite()`, `IEdgeDatabase`, migrations, `VecTable`/`VecBlob`/`Knn`, `FtsTable`, `ISqliteNativeProvider` contract, minimal connection extensions |
| `Qavren.Edge.Sqlite.Provider` | net10.0, ios | `SQLitePCLRaw.core` | Generated `SQLite3Provider_qedge : ISQLite3Provider`. `DllImport("__Internal")` on ios (static xcframework), `DllImport("qedge_sqlite3")` on net10.0 (android, maccatalyst, windows, linux, osx share the net10.0 build; Mac Catalyst loads a dylib from `runtimes/`, matching upstream SQLitePCLRaw). |
| `Qavren.Edge.Sqlite.Native` | net10.0 + all platform TFMs | Sqlite, Provider | Native libs per RID, `UseSqliteNative()` |
| `Qavren.Edge.Sqlite.Native.Cipher` | same | Sqlite, Provider | SQLCipher + libtomcrypt libs per RID, `UseSqliteNativeCipher()`; installs a `DllImportResolver` mapping `qedge_sqlite3` → `qedge_sqlcipher` |
| `Qavren.Edge` | meta | Core, Sqlite, Sqlite.Native | One-line install for the common case |

Dependency direction is strictly downward: Native → Sqlite → Core; Native →
Provider. Core never references MAUI or SQLite. Referencing both Native packages
in one app is a configuration error caught at startup.

### 5.2 Repository layout

One git repository is the shared folder; each sub-project is a top-level
folder with its own `src/`, `tests/`, `samples/`, `docs/`, and README.
Cross-sub-project references are `ProjectReference`s; one solution, one
Central Package Management file, one version line.

```
qavren-edge/                            # git repo root = C:\Users\steve\projects\qavren-edge
  QavrenEdge.slnx
  Directory.Build.props, Directory.Packages.props, global.json, .editorconfig, LICENSE, README.md
  .github/workflows/                    # native.yml, ci.yml, release.yml
  foundation/                           # sub-project 1 (this spec)
    README.md
    native/
      CMakeLists.txt
      versions.json                     # pinned: sqlite, sqlite-vec, sqlcipher, libtomcrypt (tag + sha256)
      src/qedge_init.c                  # SQLITE_EXTRA_INIT hook: auto_extension(vec), qedge_version()
      scripts/                          # build-apple.sh, build-android.sh, build-linux.sh, build-windows.ps1
    src/
      Qavren.Edge.Core/
      Qavren.Edge.Maui/
      Qavren.Edge.Sqlite/
      Qavren.Edge.Sqlite.Provider/      # Generated/SQLite3Provider_qedge.g.cs is checked in
      Qavren.Edge.Sqlite.Native/
      Qavren.Edge.Sqlite.Native.Cipher/
      Qavren.Edge/                      # meta
    tools/ProviderGen/                  # dotnet tool: manifest -> provider source; CI verifies no drift
    tests/
      Qavren.Edge.Core.Tests/
      Qavren.Edge.Sqlite.Tests/
      Qavren.Edge.DeviceTests/          # MAUI runner app; references the two test projects
    samples/Qavren.Edge.Sample/         # MAUI: migrate, insert vectors, KNN, diagnostics page
    docs/superpowers/specs/ , docs/adr/
  embeddings/                           # sub-project 2: Onnx, Embeddings.Onnx, VectorData
  ingestion/                            # sub-project 3
  chat/                                 # sub-project 4
  docs/                                 # sub-project 5: docs site
```

Each later sub-project mirrors `foundation/`'s internal shape. Workflow path
filters use the sub-project prefix (`foundation/native/**`).

Versioning: MinVer from `v*` tags. Native artifacts embed sqlite/vec/cipher
versions and the build SHA, queryable through `qedge_version()`.

## 6. Core

### 6.1 Builder

```csharp
services.AddQavrenEdge(edge => edge
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddMigration<M001_CreateNotes>()
    .UseSqliteNative());
```

`EdgeBuilder` wraps `IServiceCollection` (exposed as `.Services`). Each package
adds extension methods on it. `AddQavrenEdge` is idempotent and registers:
`EdgeOptions`, `IEdgeHost`, `IEdgeLifecycle`, `IEdgePaths` (default impl),
`IEdgeDiagnostics`, and a `TimeProvider` if none exists.

### 6.2 Host and startup

`IEdgeHost`:

- `void Start()` — begins startup exactly once; safe to call repeatedly.
- `Task Started { get; }` — completes when all `IEdgeStartupTask`s finished;
  faults with the first failure.
- `ValueTask EnsureStartedAsync(CancellationToken)` — awaits `Started` and
  rethrows its fault. Every service entry point in the suite calls this, so a
  startup failure surfaces on the first use with its real cause, and callers
  never race migrations.
- `Task StopAsync(CancellationToken)` — raises `Stopping`, disposes.

`IEdgeStartupTask { int Order { get; } Task RunAsync(CancellationToken); }` —
run sequentially ascending by `Order`, ties in registration order. Reserved
orders: 0 native provider install, 100 migrations, 1000 consumer default.

Generic host: `AddQavrenEdge` also registers an `IHostedService` that calls
`Start()` then awaits `Started`. MAUI: the bridge calls `Start()` from the
earliest platform launch event (see §7) and does not block it.

A startup fault is logged once, recorded in diagnostics, faults `Started`, and
is rethrown by every subsequent `EnsureStartedAsync`. Apps that want a repair
screen instead of a crash await `Started` in a try/catch; there is no separate
failure mode option.

### 6.3 Lifecycle

`IEdgeLifecycle` is a hub, not a platform abstraction. Anything may raise;
anything may observe.

- Events: `Sleeping`, `Resumed`, `MemoryPressure(EdgeMemoryPressure level)`
  with levels `Low | Moderate | Critical`, `Stopping`.
- Observers implement `IEdgeLifecycleObserver` (async methods per event) and
  are resolved from DI; order = registration order.
- Each observer runs inside its own try/catch. A throwing observer is logged
  with `EdgeEventIds.LifecycleObserverFailed` and the remaining observers still
  run. The hub never throws to the raiser.
- Raise methods return a `Task` that completes when all observers finish, so a
  platform bridge can await a WAL checkpoint before the OS suspends the app.
- The hub records the last N events (timestamp, event, duration, failures) for
  diagnostics.

### 6.4 Paths

`IEdgePaths { string Data; string Cache; }`. Default implementation:
`LocalApplicationData/<AppName>/qavren-edge`. MAUI implementation:
`FileSystem.AppDataDirectory` and `FileSystem.CacheDirectory`. Tests swap in a
temp directory.

### 6.5 Diagnostics

`IEdgeDiagnostics.Report()` returns `EdgeDiagnosticsReport` with:

- `Components`: every registered Edge component with name, package version,
  and per-component key/value details (Sqlite adds database path, page size,
  journal mode, encryption on/off, applied migration version).
- `Native`: provider name, resolved library path, SQLite version, vec version,
  SQLCipher version or `none`, build SHA.
- `Paths`, `Startup` (task list with durations and the fault, if any),
  `Lifecycle` (recent events).
- `ToString()` renders a readable text block; `ToJson()` renders JSON.

Components contribute through `IEdgeDiagnosticsContributor`.

### 6.6 Exceptions

All derive from `EdgeException`:

- `EdgeConfigurationException` — bad wiring: no native provider registered,
  both native packages registered, encryption key configured without the
  cipher package, duplicate database name.
- `EdgeNativeException` — provider install failed. Message includes the RID,
  the library name searched, the paths probed, and a remediation line.
- `EdgeMigrationException` — version, name, and the inner SQLite error.
- `EdgeDatabaseKeyException` — cipher database failed to open with the
  supplied key.

## 7. MAUI bridge

`MauiAppBuilder.UseQavrenEdge(Action<EdgeBuilder> configure)`:

1. Calls `Services.AddQavrenEdge(configure)`.
2. Replaces `IEdgePaths` with the `FileSystem`-backed implementation.
3. Registers lifecycle mappings through `ConfigureLifecycleEvents`:

| Platform | Launch → `Start()` | `Sleeping` | `Resumed` | `MemoryPressure` | `Stopping` |
|---|---|---|---|---|---|
| Android | `OnCreate` (Application) | `OnPause` | `OnResume` | `ComponentCallbacks2.OnTrimMemory` (levels mapped: `RunningModerate`→Low, `RunningLow`→Moderate, `RunningCritical`/`Complete`→Critical) | `OnDestroy` of the last activity |
| iOS / Mac Catalyst | `FinishedLaunching` | `DidEnterBackground` | `WillEnterForeground` | `DidReceiveMemoryWarning` → Critical | `WillTerminate` |
| Windows | `OnLaunched` | `Activated(Deactivated)` | `Activated(CodeActivated/PointerActivated)` | `MemoryManager.AppMemoryUsageLimitChanging`/`Increased` → Moderate | `Closed` |

`Sleeping` on iOS is awaited inside a `BeginBackgroundTask` so a WAL
checkpoint can finish. The bridge never references SQLite types; it only
raises hub events.

## 8. SQLite (managed)

### 8.1 Registration and options

`edge.AddSqlite(string? name = null, Action<SqliteOptions>? configure = null)`.
Unnamed = default database; named databases register keyed services
(`[FromKeyedServices("corpus")] IEdgeDatabase`). Registering the same name
twice throws `EdgeConfigurationException` at build time.

`SqliteOptions`:

| Option | Default | Notes |
|---|---|---|
| `DatabaseName` | `edge.db` | file name under `IEdgePaths.Data`; `:memory:` allowed |
| `Directory` | `IEdgePaths.Data` | override for tests or shared containers |
| `JournalMode` | `WAL` | applied per connection open |
| `Synchronous` | `Normal` | |
| `BusyTimeout` | 5 s | |
| `ForeignKeys` | `true` | |
| `Pooling` | `true` | Microsoft.Data.Sqlite pooling |
| `CacheSizeKiB` | 8192 (negative pragma) | |
| `Key` | `null` | `SqliteKey.FromRawBytes(byte[32])` or `SqliteKey.FromPassphrase(string)`; also `Func<CancellationToken, ValueTask<SqliteKey>>` for SecureStorage-backed keys. Requires the cipher provider. |

### 8.2 `IEdgeDatabase`

- `string Name`, `string Path`, `bool IsEncrypted`.
- `ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken)` — awaits
  `EnsureStartedAsync`, opens, applies key (cipher) and pragmas, returns an open
  Microsoft.Data.Sqlite connection. Caller disposes.
- `Task<T> ExecuteInTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work, CancellationToken)`.
- `Task<SqliteDatabaseInfo> GetInfoAsync(CancellationToken)` — sqlite version,
  vec version, page size, journal mode, user_version, file size, encrypted.
- `Task<SqliteCheckResult> CheckAsync(CancellationToken)` — `PRAGMA quick_check`
  plus `SELECT vec_version()`.
- `Task RekeyAsync(SqliteKey newKey, CancellationToken)` — cipher only.
- `Task CheckpointAsync(CancellationToken)` — `wal_checkpoint(TRUNCATE)`.

Startup task (order 10): open once, verify key, capture info for diagnostics.

### 8.3 Migrations

`IEdgeMigration { int Version; string Name; Task UpAsync(SqliteConnection, CancellationToken); }`.
Registration is explicit and AOT-safe: `edge.AddMigration<T>()` per migration,
or `edge.AddMigrations(IEnumerable<IEdgeMigration>)` for a hand-built list.
No assembly scanning. Versions must be unique and ascending. The migrator (startup task, order 100) reads
`PRAGMA user_version`, runs each pending migration in its own transaction, and
sets `user_version` after each. A failure stops the run and raises
`EdgeMigrationException`; already-applied migrations stay applied.

### 8.4 Vector helpers (`Qavren.Edge.Sqlite.Vec`)

Every helper emits plain SQL that the consumer could have written. They exist
to make the vec0 conventions (blob encoding, `MATCH ? AND k = ?`) impossible
to get wrong.

- `VecTable.CreateAsync(conn, name, dims, VecMetric metric = Cosine, VecElementType type = Float32, IEnumerable<VecAuxColumn>? aux = null, IEnumerable<VecPartitionKey>? partitions = null, ct)` →
  `CREATE VIRTUAL TABLE IF NOT EXISTS <name> USING vec0(embedding float[<dims>] distance_metric=<metric>, +title TEXT, ...)`.
- `VecBlob.From(ReadOnlySpan<float>)` → `byte[]` (little-endian float32);
  `VecBlob.ToFloats(ReadOnlySpan<byte>)`; int8 and bit variants mirror
  `vec_int8()`/`vec_bit()`.
- `Knn.QueryAsync(conn, table, ReadOnlyMemory<float> query, int k, string? where = null, IEnumerable<SqliteParameter>? parameters = null, ct)` →
  `IReadOnlyList<KnnHit(long RowId, float Distance)>`.
- `VecFunctions`: thin wrappers for `vec_version()`, `vec_length()`,
  `vec_normalize()`, `vec_distance_*` — string builders, not magic.

### 8.5 FTS5 helper

`FtsTable.CreateAsync(conn, name, columns, FtsTokenizer tokenizer = Unicode61, string? contentTable = null, ct)`
and `FtsTable.CreateSyncTriggersAsync(...)` for external-content tables.
Hybrid ranking (RRF over vec + FTS) lives in sub-project 2; this sub-project
only makes both tables easy to create.

### 8.6 Connection extensions

`SqliteConnectionExtensions`: `ExecuteAsync(sql, object? parameters, ct)`,
`ScalarAsync<T>(sql, parameters, ct)`, `QueryAsync<T>(sql, Func<SqliteDataReader, T> map, parameters, ct)`.
Parameters bind from an anonymous object's public properties or from
`IEnumerable<SqliteParameter>`. Supported CLR types: the ones
Microsoft.Data.Sqlite binds natively plus `ReadOnlyMemory<float>` (→ vec blob).
Not an ORM; EF Core, sqlite-net-pcl, and Dapper remain the recommended layers
for object mapping.

### 8.7 Lifecycle observer

- `Sleeping` → `wal_checkpoint(TRUNCATE)` on each open database, best effort,
  logged on failure.
- `MemoryPressure(Critical)` → `SqliteConnection.ClearPool(...)` for each
  database.
- `Stopping` → `ClearAllPools()`.

### 8.8 Encryption

`Native.Cipher` sets `ISqliteNativeProvider.SupportsEncryption = true`. With a
key configured, every physical connection open applies the key before any
pragma, and the startup task verifies it by reading `sqlite_master`. Raw
32-byte keys skip SQLCipher's KDF and are the recommended mobile path (store
the bytes in `SecureStorage`; that is the consumer's `Func<…, SqliteKey>`).
Pooled connections must retain the key across reuse; the mechanism is a plan
item (see §16).

## 9. Provider

- `tools/ProviderGen` reads `provider.manifest.json` (the sqlite3 entry points
  `ISQLite3Provider` requires, with signatures) and emits
  `SQLite3Provider_qedge.g.cs` using plain `DllImport` (`ExactSpelling`, `Cdecl`), matching upstream; `LibraryImport` +
  `DisableRuntimeMarshalling` is ruled out (plan adjustment 1). Generated code is committed; CI
  regenerates and fails on diff.
- Library name per TFM: `__Internal` for ios (static xcframework), `qedge_sqlite3`
  otherwise, Mac Catalyst included (plan adjustment 2).
- `ISqliteNativeProvider` (defined in `Qavren.Edge.Sqlite`):
  `Name`, `LibraryName`, `SupportsEncryption`, `void Install()`,
  `SqliteNativeInfo Describe()`.
- `Native.Install()`: `SQLitePCL.raw.SetProvider(new SQLite3Provider_qedge())`,
  then opens `:memory:`, runs `SELECT sqlite_version(), vec_version(), qedge_version()`
  and stores the result for diagnostics. Also registers a
  `NativeLibrary.SetDllImportResolver` on the provider assembly that records
  the resolved path (and, for Cipher, redirects `qedge_sqlite3` →
  `qedge_sqlcipher`).
- `Install()` runs as startup task order 0. Calling `UseSqliteNative()` and
  `UseSqliteNativeCipher()` together throws `EdgeConfigurationException`.
- The provider is never frozen, so tests can swap providers.

## 10. Native build

### 10.1 Sources and pinning

`native/versions.json` pins SQLite, sqlite-vec, SQLCipher, and libtomcrypt by
release tag and archive SHA-256. CI fetches by tag and verifies the hash; no
git submodules. Updating a pin is a normal PR that Dependabot cannot make, so
a monthly reminder workflow opens an issue listing newer upstream tags.

### 10.2 Compile configuration

Common flags:
`SQLITE_ENABLE_FTS5`, `SQLITE_ENABLE_RTREE`, `SQLITE_ENABLE_MATH_FUNCTIONS`,
`SQLITE_ENABLE_DBSTAT_VTAB`, `SQLITE_THREADSAFE=1`, `SQLITE_DQS=0`,
`SQLITE_DEFAULT_WAL_SYNCHRONOUS=1`, `SQLITE_USE_URI=1`, `SQLITE_TEMP_STORE=2`,
`SQLITE_LIKE_DOESNT_MATCH_BLOBS`, `SQLITE_OMIT_DEPRECATED`,
`SQLITE_OMIT_LOAD_EXTENSION` (no dlopen anywhere; App Store safe),
`SQLITE_EXTRA_INIT=qedge_extra_init`.

`qedge_init.c`: `qedge_extra_init` calls
`sqlite3_auto_extension((void(*)(void))sqlite3_vec_init)` and registers the
`qedge_version()` scalar function returning
`"sqlite <v> | vec <v> | cipher <v|none> | build <sha>"`.

Cipher variant adds `SQLITE_HAS_CODEC`, `SQLCIPHER_CRYPTO_LIBTOMCRYPT`, and
SQLCipher's recommended defaults; no OpenSSL on any platform.

### 10.3 Outputs

| Platform | Artifact | Packaging |
|---|---|---|
| iOS device + simulator | `qedge_sqlite3.xcframework` (static, arm64 device; arm64 + x86_64 simulator) | `buildTransitive/*.targets` adds `<NativeReference Kind="Static" ForceLoad="true" SmartLink="false">` for ios only; Mac Catalyst ships `runtimes/maccatalyst-{arm64,x64}/native/libqedge_sqlite3.dylib` and macOS desktop ships `runtimes/osx-{arm64,x64}/native/libqedge_sqlite3.dylib` (both consumed by the named `DllImport`) |
| Android | `libqedge_sqlite3.so` for arm64-v8a, x86_64, armeabi-v7a; linked with `-Wl,-z,max-page-size=16384` | `runtimes/android-<abi>/native/` (picked up by .NET for Android) |
| Windows | `qedge_sqlite3.dll` x64, arm64 (MSVC, `/guard:cf`) | `runtimes/win-<arch>/native/` |
| Linux | `libqedge_sqlite3.so` x64, arm64 | `runtimes/linux-<arch>/native/` (CI + desktop) |

Cipher variant produces the same set named `qedge_sqlcipher`.

### 10.4 Build hosts

`native.yml` matrix: `macos-15` (Apple), `ubuntu-24.04` (Android via NDK r28+,
Linux x64, Linux arm64 cross), `windows-2025` (MSVC). Each job uploads its
artifacts; a fan-in job assembles the two Native packages. Cache key =
hash of `native/versions.json` + `native/**` + workflow file; managed-only PRs
download the cached natives instead of rebuilding.

## 11. Data flow

App launch (MAUI):

1. `MauiProgram` → `UseQavrenEdge(...)` registers services and lifecycle maps.
2. Platform launch event → `IEdgeHost.Start()` (non-blocking).
3. Startup tasks: provider install (0) → database open + key verify (10) →
   migrations (100) → consumer tasks (1000+).
4. `Started` completes or faults; diagnostics captures both.

Consumer query:

```csharp
public sealed class NotesSearch(IEdgeDatabase db)
{
    public async Task<IReadOnlyList<KnnHit>> NearestAsync(ReadOnlyMemory<float> query, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);   // awaits Started, applies pragmas
        return await Knn.QueryAsync(conn, "notes_vec", query, k: 10, ct: ct);
    }
}
```

Migration:

```csharp
public sealed class M001_CreateNotes : IEdgeMigration
{
    public int Version => 1;
    public string Name => "create notes";
    public async Task UpAsync(SqliteConnection conn, CancellationToken ct)
    {
        await conn.ExecuteAsync("CREATE TABLE notes(id INTEGER PRIMARY KEY, title TEXT NOT NULL, body TEXT NOT NULL)", ct: ct);
        await VecTable.CreateAsync(conn, "notes_vec", dims: 384, ct: ct);
        await FtsTable.CreateAsync(conn, "notes_fts", ["title", "body"], contentTable: "notes", ct: ct);
    }
}
```

## 12. Error handling

- Configuration errors throw from `AddQavrenEdge`/`Build()` when detectable
  there (duplicate database name), otherwise from startup task 0.
- Native load failure → `EdgeNativeException` with RID, library name, probed
  paths, and a remediation ("reference Qavren.Edge.Sqlite.Native or
  .Native.Cipher; for iOS confirm the xcframework NativeReference is present in
  the build log").
- Wrong key → `EdgeDatabaseKeyException` at startup task 10.
- Migration failure → `EdgeMigrationException`; database left at the last
  successful `user_version`.
- Lifecycle observers never propagate exceptions.
- Every exception type carries a stable `EdgeErrorCode` enum for programmatic
  handling and a docs URL fragment.

## 13. Testing

Unit tests (xunit v3 on Microsoft.Testing.Platform, `net10.0`), run on
`windows-latest`, `ubuntu-24.04`, `macos-15` against the real native
artifacts from `native.yml`:

- Core: builder idempotence; startup ordering; `Started` faulting semantics;
  `EnsureStartedAsync` rethrow; lifecycle ordering, isolation of a throwing
  observer, recent-event capture; diagnostics report shape.
- Sqlite: options → pragmas (assert via `PRAGMA` reads); keyed databases;
  migrations (fresh, partial, failing mid-run, idempotent rerun); `VecBlob`
  round-trip and endianness; `VecTable` DDL text; KNN results equal brute-force
  cosine/L2 on 1k random 384-d vectors for k ∈ {1, 10, 100}; FTS create + match;
  connection extensions binding; lifecycle observer checkpoint behaviour.
- Cipher: open/rekey/wrong-key; raw-key and passphrase paths; pooled reuse
  keeps the key; plain provider + key configured → `EdgeConfigurationException`.
- Provider: `ProviderGen` output matches the committed file; both natives
  report expected `qedge_version()`.

Device tests: `Qavren.Edge.DeviceTests` is a MAUI app hosting the same test
assemblies. CI runs it on an iOS simulator (`macos-15`) and an Android
emulator (`ubuntu-24.04` with KVM). Results are written to a file inside the
app sandbox, pulled by the workflow, and published as JUnit. Mac Catalyst and
Windows run the same app on their hosts.

Sample app doubles as the manual QA target.

## 14. CI/CD and release

- `native.yml`: the matrix in §10.4; on `pull_request` only when
  `foundation/native/**` or the workflow changed; on `main` always; artifacts
  retained 30 days.
- `ci.yml`: restore, build, pack; unit tests ×3 OS; device tests ×2; provider
  drift check; `dotnet format` check; package validation
  (`EnablePackageValidation`, baseline = last release).
- `release.yml`: on `v*` tag → pack with MinVer, push to NuGet.org, GitHub
  Release with native artifacts, SBOM, and checksums.
- Dependabot: monthly, grouped minors/patches, open-PR limit 3 (fleet rules).
- Branch protection: PR-only to `main`, required checks = ci + native (when
  triggered).

## 15. Sample app

`samples/Qavren.Edge.Sample` (MAUI, all five TFMs): pages for Diagnostics
(renders `Report()`), Notes (insert rows with random 384-d vectors, run KNN,
show distances), Health (`CheckAsync`, `GetInfoAsync`, checkpoint button), and
Encryption (toggle between the two Native packages via build configuration,
not at runtime).

## 16. Bootstrap and verification items for the plan

Bootstrap:

1. GitHub org `qavren-oss` exists (created 2026-09-10 via browser; `qavren` is a squatted user). Repo `qavren-oss/qavren-edge` created and pushed the same day.
2. `Qavren.` NuGet ID prefix reservation requested 2026-09-10 by email to account@nuget.org for the existing nuget.org organization `Qavren` (admin `stevenfackley`; it already publishes `Qavren.Auth`). Awaiting reply; not blocking implementation, needed before the first publish.
3. Governance files (dependabot, branch protection JSON, editorconfig,
   CODEOWNERS) come from `repo-template-dotnet10-aot`. `new-repo.ps1` accepts
   `-Owner qavren -Author 'Qavren Solutions LLC' -Deploy none`, but it renders a
   service layout, so the plan either runs it and then restructures to §5.2,
   or copies the governance files by hand — the plan decides after checking
   the template's current contents.
4. Add the repo to `_tooling/lib/repos.psd1` and the roster.

Verify during planning (each has a stated assumption; the plan confirms or
adjusts):

- SQLitePCLRaw's current `ISQLite3Provider` surface and whether `LibraryImport`
  with `__Internal` is accepted by the .NET 10 iOS/Mac Catalyst toolchain for a
  static xcframework with `ForceLoad`. Assumption: yes, same as
  `SQLitePCLRaw.lib.e_sqlite3.ios`.
- sqlite-vec's static-init entry point name (`sqlite3_vec_init`) and that
  `sqlite3_auto_extension` from `SQLITE_EXTRA_INIT` registers it before the
  first connection. Assumption: yes, documented by sqlite-vec.
- Microsoft.Data.Sqlite key application on pooled connections: `Password` in
  the connection string handles passphrases; raw hex keys need
  `PRAGMA key = "x'…'"` on each physical open. Assumption: implement through a
  connection-string `Password` for passphrases and an open interceptor for raw
  keys; if MDS offers no interceptor, disable pooling when a raw key is used
  and document it.
- SQLCipher + libtomcrypt build defines on all four host toolchains.
- Device test runner choice: xunit v3 + Microsoft.Testing.Platform hosted in
  a MAUI app, versus `Shiny.Xunit.Runners.Maui`. Assumption: the former, with
  the latter as fallback.
- Android 16 KB page alignment flag with the pinned NDK.
- `.NET for Android` picks `runtimes/android-*/native/*.so` from a NuGet
  without extra targets. Assumption: yes.

## 17. Out of scope for sub-project 1

Embeddings, ONNX, `VectorStore`, hybrid ranking, ingestion, chat, docs site,
benchmarks, physical-device CI lane, down-migrations, any ORM.
