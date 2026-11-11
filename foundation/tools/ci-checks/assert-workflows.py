"""Assert the workflow contract spec sections 13 and 14 require. Run: python assert-workflows.py [repo-root]"""
import json
import pathlib
import re
import sys

import yaml

# Default to the checkout this script lives in (foundation/tools/ci-checks/ -> repo root), so a
# worktree or a CI clone is checked rather than a hard-coded path on one machine.
root = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else pathlib.Path(__file__).resolve().parents[3]
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
# native.yml no longer runs standalone, so its gate only ever reports through ci.yml's `natives`
# job, whose check runs are named "<caller job id> / <called job id>". A bare "native-gate"
# context would never report again and would block every PR forever.
if sorted(bp["required_status_checks"]["contexts"]) != ["ci-gate", "natives / native-gate"]:
    problems.append("branch-protection contexts do not match the gate jobs as GitHub names them")
if "natives" not in ci["jobs"] or ci["jobs"]["natives"].get("uses") != "./.github/workflows/native.yml":
    problems.append("ci.yml must call native.yml from a job literally named `natives`; the required context embeds that name")
# Deduplication: native.yml must have NO triggers of its own. ci.yml already runs on push and
# pull_request and calls it, so a push/pull_request trigger here builds every native leg twice in
# parallel - and the two runs then race for the identical actions/cache key, so one of them saves
# nothing. (`on` is a YAML 1.1 boolean, so safe_load keys it True, not "on".)
nat_on = nat.get("on", nat.get(True)) or {}
if sorted(nat_on) != ["workflow_call", "workflow_dispatch"]:
    problems.append("native.yml must be reachable only via workflow_call/workflow_dispatch, not its own push/pull_request triggers (found: %s)" % ", ".join(sorted(nat_on)))
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
if nat_text.count("actions/cache/restore@v6.1.0") != 3:
    problems.append("native.yml reuse job must restore all three OS caches")
# A cache entry can be absent while the artifact still exists (an interrupted save, an eviction).
# The reuse job must repair that instead of taking a managed-only PR red.
if nat_text.count("reuse-native-artifact.sh") != 3:
    problems.append("native.yml reuse job must fall back to the last good run's artifact for all three OS legs")
if nat_text.count("actions/cache/save@v6.1.0") != 3:
    problems.append("native.yml reuse job must re-seed all three OS caches after an artifact fallback")
if not (w.parent / "scripts" / "reuse-native-artifact.sh").is_file():
    problems.append(".github/scripts/reuse-native-artifact.sh is missing")
for f, text in (("ci.yml", ci_text), ("release.yml", rel_text)):
    if "actions: read" not in text:
        problems.append(f + " must grant the natives job `actions: read`; a called workflow cannot widen the caller's token (release.yml run 34715152977 died at startup without it)")
for f in ("ci.yml", "release.yml"):
    if "force: true" in (w / f).read_text(encoding="utf-8"):
        problems.append(f + " passes force:true to native.yml, forcing a rebuild on managed-only PRs")

# --- Sub-project 2 ---
# Every test project under embeddings/tests/ and ingestion/tests/ must appear as an explicit
# ci.yml step, so a new test project cannot silently never run. Neither trim-smoke (published,
# not run as a test step) nor ingestion/tests/fixtures/ (no csproj) is matched by the glob.
for area in ("embeddings", "ingestion"):
    area_tests = sorted((root / area / "tests").glob("*/*.csproj"))
    if not area_tests:
        problems.append("no test projects found under %s/tests/" % area)
    for proj in area_tests:
        rel_path = proj.relative_to(root).as_posix()
        if rel_path not in ci_text:
            problems.append("ci.yml has no explicit step for " + rel_path)

# The PdfPig asset assertion (spec 15). PdfPig ships no net10.0 TFM; a silent fall-back to
# lib/netstandard2.0 is a static-constructor throw on a device with no compile error anywhere.
for token in ("PdfPig/0.1.16", "lib/net9.0"):
    if token not in ci_text:
        problems.append("ci.yml PdfPig asset assertion is incomplete (" + token + ")")

# The ORT managed-asset assertion. Written against Microsoft.ML.OnnxRuntime.Managed on purpose:
# the native Microsoft.ML.OnnxRuntime package has no compile/runtime assets for any TFM, so an
# assertion against that id would pass vacuously forever.
for token in ("Microsoft.ML.OnnxRuntime.Managed/1.30.0", "lib/net9.0-android35.0",
              "lib/net9.0-ios18.0", "lib/net9.0-maccatalyst18.0", "lib/net8.0"):
    if token not in ci_text:
        problems.append("ci.yml ORT asset assertion is incomplete (" + token + ")")

