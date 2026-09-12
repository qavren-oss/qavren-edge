# Sub-project 5, part 1: the release path to `v0.1.0-preview.1`

Date: 2026-09-12. Status: approved design.

Sub-project 5 ("Docs + 1.0") splits into three pieces that can ship
independently: the release path, the docs site, and benchmarks. This spec
covers the first. The docs site and benchmarks get their own specs; `v1.0.0`
is tagged after both exist.

## 1. Goal

Publish every package in the suite to nuget.org as `0.1.0-preview.1` through
the existing `release.yml`, with the metadata a public package needs (icon,
README, tags), and prove the pipeline with a dry run before the tag is pushed.
Publishing a package with a licence expression and an icon is the
precondition for reserving the `Qavren.` ID prefix.

## 2. What exists

- `release.yml` runs on `v*` tags and on `workflow_dispatch`: builds natives
  through `native.yml`, packs the solution, zips the natives into three
  archives, generates an SPDX SBOM, writes `SHA256SUMS.txt`, pushes every
  `.nupkg` to nuget.org, and creates a GitHub release with generated notes.
  Nothing in it is conditional on the trigger, so a `workflow_dispatch` run
  would push whatever MinVer computes (`0.0.0-alpha.0.N` today, there are no
  tags yet).
- `Directory.Build.targets` gives every packable project MIT, SourceLink,
  `snupkg` symbols, package validation, authors and the `v` MinVer prefix.
  `PackageTags` is the sub-project 1 set (`sqlite;sqlite-vec;vector;maui;embedded`)
  for all 17 packages.
- Only `Qavren.Edge` (the metapackage) packs a README. `Qavren.Edge.Ingestion.Pdf`
  has a README beside it that is not packed. No package has an icon.
- `ci.yml`'s Windows test lane already packs the solution and asserts that
  every native RID reached the two native packages.
- No repository secrets exist. Branch protection on `main` requires `ci-gate`
  and `natives / native-gate`; tags are not restricted.

## 3. Design

### 3.1 Icon

`icon.png`, 128x128, at the repository root, rendered from the Qavren hex-Q
mark (`qavren/app/icon.svg` in the Qavren site repository: ink hexagon, cream
circular counter, azure diamond). `Directory.Build.targets` adds, for packable
projects, `PackageIcon=icon.png` and a `None` item that packs the root file at
the package root. One file, no per-project change.

### 3.2 Package READMEs

Each of the 16 packages without a README gets `README.md` beside its
`.csproj`, at most about 40 lines:

1. One paragraph: what the package is and which sub-project owns it.
2. `dotnet add package <id>`.
3. One snippet showing the package's entry point (the builder call, the
   client, the store), lifted from the owning sub-project README so the two
   never disagree.
4. Links to the sub-project README and, where one exists, the ADR folder.

`Directory.Build.targets` sets `PackageReadmeFile=README.md` and the matching
`None` item whenever `README.md` exists beside the project. The metapackage's
explicit wiring is removed in favour of the shared rule; `Ingestion.Pdf`'s
existing README is packed by it.

### 3.3 Tags

A `Directory.Build.props` in each sub-project folder (`foundation/`,
`embeddings/`, `ingestion/`, `chat/`) imports the root props and appends
sub-project-specific `PackageTags`:

| Folder | Appended tags |
|---|---|
| `foundation/` | `sqlcipher;hosting;migrations` |
| `embeddings/` | `onnx;onnxruntime;embeddings;vector-search;semantic-search;fts5` |
| `ingestion/` | `rag;ingestion;chunking;pdf;docx` |
| `chat/` | `rag;chat;llm;genai;onnxruntime-genai;citations` |

The root tags stay on every package.

### 3.4 `release.yml`

- The NuGet push and the GitHub release steps get
  `if: startsWith(github.ref, 'refs/tags/v')`.
- On `workflow_dispatch` the job still packs, zips natives, generates the SBOM
  and checksums, and uploads `artifacts/` as a workflow artifact named
  `release-dry-run`. That is the pipeline proof before a tag exists.
- The GitHub release is `prerelease: ${{ contains(github.ref_name, '-') }}`.
- The "at least 8 files" floor becomes exact counts: 17 `.nupkg`, 17 `.snupkg`
  (the native-only packages emit one too, measured locally), 3 zips and the
  SBOM.

### 3.5 Verification in CI

The Windows pack lane's assertion step grows two checks over every `.nupkg`
in `artifacts/packages`: the archive contains `icon.png` and `README.md` at
its root. `assert-workflows.py` gains a rule that the two release-only steps
carry the tag guard and that the dry-run upload exists.

### 3.6 Local verification

`dotnet pack QavrenEdge.slnx -c Release -o artifacts/packages` on Windows,
then the same archive inspection the CI step runs. A `workflow_dispatch` of
`release.yml` on `main` after the PR merges proves the whole path, natives
included, and leaves `release-dry-run` to download and inspect.

## 4. Owner actions

In order, all Steve's:

1. `gh secret set NUGET_API_KEY --repo qavren-oss/qavren-edge` with a key
   scoped to push new packages and versions (glob `Qavren.*`).
2. After the dry run is green: `git tag v0.1.0-preview.1 <sha>` on `main`
   and `git push origin v0.1.0-preview.1`.
3. After nuget.org lists the packages: the `Qavren.` prefix reservation email
   to `account@nuget.org` (owner display name `Qavren`, admin `stevenfackley`).

## 5. Out of scope

Changelog tooling, the docs site, benchmarks, a package validation baseline,
and `v1.0.0`. Root `README.md` status row 5 changes only when all three
pieces have shipped.

## 6. Risks

- MinVer computes the version from the tag on the checked-out commit; a tag
  on a commit that is not on `main` publishes that commit. Tag on `main`.
- `dotnet nuget push --skip-duplicate` makes a re-run of the tag safe, but a
  package that was pushed and then found wrong cannot be replaced at the same
  version; only unlisted. The dry run exists to make that unlikely.
- The SBOM action pins `@v0`; a breaking major would surface in the dry run.
