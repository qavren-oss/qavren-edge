# Docs Site Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A DocFX site under `docs/site/` that carries the repository's documentation and an API reference for all 17 packages, built on every PR and deployed to GitHub Pages at `https://edge.qavrensolutions.com` on every push to `main`.

**Architecture:** DocFX pulls repository markdown into the site through globs rooted at the repo (mirrored under `repo/`), generates API metadata from the packable projects (two entries: `net10.0` for 16 packages, `net10.0-android` for `Qavren.Edge.Maui`), and one workflow builds and, off `pull_request`, deploys the static output with `actions/deploy-pages`. `--warningsAsErrors` is the link and metadata gate.

**Tech Stack:** DocFX 2.78.5 (dotnet tool), GitHub Pages (workflow build type), GitHub Actions, Python 3 (PyYAML) for `assert-workflows.py`.

Spec: `docs/superpowers/specs/2026-09-13-sp5-docs-site-design.md`. Branch: `feat/docs-site` (exists, spec committed). Work in `C:\Users\steve\projects\qavren-edge`.

**Repo rules:** Conventional Commits; never commit to `main`; no `Co-Authored-By`; every commit message ends with `Claude-Session: https://claude.ai/code/session_015anNxsJU4sQWdMeZFsAKUF`; write the message to a file and `git commit -F`. `git add` only the paths a task names.

---

## File structure

| Path | Responsibility |
|---|---|
| `.config/dotnet-tools.json` | Pins `docfx` |
| `.gitignore` | Ignores DocFX output (`docs/site/_site/`, generated `api/`, `api-maui/`, `obj/`) |
| `docs/site/docfx.json` | Metadata (two entries) and build configuration |
| `docs/site/index.md` | Landing page |
| `docs/site/getting-started.md` | The composed quick start |
| `docs/site/toc.yml` | Top navigation |
| `.github/workflows/docs.yml` | Build on PR, build + deploy on `main` |
| `foundation/tools/ci-checks/assert-workflows.py` | Pins the docs workflow contract |
| `docs/maintainers.md` | Gains a "Docs site" section |

---

### Task 1: Pin DocFX and ignore its output

**Files:**
- Modify: `.config/dotnet-tools.json`
- Modify: `.gitignore`

- [ ] **Step 1: Add the tool**

From the repo root:

```
dotnet tool install docfx --version 2.78.5
```

Expected: `.config/dotnet-tools.json` gains a `"docfx"` entry with `"commands": ["docfx"]`. Verify: `dotnet docfx --version` prints `2.78.5`.

- [ ] **Step 2: Ignore the output**

Append to `.gitignore`:

```
# DocFX (docs/site): generated metadata and the built site
docs/site/_site/
docs/site/api/
docs/site/api-maui/
docs/site/obj/
```

- [ ] **Step 3: Commit**

```
git add .config/dotnet-tools.json .gitignore
git commit -F <msg>   # build(docs): pin docfx 2.78.5; ignore the generated site
```

---

### Task 2: docfx.json

**Files:**
- Create: `docs/site/docfx.json`

