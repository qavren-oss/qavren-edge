# Maintainer notes

Operational facts for people who cut releases and keep CI green. Consumers
should not need anything here; the root `README.md` and the area READMEs are
the public documentation.

## Repository layout

| Folder | Contents |
|---|---|
| `foundation/` | Hosting core, SQLite packages, the native build (`native/`), the sample app (`samples/`), CI helper scripts (`tools/ci-checks/`), the icon renderer (`tools/icon/`) |
| `embeddings/` | ONNX hosting, embeddings, the vector store, the trim-smoke tool |
| `ingestion/` | Ingestion core and its four satellites |
| `chat/` | Chat over ONNX Runtime GenAI and the RAG recipe |
| `docs/` | Design specs and implementation plans under `superpowers/`, and this file |
| `.github/` | `ci.yml` (every PR), `native.yml` (called by ci and release), `release.yml` (tags), `tier0-genai-smoke.yml` (manual), `upstream-pins.yml`, branch protection JSON |

Each area folder has a `Directory.Build.props` that appends its package tags;
the root `Directory.Build.targets` packs the icon and each package's README
and composes the tags. Error-code ranges are allocated per area and listed in
`foundation/docs/errors.md`.

## GitHub and nuget.org

- Organisation `qavren-oss`, repository `qavren-oss/qavren-edge`. The bare name
  `qavren` on GitHub is an unrelated user account; never use it in a remote
  URL, a workflow, or a tool argument.
- Branch protection on `main`: required contexts `ci-gate` and
  `natives / native-gate`, strict, linear history, conversation resolution.
  The JSON is `.github/branch-protection.json` and `assert-workflows.py` checks
  that the contexts still match the gate jobs.
- The nuget.org owner is the `Qavren` organisation (admin `stevenfackley`).
  The `Qavren.` package-ID prefix is reserved to it (requested 2026-09-10,
  confirmed by nuget.org support 2026-09-11).
- Publishing uses nuget.org **Trusted Publishing**, not an API key: a policy on
  the `Qavren` owner trusts GitHub Actions from `qavren-oss/qavren-edge`,
  workflow `release.yml`, glob `Qavren.*`. `release.yml` requests
  `id-token: write` and `NuGet/login@v1` exchanges the OIDC token for a
  one-hour key at publish time. There is no `NUGET_API_KEY` secret and there
  must not be one.

## Cutting a release

Versions are computed by MinVer from `v*` tags; there is no version in any
project file. A tag containing `-` (e.g. `v1.0.0-rc.1`) produces a prerelease
on nuget.org and a GitHub release marked prerelease; a bare `vX.Y.Z` tag
produces a stable release. `Qavren.Edge.Ingestion.DataIngestion` is the one
exception: its own suffix target (see its `.csproj`) keeps it on
`X.Y.Z-preview` even on a stable tag, because it references a prerelease-only
Microsoft package and NuGet's `NU5104` rule forbids a stable package
depending on a prerelease one — see "DataIngestion stays prerelease" below.

1. Everything to ship is on `main` and `ci-gate` is green.
2. Dry run: `gh workflow run release.yml --ref main`. On `workflow_dispatch`
   the workflow builds the natives, packs, asserts the package metadata,
   zips, generates the SBOM and checksums, and uploads a `release-dry-run`
   artifact; the NuGet push and the GitHub release are skipped. This proves
   the workflow shape but never touches nuget.org.
3. Rehearsal, required for a stable cut: tag `v1.0.0-rc.1` on `main` and push
   it.
   ```
   git tag -a v1.0.0-rc.1 <sha> -m "Qavren.Edge 1.0.0-rc.1"
   git push origin v1.0.0-rc.1
   ```
   `release.yml` runs for real — this is a genuine OIDC push of a prerelease,
   the only end-to-end proof of the publish path and of the DataIngestion
   suffix logic short of the stable tag itself. On a `-` tag the suffix target
   doesn't fire, so every package, `Qavren.Edge.Ingestion.DataIngestion`
   included, packs untouched as `1.0.0-rc.1`.
