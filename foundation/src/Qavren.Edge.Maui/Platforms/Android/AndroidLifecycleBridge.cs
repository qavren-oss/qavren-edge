using Android.Content;
using Microsoft.Maui.LifecycleEvents;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;

namespace Qavren.Edge.Maui;

internal static class AndroidLifecycleBridge
{
    private static int _activityCount;

    internal static void Configure(ILifecycleBuilder events) => events.AddAndroid(android => android
        // OnApplicationCreate, NOT OnCreate: OnCreate on IAndroidLifecycleBuilder is the Activity overload.
        .OnApplicationCreate(_ => Resolve<IEdgeHost>()?.Start())
        .OnCreate((_, _) => Interlocked.Increment(ref _activityCount))
        .OnPause(_ => Raise(l => l.RaiseSleepingAsync()))
        .OnResume(_ => Raise(l => l.RaiseResumedAsync()))
        .OnDestroy(_ =>
        {
            // There is no "last activity destroyed" event; count them ourselves.
            if (Interlocked.Decrement(ref _activityCount) <= 0)
            {
                Raise(l => l.RaiseStoppingAsync());
            }
        })
        .OnApplicationLowMemory(_ => Raise(l => l.RaiseMemoryPressureAsync(EdgeMemoryPressure.Critical)))
        .OnApplicationTrimMemory((_, level) =>
        {
            var mapped = MapTrimMemory(level);
            if (mapped is { } pressure)
            {
                Raise(l => l.RaiseMemoryPressureAsync(pressure));
            }
        }));

    /// <summary>
    /// Only <see cref="TrimMemory.UiHidden"/> (20) and <see cref="TrimMemory.Background"/> (40) are
    /// still delivered: every other level was deprecated in API 35 and has not been delivered to apps
    /// since API 34, while .NET MAUI 10 targets API 36. The legacy branch stays for pre-34 devices.
    /// </summary>
    private static EdgeMemoryPressure? MapTrimMemory(TrimMemory level) => level switch
    {
        TrimMemory.UiHidden => EdgeMemoryPressure.Low,
        TrimMemory.Background => EdgeMemoryPressure.Moderate,
        TrimMemory.RunningModerate => EdgeMemoryPressure.Low,
        TrimMemory.RunningLow => EdgeMemoryPressure.Moderate,
        TrimMemory.RunningCritical => EdgeMemoryPressure.Critical,
        TrimMemory.Moderate => EdgeMemoryPressure.Moderate,
        TrimMemory.Complete => EdgeMemoryPressure.Critical,
        _ => null,
    };

    private static T? Resolve<T>() where T : class
        => IPlatformApplication.Current?.Services.GetService<T>();

    private static void Raise(Func<IEdgeLifecycle, Task> raise)
    {
        var lifecycle = Resolve<IEdgeLifecycle>();
        if (lifecycle is not null)
        {
            // The hub swallows observer faults, so nothing can escape here.
            raise(lifecycle).GetAwaiter().GetResult();
        }
    }
}
