using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Maui.LifecycleEvents;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Maui;

/// <summary>MAUI host wiring for Qavren.Edge.</summary>
public static class MauiAppBuilderExtensions
{
    /// <summary>
    /// Registers Qavren.Edge, replaces <see cref="IEdgePaths"/> with the MAUI implementation, and
    /// maps platform lifecycle events onto <see cref="Qavren.Edge.Lifecycle.IEdgeLifecycle"/>.
    /// </summary>
    /// <param name="builder">The MAUI app builder.</param>
    /// <param name="configure">Configures the Qavren.Edge builder.</param>
    /// <returns>The same <paramref name="builder"/>, for chaining.</returns>
    public static MauiAppBuilder UseQavrenEdge(this MauiAppBuilder builder, Action<EdgeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.AddQavrenEdge(configure);

        // Replace, not TryAdd: AddQavrenEdge already registered DefaultEdgePaths.
        builder.Services.Replace(ServiceDescriptor.Singleton<IEdgePaths, MauiEdgePaths>());

        // The generic-host EdgeHostedService would block MAUI's launch path; the platform bridge
        // calls Start() instead and never awaits it on the UI thread. Only that one registration
        // goes: the app's own hosted services, and every other library's, are not this package's
        // to remove.
        for (var i = builder.Services.Count - 1; i >= 0; i--)
        {
            var descriptor = builder.Services[i];
            if (descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                && !descriptor.IsKeyedService
                && descriptor.ImplementationType == typeof(EdgeHostedService))
            {
                builder.Services.RemoveAt(i);
            }
        }

        builder.ConfigureLifecycleEvents(events =>
        {
#if ANDROID
            AndroidLifecycleBridge.Configure(events);
#elif IOS || MACCATALYST
            AppleLifecycleBridge.Configure(events);
#elif WINDOWS
            WindowsLifecycleBridge.Configure(events);
#endif
        });

        return builder;
    }
}
