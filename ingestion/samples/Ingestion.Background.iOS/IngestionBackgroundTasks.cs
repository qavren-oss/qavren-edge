using System.Runtime.Versioning;
using BackgroundTasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Ingestion;

namespace Ingestion.Background.iOS;

/// <summary>
/// The <c>BGTaskScheduler</c> wiring for one ingestion pass. This is a documentation project:
/// copy it into the app that owns <c>Info.plist</c> (spec 16, plan Task 8.3), because the two
/// identifiers below must appear under <c>BGTaskSchedulerPermittedIdentifiers</c> and
/// <c>UIBackgroundModes</c> must carry <c>processing</c> - see the README beside this file.
/// <para>
/// <see cref="Register"/> runs once, before <c>FinishedLaunching</c> returns - Apple rejects a
/// later registration. <see cref="Schedule"/> runs whenever the app goes to the background, and
/// its return value is checked because "the OS said no" is the NORMAL path: Background App
/// Refresh is a user setting, the pending-request cap is small, and a missing plist entry is a
/// build mistake that only ever surfaces here.
/// </para>
/// <para>
/// Apple publishes no guaranteed duration for anything. The processing handler therefore takes
/// <see cref="IngestionBudget.Background"/> (5 minutes) and lets the <c>ExpirationHandler</c>
/// end the run early on a committed boundary, rather than assuming a slot length.
/// </para>
/// </summary>
public static partial class IngestionBackgroundTasks
{
    /// <summary>
    /// The <c>BGProcessingTaskRequest</c> identifier. Replace the prefix with the app's bundle
    /// id; the value here and the one in Info.plist must match character for character.
    /// </summary>
    public const string ProcessingTaskIdentifier = "com.example.app.ingestion.processing";

    /// <summary>Spec 10.2's reason for a background task the system is about to reclaim.</summary>
    public const string ExpiringStopReason = "bgtask:expiring";

    private static IServiceProvider? s_services;
    private static Func<IngestionSource>? s_sourceFactory;

    /// <summary>
    /// Registers the processing-task launch handler. Call from <c>FinishedLaunching</c>, before
    /// it returns.
    /// </summary>
    /// <param name="services">The container <c>AddIngestion</c> was registered in.</param>
    /// <param name="sourceFactory">
    /// Builds the source each run walks. A factory, not an instance: a security-scoped URL an
    /// app persisted as a bookmark must be re-resolved per launch.
    /// </param>
    /// <returns>
    /// False when the identifier is not in <c>BGTaskSchedulerPermittedIdentifiers</c> - the same
    /// build mistake <see cref="Schedule"/> reports as <see cref="BGTaskSchedulerErrorCode.NotPermitted"/>,
    /// caught one step earlier.
    /// </returns>
    public static bool Register(IServiceProvider services, Func<IngestionSource> sourceFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sourceFactory);
        s_services = services;
        s_sourceFactory = sourceFactory;

