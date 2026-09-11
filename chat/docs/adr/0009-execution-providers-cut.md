# 9. Execution providers cut

Date: 2026-09-11

## Status

Accepted

## Context

ORT GenAI's canonical provider set is CPU / cuda / DML / QNN / WebGPU /
OpenVINO / VitisAI / RyzenAI / NvTensorRtRtx. It has **no CoreML execution
provider and no NNAPI execution provider** — SP2's `EdgeExecutionProvider
{ Cpu, XnnPack, CoreMl }` (ADR 0004 in `embeddings/docs/adr/`) says nothing
about chat and is deliberately not reused here.

The measured iOS targets file shows why `WeakFrameworks="CoreML"` is easy to
misread as a provider:

```xml
<NativeReference Include="...runtimes\ios\native\onnxruntime-genai.xcframework.zip">
  <Kind>Static</Kind><IsCxx>True</IsCxx><SmartLink>True</SmartLink>
  <ForceLoad>True</ForceLoad><LinkerFlags>-lc++</LinkerFlags><WeakFrameworks>CoreML</WeakFrameworks>
</NativeReference>
```

`WeakFrameworks` is a **link-time weak reference** — it lets the binary link
against a framework that may not be present at runtime on every OS version —
not a provider registration. Nothing in GenAI's session options names CoreML.

Two other escape hatches were checked and both dead-end for this package:

- **DirectML** dead-ends at GenAI 0.14.1 / ORT-DML 1.23.0; this repo pins
  GenAI 0.15.2 / ORT 1.30.0, past the versions DirectML was last verified
  against.
- **QNN** packaging was removed from the GenAI native package in 0.14.0.
  `OgaRegisterExecutionProviderLibrary` exists in `ort_genai_c.h` — the
  header ships in `build/native/include/` — but the managed surface dump of
  `Microsoft.ML.OnnxRuntimeGenAI.Managed` 0.15.2 has **no such method**. It
  is a C API with no C# binding.

## Decision

Sub-project 4 v1 runs **CPU only, everywhere** — Windows, Linux, Android,
iOS and Mac Catalyst all get the CPU int4 export and nothing else. The
only escape hatch is `EdgeChatOptions.ConfigOverlayJson` (§9.1's single
`Config.Overlay` call): naming any provider other than CPU on a mobile TFM
is refused with `ChatExecutionProviderUnsupported` (7009), naming the
provider, rather than being silently ignored by the native config parser
or crashing at model-load time.

If a future AOT/trimming analyzer pass (adjustment 11) forces a suppression
around GenAI's own lack of trim annotations, that suppression is recorded
here, at the call site, rather than silenced repo-wide — no such
suppression exists as of this task.

## Consequences

Chat inference is CPU-bound on every mobile platform in v1; there is no
GPU or NPU acceleration path for chat, unlike embeddings' XNNPACK/CoreML
options. A desktop or server host that wants DML/QNN/cuda would need a
GenAI version bump and a same-day re-verification of the ORT/GenAI pin
compatibility matrix, not a config change.

**Revisit trigger:** GenAI ships a CoreML or NNAPI execution provider, or
`OgaRegisterExecutionProviderLibrary` gets a C# binding on the managed
surface. Until then, adding a mobile GPU/NPU path would require native
interop this package does not currently carry.
