# 3. Pin `Microsoft.ML.OnnxRuntime` at 1.30.0

Date: 2026-09-11

## Status

Accepted

## Context

`Qavren.Edge.Onnx` needs an ONNX Runtime version whose managed and native
assets restore cleanly across every SP2 target framework
(`net10.0`, `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst`) and whose
mobile `AppendExecutionProvider(string, Dictionary<string,string>)` surface is
stable enough to build a single, TFM-independent execution-provider
implementation on top of it (ADR 0004).

1.30.0 was one day old at design time. It is the version whose asset
resolution, managed surface and `runtimes/` contents were actually measured
on this box (Environment ground truth) — `net10.0-ios` resolves
`lib/net9.0-ios18.0`, `net10.0-android` resolves `lib/net9.0-android35.0`,
`net10.0-maccatalyst` resolves `lib/net9.0-maccatalyst18.0`, and `net10.0`
resolves `lib/net8.0` (never `netstandard2.0`). Its `runtimes/` directory
lists `android`, `ios`, `linux-arm64`, `linux-x64`, `osx-arm64`, `win-arm64`
and `win-x64` — no `osx-x64`, which is why `OnnxUnsupportedRuntime` (5006) is
a real condition rather than a defensive guess.

The **only** rollback available on NuGet is 1.29.0. Upstream's 1.29.1 was
never published to NuGet, so there is no intermediate rung between 1.29.0 and
1.30.0 — a regression discovered after shipping means a two-minor step back,
not a patch bump.

## Decision

Pin `Microsoft.ML.OnnxRuntime` to exactly `1.30.0` in `Directory.Packages.props`.
The four device lanes in CI (Android, iOS, Mac Catalyst, and the Windows host
lane) are the soak for this pin — there is no separate staging period before
it reaches every TFM.

## Consequences

A regression found in 1.30.0 rolls back to 1.29.0, skipping any 1.29.1-era
fix that only ever shipped upstream and not to NuGet. There is no smaller
step to fall back to.

The pin is re-evaluated at 1.0 — when the vector store and embeddings
packages themselves leave preview — rather than left to drift on a routine
Dependabot bump; any bump proposal should link back to this ADR instead of
silently superseding it.

## Re-evaluation at 1.0 (2026-09-23)

Checked against nuget.org: `1.30.0` is still the newest published version of
`Microsoft.ML.OnnxRuntime` — there is nothing to roll forward to. The four
device lanes (Android, iOS, Mac Catalyst, Windows host) and the nightly
tier-3 lane have soaked this exact pin since 2026-09-11, unchanged. The pin
stands for 1.0.

The next review trigger is a new ORT release, not the passage of time. When
one ships, it is evaluated through the tier-0 smoke and the nightly lane —
never adopted on a Dependabot bump alone, for the same reason this ADR's
original Decision gives: `Directory.Packages.props` is the single point
where the pin is asserted, and a bump there has to carry the same
four-device-lane-plus-nightly evidence this entry records, not just a green
Dependabot PR.