        return BGTaskScheduler.Shared.Register(
            ProcessingTaskIdentifier,
            null,
            task => HandleProcessing((BGProcessingTask)task));
    }

    /// <summary>
    /// Submits one <c>BGProcessingTaskRequest</c>. Call from <c>DidEnterBackground</c>; a request
    /// submitted while the app is in the foreground is legal but the system will not run it until
    /// the app leaves.
    /// </summary>
    /// <param name="logger">Where a refused submit is explained.</param>
    /// <returns>True when the scheduler accepted the request.</returns>
    public static bool Schedule(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var request = new BGProcessingTaskRequest(ProcessingTaskIdentifier)
        {
            // Ingestion is CPU and disk: ask for power so the system prefers an overnight charge
            // slot, and say so about the network - a local-only task that claims it needs
            // connectivity waits for Wi-Fi it will never use.
            RequiresExternalPower = true,
            RequiresNetworkConnectivity = false,
        };

        return Submit(request, logger);
    }

    /// <summary>
    /// <c>Submit</c> returns a bool and the sample branches on it. Each
    /// <see cref="BGTaskSchedulerErrorCode"/> names the action a developer should take, because a
    /// submit whose return value is ignored is how a background task silently never runs.
    /// </summary>
    private static bool Submit(BGTaskRequest request, ILogger logger)
    {
        if (BGTaskScheduler.Shared.Submit(request, out NSError? error))
        {
            Log.Submitted(logger, request.Identifier);
            return true;
        }

        var code = error is null ? default : (BGTaskSchedulerErrorCode)(long)error.Code;
        switch (code)
        {
            case BGTaskSchedulerErrorCode.Unavailable:
                // 1. A user setting - Background App Refresh is off for this app or device-wide.
                // Not an error to retry; tell the user where the switch is, if it matters.
                Log.Unavailable(logger, request.Identifier);
                break;
            case BGTaskSchedulerErrorCode.TooManyPendingTaskRequests:
                // 2. The scheduler's queue is full. Cancel or coalesce before resubmitting.
                Log.TooManyPending(logger, request.Identifier);
                break;
            case BGTaskSchedulerErrorCode.NotPermitted:
                // 3. A build mistake: the identifier is not in BGTaskSchedulerPermittedIdentifiers,
                // or UIBackgroundModes lacks "processing". Nothing at runtime fixes this.
                Log.NotPermitted(logger, request.Identifier);
                break;
            case BGTaskSchedulerErrorCode.ImmediateRunIneligible:
                // 4. The request asked to run immediately and the system declined; submit again
                // without that expectation.
                Log.ImmediateRunIneligible(logger, request.Identifier);
                break;
            default:
                Log.SubmitFailed(logger, request.Identifier, error?.LocalizedDescription);
                break;
        }

        return false;
    }

    private static void HandleProcessing(BGProcessingTask task)
    {
        // Re-arm before running: a run that is killed mid-way must not also lose the chain.
        var services = s_services;
        var sourceFactory = s_sourceFactory;
        if (services is null || sourceFactory is null)
        {
            task.SetTaskCompleted(false);
            return;
        }

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(IngestionBackgroundTasks));
        var pipeline = services.GetRequiredService<IIngestionPipeline>();
        Schedule(logger);

        // The system may call the expiration handler and the run may finish in either order;
        // the task is completed exactly once whichever comes first.
        var completed = 0;

        task.ExpirationHandler = () =>
        {
            pipeline.RequestStop(ExpiringStopReason);
            if (Interlocked.Exchange(ref completed, 1) == 0)
            {
                // False: the source is not finished. The next slot resumes from the state
                // tables; nothing committed is redone (spec 10.3).
                task.SetTaskCompleted(false);
            }
        };

        _ = Task.Run(async () =>
        {
            var success = false;
            try
            {
                var result = await pipeline.RunAsync(
                        sourceFactory(),
                        collectionName: null,
                        new IngestionRunOptions { Budget = IngestionBudget.Background })
                    .ConfigureAwait(false);
                Log.RunEnded(logger, result.Outcome, result.SuspendReason, result.DocumentsIndexed, result.ChunksAdded);
                success = result.Outcome is IngestionRunOutcome.Completed;
            }
            catch (EdgeIngestionException ex)
            {
                Log.RunThrew(logger, ex.Code, ex);
            }
            finally
            {
                if (Interlocked.Exchange(ref completed, 1) == 0)
                {
                    task.SetTaskCompleted(success);
                }
            }
        });
    }