- [ ] **Step 1: Write the configuration**

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/docfx/main/schemas/docfx.schema.json",
  "metadata": [
    {
      "src": [
        {
          "src": "../..",
          "files": [
            "foundation/src/*/*.csproj",
            "embeddings/src/*/*.csproj",
            "ingestion/src/*/*.csproj",
            "chat/src/*/*.csproj"
          ],
          "exclude": [ "foundation/src/Qavren.Edge.Maui/**" ]
        }
      ],
      "dest": "api",
      "properties": { "TargetFramework": "net10.0" },
      "namespaceLayout": "nested",
      "memberLayout": "separatePages",
      "enumSortOrder": "declaringOrder"
    },
    {
      "src": [
        {
          "src": "../..",
          "files": [ "foundation/src/Qavren.Edge.Maui/Qavren.Edge.Maui.csproj" ]
        }
      ],
      "dest": "api-maui",
      "properties": { "TargetFramework": "net10.0-android" },
      "namespaceLayout": "nested",
      "memberLayout": "separatePages"
    }
  ],
  "build": {
    "content": [
      {
        "files": [ "index.md", "getting-started.md", "toc.yml", "api/**.yml", "api/index.md", "api-maui/**.yml", "api-maui/index.md" ]
      },
      {
        "src": "../..",
        "dest": "repo",
        "files": [
          "foundation/README.md",
          "foundation/docs/**/*.md",
          "foundation/native/README.md",
          "foundation/src/*/README.md",
          "embeddings/README.md",
          "embeddings/docs/adr/*.md",
          "embeddings/src/*/README.md",
          "ingestion/README.md",
          "ingestion/docs/adr/*.md",
          "ingestion/src/*/README.md",
          "chat/README.md",
          "chat/docs/adr/*.md",
          "chat/src/*/README.md"
        ],
        "exclude": [ "**/docs/superpowers/**", "**/bin/**", "**/obj/**" ]
      }
    ],
    "resource": [
      { "src": "../..", "dest": "repo", "files": [ "LICENSE", "THIRD-PARTY-NOTICES.md" ] },
      { "src": "../..", "files": [ "icon.svg", "icon.png" ] }
    ],
    "output": "_site",
    "template": [ "default", "modern" ],
    "globalMetadata": {
      "_appTitle": "Qavren.Edge",
      "_appName": "Qavren.Edge",
      "_appLogoPath": "icon.svg",
      "_appFaviconPath": "icon.png",
      "_appFooter": "Qavren.Edge is MIT licensed. A Qavren Solutions LLC project.",
      "_enableSearch": true,
      "_gitContribute": { "repo": "https://github.com/qavren-oss/qavren-edge", "branch": "main" }
    },
    "sitemap": { "baseUrl": "https://edge.qavrensolutions.com/" },
    "xref": [ "https://learn.microsoft.com/en-us/dotnet/.xrefmap.json" ]
  }
}
```

Why two metadata entries with different `dest`: two entries writing the same `dest` overwrite each other's generated `toc.yml`, and `Qavren.Edge.Maui` has no `net10.0` target, so it cannot share the first entry's `TargetFramework`.

- [ ] **Step 2: Generate metadata only and check the output**

From the repo root, after `dotnet workload list` shows `maui-android` (install with `dotnet workload install maui-android --version 10.0.201` if it does not; on this Windows box the `maui` workload already covers it):

```
dotnet docfx metadata docs/site/docfx.json
```

Expected: no errors; `docs/site/api/` contains `toc.yml`, `Qavren.Edge.yml`, `Qavren.Edge.Sqlite.yml`, `Qavren.Edge.VectorData.yml`, `Qavren.Edge.Ingestion.yml`, `Qavren.Edge.Chat.yml`, `Qavren.Edge.Rag.yml` and one `.yml` per public type; `docs/site/api-maui/` contains `toc.yml` and `Qavren.Edge.Maui.yml` (or the namespace the `UseQavrenEdge` extension lives in). No `*.Internal.*` files anywhere under `api/`. If a project fails to build in metadata, the error names it; fix the config, never the project.

- [ ] **Step 3: Commit**

```
git add docs/site/docfx.json
git commit -F <msg>   # docs(site): docfx configuration - repo content mirrored under repo/, API metadata per package
```

---

### Task 3: Landing page, getting started, navigation

**Files:**
- Create: `docs/site/index.md`, `docs/site/getting-started.md`, `docs/site/toc.yml`

- [ ] **Step 1: `docs/site/index.md`**

````markdown
---
_layout: landing
title: Qavren.Edge
---

# Qavren.Edge

On-device data and AI for .NET MAUI and .NET 10: SQLite with `sqlite-vec`,
ONNX Runtime embeddings, a `Microsoft.Extensions.VectorData` store, document
ingestion, and local chat with retrieval-augmented generation. Everything runs
in-process on the device. There is no service to call, nothing leaves the
phone, and nothing is downloaded without the user's consent.

## Install

```
dotnet add package Qavren.Edge --prerelease          # hosting core + SQLite + the native library
dotnet add package Qavren.Edge.Maui --prerelease     # MAUI lifecycle bridge and app paths
```

Then add what you use: `Qavren.Edge.Embeddings.Onnx`, `Qavren.Edge.VectorData`,
`Qavren.Edge.Ingestion` (with `.Onnx`, `.Pdf`, `.OpenXml`), `Qavren.Edge.Chat.Onnx`,
`Qavren.Edge.Rag`. Every package is MIT except that `Qavren.Edge.Ingestion.Pdf`
depends on PdfPig (Apache-2.0), which is why PDF support is a separate package.

## Where to go

- [Getting started](getting-started.md): the whole stack composed once, then
  index, search and ask.
- Guides: [hosting core and SQLite](repo/foundation/README.md),
  [embeddings and the vector store](repo/embeddings/README.md),
  [ingestion](repo/ingestion/README.md), [chat and RAG](repo/chat/README.md).
- [Error codes](repo/foundation/docs/errors.md): every `EdgeException` carries
  a numbered code and a remediation.
- [API reference](api/index.md) for every package, and the
  [MAUI package](api-maui/index.md).
- Decisions: the architecture decision records for each area, in the sidebar.

## Supported platforms

| Adopting | Android | iOS / Mac Catalyst |
|---|---|---|
| SQLite and hosting only | 21 | 15.0 |
| + embeddings, vector store, ingestion | 24 | 15.1 |
| + chat | 24 | 15.4 |

Windows x64 and arm64, Linux x64 and arm64 and macOS arm64 are supported for
plain .NET 10. Source, issues and releases:
[github.com/qavren-oss/qavren-edge](https://github.com/qavren-oss/qavren-edge).
````

If `_layout: landing` renders the page without the sidebar in a way that hides the navigation, remove the front-matter `_layout` line; the rest is unchanged.

- [ ] **Step 2: `docs/site/getting-started.md`**

Copy the root `README.md` section `## Quick start` verbatim (heading through the sentence that ends "higher is better)."), promote it to a level-1 heading `# Getting started`, and prepend:

