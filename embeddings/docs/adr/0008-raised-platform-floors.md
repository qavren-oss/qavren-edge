# 8. Raise SP2's platform floors above SP1's — android 24.0, Apple 15.1

Date: 2026-09-11

## Status

Accepted

## Context

SP1's merged projects declare `SupportedOSPlatformVersion` `21.0` on Android
and `15.0` on iOS/Mac Catalyst. SP2's own projects need to declare floors
that match what ONNX Runtime's shipped natives actually require, sourced
from ORT's own build settings rather than chosen by feel:

- `default_full_aar_build_settings.json` declares `android_min_sdk_version: 24`.
- `default_full_apple_framework_build_settings.json` declares
  `--apple_deploy_target=15.1`.

Two independent questions had to be kept separate, and the plan's §19 item 1
probe answered the first before any SP2 code was written: does raising
`SupportedOSPlatformVersion` change which managed asset NuGet restores?
Measured on this box — a throwaway multi-TFM class library referencing
`Microsoft.ML.OnnxRuntime` 1.30.0, restored once at SP1's `21.0`/`15.0` and
once at SP2's `24.0`/`15.1` — the resolved asset paths were byte-identical
both times. `TargetPlatformVersion` (taken from the installed platform SDK,
independent of this repo's floors) drives asset selection; `SupportedOSPlatformVersion`
does not participate in restore at all. So raising the floor is free in
restore terms, and its entire effect is on the Android manifest merge and the
Apple linker's deployment target — exactly the second question, which is a
build/link-time concern rather than a restore-time one.

One piece of evidence remains owed rather than in hand: the `LC_BUILD_VERSION`
of the xcframework's `ios-arm64_x86_64-maccatalyst` slice specifically (read
via `otool -l` or `vtool -show` on a Mac), because ORT's Apple build-settings
file defines only its iphoneos, iphonesimulator and macosx archs and says
nothing about which one the Mac Catalyst slice was actually cut from.

## Decision

SP2's own projects (`Qavren.Edge.Onnx`, `Qavren.Edge.Embeddings.Onnx`,
`Qavren.Edge.VectorData`, and their test/tool projects) declare
`SupportedOSPlatformVersion` `24.0` for `net10.0-android` and `15.1` for both
`net10.0-ios` and `net10.0-maccatalyst`. No SP1 csproj is edited to match —
SP1's own five merged projects keep `21.0`/`15.0`.

## Consequences

The README states the supported minimum as **Android 24, iOS 15.1, Mac
Catalyst 15.1** — ORT's floors, not a number Qavren chose independently, and
higher than SP1's own stated minimums for an app that has not added
embeddings.

If the still-owed `LC_BUILD_VERSION` read on the Mac Catalyst slice returns
anything outside the `15.1` family, this ADR's floor is wrong for that
platform specifically, and the fix cascades: §4.1's `SupportedOSPlatformVersion`
block, the device-test host's floor, and the README's stated minimum all move
together rather than independently drifting.
