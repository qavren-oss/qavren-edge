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

Adjustment 11's suppression rule lands here rather than repo-wide, and
**exactly one suppression exists**:
`[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode")]`
on `ChatDiagnosticsContributor.ReadGenAiManagedAsset`
(`chat/src/Qavren.Edge.Chat.Onnx/Internal/ChatDiagnosticsContributor.cs`).
`Assembly.GetReferencedAssemblies()` is the only way to read the
`System.Runtime` reference that separates the five GenAI managed assets —
none of them carries a `TargetFrameworkAttribute` (adjustment 2). Under
trimming the failure mode is a less-informative `genAiManagedAsset`
diagnostics string: the read sits inside `Safe(...)`, and no behaviour
depends on it. The build is otherwise clean of `IL2026`/`IL3050` under
`TreatWarningsAsErrors` on all four target frameworks.

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
