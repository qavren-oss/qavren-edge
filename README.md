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