if "trim-smoke" not in ci["jobs"]:
    problems.append("ci.yml missing job trim-smoke")
elif "trim-smoke" not in ci["jobs"]["ci-gate"]["needs"]:
    problems.append("trim-smoke is not in ci-gate's needs, so its failure would not block a merge")

# The tier-3 nightly lane (spec 16.3). Three properties, each of which has a way of quietly
# regressing: the schedule trigger can be dropped in a merge, the job can lose its event guard
# and start running on every PR (a 23 MB download per push), and the cache key can drift off the
# content hash onto a URL or a date - at which point a swapped model is invisible.
if "schedule" not in (ci.get(True) or ci.get("on") or {}):
    problems.append("ci.yml has no schedule trigger, so the tier-3 model lane never runs")
if "model-tests" not in ci["jobs"]:
    problems.append("ci.yml missing job model-tests (spec 16.3 tier 3)")
else:
    mt = ci["jobs"]["model-tests"]
    if "schedule" not in str(mt.get("if", "")):
        problems.append("model-tests is not guarded to schedule/workflow_dispatch; it would run on PRs")
    if "model-tests" in ci["jobs"]["ci-gate"]["needs"]:
        problems.append("model-tests must NOT gate ci-gate: spec 16.3 says never on a PR")
    sha = "4278337fd0ff3c68bfb6291042cad8ab363e1d9fbc43dcb499fe91c871902474"
    if sha not in ci_text:
        problems.append("model-tests does not pin the model sha256; the cache key must be the content hash")
    if "actions/cache@v6.1.0" not in ci_text:
        problems.append("model-tests does not cache the model with actions/cache@v6.1.0")
    # Ingestion.Tests loads qedge_sqlite3 (vec0, FTS5), which reaches the job only through the
    # native-* artifacts that `natives` uploads. Without the edge the job starts alongside
    # `natives`, download-artifact finds 0 artifacts, and every SQLite-backed test fails on load:
    # every nightly from the SP3 merge (9aed0e0) to run 34969457254 failed exactly that way.
    needs = mt.get("needs") or []
    if isinstance(needs, str):
        needs = [needs]
    if "natives" not in needs:
        problems.append("model-tests must `needs: natives`; it downloads native-* and runs Ingestion.Tests, which loads qedge_sqlite3")

# --- Sub-project 4 ---
# Every test project under chat/tests/ must appear as an explicit ci.yml step, so a new test
# project cannot silently never run. Same rule as embeddings/tests/, same reason.
sp4_tests = sorted((root / "chat" / "tests").glob("*/*.csproj"))
if not sp4_tests:
    problems.append("no test projects found under chat/tests/")
for proj in sp4_tests:
    relp = proj.relative_to(root).as_posix()
    if relp not in ci_text:
        problems.append("ci.yml has no explicit step for " + relp)

# The GenAI managed-asset assertion. Written against the .Managed package on purpose: the native
# Microsoft.ML.OnnxRuntimeGenAI package has no compile assets for any TFM, so an assertion against
# that id would pass vacuously forever.
for token in ("Microsoft.ML.OnnxRuntimeGenAI.Managed/0.15.2", "lib/net9.0-android31.0",
              "lib/net9.0-ios15.4", "lib/net9.0-maccatalyst14.0"):
    if token not in ci_text:
        problems.append("ci.yml GenAI asset assertion is incomplete (" + token + ")")

# The ORT floor guard. GenAI declares >= 1.28.0; the repo pins 1.30.0. Both halves must be asserted
# or somebody "fixes" the skew by downgrading ORT.
for token in ("1.28.0", "the repo pin is 1.30.0"):
    if token not in ci_text:
        problems.append("ci.yml ORT floor guard is incomplete (" + token + ")")

# The tier-3 chat lane (spec 16.3): it must exist, be guarded to schedule/workflow_dispatch, stay
# OUT of ci-gate's needs, and key its cache on the model's content hash.
if "chat-model-tests" not in ci["jobs"]:
    problems.append("ci.yml missing job chat-model-tests (spec 16.3 tier 3)")