```markdown
# Getting started

Two packages get you a database; the rest of the chain is added as you need
it. Install with `--prerelease` while the suite is pre-1.0.

```
dotnet add package Qavren.Edge --prerelease
dotnet add package Qavren.Edge.Maui --prerelease
```
```

Then the copied quick-start body follows. Do not edit the code samples.

- [ ] **Step 3: `docs/site/toc.yml`**

Generate the Packages and Decisions entries so nothing is typed by hand. From the repo root:

```
python - <<'EOF'
import pathlib, re
root = pathlib.Path(".")
def title(p):
    for line in p.read_text(encoding="utf-8").splitlines():
        if line.startswith("# "): return line[2:].strip()
    return p.stem
print("PACKAGES")
for area in ("foundation", "embeddings", "ingestion", "chat"):
    for r in sorted((root / area / "src").glob("*/README.md")):
        print(f"    - name: {r.parent.name}\n      href: repo/{r.as_posix()}")
print("DECISIONS")
for area, label in (("foundation", "Hosting core and SQLite"), ("embeddings", "Embeddings and the vector store"), ("ingestion", "Ingestion"), ("chat", "Chat and RAG")):
    print(f"    - name: {label}\n      items:")
    for a in sorted((root / area / "docs" / "adr").glob("*.md")):
        print(f"        - name: {title(a)}\n          href: repo/{a.as_posix()}")
EOF
```

Write `docs/site/toc.yml` as:

