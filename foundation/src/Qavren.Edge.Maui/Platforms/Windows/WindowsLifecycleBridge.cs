using Microsoft.Maui.LifecycleEvents;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;
using Windows.System;

namespace Qavren.Edge.Maui;

internal static class WindowsLifecycleBridge
{
    private static bool _memorySubscribed;

    internal static void Configure(ILifecycleBuilder events) => events.AddWindows(windows => windows
        // OnLaunched's first argument is UI.Xaml.Application, not Window - Learn's table is wrong.
        .OnLaunched((_, _) => Resolve<IEdgeHost>()?.Start())
        .OnWindowCreated(_ => SubscribeMemory())
        // MAUI's own cross-platform Stopped uses VisibilityChanged on Windows; OnResumed already
        // suppresses the spurious first activation, so it needs no de-duplication here.
        .OnVisibilityChanged((_, args) =>
        {
            if (!args.Visible)
            {
                Raise(l => l.RaiseSleepingAsync());
            }
        })
        .OnResumed(_ => Raise(l => l.RaiseResumedAsync()))
        .OnClosed((_, _) =>
        {
            UnsubscribeMemory();
            Raise(l => l.RaiseStoppingAsync());
        }));

    /// <summary>
    /// Windows.System.MemoryManager is a UWP API and its behaviour in an unpackaged WinUI 3 process
    /// is not documented, so every touch is guarded. These are static WinRT events, so failing to
    /// unsubscribe would leak the bridge.
    /// </summary>
    private static void SubscribeMemory()
    {
        if (_memorySubscribed)
        {
            return;
        }

        try
        {
            MemoryManager.AppMemoryUsageIncreased += OnMemoryIncreased;
            MemoryManager.AppMemoryUsageLimitChanging += OnMemoryLimitChanging;
            _memorySubscribed = true;
        }
        catch (Exception)
        {
            // Unavailable in this process model; memory pressure simply never fires on Windows.
        }
    }

    private static void UnsubscribeMemory()
    {
        if (!_memorySubscribed)
        {
            return;
        }

        try
        {
            MemoryManager.AppMemoryUsageIncreased -= OnMemoryIncreased;
            MemoryManager.AppMemoryUsageLimitChanging -= OnMemoryLimitChanging;
        }
        catch (Exception)
        {
            // Ignored: unsubscription failure cannot be acted on.
        }

        _memorySubscribed = false;
    }

    private static void OnMemoryIncreased(object? sender, object e)
        => Raise(l => l.RaiseMemoryPressureAsync(EdgeMemoryPressure.Moderate));

    private static void OnMemoryLimitChanging(object? sender, AppMemoryUsageLimitChangingEventArgs e)
        => Raise(l => l.RaiseMemoryPressureAsync(EdgeMemoryPressure.Critical));

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
