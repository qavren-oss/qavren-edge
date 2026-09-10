# Qavren.Edge Sub-project 1 — SQLite Foundation + Hosting Core: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `Qavren.Edge.Core`, `.Maui`, `.Sqlite`, `.Sqlite.Provider`, `.Sqlite.Native`, `.Sqlite.Native.Cipher` and the `Qavren.Edge` meta package — a DI-first hosting core plus a custom-built SQLite with sqlite-vec compiled in, exposed through a hand-generated `ISQLite3Provider`.

**Architecture:** Three independent tracks converge. (1) A managed hosting core over `Microsoft.Extensions.*` with a lifecycle hub, ordered startup tasks, and a diagnostics report. (2) A native CMake build that compiles the SQLite amalgamation + the sqlite-vec amalgamation + a `qedge_init.c` `SQLITE_EXTRA_INIT` hook into one shared library per RID, with a SQLCipher/libtomcrypt sibling. (3) A checked-in, generator-produced `SQLite3Provider_qedge : ISQLite3Provider` that P/Invokes that library and is installed into `SQLitePCL.raw` as startup task order 0, so `Microsoft.Data.Sqlite` sees it.

**Tech Stack:** .NET 10 (SDK 10.0.401), C# 14, `Microsoft.Data.Sqlite.Core` 10.0.12, `SQLitePCLRaw.core` 3.0.5, xunit.v3 3.2.2 on Microsoft.Testing.Platform, MinVer 8.0.0, Central Package Management, `.slnx`, CMake + Ninja + MSVC (located via `vswhere`), SQLite 3.53.4, sqlite-vec v0.1.9, SQLCipher v4.19.0, libtomcrypt v1.18.2, .NET MAUI 10.0.101, DeviceRunners 0.1.0-preview.12.

**Repo root:** `C:\Users\steve\projects\qavren-edge`.

**Remote and branch model.** The GitHub organisation is **`qavren-oss`** and the remote is **`qavren-oss/qavren-edge`** (spec §16.1; the bare name `qavren` is a squatted user account and must never appear in a URL, a workflow, or `-Owner`). The local clone is on `docs/sp1-spec` and has **no `main` yet**. Sequence, and nothing in the plan may assume a different one:

1. The orchestrator branches `feat/sp1-foundation` from `docs/sp1-spec` and commits every wave onto it. **Implementers never run git.**
2. When the plan closes (Task 11.1), the owner creates `main` from the merge base and pushes both branches — the exact commands are in the repo README bootstrap checklist (Task 1.1) — then opens the PR `feat/sp1-foundation → main`.
3. Only after `main` exists do `ci.yml` and `native.yml` fire (both trigger on `push: branches: [main]` and on pull requests), and only then can branch protection be applied (Task 1.2 Step 5).

Consequence for the whole plan: **no workflow runs during implementation.** Workflows are authored, YAML-linted, and checked for stub residue locally; their first real execution is the owner's post-plan push.

---

## Environment ground truth (this box)

| Fact | Value |
|---|---|
| .NET SDK | 10.0.401; workloads android, ios, maccatalyst, maui-windows installed |
| Visual Studio | 2026 Community at `C:\Program Files\Microsoft Visual Studio\18\Community` (MSVC 14.51.36231, toolset v145) |
| CMake | `C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe` |
| Ninja | `...\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe` |
| vcvars | `C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat` |
| vswhere | `C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe` |
| tclsh | `C:\Program Files\Git\mingw64\bin\tclsh.exe` (already on PATH) — generates the SQLCipher amalgamation |
| python + pyyaml | available (miniforge), pyyaml 6.0.3 — used for YAML lint and SHA3-256 hashing |
| NOT available | Android NDK, clang, macOS/Xcode; no push access is exercised during implementation, so no workflow can run (see the branch model above) |
| MSVC ARM64 cross tools | present only if the VS component `Microsoft.VisualStudio.Component.VC.Tools.ARM64` is installed. Task 3.3 probes for it with `vswhere -requires` and **skips** the `win-arm64` slice with a printed reason when it is absent; CI always has it |

**Consequences:** only `win-x64` natives can be built **and executed** locally (`win-arm64` can be cross-compiled here when the ARM64 toolset is installed, but not run). Every Apple and Android artifact, and every workflow run, is CI-only. Implementers **must not run git commands** — the orchestrator commits between waves. **Never use `cd`** in shell commands (it wipes PATH in this environment); PowerShell is the primary shell and every path is absolute.

---

## Spec adjustments

Every place this plan departs from the approved spec is enumerated here — whether because
verified research contradicted the spec (**the facts win**) or because the spec's own wording is
unimplementable as written and the plan does the nearest correct thing. Each adjustment names the
task that implements it. If a reader diffing the spec against the plan finds a difference that is
**not** in this list, that is a bug in the plan, not a deliberate choice.

1. **§9 — no `LibraryImport` + `DisableRuntimeMarshalling`.** `[DisableRuntimeMarshalling]` is assembly-scoped and bans all reference types and `in`/`ref`/`out` parameters in interop. `ISQLite3Provider` is built on `SafeHandle`-derived parameters (`sqlite3`, `sqlite3_stmt`, `sqlite3_blob`, `sqlite3_backup`, `sqlite3_snapshot`) plus `out IntPtr` and `out byte*`, so the attribute would force rewriting ~130 P/Invokes to raw pointers with hand-rolled `DangerousAddRef`. CA1420 is a default-on warning in .NET 10. Upstream SQLitePCLRaw uses zero `LibraryImport`; its issue #528 has been open and unimplemented since 2022. **The generated provider uses `[DllImport(SQLITE_DLL, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]`, matching upstream exactly.** (Task 3.2)
2. **§5.1 / §10.3 — three provider flavors, not two.** Upstream's `SQLitePCLRaw.config.e_sqlite3` nuspec routes `net10.0-ios` and `net10.0-tvos` to the `__Internal` provider but routes `net10.0-maccatalyst` to the ordinary named-DllImport provider. Mac Catalyst therefore ships `runtimes/maccatalyst-{arm64,x64}/native/libqedge_sqlite3.dylib` and uses `DllImport("qedge_sqlite3")`; only iOS device and simulator use the static xcframework with `__Internal`. (Tasks 3.2, 8.1)
3. **§7 Android — wrong event names.** `OnCreate` on `IAndroidLifecycleBuilder` is the **Activity** overload; the Application-level hook is `OnApplicationCreate`. `OnDestroy(Activity)` is per-activity with no "last activity" notion, so the bridge keeps its own activity counter. (Task 5.2)
4. **§7 Android — the memory levels in the spec are dead.** `TrimMemory.RunningModerate/RunningLow/RunningCritical/Moderate/Complete` are deprecated in API 35 and **not delivered to apps since API 34**, while MAUI 10 targets API 36 by default. New mapping: `TrimMemory.UiHidden` (20) → `Low`, `TrimMemory.Background` (40) → `Moderate`, `OnApplicationLowMemory` → `Critical`; legacy values stay as a pre-34 fallback branch. (Task 5.2)
5. **§7 iOS — `DidReceiveMemoryWarning` does not exist** on `IiOSLifecycleBuilder`, and `MauiUIApplicationDelegate` has no memory-warning export. Register `UIApplication.Notifications.ObserveDidReceiveMemoryWarning(...)` from inside the `FinishedLaunching` handler, hold the returned `NSObject` token, dispose it in `WillTerminate`. (Task 5.2)
6. **§7 Windows — `OnLaunched`'s first argument is `UI.Xaml.Application`, not `Window`** (Learn's table is wrong; `WindowsLifecycle.cs` is authoritative). `Sleeping` maps to `OnVisibilityChanged` with `!args.Visible` (what MAUI's own cross-platform `Stopped` uses), `Resumed` to `OnResumed` (MAUI already suppresses the spurious first activation), `Stopping` to `OnClosed`. `Windows.System.MemoryManager` has no MAUI hook and is unverified on unpackaged WinUI 3 — subscribe from `OnWindowCreated` inside a `try/catch`, unsubscribe on `OnClosed`. (Task 5.2)
7. **§8.1 / §16 — raw keys need no open interceptor and pooling stays on.** `SqliteConnectionStringBuilder.Password` set to the literal string `x'<64 hex>'` is escaped by MDS with SQLite's own `quote()`, emitted as `PRAGMA key = 'x''<64 hex>''';`, then dequoted by SQLCipher's pragma handler back to `x'<64 hex>'`, which its `blob_format` test accepts as a raw key with no PBKDF2. One integration test proves it. The documented fallback is `raw.sqlite3_key_v2(connection.Handle, ...)` tracked by a `ConditionalWeakTable<sqlite3, object>` — **not** disabling pooling. (Tasks 6.1, 9.3)
8. **§8.1 — `Synchronous` is not a connection-string keyword in Microsoft.Data.Sqlite 10.x** (it arrives in 11.0). `PRAGMA synchronous`, `PRAGMA busy_timeout` and `PRAGMA cache_size` are issued per logical open, exactly as MDS itself re-issues `foreign_keys` and `recursive_triggers` on every open. `journal_mode = WAL` is persisted in the file header, so it is set **once at database creation**, never per open and never inside a transaction.  (Task 7.1)
9. **§10.2 — `SQLITE_EXTRA_INIT` collides.** SQLCipher 4.19 hard-`#error`s unless `SQLITE_EXTRA_INIT` and `SQLITE_EXTRA_SHUTDOWN` are defined, and sqlite-vec's documented static-registration path also wants `SQLITE_EXTRA_INIT`. Resolution: one `qedge_extra_init(const char*)` that (cipher build only) calls `sqlcipher_extra_init(0)` first, then `sqlite3_auto_extension((void(*)(void))sqlite3_vec_init)`; plus `SQLITE_EXTRA_SHUTDOWN=qedge_extra_shutdown`, which forwards to `sqlcipher_extra_shutdown` on the cipher build. (Task 2.3)
10. **§10.2 — sqlite-vec needs `SQLITE_CORE` and `SQLITE_VEC_STATIC`.** `SQLITE_CORE` is the load-bearing define (it switches the include to `sqlite3.h` and removes the `SQLITE_EXTENSION_INIT1/2` API thunk); `SQLITE_VEC_STATIC` only suppresses `__declspec(dllexport)` on Windows. (Task 2.3)
11. **§16 device runner — "xunit v3 + Microsoft.Testing.Platform hosted in a MAUI app" does not exist,** and `Shiny.Xunit.Runners.Maui` is dead (last release 2022-08-03, xunit 2.4.1, net6.0 TFMs). MTP's xunit v3 entry point is `xunit.v3.runner.inproc.console`, which injects a `Main` that conflicts with the MAUI app host. Microsoft's own .NET 10 MAUI unit-testing page points at **mattleibow/DeviceRunners**. Device test libraries reference only `xunit.v3.extensibility.core` and `xunit.v3.assert`; the MAUI host references `DeviceRunners.VisualRunners.Maui`, `.VisualRunners.Xunit3` and `.Testing.Targets` (all 0.1.0-preview.12) and calls `.AddXunit3()`. (Task 10.2)
12. **xunit pinned to 3.2.2, not 4.0.0.** DeviceRunners preview.12 declares a 3.2.2 dependency and has never been built against 4.0.0 (released 2026-08-14). The same test class libraries are shared host-side and device-side, so the whole repo pins 3.2.2. No TRX extension is referenced host-side (the matching 1.x-line package version is unverified); host runs assert on process exit code, device runs get TRX from `DeviceRunners.Testing.Targets`. (Task 1.1)
13. **`SQLitePCLRaw.core` pinned to 3.0.5, not 2.1.12.** `isqlite3.cs` is byte-identical between tags v2.1.12 and v3.0.5 (`v3.md`: "There are no code changes in SQLitePCLRaw.core"), so a provider built against 3.0.5 satisfies the 2.1.12 contract that `Microsoft.Data.Sqlite.Core` 10.0.12 declares as its floor; NuGet unifies on 3.0.5. CI asserts that no `SQLitePCLRaw.bundle_*` package is in the graph, because 2.x bundles conflict with core 3.x. (Tasks 1.1, 6.2)
14. **§9 — the provider IS frozen by default.** The spec says "never frozen, so tests can swap providers", but `SqliteConnection`'s static constructor reflectively invokes `SQLitePCL.Batteries_V2.Init()`, which calls `raw.SetProvider` and would silently replace our provider if any transitive package ever drags in a `batteries_v2` assembly. `raw.FreezeProvider()` (`if (_frozen) return;`) is the only defense. New knob: `EdgeOptions.FreezeSqliteProvider`, default `true`; tests set it `false`. (Tasks 2.1, 8.1)
15. **`GetNativeLibraryName()` must not return `e_sqlite3` or `winsqlite3`** or MDS's `Password` path throws `InvalidOperationException`. The plain provider reports `qedge_sqlite3` (unknown to MDS, so `EncryptionSupported()` returns `null`, which is accepted); the cipher package sets it to `sqlcipher` (known, returns `true`). Implemented as a mutable static assigned before `Install()`, so the generated file stays byte-stable. (Tasks 3.2, 8.1)
16. **§16 bootstrap — NuGet ID prefix reservation is not a web form.** Email `account@nuget.org` with the nuget.org owner display name and the requested prefix. Stated criteria include modern `license` expression metadata and an embedded `icon`. (Recorded in the repo README bootstrap checklist, Task 1.1.)
17. **sqlite-vec pinned to v0.1.9 (stable), not `main`.** v0.1.10-alpha.* are prereleases. v0.1.9 fixes a real `DELETE`-on-long-text-metadata bug (#274). `vec_version()` returns the string `"v0.1.9"` **with a leading `v`** — parse accordingly. Additional hard caps the spec omits: `chunk_size` must satisfy `0 < N <= 4096 && N % 8 == 0`, and `distance_metric=` is a **constructor error** on `bit[N]` columns. (Tasks 2.3, 6.1)
18. **The Windows native build uses the Ninja generator, not `Visual Studio 17 2022`.** This box has VS 2026 (toolset v145), and the GitHub `windows-2025` runner label now maps to a VS 2026 image, so the VS generator string is not stable. `vcvars64.bat` plus `-G Ninja` sidesteps the question entirely. (Task 3.3)
19. **Android 16 KB alignment:** the `ubuntu-24.04` default NDK is r27.3.13750724, which is **not** 16 KB-aligned by default. CI pins `ANDROID_NDK_LATEST_HOME` (r29) **and** still passes both `-Wl,-z,max-page-size=16384` and `-Wl,-z,common-page-size=16384`, then asserts alignment with `llvm-readelf -l`. (Tasks 2.3, 6.2)
20. **Central Package Management transitive pinning stays OFF.** In a packable library, a transitively-pinned package is promoted to an explicit `<dependency>` in the produced nuspec, silently widening the public dependency set. (Task 1.1)
21. **No `Microsoft.SourceLink.GitHub` PackageReference.** Source Link is bundled in the SDK and on by default since SDK 8. Set only `PublishRepositoryUrl`, `EmbedUntrackedSources`, `IncludeSymbols`, `SymbolPackageFormat=snupkg`. (Task 1.1)
22. **`PackageValidationBaselineVersion` stays unset until 1.0.0 is on nuget.org** — with no baseline the validator fails the pack. `EnablePackageValidation` is on from day one. (Task 1.1)
23. **§13 — no `-latest` runner labels anywhere.** The spec names `windows-latest`; every workflow in this plan pins `windows-2025`, `ubuntu-24.04`, `macos-15` and (for the Apple device lanes) `macos-15-intel` instead. `-latest` labels move: `macos-latest` is now ARM64 macOS 26, and `windows-latest` now maps to a VS 2026 image. A moving label would silently change the toolchain the native build and the device lanes run on. (Task 6.2.)
24. **One generated provider variant for every TFM: upstream's `provider_internal_funcptrs.cs`** (`FEATURE_FUNCPTRS/callingconv`, `FEATURE_LOADEXTENSION/false`, `FEATURE_WIN32DIR/false`), with only the `SQLITE_DLL` constant and the reported library name substituted. Reason: our native is compiled with `SQLITE_OMIT_LOAD_EXTENSION`, so neither `sqlite3_load_extension` nor `sqlite3_enable_load_extension` is exported (confirmed with `dumpbin /exports` against the Task 3.3 build). The `false` variant stubs `sqlite3_load_extension` to `SQLITE_ERROR` without P/Invoking. **Correction (Wave 3, checked against `dotnet/efcore` `release/10.0` `SqliteConnection.cs`):** the `false` variant does *not* stub `sqlite3_enable_load_extension` — upstream leaves that one a live `DllImport`, and this repo's generated file keeps it. Two claims previously made here were wrong: `SqliteConnection.Deactivate()` calls it **only** under `if (_extensionsEnabled)`, not on every pooled return, and MDS does **not** ignore the result — both `Deactivate()` and `EnableExtensions()` pass it to `SqliteException.ThrowExceptionForRC`. Net effect: nothing throws unless an app opts in via `EnableExtensions(true)`/`LoadExtension`, and then it surfaces as `EntryPointNotFoundException` rather than a clean `SqliteException`. Task 7.1 owns the remedy; dropping `SQLITE_OMIT_LOAD_EXTENSION` is not one. Known limitation this accepts: `sqlite3_win32_set_directory` returns `SQLITE_ERROR` (nothing in Microsoft.Data.Sqlite calls it). (Tasks 2.2, 3.2)
25. **The `.slnx` is written once, in Wave 1, listing every project this plan creates.** It is a root file with a single owner, so parallel tasks never contend for it. **It will not build end-to-end until Wave 9**; every earlier verification targets an explicit `.csproj` path.
26. **Wave membership is decided by the BUILD GRAPH, not by the folder list.** Disjoint folders are necessary but not sufficient: a task whose verify command runs `dotnet build`/`dotnet run` on project P transitively compiles every `ProjectReference` of P, so it will read source files a sibling task is midway through writing. The rule this plan enforces, and the reason waves 3–11 look the way they do:

    > **A wave is parallel-safe only if, for every task in it, no project in that task's build graph is being edited by another task in the same wave.**

    Applied consequences: `Qavren.Edge.Core` is edited in waves 2, 3 and 4 and **nothing that references Core is edited or built in those waves** (`Qavren.Edge.Sqlite.Provider` is safe there because it references only `SQLitePCLRaw.core`, and the native build tasks compile no C#). `Qavren.Edge.Sqlite` is edited in waves 5, 6 and 7 and is the **sole** owner of the Sqlite build graph in each of them, so `Qavren.Edge.Sqlite.Native` (wave 8), the meta package and the integration tests (wave 9) all land after it is frozen. This replaces the earlier three-wave layout, in which T5.1/T3.4/T4.2 verified against a Core or Sqlite project a sibling was still writing — a race whose outcome depended on agent timing. (Waves 3–11.)
27. **§16.1 bootstrap — the org is `qavren-oss`, and it exists.** The spec pins it (created 2026-09-10; `qavren` is a squatted user). Every previous statement in this plan that "the org does not exist" was stale **and used the wrong name**. What is actually true during implementation: the remote exists, but implementers never push and never run git, and the repo has no `main` yet — so no workflow can run regardless. `new-repo.ps1` is invoked with `-Owner qavren-oss`, `CODEOWNERS`, workflow URLs and the README all say `qavren-oss`. (Tasks 1.1, 1.2, 6.2, and the CI-only table.)
28. **§14 branch protection — required contexts are `ci-gate` and `native-gate`, not `ci` and `native`.** A required context that never reports blocks the PR forever, and `native.yml` is deliberately path-filtered on pull requests. So the path filter moves off `on.pull_request.paths` into a `changes` job, and each workflow ends in an aggregate gate job that **always** runs and reports: `native-gate` succeeds immediately when no native path changed, and otherwise mirrors the matrix result. That is the only shape that delivers the spec's "ci + native (when triggered)" without a deadlock. The JSON is edited **and applied** — Task 1.2 Step 5 ships the exact `gh api` command, run by the owner after `main` exists. (Tasks 1.2, 6.2.)
29. **§13 vs §14 — device tests run on FOUR hosts, not two.** §13 says "Mac Catalyst and Windows run the same app on their hosts"; §14 says "device tests ×2". They contradict; §13 is the requirement and §14 is a stale count. `ci.yml` gets `device-tests-android`, `device-tests-ios`, `device-tests-maccatalyst` and `device-tests-windows`. Mac Catalyst is the cheapest Apple lane (no simulator boot) and Windows costs nothing extra, so the extra coverage is close to free. (Task 6.2.)
30. **§13 JUnit — the runner emits TRX; the repo converts it.** DeviceRunners' `dotnet test` integration streams results back over TCP and writes a **TRX** file; it has no JUnit writer, and pinning an unverified third-party `trx2junit` tool version is not acceptable in a plan that cannot restore it here. A 90-line converter, `foundation/tools/trx2junit/trx2junit.py`, is checked in, unit-tested locally against a fixture TRX, and run in every device lane; the JUnit XML is uploaded and published with `dorny/test-reporter@v1` (`reporter: java-junit`). The spec's output contract is met, with no new supply-chain pin. (Task 6.2.)
31. **§15 sample app — four TFMs, not "all five".** `net10.0` is not a MAUI application head; a MAUI app project targets only the four platform TFMs. The libraries under test do multi-target `net10.0`; the sample cannot. (Task 10.1.)
32. **§10.3 Windows `arm64` is built, not dropped.** `build-windows.ps1` takes `-Arch x64|arm64` and selects `vcvars64.bat` or `vcvarsamd64_arm64.bat`; `native.yml` loops both architectures for both variants; the packages already glob `$(NativeArtifactsDir)**`, so `runtimes/win-arm64/native/` populates with no csproj change. Locally, `win-arm64` cross-compiles only when the ARM64 MSVC toolset is installed and can never be **run** here, so the smoke tests stay x64-only. (Tasks 3.3, 4.2, 6.2, 8.1.)
33. **§14 release — SBOM and native artifacts are both attached.** `release.yml` downloads the `native-*` artifacts, zips them per platform, generates an SPDX 2.3 SBOM over `artifacts/` with `anchore/sbom-action@v0`, and attaches the nupkgs, snupkgs, the platform zips, `sbom.spdx.json` and `SHA256SUMS.txt` — with the checksum file computed **after** the zips and the SBOM exist, so it covers every file in the release. (Task 6.2.)

34. **§10.4 — there is no `force` input on `native.yml`, and the *built* natives are cached.**
    The spec is explicit: "Cache key = hash of `native/versions.json` + `native/**` + workflow
    file; managed-only PRs download the cached natives instead of rebuilding." An earlier draft
    of this plan cached only `native/_deps/download` (the fetched upstream tarballs) on a key of
    `versions.json` alone, and then had `ci.yml` call `native.yml` with `force: true`, which
    bypassed the path filter entirely — so a PR that changed only C# still rebuilt four variants
    on `windows-2025`, `ubuntu-24.04` **and** `macos-15`. That is the behaviour the spec bullet
    forbids. Corrected shape: (a) the `build` matrix caches `foundation/native/artifacts` under
    `qedge-native-<os>-<hash>` where `<hash>` is `hashFiles()` over `versions.json`,
    `CMakeLists.txt`, `src/**`, `cmake/**`, `scripts/**` and `.github/workflows/native.yml`, and
    every fetch/compile step is gated on `cache-hit != 'true'`; (b) a pull request that touched no
    native path skips the matrix entirely and takes a single `reuse` job on `ubuntu-24.04` that
    restores all three OS caches and re-uploads them under the same three artifact names — zero
    macOS minutes, zero compilation; (c) the `force` input is **deleted**, and `ci.yml` and
    `release.yml` call `native.yml` with no `with:` block at all. A tag push is not a
    `pull_request`, so `release.yml` still always builds. `assert-workflows.py` fails the build if
    `force` reappears anywhere or if the cache key loses a component. (Task 6.2 Steps 5, 6, 7 and
    the workflow contract check.)
35. **§8.7 — `MemoryPressure(Critical)` calls `ClearAllPools()`, not `ClearPool(...)` per database.**
    The spec says "`SqliteConnection.ClearPool(...)` **for each database**". `ClearPool(connection)`
    resolves to `connection.PoolGroup.Clear()`, and pool groups are keyed on the **raw connection
    string text**, so it clears exactly one connection string's group and needs a live
    `SqliteConnection` instance to name it. The lifecycle observer has neither: it holds
    `IEdgeDatabase` handles, not open connections, and opening a connection purely in order to
    clear its own pool under memory pressure is self-defeating. `ClearAllPools()` is the same
    operation with a superset scope, it is what the spec already asks for on `Stopping`, and every
    pooled connection in the process belongs to this library anyway. (Task 7.1.)
36. **§13 / §5.2 — hosting "the same test assemblies" forces the two test projects to
    multi-target, and that makes `-f net10.0` mandatory on every host run.** The spec is
    unambiguous that `Qavren.Edge.DeviceTests` "references the two test projects", and this plan
    implements exactly that rather than substituting bespoke smoke tests — the device lane exists
    to run the pragma, migration, KNN-vs-brute-force, connection-extension and lifecycle-observer
    assertions against the real Android `.so` and the real iOS `__Internal` static library.
    Consequences that are plan decisions, not spec text: `Qavren.Edge.Core.Tests` and
    `Qavren.Edge.Sqlite.Tests` become `net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst;net10.0-windows10.0.19041.0`,
    with `OutputType=Exe` + the `xunit.v3` metapackage **only** on `net10.0` and
    `xunit.v3.extensibility.core` + `xunit.v3.assert` on the device TFMs (referencing `xunit.v3`
    there would inject a `Main` and collide with the MAUI host — adjustment 11); `dotnet run`
    refuses to pick a TFM, so every host-lane command in this plan and in `ci.yml` carries
    `-f net10.0`. `Qavren.Edge.Sqlite.Cipher.Tests` is deliberately **not** hosted: it needs
    `Qavren.Edge.Sqlite.Native.Cipher`, and referencing both native packages in one app is a
    configuration error the startup pipeline rejects (spec §5.1). (Task 10.2.)
37. **§13 — the "partial" migration case gets its own test.** The spec lists four migration
    cases: "fresh, **partial**, failing mid-run, idempotent rerun". An earlier draft shipped only
    three; "idempotent rerun" runs the *same* migration list twice and does not exercise the
    partial-upgrade path (an existing file already at `user_version = N`, where only N+1.. may
    run) — which is the path every real app takes on its second release.
    `PartialUpgrade_SkipsAppliedMigrationsAndRunsOnlyThePendingOnes` seeds a database with
    migration 1 and a row, then starts a second host with migrations 1, 2 and 3 registered and
    asserts, via recording migrations, that exactly `[2, 3]` ran, that `user_version` is 3, and
    that the seeded row survived. Migration 1 re-running would fail its own `CREATE TABLE`, so
    the test also proves the skip rather than merely observing the end state. (Task 9.2 Step 4.)


---

## File structure

```
qavren-edge/
  QavrenEdge.slnx                       # T1.1 - complete project list, written once
  Directory.Build.props                 # T1.1
  Directory.Packages.props              # T1.1 - every version, written once
  global.json                           # T1.1 - SDK pin + MTP test runner
  NuGet.config .editorconfig .gitignore .gitattributes README.md   # T1.1
  .github/                              # T1.2 governance + workflow stubs; real workflows T6.2
  foundation/
    README.md  docs/adr/                     # T1.3
    native/
      versions.json  CMakeLists.txt          # T2.3
      src/qedge_init.c                       # T2.3
      cmake/ltc-sources.py                   # T2.3
      scripts/fetch-sources.ps1              # T2.3
      scripts/build-windows.ps1              # T3.3 (-Arch x64|arm64, -Cipher)
      scripts/smoke.cs                       # T3.3
      scripts/smoke-cipher.cs                # T4.2
      scripts/build-apple.sh build-android.sh build-linux.sh   # T6.2 (CI-only)
      _deps/ build/ artifacts/               # gitignored
    tools/ProviderGen/
      ProviderGen.csproj  Program.cs  provider.manifest.json    # T2.2
    tools/trx2junit/
      trx2junit.py  fixture.trx  test_trx2junit.py             # T6.2
    tools/ci-checks/assert-workflows.py                        # T6.2
    src/
      Qavren.Edge.Core/                 # T2.1 abstractions, T3.1 lifecycle, T4.1 host+diagnostics+builder
      Qavren.Edge.Sqlite.Provider/      # T3.2 (Generated/SQLite3Provider_qedge.g.cs is checked in)
      Qavren.Edge.Sqlite/               # T5.1 options, T6.1 vec+fts, T7.1 database+migrations
      Qavren.Edge.Sqlite.Native/        # T8.1
      Qavren.Edge.Sqlite.Native.Cipher/ # T8.1
      Qavren.Edge.Maui/                 # T5.2
      Qavren.Edge/                      # T9.1 meta
    tests/
      Qavren.Edge.Core.Tests/           # T2.1, T3.1, T4.1; csproj re-targeted by T10.2
      Qavren.Edge.Sqlite.Tests/         # T5.1, T6.1, T9.2; csproj re-targeted by T10.2
      Qavren.Edge.Sqlite.Cipher.Tests/  # T9.3 (host lane only - needs the Cipher native)
      Qavren.Edge.Provider.Tests/       # T5.3
      Qavren.Edge.DeviceTests/          # T10.2 MAUI host app; hosts the two test libraries above
    samples/Qavren.Edge.Sample/         # T10.1
```

## Wave map

Wave membership obeys adjustment 26: **disjoint folders AND disjoint build graphs.** The
"build graph" column below names every project each task's verify command compiles, so the
disjointness is checkable rather than asserted.

| Wave | Tasks | Folders each task owns | Build graph each task compiles | Parallel-safe because |
|---|---|---|---|---|
| 1 | 1.1 root config + `.slnx`; 1.2 `.github` + branch protection; 1.3 foundation docs | root files / `.github` / `foundation/docs` | none (no project exists yet) | nothing compiles; folders disjoint |
| 2 | 2.1 Core abstractions; 2.2 ProviderGen; 2.3 native sources + CMake | `src/Qavren.Edge.Core` + `tests/…Core.Tests` / `tools/ProviderGen` / `foundation/native` | Core / ProviderGen / no C# | three disjoint graphs |
| 3 | 3.1 lifecycle hub; 3.2 provider project; 3.3 Windows native (x64 + arm64) | Core + Core.Tests / `src/…Sqlite.Provider` / `native/scripts` | Core / Provider (references only `SQLitePCLRaw.core`) / no C# | Provider does **not** reference Core, so Core's editor is alone in its graph |
| 4 | 4.1 host + diagnostics + builder; 4.2 Windows SQLCipher native | Core + Core.Tests / `native/scripts` | Core / a file-based `smoke-cipher.cs` app with no project references | nothing but 4.1 touches or builds Core |
| 5 | 5.1 Sqlite options; 5.2 MAUI bridge; 5.3 provider drift test | `src/…Sqlite` + `tests/…Sqlite.Tests` / `src/…Maui` / `tests/…Provider.Tests` | Sqlite→Core / Maui→Core / Provider.Tests→Provider | Core and Provider are **frozen** after wave 4; only 5.1 edits Sqlite |
| 6 | 6.1 vec + FTS + connection extensions; 6.2 CI workflows + trx2junit | `src/…Sqlite` + `tests/…Sqlite.Tests` / `.github/workflows` + `native/scripts/*.sh` + `tools/trx2junit` + `.config` | Sqlite→Core / none (YAML lint + a python unit test) | 6.2 compiles no C# at all |
| 7 | 7.1 database, migrations, startup tasks, lifecycle observer, `AddSqlite` | `src/…Sqlite` + `tests/…Sqlite.Tests` | Sqlite→Core | runs alone; it is the last edit to Sqlite |
| 8 | 8.1 Native + Native.Cipher packages | `src/…Sqlite.Native`, `src/…Sqlite.Native.Cipher` | Native→Sqlite→Core (all frozen) | runs alone |
| 9 | 9.1 meta package; 9.2 Sqlite integration tests; 9.3 cipher tests | `src/Qavren.Edge` / `tests/…Sqlite.Tests` / `tests/…Sqlite.Cipher.Tests` | each →Native(.Cipher)→Sqlite→Core, all frozen | three readers, zero writers of any shared project |
| 10 | 10.1 sample app (incl. the Encryption page + cipher build configuration); 10.2 device test runner | `samples/Qavren.Edge.Sample` / `tests/…DeviceTests` **plus the two test-project csproj files** (`tests/…Core.Tests`, `tests/…Sqlite.Tests`), which 10.2 re-targets and 10.1 never touches | Maui + Sqlite + both Natives / Maui + Sqlite + Native + the two test libraries it re-targets and hosts | everything they compile was frozen in wave 9; 10.2 is the sole writer of the two test csprojs |
| 11 | 11.1 full-solution verification | none (read-only gate) | the whole `.slnx` | runs alone; it is the gate that closes the plan |

Each wave starts only after every verify command in the previous wave passes.

---

## WAVE 1 — Repository skeleton

### Task 1.1: Root build configuration and solution

**Local-verifiable:** yes.

**Files:**
- Create: `C:\Users\steve\projects\qavren-edge\global.json`
- Create: `C:\Users\steve\projects\qavren-edge\NuGet.config`
- Create: `C:\Users\steve\projects\qavren-edge\Directory.Build.props`
- Create: `C:\Users\steve\projects\qavren-edge\Directory.Packages.props`
- Create: `C:\Users\steve\projects\qavren-edge\QavrenEdge.slnx`
- Create: `C:\Users\steve\projects\qavren-edge\.editorconfig`
- Create: `C:\Users\steve\projects\qavren-edge\.gitignore`
- Create: `C:\Users\steve\projects\qavren-edge\.gitattributes`
- Ensure: `C:\Users\steve\projects\qavren-edge\LICENSE` (MIT, `Copyright (c) 2026 Qavren Solutions LLC`; present from repo bootstrap - create it from the standard MIT text if a clean checkout lacks it, since `README.md` and `PackageLicenseExpression` both reference it)
- Modify: `C:\Users\steve\projects\qavren-edge\README.md` (replace wholesale)

- [ ] **Step 1: Write `global.json`**

```json
{
  "sdk": {
    "version": "10.0.401",
    "rollForward": "latestFeature"
  },
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

The `test.runner` block is the **only** supported switch on the .NET 10 SDK. Do not set `TestingPlatformDotnetTestSupport` anywhere: it enabled the VSTest bridge, which .NET 10 removed ("Testing with VSTest target is no longer supported by MTP on .NET 10 SDK and later").

- [ ] **Step 2: Write `NuGet.config`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
```

A single source avoids NU1507, which Central Package Management raises whenever more than one source is configured.

- [ ] **Step 3: Write `Directory.Build.props`**

```xml
<Project>

  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <Deterministic>true</Deterministic>
    <InvariantGlobalization>true</InvariantGlobalization>
    <!-- NU5127: native-only packages carry no lib/ assembly.
         CS1591: XML doc coverage is a 1.0 gate, not a Wave-2 gate. -->
    <NoWarn>$(NoWarn);NU5127;CS1591</NoWarn>
    <IsPackable>false</IsPackable>
    <NativeArtifactsDir>$(MSBuildThisFileDirectory)foundation\native\artifacts\</NativeArtifactsDir>
  </PropertyGroup>

  <!-- CI only: normalizes stored source paths. Must stay off locally or the debugger loses sources. -->
  <PropertyGroup Condition="'$(GITHUB_ACTIONS)' == 'true'">
    <ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>
  </PropertyGroup>

  <PropertyGroup Condition="'$(IsPackable)' == 'true'">
    <IsAotCompatible Condition="$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net8.0'))">true</IsAotCompatible>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <EnablePackageValidation>true</EnablePackageValidation>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <EmbedUntrackedSources>true</EmbedUntrackedSources>
    <IncludeSymbols>true</IncludeSymbols>
    <SymbolPackageFormat>snupkg</SymbolPackageFormat>
    <Authors>Qavren Solutions LLC</Authors>
    <Company>Qavren Solutions LLC</Company>
    <Product>Qavren.Edge</Product>
    <Copyright>Copyright (c) Qavren Solutions LLC</Copyright>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <PackageProjectUrl>https://github.com/qavren-oss/qavren-edge</PackageProjectUrl>
    <RepositoryUrl>https://github.com/qavren-oss/qavren-edge</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <PackageTags>sqlite;sqlite-vec;vector;maui;embedded</PackageTags>
    <MinVerTagPrefix>v</MinVerTagPrefix>
  </PropertyGroup>

</Project>
```

Notes for the implementer: `PackageValidationBaselineVersion` is deliberately absent — with no published 1.0.0 the baseline validator has nothing to compare against and fails the pack. Add it in the same PR that follows the 1.0.0 push. `VerifyReferenceAotCompatibility` / `VerifyReferenceTrimCompatibility` are deliberately absent too: the `IsAotCompatible` assembly metadata is new in .NET 10 and `SQLitePCLRaw.core` does not carry it, so enabling them buys a wall of IL3058/IL2125 warnings for nothing. No `Microsoft.SourceLink.GitHub` reference: Source Link ships in the SDK and is on by default.

- [ ] **Step 4: Write `Directory.Packages.props`**

```xml
<Project>

  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <!-- OFF on purpose: a pinned transitive is promoted into the produced nuspec,
         silently widening the public dependency set of a shipped library. -->
    <CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled>
    <MauiVersion>10.0.101</MauiVersion>
  </PropertyGroup>

  <ItemGroup>
    <GlobalPackageReference Include="MinVer" Version="8.0.0" />
  </ItemGroup>

  <ItemGroup Label="Runtime">
    <PackageVersion Include="Microsoft.Data.Sqlite.Core" Version="10.0.12" />
    <PackageVersion Include="SQLitePCLRaw.core" Version="3.0.5" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.12" />
    <PackageVersion Include="Microsoft.Extensions.Options" Version="10.0.12" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.12" />
    <PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.12" />
  </ItemGroup>

  <ItemGroup Label="Test">
    <PackageVersion Include="xunit.v3" Version="3.2.2" />
    <PackageVersion Include="xunit.v3.extensibility.core" Version="3.2.2" />
    <PackageVersion Include="xunit.v3.assert" Version="3.2.2" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.0.12" />
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.12" />
    <PackageVersion Include="Microsoft.Extensions.Logging" Version="10.0.12" />
  </ItemGroup>

  <ItemGroup Label="MAUI">
    <PackageVersion Include="Microsoft.Maui.Controls" Version="$(MauiVersion)" />
  </ItemGroup>

  <ItemGroup Label="Device test runner">
    <PackageVersion Include="DeviceRunners.VisualRunners.Maui" Version="0.1.0-preview.12" />
    <PackageVersion Include="DeviceRunners.VisualRunners.Xunit3" Version="0.1.0-preview.12" />
    <PackageVersion Include="DeviceRunners.Testing.Targets" Version="0.1.0-preview.12" />
  </ItemGroup>

</Project>
```

Do **not** add `Microsoft.NET.Test.Sdk` or `xunit.runner.visualstudio`: this repo is MTP-only, and `Microsoft.Testing.Platform.MSBuild` arrives transitively through `xunit.v3`.

- [ ] **Step 5: Write `QavrenEdge.slnx` (complete, written once, never edited by another task)**

```xml
<Solution>
  <Folder Name="/foundation/src/">
    <Project Path="foundation/src/Qavren.Edge.Core/Qavren.Edge.Core.csproj" />
    <Project Path="foundation/src/Qavren.Edge.Maui/Qavren.Edge.Maui.csproj" />
    <Project Path="foundation/src/Qavren.Edge.Sqlite/Qavren.Edge.Sqlite.csproj" />
    <Project Path="foundation/src/Qavren.Edge.Sqlite.Provider/Qavren.Edge.Sqlite.Provider.csproj" />
    <Project Path="foundation/src/Qavren.Edge.Sqlite.Native/Qavren.Edge.Sqlite.Native.csproj" />
    <Project Path="foundation/src/Qavren.Edge.Sqlite.Native.Cipher/Qavren.Edge.Sqlite.Native.Cipher.csproj" />
    <Project Path="foundation/src/Qavren.Edge/Qavren.Edge.csproj" />
  </Folder>
  <Folder Name="/foundation/tests/">
    <Project Path="foundation/tests/Qavren.Edge.Core.Tests/Qavren.Edge.Core.Tests.csproj" />
    <Project Path="foundation/tests/Qavren.Edge.Sqlite.Tests/Qavren.Edge.Sqlite.Tests.csproj" />
    <Project Path="foundation/tests/Qavren.Edge.Sqlite.Cipher.Tests/Qavren.Edge.Sqlite.Cipher.Tests.csproj" />
    <Project Path="foundation/tests/Qavren.Edge.Provider.Tests/Qavren.Edge.Provider.Tests.csproj" />
    <Project Path="foundation/tests/Qavren.Edge.DeviceTests/Qavren.Edge.DeviceTests.csproj" />
  </Folder>
  <Folder Name="/foundation/tools/">
    <Project Path="foundation/tools/ProviderGen/ProviderGen.csproj" />
  </Folder>
  <Folder Name="/foundation/samples/">
    <Project Path="foundation/samples/Qavren.Edge.Sample/Qavren.Edge.Sample.csproj" />
  </Folder>
</Solution>
```

`dotnet build QavrenEdge.slnx` will fail until Wave 9 completes — that is expected and is why earlier tasks verify against explicit `.csproj` paths.

- [ ] **Step 6: Write `.gitignore`**

```gitignore
bin/
obj/
out/
*.user
*.suo
.vs/
.idea/
.DS_Store
Thumbs.db

# Test output
TestResults/
coverage.*.xml
*.trx

# Packaging
artifacts/
*.nupkg
*.snupkg

# Native build (fetched sources, intermediates, and produced binaries)
foundation/native/_deps/
foundation/native/build/
foundation/native/artifacts/
```

- [ ] **Step 7: Write `.gitattributes`**

```gitattributes
* text=auto eol=lf

*.ps1    text eol=crlf
*.psm1   text eol=crlf
*.psd1   text eol=crlf
*.cmd    text eol=crlf
*.bat    text eol=crlf
*.sln    text eol=crlf

*.png    binary
*.jpg    binary
*.ico    binary
*.pdf    binary
*.a      binary
*.dll    binary
*.dylib  binary
*.so     binary
```

- [ ] **Step 8: Write `.editorconfig`**

Copy `C:\Users\steve\projects\repo-template-dotnet10-aot\.editorconfig` verbatim, then append this block at the end of the file:

```ini
# Generated provider source is excluded from style enforcement.
[**/Generated/*.g.cs]
generated_code = true
dotnet_analyzer_diagnostic.severity = none

# Vendored C sources
[foundation/native/_deps/**]
generated_code = true
```

Command to copy:

```powershell
Copy-Item "C:\Users\steve\projects\repo-template-dotnet10-aot\.editorconfig" "C:\Users\steve\projects\qavren-edge\.editorconfig" -Force
```

- [ ] **Step 9: Replace `README.md`**

```markdown
# Qavren.Edge

Free, MIT-licensed .NET packages that make **SQLite + sqlite-vec + ONNX Runtime**
a first-class citizen in .NET MAUI and plain .NET 10.

Sub-project 1 (`foundation/`) ships the hosting core, the MAUI lifecycle bridge,
and a SQLite provider with sqlite-vec compiled into the native library for iOS,
Android, Mac Catalyst, Windows, and Linux/macOS desktop.

## Layout

| Folder | Contents |
|---|---|
| `foundation/` | Sub-project 1: Core, Maui, Sqlite, Sqlite.Provider, Sqlite.Native(.Cipher), meta |
| `embeddings/` | Sub-project 2 (not started) |
| `ingestion/` | Sub-project 3 (not started) |
| `chat/` | Sub-project 4 (not started) |
| `docs/` | Sub-project 5 (not started) |

## Build

```powershell
dotnet build C:\Users\steve\projects\qavren-edge\QavrenEdge.slnx -c Release
```

The managed build needs no native artifacts. To run the SQLite tests you must
first build the native library for your host — see `foundation/native/README.md`.

## Bootstrap checklist (owner actions, not automatable)

The GitHub organisation is `qavren-oss` and the repo is `qavren-oss/qavren-edge`
(both created 2026-09-10). The bare name `qavren` is a squatted **user** account —
never use it in a remote URL, a workflow, or a tool argument.

- [ ] **Create `main` and push.** The clone has no default branch yet, so nothing can be
      protected and no workflow can fire. Run this once, after the implementation branch is complete:

      ```powershell
      git -C C:\Users\steve\projects\qavren-edge remote add origin https://github.com/qavren-oss/qavren-edge.git
      git -C C:\Users\steve\projects\qavren-edge branch main docs/sp1-spec
      git -C C:\Users\steve\projects\qavren-edge push -u origin main
      git -C C:\Users\steve\projects\qavren-edge push -u origin feat/sp1-foundation
      gh repo edit qavren-oss/qavren-edge --default-branch main
      gh pr create --repo qavren-oss/qavren-edge --base main --head feat/sp1-foundation --fill
      ```

- [ ] **Apply branch protection** — after `main` exists and `ci.yml` / `native.yml` have each
      reported once, so the contexts are known to GitHub:

      ```powershell
      gh api -X PUT repos/qavren-oss/qavren-edge/branches/main/protection --input .github/branch-protection.json
      ```

      Required contexts are `ci-gate` and `native-gate` (see `.github/branch-protection.json`);
      both gate jobs always report, so a managed-only PR is never blocked waiting on a skipped
      native build.
- [ ] Reserve the `Qavren.` NuGet ID prefix by **emailing `account@nuget.org`** with the
      nuget.org owner display name (`Qavren`, admin `stevenfackley`) and the requested prefix.
      There is no web form. Do this after the first package is published with a `license`
      expression and an embedded `icon`.
- [ ] Add the repo to `_tooling/lib/repos.psd1` (`ActiveCI`) and regenerate the roster.
- [ ] Add the `NUGET_API_KEY` repository secret before the first `v*` tag, or `release.yml` fails at the push step.

## Licence

MIT. See `LICENSE`.
```

- [ ] **Step 10: Verify**

```powershell
pwsh -NoProfile -Command "$r='C:\Users\steve\projects\qavren-edge'; $need=@('global.json','NuGet.config','Directory.Build.props','Directory.Packages.props','QavrenEdge.slnx','.editorconfig','.gitignore','.gitattributes','README.md','LICENSE'); $missing=$need | Where-Object { -not (Test-Path (Join-Path $r $_)) }; if ($missing) { Write-Error ('MISSING: ' + ($missing -join ', ')) } else { [xml](Get-Content (Join-Path $r 'Directory.Build.props')) | Out-Null; [xml](Get-Content (Join-Path $r 'Directory.Packages.props')) | Out-Null; [xml](Get-Content (Join-Path $r 'QavrenEdge.slnx')) | Out-Null; Get-Content (Join-Path $r 'global.json') | ConvertFrom-Json | Out-Null; Write-Host 'OK: all root files present and well-formed' }"
```

Expected: `OK: all root files present and well-formed`.

---

---

### Task 1.2: GitHub governance files, branch protection, and workflow stubs

**Local-verifiable:** yes (YAML/JSON lint only). The remote `qavren-oss/qavren-edge` exists, but it
has no `main` yet and implementers never push, so no workflow can run during implementation; the
`gh api` call that applies branch protection is written here and executed by the owner after
`main` exists (README bootstrap checklist, Task 1.1).

**Files:**
- Create: `C:\Users\steve\projects\qavren-edge\.github\CODEOWNERS`
- Create: `C:\Users\steve\projects\qavren-edge\.github\dependabot.yml`
- Create: `C:\Users\steve\projects\qavren-edge\.github\branch-protection.json`
- Create: `C:\Users\steve\projects\qavren-edge\.github\PULL_REQUEST_TEMPLATE.md`
- Create: `C:\Users\steve\projects\qavren-edge\.github\ISSUE_TEMPLATE\` (copied)
- Create: `C:\Users\steve\projects\qavren-edge\.github\workflows\ci.yml`
- Create: `C:\Users\steve\projects\qavren-edge\.github\workflows\native.yml`
- Create: `C:\Users\steve\projects\qavren-edge\.github\workflows\release.yml`
- Create: `C:\Users\steve\projects\qavren-edge\.github\workflows\upstream-pins.yml`

- [ ] **Step 1: Copy governance files from the template, excluding deploy/docker**

```powershell
pwsh -NoProfile -Command "$src='C:\Users\steve\projects\repo-template-dotnet10-aot\.github'; $dst='C:\Users\steve\projects\qavren-edge\.github'; New-Item -ItemType Directory -Force -Path $dst,(Join-Path $dst 'workflows') | Out-Null; foreach ($f in 'CODEOWNERS','dependabot.yml','branch-protection.json','PULL_REQUEST_TEMPLATE.md') { Copy-Item (Join-Path $src $f) (Join-Path $dst $f) -Force }; Copy-Item (Join-Path $src 'ISSUE_TEMPLATE') $dst -Recurse -Force; Get-ChildItem $dst -Recurse -File | Select-Object -ExpandProperty FullName"
```

Do **not** copy `copilot-instructions.md`, `deploy-prod.yml`, `deploy-test.yml`, `secret-scan.yml`, `triage-issues.yml`, `Dockerfile`, `docker-compose*.yml`, `.dockerignore` or the `.env*` files. This repo has no deploy target and no container.

Then fix the owner in `CODEOWNERS` — the template ships a placeholder:

```powershell
Set-Content -Path "C:\Users\steve\projects\qavren-edge\.github\CODEOWNERS" -Encoding utf8 -Value @(
  '# Every path is owned by the repository owner.',
  '*       @stevenfackley'
)
```

- [ ] **Step 2: Rewrite `dependabot.yml` for a library repo**

```yaml
version: 2
updates:
  - package-ecosystem: nuget
    directory: "/"
    schedule:
      interval: monthly
    open-pull-requests-limit: 3
    groups:
      dotnet-minor-patch:
        patterns:
          - "*"
        update-types:
          - minor
          - patch
  - package-ecosystem: github-actions
    directory: "/"
    schedule:
      interval: monthly
    open-pull-requests-limit: 3
    groups:
      actions-minor-patch:
        patterns:
          - "*"
        update-types:
          - minor
          - patch
```

Majors are deliberately ungrouped (a grouped major is a fleet rule violation) and are handled by a tracking issue instead.

- [ ] **Step 3: Rewrite `branch-protection.json` for this repo's checks**

The template ships `"contexts": ["ci", "secret-scan"]`. Neither is right here: there is no
secret-scan workflow, and a bare `ci` / `native` context deadlocks a managed-only PR — a required
check that never reports blocks the merge forever, and `native.yml` deliberately does not build on
PRs that touch no native path. Both workflows therefore end in an aggregate **gate** job that
always runs and always reports (Task 6.2), and those two job names are the required contexts.

Replace the file with exactly this:

```json
{
  "required_status_checks": {
    "strict": true,
    "contexts": ["ci-gate", "native-gate"]
  },
  "enforce_admins": false,
  "required_pull_request_reviews": null,
  "restrictions": null,
  "allow_force_pushes": false,
  "allow_deletions": false,
  "required_linear_history": true,
  "required_conversation_resolution": true
}
```

`required_pull_request_reviews: null` is deliberate: this is a single-maintainer repo, so demanding
an approving review would make every PR unmergeable. PR-only enforcement comes from
`allow_force_pushes: false` plus GitHub's default block on direct pushes to a protected branch.

The command that applies it is owner-run, after `main` exists and both workflows have reported once:

```powershell
gh api -X PUT repos/qavren-oss/qavren-edge/branches/main/protection --input C:\Users\steve\projects\qavren-edge\.github\branch-protection.json
```

- [ ] **Step 4: Write the four workflow stubs**

Each stub is valid YAML that a real workflow replaces in Task 6.2. Every one uses `workflow_dispatch` only, so nothing fires accidentally on the first push.

`.github/workflows/ci.yml`:

```yaml
name: ci
on:
  workflow_dispatch:
permissions:
  contents: read
jobs:
  placeholder:
    runs-on: ubuntu-24.04
    steps:
      - run: echo "ci.yml is a stub - replaced in Task 6.2"
```

`.github/workflows/native.yml`:

```yaml
name: native
on:
  workflow_dispatch:
permissions:
  contents: read
jobs:
  placeholder:
    runs-on: ubuntu-24.04
    steps:
      - run: echo "native.yml is a stub - replaced in Task 6.2"
```

`.github/workflows/release.yml`:

```yaml
name: release
on:
  workflow_dispatch:
permissions:
  contents: read
jobs:
  placeholder:
    runs-on: ubuntu-24.04
    steps:
      - run: echo "release.yml is a stub - replaced in Task 6.2"
```

`.github/workflows/upstream-pins.yml`:

```yaml
name: upstream-pins
on:
  workflow_dispatch:
permissions:
  contents: read
jobs:
  placeholder:
    runs-on: ubuntu-24.04
    steps:
      - run: echo "upstream-pins.yml is a stub - replaced in Task 6.2"
```

- [ ] **Step 5: Verify (YAML lint every workflow plus dependabot, and check the protection JSON)**

```powershell
python -c "import yaml,glob,sys; fs=glob.glob(r'C:\Users\steve\projects\qavren-edge\.github\**\*.yml', recursive=True); [yaml.safe_load(open(f, encoding='utf-8')) for f in fs]; print('OK: %d yaml files parsed' % len(fs)); sys.exit(0 if len(fs) >= 5 else 1)"
```

Expected: `OK: 8 yaml files parsed` (4 workflows + dependabot + 3 issue templates), exit code 0.

```powershell
pwsh -NoProfile -Command "$bp = Get-Content 'C:\Users\steve\projects\qavren-edge\.github\branch-protection.json' -Raw | ConvertFrom-Json; $c = @($bp.required_status_checks.contexts); if (($c -join ',') -ne 'ci-gate,native-gate') { Write-Error ('unexpected contexts: ' + ($c -join ',')) }; if ($bp.allow_force_pushes -ne $false) { Write-Error 'force pushes must be blocked' }; $co = Get-Content 'C:\Users\steve\projects\qavren-edge\.github\CODEOWNERS' -Raw; if ($co -notmatch '@stevenfackley') { Write-Error 'CODEOWNERS still holds the template placeholder' }; Write-Host 'OK: branch protection requires ci-gate + native-gate'"
```

Expected: `OK: branch protection requires ci-gate + native-gate`.

---

---

### Task 1.3: Foundation folder documentation

**Local-verifiable:** yes.

**Files:**
- Create: `C:\Users\steve\projects\qavren-edge\foundation\README.md`
- Create: `C:\Users\steve\projects\qavren-edge\foundation\docs\adr\0001-record-architecture-decisions.md`
- Create: `C:\Users\steve\projects\qavren-edge\foundation\docs\adr\0002-own-sqlite-build-and-provider.md`

- [ ] **Step 1: Write `foundation/README.md`**

```markdown
# Qavren.Edge — foundation (sub-project 1)

Hosting core, MAUI lifecycle bridge, and a SQLite provider with sqlite-vec
compiled into the native library.

## Packages

| Package | TFMs | Purpose |
|---|---|---|
| `Qavren.Edge.Core` | net10.0 | `AddQavrenEdge()`, host, lifecycle hub, paths, diagnostics, exceptions |
| `Qavren.Edge.Maui` | android, ios, maccatalyst, windows | `UseQavrenEdge()`, platform lifecycle bridge, `FileSystem`-backed paths |
| `Qavren.Edge.Sqlite` | net10.0 | `AddSqlite()`, `IEdgeDatabase`, migrations, vec/FTS helpers |
| `Qavren.Edge.Sqlite.Provider` | net10.0, ios | Generated `SQLite3Provider_qedge : ISQLite3Provider` |
| `Qavren.Edge.Sqlite.Native` | net10.0 + platform TFMs | Native libraries per RID, `UseSqliteNative()` |
| `Qavren.Edge.Sqlite.Native.Cipher` | same | SQLCipher + libtomcrypt libraries, `UseSqliteNativeCipher()` |
| `Qavren.Edge` | meta | One-line install for the common case |

Dependency direction is strictly downward: Native to Sqlite to Core, and Native
to Provider. Core never references MAUI or SQLite. Referencing both Native
packages in one app is a configuration error caught at startup.

## Building the native library locally (Windows x64 only)

```powershell
pwsh C:\Users\steve\projects\qavren-edge\foundation\native\scripts\fetch-sources.ps1
pwsh C:\Users\steve\projects\qavren-edge\foundation\native\scripts\build-windows.ps1
```

Output lands in `foundation/native/artifacts/win-x64/qedge_sqlite3.dll`. Apple
and Android artifacts are produced only by `.github/workflows/native.yml`;
there is no NDK, clang, or macOS on the development box.

## Running tests

```powershell
dotnet run --project C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj -c Release -f net10.0
```

The Sqlite tests additionally need the native library from the step above; the
`Qavren.Edge.Sqlite.Native` project copies it into every referencing project's
output directory.
```

- [ ] **Step 2: Write `foundation/docs/adr/0001-record-architecture-decisions.md`**

```markdown
# 1. Record architecture decisions

Date: 2026-09-10

## Status

Accepted

## Context

Qavren.Edge makes several decisions that are expensive to reverse: owning a
native SQLite build, generating an `ISQLite3Provider` instead of consuming a
published one, and pinning upstream C sources by hash.

## Decision

We record architecturally significant decisions as numbered ADRs in
`foundation/docs/adr/`. Later sub-projects add their own numbered files to the
same sequence.

## Consequences

Decisions are reviewable in a pull request and durable beyond any one spec.
```

- [ ] **Step 3: Write `foundation/docs/adr/0002-own-sqlite-build-and-provider.md`**

```markdown
# 2. Own the SQLite build and the SQLitePCLRaw provider

Date: 2026-09-10

## Status

Accepted

## Context

iOS cannot load SQLite extensions at runtime: the OS-provided `libsqlite3` is
compiled without extension loading, and `SQLITE_OMIT_LOAD_EXTENSION` is the
App Store-safe posture anyway. sqlite-vec must therefore be compiled into the
library and registered at library initialisation.

Consuming `e_sqlite3` is not an option because it has no sqlite-vec.
Consuming `SQLitePCLRaw.provider.dynamic_cdecl` is not an option because it
resolves entry points through `dlsym`, which a static iOS link does not offer.

## Decision

Compile our own library (`qedge_sqlite3`) from the SQLite amalgamation plus the
sqlite-vec amalgamation plus a `SQLITE_EXTRA_INIT` hook, and ship a checked-in,
generator-produced `SQLite3Provider_qedge : ISQLite3Provider` that P/Invokes it.

The provider uses classic `[DllImport(..., ExactSpelling = true,
CallingConvention = CallingConvention.Cdecl)]`, matching upstream SQLitePCLRaw.
`[LibraryImport]` with `[DisableRuntimeMarshalling]` was rejected: the attribute
is assembly-scoped and bans reference types and `in`/`ref`/`out` parameters in
interop, which the `SafeHandle`-typed and `out IntPtr` signatures across
`ISQLite3Provider` require.

## Consequences

We own SQLite CVE response for this library and must bump `native/versions.json`
ourselves; Dependabot cannot see these pins, so `upstream-pins.yml` opens a
monthly issue listing newer upstream tags.

We gain sqlite-vec on every platform including iOS, one code path for
encryption via a sibling SQLCipher build, and full control of compile flags.
```

- [ ] **Step 4: Verify**

```powershell
pwsh -NoProfile -Command "$f=@('C:\Users\steve\projects\qavren-edge\foundation\README.md','C:\Users\steve\projects\qavren-edge\foundation\docs\adr\0001-record-architecture-decisions.md','C:\Users\steve\projects\qavren-edge\foundation\docs\adr\0002-own-sqlite-build-and-provider.md'); $m=$f | Where-Object { -not (Test-Path $_) }; if ($m) { Write-Error ('MISSING: ' + ($m -join ', ')) } else { Write-Host 'OK: foundation docs present' }"
```

Expected: `OK: foundation docs present`.

---

---

## WAVE 2 — Core abstractions, provider generator, native sources

### Task 2.1: `Qavren.Edge.Core` abstractions, options, paths, exceptions

**Local-verifiable:** yes.

**Files:**
- Create: `foundation\src\Qavren.Edge.Core\Qavren.Edge.Core.csproj`
- Create: `foundation\src\Qavren.Edge.Core\EdgeErrorCode.cs`
- Create: `foundation\src\Qavren.Edge.Core\EdgeExceptions.cs`
- Create: `foundation\src\Qavren.Edge.Core\EdgeEventIds.cs`
- Create: `foundation\src\Qavren.Edge.Core\EdgeOptions.cs`
- Create: `foundation\src\Qavren.Edge.Core\IEdgePaths.cs`
- Create: `foundation\src\Qavren.Edge.Core\Hosting\IEdgeHost.cs`
- Create: `foundation\src\Qavren.Edge.Core\Hosting\IEdgeStartupTask.cs`
- Create: `foundation\src\Qavren.Edge.Core\Hosting\EdgeBuilder.cs`
- Create: `foundation\src\Qavren.Edge.Core\Lifecycle\IEdgeLifecycle.cs`
- Create: `foundation\src\Qavren.Edge.Core\Diagnostics\IEdgeDiagnostics.cs`
- Test: `foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj`
- Test: `foundation\tests\Qavren.Edge.Core.Tests\ExceptionTests.cs`
- Test: `foundation\tests\Qavren.Edge.Core.Tests\PathsTests.cs`

All paths are relative to `C:\Users\steve\projects\qavren-edge\`.

- [ ] **Step 1: Write the test project and the failing tests**

`foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Qavren.Edge.Core\Qavren.Edge.Core.csproj" />
  </ItemGroup>
</Project>
```

`foundation\tests\Qavren.Edge.Core.Tests\ExceptionTests.cs`:

```csharp
using Xunit;

namespace Qavren.Edge.Core.Tests;

public class ExceptionTests
{
    [Fact]
    public void ConfigurationException_CarriesCodeAndHelpLink()
    {
        var ex = new EdgeConfigurationException(EdgeErrorCode.DuplicateDatabaseName, "dupe");

        Assert.Equal(EdgeErrorCode.DuplicateDatabaseName, ex.Code);
        Assert.Equal("dupe", ex.Message);
        Assert.EndsWith("#1001", ex.HelpLink, StringComparison.Ordinal);
        Assert.IsAssignableFrom<EdgeException>(ex);
    }

    [Fact]
    public void NativeException_MessageNamesRidLibraryAndProbedPaths()
    {
        var ex = new EdgeNativeException("win-x64", "qedge_sqlite3", ["C:\\a", "C:\\b"], "reference Qavren.Edge.Sqlite.Native");

        Assert.Equal(EdgeErrorCode.NativeLoadFailed, ex.Code);
        Assert.Contains("win-x64", ex.Message, StringComparison.Ordinal);
        Assert.Contains("qedge_sqlite3", ex.Message, StringComparison.Ordinal);
        Assert.Contains("C:\\b", ex.Message, StringComparison.Ordinal);
        Assert.Contains("reference Qavren.Edge.Sqlite.Native", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["C:\\a", "C:\\b"], ex.ProbedPaths);
    }

    [Fact]
    public void MigrationException_CarriesVersionNameAndInner()
    {
        var inner = new InvalidOperationException("no such table");
        var ex = new EdgeMigrationException(3, "create notes", inner);

        Assert.Equal(EdgeErrorCode.MigrationFailed, ex.Code);
        Assert.Equal(3, ex.Version);
        Assert.Equal("create notes", ex.Name);
        Assert.Same(inner, ex.InnerException);
        Assert.Contains("3", ex.Message, StringComparison.Ordinal);
        Assert.Contains("create notes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DatabaseKeyException_UsesKeyRejectedCode()
    {
        var ex = new EdgeDatabaseKeyException("notes.db", null);

        Assert.Equal(EdgeErrorCode.DatabaseKeyRejected, ex.Code);
        Assert.Contains("notes.db", ex.Message, StringComparison.Ordinal);
    }
}
```

`foundation\tests\Qavren.Edge.Core.Tests\PathsTests.cs`:

```csharp
using Xunit;

namespace Qavren.Edge.Core.Tests;

public class PathsTests
{
    [Fact]
    public void DefaultPaths_AreUnderLocalApplicationDataAndCreated()
    {
        var appName = "QavrenEdgeTest_" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            appName,
            "qavren-edge");
        try
        {
            IEdgePaths paths = new DefaultEdgePaths(appName);

            Assert.Equal(root, paths.Data);
            Assert.Equal(Path.Combine(root, "cache"), paths.Cache);
            Assert.True(Directory.Exists(paths.Data));
            Assert.True(Directory.Exists(paths.Cache));
        }
        finally
        {
            var appRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName);
            if (Directory.Exists(appRoot))
            {
                Directory.Delete(appRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void EdgeOptions_HaveDocumentedDefaults()
    {
        var options = new EdgeOptions();

        Assert.True(options.FreezeSqliteProvider);
        Assert.Equal(64, options.LifecycleHistoryCapacity);
        Assert.False(string.IsNullOrWhiteSpace(options.AppName));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj" -c Release -f net10.0
```

Expected: FAIL — the `Qavren.Edge.Core` project does not exist yet (`MSB3202: The project file ... was not found`).

- [ ] **Step 3: Write the Core project file**

`foundation\src\Qavren.Edge.Core\Qavren.Edge.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>true</IsPackable>
    <PackageId>Qavren.Edge.Core</PackageId>
    <Description>DI-first hosting core for Qavren.Edge: builder, ordered startup tasks, lifecycle hub, paths, and diagnostics.</Description>
    <RootNamespace>Qavren.Edge</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Write `EdgeErrorCode.cs`**

```csharp
namespace Qavren.Edge;

/// <summary>Stable, programmatically handleable error identity for every <see cref="EdgeException"/>.</summary>
public enum EdgeErrorCode
{
    Unknown = 0,

    DuplicateDatabaseName = 1001,
    NoNativeProviderRegistered = 1002,
    MultipleNativeProvidersRegistered = 1003,
    EncryptionKeyWithoutCipherProvider = 1004,
    EncryptionKeyMissing = 1005,

    NativeLoadFailed = 2001,
    NativeVerificationFailed = 2002,

    MigrationFailed = 3001,
    MigrationVersionConflict = 3002,

    DatabaseKeyRejected = 4001,
}
```

- [ ] **Step 5: Write `EdgeExceptions.cs`**

```csharp
using System.Globalization;

namespace Qavren.Edge;

/// <summary>Base type for every fault Qavren.Edge raises deliberately.</summary>
public abstract class EdgeException : Exception
{
    private const string DocsBase =
        "https://github.com/qavren-oss/qavren-edge/blob/main/foundation/docs/errors.md#";

    protected EdgeException(EdgeErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        HelpLink = DocsBase + ((int)code).ToString(CultureInfo.InvariantCulture);
    }

    public EdgeErrorCode Code { get; }
}

/// <summary>Bad wiring: detected either when the container is built or by startup task 0.</summary>
public sealed class EdgeConfigurationException : EdgeException
{
    public EdgeConfigurationException(EdgeErrorCode code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }
}

/// <summary>The native SQLite library could not be loaded or failed verification.</summary>
public sealed class EdgeNativeException : EdgeException
{
    public EdgeNativeException(
        string runtimeIdentifier,
        string libraryName,
        IReadOnlyList<string> probedPaths,
        string remediation,
        Exception? innerException = null)
        : base(EdgeErrorCode.NativeLoadFailed, Build(runtimeIdentifier, libraryName, probedPaths, remediation), innerException)
    {
        RuntimeIdentifier = runtimeIdentifier;
        LibraryName = libraryName;
        ProbedPaths = probedPaths;
        Remediation = remediation;
    }

    public string RuntimeIdentifier { get; }

    public string LibraryName { get; }

    public IReadOnlyList<string> ProbedPaths { get; }

    public string Remediation { get; }

    private static string Build(string rid, string library, IReadOnlyList<string> probed, string remediation)
    {
        var paths = probed.Count == 0 ? "(none)" : string.Join(Environment.NewLine + "  - ", probed);
        return $"Failed to load native SQLite library '{library}' for runtime identifier '{rid}'." +
               Environment.NewLine + "Probed:" + Environment.NewLine + "  - " + paths +
               Environment.NewLine + "Remediation: " + remediation;
    }
}

/// <summary>A migration failed; the database is left at the last successful user_version.</summary>
public sealed class EdgeMigrationException : EdgeException
{
    public EdgeMigrationException(int version, string name, Exception? innerException = null)
        : base(EdgeErrorCode.MigrationFailed,
               $"Migration {version.ToString(CultureInfo.InvariantCulture)} '{name}' failed.",
               innerException)
    {
        Version = version;
        Name = name;
    }

    public int Version { get; }

    public string Name { get; }
}

/// <summary>An encrypted database refused the supplied key.</summary>
public sealed class EdgeDatabaseKeyException : EdgeException
{
    public EdgeDatabaseKeyException(string databaseName, Exception? innerException = null)
        : base(EdgeErrorCode.DatabaseKeyRejected,
               $"Database '{databaseName}' could not be opened with the supplied key. " +
               "Either the key is wrong or the file is not a SQLCipher database.",
               innerException)
    {
        DatabaseName = databaseName;
    }

    public string DatabaseName { get; }
}
```

- [ ] **Step 6: Write `EdgeEventIds.cs`**

```csharp
using Microsoft.Extensions.Logging;

namespace Qavren.Edge;

/// <summary>Stable logging event ids. The numeric ranges mirror <see cref="EdgeErrorCode"/>.</summary>
public static class EdgeEventIds
{
    public static readonly EventId StartupBegan = new(100, nameof(StartupBegan));
    public static readonly EventId StartupTaskCompleted = new(101, nameof(StartupTaskCompleted));
    public static readonly EventId StartupFailed = new(102, nameof(StartupFailed));
    public static readonly EventId StartupCompleted = new(103, nameof(StartupCompleted));

    public static readonly EventId LifecycleRaised = new(200, nameof(LifecycleRaised));
    public static readonly EventId LifecycleObserverFailed = new(201, nameof(LifecycleObserverFailed));

    public static readonly EventId NativeProviderInstalled = new(300, nameof(NativeProviderInstalled));
    public static readonly EventId NativeProviderFailed = new(301, nameof(NativeProviderFailed));

    public static readonly EventId MigrationApplied = new(400, nameof(MigrationApplied));
    public static readonly EventId MigrationFailed = new(401, nameof(MigrationFailed));

    public static readonly EventId CheckpointFailed = new(500, nameof(CheckpointFailed));
}
```

- [ ] **Step 7: Write `EdgeOptions.cs`**

```csharp
namespace Qavren.Edge;

/// <summary>Options for the Qavren.Edge host itself. Bound as <c>IOptions&lt;EdgeOptions&gt;</c>.</summary>
public sealed class EdgeOptions
{
    /// <summary>Used to build the default data and cache directories. Defaults to the process friendly name.</summary>
    public string AppName { get; set; } = AppDomain.CurrentDomain.FriendlyName;

    /// <summary>
    /// Calls <c>SQLitePCL.raw.FreezeProvider()</c> immediately after installing the native provider.
    /// Defaults to <see langword="true"/>: <c>SqliteConnection</c>'s static constructor reflectively
    /// invokes <c>SQLitePCL.Batteries_V2.Init()</c>, which would otherwise silently replace the provider
    /// if any transitive package ever brings a batteries assembly into the app. Tests set this to
    /// <see langword="false"/> so they can swap providers.
    /// </summary>
    public bool FreezeSqliteProvider { get; set; } = true;

    /// <summary>How many recent lifecycle events the hub keeps for diagnostics.</summary>
    public int LifecycleHistoryCapacity { get; set; } = 64;
}
```

- [ ] **Step 8: Write `IEdgePaths.cs`**

```csharp
namespace Qavren.Edge;

/// <summary>Where Qavren.Edge stores durable data and disposable caches.</summary>
public interface IEdgePaths
{
    /// <summary>Durable storage. Backed up by the platform where the platform backs anything up.</summary>
    string Data { get; }

    /// <summary>Disposable storage. The OS may delete this at any time.</summary>
    string Cache { get; }
}

/// <summary>
/// Non-MAUI default: <c>LocalApplicationData/&lt;AppName&gt;/qavren-edge</c> with a
/// <c>cache</c> subdirectory. Both directories are created on construction.
/// </summary>
public sealed class DefaultEdgePaths : IEdgePaths
{
    public DefaultEdgePaths(string appName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);

        Data = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            appName,
            "qavren-edge");
        Cache = Path.Combine(Data, "cache");

        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Cache);
    }

    public string Data { get; }

    public string Cache { get; }
}
```

- [ ] **Step 9: Write `Hosting\IEdgeHost.cs`**

```csharp
namespace Qavren.Edge.Hosting;

/// <summary>
/// Owns one-time startup. Every Qavren.Edge service entry point calls
/// <see cref="EnsureStartedAsync"/> first, so a startup failure surfaces on first use
/// with its real cause and no caller ever races migrations.
/// </summary>
public interface IEdgeHost
{
    /// <summary>Begins startup exactly once. Safe to call repeatedly and from any thread. Never blocks.</summary>
    void Start();

    /// <summary>Completes when every startup task has finished; faults with the first failure.</summary>
    Task Started { get; }

    /// <summary>Calls <see cref="Start"/> if needed, awaits <see cref="Started"/>, and rethrows its fault.</summary>
    ValueTask EnsureStartedAsync(CancellationToken cancellationToken = default);

    /// <summary>Raises <c>Stopping</c> on the lifecycle hub and disposes owned resources.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
```

- [ ] **Step 10: Write `Hosting\IEdgeStartupTask.cs`**

```csharp
namespace Qavren.Edge.Hosting;

/// <summary>A unit of work run once, sequentially, during host startup.</summary>
public interface IEdgeStartupTask
{
    /// <summary>Ascending run order. Ties resolve in DI registration order. See <see cref="EdgeStartupOrder"/>.</summary>
    int Order { get; }

    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>Reserved startup orders. Consumer tasks should use <see cref="ConsumerDefault"/> or higher.</summary>
public static class EdgeStartupOrder
{
    public const int NativeProviderInstall = 0;
    public const int DatabaseOpen = 10;
    public const int Migrations = 100;
    public const int ConsumerDefault = 1000;
}
```

- [ ] **Step 11: Write `Hosting\EdgeBuilder.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Qavren.Edge.Hosting;

/// <summary>
/// The single chaining surface every Qavren.Edge package extends. It is a thin wrapper over
/// <see cref="IServiceCollection"/> and holds no state of its own.
/// </summary>
public sealed class EdgeBuilder
{
    public EdgeBuilder(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
    }

    public IServiceCollection Services { get; }
}
```

- [ ] **Step 12: Write `Lifecycle\IEdgeLifecycle.cs`**

```csharp
namespace Qavren.Edge.Lifecycle;

public enum EdgeMemoryPressure
{
    Low,
    Moderate,
    Critical,
}

public enum EdgeLifecycleEventKind
{
    Sleeping,
    Resumed,
    MemoryPressure,
    Stopping,
}

/// <summary>One entry in the hub's rolling history, surfaced by diagnostics.</summary>
public sealed record EdgeLifecycleRecord(
    DateTimeOffset Timestamp,
    EdgeLifecycleEventKind Kind,
    EdgeMemoryPressure? Level,
    TimeSpan Duration,
    int ObserverFailures);

/// <summary>
/// A hub, not a platform abstraction: anything may raise, anything may observe.
/// Raise methods complete when every observer has finished, so a platform bridge can
/// await a WAL checkpoint before the OS suspends the app.
/// </summary>
public interface IEdgeLifecycle
{
    Task RaiseSleepingAsync(CancellationToken cancellationToken = default);

    Task RaiseResumedAsync(CancellationToken cancellationToken = default);

    Task RaiseMemoryPressureAsync(EdgeMemoryPressure level, CancellationToken cancellationToken = default);

    Task RaiseStoppingAsync(CancellationToken cancellationToken = default);

    /// <summary>Most recent first, capped by <see cref="EdgeOptions.LifecycleHistoryCapacity"/>.</summary>
    IReadOnlyList<EdgeLifecycleRecord> RecentEvents { get; }
}

/// <summary>Resolved from DI; observers run in registration order, each inside its own try/catch.</summary>
public interface IEdgeLifecycleObserver
{
    Task OnSleepingAsync(CancellationToken cancellationToken);

    Task OnResumedAsync(CancellationToken cancellationToken);

    Task OnMemoryPressureAsync(EdgeMemoryPressure level, CancellationToken cancellationToken);

    Task OnStoppingAsync(CancellationToken cancellationToken);
}

/// <summary>No-op base so an observer only overrides the events it cares about.</summary>
public abstract class EdgeLifecycleObserver : IEdgeLifecycleObserver
{
    public virtual Task OnSleepingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual Task OnResumedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual Task OnMemoryPressureAsync(EdgeMemoryPressure level, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public virtual Task OnStoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Step 13: Write `Diagnostics\IEdgeDiagnostics.cs`**

```csharp
using Qavren.Edge.Lifecycle;

namespace Qavren.Edge.Diagnostics;

public sealed record EdgeComponentReport(
    string Name,
    string? Version,
    IReadOnlyDictionary<string, string?> Details);

public sealed record EdgeStartupTaskReport(
    string Name,
    int Order,
    TimeSpan Duration,
    string? Error);

/// <summary>Everything a support request needs, in one object.</summary>
public sealed record EdgeDiagnosticsReport(
    IReadOnlyList<EdgeComponentReport> Components,
    IReadOnlyDictionary<string, string?> Native,
    IReadOnlyDictionary<string, string> Paths,
    IReadOnlyList<EdgeStartupTaskReport> Startup,
    IReadOnlyList<EdgeLifecycleRecord> Lifecycle);

public interface IEdgeDiagnostics
{
    EdgeDiagnosticsReport Report();
}

/// <summary>Implemented by any component that wants to appear in <see cref="EdgeDiagnosticsReport.Components"/>.</summary>
public interface IEdgeDiagnosticsContributor
{
    string ComponentName { get; }

    string? ComponentVersion { get; }

    IReadOnlyDictionary<string, string?> Describe();
}
```

- [ ] **Step 14: Run the tests to verify they pass**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj" -c Release -f net10.0
```

Expected: all 6 tests pass, process exit code 0.

- [ ] **Step 15: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj" -c Release -f net10.0
```

Expected: `Passed! - Failed: 0, Passed: 6`, exit code 0.

---

---

### Task 2.2: `ProviderGen` tool and `provider.manifest.json`

**Local-verifiable:** yes.

**Approach — read this before writing code.** The manifest is **transcribed, never invented**. `ISQLite3Provider` lives at `src/SQLitePCLRaw.core/isqlite3.cs` and is byte-identical between tags `v2.1.12` and `v3.0.5`. ProviderGen therefore has two commands:

- `manifest` — loads the referenced `SQLitePCLRaw.core` 3.0.5 assembly, reflects over `SQLitePCL.ISQLite3Provider`, and writes `provider.manifest.json` containing every member's exact signature string. Nothing is hand-typed, so nothing can be invented.
- `generate` — renders `Generated/SQLite3Provider_qedge.g.cs` from a vendored upstream template (Task 3.2) by substituting the class name, the `SQLITE_DLL` constant, and the reported library name.

Correctness of the reflected manifest is pinned by a spot-check list transcribed verbatim from the research's `isqlite3.cs` listing. If upstream ever changes the interface, `manifest` emits a different file and the CI drift check (Task 5.3) fails.

**Files:**
- Create: `foundation\tools\ProviderGen\ProviderGen.csproj`
- Create: `foundation\tools\ProviderGen\Program.cs`
- Create: `foundation\tools\ProviderGen\ManifestBuilder.cs`
- Create: `foundation\tools\ProviderGen\SpotCheck.cs`
- Create (generated in Step 5): `foundation\tools\ProviderGen\provider.manifest.json`

- [ ] **Step 1: Write the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <RootNamespace>Qavren.Edge.ProviderGen</RootNamespace>
    <AssemblyName>providergen</AssemblyName>
    <!-- Reflection over ISQLite3Provider is the whole point of this tool. -->
    <IsAotCompatible>false</IsAotCompatible>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SQLitePCLRaw.core" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write `SpotCheck.cs` (the verbatim transcription)**

These 18 signatures are copied character-for-character from `SQLitePCLRaw.core/isqlite3.cs` at tag `v3.0.5`. They are the anti-invention guard: if the reflected manifest does not contain each of them, `providergen manifest` fails loudly rather than writing a plausible-looking file.

```csharp
namespace Qavren.Edge.ProviderGen;

/// <summary>
/// Signatures transcribed verbatim from ericsink/SQLitePCL.raw
/// src/SQLitePCLRaw.core/isqlite3.cs at tag v3.0.5 (byte-identical to v2.1.12).
/// Format matches <see cref="ManifestBuilder.FormatMember"/>: "ReturnType Name(ParamType name, ...)".
/// </summary>
internal static class SpotCheck
{
    internal static readonly string[] ExpectedSignatures =
    [
        "System.String GetNativeLibraryName()",
        "System.Int32 sqlite3_open(SQLitePCL.utf8z filename, System.IntPtr& db)",
        "System.Int32 sqlite3_open_v2(SQLitePCL.utf8z filename, System.IntPtr& db, System.Int32 flags, SQLitePCL.utf8z vfs)",
        "System.Int32 sqlite3_close_v2(System.IntPtr db)",
        "System.Int32 sqlite3_prepare_v2(SQLitePCL.sqlite3 db, System.ReadOnlySpan`1[System.Byte] sql, System.IntPtr& stmt, System.ReadOnlySpan`1[System.Byte]& remain)",
        "System.Int32 sqlite3_step(SQLitePCL.sqlite3_stmt stmt)",
        "System.Int32 sqlite3_finalize(System.IntPtr stmt)",
        "System.Int32 sqlite3_bind_blob(SQLitePCL.sqlite3_stmt stmt, System.Int32 index, System.ReadOnlySpan`1[System.Byte] blob)",
        "System.ReadOnlySpan`1[System.Byte] sqlite3_column_blob(SQLitePCL.sqlite3_stmt stmt, System.Int32 index)",
        "SQLitePCL.utf8z sqlite3_column_text(SQLitePCL.sqlite3_stmt stmt, System.Int32 index)",
        "SQLitePCL.utf8z sqlite3_errmsg(SQLitePCL.sqlite3 db)",
        "System.Int32 sqlite3_libversion_number()",
        "SQLitePCL.utf8z sqlite3_libversion()",
        "System.Int32 sqlite3_key(SQLitePCL.sqlite3 db, System.ReadOnlySpan`1[System.Byte] key)",
        "System.Int32 sqlite3_key_v2(SQLitePCL.sqlite3 db, SQLitePCL.utf8z dbname, System.ReadOnlySpan`1[System.Byte] key)",
        "System.Int32 sqlite3_rekey_v2(SQLitePCL.sqlite3 db, SQLitePCL.utf8z dbname, System.ReadOnlySpan`1[System.Byte] key)",
        "System.Int32 sqlite3_enable_load_extension(SQLitePCL.sqlite3 db, System.Int32 enable)",
        "System.Int32 sqlite3_win32_set_directory(System.Int32 typ, SQLitePCL.utf8z path)",
    ];
}
```

- [ ] **Step 3: Write `ManifestBuilder.cs`**

```csharp
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Qavren.Edge.ProviderGen;

internal static class ManifestBuilder
{
    /// <summary>
    /// Fully qualified CLR type name without assembly qualification. <see cref="Type.FullName"/>
    /// renders a constructed generic's arguments assembly-qualified
    /// ("System.ReadOnlySpan`1[[System.Byte, System.Private.CoreLib, Version=...]]"), which would bake
    /// the running runtime's version into the manifest. This renders "System.ReadOnlySpan`1[System.Byte]".
    /// </summary>
    internal static string FormatType(Type t)
    {
        if (t.IsByRef)
        {
            return FormatType(t.GetElementType()!) + "&";
        }

        if (t.IsPointer)
        {
            return FormatType(t.GetElementType()!) + "*";
        }

        if (t.IsArray)
        {
            return FormatType(t.GetElementType()!) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
        }

        if (t.IsConstructedGenericType)
        {
            var args = string.Join(",", t.GetGenericArguments().Select(FormatType));
            return t.GetGenericTypeDefinition().FullName + "[" + args + "]";
        }

        return t.FullName ?? t.Name;
    }

    /// <summary>"ReturnType Name(ParamType name, ...)" using fully qualified CLR type names.</summary>
    internal static string FormatMember(MethodInfo m)
    {
        var sb = new StringBuilder();
        sb.Append(FormatType(m.ReturnType)).Append(' ').Append(m.Name).Append('(');
        var ps = m.GetParameters();
        for (var i = 0; i < ps.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(FormatType(ps[i].ParameterType)).Append(' ').Append(ps[i].Name);
        }

        return sb.Append(')').ToString();
    }

    internal static JsonObject Build()
    {
        var iface = typeof(SQLitePCL.ISQLite3Provider);
        var core = iface.Assembly.GetName();

        var members = iface.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(FormatMember)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        var missing = SpotCheck.ExpectedSignatures
            .Where(expected => !members.Contains(expected, StringComparer.Ordinal))
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "ISQLite3Provider no longer matches the transcribed v3.0.5 surface. Missing:" +
                Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", missing));
        }

        var manifest = new JsonObject
        {
            ["$comment"] =
                "GENERATED by 'providergen manifest'. Do not hand-edit. " +
                "Members are reflected from SQLitePCL.ISQLite3Provider and validated against the " +
                "verbatim v3.0.5 signatures in SpotCheck.cs.",
            ["sqlitePclRawCoreVersion"] = core.Version?.ToString(),
            ["providerClassName"] = "SQLite3Provider_qedge",
            ["providerNamespace"] = "Qavren.Edge.Sqlite.Provider",
            ["defaultReportedLibraryName"] = "qedge_sqlite3",
            ["libraryNameByTargetFramework"] = new JsonObject
            {
                ["net10.0"] = "qedge_sqlite3",
                ["net10.0-ios"] = "__Internal",
                ["net10.0-maccatalyst"] = "qedge_sqlite3",
                ["net10.0-android"] = "qedge_sqlite3",
                ["net10.0-windows"] = "qedge_sqlite3",
            },
            ["memberCount"] = members.Length,
            ["members"] = new JsonArray([.. members.Select(m => (JsonNode)JsonValue.Create(m)!)]),
        };

        return manifest;
    }

    internal static string Serialize(JsonObject manifest)
        => manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
}
```

- [ ] **Step 4: Write `Program.cs`**

The `generate` command is implemented in Task 3.2, once the upstream template is vendored; until then it exits non-zero with a clear message rather than pretending to work.

```csharp
using System.Text.Json.Nodes;

namespace Qavren.Edge.ProviderGen;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: providergen manifest --out <path> [--check]");
            Console.Error.WriteLine("       providergen generate --manifest <path> --template <path> --out <path> [--check]");
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "manifest" => Manifest(args),
                "generate" => Generate(args),
                _ => Fail($"unknown command '{args[0]}'"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static string Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        if (i < 0 || i + 1 >= args.Length)
        {
            throw new InvalidOperationException($"missing required argument {name}");
        }

        return args[i + 1];
    }

    private static int Manifest(string[] args)
    {
        var outPath = Arg(args, "--out");
        var text = ManifestBuilder.Serialize(ManifestBuilder.Build());
        return Emit(args, outPath, text);
    }

    private static int Generate(string[] args)
    {
        var manifestPath = Arg(args, "--manifest");
        var templatePath = Arg(args, "--template");
        var outPath = Arg(args, "--out");

        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var template = File.ReadAllText(templatePath);
        var text = ProviderRenderer.Render(template, manifest);
        return Emit(args, outPath, text);
    }

    private static int Emit(string[] args, string outPath, string text)
    {
        var check = args.Contains("--check");
        if (check)
        {
            if (!File.Exists(outPath))
            {
                Console.Error.WriteLine($"DRIFT: {outPath} does not exist.");
                return 1;
            }

            var existing = File.ReadAllText(outPath);
            if (!string.Equals(existing.ReplaceLineEndings("\n"), text.ReplaceLineEndings("\n"), StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"DRIFT: {outPath} differs from freshly generated output.");
                return 1;
            }

            Console.WriteLine($"OK: {outPath} is up to date.");
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, text);
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }
}
```

- [ ] **Step 5: Write `ProviderRenderer.cs` (stub for now; Task 3.2 supplies the template)**

Create `foundation\tools\ProviderGen\ProviderRenderer.cs`:

```csharp
using System.Text.Json.Nodes;

namespace Qavren.Edge.ProviderGen;

internal static class ProviderRenderer
{
    /// <summary>
    /// The template is upstream's Apache-2.0 licensed generated provider
    /// (SQLitePCLRaw.provider.internal/Generated/provider_internal_funcptrs.cs at tag v3.0.5),
    /// vendored under foundation/src/Qavren.Edge.Sqlite.Provider/Template/. Substitutions are
    /// mechanical string replacements so the diff against upstream stays reviewable.
    /// </summary>
    internal static string Render(string template, JsonObject manifest)
    {
        var className = (string)manifest["providerClassName"]!;
        var ns = (string)manifest["providerNamespace"]!;

        var rendered = template
            .Replace("namespace SQLitePCL", "namespace " + ns, StringComparison.Ordinal)
            .Replace("SQLite3Provider_internal", className, StringComparison.Ordinal)
            .Replace("private const string SQLITE_DLL = \"__Internal\";",
                     "private const string SQLITE_DLL = QedgeNativeLibrary.DllImportName;",
                     StringComparison.Ordinal)
            .Replace("string ISQLite3Provider.GetNativeLibraryName()\n\t\t{\n\t\t\treturn \"__Internal\";\n\t\t}",
                     "string ISQLite3Provider.GetNativeLibraryName()\n\t\t{\n\t\t\treturn QedgeNativeLibrary.ReportedName;\n\t\t}",
                     StringComparison.Ordinal);

        if (rendered.Contains("__Internal", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Template still contains '__Internal' after substitution; the upstream file shape changed.");
        }

        return "// <auto-generated>" + Environment.NewLine +
               "// Generated by foundation/tools/ProviderGen. Do not edit." + Environment.NewLine +
               "// Derived from ericsink/SQLitePCL.raw v3.0.5, Apache-2.0. See THIRD-PARTY-NOTICES.md." + Environment.NewLine +
               "// </auto-generated>" + Environment.NewLine +
               rendered;
    }
}
```

- [ ] **Step 6: Generate the manifest and inspect it**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tools\ProviderGen\ProviderGen.csproj" -c Release -- manifest --out "C:\Users\steve\projects\qavren-edge\foundation\tools\ProviderGen\provider.manifest.json"
```

Expected: `wrote ...provider.manifest.json`. Then confirm the file names a plausible member count and contains all 18 spot-check signatures:

```powershell
pwsh -NoProfile -Command "$m = Get-Content 'C:\Users\steve\projects\qavren-edge\foundation\tools\ProviderGen\provider.manifest.json' -Raw | ConvertFrom-Json; Write-Host ('memberCount=' + $m.memberCount); if ($m.memberCount -lt 120) { Write-Error 'manifest looks truncated' } else { Write-Host 'OK' }"
```

Expected: `memberCount=` a value of at least 120 (the interface has well over a hundred members), then `OK`. Record the exact number in the commit message; from here on it is pinned by the checked-in manifest and any change is drift.

- [ ] **Step 7: Verify (idempotence — regenerating produces no diff)**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tools\ProviderGen\ProviderGen.csproj" -c Release -- manifest --out "C:\Users\steve\projects\qavren-edge\foundation\tools\ProviderGen\provider.manifest.json" --check
```

Expected: `OK: ...provider.manifest.json is up to date.`, exit code 0.

---

---

### Task 2.3: Native sources, pins, and the CMake project

**Local-verifiable:** yes (fetch + configure only; compiling happens in Task 3.3).

**Files:**
- Create: `foundation\native\versions.json`
- Create: `foundation\native\README.md`
- Create: `foundation\native\src\qedge_init.c`
- Create: `foundation\native\CMakeLists.txt`
- Create: `foundation\native\cmake\ltc-sources.py`
- Create: `foundation\native\scripts\fetch-sources.ps1`
- Create: `C:\Users\steve\projects\qavren-edge\THIRD-PARTY-NOTICES.md`

- [ ] **Step 1: Write `foundation\native\versions.json`**

The SQLite hash is SHA3-256 because sqlite.org publishes **only** SHA3-256 (PowerShell's `Get-FileHash` has no SHA3 algorithm, so the fetch script hashes with python). The other three hashes are seeded in Step 6.

```json
{
  "$comment": "Upstream C source pins. Dependabot cannot see these; upstream-pins.yml opens a monthly issue listing newer tags.",
  "sqlite": {
    "version": "3.53.4",
    "url": "https://sqlite.org/2026/sqlite-amalgamation-3530400.zip",
    "archive": "sqlite-amalgamation-3530400.zip",
    "extractedDir": "sqlite-amalgamation-3530400",
    "hashAlgorithm": "sha3_256",
    "hash": "628a44cfe82c66aed1ccbbe85a562d2e33ebe64b3288981ed76285612227934e"
  },
  "sqliteVec": {
    "version": "0.1.9",
    "tag": "v0.1.9",
    "url": "https://github.com/asg017/sqlite-vec/releases/download/v0.1.9/sqlite-vec-0.1.9-amalgamation.tar.gz",
    "archive": "sqlite-vec-0.1.9-amalgamation.tar.gz",
    "extractedDir": "sqlite-vec",
    "hashAlgorithm": "sha256",
    "hash": "SEED_ME"
  },
  "sqlcipher": {
    "version": "4.19.0",
    "tag": "v4.19.0",
    "url": "https://github.com/sqlcipher/sqlcipher/archive/refs/tags/v4.19.0.tar.gz",
    "archive": "sqlcipher-4.19.0.tar.gz",
    "extractedDir": "sqlcipher-4.19.0",
    "hashAlgorithm": "sha256",
    "hash": "SEED_ME"
  },
  "libtomcrypt": {
    "version": "1.18.2",
    "tag": "v1.18.2",
    "url": "https://github.com/libtom/libtomcrypt/archive/refs/tags/v1.18.2.tar.gz",
    "archive": "libtomcrypt-1.18.2.tar.gz",
    "extractedDir": "libtomcrypt-1.18.2",
    "hashAlgorithm": "sha256",
    "hash": "SEED_ME"
  }
}
```

- [ ] **Step 2: Write `foundation\native\scripts\fetch-sources.ps1`**

```powershell
#requires -Version 7
<#
.SYNOPSIS
  Downloads and verifies the pinned upstream C sources into foundation/native/_deps.
.PARAMETER UpdateHashes
  Recomputes every hash and rewrites versions.json. Use once to seed, then never again
  without reading the diff.
#>
[CmdletBinding()]
param(
    [switch]$UpdateHashes,
    [switch]$IncludeCipher
)

$ErrorActionPreference = 'Stop'
$nativeRoot   = Split-Path -Parent $PSScriptRoot
$versionsPath = Join-Path $nativeRoot 'versions.json'
$depsRoot     = Join-Path $nativeRoot '_deps'
$downloadRoot = Join-Path $depsRoot 'download'

New-Item -ItemType Directory -Force -Path $depsRoot, $downloadRoot | Out-Null

$versions = Get-Content $versionsPath -Raw | ConvertFrom-Json
$names = if ($IncludeCipher) { 'sqlite', 'sqliteVec', 'sqlcipher', 'libtomcrypt' } else { 'sqlite', 'sqliteVec' }

function Get-Digest {
    param([string]$Path, [string]$Algorithm)
    $script = "import hashlib,sys; h=hashlib.new(sys.argv[2]); h.update(open(sys.argv[1],'rb').read()); print(h.hexdigest())"
    $value = & python -c $script $Path $Algorithm
    if ($LASTEXITCODE -ne 0) { throw "python hashing failed for $Path" }
    return $value.Trim()
}

foreach ($name in $names) {
    $entry   = $versions.$name
    $archive = Join-Path $downloadRoot $entry.archive

    if (-not (Test-Path $archive)) {
        Write-Host "downloading $($entry.url)"
        Invoke-WebRequest -Uri $entry.url -OutFile $archive -UseBasicParsing
    }

    $actual = Get-Digest -Path $archive -Algorithm $entry.hashAlgorithm

    if ($UpdateHashes) {
        Write-Host "$name $($entry.hashAlgorithm) = $actual"
        $entry.hash = $actual
    }
    elseif ($entry.hash -ne $actual) {
        throw "HASH MISMATCH for $name`n  expected $($entry.hash)`n  actual   $actual`n  file     $archive"
    }

    $target = Join-Path $depsRoot $entry.extractedDir
    if (-not (Test-Path $target)) {
        Write-Host "extracting $($entry.archive)"
        if ($entry.archive.EndsWith('.zip')) {
            Expand-Archive -Path $archive -DestinationPath $depsRoot -Force
        }
        else {
            # sqlite-vec's amalgamation has no directory prefix, so give it its own folder.
            $dest = if ($name -eq 'sqliteVec') { $target } else { $depsRoot }
            New-Item -ItemType Directory -Force -Path $dest | Out-Null
            & tar -xzf $archive -C $dest
            if ($LASTEXITCODE -ne 0) { throw "tar failed for $archive" }
        }
    }

    if (-not (Test-Path $target)) { throw "expected $target after extracting $($entry.archive)" }
}

if ($UpdateHashes) {
    $versions | ConvertTo-Json -Depth 8 | Set-Content -Path $versionsPath -Encoding utf8
    Write-Host "versions.json updated - review the diff before committing"
}

Write-Host "OK: sources present under $depsRoot"
```

- [ ] **Step 3: Write `foundation\native\src\qedge_init.c`**

```c
/*
 * qedge_init.c - the single SQLITE_EXTRA_INIT hook for Qavren.Edge.
 *
 * SQLCipher 4.19 hard-#errors unless SQLITE_EXTRA_INIT and SQLITE_EXTRA_SHUTDOWN are
 * defined, and sqlite-vec's documented static-registration path also wants
 * SQLITE_EXTRA_INIT. Only one symbol can own it, so this file owns it and chains.
 *
 * SQLite calls SQLITE_EXTRA_INIT from sqlite3_initialize(), which runs before any
 * connection exists. sqlite3_auto_extension therefore applies to every physical
 * connection, including ones Microsoft.Data.Sqlite opens from its pool.
 */
#include "sqlite3.h"

extern int sqlite3_vec_init(sqlite3 *db, char **pzErrMsg, const sqlite3_api_routines *pApi);

#ifdef QEDGE_CIPHER
extern int sqlcipher_extra_init(const char *arg);
extern void sqlcipher_extra_shutdown(void);
#endif

#ifndef QEDGE_SQLITE_VERSION
#define QEDGE_SQLITE_VERSION "unknown"
#endif
#ifndef QEDGE_VEC_VERSION
#define QEDGE_VEC_VERSION "unknown"
#endif
#ifndef QEDGE_CIPHER_VERSION
#define QEDGE_CIPHER_VERSION "none"
#endif
#ifndef QEDGE_BUILD_SHA
#define QEDGE_BUILD_SHA "local"
#endif

static const char qedge_version_string[] =
    "sqlite " QEDGE_SQLITE_VERSION
    " | vec " QEDGE_VEC_VERSION
    " | cipher " QEDGE_CIPHER_VERSION
    " | build " QEDGE_BUILD_SHA;

static void qedge_version_func(sqlite3_context *ctx, int argc, sqlite3_value **argv)
{
  (void)argc;
  (void)argv;
  sqlite3_result_text(ctx, qedge_version_string, -1, SQLITE_STATIC);
}

static int qedge_register_version(sqlite3 *db, char **pzErrMsg, const sqlite3_api_routines *pApi)
{
  (void)pzErrMsg;
  (void)pApi;
  return sqlite3_create_function(
      db, "qedge_version", 0,
      SQLITE_UTF8 | SQLITE_DETERMINISTIC | SQLITE_INNOCUOUS,
      0, qedge_version_func, 0, 0);
}

int qedge_extra_init(const char *unused)
{
  int rc;
  (void)unused;

#ifdef QEDGE_CIPHER
  rc = sqlcipher_extra_init(0);
  if (rc != SQLITE_OK)
  {
    return rc;
  }
#endif

  rc = sqlite3_auto_extension((void (*)(void))sqlite3_vec_init);
  if (rc != SQLITE_OK)
  {
    return rc;
  }

  return sqlite3_auto_extension((void (*)(void))qedge_register_version);
}

void qedge_extra_shutdown(void)
{
#ifdef QEDGE_CIPHER
  sqlcipher_extra_shutdown();
#endif
}
```

- [ ] **Step 4: Write `foundation\native\cmake\ltc-sources.py`**

libtomcrypt 1.18.2 has no CMake build. Rather than hand-listing files (which invents facts), derive the object list from upstream's own makefile and drop the public-key and math groups, which SQLCipher's LibTomCrypt provider never calls and which would need libtommath.

```python
"""Emit a CMake-style ;-separated source list for libtomcrypt from its own makefile.

Usage: python ltc-sources.py <libtomcrypt-root>
"""
import os
import re
import sys

EXCLUDE_PREFIXES = ("src/pk/", "src/math/", "src/encauth/", "src/headers/")

def main() -> int:
    root = sys.argv[1]
    makefile = os.path.join(root, "makefile")
    with open(makefile, "r", encoding="utf-8", errors="replace") as fh:
        text = fh.read()

    objects = re.findall(r"(src/[A-Za-z0-9_./-]+)\.o\b", text)
    seen, out = set(), []
    for obj in objects:
        rel = obj + ".c"
        if rel.startswith(EXCLUDE_PREFIXES) or rel in seen:
            continue
        if not os.path.isfile(os.path.join(root, rel)):
            continue
        seen.add(rel)
        out.append(os.path.join(root, rel).replace("\\", "/"))

    if len(out) < 50:
        print("ltc-sources: refusing to emit %d sources; makefile parse looks wrong" % len(out),
              file=sys.stderr)
        return 1

    sys.stdout.write(";".join(out))
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
```

- [ ] **Step 5: Write `foundation\native\CMakeLists.txt`**

```cmake
cmake_minimum_required(VERSION 3.24)
project(qedge_sqlite LANGUAGES C)

option(QEDGE_CIPHER "Build the SQLCipher + libtomcrypt variant" OFF)
set(QEDGE_BUILD_SHA "local" CACHE STRING "Build provenance recorded in qedge_version()")

set(CMAKE_C_STANDARD 99)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

set(DEPS "${CMAKE_CURRENT_SOURCE_DIR}/_deps")

# --- versions.json is the single source of truth for the version strings -----------------
file(READ "${CMAKE_CURRENT_SOURCE_DIR}/versions.json" QEDGE_VERSIONS_JSON)
string(JSON QEDGE_SQLITE_VERSION GET "${QEDGE_VERSIONS_JSON}" sqlite version)
string(JSON QEDGE_VEC_VERSION GET "${QEDGE_VERSIONS_JSON}" sqliteVec version)
string(JSON QEDGE_SQLCIPHER_VERSION GET "${QEDGE_VERSIONS_JSON}" sqlcipher version)
string(JSON QEDGE_SQLITE_DIR GET "${QEDGE_VERSIONS_JSON}" sqlite extractedDir)

# --- common SQLite compile configuration (spec 10.2) -------------------------------------
set(QEDGE_COMMON_DEFS
    SQLITE_ENABLE_FTS5
    SQLITE_ENABLE_RTREE
    SQLITE_ENABLE_MATH_FUNCTIONS
    SQLITE_ENABLE_DBSTAT_VTAB
    SQLITE_ENABLE_COLUMN_METADATA
    SQLITE_THREADSAFE=1
    SQLITE_DQS=0
    SQLITE_DEFAULT_WAL_SYNCHRONOUS=1
    SQLITE_USE_URI=1
    SQLITE_TEMP_STORE=2
    SQLITE_LIKE_DOESNT_MATCH_BLOBS
    SQLITE_OMIT_DEPRECATED
    SQLITE_OMIT_LOAD_EXTENSION
    SQLITE_DEFAULT_FOREIGN_KEYS=1
    SQLITE_EXTRA_INIT=qedge_extra_init
    SQLITE_EXTRA_SHUTDOWN=qedge_extra_shutdown
    # sqlite-vec: SQLITE_CORE is load-bearing (switches the include to sqlite3.h and drops
    # the SQLITE_EXTENSION_INIT1/2 thunk); SQLITE_VEC_STATIC only suppresses dllexport on Windows.
    SQLITE_CORE
    SQLITE_VEC_STATIC
    QEDGE_SQLITE_VERSION="${QEDGE_SQLITE_VERSION}"
    QEDGE_VEC_VERSION="v${QEDGE_VEC_VERSION}"
    QEDGE_BUILD_SHA="${QEDGE_BUILD_SHA}")

# sqlite-vec's SIMD kernels define PORTABLE_ALIGN32/64 as __attribute__((aligned(N))), which
# cl.exe rejects (C2143/C2065/C2168 at sqlite-vec.c:133+). Enable them on GCC/Clang toolchains
# only; MSVC builds use sqlite-vec's portable scalar paths.
if(NOT MSVC)
  if(CMAKE_SYSTEM_PROCESSOR MATCHES "arm64|aarch64|ARM64")
    list(APPEND QEDGE_COMMON_DEFS SQLITE_VEC_ENABLE_NEON)
  elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "x86_64|AMD64|x64")
    list(APPEND QEDGE_COMMON_DEFS SQLITE_VEC_ENABLE_AVX)
  endif()
endif()

set(QEDGE_VEC_DIR "${DEPS}/sqlite-vec")

# --- plain variant -----------------------------------------------------------------------
if(NOT QEDGE_CIPHER)
  set(SQLITE_DIR "${DEPS}/${QEDGE_SQLITE_DIR}")
  add_library(qedge_sqlite3 SHARED
      "${SQLITE_DIR}/sqlite3.c"
      "${QEDGE_VEC_DIR}/sqlite-vec.c"
      "${CMAKE_CURRENT_SOURCE_DIR}/src/qedge_init.c")
  target_include_directories(qedge_sqlite3 PRIVATE "${SQLITE_DIR}" "${QEDGE_VEC_DIR}")
  target_compile_definitions(qedge_sqlite3 PRIVATE ${QEDGE_COMMON_DEFS} QEDGE_CIPHER_VERSION="none")
  set(QEDGE_TARGET qedge_sqlite3)
endif()

# --- cipher variant ----------------------------------------------------------------------
if(QEDGE_CIPHER)
  set(SQLCIPHER_DIR "${DEPS}/sqlcipher-${QEDGE_SQLCIPHER_VERSION}")
  set(LTC_DIR "${DEPS}/libtomcrypt-1.18.2")

  # SQLCipher ships no amalgamation; generate it with tclsh (Git for Windows provides one).
  set(SQLCIPHER_AMALGAMATION "${CMAKE_CURRENT_BINARY_DIR}/sqlcipher-amalgamation/sqlite3.c")
  add_custom_command(
      OUTPUT "${SQLCIPHER_AMALGAMATION}"
      COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_CURRENT_BINARY_DIR}/sqlcipher-amalgamation"
      COMMAND ${CMAKE_COMMAND} -E chdir "${SQLCIPHER_DIR}" ${CMAKE_COMMAND}
              -DSQLCIPHER_DIR=${SQLCIPHER_DIR}
              -DOUT_DIR=${CMAKE_CURRENT_BINARY_DIR}/sqlcipher-amalgamation
              -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/make-sqlcipher-amalgamation.cmake"
      DEPENDS "${SQLCIPHER_DIR}/tool/mksqlite3c.tcl"
      COMMENT "Generating the SQLCipher amalgamation with tclsh"
      VERBATIM)

  execute_process(
      COMMAND python "${CMAKE_CURRENT_SOURCE_DIR}/cmake/ltc-sources.py" "${LTC_DIR}"
      OUTPUT_VARIABLE LTC_SOURCES
      RESULT_VARIABLE LTC_RC)
  if(NOT LTC_RC EQUAL 0)
    message(FATAL_ERROR "ltc-sources.py failed (rc=${LTC_RC})")
  endif()

  add_library(qedge_sqlcipher SHARED
      "${SQLCIPHER_AMALGAMATION}"
      "${QEDGE_VEC_DIR}/sqlite-vec.c"
      "${CMAKE_CURRENT_SOURCE_DIR}/src/qedge_init.c"
      ${LTC_SOURCES})
  target_include_directories(qedge_sqlcipher PRIVATE
      "${CMAKE_CURRENT_BINARY_DIR}/sqlcipher-amalgamation"
      "${QEDGE_VEC_DIR}"
      "${LTC_DIR}/src/headers")
  target_compile_definitions(qedge_sqlcipher PRIVATE
      ${QEDGE_COMMON_DEFS}
      QEDGE_CIPHER
      QEDGE_CIPHER_VERSION="${QEDGE_SQLCIPHER_VERSION}"
      SQLITE_HAS_CODEC
      SQLCIPHER_CRYPTO_LIBTOMCRYPT
      CIPHER="AES-256-CBC"
      LTC_SOURCE
      LTC_NO_PROTOTYPES)
  if(WIN32)
    target_compile_definitions(qedge_sqlcipher PRIVATE ENDIAN_LITTLE _WIN32)
    target_link_libraries(qedge_sqlcipher PRIVATE advapi32 bcrypt)
  endif()
  set(QEDGE_TARGET qedge_sqlcipher)
endif()

if(MSVC)
  target_compile_options(${QEDGE_TARGET} PRIVATE /guard:cf /W1 /O2 /Oi)
  target_link_options(${QEDGE_TARGET} PRIVATE /GUARD:CF /DYNAMICBASE /NXCOMPAT)
  target_compile_definitions(${QEDGE_TARGET} PRIVATE SQLITE_OS_WIN SQLITE_WIN32_FILEMAPPING_API=1 SQLITE_API=__declspec\(dllexport\))
else()
  target_compile_definitions(${QEDGE_TARGET} PRIVATE SQLITE_OS_UNIX)
endif()

if(ANDROID)
  # NDK r28+ aligns to 16 KB by default; pass the flags anyway so a downgrade or an
  # armeabi-v7a build cannot silently regress and trip XA0141 in every consuming app.
  target_link_options(${QEDGE_TARGET} PRIVATE
      "-Wl,-z,max-page-size=16384" "-Wl,-z,common-page-size=16384")
  target_link_libraries(${QEDGE_TARGET} PRIVATE log)
endif()

set_target_properties(${QEDGE_TARGET} PROPERTIES
    C_VISIBILITY_PRESET hidden
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out")
```

- [ ] **Step 6: Write `foundation\native\cmake\make-sqlcipher-amalgamation.cmake`**

```cmake
# Runs SQLCipher's own tclsh-driven amalgamation build, then copies the result out.
find_program(TCLSH NAMES tclsh tclsh86 tclsh8.6 REQUIRED)
execute_process(
    COMMAND ${TCLSH} "${SQLCIPHER_DIR}/tool/mksqlite3c.tcl"
    WORKING_DIRECTORY "${SQLCIPHER_DIR}"
    RESULT_VARIABLE rc)
if(NOT rc EQUAL 0)
  message(FATAL_ERROR "mksqlite3c.tcl failed (rc=${rc}). Install tclsh; Git for Windows ships one at Git/mingw64/bin/tclsh.exe.")
endif()
file(COPY "${SQLCIPHER_DIR}/sqlite3.c" "${SQLCIPHER_DIR}/src/sqlite3.h" DESTINATION "${OUT_DIR}")
```

- [ ] **Step 7: Write `foundation\native\README.md`**

```markdown
# Native build

One shared library per RID, built from the SQLite amalgamation plus the sqlite-vec
amalgamation plus `src/qedge_init.c`.

`versions.json` pins every upstream source by tag and hash. sqlite.org publishes only
SHA3-256, so `fetch-sources.ps1` hashes with python rather than `Get-FileHash`.

| Variant | Target | Extra defines |
|---|---|---|
| plain | `qedge_sqlite3` | — |
| cipher | `qedge_sqlcipher` | `SQLITE_HAS_CODEC`, `SQLCIPHER_CRYPTO_LIBTOMCRYPT`, `CIPHER="AES-256-CBC"`, `LTC_SOURCE`, `LTC_NO_PROTOTYPES` |

`SQLITE_EXTRA_INIT` is owned by `qedge_extra_init`, which chains
`sqlcipher_extra_init` (cipher build only) then registers `sqlite3_vec_init` and the
`qedge_version()` scalar through `sqlite3_auto_extension`. SQLCipher 4.19 hard-`#error`s
if `SQLITE_EXTRA_INIT`/`SQLITE_EXTRA_SHUTDOWN` are undefined, so chaining is mandatory,
not stylistic.

## Local (Windows x64 only)

```powershell
pwsh .\scripts\fetch-sources.ps1
pwsh .\scripts\build-windows.ps1
```

Apple, Android and Linux artifacts come from `.github/workflows/native.yml`; the
development box has no NDK, clang or macOS.
```

- [ ] **Step 8: Write `THIRD-PARTY-NOTICES.md` at the repo root**

```markdown
# Third-party notices

Qavren.Edge is MIT licensed. It redistributes or derives from the following.

## SQLite (public domain)

`foundation/native` compiles the SQLite amalgamation, which its authors have
dedicated to the public domain.

## sqlite-vec (MIT OR Apache-2.0)

Copyright (c) 2024 Alex Garcia. Dual licensed MIT OR Apache-2.0; both licences apply
to the redistributed source and to binaries built from it. Full texts:
https://github.com/asg017/sqlite-vec/blob/main/LICENSE-MIT and
https://github.com/asg017/sqlite-vec/blob/main/LICENSE-APACHE

## SQLCipher (BSD-style, Zetetic LLC) — Cipher packages only

`Qavren.Edge.Sqlite.Native.Cipher` contains SQLCipher. See
https://github.com/sqlcipher/sqlcipher/blob/master/LICENSE

## LibTomCrypt (public domain / Unlicense) — Cipher packages only

https://github.com/libtom/libtomcrypt

## SQLitePCLRaw (Apache-2.0)

`foundation/src/Qavren.Edge.Sqlite.Provider/Generated/SQLite3Provider_qedge.g.cs` is
derived from `SQLitePCLRaw.provider.internal/Generated/provider_internal_funcptrs.cs`
at tag v3.0.5, Copyright Eric Sink, licensed under the Apache License 2.0. The Apache
header is retained in the vendored template at
`foundation/src/Qavren.Edge.Sqlite.Provider/Template/provider_internal_funcptrs.cs.template`.
```

- [ ] **Step 9: Fetch the sources and seed the three unknown hashes**

```powershell
pwsh -NoProfile -File "C:\Users\steve\projects\qavren-edge\foundation\native\scripts\fetch-sources.ps1" -IncludeCipher -UpdateHashes
```

Expected: four `... sha256 = <hex>` / `sha3_256 = <hex>` lines, then `versions.json updated`. Confirm the printed SQLite SHA3-256 is exactly `628a44cfe82c66aed1ccbbe85a562d2e33ebe64b3288981ed76285612227934e` — if it is not, **stop**: the archive is not the pinned one. Then replace the three `SEED_ME` values with the printed hashes (the script already did this) and re-run without `-UpdateHashes` to prove verification works.

- [ ] **Step 10: Verify (hash verification passes and CMake configures)**

```powershell
pwsh -NoProfile -File "C:\Users\steve\projects\qavren-edge\foundation\native\scripts\fetch-sources.ps1" -IncludeCipher
```

Expected: `OK: sources present under ...\_deps` with no `HASH MISMATCH`.

```powershell
pwsh -NoProfile -Command "& 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' --version; if (-not (Test-Path 'C:\Users\steve\projects\qavren-edge\foundation\native\_deps\sqlite-vec\sqlite-vec.c')) { Write-Error 'sqlite-vec.c missing' } elseif (-not (Test-Path 'C:\Users\steve\projects\qavren-edge\foundation\native\_deps\sqlite-amalgamation-3530400\sqlite3.c')) { Write-Error 'sqlite3.c missing' } else { Write-Host 'OK: pinned sources verified and extracted' }"
```

Expected: a cmake version banner followed by `OK: pinned sources verified and extracted`.

---

---

## WAVE 3 — Lifecycle hub, provider project, Windows native library

### Task 3.1: Lifecycle hub implementation

**Local-verifiable:** yes.

**Files:**
- Create: `foundation\src\Qavren.Edge.Core\Lifecycle\EdgeLifecycleHub.cs`
- Test: `foundation\tests\Qavren.Edge.Core.Tests\LifecycleHubTests.cs`

- [ ] **Step 1: Write the failing tests**

`foundation\tests\Qavren.Edge.Core.Tests\LifecycleHubTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Qavren.Edge.Lifecycle;
using Xunit;

namespace Qavren.Edge.Core.Tests;

public class LifecycleHubTests
{
    private sealed class Recorder(List<string> log, string name, bool throwOnSleeping = false)
        : EdgeLifecycleObserver
    {
        public override Task OnSleepingAsync(CancellationToken cancellationToken)
        {
            log.Add(name + ":sleeping");
            return throwOnSleeping
                ? Task.FromException(new InvalidOperationException("boom"))
                : Task.CompletedTask;
        }

        public override Task OnMemoryPressureAsync(EdgeMemoryPressure level, CancellationToken cancellationToken)
        {
            log.Add($"{name}:memory:{level}");
            return Task.CompletedTask;
        }
    }

    private static EdgeLifecycleHub Create(IEnumerable<IEdgeLifecycleObserver> observers, int capacity = 64)
        => new(
            observers,
            Options.Create(new EdgeOptions { LifecycleHistoryCapacity = capacity }),
            NullLogger<EdgeLifecycleHub>.Instance,
            TimeProvider.System);

    [Fact]
    public async Task Observers_RunInRegistrationOrder()
    {
        var log = new List<string>();
        var hub = Create([new Recorder(log, "a"), new Recorder(log, "b"), new Recorder(log, "c")]);

        await hub.RaiseSleepingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["a:sleeping", "b:sleeping", "c:sleeping"], log);
    }

    [Fact]
    public async Task ThrowingObserver_IsIsolatedAndRemainingObserversStillRun()
    {
        var log = new List<string>();
        var hub = Create([new Recorder(log, "a", throwOnSleeping: true), new Recorder(log, "b")]);

        await hub.RaiseSleepingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["a:sleeping", "b:sleeping"], log);
        Assert.Equal(1, hub.RecentEvents[0].ObserverFailures);
    }

    [Fact]
    public async Task MemoryPressure_PassesLevelThroughAndIsRecorded()
    {
        var log = new List<string>();
        var hub = Create([new Recorder(log, "a")]);

        await hub.RaiseMemoryPressureAsync(EdgeMemoryPressure.Critical, TestContext.Current.CancellationToken);

        Assert.Equal(["a:memory:Critical"], log);
        var record = hub.RecentEvents[0];
        Assert.Equal(EdgeLifecycleEventKind.MemoryPressure, record.Kind);
        Assert.Equal(EdgeMemoryPressure.Critical, record.Level);
    }

    [Fact]
    public async Task History_IsMostRecentFirstAndCapped()
    {
        var hub = Create([], capacity: 2);

        await hub.RaiseSleepingAsync(TestContext.Current.CancellationToken);
        await hub.RaiseResumedAsync(TestContext.Current.CancellationToken);
        await hub.RaiseStoppingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, hub.RecentEvents.Count);
        Assert.Equal(EdgeLifecycleEventKind.Stopping, hub.RecentEvents[0].Kind);
        Assert.Equal(EdgeLifecycleEventKind.Resumed, hub.RecentEvents[1].Kind);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj" -c Release -f net10.0
```

Expected: FAIL with `CS0246: The type or namespace name 'EdgeLifecycleHub' could not be found`.

- [ ] **Step 3: Implement `EdgeLifecycleHub`**

`foundation\src\Qavren.Edge.Core\Lifecycle\EdgeLifecycleHub.cs`:

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Qavren.Edge.Lifecycle;

/// <summary>
/// Default <see cref="IEdgeLifecycle"/>. Observers run sequentially in registration order,
/// each inside its own try/catch, so a throwing observer never reaches the raiser and never
/// stops the ones behind it.
/// </summary>
public sealed class EdgeLifecycleHub : IEdgeLifecycle
{
    private readonly IReadOnlyList<IEdgeLifecycleObserver> _observers;
    private readonly ILogger<EdgeLifecycleHub> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly int _capacity;
    private readonly Lock _gate = new();
    private readonly LinkedList<EdgeLifecycleRecord> _history = new();

    public EdgeLifecycleHub(
        IEnumerable<IEdgeLifecycleObserver> observers,
        IOptions<EdgeOptions> options,
        ILogger<EdgeLifecycleHub> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(observers);
        ArgumentNullException.ThrowIfNull(options);

        _observers = [.. observers];
        _logger = logger;
        _timeProvider = timeProvider;
        _capacity = Math.Max(1, options.Value.LifecycleHistoryCapacity);
    }

    public IReadOnlyList<EdgeLifecycleRecord> RecentEvents
    {
        get
        {
            lock (_gate)
            {
                return [.. _history];
            }
        }
    }

    public Task RaiseSleepingAsync(CancellationToken cancellationToken = default)
        => RaiseAsync(EdgeLifecycleEventKind.Sleeping, null, static (o, _, ct) => o.OnSleepingAsync(ct), cancellationToken);

    public Task RaiseResumedAsync(CancellationToken cancellationToken = default)
        => RaiseAsync(EdgeLifecycleEventKind.Resumed, null, static (o, _, ct) => o.OnResumedAsync(ct), cancellationToken);

    public Task RaiseMemoryPressureAsync(EdgeMemoryPressure level, CancellationToken cancellationToken = default)
        => RaiseAsync(EdgeLifecycleEventKind.MemoryPressure, level, static (o, l, ct) => o.OnMemoryPressureAsync(l!.Value, ct), cancellationToken);

    public Task RaiseStoppingAsync(CancellationToken cancellationToken = default)
        => RaiseAsync(EdgeLifecycleEventKind.Stopping, null, static (o, _, ct) => o.OnStoppingAsync(ct), cancellationToken);

    private async Task RaiseAsync(
        EdgeLifecycleEventKind kind,
        EdgeMemoryPressure? level,
        Func<IEdgeLifecycleObserver, EdgeMemoryPressure?, CancellationToken, Task> invoke,
        CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();
        var failures = 0;

        _logger.LogDebug(EdgeEventIds.LifecycleRaised, "Lifecycle {Kind} raised ({Level}).", kind, level);

        foreach (var observer in _observers)
        {
            try
            {
                await invoke(observer, level, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures++;
                _logger.LogError(
                    EdgeEventIds.LifecycleObserverFailed,
                    ex,
                    "Lifecycle observer {Observer} threw handling {Kind}.",
                    observer.GetType().FullName,
                    kind);
            }
        }

        var record = new EdgeLifecycleRecord(
            _timeProvider.GetUtcNow(),
            kind,
            level,
            _timeProvider.GetElapsedTime(started),
            failures);

        lock (_gate)
        {
            _history.AddFirst(record);
            while (_history.Count > _capacity)
            {
                _history.RemoveLast();
            }
        }
    }
}
```

`Debug` is imported for symmetry with future instrumentation; if the analyzer flags the unused using, delete the `using System.Diagnostics;` line.

- [ ] **Step 4: Run to verify it passes**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj" -c Release -f net10.0
```

Expected: `Failed: 0`, exit code 0.

- [ ] **Step 5: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj" -c Release -f net10.0
```

Expected: 10 tests pass (6 from Task 2.1 plus 4 here), exit code 0.

---

---

### Task 3.2: `Qavren.Edge.Sqlite.Provider` — vendor the template and generate the provider

**Local-verifiable:** yes.

**Files:**
- Create: `foundation\src\Qavren.Edge.Sqlite.Provider\Qavren.Edge.Sqlite.Provider.csproj`
- Create: `foundation\src\Qavren.Edge.Sqlite.Provider\QedgeNativeLibrary.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite.Provider\Template\provider_internal_funcptrs.cs.template`
- Create (generated): `foundation\src\Qavren.Edge.Sqlite.Provider\Generated\SQLite3Provider_qedge.g.cs`

- [ ] **Step 1: Vendor the upstream template**

The template is upstream's own generated provider for the static-library case: `FEATURE_FUNCPTRS/callingconv`, `FEATURE_LOADEXTENSION/false`, `FEATURE_WIN32DIR/false`. That variant is the correct one **because our native defines `SQLITE_OMIT_LOAD_EXTENSION`**: a `LOADEXTENSION/true` provider would `DllImport` a missing export. The `false` variant returns `SQLITE_ERROR` from `sqlite3_load_extension` without P/Invoking.

> **Correction (Wave 3).** An earlier draft of this paragraph claimed the `false` variant also spares `sqlite3_enable_load_extension`, and that `SqliteConnection.Deactivate()` calls it on every pooled connection return while ignoring the result. All of that is false, and each part was checked rather than assumed:
> - The vendored template leaves `ISQLite3Provider.sqlite3_enable_load_extension` as a live call into `NativeMethods` (template line 596; generated file lines 600-602, with the `DllImport` at line 1637).
> - `dumpbin /exports` on the Task 3.3 `win-x64` build reports `sqlite3_enable_load_extension` **absent**, alongside `sqlite3_load_extension`.
> - In `dotnet/efcore` `release/10.0`, `Deactivate()` wraps the call in `if (_extensionsEnabled)`, and both it and `EnableExtensions()` feed the result to `SqliteException.ThrowExceptionForRC`.
>
> The defect is therefore real but narrow: it needs an app to call `EnableExtensions(true)` or `LoadExtension`, and it presents as `EntryPointNotFoundException` instead of a clean `SqliteException`. Left as-is through Wave 3 — no code path in Waves 1-3 reaches it. Task 7.1 owns the call: either stub the body in `ProviderRenderer` (one extra `.Replace`, turning it into `SQLITE_ERROR` → `SqliteException`) or reject the opt-in earlier with a typed `EdgeConfigurationException`. Dropping `SQLITE_OMIT_LOAD_EXTENSION` is **not** an acceptable remedy: it would fail the Task 3.3 Step 6 export assertion and widen the attack surface.

```powershell
pwsh -NoProfile -Command "$dst='C:\Users\steve\projects\qavren-edge\foundation\src\Qavren.Edge.Sqlite.Provider\Template'; New-Item -ItemType Directory -Force -Path $dst | Out-Null; Invoke-WebRequest -UseBasicParsing -Uri 'https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/v3.0.5/src/SQLitePCLRaw.provider.internal/Generated/provider_internal_funcptrs.cs' -OutFile (Join-Path $dst 'provider_internal_funcptrs.cs.template'); (Get-Item (Join-Path $dst 'provider_internal_funcptrs.cs.template')).Length"
```

Expected: a byte count in the hundreds of thousands. Then sanity-check that the three substitution anchors are present:

```powershell
pwsh -NoProfile -Command "$p='C:\Users\steve\projects\qavren-edge\foundation\src\Qavren.Edge.Sqlite.Provider\Template\provider_internal_funcptrs.cs.template'; $t=Get-Content $p -Raw; foreach ($a in 'namespace SQLitePCL','SQLite3Provider_internal','private const string SQLITE_DLL = \"__Internal\";','Apache') { if ($t -notmatch [regex]::Escape($a)) { Write-Error \"anchor missing: $a\" } }; Write-Host 'OK: template anchors present'"
```

Expected: `OK: template anchors present`. The Apache-2.0 header stays in the file; `THIRD-PARTY-NOTICES.md` (Task 2.3) already records the attribution.

- [ ] **Step 2: Write `QedgeNativeLibrary.cs`**

`SQLITE_DLL` must be a compile-time constant for `[DllImport]`, so the TFM decides it. The *reported* name is a mutable static because the two Native packages report different values: `Microsoft.Data.Sqlite` maps `raw.GetNativeLibraryName()` through a fixed dictionary and throws `InvalidOperationException` on the `Password` path if the name is `e_sqlite3` or `winsqlite3`.

```csharp
namespace Qavren.Edge.Sqlite.Provider;

/// <summary>Names the native library for the generated provider.</summary>
public static class QedgeNativeLibrary
{
#if IOS
    /// <summary>iOS links the xcframework statically, so entry points live in the main executable.</summary>
    internal const string DllImportName = "__Internal";
#else
    internal const string DllImportName = "qedge_sqlite3";
#endif

    /// <summary>
    /// What <c>ISQLite3Provider.GetNativeLibraryName()</c> returns. Microsoft.Data.Sqlite looks this
    /// up in a fixed dictionary { e_sqlcipher: true, e_sqlite3: false, e_sqlite3mc: true,
    /// sqlcipher: true, sqlite3mc: true, winsqlite3: false } and throws on the Password path when the
    /// answer is <see langword="false"/>. Unknown names are accepted. The plain Native package leaves
    /// this at "qedge_sqlite3"; the Cipher package sets it to "sqlcipher" before installing.
    /// </summary>
    public static string ReportedName { get; set; } = "qedge_sqlite3";

    /// <summary>The exact string passed to <c>DllImport</c> on this target framework.</summary>
    public static string DllImportNameForCurrentTarget => DllImportName;
}
```

- [ ] **Step 3: Generate the provider source**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tools\ProviderGen\ProviderGen.csproj" -c Release -- generate --manifest "C:\Users\steve\projects\qavren-edge\foundation\tools\ProviderGen\provider.manifest.json" --template "C:\Users\steve\projects\qavren-edge\foundation\src\Qavren.Edge.Sqlite.Provider\Template\provider_internal_funcptrs.cs.template" --out "C:\Users\steve\projects\qavren-edge\foundation\src\Qavren.Edge.Sqlite.Provider\Generated\SQLite3Provider_qedge.g.cs"
```

Expected: `wrote ...SQLite3Provider_qedge.g.cs`. If instead you get `Template still contains '__Internal' after substitution`, the upstream file's whitespace differs from the `ProviderRenderer.Render` replacement string for `GetNativeLibraryName` — open the template, copy the exact three lines of that method body, and update the second argument of that `.Replace(...)` call in `ProviderRenderer.cs` to match byte-for-byte. Do not weaken the guard.

- [ ] **Step 4: Write the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0;net10.0-ios</TargetFrameworks>
    <IsPackable>true</IsPackable>
    <PackageId>Qavren.Edge.Sqlite.Provider</PackageId>
    <Description>Generated SQLitePCLRaw ISQLite3Provider for the Qavren.Edge native SQLite build.</Description>
    <RootNamespace>Qavren.Edge.Sqlite.Provider</RootNamespace>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <!-- The generated file is upstream's shape; do not hold it to this repo's style rules. -->
    <NoWarn>$(NoWarn);CA1401;CA2101;SYSLIB1054;IDE0005;IDE1006;CS8500</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SQLitePCLRaw.core" />
  </ItemGroup>
  <ItemGroup>
    <!-- Vendored template is packaging input only, never compiled. -->
    <None Include="Template\**\*" Pack="false" />
    <Compile Remove="Template\**\*" />
  </ItemGroup>
</Project>
```

`SYSLIB1054` is the "use `LibraryImport` instead of `DllImport`" suggestion. It is suppressed on purpose: `[DisableRuntimeMarshalling]` is assembly-scoped and bans reference types and `in`/`ref`/`out` parameters in interop, which the `SafeHandle`-typed and `out IntPtr` signatures throughout `ISQLite3Provider` require. See spec adjustment 1.

- [ ] **Step 5: Verify (both TFMs compile)**

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge\foundation\src\Qavren.Edge.Sqlite.Provider\Qavren.Edge.Sqlite.Provider.csproj" -c Release
```

Expected: `Build succeeded` with 0 errors for `net10.0` **and** `net10.0-ios`. Then confirm the substitution actually took:

```powershell
pwsh -NoProfile -Command "$p='C:\Users\steve\projects\qavren-edge\foundation\src\Qavren.Edge.Sqlite.Provider\Generated\SQLite3Provider_qedge.g.cs'; $t=Get-Content $p -Raw; if ($t -match '__Internal') { Write-Error 'substitution incomplete' } elseif ($t -notmatch 'SQLite3Provider_qedge') { Write-Error 'class not renamed' } elseif ($t -notmatch 'QedgeNativeLibrary.DllImportName') { Write-Error 'SQLITE_DLL not rewired' } else { Write-Host 'OK: generated provider substituted correctly' }"
```

Expected: `OK: generated provider substituted correctly`.

Then assert the interop shape is upstream's classic `DllImport` and not `LibraryImport` (see spec adjustment 1):

```powershell
pwsh -NoProfile -Command "$p='C:\Users\steve\projects\qavren-edge\foundation\src\Qavren.Edge.Sqlite.Provider\Generated\SQLite3Provider_qedge.g.cs'; $t=Get-Content $p -Raw; if ($t -match 'LibraryImport') { Write-Error 'LibraryImport present; the provider must use classic DllImport' } elseif ($t -notmatch 'ExactSpelling\s*=\s*true') { Write-Error 'ExactSpelling not found' } else { Write-Host ('OK: classic DllImport, ' + ([regex]::Matches($t, 'ExactSpelling\s*=\s*true')).Count + ' ExactSpelling sites') }"
```

Expected: `OK: classic DllImport, 152 ExactSpelling sites`. The whitespace-tolerant `ExactSpelling\s*=\s*true` is load-bearing: upstream SQLitePCL.raw v3.0.5 emits `ExactSpelling=true` **unspaced**, so a pattern hard-coding `ExactSpelling = true` reports a false failure against a byte-faithful vendoring. Do not reformat the vendored file to satisfy a regex.

---

---

### Task 3.3: Windows native build script (x64 + arm64) and smoke test

**Local-verifiable:** yes for `x64` — the only native this box can *execute*, and what every unit
test runs against. `arm64` cross-compiles here **only if** the VS component
`Microsoft.VisualStudio.Component.VC.Tools.ARM64` is installed; the script skips it with a printed
reason otherwise, and it can never be run locally. Spec §10.3 lists both Windows architectures and
neither is dropped: CI (`windows-2025`, which always carries the ARM64 tools) builds both.

**Files:**
- Create: `foundation\native\scripts\build-windows.ps1`
- Create: `foundation\native\scripts\smoke.cs`

- [ ] **Step 1: Write `build-windows.ps1`**

Ninja, not the Visual Studio generator: this box has VS 2026 (toolset v145) and the GitHub `windows-2025` label now maps to a VS 2026 image, so the generator string `"Visual Studio 17 2022"` is not stable. `vswhere` plus the right `vcvars*.bat` plus `-G Ninja` sidesteps the question.

```powershell
#requires -Version 7
<#
.SYNOPSIS
  Builds qedge_sqlite3.dll (or qedge_sqlcipher.dll) for win-x64 or win-arm64 using the
  VS-bundled CMake, Ninja and MSVC located through vswhere.
.PARAMETER Arch
  x64 (default) or arm64. arm64 is a cross-compile: it produces a DLL that cannot run on
  an x64 host, so no smoke test is run against it locally.
.PARAMETER SkipIfToolsetMissing
  Exit 0 with a printed reason instead of throwing when the requested toolset is absent.
  Used for the local arm64 slice, never in CI.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64',
    [switch]$Cipher,
    [switch]$SkipIfToolsetMissing,
    [string]$BuildSha = 'local',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$nativeRoot = Split-Path -Parent $PSScriptRoot

$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found at $vswhere" }

# The x64 host toolset is always required (it hosts the cross compiler); arm64 additionally
# requires the ARM64 target tools.
$required = @('Microsoft.VisualStudio.Component.VC.Tools.x86.x64')
if ($Arch -eq 'arm64') { $required += 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' }

$vsArgs = @('-latest', '-products', '*')
foreach ($c in $required) { $vsArgs += @('-requires', $c) }
$vsArgs += @('-property', 'installationPath')

$vsRoot = (& $vswhere @vsArgs)
if ([string]::IsNullOrWhiteSpace($vsRoot)) {
    $msg = "No Visual Studio installation carries: $($required -join ', ')"
    if ($SkipIfToolsetMissing) { Write-Host "SKIP ($Arch): $msg"; exit 0 }
    throw $msg
}
$vsRoot = $vsRoot.Trim()

$cmake  = Join-Path $vsRoot 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$ninja  = Join-Path $vsRoot 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'
$vcvarsName = if ($Arch -eq 'arm64') { 'vcvarsamd64_arm64.bat' } else { 'vcvars64.bat' }
$vcvars = Join-Path $vsRoot "VC\Auxiliary\Build\$vcvarsName"
foreach ($p in $cmake, $ninja) { if (-not (Test-Path $p)) { throw "missing tool: $p" } }
if (-not (Test-Path $vcvars)) {
    $msg = "missing tool: $vcvars"
    if ($SkipIfToolsetMissing) { Write-Host "SKIP ($Arch): $msg"; exit 0 }
    throw $msg
}

$rid       = "win-$Arch"
$variant   = if ($Cipher) { 'cipher' } else { 'plain' }
$buildDir  = Join-Path $nativeRoot "build\$rid-$variant"
$outDir    = Join-Path $nativeRoot "artifacts\$rid"
New-Item -ItemType Directory -Force -Path $buildDir, $outDir | Out-Null

$cipherFlag = if ($Cipher) { 'ON' } else { 'OFF' }
$systemProcessor = if ($Arch -eq 'arm64') { 'ARM64' } else { 'AMD64' }

# vcvars*.bat only exports into a cmd session, so configure and build inside one.
# CMAKE_SYSTEM_NAME/PROCESSOR put CMake into cross-compiling mode for the arm64 slice so it
# never tries to run a target-architecture test binary on the host.
$script = @"
call "$vcvars" >nul || exit /b 1
"$cmake" -S "$nativeRoot" -B "$buildDir" -G Ninja ^
  -DCMAKE_MAKE_PROGRAM="$ninja" ^
  -DCMAKE_BUILD_TYPE=$Configuration ^
  -DCMAKE_SYSTEM_NAME=Windows ^
  -DCMAKE_SYSTEM_PROCESSOR=$systemProcessor ^
  -DBUILD_SHARED_LIBS=ON ^
  -DQEDGE_CIPHER=$cipherFlag ^
  -DQEDGE_BUILD_SHA=$BuildSha || exit /b 1
"$cmake" --build "$buildDir" --config $Configuration || exit /b 1
"@

$batch = Join-Path $env:TEMP ("qedge-build-{0}.bat" -f ([guid]::NewGuid().ToString('N')))
Set-Content -Path $batch -Value $script -Encoding ascii
try {
    & cmd.exe /c $batch
    if ($LASTEXITCODE -ne 0) { throw "native build failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item $batch -ErrorAction SilentlyContinue
}

$dllName = if ($Cipher) { 'qedge_sqlcipher.dll' } else { 'qedge_sqlite3.dll' }
$built   = Join-Path $buildDir "out\$dllName"
if (-not (Test-Path $built)) { throw "expected $built" }
Copy-Item $built (Join-Path $outDir $dllName) -Force

Write-Host "OK: $(Join-Path $outDir $dllName)"
```

Note: `$env:TEMP` is redirected to the D: drive on this box; that is fine for a throwaway batch file, but do not put git worktrees there.

- [ ] **Step 2: Build x64**

```powershell
pwsh -NoProfile -File "C:\Users\steve\projects\qavren-edge\foundation\native\scripts\build-windows.ps1" -Arch x64 -BuildSha local
```

Expected: `OK: ...\foundation\native\artifacts\win-x64\qedge_sqlite3.dll`. First run compiles the ~9 MB SQLite amalgamation and takes a few minutes.

- [ ] **Step 3: Build arm64 (or record the documented skip)**

```powershell
pwsh -NoProfile -File "C:\Users\steve\projects\qavren-edge\foundation\native\scripts\build-windows.ps1" -Arch arm64 -SkipIfToolsetMissing -BuildSha local
```

Expected, one of exactly two outcomes, both exit code 0:

- `OK: ...\foundation\native\artifacts\win-arm64\qedge_sqlite3.dll` — the ARM64 toolset is
  installed and the cross-compile worked. Do **not** try to run the smoke test against it; it
  cannot execute on this host.
- `SKIP (arm64): No Visual Studio installation carries: ...VC.Tools.ARM64` — expected on a box
  without the ARM64 component. `native.yml` builds this slice on `windows-2025`, which always has
  it, so `runtimes/win-arm64/native/` is populated in CI; the local package verify (Task 8.1)
  therefore asserts `win-x64` only.

If you want the slice locally, install it once:
`"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vs_installer.exe" modify --passive --add Microsoft.VisualStudio.Component.VC.Tools.ARM64`.

- [ ] **Step 4: Write the smoke test as a .NET 10 file-based app**

This is the step that proves the two riskiest native assumptions at once: that `sqlite3_auto_extension` still functions with `SQLITE_OMIT_LOAD_EXTENSION` defined, and that `SQLITE_EXTRA_INIT` chaining registers `vec0` before any connection exists. It P/Invokes the DLL directly, so it does not depend on the managed provider.

`foundation\native\scripts\smoke.cs`:

```csharp
// Run: dotnet run foundation/native/scripts/smoke.cs -- <path-to-dll>
using System.Runtime.InteropServices;
using System.Text;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: smoke.cs <path-to-qedge dll>");
    return 2;
}

var dllPath = Path.GetFullPath(args[0]);
if (!File.Exists(dllPath))
{
    Console.Error.WriteLine($"not found: {dllPath}");
    return 2;
}

NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, asm, path) =>
    name == "qedge" ? NativeLibrary.Load(dllPath) : IntPtr.Zero);

var rc = Native.sqlite3_open_v2(Utf8(":memory:"), out var db, 0x00000002 /*READWRITE*/ | 0x00000004 /*CREATE*/, IntPtr.Zero);
if (rc != 0)
{
    Console.Error.WriteLine($"sqlite3_open_v2 failed rc={rc}");
    return 1;
}

foreach (var sql in new[] { "select sqlite_version()", "select vec_version()", "select qedge_version()" })
{
    Console.WriteLine($"{sql} -> {Scalar(db, sql)}");
}

rc = Exec(db, "create virtual table t using vec0(embedding float[4] distance_metric=cosine)");
if (rc != 0)
{
    Console.Error.WriteLine($"vec0 CREATE failed rc={rc}");
    return 1;
}

Console.WriteLine("OK: vec0 virtual table created, auto-extension survived SQLITE_OMIT_LOAD_EXTENSION");
return 0;

static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

static int Exec(IntPtr db, string sql)
{
    var rc = Native.sqlite3_prepare_v2(db, Utf8(sql), -1, out var stmt, IntPtr.Zero);
    if (rc != 0) { return rc; }
    rc = Native.sqlite3_step(stmt);
    Native.sqlite3_finalize(stmt);
    return rc is 100 or 101 ? 0 : rc;   // SQLITE_ROW / SQLITE_DONE
}

static string Scalar(IntPtr db, string sql)
{
    var rc = Native.sqlite3_prepare_v2(db, Utf8(sql), -1, out var stmt, IntPtr.Zero);
    if (rc != 0) { return $"<prepare rc={rc}>"; }
    try
    {
        return Native.sqlite3_step(stmt) == 100
            ? Marshal.PtrToStringUTF8(Native.sqlite3_column_text(stmt, 0)) ?? "<null>"
            : "<no row>";
    }
    finally
    {
        Native.sqlite3_finalize(stmt);
    }
}

internal static class Native
{
    private const string Dll = "qedge";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int sqlite3_step(IntPtr stmt);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int sqlite3_finalize(IntPtr stmt);
}
```

- [ ] **Step 5: Run the smoke test (x64 only)**

```powershell
dotnet run "C:\Users\steve\projects\qavren-edge\foundation\native\scripts\smoke.cs" -- "C:\Users\steve\projects\qavren-edge\foundation\native\artifacts\win-x64\qedge_sqlite3.dll"
```

Expected, exactly this shape:

```
select sqlite_version() -> 3.53.4
select vec_version() -> v0.1.9
select qedge_version() -> sqlite 3.53.4 | vec v0.1.9 | cipher none | build local
OK: vec0 virtual table created, auto-extension survived SQLITE_OMIT_LOAD_EXTENSION
```

If `vec_version()` returns `<prepare rc=1>`, `sqlite3_auto_extension` did **not** run. The single remedy is to drop `SQLITE_OMIT_LOAD_EXTENSION` from `QEDGE_COMMON_DEFS` in `foundation/native/CMakeLists.txt` and instead rely on load-extension being disabled at runtime by default (`sqlite3_enable_load_extension` defaults to off). If you take that remedy you must also re-vendor the provider template using `provider_e_sqlite3_funcptrs_notwin.cs` — which does export `sqlite3_enable_load_extension` — and record the change in `foundation/docs/adr/`.

- [ ] **Step 6: Verify (exports match the compile configuration)**

```powershell
pwsh -NoProfile -Command "$vs = (& 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe' -latest -products * -property installationPath).Trim(); $dumpbin = Get-ChildItem -Path (Join-Path $vs 'VC\Tools\MSVC') -Recurse -Filter dumpbin.exe | Where-Object FullName -like '*Hostx64\x64*' | Select-Object -First 1; $exports = & $dumpbin.FullName /exports 'C:\Users\steve\projects\qavren-edge\foundation\native\artifacts\win-x64\qedge_sqlite3.dll' | Out-String; foreach ($n in 'sqlite3_open_v2','sqlite3_prepare_v2','sqlite3_step','sqlite3_column_blob','sqlite3_auto_extension') { if ($exports -notmatch $n) { Write-Error \"missing export: $n\" } }; if ($exports -match 'sqlite3_load_extension') { Write-Error 'SQLITE_OMIT_LOAD_EXTENSION did not take effect' }; if ($exports -match 'sqlite3_key_v2') { Write-Error 'plain build unexpectedly exports sqlite3_key_v2' }; Write-Host 'OK: exports match the compile configuration'"
```

Expected: `OK: exports match the compile configuration`.

`sqlite3_key_v2` is deliberately asserted **absent** from the plain library: plain SQLite has no codec. The generated provider still declares a `DllImport` for it, which is harmless because `DllImport` resolves lazily and the managed layer refuses to call it — `AddSqlite` with a `Key` configured and the plain Native package registered throws `EdgeConfigurationException(EdgeErrorCode.EncryptionKeyWithoutCipherProvider)` before any connection is opened (Task 7.1).

---

---

## WAVE 4 — Core host and diagnostics, Windows SQLCipher native

### Task 4.1: Core host, startup pipeline, diagnostics, and `AddQavrenEdge`

**Local-verifiable:** yes.

**Files:**
- Create: `foundation\src\Qavren.Edge.Core\Hosting\EdgeHost.cs`
- Create: `foundation\src\Qavren.Edge.Core\Hosting\EdgeHostedService.cs`
- Create: `foundation\src\Qavren.Edge.Core\Diagnostics\EdgeDiagnostics.cs`
- Create: `foundation\src\Qavren.Edge.Core\EdgeServiceCollectionExtensions.cs`
- Test: `foundation\tests\Qavren.Edge.Core.Tests\HostTests.cs`
- Test: `foundation\tests\Qavren.Edge.Core.Tests\DiagnosticsTests.cs`

- [ ] **Step 1: Write the failing tests**

`foundation\tests\Qavren.Edge.Core.Tests\HostTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Xunit;

namespace Qavren.Edge.Core.Tests;

public class HostTests
{
    private sealed class RecordingTask(List<string> log, string name, int order, Exception? throws = null)
        : IEdgeStartupTask
    {
        public int Order => order;

        public Task RunAsync(CancellationToken cancellationToken)
        {
            log.Add(name);
            return throws is null ? Task.CompletedTask : Task.FromException(throws);
        }
    }

    private static ServiceProvider Build(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(_ => { });
        configure(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddQavrenEdge_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(_ => { });
        services.AddQavrenEdge(_ => { });

        using var sp = services.BuildServiceProvider();

        Assert.Single(sp.GetServices<IEdgeHost>());
    }

    [Fact]
    public async Task StartupTasks_RunAscendingByOrderThenRegistrationOrder()
    {
        var log = new List<string>();
        using var sp = Build(s =>
        {
            s.AddSingleton<IEdgeStartupTask>(new RecordingTask(log, "hundred", 100));
            s.AddSingleton<IEdgeStartupTask>(new RecordingTask(log, "zero", 0));
            s.AddSingleton<IEdgeStartupTask>(new RecordingTask(log, "ten-a", 10));
            s.AddSingleton<IEdgeStartupTask>(new RecordingTask(log, "ten-b", 10));
        });

        var host = sp.GetRequiredService<IEdgeHost>();
        await host.EnsureStartedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["zero", "ten-a", "ten-b", "hundred"], log);
    }

    [Fact]
    public async Task Start_IsIdempotentAcrossManyCalls()
    {
        var log = new List<string>();
        using var sp = Build(s => s.AddSingleton<IEdgeStartupTask>(new RecordingTask(log, "once", 0)));

        var host = sp.GetRequiredService<IEdgeHost>();
        host.Start();
        host.Start();
        host.Start();
        await host.EnsureStartedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["once"], log);
    }

    [Fact]
    public async Task FirstFailure_FaultsStartedAndIsRethrownByEveryEnsureStarted()
    {
        var boom = new EdgeConfigurationException(EdgeErrorCode.NoNativeProviderRegistered, "no provider");
        var log = new List<string>();
        using var sp = Build(s =>
        {
            s.AddSingleton<IEdgeStartupTask>(new RecordingTask(log, "first", 0, boom));
            s.AddSingleton<IEdgeStartupTask>(new RecordingTask(log, "second", 10));
        });

        var host = sp.GetRequiredService<IEdgeHost>();

        var one = await Assert.ThrowsAsync<EdgeConfigurationException>(
            async () => await host.EnsureStartedAsync(TestContext.Current.CancellationToken));
        var two = await Assert.ThrowsAsync<EdgeConfigurationException>(
            async () => await host.EnsureStartedAsync(TestContext.Current.CancellationToken));

        Assert.Same(boom, one);
        Assert.Same(boom, two);
        Assert.Equal(["first"], log);   // the run stops at the first failure
        Assert.True(host.Started.IsFaulted);
    }
}
```

`foundation\tests\Qavren.Edge.Core.Tests\DiagnosticsTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;
using Xunit;

namespace Qavren.Edge.Core.Tests;

public class DiagnosticsTests
{
    private sealed class FakeContributor : IEdgeDiagnosticsContributor
    {
        public string ComponentName => "Fake";

        public string? ComponentVersion => "1.2.3";

        public IReadOnlyDictionary<string, string?> Describe()
            => new Dictionary<string, string?> { ["answer"] = "42", ["missing"] = null };
    }

    [Fact]
    public async Task Report_ContainsComponentsPathsStartupAndLifecycle()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(_ => { });
        services.AddSingleton<IEdgeDiagnosticsContributor, FakeContributor>();
        using var sp = services.BuildServiceProvider();

        await sp.GetRequiredService<IEdgeHost>().EnsureStartedAsync(TestContext.Current.CancellationToken);
        await sp.GetRequiredService<IEdgeLifecycle>().RaiseResumedAsync(TestContext.Current.CancellationToken);

        var report = sp.GetRequiredService<IEdgeDiagnostics>().Report();

        var component = Assert.Single(report.Components);
        Assert.Equal("Fake", component.Name);
        Assert.Equal("1.2.3", component.Version);
        Assert.Equal("42", component.Details["answer"]);
        Assert.True(report.Paths.ContainsKey("Data"));
        Assert.True(report.Paths.ContainsKey("Cache"));
        Assert.Contains(report.Lifecycle, r => r.Kind == EdgeLifecycleEventKind.Resumed);
    }

    [Fact]
    public async Task Report_RendersAsTextAndAsJson()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(_ => { });
        services.AddSingleton<IEdgeDiagnosticsContributor, FakeContributor>();
        using var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<IEdgeHost>().EnsureStartedAsync(TestContext.Current.CancellationToken);

        var diagnostics = sp.GetRequiredService<IEdgeDiagnostics>();
        var report = diagnostics.Report();

        var text = EdgeDiagnosticsRenderer.ToText(report);
        var json = EdgeDiagnosticsRenderer.ToJson(report);

        Assert.Contains("Fake", text, StringComparison.Ordinal);
        Assert.StartsWith("{", json.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("\"components\"", json, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj" -c Release -f net10.0
```

Expected: FAIL with `CS1061: 'IServiceCollection' does not contain a definition for 'AddQavrenEdge'`.

- [ ] **Step 3: Write `Hosting\EdgeHost.cs`**

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Lifecycle;

namespace Qavren.Edge.Hosting;

/// <summary>Default <see cref="IEdgeHost"/>: one-shot, ordered, fault-latching startup.</summary>
public sealed class EdgeHost : IEdgeHost, IDisposable
{
    private readonly IReadOnlyList<IEdgeStartupTask> _tasks;
    private readonly IEdgeLifecycle _lifecycle;
    private readonly ILogger<EdgeHost> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TaskCompletionSource _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Lock _gate = new();
    private readonly List<EdgeStartupTaskReport> _reports = [];
    private bool _startRequested;

    public EdgeHost(
        IEnumerable<IEdgeStartupTask> tasks,
        IEdgeLifecycle lifecycle,
        ILogger<EdgeHost> logger,
        TimeProvider timeProvider)
    {
        // Stable sort: OrderBy is documented stable, so ties keep DI registration order.
        _tasks = [.. tasks.OrderBy(t => t.Order)];
        _lifecycle = lifecycle;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public Task Started => _started.Task;

    /// <summary>Startup task outcomes, for <see cref="IEdgeDiagnostics"/>.</summary>
    public IReadOnlyList<EdgeStartupTaskReport> StartupReports
    {
        get
        {
            lock (_gate)
            {
                return [.. _reports];
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_startRequested)
            {
                return;
            }

            _startRequested = true;
        }

        _ = Task.Run(RunAsync);
    }

    public async ValueTask EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        Start();
        await Started.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
        => await _lifecycle.RaiseStoppingAsync(cancellationToken).ConfigureAwait(false);

    public void Dispose()
    {
        // Nothing owned yet; present so container disposal has a hook when components register one.
    }

    private async Task RunAsync()
    {
        _logger.LogInformation(EdgeEventIds.StartupBegan, "Qavren.Edge startup began with {Count} tasks.", _tasks.Count);

        foreach (var task in _tasks)
        {
            var name = task.GetType().FullName ?? task.GetType().Name;
            var started = _timeProvider.GetTimestamp();
            try
            {
                await task.RunAsync(CancellationToken.None).ConfigureAwait(false);
                var elapsed = _timeProvider.GetElapsedTime(started);
                Record(new EdgeStartupTaskReport(name, task.Order, elapsed, null));
                _logger.LogDebug(EdgeEventIds.StartupTaskCompleted, "Startup task {Task} completed in {Elapsed}.", name, elapsed);
            }
            catch (Exception ex)
            {
                Record(new EdgeStartupTaskReport(name, task.Order, _timeProvider.GetElapsedTime(started), ex.ToString()));
                _logger.LogError(EdgeEventIds.StartupFailed, ex, "Startup task {Task} failed; Qavren.Edge is faulted.", name);
                _started.TrySetException(ex);
                return;
            }
        }

        _logger.LogInformation(EdgeEventIds.StartupCompleted, "Qavren.Edge startup completed.");
        _started.TrySetResult();
    }

    private void Record(EdgeStartupTaskReport report)
    {
        lock (_gate)
        {
            _reports.Add(report);
        }
    }
}
```

If the analyzer flags `using System.Diagnostics;` as unused, delete that line.

- [ ] **Step 4: Write `Hosting\EdgeHostedService.cs`**

```csharp
using Microsoft.Extensions.Hosting;

namespace Qavren.Edge.Hosting;

/// <summary>
/// Generic-host integration: starts the Edge host and awaits it, so a startup failure fails
/// <c>IHost.StartAsync</c>. MAUI does not use this — the lifecycle bridge calls
/// <see cref="IEdgeHost.Start"/> from the platform launch event and never blocks it.
/// </summary>
public sealed class EdgeHostedService(IEdgeHost host) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
        => await host.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

    public Task StopAsync(CancellationToken cancellationToken) => host.StopAsync(cancellationToken);
}
```

- [ ] **Step 5: Write `Diagnostics\EdgeDiagnostics.cs`**

JSON is written with `Utf8JsonWriter` rather than `JsonSerializer` so the assembly stays AOT- and trim-clean without a serializer context.

```csharp
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;

namespace Qavren.Edge.Diagnostics;

/// <summary>Default <see cref="IEdgeDiagnostics"/>: aggregates contributors, paths, startup and lifecycle.</summary>
public sealed class EdgeDiagnostics(
    IEnumerable<IEdgeDiagnosticsContributor> contributors,
    IEdgePaths paths,
    IEdgeHost host,
    IEdgeLifecycle lifecycle) : IEdgeDiagnostics
{
    public EdgeDiagnosticsReport Report()
    {
        var components = contributors
            .Select(c => new EdgeComponentReport(c.ComponentName, c.ComponentVersion, c.Describe()))
            .ToArray();

        var native = components
            .FirstOrDefault(c => c.Name.Contains("Native", StringComparison.Ordinal))
            ?.Details ?? new Dictionary<string, string?>();

        var startup = host is EdgeHost concrete ? concrete.StartupReports : [];

        return new EdgeDiagnosticsReport(
            components,
            native,
            new Dictionary<string, string> { ["Data"] = paths.Data, ["Cache"] = paths.Cache },
            startup,
            lifecycle.RecentEvents);
    }
}

/// <summary>Renders an <see cref="EdgeDiagnosticsReport"/> for humans and for machines.</summary>
public static class EdgeDiagnosticsRenderer
{
    public static string ToText(EdgeDiagnosticsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.AppendLine("Qavren.Edge diagnostics");
        sb.AppendLine("=======================");

        sb.AppendLine("Paths:");
        foreach (var (key, value) in report.Paths.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append("  ").Append(key).Append(": ").AppendLine(value);
        }

        sb.AppendLine("Components:");
        foreach (var component in report.Components)
        {
            sb.Append("  ").Append(component.Name).Append(' ').AppendLine(component.Version ?? "(no version)");
            foreach (var (key, value) in component.Details.OrderBy(d => d.Key, StringComparer.Ordinal))
            {
                sb.Append("    ").Append(key).Append(": ").AppendLine(value ?? "(null)");
            }
        }

        sb.AppendLine("Startup:");
        foreach (var task in report.Startup)
        {
            sb.Append("  [").Append(task.Order.ToString(CultureInfo.InvariantCulture)).Append("] ")
              .Append(task.Name).Append(' ')
              .Append(task.Duration.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)).Append("ms")
              .AppendLine(task.Error is null ? string.Empty : " FAILED");
            if (task.Error is not null)
            {
                sb.Append("    ").AppendLine(task.Error);
            }
        }

        sb.AppendLine("Lifecycle (most recent first):");
        foreach (var record in report.Lifecycle)
        {
            sb.Append("  ").Append(record.Timestamp.ToString("O", CultureInfo.InvariantCulture))
              .Append(' ').Append(record.Kind.ToString())
              .Append(record.Level is null ? string.Empty : "/" + record.Level.Value.ToString())
              .Append(' ').Append(record.Duration.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)).Append("ms")
              .Append(" failures=").AppendLine(record.ObserverFailures.ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    public static string ToJson(EdgeDiagnosticsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            writer.WriteStartObject("paths");
            foreach (var (key, value) in report.Paths)
            {
                writer.WriteString(key, value);
            }

            writer.WriteEndObject();

            writer.WriteStartArray("components");
            foreach (var component in report.Components)
            {
                writer.WriteStartObject();
                writer.WriteString("name", component.Name);
                writer.WriteString("version", component.Version);
                writer.WriteStartObject("details");
                foreach (var (key, value) in component.Details)
                {
                    writer.WriteString(key, value);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("startup");
            foreach (var task in report.Startup)
            {
                writer.WriteStartObject();
                writer.WriteString("name", task.Name);
                writer.WriteNumber("order", task.Order);
                writer.WriteNumber("durationMs", task.Duration.TotalMilliseconds);
                writer.WriteString("error", task.Error);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("lifecycle");
            foreach (var record in report.Lifecycle)
            {
                writer.WriteStartObject();
                writer.WriteString("timestamp", record.Timestamp);
                writer.WriteString("kind", record.Kind.ToString());
                writer.WriteString("level", record.Level?.ToString());
                writer.WriteNumber("durationMs", record.Duration.TotalMilliseconds);
                writer.WriteNumber("observerFailures", record.ObserverFailures);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
```

- [ ] **Step 6: Write `EdgeServiceCollectionExtensions.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;

namespace Qavren.Edge;

public static class EdgeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Qavren.Edge core. Idempotent: calling it twice adds one host, one hub,
    /// one diagnostics service, and applies both configuration callbacks.
    /// </summary>
    public static IServiceCollection AddQavrenEdge(this IServiceCollection services, Action<EdgeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IEdgePaths>(sp =>
            new DefaultEdgePaths(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EdgeOptions>>().Value.AppName));
        services.TryAddSingleton<IEdgeLifecycle, EdgeLifecycleHub>();
        services.TryAddSingleton<EdgeHost>();
        services.TryAddSingleton<IEdgeHost>(sp => sp.GetRequiredService<EdgeHost>());
        services.TryAddSingleton<IEdgeDiagnostics, EdgeDiagnostics>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, EdgeHostedService>());

        configure(new EdgeBuilder(services));
        return services;
    }

    /// <summary>Configures <see cref="EdgeOptions"/> without adding another host.</summary>
    public static EdgeBuilder Configure(this EdgeBuilder builder, Action<EdgeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Configure(configure);
        return builder;
    }
}
```

`EdgeDiagnostics` resolves `IEdgeHost` and downcasts to `EdgeHost` for startup reports; registering `EdgeHost` as a concrete singleton first and then forwarding `IEdgeHost` to it keeps that a single instance.

- [ ] **Step 7: Run to verify the tests pass**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj" -c Release -f net10.0
```

Expected: 16 tests pass, exit code 0.

- [ ] **Step 8: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj" -c Release -f net10.0
```

Expected: `Failed: 0`, exit code 0.

---

---

### Task 4.2: Windows SQLCipher native build (x64 + arm64)

**Local-verifiable:** yes — `tclsh` is already on PATH from Git for Windows, which is what generates the SQLCipher amalgamation. This is the highest-risk local task in the plan; budget time for libtomcrypt compile errors.

**Files:**
- Create: `foundation\native\scripts\smoke-cipher.cs`
- Modify: `foundation\native\cmake\ltc-sources.py` only if the makefile parse under-collects

- [ ] **Step 1: Build the cipher variant (x64, then the arm64 cross-compile)**

```powershell
pwsh -NoProfile -File "C:\Users\steve\projects\qavren-edge\foundation\native\scripts\build-windows.ps1" -Arch x64 -Cipher -BuildSha local
pwsh -NoProfile -File "C:\Users\steve\projects\qavren-edge\foundation\native\scripts\build-windows.ps1" -Arch arm64 -Cipher -SkipIfToolsetMissing -BuildSha local
```

Expected: `OK: ...\foundation\native\artifacts\win-x64\qedge_sqlcipher.dll`, then either
`OK: ...\artifacts\win-arm64\qedge_sqlcipher.dll` or `SKIP (arm64): ...` — exactly as in Task 3.3
Step 3. Spec §10.3's "Cipher variant produces the same set" means the same **two** Windows
architectures; CI builds both.

Two failure modes are expected and both have a single fix each:

1. `mksqlite3c.tcl failed` — `tclsh` is missing from PATH. Prepend `C:\Program Files\Git\mingw64\bin` to `$env:PATH` for the session and rerun.
2. `unresolved external symbol ltc_mp` (or similar MPI symbols) — the makefile parse pulled in a file that needs libtommath. Add the offending directory prefix to `EXCLUDE_PREFIXES` in `foundation\native\cmake\ltc-sources.py`, rerun, and record which prefix you added in the commit message. Do **not** add a hand-written file list; the parse must stay derived from upstream's makefile.

- [ ] **Step 2: Write `foundation\native\scripts\smoke-cipher.cs`**

```csharp
// Run: dotnet run foundation/native/scripts/smoke-cipher.cs -- <path-to-qedge_sqlcipher.dll> <scratch-dir>
using System.Runtime.InteropServices;
using System.Text;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: smoke-cipher.cs <path-to-dll> <scratch-dir>");
    return 2;
}

var dllPath = Path.GetFullPath(args[0]);
var dbPath = Path.Combine(Path.GetFullPath(args[1]), "cipher-smoke.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
File.Delete(dbPath);

NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, asm, path) =>
    name == "qedge" ? NativeLibrary.Load(dllPath) : IntPtr.Zero);

const string GoodKey = "x'0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20'";
const string BadKey = "x'FF02030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20'";

// Create, key, write.
if (!Open(dbPath, out var db)) { return 1; }
if (Exec(db, $"PRAGMA key = '{GoodKey.Replace("'", "''")}'") != 0) { Console.Error.WriteLine("PRAGMA key failed"); return 1; }
if (Exec(db, "CREATE TABLE t(x TEXT)") != 0) { Console.Error.WriteLine("CREATE failed"); return 1; }
if (Exec(db, "INSERT INTO t VALUES('hello')") != 0) { Console.Error.WriteLine("INSERT failed"); return 1; }
Native.sqlite3_close_v2(db);

// Reopen with the right key.
if (!Open(dbPath, out db)) { return 1; }
Exec(db, $"PRAGMA key = '{GoodKey.Replace("'", "''")}'");
Console.WriteLine($"good key -> {Scalar(db, "SELECT x FROM t")}");
Console.WriteLine($"cipher_version -> {Scalar(db, "PRAGMA cipher_version")}");
Console.WriteLine($"qedge_version -> {Scalar(db, "SELECT qedge_version()")}");
Console.WriteLine($"vec_version -> {Scalar(db, "SELECT vec_version()")}");
Native.sqlite3_close_v2(db);

// Reopen with the wrong key: reading must fail.
if (!Open(dbPath, out db)) { return 1; }
Exec(db, $"PRAGMA key = '{BadKey.Replace("'", "''")}'");
var wrong = Exec(db, "SELECT count(*) FROM sqlite_master");
Native.sqlite3_close_v2(db);

if (wrong == 0)
{
    Console.Error.WriteLine("FAIL: the wrong key was accepted");
    return 1;
}

Console.WriteLine("OK: raw key accepted, wrong key rejected, vec0 present in the cipher build");
return 0;

static bool Open(string path, out IntPtr db)
{
    var rc = Native.sqlite3_open_v2(Encoding.UTF8.GetBytes(path + "\0"), out db, 0x02 | 0x04, IntPtr.Zero);
    if (rc != 0) { Console.Error.WriteLine($"open failed rc={rc}"); }
    return rc == 0;
}

static int Exec(IntPtr db, string sql)
{
    var rc = Native.sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes(sql + "\0"), -1, out var stmt, IntPtr.Zero);
    if (rc != 0) { return rc; }
    rc = Native.sqlite3_step(stmt);
    Native.sqlite3_finalize(stmt);
    return rc is 100 or 101 ? 0 : rc;
}

static string Scalar(IntPtr db, string sql)
{
    var rc = Native.sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes(sql + "\0"), -1, out var stmt, IntPtr.Zero);
    if (rc != 0) { return $"<prepare rc={rc}>"; }
    try
    {
        return Native.sqlite3_step(stmt) == 100
            ? Marshal.PtrToStringUTF8(Native.sqlite3_column_text(stmt, 0)) ?? "<null>"
            : "<no row>";
    }
    finally { Native.sqlite3_finalize(stmt); }
}

internal static class Native
{
    private const string Dll = "qedge";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int sqlite3_step(IntPtr stmt);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int sqlite3_close_v2(IntPtr db);
}
```

- [ ] **Step 3: Run the cipher smoke test**

```powershell
dotnet run "C:\Users\steve\projects\qavren-edge\foundation\native\scripts\smoke-cipher.cs" -- "C:\Users\steve\projects\qavren-edge\foundation\native\artifacts\win-x64\qedge_sqlcipher.dll" "D:\Local\Temp\claude\C--Users-steve-projects\31ba80da-fb75-4f91-b2d4-942c33fcfa7c\scratchpad"
```

Expected:

```
good key -> hello
cipher_version -> 4.19.0 ...
qedge_version -> sqlite 3.53.4 | vec v0.1.9 | cipher 4.19.0 | build local
vec_version -> v0.1.9
OK: raw key accepted, wrong key rejected, vec0 present in the cipher build
```

This is the local proof of the two riskiest cipher assumptions: `SQLITE_EXTRA_INIT` chaining works with SQLCipher's mandatory hook, and a raw `x'…'` key is accepted without PBKDF2.

- [ ] **Step 4: Verify**

```powershell
pwsh -NoProfile -Command "$p='C:\Users\steve\projects\qavren-edge\foundation\native\artifacts\win-x64\qedge_sqlcipher.dll'; if (-not (Test-Path $p)) { Write-Error 'qedge_sqlcipher.dll missing' } else { Write-Host ('OK: x64 {0} bytes' -f (Get-Item $p).Length) }; $a='C:\Users\steve\projects\qavren-edge\foundation\native\artifacts\win-arm64\qedge_sqlcipher.dll'; if (Test-Path $a) { Write-Host ('OK: arm64 {0} bytes' -f (Get-Item $a).Length) } else { Write-Host 'NOTE: win-arm64 skipped locally (ARM64 MSVC toolset absent); native.yml builds it on windows-2025' }"
```

Expected: `OK: x64 <n> bytes` with `n` in the millions, then either the arm64 size or the documented `NOTE:` line.

---

---

## WAVE 5 — SQLite options, MAUI bridge, provider drift test

### Task 5.1: `Qavren.Edge.Sqlite` — options, keys, and the native provider contract

**Local-verifiable:** yes (pure logic; no connection is opened).

**Files:**
- Create: `foundation\src\Qavren.Edge.Sqlite\Qavren.Edge.Sqlite.csproj`
- Create: `foundation\src\Qavren.Edge.Sqlite\SqliteOptions.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite\SqliteKey.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite\ISqliteNativeProvider.cs`
- Test: `foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj`
- Test: `foundation\tests\Qavren.Edge.Sqlite.Tests\SqliteKeyTests.cs`

- [ ] **Step 1: Write the test project and the failing tests**

`foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Qavren.Edge.Sqlite\Qavren.Edge.Sqlite.csproj" />
  </ItemGroup>
</Project>
```

`foundation\tests\Qavren.Edge.Sqlite.Tests\SqliteKeyTests.cs`:

```csharp
using Qavren.Edge.Sqlite;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

public class SqliteKeyTests
{
    [Fact]
    public void FromRawBytes_ProducesTheSqlCipherRawKeyLiteral()
    {
        var bytes = new byte[32];
        bytes[0] = 0x2D;
        bytes[31] = 0x99;

        var key = SqliteKey.FromRawBytes(bytes);

        Assert.True(key.IsRaw);
        // 32 bytes -> 64 hex chars: "2D" + 30 zero bytes (60 chars) + "99".
        Assert.Equal("x'2D" + new string('0', 60) + "99'", key.ToConnectionStringPassword());
        Assert.Equal(67, key.ToConnectionStringPassword().Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void FromRawBytes_RejectsAnythingButThirtyTwoBytes(int length)
        => Assert.Throws<ArgumentException>(() => SqliteKey.FromRawBytes(new byte[length]));

    [Fact]
    public void FromPassphrase_PassesTheTextThroughUnchanged()
    {
        var key = SqliteKey.FromPassphrase("correct horse battery staple");

        Assert.False(key.IsRaw);
        Assert.Equal("correct horse battery staple", key.ToConnectionStringPassword());
    }

    [Fact]
    public void FromPassphrase_RejectsEmpty()
        => Assert.Throws<ArgumentException>(() => SqliteKey.FromPassphrase("   "));

    [Fact]
    public void Options_HaveTheDocumentedDefaults()
    {
        var options = new SqliteOptions();

        Assert.Equal("edge.db", options.DatabaseName);
        Assert.Equal(SqliteJournalMode.Wal, options.JournalMode);
        Assert.Equal(SqliteSynchronousMode.Normal, options.Synchronous);
        Assert.Equal(TimeSpan.FromSeconds(5), options.BusyTimeout);
        Assert.True(options.ForeignKeys);
        Assert.True(options.Pooling);
        Assert.Equal(8192, options.CacheSizeKiB);
        Assert.Null(options.Key);
        Assert.Null(options.KeyProvider);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj" -c Release -f net10.0
```

Expected: FAIL — `Qavren.Edge.Sqlite.csproj` does not exist.

- [ ] **Step 3: Write the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>true</IsPackable>
    <PackageId>Qavren.Edge.Sqlite</PackageId>
    <Description>SQLite for Qavren.Edge: pragma-tuned connections, explicit migrations, and sqlite-vec / FTS5 helpers that emit plain SQL.</Description>
    <RootNamespace>Qavren.Edge.Sqlite</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite.Core" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Qavren.Edge.Core\Qavren.Edge.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Write `SqliteKey.cs`**

```csharp
using System.Globalization;

namespace Qavren.Edge.Sqlite;

/// <summary>Resolves a key lazily, e.g. from platform secure storage.</summary>
public delegate ValueTask<SqliteKey> SqliteKeyProvider(CancellationToken cancellationToken);

/// <summary>
/// An encryption key for a SQLCipher database, in the form Microsoft.Data.Sqlite can carry.
/// </summary>
/// <remarks>
/// Raw keys are delivered as the literal string <c>x'&lt;64 hex&gt;'</c> in
/// <c>SqliteConnectionStringBuilder.Password</c>. Microsoft.Data.Sqlite escapes that with SQLite's
/// own <c>quote()</c> and emits <c>PRAGMA key = 'x''&lt;64 hex&gt;''';</c>; SQLCipher's pragma
/// handler dequotes it back to <c>x'&lt;64 hex&gt;'</c>, which its <c>blob_format</c> test accepts
/// as a raw key and skips PBKDF2. This is why connection pooling can stay enabled and why no
/// per-physical-open interceptor is needed.
/// </remarks>
public sealed class SqliteKey
{
    private const int RawKeyLength = 32;

    private readonly string _password;

    private SqliteKey(string password, bool isRaw)
    {
        _password = password;
        IsRaw = isRaw;
    }

    /// <summary><see langword="true"/> when the key bypasses SQLCipher's KDF.</summary>
    public bool IsRaw { get; }

    /// <summary>A raw 256-bit key. The recommended mobile path: store the bytes in platform secure storage.</summary>
    public static SqliteKey FromRawBytes(ReadOnlySpan<byte> key)
    {
        if (key.Length != RawKeyLength)
        {
            throw new ArgumentException(
                $"A raw SQLCipher key must be exactly {RawKeyLength.ToString(CultureInfo.InvariantCulture)} bytes; got {key.Length.ToString(CultureInfo.InvariantCulture)}.",
                nameof(key));
        }

        return new SqliteKey("x'" + Convert.ToHexString(key) + "'", isRaw: true);
    }

    /// <summary>A passphrase. SQLCipher derives the key with PBKDF2, which is slow by design.</summary>
    public static SqliteKey FromPassphrase(string passphrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passphrase);
        return new SqliteKey(passphrase, isRaw: false);
    }

    /// <summary>The value to assign to <c>SqliteConnectionStringBuilder.Password</c>.</summary>
    public string ToConnectionStringPassword() => _password;
}
```

- [ ] **Step 5: Write `SqliteOptions.cs`**

```csharp
namespace Qavren.Edge.Sqlite;

public enum SqliteJournalMode
{
    Delete,
    Truncate,
    Persist,
    Memory,
    Wal,
    Off,
}

public enum SqliteSynchronousMode
{
    Off = 0,
    Normal = 1,
    Full = 2,
    Extra = 3,
}

/// <summary>Per-database configuration. Bound as a named <c>IOptions</c> instance keyed by database name.</summary>
public sealed class SqliteOptions
{
    /// <summary>File name under <see cref="Directory"/>. <c>:memory:</c> is allowed.</summary>
    public string DatabaseName { get; set; } = "edge.db";

    /// <summary>Overrides <c>IEdgePaths.Data</c>. Useful for tests and shared containers.</summary>
    public string? Directory { get; set; }

    /// <summary>
    /// Applied once, at database creation. <c>journal_mode</c> is persisted in the file header,
    /// so re-issuing it per open is pointless, and issuing it inside a transaction is an error.
    /// </summary>
    public SqliteJournalMode JournalMode { get; set; } = SqliteJournalMode.Wal;

    /// <summary>Issued per logical open. Microsoft.Data.Sqlite 10.x has no <c>Synchronous</c> connection-string keyword.</summary>
    public SqliteSynchronousMode Synchronous { get; set; } = SqliteSynchronousMode.Normal;

    /// <summary>
    /// Issued per logical open as <c>PRAGMA busy_timeout</c>. This is SQLite's own busy handler and is
    /// distinct from Microsoft.Data.Sqlite's <c>Default Timeout</c>, which drives its 150 ms retry loop.
    /// Keep this below the MDS timeout so SQLite backs off before MDS starts spinning.
    /// </summary>
    public TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Applied through the <c>Foreign Keys</c> connection-string keyword, which MDS re-issues on every open.</summary>
    public bool ForeignKeys { get; set; } = true;

    /// <summary>Microsoft.Data.Sqlite connection pooling. Safe to leave on even with a raw key; see <see cref="SqliteKey"/>.</summary>
    public bool Pooling { get; set; } = true;

    /// <summary>Issued per logical open as a negative <c>PRAGMA cache_size</c>, i.e. kibibytes rather than pages.</summary>
    public int CacheSizeKiB { get; set; } = 8192;

    /// <summary>A fixed key. Requires the Cipher native package. Mutually exclusive with <see cref="KeyProvider"/>.</summary>
    public SqliteKey? Key { get; set; }

    /// <summary>A lazily resolved key, e.g. from secure storage. Mutually exclusive with <see cref="Key"/>.</summary>
    public SqliteKeyProvider? KeyProvider { get; set; }
}
```

- [ ] **Step 6: Write `ISqliteNativeProvider.cs`**

```csharp
namespace Qavren.Edge.Sqlite;

/// <summary>What the native library reports about itself once installed.</summary>
public sealed record SqliteNativeInfo(
    string ProviderName,
    string LibraryName,
    string? ResolvedPath,
    string SqliteVersion,
    string VecVersion,
    string CipherVersion,
    string BuildSha);

/// <summary>
/// Implemented by <c>Qavren.Edge.Sqlite.Native</c> and <c>Qavren.Edge.Sqlite.Native.Cipher</c>.
/// Exactly one must be registered; zero or two is <see cref="Qavren.Edge.EdgeConfigurationException"/>.
/// </summary>
public interface ISqliteNativeProvider
{
    string Name { get; }

    /// <summary>The base name passed to <c>DllImport</c>, e.g. <c>qedge_sqlite3</c> or <c>__Internal</c>.</summary>
    string LibraryName { get; }

    bool SupportsEncryption { get; }

    /// <summary>Installs the provider into <c>SQLitePCL.raw</c> and verifies it. Startup task order 0.</summary>
    void Install();

    /// <summary>Valid only after <see cref="Install"/>.</summary>
    SqliteNativeInfo Describe();
}
```

- [ ] **Step 7: Run to verify the tests pass**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj" -c Release -f net10.0
```

Expected: 8 tests pass (1 + 4 theory cases + 3), exit code 0.

- [ ] **Step 8: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj" -c Release -f net10.0
```

Expected: `Failed: 0`, exit code 0.

---

---

### Task 5.2: `Qavren.Edge.Maui` lifecycle bridge

**Local-verifiable:** partially. The `net10.0-windows10.0.19041.0` TFM compiles here; android, ios and maccatalyst compile but cannot be **run** locally, so the bridge's runtime behaviour is exercised by the device tests (Task 10.2, CI-only).

**Files:**
- Create: `foundation\src\Qavren.Edge.Maui\Qavren.Edge.Maui.csproj`
- Create: `foundation\src\Qavren.Edge.Maui\MauiEdgePaths.cs`
- Create: `foundation\src\Qavren.Edge.Maui\MauiAppBuilderExtensions.cs`
- Create: `foundation\src\Qavren.Edge.Maui\Platforms\Android\AndroidLifecycleBridge.cs`
- Create: `foundation\src\Qavren.Edge.Maui\Platforms\iOS\AppleLifecycleBridge.cs`
- Create: `foundation\src\Qavren.Edge.Maui\Platforms\Windows\WindowsLifecycleBridge.cs`

- [ ] **Step 1: Write the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0-android;net10.0-ios;net10.0-maccatalyst;net10.0-windows10.0.19041.0</TargetFrameworks>
    <UseMaui>true</UseMaui>
    <SingleProject>false</SingleProject>
    <IsPackable>true</IsPackable>
    <PackageId>Qavren.Edge.Maui</PackageId>
    <Description>MAUI lifecycle bridge for Qavren.Edge: UseQavrenEdge(), FileSystem-backed paths, and platform lifecycle and memory-pressure mapping.</Description>
    <RootNamespace>Qavren.Edge.Maui</RootNamespace>
    <EnableDefaultCompileItems>true</EnableDefaultCompileItems>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Maui.Controls" />
    <ProjectReference Include="..\Qavren.Edge.Core\Qavren.Edge.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Compile Remove="Platforms\**\*.cs" />
  </ItemGroup>
  <ItemGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'">
    <Compile Include="Platforms\Android\**\*.cs" />
  </ItemGroup>
  <ItemGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios' or $([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'maccatalyst'">
    <Compile Include="Platforms\iOS\**\*.cs" />
  </ItemGroup>
  <ItemGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'windows'">
    <Compile Include="Platforms\Windows\**\*.cs" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write `MauiEdgePaths.cs`**

```csharp
using Microsoft.Maui.Storage;

namespace Qavren.Edge.Maui;

/// <summary>
/// <see cref="IEdgePaths"/> over MAUI's <see cref="FileSystem"/>.
/// </summary>
/// <remarks>
/// The values are read on every access, never cached to settings: on iOS the sandbox path contains
/// an application GUID segment that changes across clean builds and reinstalls, so a persisted
/// absolute path is a guaranteed "the database disappeared after an update" bug.
/// </remarks>
public sealed class MauiEdgePaths : IEdgePaths
{
    public string Data => FileSystem.Current.AppDataDirectory;

    public string Cache => FileSystem.Current.CacheDirectory;
}
```

- [ ] **Step 3: Write `MauiAppBuilderExtensions.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.LifecycleEvents;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Maui;

public static class MauiAppBuilderExtensions
{
    /// <summary>
    /// Registers Qavren.Edge, replaces <see cref="IEdgePaths"/> with the MAUI implementation, and
    /// maps platform lifecycle events onto <see cref="Qavren.Edge.Lifecycle.IEdgeLifecycle"/>.
    /// </summary>
    public static MauiAppBuilder UseQavrenEdge(this MauiAppBuilder builder, Action<EdgeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.AddQavrenEdge(configure);

        // Replace, not TryAdd: AddQavrenEdge already registered DefaultEdgePaths.
        builder.Services.Replace(ServiceDescriptor.Singleton<IEdgePaths, MauiEdgePaths>());

        // The generic-host EdgeHostedService would block MAUI's launch path; the platform bridge
        // calls Start() instead and never awaits it on the UI thread.
        builder.Services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();

        builder.ConfigureLifecycleEvents(events =>
        {
#if ANDROID
            AndroidLifecycleBridge.Configure(events);
#elif IOS || MACCATALYST
            AppleLifecycleBridge.Configure(events);
#elif WINDOWS
            WindowsLifecycleBridge.Configure(events);
#endif
        });

        return builder;
    }
}
```

`RemoveAll<IHostedService>()` is blunt on purpose in v1: MAUI apps that also register their own hosted services should call `Services.AddQavrenEdge(...)` directly and wire the bridge themselves. Record that limitation in `foundation/README.md`.

- [ ] **Step 4: Write `Platforms\Android\AndroidLifecycleBridge.cs`**

```csharp
using Android.Content;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.LifecycleEvents;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;

namespace Qavren.Edge.Maui;

internal static class AndroidLifecycleBridge
{
    private static int _activityCount;

    internal static void Configure(ILifecycleBuilder events) => events.AddAndroid(android => android
        // OnApplicationCreate, NOT OnCreate: OnCreate on IAndroidLifecycleBuilder is the Activity overload.
        .OnApplicationCreate(_ => Resolve<IEdgeHost>()?.Start())
        .OnCreate((_, _) => Interlocked.Increment(ref _activityCount))
        .OnPause(_ => Raise(l => l.RaiseSleepingAsync()))
        .OnResume(_ => Raise(l => l.RaiseResumedAsync()))
        .OnDestroy(_ =>
        {
            // There is no "last activity destroyed" event; count them ourselves.
            if (Interlocked.Decrement(ref _activityCount) <= 0)
            {
                Raise(l => l.RaiseStoppingAsync());
            }
        })
        .OnApplicationLowMemory(_ => Raise(l => l.RaiseMemoryPressureAsync(EdgeMemoryPressure.Critical)))
        .OnApplicationTrimMemory((_, level) =>
        {
            var mapped = MapTrimMemory(level);
            if (mapped is { } pressure)
            {
                Raise(l => l.RaiseMemoryPressureAsync(pressure));
            }
        }));

    /// <summary>
    /// Only <see cref="TrimMemory.UiHidden"/> (20) and <see cref="TrimMemory.Background"/> (40) are
    /// still delivered: every other level was deprecated in API 35 and has not been delivered to apps
    /// since API 34, while .NET MAUI 10 targets API 36. The legacy branch stays for pre-34 devices.
    /// </summary>
    private static EdgeMemoryPressure? MapTrimMemory(TrimMemory level) => level switch
    {
        TrimMemory.UiHidden => EdgeMemoryPressure.Low,
        TrimMemory.Background => EdgeMemoryPressure.Moderate,
        TrimMemory.RunningModerate => EdgeMemoryPressure.Low,
        TrimMemory.RunningLow => EdgeMemoryPressure.Moderate,
        TrimMemory.RunningCritical => EdgeMemoryPressure.Critical,
        TrimMemory.Moderate => EdgeMemoryPressure.Moderate,
        TrimMemory.Complete => EdgeMemoryPressure.Critical,
        _ => null,
    };

    private static T? Resolve<T>() where T : class
        => IPlatformApplication.Current?.Services.GetService<T>();

    private static void Raise(Func<IEdgeLifecycle, Task> raise)
    {
        var lifecycle = Resolve<IEdgeLifecycle>();
        if (lifecycle is not null)
        {
            // The hub swallows observer faults, so nothing can escape here.
            raise(lifecycle).GetAwaiter().GetResult();
        }
    }
}
```

- [ ] **Step 5: Write `Platforms\iOS\AppleLifecycleBridge.cs`**

```csharp
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.LifecycleEvents;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;
using UIKit;

namespace Qavren.Edge.Maui;

internal static class AppleLifecycleBridge
{
    private static NSObject? _memoryWarningToken;

    internal static void Configure(ILifecycleBuilder events) => events.AddiOS(ios => ios
        .FinishedLaunching((_, _) =>
        {
            Resolve<IEdgeHost>()?.Start();

            // There is no DidReceiveMemoryWarning delegate on IiOSLifecycleBuilder and
            // MauiUIApplicationDelegate exports none, so subscribe to UIKit directly.
            _memoryWarningToken ??= UIApplication.Notifications.ObserveDidReceiveMemoryWarning(
                (_, _) => Raise(l => l.RaiseMemoryPressureAsync(EdgeMemoryPressure.Critical)));

            return true;
        })
        .DidEnterBackground(_ => SleepInsideBackgroundTask())
        .WillEnterForeground(_ => Raise(l => l.RaiseResumedAsync()))
        .WillTerminate(_ =>
        {
            _memoryWarningToken?.Dispose();
            _memoryWarningToken = null;
            Raise(l => l.RaiseStoppingAsync());
        }));

    /// <summary>
    /// Window.Backgrounding carries no deferral (BackgroundingEventArgs has only State), so the
    /// bridge takes its own background task to give the WAL checkpoint time to finish.
    /// </summary>
    private static void SleepInsideBackgroundTask()
    {
        var application = UIApplication.SharedApplication;
        var taskId = UIApplication.BackgroundTaskInvalid;
        taskId = application.BeginBackgroundTask("qavren-edge-sleep", () => application.EndBackgroundTask(taskId));

        try
        {
            Raise(l => l.RaiseSleepingAsync());
        }
        finally
        {
            application.EndBackgroundTask(taskId);
        }
    }

    private static T? Resolve<T>() where T : class
        => IPlatformApplication.Current?.Services.GetService<T>();

    private static void Raise(Func<IEdgeLifecycle, Task> raise)
    {
        var lifecycle = Resolve<IEdgeLifecycle>();
        if (lifecycle is not null)
        {
            raise(lifecycle).GetAwaiter().GetResult();
        }
    }
}
```

- [ ] **Step 6: Write `Platforms\Windows\WindowsLifecycleBridge.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.LifecycleEvents;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;
using Windows.System;

namespace Qavren.Edge.Maui;

internal static class WindowsLifecycleBridge
{
    private static bool _memorySubscribed;

    internal static void Configure(ILifecycleBuilder events) => events.AddWindows(windows => windows
        // OnLaunched's first argument is UI.Xaml.Application, not Window - Learn's table is wrong.
        .OnLaunched((_, _) => Resolve<IEdgeHost>()?.Start())
        .OnWindowCreated(_ => SubscribeMemory())
        // MAUI's own cross-platform Stopped uses VisibilityChanged on Windows; OnResumed already
        // suppresses the spurious first activation, so it needs no de-duplication here.
        .OnVisibilityChanged((_, args) =>
        {
            if (!args.Visible)
            {
                Raise(l => l.RaiseSleepingAsync());
            }
        })
        .OnResumed(_ => Raise(l => l.RaiseResumedAsync()))
        .OnClosed((_, _) =>
        {
            UnsubscribeMemory();
            Raise(l => l.RaiseStoppingAsync());
        }));

    /// <summary>
    /// Windows.System.MemoryManager is a UWP API and its behaviour in an unpackaged WinUI 3 process
    /// is not documented, so every touch is guarded. These are static WinRT events, so failing to
    /// unsubscribe would leak the bridge.
    /// </summary>
    private static void SubscribeMemory()
    {
        if (_memorySubscribed)
        {
            return;
        }

        try
        {
            MemoryManager.AppMemoryUsageIncreased += OnMemoryIncreased;
            MemoryManager.AppMemoryUsageLimitChanging += OnMemoryLimitChanging;
            _memorySubscribed = true;
        }
        catch (Exception)
        {
            // Unavailable in this process model; memory pressure simply never fires on Windows.
        }
    }

    private static void UnsubscribeMemory()
    {
        if (!_memorySubscribed)
        {
            return;
        }

        try
        {
            MemoryManager.AppMemoryUsageIncreased -= OnMemoryIncreased;
            MemoryManager.AppMemoryUsageLimitChanging -= OnMemoryLimitChanging;
        }
        catch (Exception)
        {
            // Ignored: unsubscription failure cannot be acted on.
        }

        _memorySubscribed = false;
    }

    private static void OnMemoryIncreased(object? sender, object e)
        => Raise(l => l.RaiseMemoryPressureAsync(EdgeMemoryPressure.Moderate));

    private static void OnMemoryLimitChanging(object? sender, AppMemoryUsageLimitChangingEventArgs e)
        => Raise(l => l.RaiseMemoryPressureAsync(EdgeMemoryPressure.Critical));

    private static T? Resolve<T>() where T : class
        => IPlatformApplication.Current?.Services.GetService<T>();

    private static void Raise(Func<IEdgeLifecycle, Task> raise)
    {
        var lifecycle = Resolve<IEdgeLifecycle>();
        if (lifecycle is not null)
        {
            raise(lifecycle).GetAwaiter().GetResult();
        }
    }
}
```

- [ ] **Step 7: Verify (all four TFMs compile)**

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge\foundation\src\Qavren.Edge.Maui\Qavren.Edge.Maui.csproj" -c Release
```

Expected: `Build succeeded` with 0 errors for `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst` and `net10.0-windows10.0.19041.0`.

If `AppMemoryUsageIncreased`'s handler signature does not match `EventHandler<object>`, change `OnMemoryIncreased`'s second parameter to the type the compiler reports and rebuild — the event's args type is the one detail here that no source was verified against.

---

---

### Task 5.3: Provider drift test

**Local-verifiable:** yes.

**Files:**
- Create: `foundation\tests\Qavren.Edge.Provider.Tests\Qavren.Edge.Provider.Tests.csproj`
- Create: `foundation\tests\Qavren.Edge.Provider.Tests\ProviderDriftTests.cs`

- [ ] **Step 1: Write the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Qavren.Edge.Sqlite.Provider\Qavren.Edge.Sqlite.Provider.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write `ProviderDriftTests.cs`**

```csharp
using System.Reflection;
using System.Text.Json;
using Qavren.Edge.Sqlite.Provider;
using SQLitePCL;
using Xunit;

namespace Qavren.Edge.Provider.Tests;

public class ProviderDriftTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "QavrenEdge.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void GeneratedProvider_ImplementsEveryManifestMemberAndNoOthers()
    {
        var manifestPath = Path.Combine(RepoRoot(), "foundation", "tools", "ProviderGen", "provider.manifest.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var declared = document.RootElement.GetProperty("members")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        var actual = typeof(ISQLite3Provider)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(Format)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(declared.Count, actual.Count);
        Assert.Empty(declared.Except(actual, StringComparer.Ordinal));
        Assert.Empty(actual.Except(declared, StringComparer.Ordinal));
    }

    [Fact]
    public void GeneratedProviderTypeExistsAndIsAnISQLite3Provider()
    {
        var type = typeof(SQLite3Provider_qedge);

        Assert.True(typeof(ISQLite3Provider).IsAssignableFrom(type));
        Assert.True(type.IsSealed);
    }

    [Fact]
    public void ReportedLibraryName_IsNotOneMicrosoftDataSqliteRefusesToEncrypt()
    {
        // MDS maps GetNativeLibraryName() through { e_sqlcipher: true, e_sqlite3: false,
        // e_sqlite3mc: true, sqlcipher: true, sqlite3mc: true, winsqlite3: false } and throws on the
        // Password path when the answer is false. Unknown names are accepted.
        Assert.NotEqual("e_sqlite3", QedgeNativeLibrary.ReportedName);
        Assert.NotEqual("winsqlite3", QedgeNativeLibrary.ReportedName);
    }

    [Fact]
    public void NoSqlitePclRawBundlePackageIsInTheGraph()
    {
        // 2.x bundle packages are incompatible with SQLitePCLRaw.core 3.x and would call
        // raw.SetProvider behind our back through SqliteConnection's static constructor.
        var loadable = Directory.GetFiles(AppContext.BaseDirectory, "SQLitePCLRaw.batteries*.dll");

        Assert.Empty(loadable);
    }
}

file static class Extensions
{
}
```

Add the `Format` helpers as private static methods inside `ProviderDriftTests`. They must stay
byte-identical to `ManifestBuilder.FormatType`/`FormatMember` (Task 2.2) - a plain `Type.FullName`
renders a constructed generic's arguments assembly-qualified, so all six `ReadOnlySpan<byte>`
members would mismatch the checked-in manifest:

```csharp
    private static string FormatType(Type t)
    {
        if (t.IsByRef)
        {
            return FormatType(t.GetElementType()!) + "&";
        }

        if (t.IsPointer)
        {
            return FormatType(t.GetElementType()!) + "*";
        }

        if (t.IsArray)
        {
            return FormatType(t.GetElementType()!) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
        }

        if (t.IsConstructedGenericType)
        {
            var args = string.Join(",", t.GetGenericArguments().Select(FormatType));
            return t.GetGenericTypeDefinition().FullName + "[" + args + "]";
        }

        return t.FullName ?? t.Name;
    }

    private static string Format(MethodInfo m)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(FormatType(m.ReturnType)).Append(' ').Append(m.Name).Append('(');
        var ps = m.GetParameters();
        for (var i = 0; i < ps.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(FormatType(ps[i].ParameterType)).Append(' ').Append(ps[i].Name);
        }

        return sb.Append(')').ToString();
    }
```

Delete the stray empty `file static class Extensions` block; it is not needed.

- [ ] **Step 3: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Provider.Tests\Qavren.Edge.Provider.Tests.csproj" -c Release
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tools\ProviderGen\ProviderGen.csproj" -c Release -- generate --manifest "C:\Users\steve\projects\qavren-edge\foundation\tools\ProviderGen\provider.manifest.json" --template "C:\Users\steve\projects\qavren-edge\foundation\src\Qavren.Edge.Sqlite.Provider\Template\provider_internal_funcptrs.cs.template" --out "C:\Users\steve\projects\qavren-edge\foundation\src\Qavren.Edge.Sqlite.Provider\Generated\SQLite3Provider_qedge.g.cs" --check
```

Expected: 4 tests pass, then `OK: ...SQLite3Provider_qedge.g.cs is up to date.` Both exit 0.

---

---

## WAVE 6 — SQLite vector/FTS helpers, CI workflows

### Task 6.1: Vector and FTS5 helpers plus connection extensions

**Local-verifiable:** yes (every builder is a pure function; the SQL is executed in Task 9.2).

**Files:**
- Create: `foundation\src\Qavren.Edge.Sqlite\Vec\VecBlob.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite\Vec\VecTable.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite\Vec\Knn.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite\Vec\VecFunctions.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite\Fts\FtsTable.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite\SqliteConnectionExtensions.cs`
- Test: `foundation\tests\Qavren.Edge.Sqlite.Tests\VecBlobTests.cs`
- Test: `foundation\tests\Qavren.Edge.Sqlite.Tests\SqlBuilderTests.cs`

- [ ] **Step 1: Write the failing tests**

`foundation\tests\Qavren.Edge.Sqlite.Tests\VecBlobTests.cs`:

```csharp
using Qavren.Edge.Sqlite.Vec;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

public class VecBlobTests
{
    [Fact]
    public void Float32_RoundTrips()
    {
        float[] source = [0f, 1f, -1f, 3.14159f, float.Epsilon, -0.5f];

        var blob = VecBlob.From(source);
        var back = VecBlob.ToFloats(blob);

        Assert.Equal(source.Length * 4, blob.Length);
        Assert.Equal(source, back);
    }

    [Fact]
    public void Float32_IsLittleEndianRegardlessOfHost()
    {
        // 1.0f is 0x3F800000; little-endian on the wire is 00 00 80 3F.
        var blob = VecBlob.From([1f]);

        Assert.Equal<byte[]>([0x00, 0x00, 0x80, 0x3F], blob);
    }

    [Fact]
    public void ToFloats_RejectsBlobsThatAreNotAMultipleOfFour()
        => Assert.Throws<ArgumentException>(() => VecBlob.ToFloats(new byte[7]));

    [Fact]
    public void Int8_RoundTrips()
    {
        sbyte[] source = [0, 1, -1, 127, -128];

        var blob = VecBlob.FromInt8(source);

        Assert.Equal(source.Length, blob.Length);
        Assert.Equal(source, VecBlob.ToInt8(blob));
    }

    [Fact]
    public void Bit_PacksEightValuesPerByte()
    {
        bool[] source = [true, false, false, false, false, false, false, true, true];

        var blob = VecBlob.FromBits(source);

        Assert.Equal(2, blob.Length);
        Assert.Equal(0b1000_0001, blob[0]);
        Assert.Equal(0b0000_0001, blob[1]);
    }

    [Fact]
    public void Bit_RejectsLengthsThatAreNotAMultipleOfEight()
        => Assert.Throws<ArgumentException>(() => VecBlob.FromBits([true, false, true]));
}
```

`foundation\tests\Qavren.Edge.Sqlite.Tests\SqlBuilderTests.cs`:

```csharp
using Qavren.Edge.Sqlite.Fts;
using Qavren.Edge.Sqlite.Vec;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

public class SqlBuilderTests
{
    [Fact]
    public void VecTable_DefaultShape()
        => Assert.Equal(
            "CREATE VIRTUAL TABLE IF NOT EXISTS \"notes_vec\" USING vec0(embedding float[384] distance_metric=cosine)",
            VecTable.BuildCreateSql("notes_vec", dims: 384));

    [Fact]
    public void VecTable_WithAuxiliaryMetadataAndPartitionColumns()
    {
        var sql = VecTable.BuildCreateSql(
            "notes_vec",
            dims: 4,
            metric: VecMetric.L2,
            elementType: VecElementType.Float32,
            aux: [new VecAuxColumn("contents", "TEXT")],
            metadata: [new VecMetadataColumn("label", "TEXT")],
            partitions: [new VecPartitionKey("tenant_id", "INTEGER")],
            chunkSize: 1024);

        Assert.Equal(
            "CREATE VIRTUAL TABLE IF NOT EXISTS \"notes_vec\" USING vec0(" +
            "embedding float[4] distance_metric=l2, " +
            "tenant_id INTEGER partition key, " +
            "label TEXT, " +
            "+contents TEXT, " +
            "chunk_size=1024)",
            sql);
    }

    [Fact]
    public void VecTable_BitColumnsRejectDistanceMetric()
        => Assert.Throws<ArgumentException>(() =>
            VecTable.BuildCreateSql("t", dims: 8, metric: VecMetric.Cosine, elementType: VecElementType.Bit));

    [Fact]
    public void VecTable_BitColumnsAreAllowedWithoutAMetric()
        => Assert.Equal(
            "CREATE VIRTUAL TABLE IF NOT EXISTS \"t\" USING vec0(embedding bit[8])",
            VecTable.BuildCreateSql("t", dims: 8, metric: null, elementType: VecElementType.Bit));

    [Theory]
    [InlineData(0)]
    [InlineData(8193)]
    public void VecTable_RejectsOutOfRangeDimensions(int dims)
        => Assert.Throws<ArgumentOutOfRangeException>(() => VecTable.BuildCreateSql("t", dims));

    [Theory]
    [InlineData(0)]
    [InlineData(12)]     // not a multiple of 8
    [InlineData(8192)]   // above SQLITE_VEC_CHUNK_SIZE_MAX (4096)
    public void VecTable_RejectsInvalidChunkSize(int chunkSize)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => VecTable.BuildCreateSql("t", dims: 4, chunkSize: chunkSize));

    [Fact]
    public void Knn_BuildsMatchAndKWithOptionalFilter()
    {
        Assert.Equal(
            "SELECT rowid, distance FROM \"notes_vec\" WHERE embedding MATCH $query AND k = $k",
            Knn.BuildSql("notes_vec"));

        Assert.Equal(
            "SELECT rowid, distance FROM \"notes_vec\" WHERE embedding MATCH $query AND k = $k AND label = $label",
            Knn.BuildSql("notes_vec", where: "label = $label"));
    }

    [Fact]
    public void FtsTable_StandaloneAndExternalContent()
    {
        Assert.Equal(
            "CREATE VIRTUAL TABLE IF NOT EXISTS \"notes_fts\" USING fts5(title, body, tokenize='unicode61')",
            FtsTable.BuildCreateSql("notes_fts", ["title", "body"]));

        Assert.Equal(
            "CREATE VIRTUAL TABLE IF NOT EXISTS \"notes_fts\" USING fts5(title, body, content='notes', tokenize='porter unicode61')",
            FtsTable.BuildCreateSql("notes_fts", ["title", "body"], FtsTokenizer.Porter, contentTable: "notes"));
    }

    [Fact]
    public void FtsTable_SyncTriggersCoverInsertUpdateDelete()
    {
        var statements = FtsTable.BuildSyncTriggerSql("notes_fts", "notes", ["title", "body"]);

        Assert.Equal(3, statements.Count);
        Assert.Contains(statements, s => s.Contains("AFTER INSERT ON \"notes\"", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.Contains("AFTER DELETE ON \"notes\"", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.Contains("AFTER UPDATE ON \"notes\"", StringComparison.Ordinal));
        Assert.All(statements, s => Assert.Contains("notes_fts", s, StringComparison.Ordinal));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj" -c Release -f net10.0
```

Expected: FAIL with `CS0246: The type or namespace name 'VecBlob' could not be found`.

- [ ] **Step 3: Write `Vec\VecBlob.cs`**

```csharp
using System.Buffers.Binary;

namespace Qavren.Edge.Sqlite.Vec;

/// <summary>
/// Encodes vectors the way sqlite-vec expects them: a little-endian float32 blob for
/// <c>float[N]</c>, a signed-byte blob for <c>int8[N]</c>, and a bit-packed blob for <c>bit[N]</c>.
/// </summary>
public static class VecBlob
{
    public static byte[] From(ReadOnlySpan<float> values)
    {
        var blob = new byte[values.Length * sizeof(float)];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(blob.AsSpan(i * sizeof(float)), values[i]);
        }

        return blob;
    }

    public static float[] ToFloats(ReadOnlySpan<byte> blob)
    {
        if (blob.Length % sizeof(float) != 0)
        {
            throw new ArgumentException(
                $"A float32 vector blob length must be a multiple of {sizeof(float)}; got {blob.Length}.",
                nameof(blob));
        }

        var values = new float[blob.Length / sizeof(float)];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(blob[(i * sizeof(float))..]);
        }

        return values;
    }

    public static byte[] FromInt8(ReadOnlySpan<sbyte> values)
    {
        var blob = new byte[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            blob[i] = unchecked((byte)values[i]);
        }

        return blob;
    }

    public static sbyte[] ToInt8(ReadOnlySpan<byte> blob)
    {
        var values = new sbyte[blob.Length];
        for (var i = 0; i < blob.Length; i++)
        {
            values[i] = unchecked((sbyte)blob[i]);
        }

        return values;
    }

    /// <summary>Least significant bit first within each byte, matching <c>vec_bit()</c>.</summary>
    public static byte[] FromBits(ReadOnlySpan<bool> values)
    {
        if (values.Length % 8 != 0)
        {
            throw new ArgumentException(
                $"A bit vector length must be a multiple of 8; got {values.Length}.", nameof(values));
        }

        var blob = new byte[values.Length / 8];
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i])
            {
                blob[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        return blob;
    }

    public static bool[] ToBits(ReadOnlySpan<byte> blob)
    {
        var values = new bool[blob.Length * 8];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (blob[i / 8] & (1 << (i % 8))) != 0;
        }

        return values;
    }
}
```

- [ ] **Step 4: Write `Vec\VecTable.cs`**

```csharp
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Qavren.Edge.Sqlite.Vec;

public enum VecMetric
{
    L2,
    L1,
    Cosine,
}

public enum VecElementType
{
    Float32,
    Int8,
    Bit,
}

/// <summary>An auxiliary column, prefixed with <c>+</c>. Selectable, never filterable in a KNN WHERE clause.</summary>
public sealed record VecAuxColumn(string Name, string Type);

/// <summary>A metadata column. Filterable with = != &gt; &gt;= &lt; &lt;= only (boolean: = != only).</summary>
public sealed record VecMetadataColumn(string Name, string Type);

/// <summary>A partition key. Pre-filters shards on <c>=</c> constraints.</summary>
public sealed record VecPartitionKey(string Name, string Type);

/// <summary>Emits the <c>vec0</c> DDL a consumer could have written, with the caps enforced up front.</summary>
public static class VecTable
{
    private const int MaxDimensions = 8192;          // SQLITE_VEC_VEC0_MAX_DIMENSIONS
    private const int MaxChunkSize = 4096;           // SQLITE_VEC_CHUNK_SIZE_MAX
    private const int MaxMetadataColumns = 16;
    private const int MaxAuxColumns = 16;
    private const int MaxPartitionKeys = 4;

    public static string BuildCreateSql(
        string name,
        int dims,
        VecMetric? metric = VecMetric.Cosine,
        VecElementType elementType = VecElementType.Float32,
        IEnumerable<VecAuxColumn>? aux = null,
        IEnumerable<VecMetadataColumn>? metadata = null,
        IEnumerable<VecPartitionKey>? partitions = null,
        int? chunkSize = null,
        string vectorColumn = "embedding")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(vectorColumn);
        ArgumentOutOfRangeException.ThrowIfLessThan(dims, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dims, MaxDimensions);

        if (elementType == VecElementType.Bit && metric is not null)
        {
            // sqlite-vec rejects distance_metric on bit columns outright (constructor error);
            // bit vectors are hamming-distance only.
            throw new ArgumentException(
                "sqlite-vec rejects distance_metric on a bit column; bit vectors use hamming distance only. Pass metric: null.",
                nameof(metric));
        }

        if (chunkSize is { } chunk && (chunk <= 0 || chunk > MaxChunkSize || chunk % 8 != 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunkSize),
                chunkSize,
                $"chunk_size must satisfy 0 < N <= {MaxChunkSize.ToString(CultureInfo.InvariantCulture)} and N % 8 == 0.");
        }

        var auxList = aux?.ToArray() ?? [];
        var metadataList = metadata?.ToArray() ?? [];
        var partitionList = partitions?.ToArray() ?? [];

        ArgumentOutOfRangeException.ThrowIfGreaterThan(auxList.Length, MaxAuxColumns, nameof(aux));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(metadataList.Length, MaxMetadataColumns, nameof(metadata));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(partitionList.Length, MaxPartitionKeys, nameof(partitions));

        var parts = new List<string>
        {
            elementType switch
            {
                VecElementType.Float32 => $"{vectorColumn} float[{dims.ToString(CultureInfo.InvariantCulture)}]",
                VecElementType.Int8 => $"{vectorColumn} int8[{dims.ToString(CultureInfo.InvariantCulture)}]",
                VecElementType.Bit => $"{vectorColumn} bit[{dims.ToString(CultureInfo.InvariantCulture)}]",
                _ => throw new ArgumentOutOfRangeException(nameof(elementType)),
            }
            + (metric is null ? string.Empty : " distance_metric=" + MetricToken(metric.Value)),
        };

        parts.AddRange(partitionList.Select(p => $"{p.Name} {p.Type} partition key"));
        parts.AddRange(metadataList.Select(m => $"{m.Name} {m.Type}"));
        parts.AddRange(auxList.Select(a => $"+{a.Name} {a.Type}"));

        if (chunkSize is { } size)
        {
            parts.Add("chunk_size=" + size.ToString(CultureInfo.InvariantCulture));
        }

        var sb = new StringBuilder("CREATE VIRTUAL TABLE IF NOT EXISTS \"");
        sb.Append(name).Append("\" USING vec0(").Append(string.Join(", ", parts)).Append(')');
        return sb.ToString();
    }

    public static async Task CreateAsync(
        SqliteConnection connection,
        string name,
        int dims,
        VecMetric? metric = VecMetric.Cosine,
        VecElementType elementType = VecElementType.Float32,
        IEnumerable<VecAuxColumn>? aux = null,
        IEnumerable<VecMetadataColumn>? metadata = null,
        IEnumerable<VecPartitionKey>? partitions = null,
        int? chunkSize = null,
        string vectorColumn = "embedding",
        CancellationToken cancellationToken = default)
    {
        var sql = BuildCreateSql(name, dims, metric, elementType, aux, metadata, partitions, chunkSize, vectorColumn);
        await connection.ExecuteAsync(sql, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string MetricToken(VecMetric metric) => metric switch
    {
        VecMetric.L2 => "l2",
        VecMetric.L1 => "l1",
        VecMetric.Cosine => "cosine",
        _ => throw new ArgumentOutOfRangeException(nameof(metric)),
    };
}
```

- [ ] **Step 5: Write `Vec\Knn.cs`**

```csharp
using Microsoft.Data.Sqlite;

namespace Qavren.Edge.Sqlite.Vec;

public readonly record struct KnnHit(long RowId, float Distance);

/// <summary>
/// Emits the one KNN shape sqlite-vec supports: <c>&lt;vector&gt; MATCH ? AND k = ?</c>.
/// <c>LIMIT n</c> only works on SQLite 3.41+ and is deliberately not used.
/// </summary>
public static class Knn
{
    public static string BuildSql(string table, string? where = null, string vectorColumn = "embedding")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(vectorColumn);

        var sql = $"SELECT rowid, distance FROM \"{table}\" WHERE {vectorColumn} MATCH $query AND k = $k";
        return string.IsNullOrWhiteSpace(where) ? sql : sql + " AND " + where;
    }

    public static async Task<IReadOnlyList<KnnHit>> QueryAsync(
        SqliteConnection connection,
        string table,
        ReadOnlyMemory<float> query,
        int k,
        string? where = null,
        IEnumerable<SqliteParameter>? parameters = null,
        string vectorColumn = "embedding",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);

        await using var command = connection.CreateCommand();
        command.CommandText = BuildSql(table, where, vectorColumn);
        command.Parameters.Add(new SqliteParameter("$query", VecBlob.From(query.Span)));
        command.Parameters.Add(new SqliteParameter("$k", k));
        if (parameters is not null)
        {
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(parameter);
            }
        }

        var hits = new List<KnnHit>(k);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            hits.Add(new KnnHit(reader.GetInt64(0), (float)reader.GetDouble(1)));
        }

        return hits;
    }
}
```

- [ ] **Step 6: Write `Vec\VecFunctions.cs`**

```csharp
namespace Qavren.Edge.Sqlite.Vec;

/// <summary>String builders for sqlite-vec's scalar functions. No magic, no hidden state.</summary>
public static class VecFunctions
{
    /// <summary>Returns the version string, e.g. <c>v0.1.9</c> — note the leading <c>v</c>.</summary>
    public const string Version = "vec_version()";

    public const string Debug = "vec_debug()";

    public static string Length(string expression) => $"vec_length({expression})";

    public static string Type(string expression) => $"vec_type({expression})";

    public static string Normalize(string expression) => $"vec_normalize({expression})";

    public static string ToJson(string expression) => $"vec_to_json({expression})";

    public static string DistanceCosine(string a, string b) => $"vec_distance_cosine({a}, {b})";

    public static string DistanceL2(string a, string b) => $"vec_distance_l2({a}, {b})";

    public static string DistanceL1(string a, string b) => $"vec_distance_l1({a}, {b})";

    public static string DistanceHamming(string a, string b) => $"vec_distance_hamming({a}, {b})";
}
```

- [ ] **Step 7: Write `Fts\FtsTable.cs`**

```csharp
using Microsoft.Data.Sqlite;

namespace Qavren.Edge.Sqlite.Fts;

public enum FtsTokenizer
{
    Unicode61,
    Porter,
    Ascii,
    Trigram,
}

public static class FtsTable
{
    public static string BuildCreateSql(
        string name,
        IReadOnlyList<string> columns,
        FtsTokenizer tokenizer = FtsTokenizer.Unicode61,
        string? contentTable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new ArgumentException("An FTS5 table needs at least one column.", nameof(columns));
        }

        var parts = new List<string>(columns);
        if (!string.IsNullOrWhiteSpace(contentTable))
        {
            parts.Add($"content='{contentTable}'");
        }

        parts.Add($"tokenize='{TokenizerToken(tokenizer)}'");
        return $"CREATE VIRTUAL TABLE IF NOT EXISTS \"{name}\" USING fts5({string.Join(", ", parts)})";
    }

    /// <summary>External-content tables need triggers; FTS5 does not observe the content table itself.</summary>
    public static IReadOnlyList<string> BuildSyncTriggerSql(
        string ftsTable,
        string contentTable,
        IReadOnlyList<string> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ftsTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentTable);
        ArgumentNullException.ThrowIfNull(columns);

        var columnList = string.Join(", ", columns);
        var newValues = string.Join(", ", columns.Select(c => "new." + c));
        var oldValues = string.Join(", ", columns.Select(c => "old." + c));

        return
        [
            $"CREATE TRIGGER IF NOT EXISTS \"{ftsTable}_ai\" AFTER INSERT ON \"{contentTable}\" BEGIN " +
            $"INSERT INTO \"{ftsTable}\"(rowid, {columnList}) VALUES (new.rowid, {newValues}); END",

            $"CREATE TRIGGER IF NOT EXISTS \"{ftsTable}_ad\" AFTER DELETE ON \"{contentTable}\" BEGIN " +
            $"INSERT INTO \"{ftsTable}\"(\"{ftsTable}\", rowid, {columnList}) VALUES ('delete', old.rowid, {oldValues}); END",

            $"CREATE TRIGGER IF NOT EXISTS \"{ftsTable}_au\" AFTER UPDATE ON \"{contentTable}\" BEGIN " +
            $"INSERT INTO \"{ftsTable}\"(\"{ftsTable}\", rowid, {columnList}) VALUES ('delete', old.rowid, {oldValues}); " +
            $"INSERT INTO \"{ftsTable}\"(rowid, {columnList}) VALUES (new.rowid, {newValues}); END",
        ];
    }

    public static async Task CreateAsync(
        SqliteConnection connection,
        string name,
        IReadOnlyList<string> columns,
        FtsTokenizer tokenizer = FtsTokenizer.Unicode61,
        string? contentTable = null,
        CancellationToken cancellationToken = default)
    {
        await connection.ExecuteAsync(
            BuildCreateSql(name, columns, tokenizer, contentTable),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static async Task CreateSyncTriggersAsync(
        SqliteConnection connection,
        string ftsTable,
        string contentTable,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken = default)
    {
        foreach (var sql in BuildSyncTriggerSql(ftsTable, contentTable, columns))
        {
            await connection.ExecuteAsync(sql, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static string TokenizerToken(FtsTokenizer tokenizer) => tokenizer switch
    {
        FtsTokenizer.Unicode61 => "unicode61",
        FtsTokenizer.Porter => "porter unicode61",
        FtsTokenizer.Ascii => "ascii",
        FtsTokenizer.Trigram => "trigram",
        _ => throw new ArgumentOutOfRangeException(nameof(tokenizer)),
    };
}
```

- [ ] **Step 8: Write `SqliteConnectionExtensions.cs`**

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite.Vec;

namespace Qavren.Edge.Sqlite;

/// <summary>
/// Small, visible-SQL helpers. Not an ORM: EF Core, sqlite-net-pcl and Dapper remain the
/// recommended object-mapping layers and all work unchanged on top of this provider.
/// </summary>
public static class SqliteConnectionExtensions
{
    public static async Task<int> ExecuteAsync(
        this SqliteConnection connection,
        string sql,
        IEnumerable<SqliteParameter>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        await using var command = Prepare(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T?> ScalarAsync<T>(
        this SqliteConnection connection,
        string sql,
        IEnumerable<SqliteParameter>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        await using var command = Prepare(connection, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T), provider: null);
    }

    public static async Task<IReadOnlyList<T>> QueryAsync<T>(
        this SqliteConnection connection,
        string sql,
        Func<SqliteDataReader, T> map,
        IEnumerable<SqliteParameter>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);

        await using var command = Prepare(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    /// <summary>
    /// Binds each public instance property of <paramref name="parameters"/> as <c>$Name</c>.
    /// <c>ReadOnlyMemory&lt;float&gt;</c> and <c>float[]</c> bind as a sqlite-vec float32 blob.
    /// </summary>
    [RequiresUnreferencedCode("Reflects over the properties of the supplied object. Use the SqliteParameter overload in trimmed or AOT apps.")]
    public static IReadOnlyList<SqliteParameter> ToParameters(object parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var list = new List<SqliteParameter>();
        foreach (var property in parameters.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var raw = property.GetValue(parameters);
            var value = raw switch
            {
                ReadOnlyMemory<float> memory => VecBlob.From(memory.Span),
                float[] array => VecBlob.From(array),
                null => (object)DBNull.Value,
                _ => raw,
            };

            list.Add(new SqliteParameter("$" + property.Name, value));
        }

        return list;
    }

    private static SqliteCommand Prepare(
        SqliteConnection connection,
        string sql,
        IEnumerable<SqliteParameter>? parameters)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(parameter);
            }
        }

        return command;
    }
}
```

- [ ] **Step 9: Run to verify the tests pass**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj" -c Release -f net10.0
```

Expected: 22 tests pass, exit code 0.

- [ ] **Step 10: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj" -c Release -f net10.0
```

Expected: `Failed: 0`, exit code 0.

---

---

### Task 6.2: CI workflows

**Local-verifiable:** partially. The YAML lints here and the TRX→JUnit converter is unit-tested
here; **no workflow can execute** during implementation, because the repo
(`qavren-oss/qavren-edge` — the org exists, spec §16.1) has no `main` yet and implementers never
push. First real run is the owner's post-plan push (README bootstrap checklist).

This task compiles no C#, which is why it can share Wave 6 with Task 6.1's edits to
`Qavren.Edge.Sqlite`.

**Files:**
- Modify: `.github\workflows\native.yml`
- Modify: `.github\workflows\ci.yml`
- Modify: `.github\workflows\release.yml`
- Modify: `.github\workflows\upstream-pins.yml`
- Create: `.config\dotnet-tools.json`
- Create: `foundation\native\scripts\build-linux.sh`
- Create: `foundation\native\scripts\build-android.sh`
- Create: `foundation\native\scripts\build-apple.sh`
- Create: `foundation\tools\trx2junit\trx2junit.py`
- Create: `foundation\tools\trx2junit\fixture.trx`
- Create: `foundation\tools\trx2junit\test_trx2junit.py`
- Create: `foundation\tools\ci-checks\assert-workflows.py`

- [ ] **Step 1: Write `foundation\native\scripts\build-linux.sh`**

```bash
#!/usr/bin/env bash
# Builds libqedge_sqlite3.so (or libqedge_sqlcipher.so) for one Linux RID.
set -euo pipefail

NATIVE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${1:-linux-x64}"
CIPHER="${2:-OFF}"
BUILD_SHA="${3:-local}"

case "$RID" in
  linux-x64)   CC_BIN=gcc ;;
  linux-arm64) CC_BIN=aarch64-linux-gnu-gcc ;;
  *) echo "unsupported rid: $RID" >&2; exit 2 ;;
esac

BUILD_DIR="$NATIVE_ROOT/build/$RID-$CIPHER"
cmake -S "$NATIVE_ROOT" -B "$BUILD_DIR" -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_C_COMPILER="$CC_BIN" \
  -DQEDGE_CIPHER="$CIPHER" \
  -DQEDGE_BUILD_SHA="$BUILD_SHA"
cmake --build "$BUILD_DIR"

mkdir -p "$NATIVE_ROOT/artifacts/$RID"
cp "$BUILD_DIR"/out/lib*.so "$NATIVE_ROOT/artifacts/$RID/"
echo "OK: $NATIVE_ROOT/artifacts/$RID"
```

- [ ] **Step 2: Write `foundation\native\scripts\build-android.sh`**

```bash
#!/usr/bin/env bash
# Builds libqedge_sqlite3.so for every Android ABI using the pinned NDK.
set -euo pipefail

NATIVE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CIPHER="${1:-OFF}"
BUILD_SHA="${2:-local}"
: "${ANDROID_NDK_ROOT:?ANDROID_NDK_ROOT must point at NDK r28 or newer}"

declare -A RID_FOR_ABI=(
  [arm64-v8a]=android-arm64
  [x86_64]=android-x64
  [armeabi-v7a]=android-arm
)

for ABI in "${!RID_FOR_ABI[@]}"; do
  RID="${RID_FOR_ABI[$ABI]}"
  BUILD_DIR="$NATIVE_ROOT/build/$RID-$CIPHER"
  cmake -S "$NATIVE_ROOT" -B "$BUILD_DIR" -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE="$ANDROID_NDK_ROOT/build/cmake/android.toolchain.cmake" \
    -DANDROID_ABI="$ABI" \
    -DANDROID_PLATFORM=android-21 \
    -DANDROID_STL=none \
    -DCMAKE_BUILD_TYPE=Release \
    -DQEDGE_CIPHER="$CIPHER" \
    -DQEDGE_BUILD_SHA="$BUILD_SHA"
  cmake --build "$BUILD_DIR"

  mkdir -p "$NATIVE_ROOT/artifacts/$RID"
  cp "$BUILD_DIR"/out/lib*.so "$NATIVE_ROOT/artifacts/$RID/"

  # A silently 4 KB-aligned .so fails Play submission, not the build, and raises XA0141 in every
  # consuming app. Assert it here rather than discovering it at store review.
  for SO in "$NATIVE_ROOT/artifacts/$RID"/lib*.so; do
    if ! "$ANDROID_NDK_ROOT"/toolchains/llvm/prebuilt/*/bin/llvm-readelf -l "$SO" \
         | grep -E '^\s+LOAD' | grep -q '0x4000'; then
      echo "FAIL: $SO is not 16 KB aligned" >&2
      exit 1
    fi
  done
done

echo "OK: android artifacts under $NATIVE_ROOT/artifacts"
```

- [ ] **Step 3: Write `foundation\native\scripts\build-apple.sh`**

One universal archive per platform slice, then a single `xcodebuild -create-xcframework`. `lipo` refuses to put two archives of the same architecture in one fat file, and `-create-xcframework` refuses two slices it considers equivalent, so device / simulator / maccatalyst / macos must each be lipo'd separately first.

```bash
#!/usr/bin/env bash
set -euo pipefail

NATIVE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CIPHER="${1:-OFF}"
BUILD_SHA="${2:-local}"
NAME=$([ "$CIPHER" = "ON" ] && echo qedge_sqlcipher || echo qedge_sqlite3)

build_slice() {  # $1 slice, $2 sdk, $3 arch, $4 target-triple
  local slice="$1" sdk="$2" arch="$3" triple="$4"
  local dir="$NATIVE_ROOT/build/apple/$slice-$arch-$CIPHER"
  cmake -S "$NATIVE_ROOT" -B "$dir" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_OSX_SYSROOT="$(xcrun --sdk "$sdk" --show-sdk-path)" \
    -DCMAKE_OSX_ARCHITECTURES="$arch" \
    -DCMAKE_C_FLAGS="-target $triple" \
    -DBUILD_SHARED_LIBS=OFF \
    -DQEDGE_CIPHER="$CIPHER" \
    -DQEDGE_BUILD_SHA="$BUILD_SHA"
  cmake --build "$dir"
  echo "$dir/out/lib$NAME.a"
}

OUT="$NATIVE_ROOT/artifacts/apple"
rm -rf "$OUT"; mkdir -p "$OUT"/{ios,iossim,maccatalyst,macos}

lipo -create "$(build_slice ios iphoneos arm64 arm64-apple-ios15.0)" -output "$OUT/ios/lib$NAME.a"
lipo -create \
  "$(build_slice iossim iphonesimulator arm64 arm64-apple-ios15.0-simulator)" \
  "$(build_slice iossim iphonesimulator x86_64 x86_64-apple-ios15.0-simulator)" \
  -output "$OUT/iossim/lib$NAME.a"
lipo -create \
  "$(build_slice maccatalyst macosx arm64 arm64-apple-ios15.0-macabi)" \
  "$(build_slice maccatalyst macosx x86_64 x86_64-apple-ios15.0-macabi)" \
  -output "$OUT/maccatalyst/lib$NAME.a"
lipo -create \
  "$(build_slice macos macosx arm64 arm64-apple-macos12.0)" \
  "$(build_slice macos macosx x86_64 x86_64-apple-macos12.0)" \
  -output "$OUT/macos/lib$NAME.a"

xcodebuild -create-xcframework \
  -library "$OUT/ios/lib$NAME.a" \
  -library "$OUT/iossim/lib$NAME.a" \
  -library "$OUT/maccatalyst/lib$NAME.a" \
  -library "$OUT/macos/lib$NAME.a" \
  -output "$OUT/$NAME.xcframework"

# Mac Catalyst and macOS desktop consume a dylib through runtimes/, not the xcframework.
for rid_arch in "maccatalyst-arm64:maccatalyst:arm64" "maccatalyst-x64:maccatalyst:x86_64" \
                "osx-arm64:macos:arm64" "osx-x64:macos:x86_64"; do
  IFS=: read -r rid slice arch <<<"$rid_arch"
  dir="$NATIVE_ROOT/build/apple/$slice-$arch-dylib-$CIPHER"
  triple=$([ "$slice" = maccatalyst ] && echo "$arch-apple-ios15.0-macabi" || echo "$arch-apple-macos12.0")
  cmake -S "$NATIVE_ROOT" -B "$dir" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_OSX_SYSROOT="$(xcrun --sdk macosx --show-sdk-path)" \
    -DCMAKE_OSX_ARCHITECTURES="$arch" \
    -DCMAKE_C_FLAGS="-target $triple" \
    -DQEDGE_CIPHER="$CIPHER" -DQEDGE_BUILD_SHA="$BUILD_SHA"
  cmake --build "$dir"
  mkdir -p "$NATIVE_ROOT/artifacts/$rid"
  cp "$dir/out/lib$NAME.dylib" "$NATIVE_ROOT/artifacts/$rid/"
done

echo "OK: $OUT/$NAME.xcframework"
```

The CMakeLists builds a `SHARED` library; for the static Apple slices add `-DBUILD_SHARED_LIBS=OFF` support by changing `add_library(<target> SHARED ...)` to `add_library(<target> ...)` in `foundation/native/CMakeLists.txt`, which makes CMake honour `BUILD_SHARED_LIBS` (defaulting to static). Set `-DBUILD_SHARED_LIBS=ON` in `build-windows.ps1`, `build-linux.sh` and `build-android.sh`, and leave it off for the xcframework slices.

- [ ] **Step 4: Write the TRX→JUnit converter and its test**

Spec §13 requires device-test results "published as JUnit". DeviceRunners' `dotnet test`
integration writes **TRX** and has no JUnit writer, and pinning a third-party `trx2junit` tool
version that cannot be restored on this box is not acceptable. So the repo carries a ~90-line
converter, and — unlike everything else in this task — it **is** locally verifiable.

`foundation\tools\trx2junit\trx2junit.py`:

```python
#!/usr/bin/env python3
"""Convert one or more VSTest TRX files into a single JUnit XML document.

Usage: trx2junit.py --suite NAME --out PATH TRX [TRX ...]

Only the elements DeviceRunners actually emits are handled: UnitTestResult rows with an
outcome of Passed / Failed / NotExecuted, an optional Output/ErrorInfo block, and a duration.
"""
import argparse
import os
import sys
import xml.etree.ElementTree as ET

TRX_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def _duration_seconds(value):
    # TRX durations look like "00:00:01.2345678"; anything unparseable counts as zero.
    if not value:
        return 0.0
    try:
        hours, minutes, seconds = value.split(":")
        return int(hours) * 3600 + int(minutes) * 60 + float(seconds)
    except (ValueError, AttributeError):
        return 0.0


def _results(path):
    root = ET.parse(path).getroot()
    for result in root.iter(TRX_NS + "UnitTestResult"):
        output = result.find(TRX_NS + "Output")
        message = ""
        stack = ""
        stdout = ""
        if output is not None:
            info = output.find(TRX_NS + "ErrorInfo")
            if info is not None:
                message = (info.findtext(TRX_NS + "Message") or "").strip()
                stack = (info.findtext(TRX_NS + "StackTrace") or "").strip()
            stdout = (output.findtext(TRX_NS + "StdOut") or "").strip()
        yield {
            "name": result.get("testName") or "unnamed",
            "outcome": result.get("outcome") or "Failed",
            "seconds": _duration_seconds(result.get("duration")),
            "message": message,
            "stack": stack,
            "stdout": stdout,
        }


def convert(trx_paths, suite_name):
    cases = [case for path in trx_paths for case in _results(path)]

    failures = sum(1 for c in cases if c["outcome"] == "Failed")
    skipped = sum(1 for c in cases if c["outcome"] == "NotExecuted")

    suites = ET.Element("testsuites")
    suite = ET.SubElement(
        suites,
        "testsuite",
        name=suite_name,
        tests=str(len(cases)),
        failures=str(failures),
        errors="0",
        skipped=str(skipped),
        time="%.3f" % sum(c["seconds"] for c in cases),
    )

    for case in cases:
        node = ET.SubElement(
            suite,
            "testcase",
            classname=case["name"].rsplit(".", 1)[0] if "." in case["name"] else suite_name,
            name=case["name"],
            time="%.3f" % case["seconds"],
        )
        if case["outcome"] == "Failed":
            failure = ET.SubElement(node, "failure", message=case["message"] or "test failed", type="AssertionError")
            failure.text = case["stack"] or case["message"]
        elif case["outcome"] == "NotExecuted":
            ET.SubElement(node, "skipped")
        if case["stdout"]:
            ET.SubElement(node, "system-out").text = case["stdout"]

    return suites, len(cases), failures, skipped


def main(argv):
    parser = argparse.ArgumentParser()
    parser.add_argument("--suite", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("trx", nargs="+")
    args = parser.parse_args(argv)

    existing = [p for p in args.trx if os.path.isfile(p)]
    if not existing:
        print("trx2junit: no TRX files found in %r" % (args.trx,), file=sys.stderr)
        return 1

    tree, total, failures, skipped = convert(existing, args.suite)
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    ET.ElementTree(tree).write(args.out, encoding="utf-8", xml_declaration=True)
    print("trx2junit: %d tests, %d failed, %d skipped -> %s" % (total, failures, skipped, args.out))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
```

`foundation\tools\trx2junit\fixture.trx` — a minimal but real TRX shape, one of each outcome:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<TestRun id="00000000-0000-0000-0000-000000000000" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="Qavren.Edge.DeviceTests.NativeSmokeTests.NativeLibraryLoadsAndReportsVersions" outcome="Passed" duration="00:00:00.1230000" />
    <UnitTestResult testName="Qavren.Edge.DeviceTests.NativeSmokeTests.Vec0AndFts5AreAvailableOnDevice" outcome="Failed" duration="00:00:02.5000000">
      <Output>
        <StdOut>opening device-tests.db</StdOut>
        <ErrorInfo>
          <Message>Assert.Equal() Failure</Message>
          <StackTrace>   at Qavren.Edge.DeviceTests.NativeSmokeTests.Vec0AndFts5AreAvailableOnDevice()</StackTrace>
        </ErrorInfo>
      </Output>
    </UnitTestResult>
    <UnitTestResult testName="Qavren.Edge.DeviceTests.NativeSmokeTests.SkippedExample" outcome="NotExecuted" duration="00:00:00.0000000" />
  </Results>
</TestRun>
```

`foundation\tools\trx2junit\test_trx2junit.py` — write this test FIRST and watch it fail with
`ModuleNotFoundError: No module named 'trx2junit'`, then write the converter above:

It uses only the standard library — no pytest — so it runs identically here and on any runner.

```python
"""Self-contained tests for trx2junit. Run: python test_trx2junit.py"""
import os
import sys
import tempfile
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import trx2junit  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
FIXTURE = os.path.join(HERE, "fixture.trx")


def test_counts_and_shape():
    tree, total, failures, skipped = trx2junit.convert([FIXTURE], "device-test")
    assert (total, failures, skipped) == (3, 1, 1), (total, failures, skipped)

    suite = tree.find("testsuite")
    assert suite.get("name") == "device-test"
    assert suite.get("tests") == "3"
    assert suite.get("failures") == "1"
    assert suite.get("skipped") == "1"
    assert abs(float(suite.get("time")) - 2.623) < 0.001, suite.get("time")

    cases = suite.findall("testcase")
    assert len(cases) == 3
    assert cases[1].find("failure").get("message") == "Assert.Equal() Failure"
    assert "Vec0AndFts5AreAvailableOnDevice" in cases[1].find("failure").text
    assert cases[1].find("system-out").text == "opening device-tests.db"
    assert cases[2].find("skipped") is not None


def test_missing_files_exit_code():
    with tempfile.TemporaryDirectory() as tmp:
        rc = trx2junit.main(["--suite", "x", "--out", os.path.join(tmp, "o.xml"), os.path.join(tmp, "nope.trx")])
    assert rc == 1


def test_writes_parsable_xml():
    with tempfile.TemporaryDirectory() as tmp:
        out = os.path.join(tmp, "junit", "device.xml")
        rc = trx2junit.main(["--suite", "device-test", "--out", out, FIXTURE])
        assert rc == 0
        assert ET.parse(out).getroot().tag == "testsuites"


if __name__ == "__main__":
    failed = 0
    for name, fn in sorted((n, f) for n, f in globals().items() if n.startswith("test_")):
        try:
            fn()
            print("PASS %s" % name)
        except AssertionError as ex:
            failed += 1
            print("FAIL %s: %s" % (name, ex))
    print("OK: 3 tests passed" if failed == 0 else "FAILED: %d" % failed)
    raise SystemExit(1 if failed else 0)
```

Run it. Written test-first, the first run must fail with
`ModuleNotFoundError: No module named 'trx2junit'`; after the converter exists it must print
three `PASS` lines:

```powershell
python "C:\Users\steve\projects\qavren-edge\foundation\tools\trx2junit\test_trx2junit.py"
```

Expected:

```
PASS test_counts_and_shape
PASS test_missing_files_exit_code
PASS test_writes_parsable_xml
OK: 3 tests passed
```

- [ ] **Step 5: Write `.github\workflows\native.yml`**

Three structural points, all required by the spec and explained in adjustments 28 and 34.

1. The pull-request path filter moves **off** `on.pull_request.paths` and **into** a `changes`
   job. A required status check that never reports blocks a PR forever, so `native.yml` must
   always report something on every PR — it just must not rebuild the world when no native file
   moved.
2. Every architecture is built, including `win-arm64` (spec §10.3), for both the plain and the
   cipher variant. The Windows job loops the four combinations.
3. **Spec §10.4: "Cache key = hash of `native/versions.json` + `native/**` + workflow file;
   managed-only PRs download the cached natives instead of rebuilding."** That is implemented
   twice over, and there is deliberately **no `force` input** — a `force` flag would let a caller
   bypass the path filter and rebuild four variants on three runners for a PR that changed only
   C#, which is the exact behaviour the spec forbids.
   * The `build` matrix caches the **built** artifact tree (`foundation/native/artifacts`) under
     `qedge-native-<name>-<hash>`, where `<hash>` is `hashFiles()` over `versions.json`,
     `CMakeLists.txt`, `src/**`, `cmake/**`, `scripts/**` **and** `.github/workflows/native.yml`
     — the spec's key, verbatim. On a hit, every fetch/compile step is skipped and the job only
     re-uploads.
   * On a pull request that touched **no** native path, the whole matrix is skipped and a single
     `reuse` job on `ubuntu-24.04` restores all three caches and uploads them as the same three
     artifact names. Zero macOS minutes, zero Windows minutes, zero compilation.
   * The separate `_deps/download` cache stays: it is keyed on `versions.json` alone and saves
     the upstream tarball downloads on a genuine rebuild. It is complementary, not a substitute.

```yaml
name: native

on:
  push:
    branches: [main]
  pull_request:
  workflow_call:
  workflow_dispatch:

permissions:
  contents: read

env:
  DOTNET_NOLOGO: 'true'

jobs:
  changes:
    runs-on: ubuntu-24.04
    outputs:
      native: ${{ steps.filter.outputs.native }}
    steps:
      - uses: actions/checkout@v4
      - uses: dorny/paths-filter@v3
        id: filter
        with:
          filters: |
            native:
              - 'foundation/native/**'
              - '.github/workflows/native.yml'

  build:
    needs: changes
    # Builds on every non-pull_request entry point (push to main, workflow_dispatch, and the
    # workflow_call from release.yml on a tag push), and on a pull request ONLY when a native
    # path moved. A pull request that changed no native file gets the `reuse` job instead.
    # `github.event_name` inside a called workflow is the CALLER's event, which is exactly the
    # discrimination wanted: ci.yml on a PR reuses, release.yml on a tag builds.
    if: ${{ github.event_name != 'pull_request' || needs.changes.outputs.native == 'true' }}
    strategy:
      fail-fast: false
      matrix:
        include:
          - { os: windows-2025,  name: windows }
          - { os: ubuntu-24.04,  name: linux-android }
          - { os: macos-15,      name: apple }
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4

      # Spec 10.4: cache key = versions.json + native/** + this workflow file. A hit means the
      # exact same native inputs have already been built on this OS, so skip straight to upload.
      - name: Cache built natives
        id: native-cache
        uses: actions/cache@v4
        with:
          path: foundation/native/artifacts
          key: qedge-native-${{ matrix.name }}-${{ hashFiles('foundation/native/versions.json', 'foundation/native/CMakeLists.txt', 'foundation/native/src/**', 'foundation/native/cmake/**', 'foundation/native/scripts/**', '.github/workflows/native.yml') }}

      - name: Cache fetched sources
        if: steps.native-cache.outputs.cache-hit != 'true'
        uses: actions/cache@v4
        with:
          path: foundation/native/_deps/download
          key: qedge-deps-${{ hashFiles('foundation/native/versions.json') }}

      - name: Fetch and verify pinned sources (Windows)
        if: steps.native-cache.outputs.cache-hit != 'true' && matrix.name == 'windows'
        shell: pwsh
        run: |
          $env:PATH = "C:\Program Files\Git\mingw64\bin;$env:PATH"
          ./foundation/native/scripts/fetch-sources.ps1 -IncludeCipher

      - name: Fetch and verify pinned sources (Unix)
        if: steps.native-cache.outputs.cache-hit != 'true' && matrix.name != 'windows'
        shell: pwsh
        run: ./foundation/native/scripts/fetch-sources.ps1 -IncludeCipher

      - name: Build (Windows, x64 + arm64, plain + cipher)
        if: steps.native-cache.outputs.cache-hit != 'true' && matrix.name == 'windows'
        shell: pwsh
        run: |
          $env:PATH = "C:\Program Files\Git\mingw64\bin;$env:PATH"
          foreach ($arch in 'x64', 'arm64') {
            ./foundation/native/scripts/build-windows.ps1 -Arch $arch -BuildSha "${{ github.sha }}"
            ./foundation/native/scripts/build-windows.ps1 -Arch $arch -Cipher -BuildSha "${{ github.sha }}"
          }
          # windows-2025 always carries VC.Tools.ARM64, so -SkipIfToolsetMissing is deliberately
          # NOT passed here: a missing ARM64 toolset must fail the build, not silently skip.
          foreach ($rid in 'win-x64', 'win-arm64') {
            foreach ($dll in 'qedge_sqlite3.dll', 'qedge_sqlcipher.dll') {
              $p = "foundation/native/artifacts/$rid/$dll"
              if (-not (Test-Path $p)) { throw "missing native artifact: $p" }
            }
          }
          Write-Host 'OK: 4 Windows artifacts produced'

      - name: Install Linux cross toolchain
        if: steps.native-cache.outputs.cache-hit != 'true' && matrix.name == 'linux-android'
        run: |
          sudo apt-get update
          sudo apt-get install -y ninja-build crossbuild-essential-arm64 tcl

      - name: Build Linux and Android
        if: steps.native-cache.outputs.cache-hit != 'true' && matrix.name == 'linux-android'
        run: |
          chmod +x foundation/native/scripts/*.sh
          ./foundation/native/scripts/build-linux.sh linux-x64 OFF "${{ github.sha }}"
          ./foundation/native/scripts/build-linux.sh linux-arm64 OFF "${{ github.sha }}"
          ./foundation/native/scripts/build-linux.sh linux-x64 ON "${{ github.sha }}"
          ./foundation/native/scripts/build-linux.sh linux-arm64 ON "${{ github.sha }}"
          # The default NDK on ubuntu-24.04 is r27, which is NOT 16 KB aligned by default.
          export ANDROID_NDK_ROOT="$ANDROID_NDK_LATEST_HOME"
          ./foundation/native/scripts/build-android.sh OFF "${{ github.sha }}"
          ./foundation/native/scripts/build-android.sh ON "${{ github.sha }}"

      - name: Select Xcode
        if: steps.native-cache.outputs.cache-hit != 'true' && matrix.name == 'apple'
        uses: maxim-lobanov/setup-xcode@v1
        with:
          xcode-version: '26.2'

      - name: Build Apple slices
        if: steps.native-cache.outputs.cache-hit != 'true' && matrix.name == 'apple'
        run: |
          brew install ninja
          chmod +x foundation/native/scripts/*.sh
          ./foundation/native/scripts/build-apple.sh OFF "${{ github.sha }}"
          ./foundation/native/scripts/build-apple.sh ON "${{ github.sha }}"

      - uses: actions/upload-artifact@v4
        with:
          name: native-${{ matrix.name }}
          path: foundation/native/artifacts/**
          retention-days: 30
          if-no-files-found: error

  # Spec 10.4: "managed-only PRs download the cached natives instead of rebuilding."
  # One ubuntu job restores all three OS caches (they were saved by the last build on `main`
  # for this exact native tree) and re-publishes them under the SAME three artifact names, so
  # every downstream ci.yml job is oblivious to which path produced them. No macOS runner, no
  # Windows runner, no compiler.
  reuse:
    needs: changes
    if: ${{ github.event_name == 'pull_request' && needs.changes.outputs.native != 'true' }}
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@v4

      - name: Restore cached Windows natives
        id: c-windows
        uses: actions/cache/restore@v4
        with:
          path: foundation/native/artifacts
          key: qedge-native-windows-${{ hashFiles('foundation/native/versions.json', 'foundation/native/CMakeLists.txt', 'foundation/native/src/**', 'foundation/native/cmake/**', 'foundation/native/scripts/**', '.github/workflows/native.yml') }}
      - uses: actions/upload-artifact@v4
        if: steps.c-windows.outputs.cache-hit == 'true'
        with: { name: native-windows, path: foundation/native/artifacts/**, retention-days: 30, if-no-files-found: error }
      - name: Clear staging
        run: rm -rf foundation/native/artifacts

      - name: Restore cached Linux/Android natives
        id: c-linux
        uses: actions/cache/restore@v4
        with:
          path: foundation/native/artifacts
          key: qedge-native-linux-android-${{ hashFiles('foundation/native/versions.json', 'foundation/native/CMakeLists.txt', 'foundation/native/src/**', 'foundation/native/cmake/**', 'foundation/native/scripts/**', '.github/workflows/native.yml') }}
      - uses: actions/upload-artifact@v4
        if: steps.c-linux.outputs.cache-hit == 'true'
        with: { name: native-linux-android, path: foundation/native/artifacts/**, retention-days: 30, if-no-files-found: error }
      - name: Clear staging
        run: rm -rf foundation/native/artifacts

      - name: Restore cached Apple natives
        id: c-apple
        uses: actions/cache/restore@v4
        with:
          path: foundation/native/artifacts
          key: qedge-native-apple-${{ hashFiles('foundation/native/versions.json', 'foundation/native/CMakeLists.txt', 'foundation/native/src/**', 'foundation/native/cmake/**', 'foundation/native/scripts/**', '.github/workflows/native.yml') }}
      - uses: actions/upload-artifact@v4
        if: steps.c-apple.outputs.cache-hit == 'true'
        with: { name: native-apple, path: foundation/native/artifacts/**, retention-days: 30, if-no-files-found: error }

      # A cold cache means `main` has never built THIS native tree. Fail loudly with the fix
      # rather than letting ci.yml die later on a missing artifact download.
      - name: Require all three caches
        run: |
          echo "windows=${{ steps.c-windows.outputs.cache-hit }} linux=${{ steps.c-linux.outputs.cache-hit }} apple=${{ steps.c-apple.outputs.cache-hit }}"
          if [ "${{ steps.c-windows.outputs.cache-hit }}" != "true" ]              || [ "${{ steps.c-linux.outputs.cache-hit }}" != "true" ]              || [ "${{ steps.c-apple.outputs.cache-hit }}" != "true" ]; then
            echo "::error::No cached natives for this native tree. Run the 'native' workflow on main via workflow_dispatch once, then re-run this PR."
            exit 1
          fi
          echo "reuse: all three native artifact sets restored from cache"

  # The required status check. It ALWAYS runs and always reports, so a managed-only PR is not
  # blocked waiting on a build that was correctly skipped, and a real native failure still
  # turns the check red. `always()` is required or a skipped `build` would skip this too.
  # Exactly one of `build` and `reuse` runs; the other is `skipped`, which is a pass.
  native-gate:
    needs: [changes, build, reuse]
    if: always()
    runs-on: ubuntu-24.04
    steps:
      - name: Report
        run: |
          echo "changes=${{ needs.changes.outputs.native }} build=${{ needs.build.result }} reuse=${{ needs.reuse.result }}"
          fail=0
          case "${{ needs.build.result }}" in
            success|skipped) ;;
            *) echo "native-gate: FAIL (build ${{ needs.build.result }})"; fail=1 ;;
          esac
          case "${{ needs.reuse.result }}" in
            success|skipped) ;;
            *) echo "native-gate: FAIL (reuse ${{ needs.reuse.result }})"; fail=1 ;;
          esac
          if [ "${{ needs.build.result }}" = "skipped" ] && [ "${{ needs.reuse.result }}" = "skipped" ]; then
            echo "native-gate: FAIL (neither build nor reuse ran)"; fail=1
          fi
          [ "$fail" = "0" ] && echo "native-gate: pass"
          exit $fail
```

- [ ] **Step 6: Write `.github\workflows\ci.yml`**

```yaml
name: ci

on:
  push:
    branches: [main]
  pull_request:
  workflow_dispatch:

permissions:
  contents: read

env:
  DOTNET_NOLOGO: 'true'
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: 'true'

jobs:
  # Deliberately no `with:` block. native.yml has no bypass input at all, so a pull request
  # that touched no `foundation/native/**` path takes native.yml's `reuse` job, which restores
  # the cached artifacts on one ubuntu runner instead of rebuilding four variants on three
  # operating systems (spec 10.4, adjustment 34).
  natives:
    uses: ./.github/workflows/native.yml

  test:
    needs: natives
    strategy:
      fail-fast: false
      matrix:
        os: [windows-2025, ubuntu-24.04, macos-15]
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0        # MinVer needs history, or every package builds as 0.0.0-alpha.0
          filter: tree:0

      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      - uses: actions/download-artifact@v4
        with:
          pattern: native-*
          merge-multiple: true
          path: foundation/native/artifacts

      - name: Assert no SQLitePCLRaw bundle package is in the graph
        shell: pwsh
        run: |
          dotnet restore QavrenEdge.slnx
          $hits = Get-ChildItem -Recurse -Filter 'project.assets.json' |
                  Select-String -Pattern 'SQLitePCLRaw\.bundle_' -SimpleMatch
          if ($hits) { throw "A SQLitePCLRaw bundle package is in the graph; 2.x bundles conflict with core 3.x." }
          Write-Host 'OK: no bundle packages'

      - name: Provider drift check
        run: >
          dotnet run --project foundation/tools/ProviderGen/ProviderGen.csproj -c Release --
          generate
          --manifest foundation/tools/ProviderGen/provider.manifest.json
          --template foundation/src/Qavren.Edge.Sqlite.Provider/Template/provider_internal_funcptrs.cs.template
          --out foundation/src/Qavren.Edge.Sqlite.Provider/Generated/SQLite3Provider_qedge.g.cs
          --check

      - name: Manifest drift check
        run: >
          dotnet run --project foundation/tools/ProviderGen/ProviderGen.csproj -c Release --
          manifest --out foundation/tools/ProviderGen/provider.manifest.json --check

      - name: Format check
        run: dotnet format QavrenEdge.slnx --verify-no-changes --no-restore

      - name: Core tests
        run: dotnet run --project foundation/tests/Qavren.Edge.Core.Tests/Qavren.Edge.Core.Tests.csproj -c Release -f net10.0

      - name: Provider tests
        run: dotnet run --project foundation/tests/Qavren.Edge.Provider.Tests/Qavren.Edge.Provider.Tests.csproj -c Release

      - name: Sqlite tests
        run: dotnet run --project foundation/tests/Qavren.Edge.Sqlite.Tests/Qavren.Edge.Sqlite.Tests.csproj -c Release -f net10.0

      - name: Cipher tests
        run: dotnet run --project foundation/tests/Qavren.Edge.Sqlite.Cipher.Tests/Qavren.Edge.Sqlite.Cipher.Tests.csproj -c Release

      - name: Pack
        if: matrix.os == 'windows-2025'
        run: dotnet pack QavrenEdge.slnx -c Release -o artifacts/packages

      - name: Assert every native RID reached the package
        if: matrix.os == 'windows-2025'
        shell: pwsh
        run: |
          Add-Type -AssemblyName System.IO.Compression.FileSystem
          $expected = @(
            'win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64',
            'maccatalyst-x64','maccatalyst-arm64','android-arm64','android-x64','android-arm')
          foreach ($pkg in 'Qavren.Edge.Sqlite.Native','Qavren.Edge.Sqlite.Native.Cipher') {
            $lib = if ($pkg -like '*Cipher') { 'qedge_sqlcipher' } else { 'qedge_sqlite3' }
            $nupkg = Get-ChildItem "artifacts/packages/$pkg.*.nupkg" | Select-Object -First 1
            $zip = [IO.Compression.ZipFile]::OpenRead($nupkg.FullName)
            $names = $zip.Entries.FullName
            $zip.Dispose()
            foreach ($rid in $expected) {
              $hit = $names | Where-Object { $_ -like "runtimes/$rid/native/*$lib*" }
              if (-not $hit) { throw "$pkg is missing runtimes/$rid/native/ ($lib)" }
            }
            if (-not ($names | Where-Object { $_ -like "xcframeworks/$lib.xcframework/*" })) {
              throw "$pkg is missing the $lib xcframework"
            }
          }
          Write-Host 'OK: every expected RID and the xcframework are packed'

      - uses: actions/upload-artifact@v4
        if: matrix.os == 'windows-2025'
        with:
          name: packages
          path: artifacts/packages/*.nupkg

  device-tests-android:
    needs: natives
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0, filter: 'tree:0' }
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: actions/setup-java@v4
        with: { distribution: microsoft, java-version: '21' }
      - uses: actions/download-artifact@v4
        with: { pattern: native-*, merge-multiple: true, path: foundation/native/artifacts }

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
          dotnet android avd create --name QedgeEmulator --sdk 'system-images;android-36;google_apis;x86_64' --force
          dotnet android avd start -p 5554 --name QedgeEmulator --no-window --gpu swiftshader_indirect \
            --no-snapshot --no-audio --no-boot-anim --wait --no-animations --cpu-threshold 3 --response-threshold 5

      - name: Run device tests
        run: >
          dotnet test foundation/tests/Qavren.Edge.DeviceTests/Qavren.Edge.DeviceTests.csproj
          -f net10.0-android -c Release
          -p:AndroidSdkDirectory=$ANDROID_SDK_ROOT
          --logger "trx;LogFileName=android-device-tests.trx"

      # Spec §13 requires JUnit. DeviceRunners writes TRX and has no JUnit writer, so the repo
      # converts (adjustment 30). The converter is unit-tested locally in Step 3.
      - name: Convert TRX to JUnit
        if: always()
        run: |
          python3 foundation/tools/trx2junit/trx2junit.py \
            --suite device-android \
            --out artifacts/junit/device-android.xml \
            $(find . -name 'android-device-tests.trx' -print)

      - name: Publish JUnit results
        if: always()
        uses: dorny/test-reporter@v1
        with:
          name: device tests (android)
          path: artifacts/junit/*.xml
          reporter: java-junit
          fail-on-error: false

      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: device-tests-android
          path: |
            **/*.trx
            artifacts/junit/*.xml

      - if: always()
        run: dotnet android avd delete --name QedgeEmulator --force || true

  device-tests-ios:
    needs: natives
    # On GitHub Actions macos-15 is ARM64; the certified iOS-simulator lane is x64/Rosetta.
    runs-on: macos-15-intel
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0, filter: 'tree:0' }
      - uses: maxim-lobanov/setup-xcode@v1
        with: { xcode-version: '26.2' }
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: actions/download-artifact@v4
        with: { pattern: native-*, merge-multiple: true, path: foundation/native/artifacts }

      - run: dotnet workload install maui --version 10.0.201
      - run: dotnet tool restore

      - name: Create and boot a simulator
        run: |
          NAME="Qedge-$GITHUB_RUN_ID-ios"
          UDID=$(dotnet apple simulator create "$NAME" --device-type "iPhone 16" --format json | jq -r '.udid')
          echo "SIMULATOR_UDID=$UDID" >> "$GITHUB_ENV"
          echo "SIMULATOR_NAME=$NAME" >> "$GITHUB_ENV"
          dotnet apple simulator boot "$UDID" --wait

      - name: Run device tests
        run: >
          dotnet test foundation/tests/Qavren.Edge.DeviceTests/Qavren.Edge.DeviceTests.csproj
          -f net10.0-ios -r iossimulator-x64 -c Release
          -p:DeviceRunnersDevice=$SIMULATOR_UDID
          --logger "trx;LogFileName=ios-device-tests.trx"

      - name: Convert TRX to JUnit
        if: always()
        run: |
          python3 foundation/tools/trx2junit/trx2junit.py \
            --suite device-ios \
            --out artifacts/junit/device-ios.xml \
            $(find . -name 'ios-device-tests.trx' -print)

      - name: Publish JUnit results
        if: always()
        uses: dorny/test-reporter@v1
        with:
          name: device tests (ios)
          path: artifacts/junit/*.xml
          reporter: java-junit
          fail-on-error: false

      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: device-tests-ios
          path: |
            **/*.trx
            artifacts/junit/*.xml

      - if: always()
        run: dotnet apple simulator delete --force "$SIMULATOR_NAME" || true

  # Spec §13: "Mac Catalyst and Windows run the same app on their hosts." §14's "device tests x2"
  # is a stale count; §13 is the requirement (adjustment 29). Both lanes are cheap: neither boots
  # a simulator or an emulator.
  device-tests-maccatalyst:
    needs: natives
    runs-on: macos-15-intel
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0, filter: 'tree:0' }
      - uses: maxim-lobanov/setup-xcode@v1
        with: { xcode-version: '26.2' }
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: actions/download-artifact@v4
        with: { pattern: native-*, merge-multiple: true, path: foundation/native/artifacts }

      - run: dotnet workload install maui --version 10.0.201
      - run: dotnet tool restore

      - name: Run device tests
        run: >
          dotnet test foundation/tests/Qavren.Edge.DeviceTests/Qavren.Edge.DeviceTests.csproj
          -f net10.0-maccatalyst -r maccatalyst-x64 -c Release
          --logger "trx;LogFileName=maccatalyst-device-tests.trx"

      - name: Convert TRX to JUnit
        if: always()
        run: |
          python3 foundation/tools/trx2junit/trx2junit.py \
            --suite device-maccatalyst \
            --out artifacts/junit/device-maccatalyst.xml \
            $(find . -name 'maccatalyst-device-tests.trx' -print)

      - name: Publish JUnit results
        if: always()
        uses: dorny/test-reporter@v1
        with:
          name: device tests (maccatalyst)
          path: artifacts/junit/*.xml
          reporter: java-junit
          fail-on-error: false

      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: device-tests-maccatalyst
          path: |
            **/*.trx
            artifacts/junit/*.xml

  device-tests-windows:
    needs: natives
    runs-on: windows-2025
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0, filter: 'tree:0' }
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: actions/download-artifact@v4
        with: { pattern: native-*, merge-multiple: true, path: foundation/native/artifacts }

      - run: dotnet workload install maui --version 10.0.201
      - run: dotnet tool restore

      - name: Run device tests
        run: >
          dotnet test foundation/tests/Qavren.Edge.DeviceTests/Qavren.Edge.DeviceTests.csproj
          -f net10.0-windows10.0.19041.0 -c Release
          --logger "trx;LogFileName=windows-device-tests.trx"

      - name: Convert TRX to JUnit
        if: always()
        shell: bash
        run: |
          python foundation/tools/trx2junit/trx2junit.py \
            --suite device-windows \
            --out artifacts/junit/device-windows.xml \
            $(find . -name 'windows-device-tests.trx' -print)

      - name: Publish JUnit results
        if: always()
        uses: dorny/test-reporter@v1
        with:
          name: device tests (windows)
          path: artifacts/junit/*.xml
          reporter: java-junit
          fail-on-error: false

      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: device-tests-windows
          path: |
            **/*.trx
            artifacts/junit/*.xml

  # The required status check for ci (adjustment 28). It always reports; it is red if any job
  # it depends on failed or was cancelled, green if they all succeeded or were skipped.
  ci-gate:
    needs: [natives, test, device-tests-android, device-tests-ios, device-tests-maccatalyst, device-tests-windows]
    if: always()
    runs-on: ubuntu-24.04
    steps:
      - name: Report
        run: |
          results="${{ join(needs.*.result, ' ') }}"
          echo "job results: $results"
          for r in $results; do
            case "$r" in
              success|skipped) ;;
              *) echo "ci-gate: FAIL ($r)"; exit 1 ;;
            esac
          done
          echo "ci-gate: pass"
```

Also create `.config\dotnet-tools.json` at the repo root so `dotnet tool restore` works:

```json
{
  "version": 1,
  "isRoot": true,
  "tools": {
    "androidsdk.tool": { "version": "0.35.1", "commands": ["android"], "rollForward": false },
    "appledev.tools": { "version": "0.8.10", "commands": ["apple"], "rollForward": false }
  }
}
```

- [ ] **Step 7: Write `.github\workflows\release.yml`**

Three things spec §14 asks for that the first draft of this plan missed, all present below:
the native artifacts are attached to the Release (not only buried inside the nupkgs), an SBOM is
generated, and the checksum file is computed **last** so it covers every published file.

```yaml
name: release

on:
  push:
    tags: ['v*']
  workflow_dispatch:

permissions:
  contents: write

jobs:
  # Rebuild the natives from the tagged commit rather than trusting a stale artifact, so the
  # binaries in the Release are provably the ones the packages were built from. No `force`
  # input exists any more (adjustment 34) and none is needed: a tag push is not a
  # `pull_request`, so native.yml's `build` matrix always runs here. The artifact cache still
  # short-circuits the compile when the tagged native tree is byte-identical to what `main`
  # already built, which is the normal case for a release cut from a green main.
  natives:
    uses: ./.github/workflows/native.yml

  release:
    needs: natives
    runs-on: windows-2025
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0        # MinVer derives the version from tags
          filter: tree:0

      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      - uses: actions/download-artifact@v4
        with:
          pattern: native-*
          merge-multiple: true
          path: foundation/native/artifacts

      - name: Pack
        run: dotnet pack QavrenEdge.slnx -c Release -o artifacts/packages

      # One zip per build host keeps the release page readable.
      - name: Zip the native artifacts
        shell: pwsh
        run: |
          New-Item -ItemType Directory -Force -Path artifacts/native | Out-Null
          Compress-Archive -Path foundation/native/artifacts/win-* -DestinationPath artifacts/native/qedge-native-windows.zip
          Compress-Archive -Path foundation/native/artifacts/linux-*, foundation/native/artifacts/android-* -DestinationPath artifacts/native/qedge-native-linux-android.zip
          Compress-Archive -Path foundation/native/artifacts/apple, foundation/native/artifacts/osx-*, foundation/native/artifacts/maccatalyst-* -DestinationPath artifacts/native/qedge-native-apple.zip
          Get-ChildItem artifacts/native

      # syft scans the whole artifacts/ tree, so one document covers both the packages and the
      # native binaries.
      - name: Generate SBOM
        uses: anchore/sbom-action@v0
        with:
          path: artifacts
          format: spdx-json
          output-file: artifacts/sbom.spdx.json
          upload-artifact: false
          upload-release-assets: false

      # Computed last, so it covers the packages, the native zips AND the SBOM.
      - name: Checksums
        shell: pwsh
        run: |
          $files = @(Get-ChildItem artifacts/packages/*.nupkg, artifacts/packages/*.snupkg, artifacts/native/*.zip, artifacts/sbom.spdx.json -ErrorAction SilentlyContinue)
          if ($files.Count -lt 8) { throw "expected at least 8 release files, found $($files.Count)" }
          $files | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_ -Algorithm SHA256).Hash, $_.Name } |
            Set-Content artifacts/SHA256SUMS.txt
          Get-Content artifacts/SHA256SUMS.txt

      - name: Push to NuGet.org
        run: dotnet nuget push "artifacts/packages/*.nupkg" --source https://api.nuget.org/v3/index.json --api-key ${{ secrets.NUGET_API_KEY }} --skip-duplicate

      - uses: softprops/action-gh-release@v2
        with:
          files: |
            artifacts/packages/*.nupkg
            artifacts/packages/*.snupkg
            artifacts/native/*.zip
            artifacts/sbom.spdx.json
            artifacts/SHA256SUMS.txt
          generate_release_notes: true
```

- [ ] **Step 8: Write `.github\workflows\upstream-pins.yml`**

Dependabot cannot see `native/versions.json`, so a monthly job opens a tracking issue instead.

```yaml
name: upstream-pins

on:
  schedule:
    - cron: '0 9 1 * *'
  workflow_dispatch:

permissions:
  contents: read
  issues: write

jobs:
  check:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@v4

      - name: Compare pinned versions with the latest upstream tags
        id: check
        env:
          GH_TOKEN: ${{ github.token }}
        run: |
          set -euo pipefail
          body=""
          for repo_and_key in "asg017/sqlite-vec:sqliteVec" "sqlcipher/sqlcipher:sqlcipher" "libtom/libtomcrypt:libtomcrypt"; do
            repo="${repo_and_key%%:*}"; key="${repo_and_key##*:}"
            pinned=$(jq -r ".${key}.version" foundation/native/versions.json)
            latest=$(gh api "repos/${repo}/releases/latest" --jq '.tag_name' 2>/dev/null || echo "unknown")
            body="${body}- ${repo}: pinned ${pinned}, latest ${latest}"$'\n'
          done
          sqlite_pinned=$(jq -r '.sqlite.version' foundation/native/versions.json)
          body="${body}- sqlite.org: pinned ${sqlite_pinned} (check https://sqlite.org/chronology.html)"$'\n'
          {
            echo 'body<<EOF'
            echo "$body"
            echo EOF
          } >> "$GITHUB_OUTPUT"

      - name: Open the tracking issue
        env:
          GH_TOKEN: ${{ github.token }}
        run: |
          gh issue create \
            --title "Native pin review $(date +%Y-%m)" \
            --body "${{ steps.check.outputs.body }}

          Bumping a pin means editing \`foundation/native/versions.json\`, running
          \`fetch-sources.ps1 -UpdateHashes\`, reviewing the hash diff, and letting \`native.yml\` rebuild."
```

- [ ] **Step 9: Verify (lint every workflow, and prove the converter works)**

```powershell
python -c "import yaml,glob,sys; fs=sorted(glob.glob(r'C:\Users\steve\projects\qavren-edge\.github\workflows\*.yml')); [yaml.safe_load(open(f, encoding='utf-8')) for f in fs]; print('OK: %d workflows parsed' % len(fs)); sys.exit(0 if len(fs)==4 else 1)"
```

Expected: `OK: 4 workflows parsed`, exit code 0.

```powershell
pwsh -NoProfile -Command "$w='C:\Users\steve\projects\qavren-edge\.github\workflows'; foreach ($f in 'ci.yml','native.yml','release.yml','upstream-pins.yml') { $t = Get-Content (Join-Path $w $f) -Raw; if ($t -match 'placeholder') { Write-Error \"$f is still a stub\" } }; $ci = Get-Content (Join-Path $w 'ci.yml') -Raw; if ($ci -notmatch 'fetch-depth: 0') { Write-Error 'ci.yml must checkout full history for MinVer' }; Write-Host 'OK: workflows are real and MinVer-safe'"
```

Expected: `OK: workflows are real and MinVer-safe`.

Then assert the four spec-mandated pieces that a lint alone would miss — the two gate jobs
(branch protection's required contexts), four device lanes, JUnit publication, and the release
SBOM plus native attachments:

Write this as `foundation\tools\ci-checks\assert-workflows.py` (PowerShell has no heredoc, and
checking it in means `ci.yml` can run the same assertion on every PR):

```python
"""Assert the workflow contract spec sections 13 and 14 require. Run: python assert-workflows.py [repo-root]"""
import json
import pathlib
import sys

import yaml

root = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\steve\projects\qavren-edge")
w = root / ".github" / "workflows"
ci = yaml.safe_load((w / "ci.yml").read_text(encoding="utf-8"))
nat = yaml.safe_load((w / "native.yml").read_text(encoding="utf-8"))
rel = yaml.safe_load((w / "release.yml").read_text(encoding="utf-8"))
bp = json.loads((w.parent / "branch-protection.json").read_text(encoding="utf-8"))
problems = []
for job in ("device-tests-android", "device-tests-ios", "device-tests-maccatalyst", "device-tests-windows", "ci-gate"):
    if job not in ci["jobs"]: problems.append("ci.yml missing job " + job)
if "native-gate" not in nat["jobs"]: problems.append("native.yml missing job native-gate")
if "changes" not in nat["jobs"]: problems.append("native.yml missing the changes filter job")
ci_text = (w / "ci.yml").read_text(encoding="utf-8")
if ci_text.count("trx2junit.py") != 4: problems.append("expected 4 TRX->JUnit conversions, found %d" % ci_text.count("trx2junit.py"))
if ci_text.count("java-junit") != 4: problems.append("expected 4 JUnit publish steps")
rel_text = (w / "release.yml").read_text(encoding="utf-8")
for token in ("sbom.spdx.json", "artifacts/native/*.zip", "SHA256SUMS.txt"):
    if token not in rel_text: problems.append("release.yml missing " + token)
if sorted(bp["required_status_checks"]["contexts"]) != ["ci-gate", "native-gate"]:
    problems.append("branch-protection contexts do not match the gate jobs")
nat_text = (w / "native.yml").read_text(encoding="utf-8")
for token in ("-Arch $arch", "win-arm64"):
    if token not in nat_text: problems.append("native.yml does not build win-arm64 (" + token + ")")

# Spec 10.4: the BUILT natives are cached on the spec's key, and a managed-only PR reuses them.
if "reuse" not in nat["jobs"]: problems.append("native.yml missing the `reuse` job (spec 10.4 cached-native reuse)")
if "needs.changes.outputs.native" not in str(nat["jobs"].get("build", {}).get("if", "")):
    problems.append("native.yml build job is not gated on the changes filter")
if "force" in nat_text: problems.append("native.yml still carries a `force` bypass; it defeats the spec 10.4 path filter")
for token in ("path: foundation/native/artifacts", "qedge-native-${{ matrix.name }}-",
              "foundation/native/CMakeLists.txt", "'.github/workflows/native.yml'"):
    if token not in nat_text: problems.append("native.yml artifact cache key is incomplete (" + token + ")")
if nat_text.count("actions/cache/restore@v4") != 3:
    problems.append("native.yml reuse job must restore all three OS caches")
for f in ("ci.yml", "release.yml"):
    if "force: true" in (w / f).read_text(encoding="utf-8"):
        problems.append(f + " passes force:true to native.yml, forcing a rebuild on managed-only PRs")

win = ci["jobs"]["device-tests-windows"]["runs-on"]
print("\n".join(problems) if problems else "OK: gates, 4 device lanes, JUnit, win-arm64, native artifact cache + reuse, SBOM and native release assets all present (windows lane on %s)" % win)
sys.exit(1 if problems else 0)
```

Run it:

```powershell
python "C:\Users\steve\projects\qavren-edge\foundation\tools\ci-checks\assert-workflows.py" "C:\Users\steve\projects\qavren-edge"
```

Expected: `OK: gates, 4 device lanes, JUnit, win-arm64, native artifact cache + reuse, SBOM and native release assets all present (windows lane on windows-2025)`, exit code 0.

Add the same assertion to `ci.yml`'s `test` job (Ubuntu leg only, so it runs once per PR), right
after the format check:

```yaml
      - name: Workflow contract check
        if: matrix.os == 'ubuntu-24.04'
        run: |
          python3 -m pip install --quiet pyyaml
          python3 foundation/tools/ci-checks/assert-workflows.py .
```

```powershell
python "C:\Users\steve\projects\qavren-edge\foundation\tools\trx2junit\test_trx2junit.py"
```

Expected: three `PASS` lines and `OK: 3 tests passed`.

---

---

## WAVE 7 — Database, migrations, lifecycle observer

### Task 7.1: `IEdgeDatabase`, migrations, startup tasks, lifecycle observer, `AddSqlite`

**Local-verifiable:** yes for the wiring tests; the connection-opening tests land in Task 9.2 once a Native package exists.

**Files:**
- Create: `foundation\src\Qavren.Edge.Sqlite\IEdgeDatabase.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite\EdgeDatabase.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite\Migrations\IEdgeMigration.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite\Migrations\EdgeMigrator.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite\Internal\SqliteRegistry.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite\Internal\SqliteOpenStartupTask.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite\Internal\SqliteLifecycleObserver.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite\Internal\SqliteDiagnosticsContributor.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite\SqliteEdgeBuilderExtensions.cs`
- Test: `foundation\tests\Qavren.Edge.Sqlite.Tests\RegistrationTests.cs`

- [ ] **Step 1: Write the failing wiring tests**

`foundation\tests\Qavren.Edge.Sqlite.Tests\RegistrationTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Qavren.Edge.Sqlite;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

public class RegistrationTests
{
    [Fact]
    public void AddSqlite_TwiceWithTheSameName_ThrowsAtBuildTime()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var ex = Assert.Throws<EdgeConfigurationException>(() =>
            services.AddQavrenEdge(edge => edge
                .AddSqlite(o => o.DatabaseName = "a.db")
                .AddSqlite(o => o.DatabaseName = "b.db")));

        Assert.Equal(EdgeErrorCode.DuplicateDatabaseName, ex.Code);
    }

    [Fact]
    public void AddSqlite_WithDistinctNames_RegistersKeyedDatabases()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge
            .AddSqlite(o => o.DatabaseName = "default.db")
            .AddSqlite("corpus", o => o.DatabaseName = "corpus.db"));

        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<IEdgeDatabase>());
        Assert.NotNull(sp.GetKeyedService<IEdgeDatabase>("corpus"));
    }

    [Fact]
    public async Task NoNativeProviderRegistered_FaultsStartupWithAConfigurationError()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge.AddSqlite(o => o.DatabaseName = ":memory:"));
        using var sp = services.BuildServiceProvider();

        var ex = await Assert.ThrowsAsync<EdgeConfigurationException>(async () =>
            await sp.GetRequiredService<IEdgeHost>().EnsureStartedAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EdgeErrorCode.NoNativeProviderRegistered, ex.Code);
    }

    [Fact]
    public void Migrations_MustHaveUniqueAscendingVersions()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var ex = Assert.Throws<EdgeConfigurationException>(() =>
            services.AddQavrenEdge(edge => edge
                .AddSqlite(o => o.DatabaseName = ":memory:")
                .AddMigration<M1>()
                .AddMigration<M1Duplicate>()));

        Assert.Equal(EdgeErrorCode.MigrationVersionConflict, ex.Code);
    }

    private sealed class M1 : IEdgeMigration
    {
        public int Version => 1;

        public string Name => "one";

        public Task UpAsync(Microsoft.Data.Sqlite.SqliteConnection connection, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class M1Duplicate : IEdgeMigration
    {
        public int Version => 1;

        public string Name => "also one";

        public Task UpAsync(Microsoft.Data.Sqlite.SqliteConnection connection, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj" -c Release -f net10.0
```

Expected: FAIL with `CS1061: 'EdgeBuilder' does not contain a definition for 'AddSqlite'`.

- [ ] **Step 3: Write `IEdgeDatabase.cs`**

```csharp
using Microsoft.Data.Sqlite;

namespace Qavren.Edge.Sqlite;

public sealed record SqliteDatabaseInfo(
    string SqliteVersion,
    string VecVersion,
    int PageSize,
    string JournalMode,
    int UserVersion,
    long FileSizeBytes,
    bool IsEncrypted);

public sealed record SqliteCheckResult(bool Ok, string QuickCheck, string VecVersion);

public interface IEdgeDatabase
{
    string Name { get; }

    string Path { get; }

    bool IsEncrypted { get; }

    /// <summary>Awaits startup, opens, applies the key and the per-open pragmas. The caller disposes.</summary>
    ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);

    Task<T> ExecuteInTransactionAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default);

    Task<SqliteDatabaseInfo> GetInfoAsync(CancellationToken cancellationToken = default);

    Task<SqliteCheckResult> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Cipher builds only; throws <see cref="EdgeConfigurationException"/> otherwise.</summary>
    Task RekeyAsync(SqliteKey newKey, CancellationToken cancellationToken = default);

    Task CheckpointAsync(CancellationToken cancellationToken = default);
}
```

- [ ] **Step 4: Write `EdgeDatabase.cs`**

```csharp
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Sqlite;

/// <summary>
/// One physical database. Connection strings are always built through
/// <see cref="SqliteConnectionStringBuilder"/> from this single place: Microsoft.Data.Sqlite keys
/// its pool groups on the raw connection-string text, so two textually different but semantically
/// identical strings fragment the pool and hold duplicate keyed handles open.
/// </summary>
public sealed class EdgeDatabase : IEdgeDatabase
{
    private readonly SqliteOptions _options;
    private readonly IEdgeHost _host;
    private readonly IEdgePaths _paths;
    private readonly ISqliteNativeProvider _native;
    private readonly ILogger<EdgeDatabase> _logger;
    private readonly SemaphoreSlim _keyGate = new(1, 1);

    private string? _connectionString;
    private SqliteKey? _resolvedKey;

    public EdgeDatabase(
        string name,
        SqliteOptions options,
        IEdgeHost host,
        IEdgePaths paths,
        ISqliteNativeProvider native,
        ILogger<EdgeDatabase> logger)
    {
        Name = name;
        _options = options;
        _host = host;
        _paths = paths;
        _native = native;
        _logger = logger;

        Path = string.Equals(options.DatabaseName, ":memory:", StringComparison.Ordinal)
            ? ":memory:"
            : System.IO.Path.Combine(options.Directory ?? paths.Data, options.DatabaseName);

        IsEncrypted = options.Key is not null || options.KeyProvider is not null;
    }

    public string Name { get; }

    public string Path { get; }

    public bool IsEncrypted { get; }

    public async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        await _host.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return await OpenCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens without awaiting startup. Only startup tasks may call this.</summary>
    internal async Task<SqliteConnection> OpenCoreAsync(CancellationToken cancellationToken)
    {
        var connectionString = await GetConnectionStringAsync(cancellationToken).ConfigureAwait(false);
        var connection = new SqliteConnection(connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (IsEncrypted && ex.SqliteErrorCode == 26 /* SQLITE_NOTADB */)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new EdgeDatabaseKeyException(Name, ex);
        }

        await ApplyPerOpenPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// Issued on every logical open. Microsoft.Data.Sqlite does exactly this for
    /// <c>foreign_keys</c> and <c>recursive_triggers</c>, and never resets a pragma when a pooled
    /// connection is returned, so re-issuing is both cheap and correct.
    /// <c>journal_mode</c> is deliberately absent: it lives in the file header and is set once, at creation.
    /// </summary>
    private async Task ApplyPerOpenPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var busyMs = (int)_options.BusyTimeout.TotalMilliseconds;
        await connection.ExecuteAsync(
            $"PRAGMA busy_timeout = {busyMs.ToString(CultureInfo.InvariantCulture)};",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            $"PRAGMA synchronous = {((int)_options.Synchronous).ToString(CultureInfo.InvariantCulture)};",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            $"PRAGMA cache_size = -{_options.CacheSizeKiB.ToString(CultureInfo.InvariantCulture)};",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applied once, at creation. Safe to re-run: setting the same journal_mode is a no-op.</summary>
    internal async Task ApplyCreationPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (string.Equals(Path, ":memory:", StringComparison.Ordinal))
        {
            return;
        }

        await connection.ExecuteAsync(
            $"PRAGMA journal_mode = {_options.JournalMode.ToString().ToUpperInvariant()};",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal async Task<string> GetConnectionStringAsync(CancellationToken cancellationToken)
    {
        if (_connectionString is not null)
        {
            return _connectionString;
        }

        await _keyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connectionString is not null)
            {
                return _connectionString;
            }

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = _options.Pooling,
                ForeignKeys = _options.ForeignKeys,
                Mode = string.Equals(Path, ":memory:", StringComparison.Ordinal)
                    ? SqliteOpenMode.Memory
                    : SqliteOpenMode.ReadWriteCreate,
            };

            if (IsEncrypted)
            {
                if (!_native.SupportsEncryption)
                {
                    throw new EdgeConfigurationException(
                        EdgeErrorCode.EncryptionKeyWithoutCipherProvider,
                        $"Database '{Name}' has a key configured, but the registered native provider " +
                        $"'{_native.Name}' has no codec. Reference Qavren.Edge.Sqlite.Native.Cipher and " +
                        "call UseSqliteNativeCipher() instead of UseSqliteNative().");
                }

                _resolvedKey = _options.Key
                    ?? await _options.KeyProvider!(cancellationToken).ConfigureAwait(false);
                builder.Password = _resolvedKey.ToConnectionStringPassword();
            }

            _connectionString = builder.ToString();
            return _connectionString;
        }
        finally
        {
            _keyGate.Release();
        }
    }

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var result = await work(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<SqliteDatabaseInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var fileSize = string.Equals(Path, ":memory:", StringComparison.Ordinal) || !File.Exists(Path)
            ? 0L
            : new FileInfo(Path).Length;

        return new SqliteDatabaseInfo(
            await connection.ScalarAsync<string>("SELECT sqlite_version()", cancellationToken: cancellationToken).ConfigureAwait(false) ?? "unknown",
            await connection.ScalarAsync<string>("SELECT vec_version()", cancellationToken: cancellationToken).ConfigureAwait(false) ?? "unknown",
            await connection.ScalarAsync<int>("PRAGMA page_size", cancellationToken: cancellationToken).ConfigureAwait(false),
            await connection.ScalarAsync<string>("PRAGMA journal_mode", cancellationToken: cancellationToken).ConfigureAwait(false) ?? "unknown",
            await connection.ScalarAsync<int>("PRAGMA user_version", cancellationToken: cancellationToken).ConfigureAwait(false),
            fileSize,
            IsEncrypted);
    }

    public async Task<SqliteCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var quick = await connection.ScalarAsync<string>("PRAGMA quick_check", cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? "unknown";
        var vec = await connection.ScalarAsync<string>("SELECT vec_version()", cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? "unknown";

        return new SqliteCheckResult(string.Equals(quick, "ok", StringComparison.Ordinal), quick, vec);
    }

    public async Task RekeyAsync(SqliteKey newKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newKey);

        if (!IsEncrypted || !_native.SupportsEncryption)
        {
            throw new EdgeConfigurationException(
                EdgeErrorCode.EncryptionKeyWithoutCipherProvider,
                $"Database '{Name}' is not encrypted, so it cannot be rekeyed.");
        }

        await using (var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            // SQLite forbids parameters in PRAGMA, so quote through SQLite's own quote() exactly
            // as Microsoft.Data.Sqlite does for PRAGMA key.
            await using var quoteCommand = connection.CreateCommand();
            quoteCommand.CommandText = "SELECT quote($password);";
            quoteCommand.Parameters.AddWithValue("$password", newKey.ToConnectionStringPassword());
            var quoted = (string)(await quoteCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

            await connection.ExecuteAsync("PRAGMA rekey = " + quoted + ";", cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        // Pooled handles still hold the old key. Drop them, then rebuild the connection string.
        SqliteConnection.ClearAllPools();

        await _keyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _resolvedKey = newKey;
            _connectionString = null;
        }
        finally
        {
            _keyGate.Release();
        }
    }

    public async Task CheckpointAsync(CancellationToken cancellationToken = default)
    {
        if (string.Equals(Path, ":memory:", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(EdgeEventIds.CheckpointFailed, ex, "Checkpoint of database {Database} failed.", Name);
        }
    }
}
```

- [ ] **Step 5: Write `Migrations\IEdgeMigration.cs`**

```csharp
using Microsoft.Data.Sqlite;

namespace Qavren.Edge.Sqlite;

/// <summary>
/// A forward-only schema change. Registration is explicit and AOT-safe: there is no assembly scanning,
/// and there are no down-migrations.
/// </summary>
public interface IEdgeMigration
{
    /// <summary>Unique and ascending across a database. Stored in <c>PRAGMA user_version</c>.</summary>
    int Version { get; }

    string Name { get; }

    Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken);
}
```

- [ ] **Step 6: Write `Migrations\EdgeMigrator.cs`**

```csharp
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Sqlite;

/// <summary>
/// Startup task order 100. Reads <c>PRAGMA user_version</c>, runs each pending migration in its own
/// transaction, and sets <c>user_version</c> after each. A failure stops the run and raises
/// <see cref="EdgeMigrationException"/>; migrations already applied stay applied.
/// </summary>
public sealed class EdgeMigrator(
    EdgeDatabase database,
    IReadOnlyList<IEdgeMigration> migrations,
    ILogger<EdgeMigrator> logger) : IEdgeStartupTask
{
    public int Order => EdgeStartupOrder.Migrations;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (migrations.Count == 0)
        {
            return;
        }

        await using var connection = await database.OpenCoreAsync(cancellationToken).ConfigureAwait(false);
        var current = await connection.ScalarAsync<int>("PRAGMA user_version", cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        foreach (var migration in migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await migration.UpAsync(connection, cancellationToken).ConfigureAwait(false);

                // PRAGMA takes no parameters; Version is an int we produced, so interpolation is safe.
                await connection.ExecuteAsync(
                    "PRAGMA user_version = " + migration.Version.ToString(CultureInfo.InvariantCulture) + ";",
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                logger.LogInformation(
                    EdgeEventIds.MigrationApplied,
                    "Applied migration {Version} '{Name}' to {Database}.",
                    migration.Version, migration.Name, database.Name);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                logger.LogError(
                    EdgeEventIds.MigrationFailed, ex,
                    "Migration {Version} '{Name}' failed on {Database}.",
                    migration.Version, migration.Name, database.Name);
                throw new EdgeMigrationException(migration.Version, migration.Name, ex);
            }
        }
    }
}
```

- [ ] **Step 7: Write `Internal\SqliteRegistry.cs`, `SqliteOpenStartupTask.cs`, `SqliteLifecycleObserver.cs`, `SqliteDiagnosticsContributor.cs`**

`Internal\SqliteRegistry.cs`:

```csharp
namespace Qavren.Edge.Sqlite.Internal;

/// <summary>Build-time bookkeeping so duplicate names and version conflicts fail before the container is built.</summary>
public sealed class SqliteRegistry
{
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> _migrationVersions = new(StringComparer.Ordinal);

    public const string DefaultName = "(default)";

    public IReadOnlyCollection<string> Names => _names;

    public void AddDatabase(string name)
    {
        if (!_names.Add(name))
        {
            throw new EdgeConfigurationException(
                EdgeErrorCode.DuplicateDatabaseName,
                $"A database named '{name}' is already registered. Give the second one a name: AddSqlite(\"corpus\", ...).");
        }

        _migrationVersions[name] = [];
    }

    public void AddMigrationVersion(string databaseName, int version, string migrationName)
    {
        if (!_migrationVersions.TryGetValue(databaseName, out var versions))
        {
            throw new EdgeConfigurationException(
                EdgeErrorCode.DuplicateDatabaseName,
                $"AddMigration was called before AddSqlite for database '{databaseName}'.");
        }

        if (!versions.Add(version))
        {
            throw new EdgeConfigurationException(
                EdgeErrorCode.MigrationVersionConflict,
                $"Migration version {version} is registered twice on database '{databaseName}' " +
                $"(second one: '{migrationName}'). Versions must be unique and ascending.");
        }
    }
}
```

`Internal\SqliteOpenStartupTask.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Sqlite.Internal;

/// <summary>
/// Startup task order 10: open once, apply the creation-time pragmas, verify the key by reading
/// <c>sqlite_master</c>, and capture the info for diagnostics.
/// </summary>
public sealed class SqliteOpenStartupTask(
    EdgeDatabase database,
    ISqliteNativeProvider native,
    ILogger<SqliteOpenStartupTask> logger) : IEdgeStartupTask
{
    public int Order => EdgeStartupOrder.DatabaseOpen;

    public SqliteDatabaseInfo? Info { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _ = native;   // ordering dependency: startup task 0 must have installed the provider first

        await using var connection = await database.OpenCoreAsync(cancellationToken).ConfigureAwait(false);
        await database.ApplyCreationPragmasAsync(connection, cancellationToken).ConfigureAwait(false);

        // Forces decryption on a cipher database, so a wrong key fails here rather than later.
        _ = await connection.ScalarAsync<long>("SELECT count(*) FROM sqlite_master", cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation("Opened database {Database} at {Path}.", database.Name, database.Path);
    }
}
```

`Internal\SqliteLifecycleObserver.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Qavren.Edge.Lifecycle;

namespace Qavren.Edge.Sqlite.Internal;

/// <summary>Spec 8.7: checkpoint on sleep, drop pools under critical memory pressure, clear everything on stop.</summary>
public sealed class SqliteLifecycleObserver(IEnumerable<IEdgeDatabase> databases) : EdgeLifecycleObserver
{
    public override async Task OnSleepingAsync(CancellationToken cancellationToken)
    {
        foreach (var database in databases)
        {
            await database.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override Task OnMemoryPressureAsync(EdgeMemoryPressure level, CancellationToken cancellationToken)
    {
        if (level == EdgeMemoryPressure.Critical)
        {
            SqliteConnection.ClearAllPools();
        }

        return Task.CompletedTask;
    }

    public override Task OnStoppingAsync(CancellationToken cancellationToken)
    {
        SqliteConnection.ClearAllPools();
        return Task.CompletedTask;
    }
}
```

This is **adjustment 35**, and it is a deliberate departure from spec §8.7's wording, not an oversight. `ClearPool(connection)` clears only the pool group for one exact connection string, so `ClearAllPools()` is the correct call when the set of live connection strings is not enumerable here.

`Internal\SqliteDiagnosticsContributor.cs`:

```csharp
using System.Globalization;
using Qavren.Edge.Diagnostics;

namespace Qavren.Edge.Sqlite.Internal;

public sealed class SqliteDiagnosticsContributor(
    IEnumerable<IEdgeDatabase> databases,
    ISqliteNativeProvider native) : IEdgeDiagnosticsContributor
{
    public string ComponentName => "Qavren.Edge.Sqlite";

    public string? ComponentVersion => typeof(SqliteDiagnosticsContributor).Assembly.GetName().Version?.ToString();

    public IReadOnlyDictionary<string, string?> Describe()
    {
        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["nativeProvider"] = native.Name,
            ["nativeLibrary"] = native.LibraryName,
            ["supportsEncryption"] = native.SupportsEncryption.ToString(CultureInfo.InvariantCulture),
        };

        foreach (var database in databases)
        {
            details[$"db[{database.Name}].path"] = database.Path;
            details[$"db[{database.Name}].encrypted"] = database.IsEncrypted.ToString(CultureInfo.InvariantCulture);
        }

        return details;
    }
}
```

- [ ] **Step 8: Write `SqliteEdgeBuilderExtensions.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Sqlite.Internal;

namespace Qavren.Edge.Sqlite;

public static class SqliteEdgeBuilderExtensions
{
    private static SqliteRegistry Registry(EdgeBuilder builder)
    {
        var existing = builder.Services
            .FirstOrDefault(d => d.ServiceType == typeof(SqliteRegistry))?.ImplementationInstance as SqliteRegistry;
        if (existing is not null)
        {
            return existing;
        }

        var registry = new SqliteRegistry();
        builder.Services.AddSingleton(registry);
        return registry;
    }

    public static EdgeBuilder AddSqlite(this EdgeBuilder builder, Action<SqliteOptions>? configure = null)
        => builder.AddSqlite(null, configure);

    public static EdgeBuilder AddSqlite(this EdgeBuilder builder, string? name, Action<SqliteOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var key = name ?? SqliteRegistry.DefaultName;
        Registry(builder).AddDatabase(key);

        builder.Services.Configure<SqliteOptions>(key, o => configure?.Invoke(o));

        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEdgeDiagnosticsContributor, SqliteDiagnosticsContributor>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEdgeLifecycleObserver, SqliteLifecycleObserver>());

        builder.Services.AddKeyedSingleton<EdgeDatabase>(key, (sp, _) => new EdgeDatabase(
            key,
            sp.GetRequiredService<IOptionsMonitor<SqliteOptions>>().Get(key),
            sp.GetRequiredService<IEdgeHost>(),
            sp.GetRequiredService<IEdgePaths>(),
            RequireNative(sp),
            sp.GetRequiredService<ILogger<EdgeDatabase>>()));

        builder.Services.AddKeyedSingleton<IEdgeDatabase>(key, (sp, k) => sp.GetRequiredKeyedService<EdgeDatabase>(k));
        builder.Services.AddSingleton<IEdgeDatabase>(sp => sp.GetRequiredKeyedService<IEdgeDatabase>(key));

        builder.Services.AddSingleton<IEdgeStartupTask>(sp => new SqliteOpenStartupTask(
            sp.GetRequiredKeyedService<EdgeDatabase>(key),
            RequireNative(sp),
            sp.GetRequiredService<ILogger<SqliteOpenStartupTask>>()));

        builder.Services.AddSingleton<IEdgeStartupTask>(sp => new EdgeMigrator(
            sp.GetRequiredKeyedService<EdgeDatabase>(key),
            [.. sp.GetKeyedServices<IEdgeMigration>(key)],
            sp.GetRequiredService<ILogger<EdgeMigrator>>()));

        return builder;
    }

    /// <summary>Explicit, AOT-safe migration registration. There is no assembly scanning.</summary>
    public static EdgeBuilder AddMigration<TMigration>(this EdgeBuilder builder, string? databaseName = null)
        where TMigration : class, IEdgeMigration, new()
    {
        ArgumentNullException.ThrowIfNull(builder);

        var key = databaseName ?? SqliteRegistry.DefaultName;
        var migration = new TMigration();
        Registry(builder).AddMigrationVersion(key, migration.Version, migration.Name);
        builder.Services.AddKeyedSingleton<IEdgeMigration>(key, migration);
        return builder;
    }

    public static EdgeBuilder AddMigrations(
        this EdgeBuilder builder,
        IEnumerable<IEdgeMigration> migrations,
        string? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(migrations);

        var key = databaseName ?? SqliteRegistry.DefaultName;
        var registry = Registry(builder);
        foreach (var migration in migrations)
        {
            registry.AddMigrationVersion(key, migration.Version, migration.Name);
            builder.Services.AddKeyedSingleton<IEdgeMigration>(key, migration);
        }

        return builder;
    }

    private static ISqliteNativeProvider RequireNative(IServiceProvider services)
    {
        var providers = services.GetServices<ISqliteNativeProvider>().ToArray();
        return providers.Length switch
        {
            1 => providers[0],
            0 => throw new EdgeConfigurationException(
                EdgeErrorCode.NoNativeProviderRegistered,
                "No SQLite native provider is registered. Reference Qavren.Edge.Sqlite.Native and call " +
                "UseSqliteNative(), or reference Qavren.Edge.Sqlite.Native.Cipher and call UseSqliteNativeCipher()."),
            _ => throw new EdgeConfigurationException(
                EdgeErrorCode.MultipleNativeProvidersRegistered,
                "More than one SQLite native provider is registered: " +
                string.Join(", ", providers.Select(p => p.Name)) +
                ". Reference exactly one of Qavren.Edge.Sqlite.Native and Qavren.Edge.Sqlite.Native.Cipher."),
        };
    }
}
```

`AddQavrenEdge` invokes the configure callback synchronously, so `AddSqlite`'s duplicate-name and version-conflict checks throw from `AddQavrenEdge` itself, which is what the tests assert. The missing-provider check happens when the database is first resolved — startup task 0's absence surfaces it at `EnsureStartedAsync`.

- [ ] **Step 9: Run to verify the tests pass**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj" -c Release -f net10.0
```

Expected: 26 tests pass, exit code 0.

- [ ] **Step 10: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj" -c Release -f net10.0
```

Expected: `Failed: 0`, exit code 0.

---

---

## WAVE 8 — Native packages

### Task 8.1: `Qavren.Edge.Sqlite.Native` and `Qavren.Edge.Sqlite.Native.Cipher`

**Local-verifiable:** yes for `net10.0` and the win-x64 asset flow. The iOS `NativeReference`/xcframework path is **CI-only** — no macOS on this box.

Both packages target `net10.0;net10.0-ios` only. Android, Windows, Linux, macOS and Mac Catalyst all consume the `net10.0` assembly plus `runtimes/<rid>/native/`, which the .NET for Android and Apple SDKs classify by file extension with no extra MSBuild. Only iOS needs its own TFM, because only iOS uses `DllImport("__Internal")` against a statically linked xcframework.

**Files:**
- Create: `foundation\src\Qavren.Edge.Sqlite.Native\Qavren.Edge.Sqlite.Native.csproj`
- Create: `foundation\src\Qavren.Edge.Sqlite.Native\QedgeSqliteNativeProvider.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite.Native\SqliteNativeInstallStartupTask.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite.Native\SqliteNativeBuilderExtensions.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite.Native\buildTransitive\net10.0-ios\Qavren.Edge.Sqlite.Native.targets`
- Create: `foundation\src\Qavren.Edge.Sqlite.Native.Cipher\Qavren.Edge.Sqlite.Native.Cipher.csproj`
- Create: `foundation\src\Qavren.Edge.Sqlite.Native.Cipher\QedgeSqlCipherNativeProvider.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite.Native.Cipher\SqliteNativeCipherBuilderExtensions.cs`
- Create: `foundation\src\Qavren.Edge.Sqlite.Native.Cipher\buildTransitive\net10.0-ios\Qavren.Edge.Sqlite.Native.Cipher.targets`

- [ ] **Step 1: Write `QedgeSqliteNativeProvider.cs`**

```csharp
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite.Provider;

namespace Qavren.Edge.Sqlite.Native;

/// <summary>
/// Installs the generated provider into <c>SQLitePCL.raw</c> and proves the native library actually
/// loaded by running one query on an in-memory connection.
/// </summary>
public class QedgeSqliteNativeProvider : ISqliteNativeProvider
{
    private static readonly Lock InstallGate = new();
    private static bool _resolverInstalled;

    private SqliteNativeInfo? _info;

    public virtual string Name => "Qavren.Edge.Sqlite.Native";

    public virtual string LibraryName => QedgeNativeLibrary.DllImportNameForCurrentTarget;

    public virtual bool SupportsEncryption => false;

    /// <summary>What the provider reports to Microsoft.Data.Sqlite. See <see cref="QedgeNativeLibrary.ReportedName"/>.</summary>
    protected virtual string ReportedLibraryName => "qedge_sqlite3";

    /// <summary>The file base name actually loaded. The Cipher package overrides this to redirect.</summary>
    protected virtual string PhysicalLibraryName => "qedge_sqlite3";

    /// <summary>Set to <see langword="false"/> by tests that need to swap providers.</summary>
    public bool FreezeProvider { get; set; } = true;

    public string? ResolvedPath { get; private set; }

    public void Install()
    {
        lock (InstallGate)
        {
            InstallResolver();

            QedgeNativeLibrary.ReportedName = ReportedLibraryName;
            SQLitePCL.raw.SetProvider(new SQLite3Provider_qedge());

            if (FreezeProvider)
            {
                // SqliteConnection's static ctor reflectively calls SQLitePCL.Batteries_V2.Init(),
                // which would silently replace this provider if any transitive package ever brings
                // a batteries assembly into the app. FreezeProvider makes that a no-op.
                SQLitePCL.raw.FreezeProvider();
            }

            _info = Probe();
        }
    }

    public SqliteNativeInfo Describe()
        => _info ?? throw new InvalidOperationException("Install() has not run yet.");

    private void InstallResolver()
    {
        if (_resolverInstalled)
        {
            return;
        }

        var providerAssembly = typeof(SQLite3Provider_qedge).Assembly;
        NativeLibrary.SetDllImportResolver(providerAssembly, Resolve);
        _resolverInstalled = true;
    }

    private IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, QedgeNativeLibrary.DllImportNameForCurrentTarget, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        if (string.Equals(libraryName, "__Internal", StringComparison.Ordinal))
        {
            // iOS: the symbols are in the main executable; let the runtime handle it.
            return IntPtr.Zero;
        }

        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                ResolvedPath = candidate;
                return handle;
            }
        }

        if (NativeLibrary.TryLoad(PhysicalLibraryName, assembly, searchPath, out var fallback))
        {
            ResolvedPath = "(default probing)";
            return fallback;
        }

        throw new EdgeNativeException(
            RuntimeInformation.RuntimeIdentifier,
            PhysicalLibraryName,
            [.. Candidates()],
            $"Reference {Name} (or Qavren.Edge.Sqlite.Native.Cipher) and make sure the runtimes/ asset for " +
            $"{RuntimeInformation.RuntimeIdentifier} shipped. On iOS, confirm the xcframework NativeReference " +
            "appears in the build log.");
    }

    private IEnumerable<string> Candidates()
    {
        var fileName = OperatingSystem.IsWindows()
            ? PhysicalLibraryName + ".dll"
            : OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
                ? "lib" + PhysicalLibraryName + ".dylib"
                : "lib" + PhysicalLibraryName + ".so";

        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, fileName);
        yield return Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", fileName);
    }

    private SqliteNativeInfo Probe()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Mode=Memory");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version(), vec_version(), qedge_version()";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new EdgeNativeException(
                RuntimeInformation.RuntimeIdentifier,
                PhysicalLibraryName,
                [.. Candidates()],
                "The native library loaded but returned no version row; the build is malformed.");
        }

        var qedge = reader.GetString(2);
        var cipher = ParseSegment(qedge, "cipher ");

        return new SqliteNativeInfo(
            Name,
            LibraryName,
            ResolvedPath,
            reader.GetString(0),
            reader.GetString(1),
            cipher,
            ParseSegment(qedge, "build "));
    }

    /// <summary>qedge_version() is "sqlite &lt;v&gt; | vec &lt;v&gt; | cipher &lt;v|none&gt; | build &lt;sha&gt;".</summary>
    private static string ParseSegment(string version, string prefix)
    {
        foreach (var part in version.Split('|', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith(prefix, StringComparison.Ordinal))
            {
                return part[prefix.Length..];
            }
        }

        return "unknown";
    }
}
```

- [ ] **Step 2: Write `SqliteNativeInstallStartupTask.cs` and `SqliteNativeBuilderExtensions.cs`**

`SqliteNativeInstallStartupTask.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Sqlite.Native;

/// <summary>Startup task order 0. Everything else in the suite depends on this having run.</summary>
public sealed class SqliteNativeInstallStartupTask(
    ISqliteNativeProvider provider,
    ILogger<SqliteNativeInstallStartupTask> logger) : IEdgeStartupTask
{
    public int Order => EdgeStartupOrder.NativeProviderInstall;

    public Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            provider.Install();
            var info = provider.Describe();
            logger.LogInformation(
                EdgeEventIds.NativeProviderInstalled,
                "Installed {Provider} from {Path}: sqlite {Sqlite}, vec {Vec}, cipher {Cipher}, build {Build}.",
                info.ProviderName, info.ResolvedPath ?? "(unknown)", info.SqliteVersion, info.VecVersion,
                info.CipherVersion, info.BuildSha);
        }
        catch (EdgeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(EdgeEventIds.NativeProviderFailed, ex, "Native provider install failed.");
            throw new EdgeNativeException(
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                provider.LibraryName,
                [],
                "Reference Qavren.Edge.Sqlite.Native or Qavren.Edge.Sqlite.Native.Cipher for this platform.",
                ex);
        }

        return Task.CompletedTask;
    }
}
```

`SqliteNativeBuilderExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Sqlite.Native;

public static class SqliteNativeBuilderExtensions
{
    /// <summary>
    /// Registers the plain (unencrypted) native SQLite provider. Calling this together with
    /// <c>UseSqliteNativeCipher()</c> is a configuration error caught when a database is resolved.
    /// </summary>
    public static EdgeBuilder UseSqliteNative(this EdgeBuilder builder, Action<QedgeSqliteNativeProvider>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var provider = new QedgeSqliteNativeProvider();
        configure?.Invoke(provider);

        builder.Services.AddSingleton<ISqliteNativeProvider>(provider);
        builder.Services.AddSingleton<IEdgeStartupTask, SqliteNativeInstallStartupTask>();
        builder.Services.AddSingleton<IEdgeDiagnosticsContributor>(new NativeDiagnosticsContributor(provider));
        return builder;
    }

    private sealed class NativeDiagnosticsContributor(ISqliteNativeProvider provider) : IEdgeDiagnosticsContributor
    {
        public string ComponentName => "Qavren.Edge.Sqlite.Native";

        public string? ComponentVersion => typeof(NativeDiagnosticsContributor).Assembly.GetName().Version?.ToString();

        public IReadOnlyDictionary<string, string?> Describe()
        {
            try
            {
                var info = provider.Describe();
                return new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["providerName"] = info.ProviderName,
                    ["libraryName"] = info.LibraryName,
                    ["resolvedPath"] = info.ResolvedPath,
                    ["sqliteVersion"] = info.SqliteVersion,
                    ["vecVersion"] = info.VecVersion,
                    ["cipherVersion"] = info.CipherVersion,
                    ["buildSha"] = info.BuildSha,
                };
            }
            catch (InvalidOperationException)
            {
                return new Dictionary<string, string?>(StringComparer.Ordinal) { ["state"] = "not installed" };
            }
        }
    }
}
```

- [ ] **Step 3: Write `Qavren.Edge.Sqlite.Native.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0;net10.0-ios</TargetFrameworks>
    <IsPackable>true</IsPackable>
    <PackageId>Qavren.Edge.Sqlite.Native</PackageId>
    <Description>Native SQLite with sqlite-vec compiled in, for Qavren.Edge. Includes runtime libraries for Windows, Linux, macOS, Mac Catalyst, Android and iOS.</Description>
    <RootNamespace>Qavren.Edge.Sqlite.Native</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Qavren.Edge.Sqlite\Qavren.Edge.Sqlite.csproj" />
    <ProjectReference Include="..\Qavren.Edge.Sqlite.Provider\Qavren.Edge.Sqlite.Provider.csproj" />
  </ItemGroup>

  <!-- Which runtimes/ folder to also copy flat into a referencing project's output, so tests and
       samples can run on the dev box without installing the package. -->
  <PropertyGroup>
    <!-- The SDK sets NETCoreSdkPortableRuntimeIdentifier to the host RID (win-x64, win-arm64,
         linux-x64, osx-arm64, ...). Using it means an arm64 dev box copies the arm64 native
         instead of silently picking up an x64 one it cannot load. -->
    <QedgeHostRidFolder>$(NETCoreSdkPortableRuntimeIdentifier)\</QedgeHostRidFolder>
    <QedgeHostRidFolder Condition="'$(NETCoreSdkPortableRuntimeIdentifier)' == ''">win-x64\</QedgeHostRidFolder>
  </PropertyGroup>

  <ItemGroup>
    <_QedgeNative Include="$(NativeArtifactsDir)**\qedge_sqlite3.dll;$(NativeArtifactsDir)**\libqedge_sqlite3.so;$(NativeArtifactsDir)**\libqedge_sqlite3.dylib" />
    <None Include="@(_QedgeNative)" Pack="true" PackagePath="runtimes/%(RecursiveDir)native/" Visible="false">
      <CopyToOutputDirectory Condition="'%(RecursiveDir)' == '$(QedgeHostRidFolder)'">PreserveNewest</CopyToOutputDirectory>
      <Link Condition="'%(RecursiveDir)' == '$(QedgeHostRidFolder)'">%(Filename)%(Extension)</Link>
    </None>
  </ItemGroup>

  <ItemGroup Condition="Exists('$(NativeArtifactsDir)apple\qedge_sqlite3.xcframework')">
    <None Include="$(NativeArtifactsDir)apple\qedge_sqlite3.xcframework\**\*" Pack="true"
          PackagePath="xcframeworks/qedge_sqlite3.xcframework/%(RecursiveDir)%(Filename)%(Extension)" Visible="false" />
  </ItemGroup>

  <ItemGroup>
    <None Include="buildTransitive\net10.0-ios\Qavren.Edge.Sqlite.Native.targets" Pack="true"
          PackagePath="buildTransitive/net10.0-ios/" Visible="false" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Write `buildTransitive\net10.0-ios\Qavren.Edge.Sqlite.Native.targets`**

The file name must equal the package id exactly, or NuGet silently ignores it. A `runtimes/ios-arm64/native/*.a` asset would be linked automatically but could not carry `ForceLoad`, `SmartLink` or `Frameworks`, so the `NativeReference` route is required. One item, no RID conditions: the SDK resolves the correct slice from the xcframework's `Info.plist` by target framework, simulator flag, and architecture.

```xml
<Project>
  <ItemGroup Condition="'$(TargetPlatformIdentifier)' == 'ios'">
    <NativeReference Include="$(MSBuildThisFileDirectory)..\..\xcframeworks\qedge_sqlite3.xcframework">
      <Kind>Static</Kind>
      <ForceLoad>true</ForceLoad>
      <SmartLink>false</SmartLink>
      <Frameworks>Security</Frameworks>
    </NativeReference>
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Write the Cipher package**

`foundation\src\Qavren.Edge.Sqlite.Native.Cipher\QedgeSqlCipherNativeProvider.cs`:

```csharp
namespace Qavren.Edge.Sqlite.Native.Cipher;

/// <summary>
/// Same install path as the plain provider, redirected to <c>qedge_sqlcipher</c> and reporting the
/// library name as <c>sqlcipher</c> so Microsoft.Data.Sqlite's known-library table returns
/// <see langword="true"/> and its <c>Password</c> path is enabled.
/// </summary>
public sealed class QedgeSqlCipherNativeProvider : QedgeSqliteNativeProvider
{
    public override string Name => "Qavren.Edge.Sqlite.Native.Cipher";

    public override bool SupportsEncryption => true;

    protected override string ReportedLibraryName => "sqlcipher";

    protected override string PhysicalLibraryName => "qedge_sqlcipher";
}
```

`foundation\src\Qavren.Edge.Sqlite.Native.Cipher\SqliteNativeCipherBuilderExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Sqlite.Native.Cipher;

public static class SqliteNativeCipherBuilderExtensions
{
    /// <summary>
    /// Registers the SQLCipher native provider. Calling this together with <c>UseSqliteNative()</c>
    /// is a configuration error caught when a database is resolved.
    /// </summary>
    public static EdgeBuilder UseSqliteNativeCipher(
        this EdgeBuilder builder,
        Action<QedgeSqlCipherNativeProvider>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var provider = new QedgeSqlCipherNativeProvider();
        configure?.Invoke(provider);

        builder.Services.AddSingleton<Qavren.Edge.Sqlite.ISqliteNativeProvider>(provider);
        builder.Services.AddSingleton<IEdgeStartupTask, SqliteNativeInstallStartupTask>();
        return builder;
    }
}
```

`Qavren.Edge.Sqlite.Native.Cipher.csproj` — identical to the plain project with these three differences: `PackageId` is `Qavren.Edge.Sqlite.Native.Cipher`, every `qedge_sqlite3` in the `_QedgeNative` glob and in the xcframework path becomes `qedge_sqlcipher`, and it adds a `ProjectReference` to `..\Qavren.Edge.Sqlite.Native\Qavren.Edge.Sqlite.Native.csproj` (it subclasses that provider). Write it in full:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0;net10.0-ios</TargetFrameworks>
    <IsPackable>true</IsPackable>
    <PackageId>Qavren.Edge.Sqlite.Native.Cipher</PackageId>
    <Description>SQLCipher + LibTomCrypt build of native SQLite with sqlite-vec compiled in, for Qavren.Edge.</Description>
    <RootNamespace>Qavren.Edge.Sqlite.Native.Cipher</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Qavren.Edge.Sqlite.Native\Qavren.Edge.Sqlite.Native.csproj" />
  </ItemGroup>

  <PropertyGroup>
    <!-- The SDK sets NETCoreSdkPortableRuntimeIdentifier to the host RID (win-x64, win-arm64,
         linux-x64, osx-arm64, ...). Using it means an arm64 dev box copies the arm64 native
         instead of silently picking up an x64 one it cannot load. -->
    <QedgeHostRidFolder>$(NETCoreSdkPortableRuntimeIdentifier)\</QedgeHostRidFolder>
    <QedgeHostRidFolder Condition="'$(NETCoreSdkPortableRuntimeIdentifier)' == ''">win-x64\</QedgeHostRidFolder>
  </PropertyGroup>

  <ItemGroup>
    <_QedgeNative Include="$(NativeArtifactsDir)**\qedge_sqlcipher.dll;$(NativeArtifactsDir)**\libqedge_sqlcipher.so;$(NativeArtifactsDir)**\libqedge_sqlcipher.dylib" />
    <None Include="@(_QedgeNative)" Pack="true" PackagePath="runtimes/%(RecursiveDir)native/" Visible="false">
      <CopyToOutputDirectory Condition="'%(RecursiveDir)' == '$(QedgeHostRidFolder)'">PreserveNewest</CopyToOutputDirectory>
      <Link Condition="'%(RecursiveDir)' == '$(QedgeHostRidFolder)'">%(Filename)%(Extension)</Link>
    </None>
  </ItemGroup>

  <ItemGroup Condition="Exists('$(NativeArtifactsDir)apple\qedge_sqlcipher.xcframework')">
    <None Include="$(NativeArtifactsDir)apple\qedge_sqlcipher.xcframework\**\*" Pack="true"
          PackagePath="xcframeworks/qedge_sqlcipher.xcframework/%(RecursiveDir)%(Filename)%(Extension)" Visible="false" />
  </ItemGroup>

  <ItemGroup>
    <None Include="buildTransitive\net10.0-ios\Qavren.Edge.Sqlite.Native.Cipher.targets" Pack="true"
          PackagePath="buildTransitive/net10.0-ios/" Visible="false" />
  </ItemGroup>
</Project>
```

`foundation\src\Qavren.Edge.Sqlite.Native.Cipher\buildTransitive\net10.0-ios\Qavren.Edge.Sqlite.Native.Cipher.targets`:

```xml
<Project>
  <ItemGroup Condition="'$(TargetPlatformIdentifier)' == 'ios'">
    <NativeReference Include="$(MSBuildThisFileDirectory)..\..\xcframeworks\qedge_sqlcipher.xcframework">
      <Kind>Static</Kind>
      <ForceLoad>true</ForceLoad>
      <SmartLink>false</SmartLink>
      <Frameworks>Security</Frameworks>
    </NativeReference>
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Build both packages**

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge\foundation\src\Qavren.Edge.Sqlite.Native\Qavren.Edge.Sqlite.Native.csproj" -c Release
dotnet build "C:\Users\steve\projects\qavren-edge\foundation\src\Qavren.Edge.Sqlite.Native.Cipher\Qavren.Edge.Sqlite.Native.Cipher.csproj" -c Release
```

Expected: `Build succeeded` for both, for both `net10.0` and `net10.0-ios`.

- [ ] **Step 7: Verify (the win-x64 native reaches a referencing project's output)**

```powershell
dotnet pack "C:\Users\steve\projects\qavren-edge\foundation\src\Qavren.Edge.Sqlite.Native\Qavren.Edge.Sqlite.Native.csproj" -c Release -o "D:\Local\Temp\claude\C--Users-steve-projects\31ba80da-fb75-4f91-b2d4-942c33fcfa7c\scratchpad\pack"
pwsh -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; $nupkg = Get-ChildItem 'D:\Local\Temp\claude\C--Users-steve-projects\31ba80da-fb75-4f91-b2d4-942c33fcfa7c\scratchpad\pack\Qavren.Edge.Sqlite.Native.*.nupkg' | Select-Object -First 1; $zip=[IO.Compression.ZipFile]::OpenRead($nupkg.FullName); $names=$zip.Entries.FullName; $zip.Dispose(); $names | Where-Object { $_ -like 'runtimes/*' -or $_ -like 'buildTransitive/*' -or $_ -like 'lib/*' } | Sort-Object; if ($names -notcontains 'runtimes/win-x64/native/qedge_sqlite3.dll') { Write-Error 'win-x64 native not packed' }; if ($names -notcontains 'buildTransitive/net10.0-ios/Qavren.Edge.Sqlite.Native.targets') { Write-Error 'ios targets not packed' }; Write-Host 'OK: package layout correct'"
```

Expected: a listing containing `lib/net10.0/...`, `lib/net10.0-ios/...`, `runtimes/win-x64/native/qedge_sqlite3.dll` and `buildTransitive/net10.0-ios/Qavren.Edge.Sqlite.Native.targets`, then `OK: package layout correct`.

The assertion names `win-x64` only because that is the one slice this box can be relied on to have
produced. Every other RID — `win-arm64`, the three Android ABIs, both Linux RIDs, both macOS and
both Mac Catalyst RIDs — reaches the package through the same `$(NativeArtifactsDir)**` glob with
**no csproj change**, once `native.yml` has downloaded its artifacts into
`foundation/native/artifacts/`. The full-RID assertion belongs to CI, and `ci.yml`'s pack step runs
it (Task 6.2 Step 6):

```powershell
pwsh -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; $nupkg = Get-ChildItem 'D:\Local\Temp\claude\C--Users-steve-projects\31ba80da-fb75-4f91-b2d4-942c33fcfa7c\scratchpad\pack\Qavren.Edge.Sqlite.Native.*.nupkg' | Select-Object -First 1; $zip=[IO.Compression.ZipFile]::OpenRead($nupkg.FullName); $names=$zip.Entries.FullName; $zip.Dispose(); $expected = 'runtimes/win-arm64/native/qedge_sqlite3.dll'; if ($names -contains $expected) { Write-Host 'OK: win-arm64 slice packed too' } else { Write-Host 'NOTE: win-arm64 absent locally (ARM64 toolset not installed); native.yml supplies it' }"
```

Expected: either `OK: win-arm64 slice packed too` or the `NOTE:` line — both are acceptable
locally, neither is acceptable in CI, where Task 6.2's pack step fails if any expected RID folder
is missing.

---

---

## WAVE 9 — Meta package and integration tests

### Task 9.1: `Qavren.Edge` meta package

**Local-verifiable:** yes.

**Files:**
- Create: `foundation\src\Qavren.Edge\Qavren.Edge.csproj`
- Create: `foundation\src\Qavren.Edge\README.md`

- [ ] **Step 1: Write the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0;net10.0-ios</TargetFrameworks>
    <IsPackable>true</IsPackable>
    <PackageId>Qavren.Edge</PackageId>
    <Description>One-line install for the common Qavren.Edge case: hosting core, SQLite with sqlite-vec, and the plain native library. Add Qavren.Edge.Maui for MAUI lifecycle, or swap the native package for Qavren.Edge.Sqlite.Native.Cipher for encryption.</Description>
    <RootNamespace>Qavren.Edge.Meta</RootNamespace>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <!-- Meta package: no code of its own. -->
    <NoWarn>$(NoWarn);NU5128</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Qavren.Edge.Core\Qavren.Edge.Core.csproj" />
    <ProjectReference Include="..\Qavren.Edge.Sqlite\Qavren.Edge.Sqlite.csproj" />
    <ProjectReference Include="..\Qavren.Edge.Sqlite.Native\Qavren.Edge.Sqlite.Native.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write `foundation\src\Qavren.Edge\README.md`**

```markdown
# Qavren.Edge

SQLite with `sqlite-vec` compiled in, wired for `Microsoft.Extensions.*` dependency injection.

```csharp
services.AddQavrenEdge(edge => edge
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddMigration<M001_CreateNotes>()
    .UseSqliteNative());
```

Then inject `IEdgeDatabase`:

```csharp
await using var conn = await db.OpenConnectionAsync(ct);   // awaits startup, applies pragmas
var hits = await Knn.QueryAsync(conn, "notes_vec", query, k: 10, cancellationToken: ct);
```

Microsoft.Data.Sqlite, EF Core Sqlite (the `.Core` package), sqlite-net-pcl and Dapper all work
unchanged on top of this provider.

- MAUI lifecycle and `FileSystem`-backed paths: add `Qavren.Edge.Maui`.
- Encryption: replace `Qavren.Edge.Sqlite.Native` with `Qavren.Edge.Sqlite.Native.Cipher` and call
  `UseSqliteNativeCipher()`. Referencing both native packages is a configuration error.

MIT licensed. https://github.com/qavren-oss/qavren-edge
```

- [ ] **Step 3: Verify**

```powershell
dotnet pack "C:\Users\steve\projects\qavren-edge\foundation\src\Qavren.Edge\Qavren.Edge.csproj" -c Release -o "D:\Local\Temp\claude\C--Users-steve-projects\31ba80da-fb75-4f91-b2d4-942c33fcfa7c\scratchpad\pack"
```

Expected: `Successfully created package ...Qavren.Edge.<version>.nupkg`, exit code 0.

---

---

### Task 9.2: SQLite integration tests, including KNN against brute force

**Local-verifiable:** yes — this is the payoff for building the win-x64 native in Task 3.3.

**Files:**
- Modify: `foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj` (add the Native project reference)
- Create: `foundation\tests\Qavren.Edge.Sqlite.Tests\EdgeTestHost.cs`
- Create: `foundation\tests\Qavren.Edge.Sqlite.Tests\DatabaseIntegrationTests.cs`
- Create: `foundation\tests\Qavren.Edge.Sqlite.Tests\MigrationTests.cs`
- Create: `foundation\tests\Qavren.Edge.Sqlite.Tests\KnnAccuracyTests.cs`
- Create: `foundation\tests\Qavren.Edge.Sqlite.Tests\ConnectionExtensionsBindingTests.cs`
- Create: `foundation\tests\Qavren.Edge.Sqlite.Tests\SqliteLifecycleObserverTests.cs`

The last two close the two spec §13 bullets — "connection extensions binding" and "lifecycle
observer checkpoint behaviour" — that no other task covers. `Checkpoint_TruncatesTheWal` in
`DatabaseIntegrationTests` calls `IEdgeDatabase.CheckpointAsync` directly and therefore proves
nothing about the observer; these tests drive `IEdgeLifecycle` instead.

- [ ] **Step 1: Add the Native reference to the test project**

Replace the `ItemGroup` holding `ProjectReference` in `Qavren.Edge.Sqlite.Tests.csproj` with:

```xml
  <ItemGroup>
    <ProjectReference Include="..\..\src\Qavren.Edge.Sqlite\Qavren.Edge.Sqlite.csproj" />
    <ProjectReference Include="..\..\src\Qavren.Edge.Sqlite.Native\Qavren.Edge.Sqlite.Native.csproj" />
  </ItemGroup>
```

The Native project's `None` items carry `CopyToOutputDirectory` for the host RID, so
`qedge_sqlite3.dll` lands next to the test executable automatically. No `RuntimeIdentifier` is set
on the test project: setting one would flatten the whole `runtimes/` tree and break the layout.

- [ ] **Step 2: Write `EdgeTestHost.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Native;

namespace Qavren.Edge.Sqlite.Tests;

/// <summary>Builds a fully wired provider over a scratch directory, and deletes it on dispose.</summary>
public sealed class EdgeTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private EdgeTestHost(ServiceProvider services, string root)
    {
        _services = services;
        Root = root;
    }

    public string Root { get; }

    public IServiceProvider Services => _services;

    public static async Task<EdgeTestHost> StartAsync(
        Action<EdgeBuilder>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge =>
        {
            edge.Services.AddSingleton<IEdgePaths>(new FixedPaths(root));
            if (configure is null)
            {
                edge.AddSqlite(o =>
                {
                    o.DatabaseName = "test.db";
                    o.Directory = root;
                });
                edge.UseSqliteNative();
            }
            else
            {
                configure(edge);
            }
        });

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync(cancellationToken);
        return new EdgeTestHost(provider, root);
    }

    public IEdgeDatabase Database => _services.GetRequiredService<IEdgeDatabase>();

    public async ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await _services.DisposeAsync();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A WAL file may still be mapped on Windows; a leftover temp directory is harmless.
        }
    }

    private sealed class FixedPaths(string root) : IEdgePaths
    {
        public string Data => root;

        public string Cache => Path.Combine(root, "cache");
    }
}
```

`AddQavrenEdge` registers `IEdgePaths` with `TryAddSingleton`, so the explicit `AddSingleton` inside the callback would come second and lose. Register the test paths **before** calling `AddQavrenEdge` instead — move that line out of the callback:

```csharp
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new FixedPaths(root));   // wins: TryAddSingleton respects it
        services.AddQavrenEdge(edge => { /* AddSqlite / UseSqliteNative as above */ });
```

Write `StartAsync` with the `IEdgePaths` registration outside the `AddQavrenEdge` callback.

- [ ] **Step 3: Write `DatabaseIntegrationTests.cs`**

```csharp
using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite.Fts;
using Qavren.Edge.Sqlite.Vec;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

public class DatabaseIntegrationTests
{
    [Fact]
    public async Task NativeProvider_ReportsTheExpectedVersions()
    {
        await using var host = await EdgeTestHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var info = await host.Database.GetInfoAsync(TestContext.Current.CancellationToken);

        Assert.Equal("3.53.4", info.SqliteVersion);
        Assert.Equal("v0.1.9", info.VecVersion);   // leading 'v' is part of SQLITE_VEC_VERSION
        Assert.False(info.IsEncrypted);
    }

    [Fact]
    public async Task PerOpenPragmas_AreApplied()
    {
        await using var host = await EdgeTestHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var connection = await host.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);

        Assert.Equal("wal", (await connection.ScalarAsync<string>("PRAGMA journal_mode", cancellationToken: TestContext.Current.CancellationToken))!.ToLowerInvariant());
        Assert.Equal(1, await connection.ScalarAsync<int>("PRAGMA synchronous", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await connection.ScalarAsync<int>("PRAGMA foreign_keys", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(-8192, await connection.ScalarAsync<int>("PRAGMA cache_size", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PooledReuse_KeepsThePragmasAndTheProvider()
    {
        await using var host = await EdgeTestHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        for (var i = 0; i < 5; i++)
        {
            await using var connection = await host.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            Assert.Equal("v0.1.9", await connection.ScalarAsync<string>("SELECT vec_version()", cancellationToken: TestContext.Current.CancellationToken));
        }

        // Microsoft.Data.Sqlite's SqliteConnection static ctor has now run many times; the provider
        // must still be ours, proving FreezeProvider held.
        Assert.Equal("qedge_sqlite3", SQLitePCL.raw.GetNativeLibraryName());
    }

    [Fact]
    public async Task VecTableAndKnnRoundTrip()
    {
        await using var host = await EdgeTestHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var connection = await host.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await VecTable.CreateAsync(connection, "v", dims: 4, cancellationToken: TestContext.Current.CancellationToken);

        await connection.ExecuteAsync(
            "INSERT INTO v(rowid, embedding) VALUES ($id, $e)",
            [new SqliteParameter("$id", 1L), new SqliteParameter("$e", VecBlob.From([1f, 0f, 0f, 0f]))],
            TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO v(rowid, embedding) VALUES ($id, $e)",
            [new SqliteParameter("$id", 2L), new SqliteParameter("$e", VecBlob.From([0f, 1f, 0f, 0f]))],
            TestContext.Current.CancellationToken);

        var hits = await Knn.QueryAsync(
            connection, "v", new[] { 1f, 0f, 0f, 0f }.AsMemory(), k: 1,
            cancellationToken: TestContext.Current.CancellationToken);

        var hit = Assert.Single(hits);
        Assert.Equal(1L, hit.RowId);
        Assert.True(hit.Distance < 1e-5f, $"expected ~0 cosine distance, got {hit.Distance}");
    }

    [Fact]
    public async Task Fts5_CreateAndMatch()
    {
        await using var host = await EdgeTestHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var connection = await host.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await FtsTable.CreateAsync(connection, "f", ["title", "body"], cancellationToken: TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO f(title, body) VALUES ('hello', 'world of sqlite')",
            cancellationToken: TestContext.Current.CancellationToken);

        var count = await connection.ScalarAsync<long>(
            "SELECT count(*) FROM f WHERE f MATCH 'sqlite'",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1L, count);
    }

    [Fact]
    public async Task Checkpoint_TruncatesTheWal()
    {
        await using var host = await EdgeTestHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using (var connection = await host.Database.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync("CREATE TABLE t(x)", cancellationToken: TestContext.Current.CancellationToken);
            for (var i = 0; i < 200; i++)
            {
                await connection.ExecuteAsync("INSERT INTO t VALUES (randomblob(512))", cancellationToken: TestContext.Current.CancellationToken);
            }
        }

        await host.Database.CheckpointAsync(TestContext.Current.CancellationToken);

        var wal = host.Database.Path + "-wal";
        Assert.True(!File.Exists(wal) || new FileInfo(wal).Length == 0, "the WAL should be truncated after a checkpoint");
    }
}
```

- [ ] **Step 4: Write `MigrationTests.cs`**

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

public class MigrationTests
{
    private sealed class CreateNotes : IEdgeMigration
    {
        public int Version => 1;

        public string Name => "create notes";

        public Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken)
            => connection.ExecuteAsync(
                "CREATE TABLE notes(id INTEGER PRIMARY KEY, title TEXT NOT NULL)",
                cancellationToken: cancellationToken);
    }

    private sealed class AddBody : IEdgeMigration
    {
        public int Version => 2;

        public string Name => "add body";

        public Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken)
            => connection.ExecuteAsync("ALTER TABLE notes ADD COLUMN body TEXT", cancellationToken: cancellationToken);
    }

    private sealed class Broken : IEdgeMigration
    {
        public int Version => 3;

        public string Name => "broken";

        public Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken)
            => connection.ExecuteAsync("CREATE TABLE notes(oops)", cancellationToken: cancellationToken);
    }

    [Fact]
    public async Task FreshDatabase_AppliesEveryMigrationAndSetsUserVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        await using var host = await EdgeTestHost.StartAsync(edge => edge
            .AddSqlite(o => { o.DatabaseName = "m.db"; o.Directory = root; })
            .AddMigration<CreateNotes>()
            .AddMigration<AddBody>()
            .UseSqliteNative(), TestContext.Current.CancellationToken);

        var info = await host.Database.GetInfoAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, info.UserVersion);

        await using var connection = await host.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var columns = await connection.QueryAsync(
            "PRAGMA table_info(notes)", r => r.GetString(1),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("body", columns);
    }

    [Fact]
    public async Task Rerun_IsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        for (var run = 0; run < 2; run++)
        {
            await using var host = await EdgeTestHost.StartAsync(edge => edge
                .AddSqlite(o => { o.DatabaseName = "m.db"; o.Directory = root; })
                .AddMigration<CreateNotes>()
                .UseSqliteNative(), TestContext.Current.CancellationToken);

            Assert.Equal(1, (await host.Database.GetInfoAsync(TestContext.Current.CancellationToken)).UserVersion);
        }
    }

    // Spec 13 lists FOUR migration cases: fresh, PARTIAL, failing mid-run, idempotent rerun.
    // This is the partial one — the path every real app takes on its second release: the file
    // already sits at user_version = 1, so migration 1 must be SKIPPED (proved by the fact that
    // re-running it would throw "table notes already exists") and only 2 and 3 may run.
    [Fact]
    public async Task PartialUpgrade_SkipsAppliedMigrationsAndRunsOnlyThePendingOnes()
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // Release 1 of the app: only migration 1 exists.
        await using (var v1 = await EdgeTestHost.StartAsync(edge => edge
            .AddSqlite(o => { o.DatabaseName = "m.db"; o.Directory = root; })
            .AddMigration<CreateNotes>()
            .UseSqliteNative(), TestContext.Current.CancellationToken))
        {
            Assert.Equal(1, (await v1.Database.GetInfoAsync(TestContext.Current.CancellationToken)).UserVersion);

            await using var seed = await v1.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await seed.ExecuteAsync(
                "INSERT INTO notes(id, title) VALUES (1, 'kept')",
                cancellationToken: TestContext.Current.CancellationToken);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Release 2 of the same app, against the SAME file: migrations 1, 2 and 3 are all
        // registered, but only 2 and 3 are pending.
        var ran = new List<int>();
        await using var v2 = await EdgeTestHost.StartAsync(edge => edge
            .AddSqlite(o => { o.DatabaseName = "m.db"; o.Directory = root; })
            .AddMigration<CreateNotes>()
            .AddMigrations([new RecordingMigration(2, "add body", ran, "ALTER TABLE notes ADD COLUMN body TEXT"),
                            new RecordingMigration(3, "add tags", ran, "ALTER TABLE notes ADD COLUMN tags TEXT")])
            .UseSqliteNative(), TestContext.Current.CancellationToken);

        // Exactly the pending set ran, in ascending order. Migration 1 never executed: had it
        // run again, its CREATE TABLE would have failed and faulted startup.
        Assert.Equal([2, 3], ran);
        Assert.Equal(3, (await v2.Database.GetInfoAsync(TestContext.Current.CancellationToken)).UserVersion);

        await using var connection = await v2.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);

        var columns = await connection.QueryAsync(
            "PRAGMA table_info(notes)", r => r.GetString(1),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("body", columns);
        Assert.Contains("tags", columns);

        // The pre-existing row survived: a partial upgrade migrates, it does not recreate.
        Assert.Equal("kept", await connection.ScalarAsync<string>(
            "SELECT title FROM notes WHERE id = 1",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>A migration that records the fact that it ran, so the test can assert which
    /// versions the migrator chose to execute rather than only the end state.</summary>
    private sealed class RecordingMigration(int version, string name, List<int> ran, string sql) : IEdgeMigration
    {
        public int Version => version;

        public string Name => name;

        public async Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken)
        {
            ran.Add(version);
            await connection.ExecuteAsync(sql, cancellationToken: cancellationToken);
        }
    }

    [Fact]
    public async Task FailureMidRun_LeavesTheLastGoodVersionAndRaisesEdgeMigrationException()
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new TestPaths(root));
        services.AddQavrenEdge(edge => edge
            .AddSqlite(o => { o.DatabaseName = "m.db"; o.Directory = root; })
            .AddMigration<CreateNotes>()
            .AddMigration<AddBody>()
            .AddMigration<Broken>()
            .UseSqliteNative());

        await using var sp = services.BuildServiceProvider();

        var ex = await Assert.ThrowsAsync<EdgeMigrationException>(async () =>
            await sp.GetRequiredService<IEdgeHost>().EnsureStartedAsync(TestContext.Current.CancellationToken));

        Assert.Equal(3, ex.Version);
        Assert.Equal("broken", ex.Name);

        // The database is left at the last successful user_version.
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "m.db")}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, await connection.ScalarAsync<int>("PRAGMA user_version", cancellationToken: TestContext.Current.CancellationToken));
    }

    private sealed class TestPaths(string root) : IEdgePaths
    {
        public string Data => root;

        public string Cache => Path.Combine(root, "cache");
    }
}
```

- [ ] **Step 5: Write `KnnAccuracyTests.cs` — vec0 must agree with brute force**

```csharp
using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite.Vec;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

/// <summary>
/// Spec 13: KNN results must equal brute-force cosine and L2 on 1000 random 384-d vectors for
/// k in {1, 10, 100}. This is the test that would catch a broken blob encoding, a wrong
/// distance_metric token, or an endianness bug on a new platform.
/// </summary>
public class KnnAccuracyTests
{
    private const int Dims = 384;
    private const int Count = 1000;

    [Theory]
    [InlineData(VecMetric.Cosine, 1)]
    [InlineData(VecMetric.Cosine, 10)]
    [InlineData(VecMetric.Cosine, 100)]
    [InlineData(VecMetric.L2, 1)]
    [InlineData(VecMetric.L2, 10)]
    [InlineData(VecMetric.L2, 100)]
    public async Task Vec0_MatchesBruteForce(VecMetric metric, int k)
    {
        var random = new Random(20260910);
        var vectors = new float[Count][];
        for (var i = 0; i < Count; i++)
        {
            var v = new float[Dims];
            for (var d = 0; d < Dims; d++)
            {
                v[d] = (float)(random.NextDouble() * 2.0 - 1.0);
            }

            vectors[i] = v;
        }

        var query = new float[Dims];
        for (var d = 0; d < Dims; d++)
        {
            query[d] = (float)(random.NextDouble() * 2.0 - 1.0);
        }

        await using var host = await EdgeTestHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var connection = await host.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);

        var table = "vec_" + metric.ToString().ToLowerInvariant();
        await VecTable.CreateAsync(connection, table, Dims, metric, cancellationToken: TestContext.Current.CancellationToken);

        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            for (var i = 0; i < Count; i++)
            {
                await connection.ExecuteAsync(
                    $"INSERT INTO \"{table}\"(rowid, embedding) VALUES ($id, $e)",
                    [new SqliteParameter("$id", (long)i), new SqliteParameter("$e", VecBlob.From(vectors[i]))],
                    TestContext.Current.CancellationToken);
            }

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        var hits = await Knn.QueryAsync(connection, table, query.AsMemory(), k,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(k, hits.Count);

        var expected = Enumerable.Range(0, Count)
            .Select(i => (Id: (long)i, Distance: metric == VecMetric.Cosine
                ? CosineDistance(query, vectors[i])
                : L2Distance(query, vectors[i])))
            .OrderBy(t => t.Distance)
            .Take(k)
            .ToArray();

        // Ranks can legitimately swap when two distances are within float noise, so compare the
        // returned distances rather than the ids, and compare the id sets after that.
        for (var i = 0; i < k; i++)
        {
            Assert.True(
                Math.Abs(hits[i].Distance - expected[i].Distance) < 1e-4f,
                $"rank {i}: vec0 {hits[i].Distance} vs brute force {expected[i].Distance}");
        }

        Assert.Equal(
            expected.Select(e => e.Id).OrderBy(id => id).ToArray(),
            hits.Select(h => h.RowId).OrderBy(id => id).ToArray());
    }

    private static float L2Distance(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            sum += d * d;
        }

        return (float)Math.Sqrt(sum);
    }

    private static float CosineDistance(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            na += a[i] * (double)a[i];
            nb += b[i] * (double)b[i];
        }

        return (float)(1.0 - (dot / (Math.Sqrt(na) * Math.Sqrt(nb))));
    }
}
```

- [ ] **Step 6: Write `ConnectionExtensionsBindingTests.cs` — spec §8.6 binding**

Nothing else in the plan exercises `SqliteConnectionExtensions.ToParameters` (anonymous-object
binding, the `ReadOnlyMemory<float>` → vec-blob conversion, `null` → `DBNull`) or the
`IEnumerable<SqliteParameter>` overloads of `ExecuteAsync` / `ScalarAsync` / `QueryAsync`.
Write the tests first; they fail to compile until Task 6.1's file exists, which it does by now.

```csharp
using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Vec;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

public class ConnectionExtensionsBindingTests
{
    private static async Task<(EdgeTestHost Host, SqliteConnection Connection)> OpenAsync(CancellationToken ct)
    {
        var host = await EdgeTestHost.StartAsync(cancellationToken: ct);
        var connection = await host.Database.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            "CREATE TABLE b(id INTEGER PRIMARY KEY, name TEXT, score REAL, flag INTEGER, payload BLOB)",
            cancellationToken: ct);
        return (host, connection);
    }

    [Fact]
    public async Task AnonymousObject_BindsEveryPublicProperty()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, connection) = await OpenAsync(ct);
        await using var _ = host;
        await using var __ = connection;

        var rows = await connection.ExecuteAsync(
            "INSERT INTO b(id, name, score, flag) VALUES ($Id, $Name, $Score, $Flag)",
            SqliteConnectionExtensions.ToParameters(new { Id = 1L, Name = "alpha", Score = 2.5, Flag = true }),
            ct);

        Assert.Equal(1, rows);
        Assert.Equal("alpha", await connection.ScalarAsync<string>("SELECT name FROM b WHERE id = 1", cancellationToken: ct));
        Assert.Equal(2.5, await connection.ScalarAsync<double>("SELECT score FROM b WHERE id = 1", cancellationToken: ct));
        Assert.Equal(1L, await connection.ScalarAsync<long>("SELECT flag FROM b WHERE id = 1", cancellationToken: ct));
    }

    [Fact]
    public async Task NullProperty_BindsAsDbNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, connection) = await OpenAsync(ct);
        await using var _ = host;
        await using var __ = connection;

        await connection.ExecuteAsync(
            "INSERT INTO b(id, name) VALUES ($Id, $Name)",
            SqliteConnectionExtensions.ToParameters(new { Id = 2L, Name = (string?)null }),
            ct);

        Assert.Equal(1L, await connection.ScalarAsync<long>(
            "SELECT count(*) FROM b WHERE id = 2 AND name IS NULL", cancellationToken: ct));
    }

    [Theory]
    [InlineData(true)]   // ReadOnlyMemory<float>
    [InlineData(false)]  // float[]
    public async Task FloatVector_BindsAsAVecBlob(bool asMemory)
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, connection) = await OpenAsync(ct);
        await using var _ = host;
        await using var __ = connection;

        float[] vector = [1.0f, -2.5f, 3.25f, 0.0f];
        object parameters = asMemory
            ? new { Id = 3L, Payload = vector.AsMemory() }
            : new { Id = 3L, Payload = vector };

        await connection.ExecuteAsync(
            "INSERT INTO b(id, payload) VALUES ($Id, $Payload)",
            SqliteConnectionExtensions.ToParameters(parameters),
            ct);

        var stored = await connection.QueryAsync(
            "SELECT payload FROM b WHERE id = 3",
            reader => (byte[])reader["payload"],
            cancellationToken: ct);

        Assert.Single(stored);
        Assert.Equal(vector.Length * sizeof(float), stored[0].Length);
        Assert.Equal(vector, VecBlob.ToFloats(stored[0]));

        // And the native side agrees it is a vector, not just a blob of the right size.
        Assert.Equal(4L, await connection.ScalarAsync<long>(
            "SELECT vec_length(payload) FROM b WHERE id = 3", cancellationToken: ct));
    }

    [Fact]
    public async Task ExplicitSqliteParameters_BindByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, connection) = await OpenAsync(ct);
        await using var _ = host;
        await using var __ = connection;

        await connection.ExecuteAsync(
            "INSERT INTO b(id, name) VALUES ($id, $name)",
            [new SqliteParameter("$id", 4L), new SqliteParameter("$name", "beta")],
            ct);

        var names = await connection.QueryAsync(
            "SELECT name FROM b WHERE id = $id",
            reader => reader.GetString(0),
            [new SqliteParameter("$id", 4L)],
            ct);

        Assert.Equal(new[] { "beta" }, names);
    }

    [Fact]
    public async Task ScalarAsync_ReturnsDefaultForNoRowAndForNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, connection) = await OpenAsync(ct);
        await using var _ = host;
        await using var __ = connection;

        Assert.Null(await connection.ScalarAsync<string>("SELECT name FROM b WHERE id = 999", cancellationToken: ct));
        Assert.Equal(0L, await connection.ScalarAsync<long>("SELECT NULL", cancellationToken: ct));
    }
}
```

- [ ] **Step 7: Write `SqliteLifecycleObserverTests.cs` — spec §8.7 / §13 observer behaviour**

`SqliteLifecycleObserver` is registered by `AddSqlite` (Task 7.1) but nothing drives it. These
tests raise the hub events and assert the observable side effects. First add one property to
`EdgeTestHost.cs` so the hub is reachable:

```csharp
    public IEdgeLifecycle Lifecycle => _services.GetRequiredService<IEdgeLifecycle>();
```

(`using Qavren.Edge.Lifecycle;` at the top of `EdgeTestHost.cs`.)

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Sqlite;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

public class SqliteLifecycleObserverTests
{
    private static async Task WriteWithoutCheckpointAsync(EdgeTestHost host, CancellationToken ct)
    {
        await using var connection = await host.Database.OpenConnectionAsync(ct);
        await connection.ExecuteAsync("CREATE TABLE IF NOT EXISTS wal_probe(id INTEGER PRIMARY KEY, blob BLOB)", cancellationToken: ct);
        for (var i = 0; i < 200; i++)
        {
            await connection.ExecuteAsync(
                "INSERT INTO wal_probe(blob) VALUES ($b)",
                [new SqliteParameter("$b", new byte[4096])],
                ct);
        }
    }

    [Fact]
    public async Task Sleeping_CheckpointsAndTruncatesTheWal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await EdgeTestHost.StartAsync(cancellationToken: ct);

        await WriteWithoutCheckpointAsync(host, ct);

        var wal = host.Database.Path + "-wal";
        Assert.True(File.Exists(wal), "WAL mode should have produced a -wal file");
        Assert.True(new FileInfo(wal).Length > 0, "the WAL should hold un-checkpointed frames");

        await host.Lifecycle.RaiseSleepingAsync(ct);

        // wal_checkpoint(TRUNCATE) leaves the file present but zero-length.
        Assert.Equal(0, new FileInfo(wal).Length);
    }

    [Fact]
    public async Task Sleeping_RunsWithoutObserverFailures()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await EdgeTestHost.StartAsync(cancellationToken: ct);

        await host.Lifecycle.RaiseSleepingAsync(ct);

        var record = host.Lifecycle.RecentEvents[0];
        Assert.Equal(EdgeLifecycleEventKind.Sleeping, record.Kind);
        Assert.Equal(0, record.ObserverFailures);
    }

    [Theory]
    [InlineData(EdgeMemoryPressure.Low)]
    [InlineData(EdgeMemoryPressure.Moderate)]
    public async Task MemoryPressure_BelowCritical_LeavesThePoolAlone(EdgeMemoryPressure level)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await EdgeTestHost.StartAsync(cancellationToken: ct);

        await using (var connection = await host.Database.OpenConnectionAsync(ct))
        {
            await connection.ExecuteAsync("CREATE TABLE keepalive(x INTEGER)", cancellationToken: ct);
        }

        await host.Lifecycle.RaiseMemoryPressureAsync(level, ct);

        // The physical connection is still pooled, so the file is still open and cannot be deleted.
        Assert.Throws<IOException>(() => File.Delete(host.Database.Path));
    }

    [Fact]
    public async Task MemoryPressure_Critical_ReleasesPooledPhysicalConnections()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await EdgeTestHost.StartAsync(cancellationToken: ct);

        await using (var connection = await host.Database.OpenConnectionAsync(ct))
        {
            await connection.ExecuteAsync("CREATE TABLE keepalive(x INTEGER)", cancellationToken: ct);
        }

        await host.Lifecycle.RaiseMemoryPressureAsync(EdgeMemoryPressure.Critical, ct);

        // SQLite does not open the database file with FILE_SHARE_DELETE, so on Windows the delete
        // succeeds only once every pooled physical connection has actually been disposed. That is
        // the observable proof that ClearAllPools ran.
        File.Delete(host.Database.Path);
        Assert.False(File.Exists(host.Database.Path));
    }

    [Fact]
    public async Task Stopping_ReleasesPooledPhysicalConnections()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await EdgeTestHost.StartAsync(cancellationToken: ct);

        await using (var connection = await host.Database.OpenConnectionAsync(ct))
        {
            await connection.ExecuteAsync("CREATE TABLE keepalive(x INTEGER)", cancellationToken: ct);
        }

        await host.Lifecycle.RaiseStoppingAsync(ct);

        File.Delete(host.Database.Path);
        Assert.False(File.Exists(host.Database.Path));
    }

    [Fact]
    public async Task Observer_IsRegisteredExactlyOnceByAddSqlite()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await EdgeTestHost.StartAsync(cancellationToken: ct);

        var observers = host.Services.GetServices<IEdgeLifecycleObserver>()
            .Where(o => o.GetType().Name == "SqliteLifecycleObserver")
            .ToArray();

        Assert.Single(observers);
    }
}
```

`MemoryPressure_BelowCritical_LeavesThePoolAlone` is Windows-only by nature: POSIX `unlink`
succeeds on an open file, so the negative assertion means nothing on Linux or macOS. Make that
explicit rather than letting it fail on the Ubuntu and macOS CI legs — add this as the theory
method's first line:

```csharp
        Assert.SkipUnless(OperatingSystem.IsWindows(), "delete-while-open is only observable on Windows");
```

The two positive tests stay unguarded: they assert a delete that must succeed on every platform,
and on Windows they would fail outright if the pool had not been cleared.

- [ ] **Step 8: Run the suite**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj" -c Release -f net10.0
```

Expected: every test passes, exit code 0. If `NativeProvider_ReportsTheExpectedVersions` fails with `EdgeNativeException`, the `win-x64` artifact from Task 3.3 is missing — rerun `build-windows.ps1`.

- [ ] **Step 9: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj" -c Release -f net10.0
```

Expected: `Failed: 0`, exit code 0, with the six `Vec0_MatchesBruteForce` theory cases, the five `ConnectionExtensionsBindingTests` cases (one of them a two-case theory), the six `SqliteLifecycleObserverTests` cases and all **four** `MigrationTests` cases — fresh, partial, failing mid-run and idempotent rerun, which is exactly spec §13's list — among the passes.

---

---

### Task 9.3: Cipher tests

**Local-verifiable:** yes, provided Task 4.2 produced `qedge_sqlcipher.dll`. Each test skips with a clear reason when the artifact is absent, so the suite stays green on a machine that has not built it.

**Files:**
- Create: `foundation\tests\Qavren.Edge.Sqlite.Cipher.Tests\Qavren.Edge.Sqlite.Cipher.Tests.csproj`
- Create: `foundation\tests\Qavren.Edge.Sqlite.Cipher.Tests\CipherTests.cs`

The cipher provider must live in its own test process: `raw.FreezeProvider()` is process-wide, so a plain-provider test and a cipher-provider test cannot share a host.

- [ ] **Step 1: Write the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Qavren.Edge.Sqlite\Qavren.Edge.Sqlite.csproj" />
    <ProjectReference Include="..\..\src\Qavren.Edge.Sqlite.Native.Cipher\Qavren.Edge.Sqlite.Native.Cipher.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write `CipherTests.cs`**

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Native;
using Qavren.Edge.Sqlite.Native.Cipher;
using Xunit;

namespace Qavren.Edge.Sqlite.Cipher.Tests;

public class CipherTests
{
    private static readonly byte[] KeyA = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] KeyB = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();

    private static bool NativePresent =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "qedge_sqlcipher.dll")) ||
        File.Exists(Path.Combine(AppContext.BaseDirectory, "libqedge_sqlcipher.so")) ||
        File.Exists(Path.Combine(AppContext.BaseDirectory, "libqedge_sqlcipher.dylib"));

    private sealed class FixedPaths(string root) : IEdgePaths
    {
        public string Data => root;

        public string Cache => Path.Combine(root, "cache");
    }

    private static async Task<ServiceProvider> StartAsync(string root, SqliteKey? key, CancellationToken ct)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new FixedPaths(root));
        services.AddQavrenEdge(edge => edge
            .AddSqlite(o =>
            {
                o.DatabaseName = "secret.db";
                o.Directory = root;
                o.Key = key;
            })
            .UseSqliteNativeCipher());

        var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<IEdgeHost>().EnsureStartedAsync(ct);
        return sp;
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-cipher", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public async Task RawKey_OpensWritesAndReopens()
    {
        Assert.SkipUnless(NativePresent, "qedge_sqlcipher was not built on this machine; run build-windows.ps1 -Cipher.");

        var root = NewRoot();
        var ct = TestContext.Current.CancellationToken;

        await using (var sp = await StartAsync(root, SqliteKey.FromRawBytes(KeyA), ct))
        {
            var db = sp.GetRequiredService<IEdgeDatabase>();
            Assert.True(db.IsEncrypted);

            await using var connection = await db.OpenConnectionAsync(ct);
            await connection.ExecuteAsync("CREATE TABLE t(x TEXT)", cancellationToken: ct);
            await connection.ExecuteAsync("INSERT INTO t VALUES('hello')", cancellationToken: ct);
        }

        SqliteConnection.ClearAllPools();

        // Same process, same frozen provider: reopen and read back.
        await using (var sp = await StartAsync(root, SqliteKey.FromRawBytes(KeyA), ct))
        {
            await using var connection = await sp.GetRequiredService<IEdgeDatabase>().OpenConnectionAsync(ct);
            Assert.Equal("hello", await connection.ScalarAsync<string>("SELECT x FROM t", cancellationToken: ct));
        }
    }

    [Fact]
    public async Task PooledReuse_KeepsTheKey()
    {
        Assert.SkipUnless(NativePresent, "qedge_sqlcipher was not built on this machine.");

        var root = NewRoot();
        var ct = TestContext.Current.CancellationToken;

        await using var sp = await StartAsync(root, SqliteKey.FromRawBytes(KeyA), ct);
        var db = sp.GetRequiredService<IEdgeDatabase>();

        await using (var connection = await db.OpenConnectionAsync(ct))
        {
            await connection.ExecuteAsync("CREATE TABLE t(x TEXT)", cancellationToken: ct);
        }

        // Each iteration returns its connection to the pool and takes it back out again.
        for (var i = 0; i < 10; i++)
        {
            await using var connection = await db.OpenConnectionAsync(ct);
            Assert.Equal(0L, await connection.ScalarAsync<long>("SELECT count(*) FROM t", cancellationToken: ct));
        }
    }

    [Fact]
    public async Task WrongKey_RaisesEdgeDatabaseKeyExceptionAtStartup()
    {
        Assert.SkipUnless(NativePresent, "qedge_sqlcipher was not built on this machine.");

        var root = NewRoot();
        var ct = TestContext.Current.CancellationToken;

        await using (var sp = await StartAsync(root, SqliteKey.FromRawBytes(KeyA), ct))
        {
            await using var connection = await sp.GetRequiredService<IEdgeDatabase>().OpenConnectionAsync(ct);
            await connection.ExecuteAsync("CREATE TABLE t(x)", cancellationToken: ct);
        }

        SqliteConnection.ClearAllPools();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var sp = await StartAsync(root, SqliteKey.FromRawBytes(KeyB), ct);
        });
    }

    [Fact]
    public async Task Passphrase_AlsoWorks()
    {
        Assert.SkipUnless(NativePresent, "qedge_sqlcipher was not built on this machine.");

        var root = NewRoot();
        var ct = TestContext.Current.CancellationToken;

        await using var sp = await StartAsync(root, SqliteKey.FromPassphrase("correct horse battery staple"), ct);
        await using var connection = await sp.GetRequiredService<IEdgeDatabase>().OpenConnectionAsync(ct);

        await connection.ExecuteAsync("CREATE TABLE t(x)", cancellationToken: ct);
        Assert.Equal("v0.1.9", await connection.ScalarAsync<string>("SELECT vec_version()", cancellationToken: ct));
    }

    [Fact]
    public async Task Rekey_ChangesTheKey()
    {
        Assert.SkipUnless(NativePresent, "qedge_sqlcipher was not built on this machine.");

        var root = NewRoot();
        var ct = TestContext.Current.CancellationToken;

        await using var sp = await StartAsync(root, SqliteKey.FromRawBytes(KeyA), ct);
        var db = sp.GetRequiredService<IEdgeDatabase>();

        await using (var connection = await db.OpenConnectionAsync(ct))
        {
            await connection.ExecuteAsync("CREATE TABLE t(x TEXT)", cancellationToken: ct);
            await connection.ExecuteAsync("INSERT INTO t VALUES('kept')", cancellationToken: ct);
        }

        await db.RekeyAsync(SqliteKey.FromRawBytes(KeyB), ct);

        await using var reopened = await db.OpenConnectionAsync(ct);
        Assert.Equal("kept", await reopened.ScalarAsync<string>("SELECT x FROM t", cancellationToken: ct));
    }

    [Fact]
    public void PlainProviderPlusKey_IsAConfigurationError()
    {
        var root = NewRoot();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new FixedPaths(root));
        services.AddQavrenEdge(edge => edge
            .AddSqlite(o => { o.DatabaseName = "x.db"; o.Directory = root; o.Key = SqliteKey.FromRawBytes(KeyA); })
            .UseSqliteNative());

        using var sp = services.BuildServiceProvider();
        var db = sp.GetRequiredService<IEdgeDatabase>();

        var ex = Assert.Throws<EdgeConfigurationException>(() =>
            db.OpenConnectionAsync(TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult());

        Assert.Equal(EdgeErrorCode.EncryptionKeyWithoutCipherProvider, ex.Code);
    }
}
```

`PlainProviderPlusKey_IsAConfigurationError` deliberately never starts the host, so it does not install a second provider into this process.

- [ ] **Step 3: Verify**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Sqlite.Cipher.Tests\Qavren.Edge.Sqlite.Cipher.Tests.csproj" -c Release
```

Expected: `Failed: 0` with 6 passed (or 5 passed and 5 skipped if the cipher native is absent), exit code 0.

---

---

## WAVE 10 — Sample app and device test runner

### Task 10.1: MAUI sample app

**Local-verifiable:** partially — `net10.0-windows10.0.19041.0` builds and runs here, in **both**
the `Release` and the `Cipher` configuration (Task 4.2 produced `qedge_sqlcipher.dll` for
`win-x64`); the other three TFMs compile but cannot run locally.

**Files:**
- Create: `foundation\samples\Qavren.Edge.Sample\Qavren.Edge.Sample.csproj`
- Create: `foundation\samples\Qavren.Edge.Sample\MauiProgram.cs`
- Create: `foundation\samples\Qavren.Edge.Sample\App.xaml` and `App.xaml.cs`
- Create: `foundation\samples\Qavren.Edge.Sample\AppShell.xaml` and `AppShell.xaml.cs`
- Create: `foundation\samples\Qavren.Edge.Sample\Migrations\M001_CreateNotes.cs`
- Create: `foundation\samples\Qavren.Edge.Sample\Pages\DiagnosticsPage.xaml(.cs)`
- Create: `foundation\samples\Qavren.Edge.Sample\Pages\NotesPage.xaml(.cs)`
- Create: `foundation\samples\Qavren.Edge.Sample\Pages\HealthPage.xaml(.cs)`
- Create: `foundation\samples\Qavren.Edge.Sample\Pages\EncryptionPage.xaml(.cs)`
- Create: `foundation\samples\Qavren.Edge.Sample\SampleKey.cs`

Spec §15 asks for **four** pages — Diagnostics, Notes, Health and Encryption — and says the
Encryption page toggles between the two Native packages "via build configuration, not at runtime".
Spec §15 also says "all five TFMs"; a MAUI application head cannot target plain `net10.0`, so the
sample has four (adjustment 31).

- [ ] **Step 1: Create the app from the MAUI template, then rename**

```powershell
dotnet new maui -n Qavren.Edge.Sample -o "C:\Users\steve\projects\qavren-edge\foundation\samples\Qavren.Edge.Sample" --force
```

Then edit `Qavren.Edge.Sample.csproj`: set `<TargetFrameworks>net10.0-android;net10.0-ios;net10.0-maccatalyst;net10.0-windows10.0.19041.0</TargetFrameworks>`, `<IsPackable>false</IsPackable>`, `<ApplicationId>app.qavren.edge.sample</ApplicationId>`, and add:

```xml
  <!-- Spec 15: the Encryption page toggles the native package by BUILD CONFIGURATION.
       `Cipher` is a release-like configuration that swaps Qavren.Edge.Sqlite.Native for
       Qavren.Edge.Sqlite.Native.Cipher and defines QEDGE_CIPHER. Referencing both packages in
       one app is a configuration error the startup pipeline rejects, so this must be an
       either/or at build time, never a runtime switch. -->
  <PropertyGroup>
    <Configurations>Debug;Release;Cipher</Configurations>
  </PropertyGroup>

  <PropertyGroup Condition="'$(Configuration)' == 'Cipher'">
    <QedgeCipher>true</QedgeCipher>
    <Optimize>true</Optimize>
    <DefineConstants>$(DefineConstants);QEDGE_CIPHER</DefineConstants>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Qavren.Edge.Maui\Qavren.Edge.Maui.csproj" />
    <ProjectReference Include="..\..\src\Qavren.Edge.Sqlite\Qavren.Edge.Sqlite.csproj" />
  </ItemGroup>

  <ItemGroup Condition="'$(QedgeCipher)' == 'true'">
    <ProjectReference Include="..\..\src\Qavren.Edge.Sqlite.Native.Cipher\Qavren.Edge.Sqlite.Native.Cipher.csproj" />
  </ItemGroup>

  <ItemGroup Condition="'$(QedgeCipher)' != 'true'">
    <ProjectReference Include="..\..\src\Qavren.Edge.Sqlite.Native\Qavren.Edge.Sqlite.Native.csproj" />
  </ItemGroup>
```

- [ ] **Step 2: Write `Migrations\M001_CreateNotes.cs`**

```csharp
using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Fts;
using Qavren.Edge.Sqlite.Vec;

namespace Qavren.Edge.Sample.Migrations;

public sealed class M001_CreateNotes : IEdgeMigration
{
    public int Version => 1;

    public string Name => "create notes";

    public async Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
            "CREATE TABLE notes(id INTEGER PRIMARY KEY, title TEXT NOT NULL, body TEXT NOT NULL)",
            cancellationToken: cancellationToken);

        await VecTable.CreateAsync(connection, "notes_vec", dims: 384, cancellationToken: cancellationToken);

        await FtsTable.CreateAsync(
            connection, "notes_fts", ["title", "body"], contentTable: "notes",
            cancellationToken: cancellationToken);

        await FtsTable.CreateSyncTriggersAsync(
            connection, "notes_fts", "notes", ["title", "body"], cancellationToken);
    }
}
```

- [ ] **Step 3: Write `MauiProgram.cs`**

```csharp
using Microsoft.Extensions.Logging;
using Qavren.Edge.Maui;
using Qavren.Edge.Sample.Migrations;
using Qavren.Edge.Sample.Pages;
using Qavren.Edge.Sqlite;
#if QEDGE_CIPHER
using Qavren.Edge.Sqlite.Native.Cipher;
#else
using Qavren.Edge.Sqlite.Native;
#endif

namespace Qavren.Edge.Sample;

public static class MauiProgram
{
    /// <summary>Surfaced by the Encryption page so the UI can say which build it is running in.</summary>
    public const bool IsCipherBuild =
#if QEDGE_CIPHER
        true;
#else
        false;
#endif

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseQavrenEdge(edge => edge
                .AddSqlite(o =>
                {
                    o.DatabaseName = "sample.db";
#if QEDGE_CIPHER
                    // The key never lives in source. SecureStorage holds 32 raw bytes, generated
                    // once on first launch; the raw-key path skips SQLCipher's KDF, which is the
                    // recommended mobile shape (spec 8.8).
                    o.KeyProvider = SampleKey.GetAsync;
#endif
                })
                .AddMigration<M001_CreateNotes>()
#if QEDGE_CIPHER
                .UseSqliteNativeCipher());
#else
                .UseSqliteNative());
#endif

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddTransient<DiagnosticsPage>();
        builder.Services.AddTransient<NotesPage>();
        builder.Services.AddTransient<HealthPage>();
        builder.Services.AddTransient<EncryptionPage>();

        return builder.Build();
    }
}
```

`foundation\samples\Qavren.Edge.Sample\SampleKey.cs` — compiled in both configurations so the
Encryption page can reference it, but only *used* by the cipher build:

```csharp
using System.Security.Cryptography;
using Microsoft.Maui.Storage;
using Qavren.Edge.Sqlite;

namespace Qavren.Edge.Sample;

/// <summary>A 32-byte raw key kept in SecureStorage. Generated once, never printed.</summary>
public static class SampleKey
{
    private const string StorageKey = "qavren.edge.sample.dbkey";

    public static async ValueTask<SqliteKey> GetAsync(CancellationToken cancellationToken)
    {
        var existing = await SecureStorage.Default.GetAsync(StorageKey).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(existing))
        {
            return SqliteKey.FromRawBytes(Convert.FromHexString(existing));
        }

        var bytes = RandomNumberGenerator.GetBytes(32);
        await SecureStorage.Default.SetAsync(StorageKey, Convert.ToHexString(bytes)).ConfigureAwait(false);
        return SqliteKey.FromRawBytes(bytes);
    }

    public static Task<bool> HasKeyAsync()
        => SecureStorage.Default.GetAsync(StorageKey).ContinueWith(
            t => !string.IsNullOrEmpty(t.Result), TaskScheduler.Default);
}
```

- [ ] **Step 4: Write `Pages\DiagnosticsPage.xaml.cs`** (XAML is a `ScrollView` containing one `Label x:Name="ReportLabel"` with `FontFamily="Consolas"`)

```csharp
using Qavren.Edge.Diagnostics;

namespace Qavren.Edge.Sample.Pages;

public partial class DiagnosticsPage : ContentPage
{
    private readonly IEdgeDiagnostics _diagnostics;

    public DiagnosticsPage(IEdgeDiagnostics diagnostics)
    {
        InitializeComponent();
        _diagnostics = diagnostics;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ReportLabel.Text = EdgeDiagnosticsRenderer.ToText(_diagnostics.Report());
    }
}
```

- [ ] **Step 5: Write `Pages\NotesPage.xaml.cs`** (XAML: an `Entry x:Name="TitleEntry"`, a `Button Clicked="OnAdd"` labelled "Add note with a random 384-d vector", a `Button Clicked="OnSearch"` labelled "Nearest 10", and a `CollectionView x:Name="Results"`)

```csharp
using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Vec;

namespace Qavren.Edge.Sample.Pages;

public partial class NotesPage : ContentPage
{
    private readonly IEdgeDatabase _database;
    private readonly Random _random = new();

    public NotesPage(IEdgeDatabase database)
    {
        InitializeComponent();
        _database = database;
    }

    private float[] RandomVector()
    {
        var v = new float[384];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = (float)(_random.NextDouble() * 2.0 - 1.0);
        }

        return v;
    }

    private async void OnAdd(object? sender, EventArgs e)
    {
        var title = string.IsNullOrWhiteSpace(TitleEntry.Text) ? "note" : TitleEntry.Text;

        await _database.ExecuteInTransactionAsync(async (connection, transaction, ct) =>
        {
            await connection.ExecuteAsync(
                "INSERT INTO notes(title, body) VALUES ($title, $body)",
                [new SqliteParameter("$title", title), new SqliteParameter("$body", "sample body")],
                ct);

            var id = await connection.ScalarAsync<long>("SELECT last_insert_rowid()", cancellationToken: ct);

            await connection.ExecuteAsync(
                "INSERT INTO notes_vec(rowid, embedding) VALUES ($id, $e)",
                [new SqliteParameter("$id", id), new SqliteParameter("$e", VecBlob.From(RandomVector()))],
                ct);

            return id;
        });

        await DisplayAlert("Added", $"Inserted '{title}' with a random 384-d vector.", "OK");
    }

    private async void OnSearch(object? sender, EventArgs e)
    {
        await using var connection = await _database.OpenConnectionAsync();
        var hits = await Knn.QueryAsync(connection, "notes_vec", RandomVector().AsMemory(), k: 10);
        Results.ItemsSource = hits.Select(h => $"rowid {h.RowId}  distance {h.Distance:F4}").ToArray();
    }
}
```

- [ ] **Step 6: Write `Pages\HealthPage.xaml.cs`** (XAML: three `Label`s named `InfoLabel`, `CheckLabel`, `CheckpointLabel` and a `Button Clicked="OnCheckpoint"`)

```csharp
using Qavren.Edge.Sqlite;

namespace Qavren.Edge.Sample.Pages;

public partial class HealthPage : ContentPage
{
    private readonly IEdgeDatabase _database;

    public HealthPage(IEdgeDatabase database)
    {
        InitializeComponent();
        _database = database;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        var info = await _database.GetInfoAsync();
        InfoLabel.Text =
            $"sqlite {info.SqliteVersion}\nvec {info.VecVersion}\npage_size {info.PageSize}\n" +
            $"journal {info.JournalMode}\nuser_version {info.UserVersion}\n" +
            $"size {info.FileSizeBytes} bytes\nencrypted {info.IsEncrypted}";

        var check = await _database.CheckAsync();
        CheckLabel.Text = $"quick_check: {check.QuickCheck}  vec: {check.VecVersion}";
    }

    private async void OnCheckpoint(object? sender, EventArgs e)
    {
        await _database.CheckpointAsync();
        CheckpointLabel.Text = $"checkpointed at {DateTimeOffset.Now:HH:mm:ss}";
    }
}
```

- [ ] **Step 7: Write `Pages\EncryptionPage.xaml.cs`**

XAML: four `Label`s named `BuildLabel`, `ProviderLabel`, `DatabaseLabel` and `RekeyLabel`, plus a
`Button x:Name="RekeyButton" Clicked="OnRekey"` labelled "Rotate the database key".

This is the page spec §15 asks for. It reports which of the two Native packages was linked, and
that the choice was made at **build** time — there is no runtime toggle, because referencing both
packages in one app is a configuration error the startup pipeline rejects.

```csharp
using System.Security.Cryptography;
using Qavren.Edge.Sqlite;

namespace Qavren.Edge.Sample.Pages;

public partial class EncryptionPage : ContentPage
{
    private readonly IEdgeDatabase _database;
    private readonly ISqliteNativeProvider _native;

    public EncryptionPage(IEdgeDatabase database, ISqliteNativeProvider native)
    {
        InitializeComponent();
        _database = database;
        _native = native;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        BuildLabel.Text = MauiProgram.IsCipherBuild
            ? "Build configuration: Cipher — linked against Qavren.Edge.Sqlite.Native.Cipher.\n" +
              "Rebuild with `-c Release` to get the plain package instead."
            : "Build configuration: Release — linked against Qavren.Edge.Sqlite.Native (no encryption).\n" +
              "Rebuild with `-c Cipher` to get the SQLCipher package instead.";

        var native = _native.Describe();
        ProviderLabel.Text =
            $"provider {native.ProviderName}\nlibrary {native.LibraryName}\n" +
            $"resolved {native.ResolvedPath ?? "(default probing)"}\n" +
            $"sqlite {native.SqliteVersion}\nvec {native.VecVersion}\ncipher {native.CipherVersion}\n" +
            $"supportsEncryption {_native.SupportsEncryption}";

        var info = await _database.GetInfoAsync();
        DatabaseLabel.Text = $"{_database.Path}\nencrypted: {info.IsEncrypted}";

        RekeyButton.IsEnabled = MauiProgram.IsCipherBuild;
        RekeyLabel.Text = MauiProgram.IsCipherBuild
            ? "Rotating writes a new 32-byte raw key to SecureStorage and rekeys the file in place."
            : "Rekey is unavailable: this build has no codec.";
    }

    private async void OnRekey(object? sender, EventArgs e)
    {
        try
        {
            var fresh = SqliteKey.FromRawBytes(RandomNumberGenerator.GetBytes(32));
            await _database.RekeyAsync(fresh);
            RekeyLabel.Text = $"rekeyed at {DateTimeOffset.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            RekeyLabel.Text = $"rekey failed: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
```

The new key is deliberately **not** persisted here — the page proves `RekeyAsync` works; a real
app writes the new bytes to `SecureStorage` inside the same operation, and the page says so in its
label rather than pretending otherwise.

- [ ] **Step 8: Register the four pages in `AppShell.xaml`**

Four `ShellContent` tabs, titled Diagnostics, Notes, Health and Encryption, each with
`ContentTemplate="{DataTemplate pages:DiagnosticsPage}"` (and so on) and
`xmlns:pages="clr-namespace:Qavren.Edge.Sample.Pages"` on the `Shell` element.

- [ ] **Step 9: Verify (both build configurations)**

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge\foundation\samples\Qavren.Edge.Sample\Qavren.Edge.Sample.csproj" -c Release -f net10.0-windows10.0.19041.0
```

Expected: `Build succeeded`, 0 errors. Then the cipher configuration, which must swap the package:

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge\foundation\samples\Qavren.Edge.Sample\Qavren.Edge.Sample.csproj" -c Cipher -f net10.0-windows10.0.19041.0
```

Expected: `Build succeeded`, 0 errors. Then prove each configuration linked the package it should
have, and that neither linked both:

```powershell
pwsh -NoProfile -Command "$b='C:\Users\steve\projects\qavren-edge\foundation\samples\Qavren.Edge.Sample\bin'; $plain = Get-ChildItem -Path (Join-Path $b 'Release') -Recurse -Filter 'qedge_*.dll' | Select-Object -ExpandProperty Name -Unique; $cipher = Get-ChildItem -Path (Join-Path $b 'Cipher') -Recurse -Filter 'qedge_*.dll' | Select-Object -ExpandProperty Name -Unique; Write-Host ('Release: ' + ($plain -join ', ')); Write-Host ('Cipher:  ' + ($cipher -join ', ')); if ($plain -contains 'qedge_sqlcipher.dll') { Write-Error 'the Release configuration linked the cipher native' }; if ($cipher -notcontains 'qedge_sqlcipher.dll') { Write-Error 'the Cipher configuration did not link qedge_sqlcipher' }; Write-Host 'OK: the two configurations link different natives'"
```

Expected: `Release: qedge_sqlite3.dll`, `Cipher: qedge_sqlcipher.dll`, then
`OK: the two configurations link different natives`.

Finally confirm all four TFMs compile:

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge\foundation\samples\Qavren.Edge.Sample\Qavren.Edge.Sample.csproj" -c Release
```

Expected: `Build succeeded`, 0 errors.

---

---

### Task 10.2: Device test runner app hosting the two test assemblies

**Local-verifiable:** every project **builds** locally, for all four device TFMs and for the
`net10.0` host lane. Actually **running** the device lanes is **CI-only** (Android emulator on
`ubuntu-24.04` with KVM; iOS simulator and Mac Catalyst on `macos-15-intel`; Windows on
`windows-2025`).

Spec §13: "`Qavren.Edge.DeviceTests` is a MAUI app hosting **the same test assemblies**."
Spec §5.2: "MAUI runner app; **references the two test projects**." That is the whole point of the
device lane — the pragmas, migrations, KNN-vs-brute-force, connection-extension binding and
lifecycle-observer assertions must be proved against the **real** `libqedge_sqlite3.so` /
`__Internal` static library on a real Android and Apple runtime, not only against
`qedge_sqlite3.dll` on this Windows box. A pair of bespoke smoke tests would not do that.

So this task does two things: it turns `Qavren.Edge.Core.Tests` and `Qavren.Edge.Sqlite.Tests`
into **multi-targeted** projects (an MTP test app on `net10.0`, a plain class library on the four
device TFMs), and it builds the MAUI runner that hosts both of those assemblies plus one extra
device-only smoke class.

The plan's original assumption — xunit v3 hosted on Microsoft.Testing.Platform inside a MAUI app —
does not exist (adjustment 11). MTP's xunit v3 entry point (`xunit.v3.runner.inproc.console`)
injects a `Main` that collides with the MAUI app host, and Microsoft's own .NET 10 MAUI
unit-testing page points at **mattleibow/DeviceRunners** instead. `Shiny.Xunit.Runners.Maui` is
dead (2022, xunit 2.4.1, net6.0 TFMs). That constraint is exactly why the two test projects must
split their package references per TFM rather than simply gaining four more TFMs.

**Files:**
- Modify: `foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj` (replace wholesale)
- Modify: `foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj` (replace wholesale)
- Create: `foundation\tests\Qavren.Edge.DeviceTests\Qavren.Edge.DeviceTests.csproj`
- Create: `foundation\tests\Qavren.Edge.DeviceTests\MauiProgram.cs`
- Create: `foundation\tests\Qavren.Edge.DeviceTests\App.xaml` and `App.xaml.cs`
- Create: `foundation\tests\Qavren.Edge.DeviceTests\DevicePaths.cs`
- Create: `foundation\tests\Qavren.Edge.DeviceTests\NativeSmokeTests.cs`

- [ ] **Step 1: Re-target `Qavren.Edge.Core.Tests` to the host lane plus four device TFMs**

Replace `foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj` wholesale:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- net10.0 is the HOST lane: an MTP test application, run with `dotnet run -f net10.0`.
         The four platform TFMs exist ONLY so this same assembly can be loaded by
         Qavren.Edge.DeviceTests and executed on a device (spec 13 / 5.2). -->
    <TargetFrameworks>net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst;net10.0-windows10.0.19041.0</TargetFrameworks>
    <IsPackable>false</IsPackable>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'">21.0</SupportedOSPlatformVersion>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios'">15.0</SupportedOSPlatformVersion>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'maccatalyst'">15.0</SupportedOSPlatformVersion>
  </PropertyGroup>

  <!-- HOST LANE ONLY. The xunit.v3 metapackage pulls xunit.v3.runner.inproc.console, which is
       what generates the Main that Microsoft.Testing.Platform needs. -->
  <PropertyGroup Condition="'$(TargetFramework)' == 'net10.0'">
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
    <PackageReference Include="xunit.v3" />
  </ItemGroup>

  <!-- DEVICE LANES. A plain class library. Referencing xunit.v3 or xunit.v3.core here would
       inject a Main and collide with the MAUI application host (adjustment 11). -->
  <PropertyGroup Condition="'$(TargetFramework)' != 'net10.0'">
    <OutputType>Library</OutputType>
    <IsTestingPlatformApplication>false</IsTestingPlatformApplication>
    <GenerateTestingPlatformEntryPoint>false</GenerateTestingPlatformEntryPoint>
  </PropertyGroup>
  <ItemGroup Condition="'$(TargetFramework)' != 'net10.0'">
    <PackageReference Include="xunit.v3.extensibility.core" />
    <PackageReference Include="xunit.v3.assert" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
    <ProjectReference Include="..\..\src\Qavren.Edge.Core\Qavren.Edge.Core.csproj" />
  </ItemGroup>
</Project>
```

`Qavren.Edge.Core` targets `net10.0` only, which every `net10.0-*` platform TFM is compatible
with, so no change is needed there.

- [ ] **Step 2: Re-target `Qavren.Edge.Sqlite.Tests` the same way**

Replace `foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj` wholesale.
It is the Core.Tests file plus this project's own references — repeated in full rather than
described as "same as Step 1", because the two projects are edited independently:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst;net10.0-windows10.0.19041.0</TargetFrameworks>
    <IsPackable>false</IsPackable>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'">21.0</SupportedOSPlatformVersion>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios'">15.0</SupportedOSPlatformVersion>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'maccatalyst'">15.0</SupportedOSPlatformVersion>
  </PropertyGroup>

  <PropertyGroup Condition="'$(TargetFramework)' == 'net10.0'">
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
    <PackageReference Include="xunit.v3" />
  </ItemGroup>

  <PropertyGroup Condition="'$(TargetFramework)' != 'net10.0'">
    <OutputType>Library</OutputType>
    <IsTestingPlatformApplication>false</IsTestingPlatformApplication>
    <GenerateTestingPlatformEntryPoint>false</GenerateTestingPlatformEntryPoint>
  </PropertyGroup>
  <ItemGroup Condition="'$(TargetFramework)' != 'net10.0'">
    <PackageReference Include="xunit.v3.extensibility.core" />
    <PackageReference Include="xunit.v3.assert" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
    <ProjectReference Include="..\..\src\Qavren.Edge.Sqlite\Qavren.Edge.Sqlite.csproj" />
    <ProjectReference Include="..\..\src\Qavren.Edge.Sqlite.Native\Qavren.Edge.Sqlite.Native.csproj" />
  </ItemGroup>
</Project>
```

`Qavren.Edge.Sqlite.Native` already multi-targets `net10.0` plus every platform TFM (Task 8.1), so
on `net10.0-android` this reference brings `runtimes/android-*/native/libqedge_sqlite3.so` and on
`net10.0-ios` it brings the `<NativeReference>` to the static xcframework. That is precisely the
wiring the device lane exists to prove.

- [ ] **Step 3: Verify both test libraries compile for every TFM**

Do this **before** writing the runner, because it is where the one real unknown surfaces.

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj" -c Release
dotnet build "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj" -c Release
```

Expected: `Build succeeded`, 0 errors, five TFMs each. No `-f` here on purpose: this step exists to prove every TFM compiles.

**If, and only if, the device TFMs fail with `CS0103: The name 'TestContext' does not exist`**, the
`TestContext` type is not surfaced by `xunit.v3.extensibility.core` in the pinned 3.2.2 build. Do
**not** add `xunit.v3` or `xunit.v3.core` to the device TFMs — that reintroduces the `Main`
collision. Apply this self-contained fix instead, which needs no new package and no version guess.

Create `foundation\tests\Qavren.Edge.Core.Tests\TestCancellation.cs`:

```csharp
namespace Qavren.Edge.Core.Tests;

/// <summary>A cancellation source every test can use, on the host lane and on device alike.
/// It exists because <c>TestContext.Current</c> is only guaranteed on the host lane; the device
/// host is DeviceRunners, not Microsoft.Testing.Platform.</summary>
internal static class TestCancellation
{
    private static readonly CancellationTokenSource Source = new(TimeSpan.FromMinutes(5));

    public static CancellationToken Token => Source.Token;
}
```

and the identical file at `foundation\tests\Qavren.Edge.Sqlite.Tests\TestCancellation.cs`, changing
only the first line to `namespace Qavren.Edge.Sqlite.Tests;`. Then replace every occurrence of
`TestContext.Current.CancellationToken` in both test projects with `TestCancellation.Token`:

```powershell
pwsh -NoProfile -Command "foreach ($d in 'Qavren.Edge.Core.Tests','Qavren.Edge.Sqlite.Tests') { Get-ChildItem \"C:\Users\steve\projects\qavren-edge\foundation\tests\$d\*.cs\" | ForEach-Object { $t = Get-Content $_ -Raw; $n = $t.Replace('TestContext.Current.CancellationToken','TestCancellation.Token'); if ($n -ne $t) { Set-Content -NoNewline $_ $n; Write-Host \"patched $($_.Name)\" } } }"
```

Then re-run this step's two builds; they must succeed.

- [ ] **Step 4: Write the runner project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0-android;net10.0-ios;net10.0-maccatalyst;net10.0-windows10.0.19041.0</TargetFrameworks>
    <UseMaui>true</UseMaui>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <ApplicationId>app.qavren.edge.devicetests</ApplicationId>
    <ApplicationTitle>Qavren.Edge device tests</ApplicationTitle>
    <RootNamespace>Qavren.Edge.DeviceTests</RootNamespace>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'">21.0</SupportedOSPlatformVersion>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios'">15.0</SupportedOSPlatformVersion>
    <SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'maccatalyst'">15.0</SupportedOSPlatformVersion>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Maui.Controls" />
    <PackageReference Include="DeviceRunners.VisualRunners.Maui" />
    <PackageReference Include="DeviceRunners.VisualRunners.Xunit3" />
    <!-- Provides the MSBuild targets that make `dotnet test -f net10.0-android` deploy, launch,
         stream results back over TCP and write a TRX. -->
    <PackageReference Include="DeviceRunners.Testing.Targets" />
    <!-- This project's OWN test class (NativeSmokeTests) needs the attributes and asserts.
         Same rule as the libraries: never xunit.v3 or xunit.v3.core here. -->
    <PackageReference Include="xunit.v3.extensibility.core" />
    <PackageReference Include="xunit.v3.assert" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Qavren.Edge.Maui\Qavren.Edge.Maui.csproj" />
    <ProjectReference Include="..\..\src\Qavren.Edge.Sqlite\Qavren.Edge.Sqlite.csproj" />
    <ProjectReference Include="..\..\src\Qavren.Edge.Sqlite.Native\Qavren.Edge.Sqlite.Native.csproj" />
  </ItemGroup>

  <!-- Spec 5.2: "references the two test projects". These are the assemblies the runner hosts,
       so the device lane runs the SAME pragma, migration, KNN-vs-brute-force,
       connection-extension and lifecycle-observer tests the host lane runs.
       SetTargetFramework is not needed: both projects multi-target the four device TFMs, so
       MSBuild resolves the matching one. -->
  <ItemGroup>
    <ProjectReference Include="..\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj" />
    <ProjectReference Include="..\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj" />
  </ItemGroup>
</Project>
```

Note the runner does **not** reference `Qavren.Edge.Sqlite.Cipher.Tests`. It cannot: those tests
need `Qavren.Edge.Sqlite.Native.Cipher`, and referencing both native packages in one app is a
configuration error the startup pipeline rejects by design (spec §5.1). Cipher coverage stays on
the host lane, where each test project is its own process.

- [ ] **Step 5: Write `DevicePaths.cs`**

```csharp
using Microsoft.Maui.Storage;

namespace Qavren.Edge.DeviceTests;

/// <summary>Sandbox-relative paths for the device lane. Never cache the absolute string: on iOS
/// the sandbox path carries an application GUID segment that changes across reinstalls.</summary>
public sealed class DevicePaths : IEdgePaths
{
    public string Data => FileSystem.Current.AppDataDirectory;

    public string Cache => FileSystem.Current.CacheDirectory;
}
```

- [ ] **Step 6: Write `MauiProgram.cs`, `App.xaml.cs` and `App.xaml`**

```csharp
using DeviceRunners.VisualRunners;
using Microsoft.Maui.Hosting;

namespace Qavren.Edge.DeviceTests;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseVisualTestRunner(config => config
                .AddCliConfiguration()          // reads config from `dotnet test` / the DeviceRunners CLI
                .AddConsoleResultChannel()
                // Spec 13: "hosting the same test assemblies". All three, in one runner.
                .AddTestAssembly(typeof(MauiProgram).Assembly)
                .AddTestAssemblies(
                    typeof(Qavren.Edge.Core.Tests.PathsTests).Assembly,
                    typeof(Qavren.Edge.Sqlite.Tests.MigrationTests).Assembly)
                .AddXunit3());

        return builder.Build();
    }
}
```

`App.xaml.cs`:

```csharp
using DeviceRunners.VisualRunners;

namespace Qavren.Edge.DeviceTests;

public partial class App : Application
{
    public App() => InitializeComponent();

    protected override Window CreateWindow(IActivationState? activationState)
        => new VisualRunnerWindow();
}
```

`App.xaml`:

```xml
<?xml version="1.0" encoding="UTF-8" ?>
<Application xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             x:Class="Qavren.Edge.DeviceTests.App">
    <Application.Resources>
        <ResourceDictionary />
    </Application.Resources>
</Application>
```

- [ ] **Step 7: Write `NativeSmokeTests.cs` — the device-only additions**

The two hosted assemblies already cover pragmas, migrations, KNN accuracy, connection-extension
binding and the lifecycle observer. This class adds only what is meaningless on the host: proof
that the **platform-specific** native artifact loaded and reports the pinned versions, and that
`FileSystem`-backed paths work inside the sandbox.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Hosting;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Native;
using Qavren.Edge.Sqlite.Vec;
using Xunit;

namespace Qavren.Edge.DeviceTests;

public class NativeSmokeTests
{
    private static async Task<ServiceProvider> StartAsync(CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new DevicePaths());
        services.AddQavrenEdge(edge => edge
            .AddSqlite(o => o.DatabaseName = "device-tests.db")
            .UseSqliteNative());

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync(cancellationToken);
        return provider;
    }

    [Fact]
    public async Task NativeLibraryLoadsAndReportsVersions()
    {
        await using var services = await StartAsync(TestContext.Current.CancellationToken);

        var info = await services.GetRequiredService<IEdgeDatabase>()
            .GetInfoAsync(TestContext.Current.CancellationToken);

        Assert.Equal("3.53.4", info.SqliteVersion);
        Assert.Equal("v0.1.9", info.VecVersion);
    }

    [Fact]
    public async Task Vec0AndFts5AreAvailableOnDevice()
    {
        await using var services = await StartAsync(TestContext.Current.CancellationToken);
        await using var connection = await services.GetRequiredService<IEdgeDatabase>()
            .OpenConnectionAsync(TestContext.Current.CancellationToken);

        await VecTable.CreateAsync(connection, "device_vec", dims: 4,
            cancellationToken: TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            "CREATE VIRTUAL TABLE IF NOT EXISTS device_fts USING fts5(body)",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1L, await connection.ScalarAsync<long>(
            "SELECT count(*) FROM sqlite_master WHERE name = 'device_vec'",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SandboxPathsAreUsable()
    {
        IEdgePaths paths = new DevicePaths();

        Assert.False(string.IsNullOrWhiteSpace(paths.Data));
        Assert.False(string.IsNullOrWhiteSpace(paths.Cache));

        var probe = Path.Combine(paths.Data, "qedge-probe.txt");
        File.WriteAllText(probe, "ok");
        Assert.Equal("ok", File.ReadAllText(probe));
        File.Delete(probe);
    }
}
```

If Step 3 forced the `TestCancellation` fallback, add the same `TestCancellation.cs` under
`Qavren.Edge.DeviceTests` with namespace `Qavren.Edge.DeviceTests` and use `TestCancellation.Token`
here too.

- [ ] **Step 8: Verify the runner builds, and that the host lane still runs**

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.DeviceTests\Qavren.Edge.DeviceTests.csproj" -c Release -f net10.0-windows10.0.19041.0
```

Expected: `Build succeeded`, 0 errors.

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.DeviceTests\Qavren.Edge.DeviceTests.csproj" -c Release
```

Expected: `Build succeeded`, 0 errors, for all four TFMs.

The host lane must be unaffected by the re-targeting. `dotnet run` on a multi-TFM project refuses
to guess, so `-f net10.0` is **required** from here on — it is already present on every host-run
command in this plan and in `ci.yml`:

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj" -c Release -f net10.0
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Sqlite.Tests\Qavren.Edge.Sqlite.Tests.csproj" -c Release -f net10.0
```

Expected: two `Failed: 0` blocks, exit code 0 each — the same results as in waves 4 and 9.

- [ ] **Step 9: Assert the runner really does host both test assemblies**

A `ProjectReference` that silently stops flowing is exactly the regression that would turn the
device lane back into two smoke tests without anyone noticing, so pin it down mechanically:

```powershell
pwsh -NoProfile -Command "$p='C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.DeviceTests'; $out=Join-Path $p 'bin\Release\net10.0-windows10.0.19041.0'; foreach ($a in 'Qavren.Edge.Core.Tests.dll','Qavren.Edge.Sqlite.Tests.dll') { $f=Get-ChildItem -Recurse -Path $out -Filter $a -ErrorAction SilentlyContinue | Select-Object -First 1; if (-not $f) { throw \"device runner output is missing $a - the ProjectReference to the test library is not flowing\" }; Write-Host \"OK $a\" }; $src=Get-Content (Join-Path $p 'MauiProgram.cs') -Raw; foreach ($t in 'Qavren.Edge.Core.Tests.PathsTests','Qavren.Edge.Sqlite.Tests.MigrationTests') { if ($src -notmatch [regex]::Escape($t)) { throw \"MauiProgram.cs does not register the assembly of $t\" } }; Write-Host 'OK: the device runner hosts both test assemblies'"
```

Expected:

```
OK Qavren.Edge.Core.Tests.dll
OK Qavren.Edge.Sqlite.Tests.dll
OK: the device runner hosts both test assemblies
```

**Where this app actually runs.** Spec §13 puts it on four hosts, and `ci.yml` (Task 6.2 Step 6)
has one job per host: `device-tests-android` (Android emulator on `ubuntu-24.04` with KVM),
`device-tests-ios` (iOS simulator on `macos-15-intel`), `device-tests-maccatalyst`
(`macos-15-intel`, `maccatalyst-x64`) and `device-tests-windows` (`windows-2025`). Each writes a
TRX inside the app sandbox, `DeviceRunners.Testing.Targets` streams it back to the host, and the
workflow converts it to JUnit with `foundation/tools/trx2junit/trx2junit.py` and publishes it with
`dorny/test-reporter@v1` — that is spec §13's "written to a file inside the app sandbox, pulled by
the workflow, and published as JUnit". Executing any of those four lanes is CI-only.

**Cost note.** Hosting the full suites makes each device lane longer than a two-test smoke run —
the KNN-vs-brute-force theory over 1k 384-d vectors is the heaviest single case. That is the price
of the signal spec §13 asks for, and it is paid on free public-repo runners (suite decision 14).
If a lane ever times out, the fix is to trait-filter the KNN theory down to `k ∈ {1, 10}` on
device, never to drop the assemblies.

---

---

## WAVE 11 — Closing gate

### Task 11.1: Full-solution verification

**Local-verifiable:** yes. This is the gate that closes the plan.

- [ ] **Step 1: Restore and build the whole solution**

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge\QavrenEdge.slnx" -c Release
```

Expected: `Build succeeded`, 0 errors, 0 warnings (`TreatWarningsAsErrors` is on, so any warning is already an error).

- [ ] **Step 2: Run every host test project**

```powershell
pwsh -NoProfile -Command "$root='C:\Users\steve\projects\qavren-edge\foundation\tests'; $projects = 'Qavren.Edge.Core.Tests','Qavren.Edge.Provider.Tests','Qavren.Edge.Sqlite.Tests','Qavren.Edge.Sqlite.Cipher.Tests'; foreach ($p in $projects) { Write-Host \"=== $p ===\"; dotnet run --project (Join-Path $root \"$p\$p.csproj\") -c Release -f net10.0; if ($LASTEXITCODE -ne 0) { Write-Error \"$p FAILED\" } }; Write-Host 'OK: all host test projects passed'"
```

Expected: four `Failed: 0` blocks followed by `OK: all host test projects passed`.

`-f net10.0` is mandatory for `Qavren.Edge.Core.Tests` and `Qavren.Edge.Sqlite.Tests`, which
Task 10.2 multi-targeted so the device runner can host them (spec §13 / §5.2). It is harmless
for the other two, which target `net10.0` only.

- [ ] **Step 3: Pack everything**

```powershell
dotnet pack "C:\Users\steve\projects\qavren-edge\QavrenEdge.slnx" -c Release -o "C:\Users\steve\projects\qavren-edge\artifacts\packages"
pwsh -NoProfile -Command "Get-ChildItem 'C:\Users\steve\projects\qavren-edge\artifacts\packages\*.nupkg' | Select-Object -ExpandProperty Name | Sort-Object"
```

Expected seven packages: `Qavren.Edge`, `Qavren.Edge.Core`, `Qavren.Edge.Maui`, `Qavren.Edge.Sqlite`, `Qavren.Edge.Sqlite.Native`, `Qavren.Edge.Sqlite.Native.Cipher`, `Qavren.Edge.Sqlite.Provider`.

- [ ] **Step 4: Format check**

```powershell
dotnet format "C:\Users\steve\projects\qavren-edge\QavrenEdge.slnx" --verify-no-changes
```

Expected: no output and exit code 0. If it reports changes, run it without `--verify-no-changes`, then re-run this step.

- [ ] **Step 5: Both drift checks**

```powershell
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tools\ProviderGen\ProviderGen.csproj" -c Release -- manifest --out "C:\Users\steve\projects\qavren-edge\foundation\tools\ProviderGen\provider.manifest.json" --check
dotnet run --project "C:\Users\steve\projects\qavren-edge\foundation\tools\ProviderGen\ProviderGen.csproj" -c Release -- generate --manifest "C:\Users\steve\projects\qavren-edge\foundation\tools\ProviderGen\provider.manifest.json" --template "C:\Users\steve\projects\qavren-edge\foundation\src\Qavren.Edge.Sqlite.Provider\Template\provider_internal_funcptrs.cs.template" --out "C:\Users\steve\projects\qavren-edge\foundation\src\Qavren.Edge.Sqlite.Provider\Generated\SQLite3Provider_qedge.g.cs" --check
```

Expected: two `OK: ... is up to date.` lines, both exit 0.

- [ ] **Step 6: The two non-.NET checks**

```powershell
python "C:\Users\steve\projects\qavren-edge\foundation\tools\trx2junit\test_trx2junit.py"
python "C:\Users\steve\projects\qavren-edge\foundation\tools\ci-checks\assert-workflows.py" "C:\Users\steve\projects\qavren-edge"
```

Expected: `OK: 3 tests passed`, then
`OK: gates, 4 device lanes, JUnit, win-arm64, native artifact cache + reuse, SBOM and native release assets all present (windows lane on windows-2025)`.

- [ ] **Step 7: The two app heads, in every configuration the plan defines**

```powershell
dotnet build "C:\Users\steve\projects\qavren-edge\foundation\samples\Qavren.Edge.Sample\Qavren.Edge.Sample.csproj" -c Release
dotnet build "C:\Users\steve\projects\qavren-edge\foundation\samples\Qavren.Edge.Sample\Qavren.Edge.Sample.csproj" -c Cipher -f net10.0-windows10.0.19041.0
dotnet build "C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.DeviceTests\Qavren.Edge.DeviceTests.csproj" -c Release
```

Expected: `Build succeeded`, 0 errors, three times. (`QavrenEdge.slnx` builds the sample and the
device-test app in `Release` only; the `Cipher` configuration is exercised here.)

Then re-run Task 10.2 Step 9's assertion, so the closing gate itself proves the device runner
still hosts both test assemblies rather than a pair of bespoke smoke tests:

```powershell
pwsh -NoProfile -Command "$p='C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.DeviceTests'; $out=Join-Path $p 'bin\Release\net10.0-windows10.0.19041.0'; foreach ($a in 'Qavren.Edge.Core.Tests.dll','Qavren.Edge.Sqlite.Tests.dll') { $f=Get-ChildItem -Recurse -Path $out -Filter $a -ErrorAction SilentlyContinue | Select-Object -First 1; if (-not $f) { throw \"device runner output is missing $a\" }; Write-Host \"OK $a\" }; Write-Host 'OK: the device runner hosts both test assemblies'"
```

Expected: two `OK ...dll` lines, then `OK: the device runner hosts both test assemblies`.

- [ ] **Step 8: Spec coverage sweep**

Walk spec §§6–15 and tick each requirement against the task that implements it. The seven that
were missed once and must not be missed again: the sample's **Encryption** page and its `Cipher`
configuration (§15 → Task 10.1), **`win-arm64`** (§10.3 → Tasks 3.3/4.2/6.2), the release
**SBOM and native assets** (§14 → Task 6.2), **branch protection actually applied** (§14 →
Task 1.2 plus the README checklist), the device runner **hosting the two test projects rather than
bespoke smoke tests** (§13 / §5.2 → Task 10.2 Steps 1, 2, 4 and 9), the **cached-native reuse on
managed-only PRs** (§10.4 → Task 6.2 Steps 5 and 6, asserted by `assert-workflows.py`), and the
**partial**-upgrade migration case (§13 → Task 9.2 Step 4). If any is missing, the plan is not
closed.

---

---

## CI-only work

The repo `qavren-oss/qavren-edge` exists (spec §16.1), but during implementation it has no `main`
and implementers never push, so nothing below can be exercised until the owner completes the
README bootstrap checklist:

| Task | Why it is CI-only |
|---|---|
| 2.3 (Android/Apple compile paths in `CMakeLists.txt`) | no NDK, no clang, no macOS |
| 3.3 / 4.2 (`win-arm64` slices) | cross-compiles here only with the ARM64 MSVC toolset installed, and can never be **run** on this x64 box; `windows-2025` builds and CI asserts them |
| 6.2 (every workflow run, all four device lanes, the JUnit publication, the SBOM, the release assets) | needs `main` and a push; the YAML, the converter and the workflow-contract assertion are all verified locally |
| 8.1 (iOS `NativeReference` / xcframework resolution) | needs a real Apple build to resolve the xcframework slice |
| 5.2 (MAUI bridge runtime behaviour on android / ios / maccatalyst) | compiles locally, cannot run |
| 10.1 (sample app on android / ios / maccatalyst) | compiles locally, cannot run; Windows runs here in both configurations |
| 10.2 (device **execution** of `Qavren.Edge.Core.Tests` + `Qavren.Edge.Sqlite.Tests` inside the runner) | Android emulator on `ubuntu-24.04` + KVM, iOS simulator and Mac Catalyst on `macos-15-intel`, Windows on `windows-2025`. The re-targeting, the runner build for all four TFMs, and the assertion that both test assemblies reach the runner's output are all verified locally (Task 10.2 Steps 3, 8, 9) |
| Branch protection | applied by the owner with `gh api` once `main` exists and both gate jobs have reported |

## Open risks

1. **`SQLITE_OMIT_LOAD_EXTENSION` + `sqlite3_auto_extension`.** Believed compatible (SQLite's auto-extension API sits outside the `OMIT` guard in `loadext.c`), but proven only by Task 3.3 Step 5. The remedy if it fails is written into that step.
2. **libtomcrypt source selection.** The list is parsed from upstream's makefile with `pk/`, `math/` and `encauth/` excluded. If the link fails on MPI symbols, extend `EXCLUDE_PREFIXES` — never hand-write a file list.
3. **SQLCipher's LibTomCrypt provider is young.** It was removed in 4.12.0 and restored in 4.14.0 (March 2026), with fortuna seeding changed again in 4.17.0. It is the least-exercised of SQLCipher's providers. Task 4.2's smoke test is the guard.
4. **DeviceRunners is 0.1.0-preview with one maintainer.** Exit strategy: .NET 11 ships first-party MTP mobile support in `dotnet test` with `--device`, but its platform test templates are MSTest-based today and there is no documented xunit-v3-on-device path. Re-evaluate after .NET 11 GA; do not assume the migration is free.
5. **`Windows.System.MemoryManager` on unpackaged WinUI 3** is undocumented. Every touch is inside a `try/catch`; the worst case is that Windows never raises memory pressure.
6. **`windows-2025` runner image now carries VS 2026.** The native build uses `vswhere` + Ninja and never names a VS generator, so an image bump cannot break it.
7. **`win-arm64` is never executed before release.** GitHub has no hosted Windows-on-ARM runner in this plan's matrix, so the arm64 DLL is cross-compiled, checksum-published and shipped without ever having run a query. The first execution is a consumer's. Mitigation available if this bites: add a `windows-11-arm` lane to `ci.yml`'s `test` matrix — the label exists — and run the Sqlite suite there. Out of scope for sub-project 1; recorded so the omission is deliberate.
8. **The two gate jobs are the only required checks.** If a future job is added to `ci.yml` and forgotten in `ci-gate`'s `needs:` list, its failure will not block a merge. `assert-workflows.py` checks the gates exist but cannot know which jobs *ought* to be gated; adding a job means adding it to `needs:` in the same PR.
9. **`anchore/sbom-action@v0` is a floating major.** It is the only third-party action in the release path that is not pinned to an exact release. If reproducibility of the SBOM step matters more than automatic updates, pin it to a SHA in the same PR that first publishes a release.