#if IOS
    /// <summary>
    /// The iOS 26 continued-processing task identifier. Continued tasks may be registered under a
    /// wildcard (<c>com.example.app.ingestion.*</c>) in <c>BGTaskSchedulerPermittedIdentifiers</c>.
    /// </summary>
    public const string ContinuedTaskIdentifier = "com.example.app.ingestion.continued";

    /// <summary>
    /// iOS 26: a user-initiated run that keeps going after the app leaves the foreground, with
    /// system UI showing <c>NSProgress</c>. Guarded <c>#if IOS</c> because
    /// <c>BGContinuedProcessingTask</c>, <c>BGContinuedProcessingTaskRequest</c> and
    /// <c>BGContinuedProcessingTaskRequestResources</c> ship in <c>Microsoft.iOS.dll</c> only -
    /// not Mac Catalyst, not tvOS - while the rest of <c>BackgroundTasks</c> is in all three.
    /// </summary>
    /// <param name="services">The container <c>AddIngestion</c> was registered in.</param>
    /// <param name="sourceFactory">Builds the source each run walks.</param>
    /// <returns>False when the identifier is not permitted.</returns>
    [SupportedOSPlatform("ios26.0")]
    [UnsupportedOSPlatform("maccatalyst")]
    public static bool RegisterContinued(IServiceProvider services, Func<IngestionSource> sourceFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sourceFactory);
        s_services = services;
        s_sourceFactory = sourceFactory;

        return BGTaskScheduler.Shared.Register(
            ContinuedTaskIdentifier,
            null,
            task => HandleContinued((BGContinuedProcessingTask)task));
    }

    /// <summary>
    /// Submits the continued request. Unlike <see cref="Schedule"/> this is called from a user
    /// gesture in the FOREGROUND - the system shows the title and subtitle immediately.
    /// </summary>
    /// <param name="title">What the system UI shows.</param>
    /// <param name="subtitle">The second line.</param>
    /// <param name="logger">Where a refused submit is explained.</param>
    /// <returns>True when the scheduler accepted the request.</returns>
    [SupportedOSPlatform("ios26.0")]
    [UnsupportedOSPlatform("maccatalyst")]
    public static bool ScheduleContinued(string title, string subtitle, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var request = new BGContinuedProcessingTaskRequest(ContinuedTaskIdentifier, title, subtitle)
        {
            // Fail rather than queue: a user who tapped "index now" wants to know now.
            Strategy = BGContinuedProcessingTaskRequestSubmissionStrategy.Fail,
            RequiredResources = BGContinuedProcessingTaskRequestResources.Default,
        };

        return Submit(request, logger);
    }

    [SupportedOSPlatform("ios26.0")]
    [UnsupportedOSPlatform("maccatalyst")]
    private static void HandleContinued(BGContinuedProcessingTask task)
    {
        var services = s_services;
        var sourceFactory = s_sourceFactory;
        if (services is null || sourceFactory is null)
        {
            task.SetTaskCompleted(false);
            return;
        }

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(IngestionBackgroundTasks));
        var pipeline = services.GetRequiredService<IIngestionPipeline>();
        var completed = 0;

        task.ExpirationHandler = () =>
        {
            pipeline.RequestStop(ExpiringStopReason);
            if (Interlocked.Exchange(ref completed, 1) == 0)
            {
                task.SetTaskCompleted(false);
            }
        };

        // The bridge. IngestionProgress carries counts and no denominator (spec 10.4), so the
        // NSProgress is INDETERMINATE - a negative total - and the counts go in the description.
        // The system kills a continued task that reports no progress, so the bridge is not
        // optional: every progress callback touches the NSProgress.
        var progress = task.Progress;
        progress.TotalUnitCount = -1;
        var bridge = new Progress<IngestionProgress>(p =>
        {
            progress.CompletedUnitCount = p.DocumentsSeen;
            progress.LocalizedDescription = $"{p.Stage}: {p.DocumentsIndexed} indexed, {p.ChunksAdded} chunks";
        });

        _ = Task.Run(async () =>
        {
            var success = false;
            try
            {
                var result = await pipeline.RunAsync(
                        sourceFactory(),
                        collectionName: null,
                        new IngestionRunOptions { Progress = bridge })
                    .ConfigureAwait(false);
                Log.RunEnded(logger, result.Outcome, result.SuspendReason, result.DocumentsIndexed, result.ChunksAdded);
                success = result.Outcome is IngestionRunOutcome.Completed;
            }
            catch (EdgeIngestionException ex)
            {
                Log.RunThrew(logger, ex.Code, ex);
            }
            finally
            {
                if (Interlocked.Exchange(ref completed, 1) == 0)
                {
                    task.SetTaskCompleted(success);
                }
            }
        });
    }
#endif

    private static partial class Log
    {
        [LoggerMessage(1, LogLevel.Debug, "Background task {Identifier} submitted")]
        public static partial void Submitted(ILogger logger, string identifier);

        [LoggerMessage(2, LogLevel.Information,
            "Background task {Identifier} not submitted: Background App Refresh is off for this app or the device (BGTaskSchedulerErrorCode.Unavailable). A user setting; do not retry")]
        public static partial void Unavailable(ILogger logger, string identifier);

        [LoggerMessage(3, LogLevel.Warning,
            "Background task {Identifier} not submitted: too many pending requests (BGTaskSchedulerErrorCode.TooManyPendingTaskRequests). Cancel or coalesce before resubmitting")]
        public static partial void TooManyPending(ILogger logger, string identifier);

        [LoggerMessage(4, LogLevel.Error,
            "Background task {Identifier} not submitted: not permitted (BGTaskSchedulerErrorCode.NotPermitted). The identifier is missing from BGTaskSchedulerPermittedIdentifiers or UIBackgroundModes lacks 'processing' - a build mistake, fix Info.plist")]
        public static partial void NotPermitted(ILogger logger, string identifier);

        [LoggerMessage(5, LogLevel.Warning,
            "Background task {Identifier} not submitted: immediate run ineligible (BGTaskSchedulerErrorCode.ImmediateRunIneligible). Resubmit without the immediate expectation")]
        public static partial void ImmediateRunIneligible(ILogger logger, string identifier);

        [LoggerMessage(6, LogLevel.Error, "Background task {Identifier} not submitted: {Description}")]
        public static partial void SubmitFailed(ILogger logger, string identifier, string? description);

        [LoggerMessage(7, LogLevel.Information,
            "Ingestion run ended: {Outcome} ({SuspendReason}); {DocumentsIndexed} documents indexed, {ChunksAdded} chunks added")]
        public static partial void RunEnded(
            ILogger logger, IngestionRunOutcome outcome, string? suspendReason, int documentsIndexed, int chunksAdded);

        [LoggerMessage(8, LogLevel.Error, "Ingestion run threw {Code}")]
        public static partial void RunThrew(ILogger logger, Qavren.Edge.EdgeErrorCode code, Exception exception);
    }
}