```yaml
- name: Getting started
  href: getting-started.md
- name: Guides
  items:
    - name: Hosting core and SQLite
      href: repo/foundation/README.md
    - name: Embeddings and the vector store
      href: repo/embeddings/README.md
    - name: Ingestion
      href: repo/ingestion/README.md
    - name: Chat and RAG
      href: repo/chat/README.md
    - name: Building the native library
      href: repo/foundation/native/README.md
- name: Packages
  items:
    <paste the PACKAGES block, 17 entries>
- name: Decisions
  items:
    <paste the DECISIONS block, four areas>
- name: Errors
  href: repo/foundation/docs/errors.md
- name: API reference
  href: api/
- name: MAUI API
  href: api-maui/
```

- [ ] **Step 4: Full build, then drive warnings to zero**

```
dotnet docfx docs/site/docfx.json
```

Read every `warning` line. Expected classes and their fixes:
- `InvalidFileLink` to a markdown or text file that is documentation (an ADR, a README, `errors.md`, `LICENSE`): add that path to the matching `content` or `resource` glob in `docfx.json`.
- `InvalidFileLink` to a source file, a test, a sample folder or a workflow: these are GitHub-relative links in READMEs that do not belong in the site. Convert the link in the README to an absolute `https://github.com/qavren-oss/qavren-edge/blob/main/<path>` URL. Keep the change to the link only.
- `InvalidTocHref`: a typo in `toc.yml`.
- Metadata warnings about missing XML comments are NOT expected from DocFX (it does not warn on missing summaries); anything else from metadata is a real problem, report it.

Then run with the gate the workflow uses:

```
dotnet docfx docs/site/docfx.json --warningsAsErrors
```

Expected: exit 0. Spot-check the output: `docs/site/_site/index.html`, `docs/site/_site/getting-started.html`, `docs/site/_site/repo/foundation/docs/errors.html`, `docs/site/_site/api/Qavren.Edge.Sqlite.html` (or the namespace page name DocFX chose) and `docs/site/_site/api-maui/` exist. Open `docs/site/_site/index.html` in a browser (`start docs/site/_site/index.html`) and confirm the logo, the sidebar and search render.

- [ ] **Step 5: Commit**

```
git add docs/site/index.md docs/site/getting-started.md docs/site/toc.yml docs/site/docfx.json <any README whose link you converted>
git commit -F <msg>   # docs(site): landing page, getting started, navigation; site builds with zero warnings
```

---

### Task 4: docs.yml and the assertion rules

**Files:**
- Create: `.github/workflows/docs.yml`
- Modify: `foundation/tools/ci-checks/assert-workflows.py`

- [ ] **Step 1: The workflow**

```yaml
name: docs

# Builds the DocFX site on every PR (no deploy; a broken link or a metadata failure is a red
# check) and builds + deploys it to GitHub Pages on every push to main.
on:
  pull_request:
  push:
    branches: [main]
  workflow_dispatch:

permissions:
  contents: read

concurrency:
  group: pages
  cancel-in-progress: false

env:
  DOTNET_NOLOGO: 'true'
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: 'true'

jobs:
  build:
    runs-on: ubuntu-24.04
    timeout-minutes: 30
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0    # the template's "edit this page" and last-updated read git history
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json
      # Qavren.Edge.Maui has no net10.0 target; its API metadata is built as net10.0-android,
      # the one MAUI target a Linux runner can restore.
      - run: dotnet workload install maui-android --version 10.0.201
      - run: dotnet tool restore
      - name: Build the site
        run: dotnet docfx docs/site/docfx.json --warningsAsErrors
      - uses: actions/upload-pages-artifact@v4
        with:
          path: docs/site/_site

  deploy:
    if: github.event_name != 'pull_request'
    needs: build
    runs-on: ubuntu-24.04
    permissions:
      pages: write
      id-token: write
    environment:
      name: github-pages
      url: ${{ steps.deployment.outputs.page_url }}
    steps:
      - id: deployment
        uses: actions/deploy-pages@v4
```

Validate: `python -c "import yaml; yaml.safe_load(open('.github/workflows/docs.yml'))"`.

- [ ] **Step 2: Assertion rules**