4. Verify the rehearsal before cutting the stable tag: all 17 packages listed
   on nuget.org, the GitHub release marked prerelease, and
   `dotnet add package Qavren.Edge --prerelease` resolving `1.0.0-rc.1` in a
   scratch project.
5. Tag `v1.0.0` on the same commit and push it:
   ```
   git tag -a v1.0.0 <sha> -m "Qavren.Edge 1.0.0"
   git push origin v1.0.0
   ```
   `release.yml` runs again, this time stable:
   `Qavren.Edge.Ingestion.DataIngestion` packs as `1.0.0-preview` (its suffix
   target fires because the tag carries no `-`), every other package as
   `1.0.0`.
6. Confirm the stable release: packages, symbol packages, native archives,
   SBOM and `SHA256SUMS.txt` all present, GitHub release not marked
   prerelease. Unlist the `1.0.0-rc.1` packages only if the rehearsal turned
   up something wrong with them; leaving them listed is fine.

A published version cannot be replaced, only unlisted — this applies to the
rc packages too if step 6 finds a reason to pull them, and applies without
exception to `1.0.0` itself once it is out. If a tagged run fails before the
push, nothing is published: fix on `main`, delete and re-point the tag, push
it again. Re-running the failed job replays the workflow file at the tagged
commit, so it only helps when the fix is not in the workflow.

### DataIngestion stays prerelease

`ci.yml`'s Windows pack lane re-packs the whole solution under
`-p:MinVerVersionOverride=9.9.9` and runs
`assert-packages.ps1 -StableOverride 9.9.9` against it, on every PR, so a
stable release tag cannot reach `release.yml`'s pack step and fail `NU5104`
there — proven 2026-09-23: an unguarded `-p:MinVerVersionOverride=1.0.0` pack
of `Qavren.Edge.Ingestion.DataIngestion` alone fails `NU5104` (a stable
package must not depend on a prerelease one); with the suffix target it packs
`1.0.0-preview` instead, depending on `Qavren.Edge.Ingestion 1.0.0`.

### After 1.0

- First follow-up PR: set `PackageValidationBaselineVersion` to `1.0.0` in the
  packable block of `Directory.Build.targets` (the type-forward of
  `IEdgeModelPaths` from PR #32 into Core is intended shape to carry forward
  into that baseline, not a regression to suppress).
- ADR 0003 and chat ADR 0009 hold the ONNX Runtime / ONNX Runtime GenAI pin
  re-evaluations. A pin bump is a post-1.0 minor, proved by the tier-0 GenAI
  smoke plus the nightly model tests as the soak.

## CI shape

- `ci.yml` runs on every PR and on `main`: host test lanes on Windows, Ubuntu
  and macOS; device lanes for Android (emulator), iOS (simulator), Mac
  Catalyst and Windows; a trim smoke; and the `natives` job that calls
  `native.yml`. `ci-gate` fans everything in and is the one required context.
- `native.yml` builds the SQLite natives once per OS leg and caches them on a
  content key; a managed-only PR reuses the cache, and the `reuse` job repairs
  a missing cache entry from the last good run's artifact (it needs
  `actions: read` from every caller, `ci.yml` and `release.yml` both grant it).
- `model-tests` and `chat-model-tests` run nightly and on manual dispatch
  only, fetch a pinned model and verify its SHA-256 on every run; they never
  gate a PR.
- `foundation/tools/ci-checks/assert-workflows.py` pins all of the above and
  fails when a workflow drifts. Run it locally after any workflow edit.
- `foundation/tools/ci-checks/assert-packages.ps1` opens every packed
  `.nupkg` and checks icon, README, MIT licence and area tags. The Windows
  pack lane runs it on every PR.
