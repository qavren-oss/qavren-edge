# SP5 Release Path Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every package in the suite packs with an icon, a README and sub-project tags, `release.yml` publishes only on a `v*` tag and dry-runs on `workflow_dispatch`, and CI proves the package metadata on every PR, so that `v0.1.0-preview.1` can be tagged.

**Architecture:** All metadata wiring lives in the root `Directory.Build.targets` (icon, README) and one `Directory.Build.props` per sub-project folder (tags), so no `.csproj` changes. A PowerShell script under `foundation/tools/ci-checks/` inspects every `.nupkg` and is run by the existing Windows pack lane; `assert-workflows.py` pins the workflow contract as it already does for everything else.

**Tech Stack:** MSBuild, NuGet pack, MinVer 8, PowerShell 7, Python 3 (PyYAML, Pillow), GitHub Actions.

Spec: `docs/superpowers/specs/2026-09-12-sp5-release-path-design.md`. Branch: `feat/sp5-release-path` (already exists, spec committed on it). Work in `C:\Users\steve\projects\qavren-edge`.

**Repo rules that apply to every task:** Conventional Commits; never commit to `main`; no `Co-Authored-By`; every commit message ends with the line `Claude-Session: https://claude.ai/code/session_015anNxsJU4sQWdMeZFsAKUF`. Write commit messages to a file and use `git commit -F`. `dotnet pack` output goes to `artifacts/packages` (gitignored). Restore first if `dotnet format` is ever run: `dotnet restore QavrenEdge.slnx`.

**Verification helper used by several tasks** (run from the repo root, after a pack):

```powershell
# Lists the root entries of one package. $id is the package id.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$nupkg = Get-ChildItem "artifacts/packages/$id.*.nupkg" | Where-Object Name -notlike '*.symbols.nupkg' | Select-Object -First 1
$zip = [IO.Compression.ZipFile]::OpenRead($nupkg.FullName); $zip.Entries.FullName | Where-Object { $_ -notlike '*/*' }; $zip.Dispose()
```

---

## File structure

| Path | Responsibility |
|---|---|
| `icon.svg` (root) | The brand mark, source of truth for the icon |
| `icon.png` (root) | 128x128 render, packed into every package |
| `foundation/tools/icon/render-icon.py` | Reproducible SVG-shapes-to-PNG render (Pillow, supersampled) |
| `Directory.Build.targets` | Adds `PackageIcon`, `PackageReadmeFile`, the two `None` items, and composes `PackageTags` from a per-folder property |
| `foundation/Directory.Build.props`, `embeddings/…`, `ingestion/…`, `chat/…` | Import the root props; set `QedgeSubProjectTags` |
| `<package dir>/README.md` x16 | Packed package READMEs |
| `foundation/src/Qavren.Edge/Qavren.Edge.csproj` | Loses its explicit README wiring (now central) |
| `foundation/tools/ci-checks/assert-packages.ps1` | Asserts icon, README, tags in every `.nupkg` |
| `.github/workflows/ci.yml` | Runs `assert-packages.ps1` in the Windows pack lane |
| `.github/workflows/release.yml` | Tag guards, prerelease flag, dry-run artifact, exact checksum counts |
| `foundation/tools/ci-checks/assert-workflows.py` | New rules for the above |
| `README.md` (root) | Bootstrap checklist rows updated |

---

### Task 1: The icon

**Files:**
- Create: `icon.svg`
- Create: `foundation/tools/icon/render-icon.py`
- Create: `icon.png`
- Modify: `Directory.Build.targets`

- [ ] **Step 1: Add the SVG source**

Create `icon.svg` at the repo root with exactly this content (the Qavren hex-Q mark from the Qavren site repository, `app/icon.svg`, comment shortened):

```xml
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32" role="img" aria-label="Qavren">
  <title>Qavren</title>
  <!-- The Qavren hex-Q mark: ink hexagon, cream circular counter, azure diamond. Source of
       truth for icon.png (rendered by foundation/tools/icon/render-icon.py). -->
  <polygon points="8,0 24,0 32,16 24,32 8,32 0,16" fill="#1b2330"/>
  <circle cx="16" cy="14" r="7" fill="none" stroke="#f5f4ef" stroke-width="2.5"/>
  <polygon points="9,22 13,26 9,30 5,26" fill="#3b7bd6"/>
</svg>
```

- [ ] **Step 2: Write the renderer**

Create `foundation/tools/icon/render-icon.py`. It draws the three shapes above at 128 px with 8x supersampling on a transparent canvas, so the PNG matches the SVG without a browser or an SVG library:

```python
"""Render icon.png (128x128, transparent) from the shapes in icon.svg.
Run from the repo root: python foundation/tools/icon/render-icon.py"""
from PIL import Image, ImageDraw

SIZE, SS = 128, 8            # output size, supersample factor
S = SIZE * SS / 32           # svg units -> supersampled pixels

def pts(seq):
    return [(x * S, y * S) for x, y in seq]

img = Image.new("RGBA", (SIZE * SS, SIZE * SS), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.polygon(pts([(8, 0), (24, 0), (32, 16), (24, 32), (8, 32), (0, 16)]), fill="#1b2330")
# stroke-width 2.5 centred on r=7: outer radius 8.25, inner radius 5.75
cx, cy = 16 * S, 14 * S
d.ellipse([cx - 8.25 * S, cy - 8.25 * S, cx + 8.25 * S, cy + 8.25 * S], fill="#f5f4ef")
d.ellipse([cx - 5.75 * S, cy - 5.75 * S, cx + 5.75 * S, cy + 5.75 * S], fill="#1b2330")
d.polygon(pts([(9, 22), (13, 26), (9, 30), (5, 26)]), fill="#3b7bd6")
img.resize((SIZE, SIZE), Image.LANCZOS).save("icon.png", optimize=True)
print("wrote icon.png 128x128")
```

- [ ] **Step 3: Render it**

Run: `python -c "import PIL; print(PIL.__version__)"`. If that fails: `uv pip install pillow` (or `pip install pillow`). Then from the repo root:

```
python foundation/tools/icon/render-icon.py
```

Expected: `wrote icon.png 128x128`. Verify size and transparency:

```powershell
Add-Type -AssemblyName System.Drawing
$i = [System.Drawing.Image]::FromFile((Resolve-Path icon.png)); "$($i.Width)x$($i.Height) $($i.PixelFormat)"; $i.Dispose()
```

Expected: `128x128 Format32bppArgb`. Open `icon.png` (Read tool) and confirm: dark hexagon, cream ring, blue diamond at lower left, transparent corners.

- [ ] **Step 4: Pack the icon into every package**

In `Directory.Build.targets`, inside the existing `<PropertyGroup Condition="'$(IsPackable)' == 'true'">`, add after `<RepositoryType>git</RepositoryType>`:

```xml
    <PackageIcon>icon.png</PackageIcon>
```

and add a new item group after that property group (before `</Project>`):

```xml
  <ItemGroup Condition="'$(IsPackable)' == 'true'">
    <!-- One icon for the suite, packed at the package root. Visible=false keeps it out of IDE trees. -->
    <None Include="$(MSBuildThisFileDirectory)icon.png" Pack="true" PackagePath="\" Visible="false" />
  </ItemGroup>
```

- [ ] **Step 5: Verify on one package**

```
dotnet pack foundation/src/Qavren.Edge.Core/Qavren.Edge.Core.csproj -c Release -o artifacts/packages
```

Then the verification helper with `$id = 'Qavren.Edge.Core'`. Expected root entries include `icon.png`. Also `dotnet build QavrenEdge.slnx -c Release` must still report 0 errors (the `None` item must not break non-packable projects; it is conditioned on `IsPackable`).

- [ ] **Step 6: Commit**

```
git add icon.svg icon.png foundation/tools/icon/render-icon.py Directory.Build.targets
git commit -F <msg>   # feat(pack): the Qavren hex-Q icon, rendered reproducibly, packed into every package
```

---

### Task 2: Central README packing

**Files:**
- Modify: `Directory.Build.targets`
- Modify: `foundation/src/Qavren.Edge/Qavren.Edge.csproj`

- [ ] **Step 1: Wire `PackageReadmeFile` centrally**

In `Directory.Build.targets`, in the same packable property group, add after `<PackageIcon>icon.png</PackageIcon>`:

```xml
    <PackageReadmeFile Condition="Exists('$(MSBuildProjectDirectory)\README.md')">README.md</PackageReadmeFile>
```

and in the item group created in Task 1 add:

```xml
    <None Include="$(MSBuildProjectDirectory)\README.md" Pack="true" PackagePath="\" Visible="false" Condition="Exists('$(MSBuildProjectDirectory)\README.md')" />
```

- [ ] **Step 2: Remove the metapackage's explicit wiring**

In `foundation/src/Qavren.Edge/Qavren.Edge.csproj` delete the line `<PackageReadmeFile>README.md</PackageReadmeFile>` and the whole item group:

```xml
  <ItemGroup>
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
```

Leaving them would pack `README.md` twice (NU5118).

- [ ] **Step 3: Verify the two packages that already have a README**

```
dotnet pack foundation/src/Qavren.Edge/Qavren.Edge.csproj -c Release -o artifacts/packages
dotnet pack ingestion/src/Qavren.Edge.Ingestion.Pdf/Qavren.Edge.Ingestion.Pdf.csproj -c Release -o artifacts/packages
```

Expected: no NU5118, no NU5039 ("readme file not found"). The helper for `Qavren.Edge` and `Qavren.Edge.Ingestion.Pdf` lists `README.md` and `icon.png`.

- [ ] **Step 4: Commit**

```
git add Directory.Build.targets foundation/src/Qavren.Edge/Qavren.Edge.csproj
git commit -F <msg>   # build(pack): pack README.md from beside any packable project; drop the metapackage's own wiring
```

---

### Task 3: Foundation package READMEs

**Files:** create `README.md` in each of `foundation/src/Qavren.Edge.Core`, `Qavren.Edge.Maui`, `Qavren.Edge.Sqlite`, `Qavren.Edge.Sqlite.Provider`, `Qavren.Edge.Sqlite.Native`, `Qavren.Edge.Sqlite.Native.Cipher`.

- [ ] **Step 1: `foundation/src/Qavren.Edge.Core/README.md`**

````markdown
# Qavren.Edge.Core

The hosting core of the Qavren.Edge suite (sub-project 1): `AddQavrenEdge()`, the startup host
that runs every registered `IEdgeStartupTask` once and records a single startup fault, the
lifecycle hub, app paths, diagnostics and the `EdgeErrorCode` catalogue. Plain
`Microsoft.Extensions.*` wiring, no framework. Every other Qavren.Edge package registers into
the builder this package defines.

```
dotnet add package Qavren.Edge.Core
```

```csharp
services.AddQavrenEdge(edge => edge
    // providers register here: .AddSqlite(...), .AddOnnxEmbeddings(...), .AddOnnxChat(...)
);
```

