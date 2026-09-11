# 11. Guidance is unverified, not unavailable

Date: 2026-09-11

## Status

Accepted

## Context

At tag v0.15.2, `CreateGuidanceLogitsProcessor` returns `nullptr` with at
most a log line when `USE_GUIDANCE` is off at build time — it does not
throw; the throwing behaviour the public docs describe landed only on
unreleased `main`. `tools/ci_build/github/apple/default_full_ios_framework_build_settings.json`
passes `--parallel --build_apple_framework --skip_tests --skip_wheel` and
**no** `--use_guidance`, so the shipped mobile natives (iOS, Mac Catalyst,
Android) are built without constrained decoding at all. The desktop
Windows/Linux/macOS core pipelines **do** pass `--use_guidance`.

Handing a caller's `ChatResponseFormatJson` straight to
`GeneratorParams.SetGuidance` on a mobile build would silently produce
unconstrained output labelled as schema-constrained — the failure mode
this ADR exists to prevent. An unconditional throw on every platform would
be wrong too, since desktop natives genuinely support it.

`GeneratorParams.SetGuidance` also has a different, three-parameter
signature than the spec originally assumed:
`SetGuidance(string type, string data, bool enableFFTokens = false)`. The
positive-control probe below passes exactly two arguments and never passes
`enableFFTokens: true` — fast-forward tokens change what the model emits,
and a probe that changes the thing it is trying to measure is not a
control.

## Decision

Ship `EdgeGuidancePolicy` with three values (`Disabled`, `PreferNative`,
`RequireNative`) and `Disabled` as the default. At startup, run a
**positive-control probe**: generate at most eight tokens under a schema
admitting exactly one string, and compare the decoded output against that
string — never by catching an exception, since the mobile failure mode is
silent. Publish the result as the diagnostics key `guidanceEnforced`
(`Enforced` / `NotEnforced` / `Unknown`).

Under the default `Disabled`, a `ChatResponseFormatJson` request throws
`ChatGuidanceUnavailable` (7103) unconditionally — the policy decides, and
the probe result is not consulted. The probe governs only the two opt-in
policies: `RequireNative` throws the same 7103 naming the probe result when
it did not come back `Enforced`, and `PreferNative` falls back to an
unconstrained turn recorded in `guidanceEnforced` and `UnhonouredOptions`.
Never a silent claim of constraint.

## Consequences

A consumer asking for JSON-schema-constrained output on iOS, Mac Catalyst
or Android today gets either a documented refusal or a documented
unconstrained turn, never output that looks constrained but is not. On
desktop, where the natives are built with `--use_guidance`, the same probe
is expected to report `Enforced`, and the policy composes unchanged.

**Revisit trigger:** a future ORT GenAI mobile release ships natives built
with `--use_guidance`, at which point the probe's own result flips to
`Enforced` on that platform with no code change required — the mechanism
already accounts for platform variance, only the default policy value
would be worth revisiting.