Append to `foundation/tools/ci-checks/assert-workflows.py` before the final `win = ...` line:

```python
# --- Docs site (SP5 part 2) ---
# docs.yml builds on every PR and deploys only off pull_request; the build is the link check,
# so --warningsAsErrors must stay on; the deploy is the official Pages action.
docs_path = w / "docs.yml"
if not docs_path.is_file():
    problems.append("docs.yml is missing")
else:
    docs_text = docs_path.read_text(encoding="utf-8")
    docs = yaml.safe_load(docs_text)
    docs_on = docs.get("on", docs.get(True)) or {}
    if "pull_request" not in docs_on or "push" not in docs_on:
        problems.append("docs.yml must run on pull_request and on push")
    deploy = docs.get("jobs", {}).get("deploy", {})
    if "pull_request" not in str(deploy.get("if", "")):
        problems.append("docs.yml deploy job must be guarded off pull_request")
    if "--warningsAsErrors" not in docs_text:
        problems.append("docs.yml must build the site with --warningsAsErrors")
    if "actions/deploy-pages" not in docs_text or "actions/upload-pages-artifact" not in docs_text:
        problems.append("docs.yml must upload with upload-pages-artifact and deploy with deploy-pages")
    if "maui-android" not in docs_text:
        problems.append("docs.yml must install maui-android for the Qavren.Edge.Maui metadata build")
```

Run: `python foundation/tools/ci-checks/assert-workflows.py`. Expected: the `OK: ...` line.

- [ ] **Step 3: Commit**

```
git add .github/workflows/docs.yml foundation/tools/ci-checks/assert-workflows.py
git commit -F <msg>   # ci(docs): build the site on every PR, deploy to GitHub Pages from main
```

---

### Task 5: Maintainer notes

**Files:**
- Modify: `docs/maintainers.md`

- [ ] **Step 1: Add a section after "## CI shape"**

```markdown
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
```

- [ ] **Step 2: Commit**

```
git add docs/maintainers.md
git commit -F <msg>   # docs: maintainer notes for the docs site
```

---

### Task 6: PR, Pages, DNS, first deploy

- [ ] **Step 1: Local gate**

```
python foundation/tools/ci-checks/assert-workflows.py
dotnet docfx docs/site/docfx.json --warningsAsErrors
```

- [ ] **Step 2: Push and open the PR**

```
git push -u origin feat/docs-site
gh pr create --base main --head feat/docs-site --title "feat(docs): DocFX documentation site with API reference, deployed to GitHub Pages" --body-file <body>
```

The `docs` check must be green alongside `ci-gate`.

- [ ] **Step 3: Enable Pages with the custom domain** (repository admin; done from the controller session)

```
gh api -X POST repos/qavren-oss/qavren-edge/pages -f build_type=workflow
gh api -X PUT  repos/qavren-oss/qavren-edge/pages -f cname=edge.qavrensolutions.com -f build_type=workflow
gh api repos/qavren-oss/qavren-edge/pages --jq '{status,cname,https_enforced,build_type}'
```

- [ ] **Step 4: Owner action: DNS**

In Cloudflare, zone `qavrensolutions.com`: `CNAME`, name `edge`, target `qavren-oss.github.io`, proxy status DNS only. Verify from anywhere: `nslookup -type=CNAME edge.qavrensolutions.com`.

- [ ] **Step 5: Merge and deploy**

Squash-merge the PR. The push to `main` runs `docs.yml`; the deploy job prints the Pages URL. Then:

```
gh api repos/qavren-oss/qavren-edge/pages --jq '{status,cname,https_enforced,protected_domain_state}'
```

When `protected_domain_state` is `verified` and the certificate is issued (the API's `https_certificate.state` is `approved`), enforce HTTPS:

```
gh api -X PUT repos/qavren-oss/qavren-edge/pages -f https_enforced=true
curl -sI https://edge.qavrensolutions.com/ | head -1
```

Expected: `HTTP/2 200`.
