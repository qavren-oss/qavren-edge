using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
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
    /// model store, the session host, <see cref="FileOnnxModelSource"/> as the fallback source, the
    /// <c>OrtEnv</c> startup task (order 200), the lifecycle observer and the diagnostics
    /// contributor. Calling it twice registers one of each.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="configure">Optional <see cref="OnnxOptions"/> configuration.</param>
    /// <returns>The same builder.</returns>
    public static EdgeBuilder AddOnnx(this EdgeBuilder builder, Action<OnnxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<OnnxOptions>();
        builder.Services.AddOptions<HttpOnnxModelSourceOptions>();
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

        // The concrete type is registered and the interface forwards to it, so DI owns exactly one
        // instance and disposes it, while the internals that need the concrete type (the warm-up
        // task's AcquireCoreAsync, the observer's CancelLoadsInFlight) can still reach it.
        builder.Services.TryAddSingleton<OnnxModelStore>();
        builder.Services.TryAddSingleton<IOnnxModelStore>(sp => sp.GetRequiredService<OnnxModelStore>());
        builder.Services.TryAddSingleton<OnnxSessionHost>();
        builder.Services.TryAddSingleton<IOnnxSessionHost>(sp => sp.GetRequiredService<OnnxSessionHost>());

        // LAST as the fallback. The store re-orders it to the end whatever its registration index,
        // because AddOnnx is almost always called before the source a consumer wants probed first.
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IOnnxModelSource, FileOnnxModelSource>());

        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEdgeStartupTask, OnnxEnvironmentStartupTask>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEdgeLifecycleObserver, OnnxLifecycleObserver>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEdgeDiagnosticsContributor, OnnxDiagnosticsContributor>());

        return builder;
    }

    /// <summary>
    /// Declares a model. Provisioning and session creation stay lazy: nothing is fetched and no
    /// session is created until the first <c>IOnnxSessionHost.AcquireAsync</c>.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="manifest">The manifest.</param>
    /// <param name="configure">Optional per-model session settings.</param>
    /// <returns>The same builder.</returns>
    public static EdgeBuilder AddOnnxModel(
        this EdgeBuilder builder,
        OnnxModelManifest manifest,
        Action<OnnxSessionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(manifest);

        // Fails here rather than three waves later: the graph's declared digest is what names the
        // model directory and the CoreML cache key, so a manifest that does not list its own graph
        // has no directory to live in.
        _ = OnnxModelLayout.Graph(manifest);

        builder.Services.AddSingleton(sp => new OnnxModelRegistration
        {
            ModelId = manifest.ModelId,
            Manifest = manifest,
            SessionOptions = BuildSessionOptions(sp, configure),
        });

        return builder;
    }

    /// <summary>
    /// Registers a source. Sources are probed in registration order, with
    /// <see cref="FileOnnxModelSource"/> always last.
    /// </summary>
    /// <typeparam name="TSource">The source type, constructed by DI.</typeparam>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <returns>The same builder.</returns>
    public static EdgeBuilder AddModelSource<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSource>(
        this EdgeBuilder builder)
        where TSource : class, IOnnxModelSource
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IOnnxModelSource, TSource>());
        return builder;
    }

    /// <summary>Registers a source built by a factory.</summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="factory">The factory.</param>
    /// <returns>The same builder.</returns>
    public static EdgeBuilder AddModelSource(
        this EdgeBuilder builder,
        Func<IServiceProvider, IOnnxModelSource> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        builder.Services.AddSingleton(factory);
        return builder;
    }

    /// <summary>
    /// Registers the resumable, verified HTTP source.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="configure">Optional fetch options - a mirror, a proxy, a token, or no downloads at all.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// The <see cref="HttpClient"/> is registered with <c>TryAddSingleton</c>, so a consumer who has
    /// already registered their own - with a proxy handler, or a pinned certificate - keeps it.
    /// <c>Microsoft.Extensions.Http</c> is deliberately NOT a dependency: a transitive DI-and-Polly
    /// stack in a mobile embedding package, to replace one client and forty lines of bounded retry,
    /// is a bad trade for consumers.
    /// </remarks>
    public static EdgeBuilder AddHuggingFaceModelSource(
        this EdgeBuilder builder,
        Action<HttpOnnxModelSourceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<HttpOnnxModelSourceOptions>();
        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        builder.Services.TryAddSingleton(_ => new HttpClient());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IOnnxModelSource, HttpOnnxModelSource>());

        return builder;
    }

    /// <summary>
    /// Registers an app-package source. The opener is the consumer's - in MAUI that is
    /// <c>FileSystem.OpenAppPackageFileAsync</c> - so this package never references MAUI.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="openAsset">Opens an asset by its manifest-relative path.</param>
    /// <returns>The same builder.</returns>
    public static EdgeBuilder AddBundledModelSource(
        this EdgeBuilder builder,
        Func<string, CancellationToken, ValueTask<Stream>> openAsset)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(openAsset);

        builder.Services.AddSingleton<IOnnxModelSource>(sp =>
            new BundledOnnxModelSource(sp.GetRequiredService<IEdgeModelPaths>(), openAsset));

        return builder;
    }

    /// <summary>
    /// Startup order 210. Verifies presence and faults startup if the model is missing. Off by
    /// default, and it never downloads: blocking <c>IEdgeHost.Started</c> on a transfer also blocks
    /// every <c>IEdgeDatabase.OpenConnectionAsync</c>.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="modelId">The model to check for.</param>
    /// <returns>The same builder.</returns>
    public static EdgeBuilder ProvisionModelAtStartup(this EdgeBuilder builder, string modelId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        builder.Services.AddSingleton<IEdgeStartupTask>(
            sp => new OnnxProvisioningStartupTask(sp, modelId));

        return builder;
    }

    /// <summary>
    /// Startup order 220. Creates the session so the first real call pays neither graph optimisation
    /// nor a CoreML compile. Loads only - it runs no inference, because a batch needs a tokenizer and
    /// a generator and both live in <c>Qavren.Edge.Embeddings.Onnx</c>. Off by default, because it
    /// forces provisioning and spec 10 keeps that lazy.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="modelId">The model to load.</param>
    /// <returns>The same builder.</returns>
    public static EdgeBuilder WarmUpSessionAtStartup(this EdgeBuilder builder, string modelId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        builder.Services.AddSingleton<IEdgeStartupTask>(sp => new OnnxWarmUpStartupTask(sp, modelId));
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

    /// <summary>
    /// Registers a model whose graph is a byte array rather than a file. The ONLY caller is a tier-1
    /// test over a 533-831-byte base64 fixture; see the comment in AssemblyInfo.cs for why the
    /// product surface has no byte-array entry point at all.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="modelId">The id to register under.</param>
    /// <param name="graph">The serialised ONNX graph.</param>
    /// <param name="expectation">What the graph must declare, or null to assert nothing.</param>
    /// <param name="configure">Optional per-model session settings.</param>
    /// <returns>The same builder.</returns>
    internal static EdgeBuilder AddOnnxModelFromBytes(
        this EdgeBuilder builder,
        string modelId,
        byte[] graph,
        OnnxSessionExpectation? expectation = null,
        Action<OnnxSessionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(graph);

        builder.Services.AddSingleton(sp => new OnnxModelRegistration
        {
            ModelId = modelId,
            TestGraph = graph,
            Expectation = expectation,
            SessionOptions = BuildSessionOptions(sp, configure),
        });

        return builder;
    }

    /// <summary>
    /// Every model's session options start from the policy <see cref="UseExecutionProviderPolicy"/>
    /// configured, then the per-model callback narrows it.
    /// </summary>
    private static OnnxSessionOptions BuildSessionOptions(
        IServiceProvider services,
        Action<OnnxSessionOptions>? configure)
    {
        var options = new OnnxSessionOptions();
        CopyPolicy(services.GetRequiredService<IOptions<OnnxExecutionProviderPolicy>>().Value, options.ExecutionProviders);
        configure?.Invoke(options);
        return options;
    }

    private static void CopyPolicy(OnnxExecutionProviderPolicy from, OnnxExecutionProviderPolicy to)
    {
        to.Order = from.Order;
        to.FallBackToCpu = from.FallBackToCpu;
        to.Required = from.Required;
        to.IntraOpNumThreads = from.IntraOpNumThreads;
        to.GraphOptimization = from.GraphOptimization;

        to.CoreMl.ModelFormat = from.CoreMl.ModelFormat;
        to.CoreMl.MLComputeUnits = from.CoreMl.MLComputeUnits;
        to.CoreMl.RequireStaticInputShapes = from.CoreMl.RequireStaticInputShapes;
        to.CoreMl.EnableModelCache = from.CoreMl.EnableModelCache;
        to.CoreMl.FastPrediction = from.CoreMl.FastPrediction;
        to.CoreMl.ProfileComputePlan = from.CoreMl.ProfileComputePlan;

        to.XnnPack.IntraOpNumThreads = from.XnnPack.IntraOpNumThreads;

        foreach (var (provider, entries) in from.Overrides)
        {
            to.Overrides[provider] = entries;
        }
    }
}
