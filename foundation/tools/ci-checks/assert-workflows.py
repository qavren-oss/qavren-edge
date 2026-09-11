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
if "actions: read" not in ci_text:
    problems.append("ci.yml must grant the natives job `actions: read`; a called workflow cannot widen the caller's token")
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

win = ci["jobs"]["device-tests-windows"]["runs-on"]
print("\n".join(problems) if problems else "OK: gates, 4 device lanes, JUnit, win-arm64, native artifact cache + reuse, SBOM and native release assets all present (windows lane on %s)" % win)
sys.exit(1 if problems else 0)
