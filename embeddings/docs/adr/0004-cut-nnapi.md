# 4. Cut NNAPI from `EdgeExecutionProvider`

Date: 2026-09-11

## Status

Accepted

## Context

`Qavren.Edge.Onnx` needs exactly one execution-provider implementation that
compiles for every SP2 target framework — `net10.0`, `net10.0-android`,
`net10.0-ios`, `net10.0-maccatalyst` and desktop `net10.0`. The only ONNX
Runtime API that is not TFM-gated is the portable overload
`AppendExecutionProvider(string providerName, Dictionary<string,string>
providerOptions)`. The typed helpers are not usable for this: `
AppendExecutionProvider_CoreML` calls a native entry point Microsoft
deprecated in ORT 1.20.0, and `AppendExecutionProvider_Nnapi` is compiled
only inside `#if __ANDROID__` and throws `NotSupportedException` on every
other platform — reaching it from shared code would require conditional
compilation, which is exactly the "one EP implementation compiled for every
TFM" property this package is built on (§9.2).

ORT documents the portable string overload as accepting exactly `"QNN"`,
`"SNPE"`, `"XNNPACK"`, `"CoreML"` and `"AZURE"`. **NNAPI is not among them.**
Of the five, CoreML and XNNPACK are the two compiled into the shipped mobile
natives and relevant to an embedding encoder.

Secondarily, and not the decisive reason: Google deprecated NNAPI starting
with Android 15 and expects most devices to fall back to CPU regardless.

## Decision

`EdgeExecutionProvider` is exactly `{ Cpu, XnnPack, CoreMl }`. NNAPI is not a
member of the enum and is never appended, on Android or anywhere else.

## Consequences

Android embedding inference runs XNNPACK → CPU, never NNAPI, even on devices
where an NNAPI accelerator would otherwise be used by other frameworks.

**Revisit trigger:** either ORT exposes NNAPI through the portable string
overload, or ships a supported Android execution-provider successor to NNAPI
through the .NET package. Until then, adding NNAPI back would require
`#if __ANDROID__`-gated code in a package that currently has none, which is a
structural change, not a one-line addition.
