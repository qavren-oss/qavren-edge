using Foundation;
using Microsoft.Maui.LifecycleEvents;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;
using UIKit;

namespace Qavren.Edge.Maui;

internal static class AppleLifecycleBridge
{
    private static NSObject? _memoryWarningToken;

    internal static void Configure(ILifecycleBuilder events) => events.AddiOS(ios => ios
        .FinishedLaunching((_, _) =>
        {
            Resolve<IEdgeHost>()?.Start();

            // There is no DidReceiveMemoryWarning delegate on IiOSLifecycleBuilder and
            // MauiUIApplicationDelegate exports none, so subscribe to UIKit directly.
            _memoryWarningToken ??= UIApplication.Notifications.ObserveDidReceiveMemoryWarning(
                (_, _) => Raise(l => l.RaiseMemoryPressureAsync(EdgeMemoryPressure.Critical)));

            return true;
        })
        .DidEnterBackground(_ => SleepInsideBackgroundTask())
        .WillEnterForeground(_ => Raise(l => l.RaiseResumedAsync()))
        .WillTerminate(_ =>
        {
            _memoryWarningToken?.Dispose();
            _memoryWarningToken = null;
            Raise(l => l.RaiseStoppingAsync());
        }));

    /// <summary>
    /// Window.Backgrounding carries no deferral (BackgroundingEventArgs has only State), so the
    /// bridge takes its own background task to give the WAL checkpoint time to finish.
    /// </summary>
    private static void SleepInsideBackgroundTask()
    {
        var application = UIApplication.SharedApplication;
        var taskId = UIApplication.BackgroundTaskInvalid;
        taskId = application.BeginBackgroundTask("qavren-edge-sleep", () => application.EndBackgroundTask(taskId));

        try
        {
            Raise(l => l.RaiseSleepingAsync());
        }
        finally
        {
            application.EndBackgroundTask(taskId);
        }
    }

    private static T? Resolve<T>() where T : class
        => IPlatformApplication.Current?.Services.GetService<T>();

    private static void Raise(Func<IEdgeLifecycle, Task> raise)
    {
        var lifecycle = Resolve<IEdgeLifecycle>();
        if (lifecycle is not null)
        {
            raise(lifecycle).GetAwaiter().GetResult();
        }
    }
}
