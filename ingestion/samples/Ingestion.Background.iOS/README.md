# Ingestion.Background.iOS

The `BGTaskScheduler` wiring for one `Qavren.Edge.Ingestion` pass: a `BGProcessingTaskRequest`
for the overnight-on-charge case, and the iOS 26 `BGContinuedProcessingTask` for a run the user
started and wants to keep going after leaving the app.

This is a **documentation project that compiles** (plan adjustment 8): a `net10.0-ios` class
library, `IsPackable=false`, in `QavrenEdge.slnx` so restore, format and pack on the
`windows-2025` leg catch an API rename. It ships nothing. Copy `IngestionBackgroundTasks.cs`
into the app that owns `Info.plist`, because the identifiers it registers must be listed there.

## Info.plist

```xml
<key>UIBackgroundModes</key>
<array>
  <string>processing</string>
</array>
<key>BGTaskSchedulerPermittedIdentifiers</key>
<array>
  <string>com.example.app.ingestion.processing</string>
  <!-- iOS 26 continued-processing tasks may use a wildcard suffix. -->
  <string>com.example.app.ingestion.*</string>
</array>
```

Replace `com.example.app` with the bundle id. The string in the plist and
`IngestionBackgroundTasks.ProcessingTaskIdentifier` must match character for character - a
mismatch is `BGTaskSchedulerErrorCode.NotPermitted` at submit time and nothing at build time.

## Wiring

```csharp
// AppDelegate (or MauiUIApplicationDelegate)
public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
{
    var ok = base.FinishedLaunching(application, launchOptions);
    var services = IPlatformApplication.Current!.Services;
    // Register BEFORE this method returns; a later registration is rejected by the system.
    IngestionBackgroundTasks.Register(
        services,
        () => IngestionSource.Folder(Path.Combine(FileSystem.AppDataDirectory, "docs"), sourceId: "docs"));
    return ok;
}

public override void DidEnterBackground(UIApplication application)
{
    var logger = IPlatformApplication.Current!.Services
        .GetRequiredService<ILoggerFactory>().CreateLogger("Background");
    IngestionBackgroundTasks.Schedule(logger);   // the bool is logged inside; see below
}
```

The source is a **factory**, not an instance. A folder under `AppDataDirectory` is the simple
case; a user-picked location is a security-scoped URL that must be persisted as a bookmark and
re-resolved per launch, with `StartAccessingSecurityScopedResource` around each open (spec 11.3).

## `Submit` is checked, because "the OS said no" is the normal path

`BGTaskScheduler.Submit(request, out NSError? error)` returns `bool`. The sample branches on it
and names the action for each `BGTaskSchedulerErrorCode`:

| Code | Value | Meaning | Do |
| --- | --- | --- | --- |
| `Unavailable` | 1 | Background App Refresh is off for the app or device-wide. | Nothing - a user setting, not an error to retry. Tell the user where the switch is if it matters. |
| `TooManyPendingTaskRequests` | 2 | The scheduler's pending queue is full. | Cancel or coalesce before resubmitting. |
| `NotPermitted` | 3 | The identifier is missing from `BGTaskSchedulerPermittedIdentifiers`, or `UIBackgroundModes` lacks `processing`. | A build mistake; fix `Info.plist`. The log says so. |
| `ImmediateRunIneligible` | 4 | The request asked to run immediately and the system declined. | Resubmit without that expectation. |

A submit whose return value is ignored is how a background task silently never runs.

## The processing task

`Schedule` submits a `BGProcessingTaskRequest` with `RequiresExternalPower = true` (ingestion is
CPU and disk; ask for the overnight charge slot) and `RequiresNetworkConnectivity = false` (a
local-only task that claims it needs connectivity waits for Wi-Fi it never uses). Set both
explicitly; the defaults are both `false`.

When the system launches it, the handler:

1. **Re-submits first**, so a run killed mid-way does not also lose the chain.
2. Sets the `ExpirationHandler` to `pipeline.RequestStop("bgtask:expiring")` and then
   `SetTaskCompleted(false)`. The stop lands on the run's next committed boundary; the task is
   completed immediately, because the system wants an answer now and the state tables carry the
   resume point (spec 10.3).
3. Runs the pipeline with `IngestionBudget.Background` (5 minutes of wall time) and completes the
   task with `Outcome == Completed`. Completion happens exactly once whichever of the expiration
   handler or the run finishes first.

**Apple publishes no guaranteed duration for anything.** Do not assume the community-measured
~30 s for the expiration warning or any particular slot length for a processing task; read
`UIApplication.SharedApplication.BackgroundTimeRemaining` when a decision depends on it, and let
the budget plus the expiration handler end the run on a committed boundary rather than counting
on time you were not promised.

## iOS 26: `BGContinuedProcessingTask`

`RegisterContinued` / `ScheduleContinued` are **`#if IOS`-guarded** and marked
`[SupportedOSPlatform("ios26.0")] [UnsupportedOSPlatform("maccatalyst")]`:
`BGContinuedProcessingTask`, `BGContinuedProcessingTaskRequest` and
`BGContinuedProcessingTaskRequestResources` ship in `Microsoft.iOS.dll` only - not Mac Catalyst,
not tvOS - while the rest of `BackgroundTasks` is in all three.

The request is submitted from a **foreground** user gesture with a title and subtitle the system
shows immediately, `Strategy = Fail` (a user who tapped "index now" wants to know now, not later),
and `RequiredResources = Default` (no GPU claim).

The bridge from `IProgress<IngestionProgress>` to the task's `NSProgress`: `IngestionProgress`
carries counts and no denominator (spec 10.4), so `TotalUnitCount` is `-1` - **indeterminate** -
`CompletedUnitCount` tracks `DocumentsSeen`, and `LocalizedDescription` carries the counts. The
bridge is not optional: the system ends a continued task that reports no progress. The run takes
no budget here; the expiration handler is what ends it.

## Debugging

The processing task can be forced from the debugger with the app paused:

```text
e -l objc -- (void)[[BGTaskScheduler sharedScheduler] _simulateLaunchForTaskWithIdentifier:@"com.example.app.ingestion.processing"]
```

and its expiration with `_simulateExpirationForTaskWithIdentifier:`. Both work on a device only.
