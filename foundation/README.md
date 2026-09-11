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

## MAUI hosted-service limitation

`UseQavrenEdge()` calls `Services.RemoveAll<IHostedService>()` — the generic-host
`EdgeHostedService` would block MAUI's launch path, so the platform lifecycle bridge
calls `IEdgeHost.Start()` instead. A MAUI app that registers its own `IHostedService`
implementations must skip `UseQavrenEdge()`, call `Services.AddQavrenEdge(...)`
directly, and wire the lifecycle bridge itself.

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
dotnet run --project C:\Users\steve\projects\qavren-edge\foundation\tests\Qavren.Edge.Core.Tests\Qavren.Edge.Core.Tests.csproj -c Release
```

The Sqlite tests additionally need the native library from the step above; the
`Qavren.Edge.Sqlite.Native` project copies it into every referencing project's
output directory.
