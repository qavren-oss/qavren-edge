using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Onnx.Internal;

namespace Qavren.Edge.Onnx;

/// <summary>Registers ONNX Runtime hosting on the Qavren.Edge builder.</summary>
public static class OnnxEdgeBuilderExtensions
{
    /// <summary>
    /// Idempotent. Registers <see cref="IEdgeModelPaths"/>, <see cref="IEdgeResourceMonitor"/>, the
    /// <c>OrtEnv</c> startup task (order 200), the lifecycle observer and the diagnostics
    /// contributor. Calling it twice registers one of each.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="configure">Optional <see cref="OnnxOptions"/> configuration.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// The session host, the model store and the model sources arrive with model registration;
    /// <see cref="UseExecutionProviderPolicy"/> and <see cref="UseModelPaths"/> are the two
    /// configuration hooks this package ships today.
    /// </remarks>
    public static EdgeBuilder AddOnnx(this EdgeBuilder builder, Action<OnnxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<OnnxOptions>();
        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        builder.Services.TryAddSingleton<OnnxEnvironmentState>();

#if ANDROID
        builder.Services.TryAddSingleton<IEdgeModelPaths, AndroidEdgeModelPaths>();
        builder.Services.TryAddSingleton<IEdgeResourceMonitor, AndroidResourceMonitor>();
#elif IOS || MACCATALYST
        builder.Services.TryAddSingleton<IEdgeModelPaths, AppleEdgeModelPaths>();
        builder.Services.TryAddSingleton<IEdgeResourceMonitor, AppleResourceMonitor>();
#else
        builder.Services.TryAddSingleton<IEdgeModelPaths, DefaultEdgeModelPaths>();
        builder.Services.TryAddSingleton<IEdgeResourceMonitor, DefaultEdgeResourceMonitor>();
#endif

        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEdgeStartupTask, OnnxEnvironmentStartupTask>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEdgeLifecycleObserver, OnnxLifecycleObserver>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEdgeDiagnosticsContributor, OnnxDiagnosticsContributor>());

        return builder;
    }

    /// <summary>
    /// Configures the default execution-provider policy every session starts from.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="configure">The policy configuration.</param>
    /// <returns>The same builder.</returns>
    public static EdgeBuilder UseExecutionProviderPolicy(
        this EdgeBuilder builder,
        Action<OnnxExecutionProviderPolicy> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.Configure(configure);
        return builder;
    }

    /// <summary>
    /// Replaces <see cref="IEdgeModelPaths"/>. Tests point it at a temp directory.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="paths">The replacement.</param>
    /// <returns>The same builder.</returns>
    public static EdgeBuilder UseModelPaths(this EdgeBuilder builder, IEdgeModelPaths paths)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(paths);

        builder.Services.RemoveAll<IEdgeModelPaths>();
        builder.Services.AddSingleton(paths);
        return builder;
    }
}
