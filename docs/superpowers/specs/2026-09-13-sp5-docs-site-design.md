# Sub-project 5, part 2: the documentation site

Date: 2026-09-13. Status: approved design.

Part 1 (the release path) shipped `v0.1.0-preview.1`. This part gives the
suite a documentation site with an API reference. Part 3 (benchmarks) and
`v1.0.0` follow.

## 1. Goal

A public site at `https://edge.qavrensolutions.com` that carries the conceptual
documentation the repository already has (area READMEs, ADRs, the error
catalogue, package READMEs) and an API reference generated from the packages'
XML documentation, rebuilt and deployed on every push to `main`, and built
(without deploying) on every pull request so a broken page or a metadata
failure blocks the merge that caused it.

## 2. What exists

- Every packable project already generates an XML documentation file
  (`GenerateDocumentationFile=true` repo-wide). Coverage is incomplete and
  `CS1591` is suppressed; complete coverage is a 1.0 gate, not this part's.
- Four area READMEs, 18 ADRs, `foundation/docs/errors.md`,
  `foundation/native/README.md`, and one README per package. Internal design
  records live under `docs/superpowers/` and each area's `docs/superpowers/`.
- No docs generator anywhere in the Qavren family. No GitHub Pages on the
  repository. The Qavren site is `qavrensolutions.com`, DNS on Cloudflare.
- `Qavren.Edge.Maui` has no `net10.0` target; on Linux it builds as
  `net10.0-android`, and the Linux CI lane installs `maui-android` for that.

## 3. Design

### 3.1 Generator and layout

DocFX 2.78.x, pinned in `.config/dotnet-tools.json` and run as
`dotnet docfx`. Everything site-specific lives under `docs/site/`:

- `docfx.json`: metadata and build configuration.
- `index.md`: the landing page (what the suite is, the install lines, links
  into the sections). Written for the site, not a copy of the root README.
- `getting-started.md`: the composed quick start, taken from the root README's
  quick start so the two do not drift in substance.
- `toc.yml`: the top navigation: Getting started, Guides (the four areas),
  Packages (the 17 package READMEs), Decisions (the ADRs), Errors, API.
- `templates/`: nothing custom in this part. The `modern` template with the
  suite icon as logo and favicon.

Repository documents are pulled into the site from where they live, through
content globs rooted at the repository (`"src": "../.."`), mirrored under a
`repo/` prefix so relative links between them keep resolving. Included:
`foundation/README.md`, `foundation/docs/**/*.md`,
`foundation/native/README.md`, `embeddings/README.md`,
`embeddings/docs/adr/*.md`, `ingestion/README.md`, `ingestion/docs/adr/*.md`,
`chat/README.md`, `chat/docs/adr/*.md`, and `*/src/*/README.md`.
`LICENSE` and `THIRD-PARTY-NOTICES.md` ride along as resources so links to
them resolve. Excluded: every `docs/superpowers/` folder, tests, samples,
`bin/`, `obj/`. The root README is not mirrored; the landing page replaces it.

### 3.2 API reference

`docfx metadata` over the 17 packable projects, public surface only, output
under `api/`, namespaces nested, one page per type. Two metadata entries:

- 16 projects with `TargetFramework=net10.0`.
- `Qavren.Edge.Maui` with `TargetFramework=net10.0-android`, which is what the
  Linux docs runner can build after `dotnet workload install maui-android`.

`*.Internal` namespaces contain `internal` types and never appear. Members
without an XML summary render with an empty summary; that is visible, not
fatal, and is the coverage gate's job later.

### 3.3 Workflow

`.github/workflows/docs.yml`:

- Triggers: `pull_request`, `push` to `main`, `workflow_dispatch`.
- One `build` job on `ubuntu-24.04`: checkout, setup-dotnet from
  `global.json`, `dotnet workload install maui-android --version 10.0.201`,
  `dotnet tool restore`, `dotnet docfx docs/site/docfx.json --warningsAsErrors`,
  then `actions/upload-pages-artifact` from `docs/site/_site`.
- One `deploy` job, `needs: build`, `if: github.event_name != 'pull_request'`,
  `environment: github-pages`, permissions `pages: write` and `id-token: write`,
  `actions/deploy-pages`. A `concurrency` group `pages` with
  `cancel-in-progress: false` serialises deploys.
- `assert-workflows.py` gains rules: `docs.yml` exists, its deploy job is
  guarded off `pull_request`, and the build passes `--warningsAsErrors`.

`--warningsAsErrors` is the link check: DocFX warns on every unresolved
internal link, missing TOC target, and metadata failure.

### 3.4 Domain

GitHub Pages on the repository is enabled with `build_type: workflow` and
custom domain `edge.qavrensolutions.com`; `https_enforced` is switched on
once GitHub has issued the certificate. DNS: a `CNAME` record `edge` pointing
at `qavren-oss.github.io`, DNS-only (not proxied) so GitHub's certificate
issuance and domain verification see the record directly. Adding the record is
an owner action: no working Cloudflare credential exists on the build machine.
The site's `sitemap` base URL is the custom domain from the first deploy.

### 3.5 Verification

- Local: `dotnet docfx docs/site/docfx.json --warningsAsErrors` exits 0 and
  `docs/site/_site/api/` contains a page per package namespace;
  `docs/site/_site/repo/foundation/docs/errors.html` exists; the landing page
  links resolve.
- CI: the PR build job is green before merge.
- After merge: the deploy job is green, the default Pages URL serves the site,
  and once DNS resolves, `https://edge.qavrensolutions.com/` serves it with a
  valid certificate.

## 4. Owner action

Add the DNS record in Cloudflare for `qavrensolutions.com`:
`CNAME edge -> qavren-oss.github.io`, proxy off. Everything else is in the
repository or the GitHub API.

## 5. Out of scope

Benchmarks, per-release versioned documentation, analytics, a custom theme,
XML documentation coverage, a family-wide docs portal.

## 6. Risks

- The `Qavren.Edge.Maui` metadata build needs the Android workload on the
  docs runner; if that step ever breaks, the fallback is to drop the second
  metadata entry and document `UseQavrenEdge` by hand.
- `--warningsAsErrors` makes any new unresolved link in a README a red PR.
  That is the point, but it means README edits must keep links to files that
  are part of the site's content set.
- GitHub Pages certificate issuance waits on DNS; until the record exists the
  deploy job succeeds and the custom domain simply does not resolve.