else:
    cmt = ci["jobs"]["chat-model-tests"]
    if "schedule" not in str(cmt.get("if", "")):
        problems.append("chat-model-tests is not guarded to schedule/workflow_dispatch; it would run on PRs")
    if "chat-model-tests" in ci["jobs"]["ci-gate"]["needs"]:
        problems.append("chat-model-tests must NOT gate ci-gate: spec 16.3 says never on a PR")
    if "52640ca0d65e00d33dfb10b822c6a41e31bab1aaa6a49457b1dc5952c0dab0fb" not in ci_text:
        problems.append("chat-model-tests does not pin the model sha256; the cache key must be the content hash")

# Tier 0 (Task 1.5) is a THROWAWAY workflow in its own file, on workflow_dispatch only. It must
# never migrate into ci.yml: a six-job emulator/simulator matrix on every PR is not a gate anyone
# wants, and spec 16.0 scopes it to one run before wave 2.
if any(j.startswith("tier0-") for j in ci["jobs"]):
    problems.append("a tier0-* job has been added to ci.yml; tier 0 is workflow_dispatch only (spec 16.0)")
tier0 = w / "tier0-genai-smoke.yml"
if tier0.exists():
    t0 = yaml.safe_load(tier0.read_text(encoding="utf-8"))
    # `on:` parses as the boolean True in YAML 1.1, which is why this reads both keys.
    trig = t0.get("on", t0.get(True))
    if trig != "workflow_dispatch" and list(trig or []) != ["workflow_dispatch"]:
        problems.append("tier0-genai-smoke.yml is not workflow_dispatch-only")

# Issue #13: the sample app head's Apple native wiring (foundation/samples/Directory.Build.targets)
# is only ever linked by the two Apple device lanes, so each must build it or it rots unseen.
for job, tfm in (("device-tests-ios", "net10.0-ios"), ("device-tests-maccatalyst", "net10.0-maccatalyst")):
    runs = [str(s.get("run", "")) for s in ci["jobs"].get(job, {}).get("steps", [])]
    if not any("Qavren.Edge.Sample.csproj" in r and "-f " + tfm in r for r in runs):
        problems.append(job + " does not build the sample for " + tfm + " (issue #13)")

# --- Sub-project 5 (release path) ---
# Every nupkg must carry the icon, a README and its sub-project tags; the Windows pack lane runs
# the script that proves it, so a package that loses its README fails the PR, not the release.
if ci_text.count("assert-packages.ps1") != 2:
    problems.append("ci.yml Windows pack lane must run foundation/tools/ci-checks/assert-packages.ps1 exactly twice (metadata, then the stable-override guard)")
if not (root / "foundation" / "tools" / "ci-checks" / "assert-packages.ps1").is_file():
    problems.append("foundation/tools/ci-checks/assert-packages.ps1 is missing")
# A stable release tag (v1.0.0, no `-`) must not be able to reach release.yml's pack step and fail
# NU5104 there: ci.yml proves it on every PR by re-packing under a stable MinVerVersionOverride and
# asserting the result with assert-packages.ps1 -StableOverride.
if "-p:MinVerVersionOverride=9.9.9" not in ci_text:
    problems.append("ci.yml must pack the solution again with -p:MinVerVersionOverride=9.9.9 (the stable-tag guard)")
if "-StableOverride" not in ci_text:
    problems.append("ci.yml must run assert-packages.ps1 -StableOverride against the stable-override pack")

# release.yml is two jobs. `pack` builds everything and holds no write scope and no id-token;
# `publish` builds nothing, runs only on a v* tag, and is the one job that can mint the OIDC token
# nuget.org trusts. workflow_dispatch is the dry run: `pack` uploads the would-be assets as
# `release-dry-run` and `publish` never starts.
rel_jobs = rel.get("jobs", {})
pack, publish = rel_jobs.get("pack"), rel_jobs.get("publish")
if pack is None or publish is None:
    problems.append("release.yml must have a `pack` job and a `publish` job")
