# 13. Apple platform floor rises to 15.4

Date: 2026-09-11

## Status

Accepted

## Context

`Microsoft.ML.OnnxRuntimeGenAI.Managed` 0.15.2's asset resolution, measured
on this box, lands the iOS managed asset at `lib/net9.0-ios15.4` — that is
GenAI's own published TFM, not a floor this suite chose. Its xcframework
slices (`ios-arm64`, `ios-arm64_x86_64-simulator`,
`ios-arm64_x86_64-maccatalyst`) are built at that same floor, so a project
declaring a lower `SupportedOSPlatformVersion` would be linking a 15.4-built
slice into a lower deployment target.

Mac Catalyst inherits 15.4 too, and for a specific mechanical reason
rather than by GenAI declaring `net9.0-maccatalyst15.4`: Mac Catalyst
resolves the iOS xcframework slice through the .NET SDK's RID-graph
fallback rather than shipping its own distinct managed asset at a
different floor, so whatever floor the iOS slice was built at is the floor
Mac Catalyst inherits. Android is unaffected — its AAR declares
`minSdkVersion 24`, already SP2's floor, so nothing there needs to move.

Whether Mac Catalyst's RID-graph fallback actually **links**, as opposed
to merely resolving at restore time, is closed by Task 1.5's tier-0 smoke
(spec §19 item 1(a)) rather than by this ADR — this decision is about
where the floor has to sit *if* it links, and the fallback (adjustment
21's third bullet) is what fires if it does not.

## Decision

The Apple platform floor rises to **15.4** — `SupportedOSPlatformVersion`
15.4 for both `ios` and `maccatalyst` — everywhere GenAI's assemblies are
in the closure: `Qavren.Edge.Chat.Onnx`, `Qavren.Edge.Chat.Tests`, and
`foundation/tests/Qavren.Edge.DeviceTests` (adjustment 8 — that project's
Apple floor rises unconditionally, not contingently, because the device
host is currently `ios 15.0` / `maccatalyst 15.0` and GenAI's asset group
is already known to be 15.4 before any link is attempted).
`chat/README.md`'s supported-minimum statement follows this floor.

## Consequences

Any app that adopts `Qavren.Edge.Chat.Onnx` on iOS or Mac Catalyst must
raise its own deployment target to 15.4, above SP1's and SP2's 15.1 floor.
An app that stays on 15.1 cannot add chat without also raising that
target. `SupportedOSPlatformVersion` plays no part in NuGet asset
resolution itself — raising it adds no new versioned TFM — so this is a
deployment-target consequence only, not a restore-time one.

**Revisit trigger:** a future `Microsoft.ML.OnnxRuntimeGenAI.Managed`
release publishes its iOS/Mac Catalyst managed asset and xcframework at a
lower TFM/deployment floor. Until then, 15.4 is not a choice this suite
made — it is the floor GenAI's own shipped asset dictates.