You rarely reference this package alone: `Qavren.Edge` (SQLite + native library) or
`Qavren.Edge.Maui` (MAUI lifecycle and paths) pull it in. Error codes 1001-4999 are documented in
[foundation/docs/errors.md](https://github.com/qavren-oss/qavren-edge/blob/main/foundation/docs/errors.md).

Sub-project README: [foundation/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/foundation/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
````

- [ ] **Step 2: `foundation/src/Qavren.Edge.Maui/README.md`**

````markdown
# Qavren.Edge.Maui

The .NET MAUI bridge for Qavren.Edge: `UseQavrenEdge()` on `MauiAppBuilder`, the platform
lifecycle bridge (Android, iOS, Mac Catalyst, Windows) that feeds the suite's suspend/resume and
memory-pressure signals, and `FileSystem`-backed app paths so databases and models land in the
right per-platform directory.

```
dotnet add package Qavren.Edge.Maui
```

```csharp
// MauiProgram.cs
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db"));
```

`UseQavrenEdge` is `AddQavrenEdge` plus the MAUI wiring; use one or the other, not both. MAUI
has no hosted-service loop, so background work is triggered from the lifecycle events this
package raises, not from `IHostedService`.

Sub-project README: [foundation/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/foundation/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
````

- [ ] **Step 3: `foundation/src/Qavren.Edge.Sqlite/README.md`**

````markdown
# Qavren.Edge.Sqlite

SQLite for Qavren.Edge: `AddSqlite()`, `IEdgeDatabase`, pragma-tuned connections, versioned
migrations, and helpers for `sqlite-vec` KNN queries and FTS5. Microsoft.Data.Sqlite, EF Core
Sqlite, sqlite-net-pcl and Dapper all work unchanged on the connection this package opens.
Pair it with one native package: `Qavren.Edge.Sqlite.Native` or `Qavren.Edge.Sqlite.Native.Cipher`.

```
dotnet add package Qavren.Edge.Sqlite
dotnet add package Qavren.Edge.Sqlite.Native
```

```csharp
services.AddQavrenEdge(edge => edge
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddMigration<M001_CreateNotes>()
    .UseSqliteNative());

await using var conn = await db.OpenConnectionAsync(ct);   // db is the injected IEdgeDatabase
var hits = await Knn.QueryAsync(conn, "notes_vec", query, k: 10, cancellationToken: ct);
```

Migrations are registered explicitly and run at startup; a failed migration is the app's single
startup fault and every later `OpenConnectionAsync` rethrows it with its remediation. No
down-migrations.

Sub-project README: [foundation/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/foundation/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
````

- [ ] **Step 4: `foundation/src/Qavren.Edge.Sqlite.Provider/README.md`**

````markdown
# Qavren.Edge.Sqlite.Provider

The generated SQLitePCLRaw provider `SQLite3Provider_qedge : ISQLite3Provider` that binds the
suite's own `qedge_sqlite3` / `qedge_sqlcipher` native library. A drift test keeps it in step
with the SQLitePCLRaw version the suite pins.

```
dotnet add package Qavren.Edge.Sqlite.Native   # pulls this package in
```

You do not reference this package directly and you do not call it: `Qavren.Edge.Sqlite.Native`
and `Qavren.Edge.Sqlite.Native.Cipher` depend on it and `UseSqliteNative()` /
`UseSqliteNativeCipher()` install it as the SQLitePCLRaw provider at startup.

Sub-project README: [foundation/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/foundation/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
````

- [ ] **Step 5: `foundation/src/Qavren.Edge.Sqlite.Native/README.md`**

````markdown
# Qavren.Edge.Sqlite.Native

The suite's own SQLite native library with `sqlite-vec` compiled in, for every supported RID:
`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `maccatalyst-x64`,
`maccatalyst-arm64`, `android-arm64`, `android-x64`, `android-arm` (16 KB page aligned), plus the
iOS and Mac Catalyst xcframework. Plain build, no encryption.

```
dotnet add package Qavren.Edge.Sqlite.Native
```

```csharp
services.AddQavrenEdge(edge => edge
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .UseSqliteNative());
```

Which library loaded, from where, and which SQLite and sqlite-vec versions it carries are all
inspectable through the suite's diagnostics. Referencing this package and
`Qavren.Edge.Sqlite.Native.Cipher` in one app is a configuration error caught at startup.

Sub-project README: [foundation/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/foundation/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
````

- [ ] **Step 6: `foundation/src/Qavren.Edge.Sqlite.Native.Cipher/README.md`**

````markdown
# Qavren.Edge.Sqlite.Native.Cipher

The encrypted variant of `Qavren.Edge.Sqlite.Native`: SQLCipher with libtomcrypt and `sqlite-vec`
compiled in, for the same RIDs and the same xcframework. Use it instead of the plain native
package, never alongside it.

```
dotnet add package Qavren.Edge.Sqlite.Native.Cipher
```

```csharp
services.AddQavrenEdge(edge => edge
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .UseSqliteNativeCipher());
```

The key is supplied through the SQLite options at registration; the pragmas the suite applies on
every open connection include the SQLCipher ones. Referencing both native packages in one app is
a configuration error caught at startup.

Sub-project README: [foundation/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/foundation/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
````

- [ ] **Step 7: Verify and commit**

```
dotnet pack QavrenEdge.slnx -c Release -o artifacts/packages
```

Expected: 0 errors. Run the helper for `Qavren.Edge.Sqlite.Native.Cipher`; root entries include `README.md`. Then:

```
git add foundation/src/*/README.md
git commit -F <msg>   # docs(pack): READMEs for the six foundation packages
```

---

### Task 4: Embeddings package READMEs

**Files:** create `README.md` in `embeddings/src/Qavren.Edge.Onnx`, `Qavren.Edge.Embeddings.Onnx`, `Qavren.Edge.VectorData`.

- [ ] **Step 1: `embeddings/src/Qavren.Edge.Onnx/README.md`**

````markdown
# Qavren.Edge.Onnx

ONNX Runtime hosting for the Qavren.Edge suite (sub-project 2): `AddOnnx()`, the session
factory, the execution-provider policy per platform, consent-gated model provisioning with
pinned SHA-256 digests, `UseModelPaths()`, and the resource monitor (`IEdgeResourceMonitor`) the
embedding, ingestion and chat packages pace themselves against.

```
dotnet add package Qavren.Edge.Onnx
```

```csharp
services.AddQavrenEdge(edge => edge
    .AddOnnx()
    .UseModelPaths(p => p.ModelsDirectory = Path.Combine(FileSystem.AppDataDirectory, "models")));
```

`AddOnnxEmbeddings()` and `AddOnnxChat()` call `AddOnnx()` for you; it is idempotent. Nothing
downloads implicitly: a provisioner's `Plan()` reports byte counts and licences before
`ProvisionAsync` moves a byte. Error codes 5000-5299.

Sub-project README: [embeddings/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/embeddings/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
````

- [ ] **Step 2: `embeddings/src/Qavren.Edge.Embeddings.Onnx/README.md`**

````markdown
# Qavren.Edge.Embeddings.Onnx

On-device text embeddings as a `Microsoft.Extensions.AI` `IEmbeddingGenerator<string, Embedding<float>>`,
over ONNX Runtime and `Microsoft.ML.Tokenizers`. Presets carry pinned model hashes;
`EmbeddingPresets.MiniLmL6V2Int8` is the 384-dimension default.

```
dotnet add package Qavren.Edge.Embeddings.Onnx
```

```csharp
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddOnnxEmbeddings(o => o.Preset = EmbeddingPresets.MiniLmL6V2Int8));

var embed = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
var vector = (await embed.GenerateAsync(["roof leak above the kitchen"]))[0].Vector;
```

`Qavren.Edge.VectorData` resolves this generator from the container and fills any record
property whose vector source is a `string`, so most apps never call it directly.

Sub-project README: [embeddings/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/embeddings/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
````

- [ ] **Step 3: `embeddings/src/Qavren.Edge.VectorData/README.md`**

````markdown
# Qavren.Edge.VectorData

A `Microsoft.Extensions.VectorData` `VectorStore` over the suite's SQLite: `sqlite-vec` (`vec0`)
for vectors, FTS5 for keywords, hybrid search fused with reciprocal rank fusion, and LINQ filters
pushed into SQL. Collections are declared as versioned migrations, so the schema is explicit.

```
dotnet add package Qavren.Edge.VectorData
```

```csharp
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddOnnxEmbeddings(o => o.Preset = EmbeddingPresets.MiniLmL6V2Int8)
    .AddVectorStore()
    .AddVectorCollectionMigration<string, Note>(version: 1, "notes"));

public sealed class Note
{
    [VectorStoreKey] public string Key { get; set; } = "";
    [VectorStoreData(IsFullTextIndexed = true)] public string Title { get; set; } = "";
    [VectorStoreData(IsFullTextIndexed = true)] public string Body { get; set; } = "";
    [VectorStoreVector(384, DistanceFunction = DistanceFunction.CosineDistance)]
    public string? Embedding => Body;          // string source: the registered generator fills it
}

var notes = store.GetCollection<string, Note>("notes");
await notes.UpsertAsync(new Note { Key = "n1", Title = "Roof leak", Body = "…" });
await foreach (var hit in notes.HybridSearchAsync("water damage", ["roof", "leak"], top: 10))
    Console.WriteLine($"{hit.Record.Title} {hit.Score:F4}");
```

Distance functions: cosine, Euclidean and Manhattan (what `vec0` exposes). Collection-valued
data properties and bit/int8 vectors are out of scope for v1.

Sub-project README: [embeddings/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/embeddings/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
````

- [ ] **Step 4: Verify and commit**

```
dotnet pack QavrenEdge.slnx -c Release -o artifacts/packages
git add embeddings/src/*/README.md
git commit -F <msg>   # docs(pack): READMEs for the three embeddings packages
```

---

### Task 5: Ingestion package READMEs

**Files:** create `README.md` in `ingestion/src/Qavren.Edge.Ingestion`, `Qavren.Edge.Ingestion.OpenXml`, `Qavren.Edge.Ingestion.Onnx`, `Qavren.Edge.Ingestion.DataIngestion`. (`Qavren.Edge.Ingestion.Pdf` already has one.)

- [ ] **Step 1: `ingestion/src/Qavren.Edge.Ingestion/README.md`**

````markdown
# Qavren.Edge.Ingestion

The document ingestion pipeline of the Qavren.Edge suite (sub-project 3): `AddIngestion()`, the
document model, plain-text and Markdown extractors, three chunkers (plain, heading-scoped,
token window), a token budget, xxHash128 content hashing for incremental re-indexing, a
cancellable and checkpointed runner, and the chunk writer into `Qavren.Edge.VectorData`.

```
dotnet add package Qavren.Edge.Ingestion
```

```csharp
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddOnnxEmbeddings()
    .AddVectorStore()
    .AddIngestion(migrationVersion: 10));   // claims versions 10 AND 11

var pipeline = services.GetRequiredService<IIngestionPipeline>();
var result = await pipeline.RunAsync(
    IngestionSource.Folder(@"C:\docs", "*.md", recursive: true),
    options: new IngestionRunOptions { Budget = IngestionBudget.Background });
Console.WriteLine($"{result.Outcome}: +{result.ChunksAdded} -{result.ChunksRemoved}");
```

Satellites: `.Pdf` (PdfPig, Apache-2.0, opt-in), `.OpenXml` (DOCX), `.Onnx` (the real tokenizer
and a resource-aware throttle), `.DataIngestion` (the Microsoft.Extensions.DataIngestion shim).
Error codes 6000-6299.

Sub-project README: [ingestion/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/ingestion/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
````

- [ ] **Step 2: `ingestion/src/Qavren.Edge.Ingestion.OpenXml/README.md`**

````markdown
# Qavren.Edge.Ingestion.OpenXml

DOCX text extraction for `Qavren.Edge.Ingestion`, over `DocumentFormat.OpenXml`. MIT like the
core; it is a separate package for size (the dependency is about 14 MB), so a Markdown-only app
never carries it.

```
dotnet add package Qavren.Edge.Ingestion.OpenXml
```

```csharp
builder.UseQavrenEdge(edge => edge
    // ... AddSqlite, AddOnnxEmbeddings, AddVectorStore, AddIngestion ...
    .AddDocxExtractor());
```

Headings become the chunk breadcrumb the same way Markdown headings do.

Sub-project README: [ingestion/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/ingestion/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
````

- [ ] **Step 3: `ingestion/src/Qavren.Edge.Ingestion.Onnx/README.md`**

````markdown
# Qavren.Edge.Ingestion.Onnx

The ONNX satellite of `Qavren.Edge.Ingestion`: `AddOnnxIngestion()` wires the embedding
preset's real tokenizer in as the chunk tokenizer (so chunk budgets are counted in the tokens
the model will see) and a throttle over `Qavren.Edge.Onnx`'s resource monitor (so a background
run yields under memory pressure or thermal load).

```
dotnet add package Qavren.Edge.Ingestion.Onnx
```

```csharp
builder.UseQavrenEdge(edge => edge
    .AddOnnxEmbeddings()
    .AddVectorStore()
    .AddIngestion(migrationVersion: 10)
    .AddOnnxIngestion());
```

Without it, ingestion counts tokens with a fast approximation and runs unthrottled.

Sub-project README: [ingestion/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/ingestion/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
````

- [ ] **Step 4: `ingestion/src/Qavren.Edge.Ingestion.DataIngestion/README.md`**

````markdown
# Qavren.Edge.Ingestion.DataIngestion

The `Microsoft.Extensions.DataIngestion` (MEDI) shim for `Qavren.Edge.Ingestion`, in both
directions: `EdgeDocumentConverter.ToMedi` / `FromMedi` between the suite's `ExtractedDocument`
and MEDI's `IngestionDocument`, `EdgeChunkerMediAdapter` (a MEDI `IngestionChunker<string>` over
the suite's chunkers), `EdgeVectorStoreMediWriter` (a MEDI chunk writer into the suite's vector
store) and `MediReaderAdapter` (a MEDI reader as an `IDocumentExtractor`). Built against the
zero-dependency Abstractions package only. **Prerelease only** while MEDI is.

```
dotnet add package Qavren.Edge.Ingestion.DataIngestion --prerelease
```

```csharp
IngestionDocument medi = EdgeDocumentConverter.ToMedi(extracted);
ExtractedDocument back  = EdgeDocumentConverter.FromMedi(medi);
```

Why a shim rather than a dependency on MEDI's implementation package is ADR 0011.

Sub-project README: [ingestion/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/ingestion/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
````

- [ ] **Step 5: Verify and commit**

```
dotnet pack QavrenEdge.slnx -c Release -o artifacts/packages
git add ingestion/src/*/README.md
git commit -F <msg>   # docs(pack): READMEs for the four ingestion packages that lacked one
```

---

### Task 6: Chat package READMEs

**Files:** create `README.md` in `chat/src/Qavren.Edge.Chat.Onnx`, `chat/src/Qavren.Edge.Rag`.

- [ ] **Step 1: `chat/src/Qavren.Edge.Chat.Onnx/README.md`**

````markdown
# Qavren.Edge.Chat.Onnx

An on-device `Microsoft.Extensions.AI` `IChatClient` over ONNX Runtime GenAI (sub-project 4).
Presets with pinned SHA-256 digests (`ChatPresets.Llama32_1BInstructInt4`,
`ChatPresets.Qwen3_600MInt4`), consent-gated provisioning, a KV-cache-aware context budget
with a ladder for small phones, thermally paced streaming, cooperative termination under memory
pressure, a conversation cache, and history reduction with pinning. Error codes 7000-7299.

```
dotnet add package Qavren.Edge.Chat.Onnx
```

```csharp
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddOnnxChat(ChatPresets.Llama32_1BInstructInt4));

var chat = sp.GetRequiredService<IChatClient>();
await foreach (var update in chat.GetStreamingResponseAsync("summarise my week"))
    Console.Write(update.Text);
```

There is no default preset and nothing downloads implicitly. Turns serialise behind one gate per
model (the GenAI C API is not thread safe); a fifth queued turn is refused with `ChatBusy`. On iOS,
a multi-GB model needs two entitlements in the consuming app:
`com.apple.developer.kernel.increased-memory-limit` and
`com.apple.developer.kernel.extended-virtual-addressing`. No Windows TFM: GenAI ships no Windows
platform asset (the `net10.0` build covers desktop).

Sub-project README: [chat/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/chat/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
````

- [ ] **Step 2: `chat/src/Qavren.Edge.Rag/README.md`**

````markdown
# Qavren.Edge.Rag

The retrieval-augmented generation recipe of the Qavren.Edge suite, over any
`Microsoft.Extensions.AI` chat client and any `Microsoft.Extensions.VectorData` store:
`IEdgeRetriever`, `VectorStoreRetriever` (keyed collections, hybrid search opt-in), the
`UseRag()` pipeline step that puts numbered context under a token budget, `[n]` citations
returned as `CitationAnnotation` with text-span regions, and `ExtractiveChatClient` as the
no-LLM floor. No ONNX dependency; pairs with `Qavren.Edge.Chat.Onnx` or any other client.

```
dotnet add package Qavren.Edge.Rag
```

```csharp
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddOnnxEmbeddings(o => o.Preset = EmbeddingPresets.MiniLmL6V2Int8)
    .AddVectorStore()
    .AddVectorCollectionMigration<string, Note>(version: 1, "notes")
    .AddOnnxChat(ChatPresets.Llama32_1BInstructInt4, pipeline: chat => chat.UseRag())
    .AddVectorStoreRetriever<string, Note>("notes",
        n => new RagSource(n.Key, n.Body) { Title = n.Title, Uri = n.Url }));

var response = await chat.GetResponseAsync("what does the warranty cover?");
foreach (var citation in response.Messages[^1].Contents
                                 .SelectMany(c => c.Annotations ?? [])
                                 .OfType<CitationAnnotation>())
    Console.WriteLine($"[{citation.Title}] {citation.Url}");
```

When streaming, citations arrive once on the final metadata update and their spans index the
accumulated answer.

Sub-project README: [chat/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/chat/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
````

- [ ] **Step 3: Verify and commit**

```
dotnet pack QavrenEdge.slnx -c Release -o artifacts/packages
git add chat/src/*/README.md
git commit -F <msg>   # docs(pack): READMEs for the two chat packages
```

---

### Task 7: Sub-project tags

**Files:**
- Modify: `Directory.Build.targets`
- Create: `foundation/Directory.Build.props`, `embeddings/Directory.Build.props`, `ingestion/Directory.Build.props`, `chat/Directory.Build.props`

- [ ] **Step 1: Compose tags from a per-folder property**

In `Directory.Build.targets` replace the line

```xml
    <PackageTags>sqlite;sqlite-vec;vector;maui;embedded</PackageTags>
```

with

```xml
    <!-- Suite-wide tags plus the sub-project's own, set by the Directory.Build.props of the
         folder the project lives in. Composed here (targets) because that props file has
         already been evaluated by now. -->
    <PackageTags>sqlite;sqlite-vec;vector;maui;embedded</PackageTags>
    <PackageTags Condition="'$(QedgeSubProjectTags)' != ''">$(PackageTags);$(QedgeSubProjectTags)</PackageTags>
```

- [ ] **Step 2: The four props files**

Each file imports the root props first, or the whole root configuration (nullable, warnings as errors, `IsPackable=false`, `TestingPlatformDotnetTestSupport`) would silently stop applying to that folder.

`foundation/Directory.Build.props`:

```xml
<Project>
  <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
  <PropertyGroup>
    <QedgeSubProjectTags>sqlcipher;hosting;migrations</QedgeSubProjectTags>
  </PropertyGroup>
</Project>
```

`embeddings/Directory.Build.props`:

```xml
<Project>
  <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
  <PropertyGroup>
    <QedgeSubProjectTags>onnx;onnxruntime;embeddings;vector-search;semantic-search;fts5</QedgeSubProjectTags>
  </PropertyGroup>
</Project>
```

`ingestion/Directory.Build.props`:

```xml
<Project>
  <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
  <PropertyGroup>
    <QedgeSubProjectTags>rag;ingestion;chunking;pdf;docx</QedgeSubProjectTags>
  </PropertyGroup>
</Project>
```

`chat/Directory.Build.props`:

```xml
<Project>
  <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
  <PropertyGroup>
    <QedgeSubProjectTags>rag;chat;llm;genai;onnxruntime-genai;citations</QedgeSubProjectTags>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Verify the root configuration still reaches every folder**

```
dotnet build QavrenEdge.slnx -c Release
```

Expected: 0 errors and the same warning count as before the change (the `CS1668` LIB-path noise). If `TreatWarningsAsErrors` stopped applying, nullable warnings would appear as warnings instead of errors; if `IsPackable` stopped defaulting to false, `dotnet pack` below would emit packages for test projects. Then:

```
dotnet pack QavrenEdge.slnx -c Release -o artifacts/packages
```

Expected: exactly 17 `.nupkg` (not counting `.snupkg`): `(Get-ChildItem artifacts/packages/*.nupkg).Count` is `17`. Read the tags out of one package per folder:

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($id in 'Qavren.Edge.Core','Qavren.Edge.VectorData','Qavren.Edge.Ingestion','Qavren.Edge.Rag') {
  $nupkg = Get-ChildItem "artifacts/packages/$id.*.nupkg" | Select-Object -First 1
  $zip = [IO.Compression.ZipFile]::OpenRead($nupkg.FullName)
  $e = $zip.Entries | Where-Object FullName -like '*.nuspec'
  $r = New-Object IO.StreamReader($e.Open()); $x = [xml]$r.ReadToEnd(); $r.Dispose(); $zip.Dispose()
  "$id -> $($x.package.metadata.tags)"
}
```

Expected: each line ends with that folder's `QedgeSubProjectTags` value, after `sqlite;sqlite-vec;vector;maui;embedded`.

- [ ] **Step 4: Commit**

```
git add Directory.Build.targets foundation/Directory.Build.props embeddings/Directory.Build.props ingestion/Directory.Build.props chat/Directory.Build.props
git commit -F <msg>   # build(pack): sub-project package tags via a per-folder Directory.Build.props
```

---

### Task 8: Package metadata assertion in CI

**Files:**
- Create: `foundation/tools/ci-checks/assert-packages.ps1`
- Modify: `.github/workflows/ci.yml` (Windows pack lane, after "Assert every native RID reached the package")
- Modify: `foundation/tools/ci-checks/assert-workflows.py`

- [ ] **Step 1: Write the assertion script**

`foundation/tools/ci-checks/assert-packages.ps1`:

```powershell
<#
.SYNOPSIS
Asserts every packed .nupkg carries the suite's package metadata: icon.png and README.md at the
package root, PackageIcon/PackageReadmeFile pointing at them, and the sub-project's tags.
Run after `dotnet pack QavrenEdge.slnx -c Release -o artifacts/packages`, from the repo root.
#>
param([string]$PackagesDir = 'artifacts/packages', [int]$ExpectedCount = 17)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

# One tag that only the sub-project's Directory.Build.props supplies, keyed by id prefix.
# Order matters: the first matching prefix wins, so Ingestion.* precedes the plain foundation rule.
$folderTag = @(
    @{ Prefix = 'Qavren.Edge.Ingestion'; Tag = 'chunking' },
    @{ Prefix = 'Qavren.Edge.Chat';      Tag = 'genai' },
    @{ Prefix = 'Qavren.Edge.Rag';       Tag = 'genai' },
    @{ Prefix = 'Qavren.Edge.Onnx';      Tag = 'onnxruntime' },
    @{ Prefix = 'Qavren.Edge.Embeddings';Tag = 'onnxruntime' },
    @{ Prefix = 'Qavren.Edge.VectorData';Tag = 'onnxruntime' },
    @{ Prefix = 'Qavren.Edge';           Tag = 'hosting' }
)

$nupkgs = @(Get-ChildItem "$PackagesDir/*.nupkg" | Where-Object Name -notlike '*.symbols.nupkg')
if ($nupkgs.Count -ne $ExpectedCount) { throw "expected $ExpectedCount packages in $PackagesDir, found $($nupkgs.Count)" }

foreach ($nupkg in $nupkgs) {
    $zip = [IO.Compression.ZipFile]::OpenRead($nupkg.FullName)
    try {
        $names = $zip.Entries.FullName
        foreach ($required in 'icon.png', 'README.md') {
            if ($names -notcontains $required) { throw "$($nupkg.Name) is missing $required at the package root" }
        }
        $entry = $zip.Entries | Where-Object FullName -like '*.nuspec' | Select-Object -First 1
        $reader = New-Object IO.StreamReader($entry.Open())
        try { $xml = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
        $m = $xml.package.metadata
        if ($m.icon -ne 'icon.png')     { throw "$($m.id): PackageIcon is '$($m.icon)', expected icon.png" }
        if ($m.readme -ne 'README.md')  { throw "$($m.id): PackageReadmeFile is '$($m.readme)', expected README.md" }
        if ($m.license.'#text' -ne 'MIT') { throw "$($m.id): licence expression is '$($m.license.'#text')', expected MIT" }
        $tags = ($m.tags -split ' ')
        $rule = $folderTag | Where-Object { $m.id.StartsWith($_.Prefix) } | Select-Object -First 1
        if ($tags -notcontains $rule.Tag) { throw "$($m.id): tags '$($m.tags)' lack the sub-project tag '$($rule.Tag)'" }
        foreach ($suite in 'sqlite', 'maui') {
            if ($tags -notcontains $suite) { throw "$($m.id): tags '$($m.tags)' lack the suite tag '$suite'" }
        }
    }
    finally { $zip.Dispose() }
}
Write-Host "OK: $($nupkgs.Count) packages carry icon.png, README.md, MIT and their sub-project tags"
```

Note: NuGet writes tags space-separated into the nuspec even though MSBuild takes them `;`-separated, hence `-split ' '`.

- [ ] **Step 2: Run it locally to see it pass**

From the repo root, after `dotnet pack QavrenEdge.slnx -c Release -o artifacts/packages`:

```
pwsh foundation/tools/ci-checks/assert-packages.ps1
```

Expected: `OK: 17 packages carry icon.png, README.md, MIT and their sub-project tags`. Then prove it can fail: `Remove-Item artifacts/packages/Qavren.Edge.Rag.*.nupkg` and run again; expected: `expected 17 packages ... found 16`. Re-pack afterwards.

- [ ] **Step 3: Call it from the Windows pack lane**

In `.github/workflows/ci.yml`, directly after the step named `Assert every native RID reached the package` (and before the `actions/upload-artifact@v7` step that uploads `packages`), add:

```yaml
      - name: Assert package metadata (icon, README, tags)
        if: matrix.os == 'windows-2025'
        shell: pwsh
        run: pwsh foundation/tools/ci-checks/assert-packages.ps1
```

Match the indentation of the neighbouring steps exactly (six spaces before `- name:`).

- [ ] **Step 4: Pin it in assert-workflows.py**

Append to `foundation/tools/ci-checks/assert-workflows.py`, before the final `win = ...` line:

```python
# --- Sub-project 5 (release path) ---
# Every nupkg must carry the icon, a README and its sub-project tags; the Windows pack lane runs
# the script that proves it, so a package that loses its README fails the PR, not the release.
if ci_text.count("assert-packages.ps1") != 1:
    problems.append("ci.yml Windows pack lane must run foundation/tools/ci-checks/assert-packages.ps1 exactly once")
if not (root / "foundation" / "tools" / "ci-checks" / "assert-packages.ps1").is_file():
    problems.append("foundation/tools/ci-checks/assert-packages.ps1 is missing")
```

Run: `python foundation/tools/ci-checks/assert-workflows.py`. Expected: the `OK: ...` line.

- [ ] **Step 5: Commit**

```
git add foundation/tools/ci-checks/assert-packages.ps1 foundation/tools/ci-checks/assert-workflows.py .github/workflows/ci.yml
git commit -F <msg>   # ci(pack): assert icon, README, licence and tags in every nupkg on the Windows lane
```

---

### Task 9: release.yml - tag guards, prerelease flag, dry run, exact counts

**Files:**
- Modify: `.github/workflows/release.yml`
- Modify: `foundation/tools/ci-checks/assert-workflows.py`

- [ ] **Step 1: Exact counts in the Checksums step**

Replace the body of the `Checksums` step's `run:` with:

```powershell
$nupkg  = @(Get-ChildItem artifacts/packages/*.nupkg)
$snupkg = @(Get-ChildItem artifacts/packages/*.snupkg)
$zips   = @(Get-ChildItem artifacts/native/*.zip)
if ($nupkg.Count -ne 17) { throw "expected 17 .nupkg, found $($nupkg.Count)" }
if ($snupkg.Count -lt 15) { throw "expected at least 15 .snupkg, found $($snupkg.Count)" }
if ($zips.Count -ne 3)    { throw "expected 3 native zips, found $($zips.Count)" }
if (-not (Test-Path artifacts/sbom.spdx.json)) { throw "artifacts/sbom.spdx.json is missing" }
$files = $nupkg + $snupkg + $zips + @(Get-Item artifacts/sbom.spdx.json)
$files | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_ -Algorithm SHA256).Hash, $_.Name } |
  Set-Content artifacts/SHA256SUMS.txt
Get-Content artifacts/SHA256SUMS.txt
```

(The two native-only packages may produce no symbol package; the dry run in Task 11 shows the real `.snupkg` count, and a follow-up can pin it exactly.)

- [ ] **Step 2: Run the package metadata assertion in the release job too**

After the `Pack` step add:

```yaml
      - name: Assert package metadata (icon, README, tags)
        shell: pwsh
        run: pwsh foundation/tools/ci-checks/assert-packages.ps1
```

- [ ] **Step 3: Dry-run artifact, tag guards, prerelease flag**

Replace the `Push to NuGet.org` step and the `softprops/action-gh-release@v2` step with:

```yaml
      # workflow_dispatch is the dry run: everything above ran, nothing below does, and the
      # would-be release assets are inspectable as a workflow artifact.
      - uses: actions/upload-artifact@v7
        if: "!startsWith(github.ref, 'refs/tags/v')"
        with:
          name: release-dry-run
          path: |
            artifacts/packages/*.nupkg
            artifacts/packages/*.snupkg
            artifacts/native/*.zip
            artifacts/sbom.spdx.json
            artifacts/SHA256SUMS.txt

      - name: Push to NuGet.org
        if: startsWith(github.ref, 'refs/tags/v')
        run: dotnet nuget push "artifacts/packages/*.nupkg" --source https://api.nuget.org/v3/index.json --api-key ${{ secrets.NUGET_API_KEY }} --skip-duplicate

      - uses: softprops/action-gh-release@v2
        if: startsWith(github.ref, 'refs/tags/v')
        with:
          prerelease: ${{ contains(github.ref_name, '-') }}
          files: |
            artifacts/packages/*.nupkg
            artifacts/packages/*.snupkg
            artifacts/native/*.zip
            artifacts/sbom.spdx.json
            artifacts/SHA256SUMS.txt
          generate_release_notes: true
```

- [ ] **Step 4: Pin the contract in assert-workflows.py**

Append after the Task 8 block:

```python
# release.yml: workflow_dispatch is a dry run. Exactly the two publishing steps are guarded on a
# v* tag, the dry run uploads the would-be assets, prereleases are flagged from the tag name, and
# the release job proves package metadata before anything is pushed.
if rel_text.count("if: startsWith(github.ref, 'refs/tags/v')") != 2:
    problems.append("release.yml must guard exactly two steps (NuGet push, GitHub release) on startsWith(github.ref, 'refs/tags/v')")
for token in ("name: release-dry-run", "prerelease: ${{ contains(github.ref_name, '-') }}",
              "assert-packages.ps1", "expected 17 .nupkg"):
    if token not in rel_text:
        problems.append("release.yml missing " + token)
rel_steps = rel["jobs"]["release"]["steps"]
names = [str(s.get("name", s.get("uses", ""))) for s in rel_steps]
if names.index("Push to NuGet.org") > [i for i, s in enumerate(rel_steps) if str(s.get("uses", "")).startswith("softprops/action-gh-release")][0]:
    problems.append("release.yml must push to NuGet before creating the GitHub release")
```

Run: `python foundation/tools/ci-checks/assert-workflows.py`. Expected: the `OK: ...` line. Also `python -c "import yaml,sys; yaml.safe_load(open('.github/workflows/release.yml'))"` must not raise (the `if: "!startsWith(...)"` needs its quotes).

- [ ] **Step 5: Commit**

```
git add .github/workflows/release.yml foundation/tools/ci-checks/assert-workflows.py
git commit -F <msg>   # ci(release): publish only on v* tags, dry-run on workflow_dispatch, flag prereleases, exact asset counts
```

---

### Task 10: Root README bootstrap checklist and spec pointer

**Files:**
- Modify: `README.md` (root), section `## Bootstrap checklist (owner actions, not automatable)`

- [ ] **Step 1: Update the rows**

- Change `- [ ] **Apply branch protection**, ...` to `- [x] **Apply branch protection** (applied 2026-09-12; required contexts `ci-gate` and `natives / native-gate`, linear history, conversation resolution).` and delete the fenced `gh api` command and the two explanatory lines under it (they are now history; `.github/branch-protection.json` and `assert-workflows.py` keep the contract).
- Change `- [ ] Add the repo to _tooling/lib/repos.psd1 ...` to `- [x] Added to the workspace CI audit roster (2026-09-12).`
- Change the `NUGET_API_KEY` row to:
  `- [ ] Add the `NUGET_API_KEY` repository secret (scope: push new packages and versions, glob `Qavren.*`). Then dispatch `release.yml` by hand once as a dry run, download `release-dry-run`, and only then push `v0.1.0-preview.1` on `main`. The prefix email goes out after nuget.org lists the packages. Design: `docs/superpowers/specs/2026-09-12-sp5-release-path-design.md`.`

Keep the prefix-reservation row as is.

- [ ] **Step 2: Commit**

```
git add README.md
git commit -F <msg>   # docs: bootstrap checklist - branch protection and roster done, release dry-run sequence
```

---

### Task 11: PR, CI, merge, dry run

- [ ] **Step 1: Full local gate**

```
dotnet restore QavrenEdge.slnx
dotnet build QavrenEdge.slnx -c Release
dotnet pack QavrenEdge.slnx -c Release -o artifacts/packages
pwsh foundation/tools/ci-checks/assert-packages.ps1
python foundation/tools/ci-checks/assert-workflows.py
dotnet format QavrenEdge.slnx --verify-no-changes --no-restore
```

Expected: 0 errors, `OK: 17 packages ...`, `OK: gates ...`, format exit 0.

- [ ] **Step 2: Push and open the PR**

```
git push -u origin feat/sp5-release-path
gh pr create --base main --head feat/sp5-release-path --title "feat(release): SP5 release path - icon, package READMEs, sub-project tags, guarded release.yml with a dry run" --body-file <body>
```

Body: the spec's section 3 headings as bullets, the owner actions, and the session link line `https://claude.ai/code/session_015anNxsJU4sQWdMeZFsAKUF` last.

- [ ] **Step 3: Wait for `ci-gate` and `natives / native-gate`**

`gh pr checks <n> --watch`. The Windows lane's new assertion step must be green. Fix anything red on the branch; never bypass.

- [ ] **Step 4: Squash-merge**

`gh pr merge <n> --squash --delete-branch --subject "<title> (#<n>)" --body-file <body-with-Claude-Session-trailer>`.

- [ ] **Step 5: Dry run**

`gh workflow run release.yml --ref main`, then `gh run watch <id> --exit-status`. Expected: green with the `release-dry-run` artifact and NO "Push to NuGet.org" or release step executed (both skipped). `gh run download <id> -n release-dry-run --dir <scratch>`: count `.nupkg` (17), `.snupkg` (record the number), zips (3), `SHA256SUMS.txt` present. Report the `.snupkg` count so the floor in Task 9 Step 1 can be pinned in a follow-up.

- [ ] **Step 6: Hand over**

Report to Steve: the merge SHA, the dry-run run URL, and the three owner actions in order (secret, tag on `main`, prefix email).