- The Windows pack lane also re-packs the solution under
  `-p:MinVerVersionOverride=9.9.9` into `artifacts/packages-stable` and runs
  `assert-packages.ps1 -StableOverride 9.9.9` against it, proving a stable
  release tag will not fail `NU5104` (see "DataIngestion stays prerelease"
  above).

Local gate before a PR that touches packaging or workflows:

```
dotnet restore QavrenEdge.slnx
dotnet build QavrenEdge.slnx -c Release
dotnet pack QavrenEdge.slnx -c Release -o artifacts/packages
pwsh foundation/tools/ci-checks/assert-packages.ps1
python foundation/tools/ci-checks/assert-workflows.py
dotnet format QavrenEdge.slnx --verify-no-changes --no-restore
```

`dotnet run -p:TargetFrameworks=net10.0` on a test project re-restores the
projects it references for that single TFM; run `dotnet restore` on the
solution again before `dotnet format`, or it reports `IDE0005` on every file.

## Docs site

`https://edge.qavrensolutions.com` is a DocFX site (`docs/site/`), built by
`docs.yml` on every PR and deployed to GitHub Pages on every push to `main`.
Repository markdown is pulled in from where it lives and mirrored under
`repo/`; the API reference is generated from the packages' XML documentation
(`Qavren.Edge.Maui` as `net10.0-android` from `docs/site/docfx.maui.json`, run
first and ungated because DocFX warns about its `net10.0`-only project reference;
everything else as `net10.0` from `docs/site/docfx.json`). The gated build runs with
`--warningsAsErrors`, so an unresolved link in any README that
is part of the site fails the PR: link to documentation files by relative path
and to source or samples by absolute GitHub URL.

The navbar nests one level only, so Decisions, API reference and MAUI API are sections with
their own folder TOC and overview page (`docs/site/decisions/`, `docs/site/api/`,
`docs/site/api-maui/`); the generated API pages land under `api/reference/` and
`api-maui/reference/`, which are gitignored. After adding or renaming an ADR run
`python docs/site/tools/generate-decisions.py` to regenerate the Decisions section.

Local build: `dotnet tool restore` once, then
`dotnet docfx docs/site/docfx.json --serve` and open the printed URL.

GitHub Pages is configured with `build_type: workflow` and the custom domain;
DNS is a `CNAME edge -> qavren-oss.github.io` record on Cloudflare, not
proxied. If the certificate ever lapses, GitHub reissues it once the record
resolves; nothing in the repo holds it.

## Verification evidence

- The tier-0 GenAI smoke (`tier0-genai-smoke.yml`, manual) links ONNX Runtime
  GenAI and generates one token over the committed fixture model on Windows,
  Linux, unpackaged WinUI, an Android emulator, an iOS simulator and Mac
  Catalyst. All six legs passed in
  [run 34637304766](https://github.com/qavren-oss/qavren-edge/actions/runs/34637304766).
  The packaged (MSIX) WinUI path is not covered: the repository has no signing
  identity.
- The `release.yml` dry run
  [34716740504](https://github.com/qavren-oss/qavren-edge/actions/runs/34716740504)
  produced 17 packages, 17 symbol packages, 3 native archives, the SBOM and
  the checksum file.
- The trim-smoke evidence for ingestion is in `ingestion/README.md`.

## Bootstrap history

| Item | Done |
|---|---|
| `main` exists and CI reports on it | 2026-09-10 |
| Branch protection applied | 2026-09-12 |
| `Qavren.` prefix reserved on nuget.org | 2026-09-11 |
| Added to the workspace CI audit roster | 2026-09-12 |
| `release.yml` dry run green | 2026-09-12 |
| Trusted publishing policy created; `release.yml` switched to OIDC | 2026-09-12 |
| `IEdgeModelPaths` moved to Core (PR #32) | 2026-09-23 |
| XML-doc gate: `CS1591` an error for every shipped package | 2026-09-23 |
| Benchmark suite + `benchmarks.yml` | 2026-09-23 |
| 1.0 wording + DataIngestion stable-tag guard | 2026-09-23 |
