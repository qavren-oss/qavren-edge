# Ingestion.Background.Android

A WorkManager `Worker` that runs one `Qavren.Edge.Ingestion` pass inside a foreground service of
type `dataSync`, plus the literal `Service.OnTimeout` wiring for Android 15's six-hour wall.

This is a **documentation project that compiles** (plan adjustment 8): a `net10.0-android` class
library, `IsPackable=false`, in `QavrenEdge.slnx` so restore, format and pack on the
`windows-2025` leg catch an API rename. It ships nothing. Copy the two classes into the app that
owns the manifest, because every line here implies a permission or a manifest entry a library
cannot add for you.

## Files

| File | What it shows |
| --- | --- |
| `IngestionWorker.cs` | The recommended shape. `SetForegroundAsync(new ForegroundInfo(id, notification, (int)ForegroundService.TypeDataSync))`, the three failure modes below, `OnStopped` mapping WorkManager's stop reason to a pipeline `RequestStop`. |
| `IngestionForegroundService.cs` | The `Service.OnTimeout(int, ForegroundService)` override spelled out, for an app that hosts the run in its own foreground service instead of behind WorkManager. Also carries the `[assembly: UsesPermission(...)]` attributes. |

**Spelling:** the enum members are `ForegroundService.TypeDataSync` (1) and
`ForegroundService.TypeMediaProcessing` (8192). `ForegroundService.DataSync` does not exist and
will not compile.

## Wiring

```csharp
// MauiProgram.CreateMauiApp, after builder.Build() - or MainApplication.OnCreate.
var app = builder.Build();
#if ANDROID
Ingestion.Background.Android.IngestionWorker.Configure(
    app.Services,
    () => IngestionSource.Folder(Path.Combine(FileSystem.AppDataDirectory, "docs"), sourceId: "docs"));
Ingestion.Background.Android.IngestionWorker.Enqueue(Android.App.Application.Context);
#endif
return app;
```

`Configure` must run before the first Worker can: WorkManager constructs Workers itself through
the default `WorkerFactory` with the `(Context, WorkerParameters)` constructor, so there is no
constructor injection and the container is handed over once, statically. `Application.OnCreate`
runs before WorkManager starts anything in a fresh process, so a periodic request that wakes the
app cold still finds its configuration.

`Enqueue` submits a unique periodic request (12 h, charging, battery-not-low) with policy
`KEEP`; calling it on every launch is harmless.

## Manifest

The attributes in `IngestionForegroundService.cs` merge into a referencing app's manifest. An
app that manages `AndroidManifest.xml` by hand adds:

```xml
<uses-permission android:name="android.permission.FOREGROUND_SERVICE" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE_DATA_SYNC" />
<!-- API 33+: the foreground notification is not shown without it. Request it at runtime. -->
<uses-permission android:name="android.permission.POST_NOTIFICATIONS" />
```

and, for the WorkManager path on **API 34+**, the service type must also be declared on
WorkManager's own service, or `SetForegroundAsync` throws
`MissingForegroundServiceTypeException`:

```xml
<!-- inside <application>; xmlns:tools="http://schemas.android.com/tools" on the root -->
<service
    android:name="androidx.work.impl.foreground.SystemForegroundService"
    android:foregroundServiceType="dataSync"
    tools:node="merge" />
```

`IngestionForegroundService` declares its own type through
`[Service(ForegroundServiceType = ForegroundService.TypeDataSync)]`; nothing to add for it.

The sample's `SupportedOSPlatformVersion` is **29**: `TypeDataSync` and the typed
`StartForeground` overload are API 29+, and the repo's `CA1416` is an error. An app with a lower
minimum guards the promotion with `OperatingSystem.IsAndroidVersionAtLeast(29)` and runs
unpromoted below it - the same path failure mode 1 takes.

## The three failure modes, in the order a consumer meets them

### 1. Android 12+ (API 31) refuses the promotion

