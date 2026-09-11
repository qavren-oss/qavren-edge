# Tier 0 - the five-target link-and-generate smoke

Spec 16.0 / spec 19 item 1. **THROWAWAY** - this whole `chat/tools/tier0-genai-smoke/` directory
and `.github/workflows/tier0-genai-smoke.yml` exist only to answer three go/no-go questions once,
before sub-project 4 has a single line of API, and both are deleted in the closing PR.

- `Tier0Smoke.cs` is the one shared smoke body, linked (never copied) into both projects below, so
  the console leg and the device leg cannot drift into proving different things.
- `Qavren.Edge.Tier0Smoke/` is the `net10.0` console leg - the Windows and Linux targets.
- `Qavren.Edge.Tier0Smoke.Device/` is the MAUI device head - the android, ios, maccatalyst and
  windows targets, run through `dotnet build -t:VSTest` the way `ci.yml`'s device lanes do.

Neither project is in `QavrenEdge.slnx`, neither references anything under `Qavren.Edge.*`, and
neither is touched by `dotnet format` or the wave-8 gate.

## If `tier0-maccatalyst` fails with a `DllNotFoundException` (spec 16.0 item 1(a) says no)

1. delete `net10.0-maccatalyst` from `<TargetFrameworks>` in
   `chat/src/Qavren.Edge.Chat.Onnx/Qavren.Edge.Chat.Onnx.csproj` and
   `chat/tests/Qavren.Edge.Chat.Tests/Qavren.Edge.Chat.Tests.csproj`, and from this device head;
2. delete the `'net10.0-maccatalyst' = 'lib/net9.0-maccatalyst14.0/…'` row from `ci.yml`'s GenAI
   asset assertion and its TFM from the ORT floor guard's list (Task 2.3);
3. delete `"lib/net9.0-maccatalyst14.0"` from `assert-workflows.py`'s SP4 token list (Task 2.3);
4. delete the `maccatalyst` `SupportedOSPlatformVersion` line from the two SP4 csprojs, and drop
   Mac Catalyst from Task 7.1's and Task 8.1's TFM loops;
5. state in `chat/README.md`: **"Mac Catalyst has no chat in v1 - ORT GenAI ships no Mac Catalyst
   native and the SDK's RID-graph fallback to the iOS slice does not load."**
6. record it here as adjustment 21(a) in the plan's **Spec adjustments**.

**If `tier0-android` or `tier0-ios` fails**, the sub-project has no mobile chat and the whole TFM
table is reopened - stop and escalate rather than editing anything.
