using Android.Content;
using Android.Content.PM;
using Android.OS;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Ingestion;

// The manifest entries the README quotes, in attribute form. A referencing app's manifest merge
// picks these up; an app that manages its manifest by hand copies the README's XML instead.
[assembly: UsesPermission(global::Android.Manifest.Permission.ForegroundService)]
[assembly: UsesPermission(global::Android.Manifest.Permission.ForegroundServiceDataSync)]
[assembly: UsesPermission(global::Android.Manifest.Permission.PostNotifications)]

namespace Ingestion.Background.Android;

/// <summary>
/// The literal <c>Service.OnTimeout(int, ForegroundService)</c> wiring, for a consumer who hosts the run in a
/// foreground service of their own instead of behind WorkManager. <see cref="IngestionWorker"/>
/// is the recommended shape - WorkManager owns the service there and routes the same timeout to
/// <see cref="IngestionWorker.OnStopped"/> - but the callback itself lives on
/// <see cref="Service"/>, so this is where a reader sees it spelled out.
/// <para>
/// Android 15 (API 35) gives <c>dataSync</c> six hours per rolling 24, tracked separately from
/// <c>mediaProcessing</c> and reset only when the app comes to the foreground. On expiry the
/// system calls <see cref="OnTimeout(int, ForegroundService)"/> and the process has a few seconds to
/// <c>stopSelf()</c> before it is killed with <c>RemoteServiceException</c>. The handler asks the
/// pipeline for <c>fgs:timeout</c>, stops the service and RETURNS - it does not wait for the run,
/// which lands on its next committed boundary on its own.
/// </para>
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class IngestionForegroundService : Service
{
    /// <summary>Spec 10.2's reason for the Android 15 foreground-service timeout.</summary>
    public const string TimeoutStopReason = "fgs:timeout";

    private const int NotificationId = 0x5134;

    private static IServiceProvider? s_services;
    private static Func<IngestionSource>? s_sourceFactory;

    private IIngestionPipeline? _pipeline;

    /// <summary>Same contract as <see cref="IngestionWorker.Configure"/>: once, at app startup.</summary>
    /// <param name="services">The container <c>AddIngestion</c> was registered in.</param>
    /// <param name="sourceFactory">Builds the source each run walks.</param>
    public static void Configure(IServiceProvider services, Func<IngestionSource> sourceFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sourceFactory);
        s_services = services;
        s_sourceFactory = sourceFactory;
    }

    /// <summary>
    /// Starts the service from the FOREGROUND - an activity, a notification action, a user
    /// gesture. Started from the background on Android 12+ this throws
    /// <see cref="ForegroundServiceStartNotAllowedException"/>; that is the case
    /// <see cref="IngestionWorker"/> exists to absorb.
    /// </summary>
    /// <param name="context">Any context.</param>
    public static void Start(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.StartForegroundService(new Intent(context, typeof(IngestionForegroundService)));
    }

    /// <inheritdoc />
    public override IBinder? OnBind(Intent? intent) => null;

    /// <inheritdoc />
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (s_services is null || s_sourceFactory is null)
        {
            StopSelf(startId);
            return StartCommandResult.NotSticky;
        }

        // The typed overload (API 29+) is what makes this a dataSync service at runtime; the
        // manifest attribute above declares it, and API 34+ requires both to agree.
        StartForeground(NotificationId, BuildNotification(), ForegroundService.TypeDataSync);

        var pipeline = s_services.GetRequiredService<IIngestionPipeline>();
        _pipeline = pipeline;
        var source = s_sourceFactory();

        _ = Task.Run(async () =>
        {
            try
            {
                // Unlimited on purpose: this shape has no Worker cap, only the six-hour wall,
                // and OnTimeout is what ends the run there. Trade for IngestionBudget.Background
                // if a shorter slot is wanted.
                await pipeline.RunAsync(source, collectionName: null, options: null).ConfigureAwait(false);
            }
            catch (EdgeIngestionException)
            {
                // A sample has nowhere to report this but the run result the app never sees;
                // an app logs it through its container's ILoggerFactory.
            }
            finally
            {
                StopSelf(startId);
            }
        });

        return StartCommandResult.NotSticky;
    }

    /// <summary>
    /// Android 15's expiry callback. <paramref name="fgsType"/> is the timed-out type - here
    /// always <see cref="ForegroundService.TypeDataSync"/>, because that is the only type this
    /// service declares.
    /// </summary>
    /// <param name="startId">The start id being timed out.</param>
    /// <param name="fgsType">The foreground service type whose quota expired.</param>
    public override void OnTimeout(int startId, ForegroundService fgsType)
    {
        _pipeline?.RequestStop(TimeoutStopReason);
        StopSelf(startId);
        // Return. The run stops on its next committed boundary; waiting here would spend the
        // few seconds the system grants and end in RemoteServiceException anyway.
    }

    private Notification BuildNotification()
    {
        NotificationManager.FromContext(this)!.CreateNotificationChannel(
            new NotificationChannel(IngestionWorker.ChannelId, "Indexing", NotificationImportance.Low));

        return new Notification.Builder(this, IngestionWorker.ChannelId)
            .SetContentTitle("Indexing documents")!
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)!
            .SetOngoing(true)!
            .Build()!;
    }
}
