using Android.Content;
using Android.Content.PM;
using AndroidX.Work;
using Java.Util.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Ingestion;

namespace Ingestion.Background.Android;

/// <summary>
/// A WorkManager <see cref="Worker"/> that runs one ingestion pass inside a foreground service of
/// type <c>dataSync</c>. This is a documentation project: copy the class into the app that owns
/// the manifest (spec 16, plan Task 8.3), because every line below implies a permission or a
/// manifest entry the library cannot ship for you - see the README beside this file.
/// <para>
/// The three failure modes it handles, in the order a consumer meets them:
/// </para>
/// <list type="number">
///   <item><b>Android 12+ refuses the promotion.</b> A Worker that WorkManager started while the
///   app was backgrounded may not start a foreground service, so <c>SetForegroundAsync</c> fails
///   with <see cref="ForegroundServiceStartNotAllowedException"/>. The run continues UNPROMOTED
///   under the plain Worker's ~10-minute cap with <see cref="IngestionBudget.Background"/>
///   (5 minutes), which suspends on a committed boundary instead of being killed mid-write.</item>
///   <item><b>API 36 charges jobs started from a foreground service to the app's job quota.</b>
///   So the ingestion happens HERE, inside the Worker, rather than the Worker scheduling
///   further jobs it would then be billed for.</item>
///   <item><b>Android 15's dataSync wall: six hours per rolling 24.</b> On expiry the system
///   calls <c>Service.onTimeout</c> on WorkManager's <c>SystemForegroundService</c>, and
///   WorkManager 2.10+ turns that into <see cref="OnStopped"/> with
///   <see cref="WorkInfo.StopReasonForegroundServiceTimeout"/>. That path asks the pipeline for
///   <c>fgs:timeout</c> and returns; the run lands on its next committed boundary.</item>
/// </list>
/// </summary>
public sealed partial class IngestionWorker : Worker
{
    /// <summary>The unique-work name used by <see cref="Enqueue"/>.</summary>
    public const string UniqueWorkName = "qavren.edge.ingestion";

    /// <summary>The notification channel the foreground notification is posted on.</summary>
    public const string ChannelId = "qavren.edge.ingestion";

    /// <summary>Spec 10.2's reason for the Android 15 foreground-service timeout.</summary>
    public const string TimeoutStopReason = "fgs:timeout";

    /// <summary>The reason recorded for every other WorkManager stop (constraints, cancel, quota).</summary>
    public const string WorkerStopReason = "worker:stopped";

    private const int NotificationId = 0x5133;

    private static IngestionWorkerConfiguration? s_configuration;

    private IIngestionPipeline? _pipeline;

    /// <summary>
    /// WorkManager constructs Workers itself through the default <c>WorkerFactory</c>, so this is
    /// the only constructor shape it accepts and constructor injection is not available. The app
    /// hands over its container once, through <see cref="Configure"/>.
    /// </summary>
    /// <param name="context">The application context.</param>
    /// <param name="workerParams">WorkManager's parameters.</param>
    public IngestionWorker(Context context, WorkerParameters workerParams)
        : base(context, workerParams)
    {
    }

