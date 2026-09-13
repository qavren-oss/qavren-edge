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
if nat_text.count("actions/cache/restore@v4") != 3:
    problems.append("native.yml reuse job must restore all three OS caches")
# A cache entry can be absent while the artifact still exists (an interrupted save, an eviction).
# The reuse job must repair that instead of taking a managed-only PR red.
if nat_text.count("reuse-native-artifact.sh") != 3:
    problems.append("native.yml reuse job must fall back to the last good run's artifact for all three OS legs")
if nat_text.count("actions/cache/save@v4") != 3:
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
if ci_text.count("assert-packages.ps1") != 1:
    problems.append("ci.yml Windows pack lane must run foundation/tools/ci-checks/assert-packages.ps1 exactly once")
if not (root / "foundation" / "tools" / "ci-checks" / "assert-packages.ps1").is_file():
    problems.append("foundation/tools/ci-checks/assert-packages.ps1 is missing")

# release.yml: workflow_dispatch is a dry run. Exactly the two publishing steps are guarded on a
# v* tag, the dry run uploads the would-be assets, prereleases are flagged from the tag name, and
# the release job proves package metadata before anything is pushed.
if rel_text.count("if: startsWith(github.ref, 'refs/tags/v')") != 3:
    problems.append("release.yml must guard exactly three steps (NuGet login, NuGet push, GitHub release) on startsWith(github.ref, 'refs/tags/v')")
# Trusted publishing: the login step mints the key from the GitHub OIDC token, which needs
# id-token: write on the release job; no NUGET_API_KEY secret may survive in the workflow.
for token in ("uses: NuGet/login@v1", "id-token: write", "steps.login.outputs.NUGET_API_KEY"):
    if token not in rel_text:
        problems.append("release.yml missing " + token)
if "secrets.NUGET_API_KEY" in rel_text:
    problems.append("release.yml still reads secrets.NUGET_API_KEY; publishing is OIDC trusted publishing")
for token in ("name: release-dry-run", "prerelease: ${{ contains(github.ref_name, '-') }}",
              "assert-packages.ps1", "expected 17 .nupkg"):
    if token not in rel_text:
        problems.append("release.yml missing " + token)
rel_steps = rel["jobs"]["release"]["steps"]
names = [str(s.get("name", s.get("uses", ""))) for s in rel_steps]
release_idx = [i for i, s in enumerate(rel_steps) if str(s.get("uses", "")).startswith("softprops/action-gh-release")]
if "Push to NuGet.org" not in names or not release_idx:
    problems.append("release.yml must keep a step named `Push to NuGet.org` and a softprops/action-gh-release step")
elif names.index("Push to NuGet.org") > release_idx[0]:
    problems.append("release.yml must push to NuGet before creating the GitHub release")

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

win = ci["jobs"]["device-tests-windows"]["runs-on"]
print("\n".join(problems) if problems else "OK: gates, 4 device lanes, JUnit, win-arm64, native artifact cache + reuse, SBOM and native release assets all present (windows lane on %s)" % win)
sys.exit(1 if problems else 0)