else:
    if (rel.get("permissions") or {}) != {"contents": "read"}:
        problems.append("release.yml's workflow-level permissions must be exactly contents: read")
    if (pack.get("permissions") or {}) != {"contents": "read"}:
        problems.append("release.yml `pack` must hold contents: read and nothing else: it restores and builds, so it must not be able to mint the publishing token or write the repository")
    if publish.get("needs") != "pack" and publish.get("needs") != ["pack"]:
        problems.append("release.yml `publish` must need `pack` and nothing else")
    if str(publish.get("if", "")).strip() != "startsWith(github.ref, 'refs/tags/v')":
        problems.append("release.yml `publish` must run only on a v* tag: if: startsWith(github.ref, 'refs/tags/v')")
    if (publish.get("permissions") or {}) != {"contents": "write", "id-token": "write"}:
        problems.append("release.yml `publish` must hold exactly contents: write and id-token: write")
    pack_text = yaml.safe_dump(pack)
    if "dotnet pack" not in pack_text or "assert-packages.ps1" not in pack_text:
        problems.append("release.yml `pack` must pack the solution and assert the package metadata")
    pub_steps = publish.get("steps", [])
    pub_text = yaml.safe_dump(publish)
    for forbidden in ("dotnet pack", "dotnet build", "dotnet restore", "dotnet run"):
        if forbidden in pub_text:
            problems.append("release.yml `publish` must build nothing (found `" + forbidden + "`); everything it publishes comes from `pack`'s artifact")
    if "release-assets" not in pub_text or "SHA256SUMS.txt" not in pub_text:
        problems.append("release.yml `publish` must download `release-assets` and verify it against SHA256SUMS.txt before pushing")
    names = [str(s.get("name", s.get("uses", ""))) for s in pub_steps]
    release_idx = [i for i, s in enumerate(pub_steps) if str(s.get("uses", "")).startswith("softprops/action-gh-release")]
    verify_idx = [i for i, n in enumerate(names) if "SHA256SUMS" in n]
    if "Push to NuGet.org" not in names or not release_idx:
        problems.append("release.yml `publish` must keep a step named `Push to NuGet.org` and a softprops/action-gh-release step")
    elif names.index("Push to NuGet.org") > release_idx[0]:
        problems.append("release.yml must push to NuGet before creating the GitHub release")
    elif not verify_idx or verify_idx[0] > names.index("Push to NuGet.org"):
        problems.append("release.yml `publish` must verify the hand-over against SHA256SUMS before pushing")
# Trusted publishing: the login step mints the key from the GitHub OIDC token; no NUGET_API_KEY
# secret may survive in the workflow.
for token in ("uses: NuGet/login@", "steps.login.outputs.NUGET_API_KEY"):
    if token not in rel_text:
        problems.append("release.yml missing " + token)
if "secrets.NUGET_API_KEY" in rel_text:
    problems.append("release.yml still reads secrets.NUGET_API_KEY; publishing is OIDC trusted publishing")
for token in ("'release-dry-run'", "'release-assets'", "prerelease: ${{ contains(github.ref_name, '-') }}",
              "assert-packages.ps1", "expected 17 .nupkg"):
    if token not in rel_text:
        problems.append("release.yml missing " + token)

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
    maui_cmd = "dotnet docfx metadata docs/site/docfx.maui.json"
    gated_cmd = "dotnet docfx docs/site/docfx.json --warningsAsErrors"
    if maui_cmd not in docs_text or gated_cmd not in docs_text or docs_text.index(maui_cmd) > docs_text.index(gated_cmd):
        problems.append("docs.yml must run the ungated Qavren.Edge.Maui metadata (docfx.maui.json) before the gated build")

# --- Sub-project 5 (benchmarks) ---
# benchmarks.yml is manual: a benchmark on a PR or a schedule spends hosted minutes (and a model
# download) to measure whichever runner it drew. Its model caches key on the content hash, like
# model-tests, and ci.yml never calls it. The docs page is in the site's navigation, so it cannot
# be dropped silently.
bench_path = w / "benchmarks.yml"
if not bench_path.is_file():
    problems.append("benchmarks.yml is missing (SP5 part 3)")
else:
    bench_text = bench_path.read_text(encoding="utf-8")
    bench = yaml.safe_load(bench_text)
    bench_on = bench.get("on", bench.get(True)) or {}
    if list(bench_on) != ["workflow_dispatch"]:
        problems.append("benchmarks.yml must be workflow_dispatch only (found: %s)" % ", ".join(sorted(bench_on)))
    for token in ("key: qavren-edge-model-${{ env.QAVREN_EDGE_MODEL_SHA256 }}",
                  "key: qavren-edge-chat-model-${{ env.QAVREN_EDGE_CHAT_MODEL_SHA256 }}"):
        if token not in bench_text:
            problems.append("benchmarks.yml model cache key is not the content hash (" + token + ")")
    if bench.get("jobs", {}).get("natives", {}).get("uses") != "./.github/workflows/native.yml" or "actions: read" not in bench_text:
        problems.append("benchmarks.yml must call native.yml from a `natives` job granting `actions: read`, as ci.yml does")
    # The referenced packages have mobile TFMs; without the host-only property the run step's restore
    # demands the android/ios workloads a hosted runner does not have (NETSDK1147, run 35950074339).
    if "dotnet run --project benchmarks/Qavren.Edge.Benchmarks/Qavren.Edge.Benchmarks.csproj -c Release -f net10.0 -p:TargetFrameworks=net10.0" not in bench_text:
        problems.append("benchmarks.yml must run the suite with -f net10.0 -p:TargetFrameworks=net10.0 (host-only restore)")