    /// <summary>
    /// Call once from the app's startup - <c>MauiProgram.CreateMauiApp</c> or
    /// <c>Application.OnCreate</c> - BEFORE the first Worker can run. Both run before WorkManager
    /// starts anything in a fresh process, so a periodic request that wakes the app cold still
    /// finds its configuration.
    /// </summary>
    /// <param name="services">The container <c>AddIngestion</c> was registered in.</param>
    /// <param name="sourceFactory">
    /// Builds the source each run walks. A factory, not an instance: <see cref="IngestionSource"/>
    /// implementations may hold handles a process restart invalidates.
    /// </param>
    public static void Configure(IServiceProvider services, Func<IngestionSource> sourceFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sourceFactory);
        s_configuration = new IngestionWorkerConfiguration(services, sourceFactory);
    }

    /// <summary>
    /// Enqueues the periodic request: every 12 hours, charging and battery-not-low. KEEP means
    /// calling this on every launch is harmless; the existing request survives.
    /// </summary>
    /// <param name="context">Any context.</param>
    public static void Enqueue(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var constraints = new Constraints.Builder()
            .SetRequiresCharging(true)
            .SetRequiresBatteryNotLow(true)
            .Build();
        var request = new PeriodicWorkRequest.Builder(typeof(IngestionWorker), 12, TimeUnit.Hours!)
            .SetConstraints(constraints)
            .Build();

        WorkManager.GetInstance(context)
            .EnqueueUniquePeriodicWork(UniqueWorkName, ExistingPeriodicWorkPolicy.Keep!, request);
    }

    /// <inheritdoc />
    public override Result DoWork()
    {
        var configuration = s_configuration;
        if (configuration is null)
        {
            // No logger either - the container is what would have supplied one. Failure, not
            // Retry: a missing Configure call is a programming error a backoff cannot fix.
            return Result.InvokeFailure()!;
        }

        var logger = configuration.Services.GetRequiredService<ILoggerFactory>().CreateLogger<IngestionWorker>();
        var pipeline = configuration.Services.GetRequiredService<IIngestionPipeline>();
        _pipeline = pipeline;

        // Failure mode 1. Promotion is attempted, never assumed.
        var promoted = TryPromote(logger);

        var options = new IngestionRunOptions
        {
            // Failure mode 1, the other half: 5 minutes fits under the unpromoted Worker's cap
            // with room for the last write window to commit. A promoted run could afford more;
            // keeping one budget keeps the two paths' behaviour identical except for the
            // notification, which is the point of a sample.
            Budget = IngestionBudget.Background,
            Progress = promoted ? new NotificationProgress(this) : null,
        };

        // Failure mode 2: the ingestion runs HERE. Nothing below schedules another job.
        IngestionRunResult result;
        try
        {
            result = pipeline.RunAsync(configuration.SourceFactory(), collectionName: null, options)
                .GetAwaiter()
                .GetResult();
        }
        catch (EdgeIngestionException ex)
        {
            Log.RunThrew(logger, ex.Code, ex);
            return Result.InvokeFailure()!;
        }

        Log.RunEnded(logger, result.Outcome, result.SuspendReason, result.DocumentsIndexed, result.ChunksAdded);

        return result.Outcome switch
        {
            IngestionRunOutcome.Completed => Result.InvokeSuccess()!,
            IngestionRunOutcome.Failed => Result.InvokeFailure()!,
            // Suspended (budget) or Cancelled (a stop from OnStopped): the source is not finished.
            // Retry re-runs with backoff; a periodic request would also pick it up next period.
            _ => Result.InvokeRetry()!,
        };
    }

    /// <summary>
    /// Failure mode 3, and every other stop. WorkManager calls this when the system times the
    /// foreground service out (Android 15's six-hour dataSync wall), when a constraint stops
    /// holding, when the app cancels the work, or when the ~10-minute cap of an unpromoted
    /// Worker is reached. The pipeline stops on its NEXT committed boundary and
    /// <see cref="DoWork"/> returns normally - there is nothing else to tear down.
    /// </summary>
    public override void OnStopped()
    {
        base.OnStopped();

        var reason = WorkerStopReason;
        if (OperatingSystem.IsAndroidVersionAtLeast(31)
            && StopReason == WorkInfo.StopReasonForegroundServiceTimeout)
        {
            reason = TimeoutStopReason;
        }

        _pipeline?.RequestStop(reason);
    }

    private bool TryPromote(ILogger logger)
    {
        var info = new ForegroundInfo(
            NotificationId,
            BuildNotification("Starting"),
            // TypeDataSync, not DataSync: the latter does not exist on this enum.
            (int)ForegroundService.TypeDataSync);

        try
        {
            // Block for the promotion result. DoWork already runs on WorkManager's executor, so
            // there is no UI thread to starve, and the promotion must be settled before the run
            // starts - a Worker cannot retroactively become foreground.
            SetForegroundAsync(info)!.Get();
            return true;
        }
        catch (ExecutionException ex) when (IsStartNotAllowed(ex))
        {
            Log.PromotionRefused(logger, ex);
            return false;
        }
        catch (Java.Lang.Exception ex) when (IsStartNotAllowed(ex))
        {
            Log.PromotionRefused(logger, ex);
            return false;
        }
    }

    /// <summary>
    /// <see cref="ForegroundServiceStartNotAllowedException"/> arrives either directly or as the
    /// cause of the future's <see cref="ExecutionException"/>, depending on the WorkManager
    /// version. Both are checked, and only on API 31+ where the type exists at all.
    /// </summary>
    private static bool IsStartNotAllowed(Java.Lang.Throwable ex)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            return false;
        }

        return ex is ForegroundServiceStartNotAllowedException
            || ex.Cause is ForegroundServiceStartNotAllowedException
            || ex.InnerException is ForegroundServiceStartNotAllowedException;
    }

    private Notification BuildNotification(string text)
    {
        var context = ApplicationContext!;
        var manager = NotificationManager.FromContext(context)!;

        // Idempotent: creating a channel that exists is a no-op. Importance Low keeps the
        // ongoing notification silent.
        manager.CreateNotificationChannel(
            new NotificationChannel(ChannelId, "Indexing", NotificationImportance.Low));

        // The system icon is a placeholder so a class library needs no drawable resource. An app
        // uses its own.
        return new Notification.Builder(context, ChannelId)
            .SetContentTitle("Indexing documents")!
            .SetContentText(text)!
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .Build()!;
    }

    private void UpdateNotification(in IngestionProgress progress)
    {
        var text = $"{progress.Stage}: {progress.DocumentsIndexed} indexed, {progress.ChunksAdded} chunks";
        NotificationManager.FromContext(ApplicationContext!)!.Notify(NotificationId, BuildNotification(text));
    }

    /// <summary>
    /// A direct <see cref="IProgress{T}"/> rather than <see cref="Progress{T}"/>: the latter posts
    /// through the captured <see cref="SynchronizationContext"/>, and on WorkManager's executor
    /// there is none, which makes the delivery thread an implementation detail. Counts only, per
    /// spec 10.4 - the denominator is unknown until the run ends.
    /// </summary>
    private sealed class NotificationProgress(IngestionWorker worker) : IProgress<IngestionProgress>
    {
        private int _lastDocumentsSeen = -1;

        public void Report(IngestionProgress value)
        {
            // Once per document boundary, not once per 100 ms: NotificationManager.notify is not
            // free and the text only changes when a count does.
            if (value.DocumentsSeen == _lastDocumentsSeen)
            {
                return;
            }

            _lastDocumentsSeen = value.DocumentsSeen;
            worker.UpdateNotification(value);
        }
    }

    private sealed record IngestionWorkerConfiguration(IServiceProvider Services, Func<IngestionSource> SourceFactory);

    private static partial class Log
    {
        [LoggerMessage(1, LogLevel.Warning,
            "Foreground promotion refused (Android 12+ background start restriction); running unpromoted under IngestionBudget.Background")]
        public static partial void PromotionRefused(ILogger logger, Exception exception);

        [LoggerMessage(2, LogLevel.Information,
            "Ingestion run ended: {Outcome} ({SuspendReason}); {DocumentsIndexed} documents indexed, {ChunksAdded} chunks added")]
        public static partial void RunEnded(
            ILogger logger, IngestionRunOutcome outcome, string? suspendReason, int documentsIndexed, int chunksAdded);

        [LoggerMessage(3, LogLevel.Error, "Ingestion run threw {Code}")]
        public static partial void RunThrew(ILogger logger, Qavren.Edge.EdgeErrorCode code, Exception exception);
    }
}
