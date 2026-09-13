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
project file.

1. Everything to ship is on `main` and `ci-gate` is green.
2. Optional dry run: `gh workflow run release.yml --ref main`. On
   `workflow_dispatch` the workflow builds the natives, packs, asserts the
   package metadata, zips, generates the SBOM and checksums, and uploads a
   `release-dry-run` artifact; the NuGet push and the GitHub release are
   skipped.
3. Tag on `main` and push the tag:
   ```
   git tag -a v0.1.0-preview.1 <sha> -m "Qavren.Edge 0.1.0-preview.1"
   git push origin v0.1.0-preview.1
   ```
   A tag containing `-` produces a prerelease on nuget.org and a GitHub
   release marked prerelease.
4. `release.yml` runs on the tag: natives on three legs, pack, the
   `assert-packages.ps1` metadata check, zips, SBOM, checksums, OIDC login,
   one push per package with `--skip-duplicate`, then the GitHub release with
   the packages, symbol packages, native archives, SBOM and `SHA256SUMS.txt`.

A published version cannot be replaced, only unlisted. If a tagged run fails
before the push, nothing is published: fix on `main`, delete and re-point the
tag, push it again. Re-running the failed job replays the workflow file at the
tagged commit, so it only helps when the fix is not in the workflow.

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
(`Qavren.Edge.Maui` as `net10.0-android`, everything else as `net10.0`). The
build runs with `--warningsAsErrors`, so an unresolved link in any README that
is part of the site fails the PR: link to documentation files by relative path
and to source or samples by absolute GitHub URL.

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