An app in the background may not start a foreground service. A Worker WorkManager scheduled
while the app was backgrounded therefore gets `ForegroundServiceStartNotAllowedException` from
`SetForegroundAsync` - directly, or as the cause of the future's `ExecutionException`, depending
on the WorkManager version; `IngestionWorker.IsStartNotAllowed` checks both.

The sample **catches it, logs, and runs unpromoted** under the plain Worker's ~10-minute cap with
`IngestionBudget.Background` (5 minutes of wall time, nothing else). A budget suspends on a
committed boundary, so the run that gets cut short at 5 minutes has lost nothing and the next run
resumes from the state tables - which is the shape SP3 exists to make safe. The job is not failed.

Alternatives when the promotion matters:

- **An expedited request** - `OneTimeWorkRequest.Builder(...).SetExpedited(OutOfQuotaPolicy.RunAsNonExpeditedWorkRequest)`.
  Expedited work may promote itself from the background, subject to a quota.
- **A foreground-initiated start** - `IngestionForegroundService.Start(context)` from an activity,
  a notification action or any user gesture. That path may always promote, and it is the one that
  has no Worker cap at all.

### 2. API 36 (the .NET 10 default) charges jobs started from a foreground service to the job quota

On Android 16 a background job kicked off *from* a foreground service is billed to the app's job
quota rather than running freely; Google points user-triggered transfers at user-initiated
data-transfer jobs instead. So the sample does the ingestion **inside** the Worker - `DoWork`
blocks on `RunAsync` - rather than having the Worker promote itself and then enqueue further jobs
it would be charged for. Nothing in `DoWork` schedules anything.

### 3. The Android 15 (API 35) wall

`dataSync` gets **six hours per rolling 24**, tracked separately from `mediaProcessing` and reset
only when the app comes to the foreground. On expiry the system calls
`Service.OnTimeout(int startId, ForegroundService fgsType)` and the process has a few seconds to
`stopSelf()` before it is killed with `RemoteServiceException`.

- **Behind WorkManager** the service is WorkManager's `SystemForegroundService`, and WorkManager
  2.10+ turns its `onTimeout` into `ListenableWorker.onStopped` with stop reason
  `WorkInfo.STOP_REASON_FOREGROUND_SERVICE_TIMEOUT`. `IngestionWorker.OnStopped` reads
  `StopReason` (API 31+), maps that constant to `pipeline.RequestStop("fgs:timeout")` and every
  other stop to `"worker:stopped"`, and returns.
- **In your own service** `IngestionForegroundService.OnTimeout` calls
  `pipeline.RequestStop("fgs:timeout")`, `StopSelf(startId)`, and **returns**. It does not wait
  for the run: the run lands on its next committed boundary by itself, and waiting would spend the
  seconds the system grants and end in `RemoteServiceException` anyway.

Either way `IngestionRunResult.SuspendReason` is `fgs:timeout` and the next run resumes.

## What the Worker returns

| `IngestionRunOutcome` | `Result` | Why |
| --- | --- | --- |
| `Completed` | `success()` | The source is done; the periodic request fires again next period. |
| `Failed` | `failure()` | A run-tier fault; `IngestionRunResult.Failure` carries the code. |
| `Suspended`, `Cancelled` | `retry()` | Budget or stop - the source is not finished. Retry re-runs with backoff; the periodic request would also pick it up. |

An `EdgeIngestionException` thrown out of `RunAsync` (a 6xxx configuration fault) is `failure()`.

## Notification

The foreground notification uses a system drawable (`stat_sys_download`) so a class library needs
no resource; an app swaps in its own icon. Importance is `Low` (silent, ongoing) and the text is
updated **once per document boundary**, not once per 100 ms progress tick - `IngestionProgress`
carries counts and no denominator (spec 10.4), so the text is "Embedding: 12 indexed, 340 chunks",
never a percentage.

## Dependency

`Xamarin.AndroidX.Work.Runtime` (the sample pins 2.11.2.1). `Worker`, `ForegroundInfo`,
`SetForegroundAsync` and the `WorkInfo.StopReason*` constants come from it; the stop-reason
routing for `onTimeout` needs WorkManager 2.10 or later.