if "benchmarks.yml" in ci_text or "Qavren.Edge.Benchmarks" in ci_text:
    problems.append("ci.yml references the benchmark suite; benchmarks run from benchmarks.yml on dispatch only")
toc_text = (root / "docs" / "site" / "toc.yml").read_text(encoding="utf-8")
if "href: benchmarks.md" not in toc_text:
    problems.append("docs/site/toc.yml does not link benchmarks.md")

# --- Supply chain ---
# A third-party action is pinned to a full commit SHA, with the release it corresponds to in a
# trailing comment (the form Dependabot rewrites in place). A tag is a mutable pointer its owner,
# or whoever takes the owner's account, can move: every step of release.yml's `release` job can
# mint the OIDC token nuget.org trusts, so a moved `@v0` there publishes under the reserved
# `Qavren.` prefix. GitHub's own `actions/*` and this repository's reusable workflows stay on tags.
pinned_use = re.compile(r"^[\w.-]+/[\w./-]+@[0-9a-f]{40}\s+#\s*v\d+(\.\d+)*\s*$")
for wf in sorted(w.glob("*.yml")):
    for number, line in enumerate(wf.read_text(encoding="utf-8").splitlines(), 1):
        used = re.match(r"\s*(?:-\s+)?uses:\s*(.+)$", line)
        if not used or used.group(1).startswith(("actions/", "./")):
            continue
        if not pinned_use.match(used.group(1)):
            problems.append("%s:%d: third-party action must be pinned as owner/repo@<40-hex sha> # vX.Y.Z (found: %s)" % (wf.name, number, used.group(1).strip()))

# --- Merge queue ---
# The queue tests each queued pull request on a temporary gh-readonly-queue/main/* branch and waits
# for the two required contexts to report there. They report only if ci.yml runs on `merge_group`:
# drop the trigger and every queued pull request sits until the queue's timeout, then is thrown
# out, with nothing red anywhere to say why.
ci_on = ci.get("on", ci.get(True)) or {}
if "merge_group" not in ci_on:
    problems.append("ci.yml must run on merge_group; the merge queue's required checks report only from that event")
mq_path = w.parent / "merge-queue-ruleset.json"
if not mq_path.is_file():
    problems.append(".github/merge-queue-ruleset.json is missing (the merge queue on main, as applied through the rulesets API)")
else:
    mq = json.loads(mq_path.read_text(encoding="utf-8"))
    queue = [r for r in mq.get("rules", []) if r.get("type") == "merge_queue"]
    if len(queue) != 1:
        problems.append("merge-queue-ruleset.json must carry exactly one merge_queue rule")
    else:
        params = queue[0].get("parameters", {})
        # `main` requires linear history, so a queue that merges with a merge commit can never land
        # anything; the project squashes.
        if params.get("merge_method") != "SQUASH":
            problems.append("merge-queue-ruleset.json merge_method must be SQUASH (main requires linear history)")
        # The iOS device lane alone has taken an hour when the hosted macOS runners were busy. A
        # timeout under that throws a green pull request out of the queue.
        if params.get("check_response_timeout_minutes", 0) < 90:
            problems.append("merge-queue-ruleset.json check_response_timeout_minutes must be at least 90 (the iOS device lane can take an hour)")
    if mq.get("conditions", {}).get("ref_name", {}).get("include") != ["~DEFAULT_BRANCH"]:
        problems.append("merge-queue-ruleset.json must target exactly the default branch")

win = ci["jobs"]["device-tests-windows"]["runs-on"]
print("\n".join(problems) if problems else "OK: gates, 4 device lanes, JUnit, win-arm64, native artifact cache + reuse, merge queue, SBOM and native release assets all present (windows lane on %s)" % win)
sys.exit(1 if problems else 0)
