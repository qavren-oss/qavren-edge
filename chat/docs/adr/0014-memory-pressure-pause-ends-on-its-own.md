# 14. A memory-pressure pause ends on its own

Date: 2026-11-11

## Status

Accepted

## Context

Spec section 14.2 turns new turns off on `MemoryPressure(Moderate)` and
`(Critical)` and back on at `Resumed`, and nowhere else. Sub-project 2's
observer latches the level on `IEdgeResourceMonitor.LastPressure`, which is
likewise cleared only by `Resumed`, and the chat host refuses to start a model
load while that latch reads `Critical`.

`Resumed` follows a trip to the background. An iOS memory warning does not
involve one: it is delivered to the app in the **foreground**, most often
while a model is loading, which is exactly when this package is the largest
thing in the process. The app sheds its caches and carries on. From that moment
every turn was refused with `ChatBusy` (7105) and the remediation "retry after
the app is resumed" — which a user can satisfy only by leaving the app and
coming back. Nothing in the session ever cleared it.

No platform sends the event that would: `EdgeMemoryPressure` is
`{ Low, Moderate, Critical }` with no "relieved" member, and the lifecycle hub
has no such callback to raise.

## Decision

A memory-pressure pause is tracked separately from the on/off switch that
`Stopping` and `Resumed` drive, and it ends at whichever comes first:

- the next `Resumed`, as before; or
- the first time it is read at least **30 seconds** after the last pressure
  event while the device does not report itself low on memory
  (`EdgeResourceSnapshot.IsLowMemory` is not `true`).

A second pressure event restarts the 30 seconds. Once the pause has ended this
way the host also stops honouring the monitor's `Critical` latch when asked to
load: the latch records that pressure happened, not that it is still
happening. A latch the host never saw set — nothing paused it — is still
honoured.

Ending the pause is not a promise that a model fits. The memory budget decides
that, against a fresh reading, when the next turn loads one, and refuses with
`ChatInsufficientMemory` (7005) if it does not.

The 30 seconds is an internal constant (`ChatPressureGate.Cooldown`), not an
option: the public surface is frozen at 1.0.0 and one number nobody has yet
asked to change does not justify widening it.

## Consequences

Chat recovers from a foreground memory warning without the user doing
anything. A turn sent inside the 30 seconds still gets 7105; its remediation
now says to retry shortly rather than to background the app.

On Android `IsLowMemory` is `ActivityManager.MemoryInfo.lowMemory`, so the
pause holds for as long as the system says it is low. On Apple platforms the
monitor reports no such flag, so the 30 seconds alone ends the pause and the
budget's `os_proc_available_memory()` reading is what stands between the next
turn and a second warning. The worst case is one refused or reloaded model per
30 seconds, not a loop: a new warning restarts the pause.

`Stopping` is unchanged: it turns turns off for good.

**Revisit trigger:** the lifecycle hub gains a "pressure relieved" event
(Apple's dispatch memory-pressure source does report a return to normal), at
which point the pause should end on that event and the timer becomes the
fallback; or a consumer needs a different interval, at which point it becomes
an option in a minor release.
