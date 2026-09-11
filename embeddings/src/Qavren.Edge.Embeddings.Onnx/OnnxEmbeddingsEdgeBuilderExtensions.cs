using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Embeddings.Onnx.Internal;
using Qavren.Edge.Hosting;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Embeddings.Onnx;

/// <summary>Registers on-device embeddings on the Qavren.Edge builder.</summary>
public static class OnnxEmbeddingsEdgeBuilderExtensions
{
    /// <summary>
    /// Calls <c>AddOnnx</c> + <c>AddOnnxModel</c>, then delegates to
    /// <c>services.AddEmbeddingGenerator</c>, which registers BOTH the closed generic and the
    /// non-generic forwarding descriptor. Hand-registering would satisfy only one and break half
    /// of MEVD and Semantic Kernel.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="configure">Optional <see cref="OnnxEmbeddingOptions"/> configuration.</param>
    /// <returns>The same builder.</returns>
    public static EdgeBuilder AddOnnxEmbeddings(
        this EdgeBuilder builder,
        Action<OnnxEmbeddingOptions>? configure = null)
        => AddCore(builder, name: null, configure, pipeline: null);

    /// <summary>
    /// Same, plus the MEAI pipeline for <c>UseLogging</c> / <c>UseOpenTelemetry</c> /
    /// <c>UseDistributedCache</c>. SP2 ships no middleware of its own; those three already exist
    /// upstream.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="configure">Optional <see cref="OnnxEmbeddingOptions"/> configuration.</param>
    /// <param name="pipeline">The MEAI pipeline.</param>
    /// <returns>The same builder.</returns>
    public static EdgeBuilder AddOnnxEmbeddings(
        this EdgeBuilder builder,
        Action<OnnxEmbeddingOptions>? configure,
        Action<EmbeddingGeneratorBuilder<string, Embedding<float>>> pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return AddCore(builder, name: null, configure, pipeline);
    }

    /// <summary>Keyed registration, mirroring SP1's named-database convention.</summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="name">The service key.</param>
    /// <param name="configure">Optional <see cref="OnnxEmbeddingOptions"/> configuration.</param>
    /// <param name="pipeline">The optional MEAI pipeline.</param>
    /// <returns>The same builder.</returns>
    public static EdgeBuilder AddOnnxEmbeddings(
        this EdgeBuilder builder,
        string name,
        Action<OnnxEmbeddingOptions>? configure = null,
        Action<EmbeddingGeneratorBuilder<string, Embedding<float>>>? pipeline = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return AddCore(builder, name, configure, pipeline);
    }

    /// <summary>
    /// Startup order <see cref="EdgeAiStartupOrder.SessionWarmUp"/> (220). Builds this
    /// registration's tokenizer, so the first user-visible call does not pay vocabulary parsing.
    /// Off by default, because it forces provisioning and spec 10 keeps that lazy. Idempotent: a
    /// second call for the same registration adds no second task.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="name">The keyed registration to warm, or null for the unkeyed one.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="InvalidOperationException">
    /// No <c>AddOnnxEmbeddings</c> call on this builder carries <paramref name="name"/>. The preset
    /// to warm comes from that registration, so the order of the two calls matters.
    /// </exception>
    /// <remarks>
    /// <b>Two deviations from spec 7 and 14.1 live here, and both are deliberate.</b>
    /// <para>
    /// <b>1. It registers ONE task, not two.</b> Spec 7 presents this and
    /// <c>Qavren.Edge.Onnx</c>'s <c>WarmUpSessionAtStartup</c> as independent opt-ins, so this
    /// method no longer calls that one on the caller's behalf. It used to, and the coupling was
    /// wrong twice over: L0 registers its task with <c>AddSingleton</c> rather than a
    /// <c>TryAdd</c>, so an app that opted into both - or called this method twice - got duplicate
    /// order-220 session tasks. An app that wants the graph loaded at startup as well calls
    /// <c>builder.WarmUpSessionAtStartup(preset.Manifest.ModelId)</c> itself, which is the shape
    /// spec 7 describes.
    /// </para>
    /// <para>
    /// <b>2. It runs no dummy batch, which spec 7 and spec 14.1's order table both require</b>
    /// ("builds the tokenizer, acquires the session and embeds one short string"). It cannot, and
    /// the blocker is a layer boundary rather than an omission: embedding one string means
    /// <c>IOnnxSessionHost.AcquireAsync</c>, which awaits <c>IEdgeHost.EnsureStartedAsync</c>, and
    /// a startup task awaiting startup deadlocks on its own completion. The load path that skips
    /// that await, <c>OnnxSessionHost.AcquireCoreAsync</c>, is <c>internal</c> to
    /// <c>Qavren.Edge.Onnx</c>, and that assembly grants <c>InternalsVisibleTo</c> to test
    /// assemblies only - so no member of this package can drive a session load at startup.
    /// Closing this needs an owner ruling: either spec 7 and 14.1 drop the dummy batch, or
    /// <c>Qavren.Edge.Onnx</c> exposes a load path L1 can reach. Until then a first embed pays the
    /// graph load, the CoreML compile and the first <c>Run</c>; only vocabulary parsing is
    /// pre-paid.
    /// </para>
    /// </remarks>
    public static EdgeBuilder WarmUpEmbeddingsAtStartup(this EdgeBuilder builder, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _ = FindRegistration(builder.Services, name)
            ?? throw new InvalidOperationException(
                name is null
                    ? "Call AddOnnxEmbeddings(...) before WarmUpEmbeddingsAtStartup(): the preset "
                        + "to warm comes from the registered options."
                    : $"No AddOnnxEmbeddings registration is named '{name}'. Register it before "
                        + "calling WarmUpEmbeddingsAtStartup(name).");

        // Idempotent by inspecting the collection, because IEdgeStartupTask is an enumerable
        // service: a TryAddEnumerable on it would suppress every OTHER package's task rather than
        // a duplicate of this one.
        if (!AlreadyWarming(builder.Services, name))
        {
            builder.Services.AddSingleton<IEdgeStartupTask>(
                sp => new EmbeddingsWarmUpStartupTask(sp, name));
            builder.Services.AddSingleton(new EmbeddingWarmUpMarker(name));
        }

        return builder;
    }

    /// <summary>
    /// Records that <c>WarmUpEmbeddingsAtStartup</c> already registered a task for one
    /// registration, so calling it twice does not add a second order-220 task that parses the same
    /// vocabulary a second time.
    /// </summary>
    /// <param name="Name">The service key this warm-up covers, or null for the unkeyed one.</param>
    internal sealed record EmbeddingWarmUpMarker(string? Name);

    /// <summary>
    /// What one <c>AddOnnxEmbeddings</c> call left behind, so a later call on the same builder -
    /// <c>WarmUpEmbeddingsAtStartup</c>, or a second registration under the same name - can find it
    /// at REGISTRATION time, when no service provider exists yet. Registered as an instance so the
    /// lookup is a scan of the collection rather than a resolve.
    /// </summary>
    /// <param name="Name">The service key, or null for the unkeyed registration.</param>
    /// <param name="Options">The options instance that registration was built from.</param>
    internal sealed record EmbeddingRegistration(string? Name, OnnxEmbeddingOptions Options);

    private static bool AlreadyWarming(IServiceCollection services, string? name)
    {
        for (var i = 0; i < services.Count; i++)
        {
            if (services[i].ServiceType == typeof(EmbeddingWarmUpMarker)
                && services[i].ImplementationInstance is EmbeddingWarmUpMarker marker
                && string.Equals(marker.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static EmbeddingRegistration? FindRegistration(IServiceCollection services, string? name)
    {
        for (var i = 0; i < services.Count; i++)
        {
            if (services[i].ServiceType == typeof(EmbeddingRegistration)
                && services[i].ImplementationInstance is EmbeddingRegistration registration
                && string.Equals(registration.Name, name, StringComparison.Ordinal))
            {
                return registration;
            }
        }

        return null;
    }

    private static EdgeBuilder AddCore(
        EdgeBuilder builder,
        string? name,
        Action<OnnxEmbeddingOptions>? configure,
        Action<EmbeddingGeneratorBuilder<string, Embedding<float>>>? pipeline)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new OnnxEmbeddingOptions();
        configure?.Invoke(options);
        OnnxEmbeddingOptions.ApplyPinning(options);

        builder.AddOnnx();

        if (options.ModelSource is { } source)
        {
            builder.AddModelSource(_ => source);
        }
        else
        {
            builder.AddHuggingFaceModelSource();
        }

        builder.AddOnnxModel(options.Preset.Manifest, session => ApplyEmbeddingSessionSettings(options, session));

        builder.Services.TryAddSingleton<EdgeTokenizerProvider>();
        builder.Services.TryAddSingleton<IEdgeTokenizerProvider>(
            sp => sp.GetRequiredService<EdgeTokenizerProvider>());

        var accessor = Options.Create(options);

        // One diagnostics contributor per registration, closed over THIS registration's options and
        // key. Not TryAddEnumerable-by-type: that would register one contributor for the whole
        // process, and the one it registered would read the unkeyed IOptions - which a keyed-only
        // app never registers, so the block would report the default preset's dimensions and
        // pooling for an app running something else entirely. The marker below keeps a repeated
        // call with the same name from adding a second block for the same registration.
        if (FindRegistration(builder.Services, name) is null)
        {
            builder.Services.AddSingleton(new EmbeddingRegistration(name, options));
            builder.Services.AddSingleton<IEdgeDiagnosticsContributor>(sp =>
                new EmbeddingsDiagnosticsContributor(
                    accessor,
                    name,
                    sp.GetRequiredService<EdgeTokenizerProvider>(),
                    sp));
        }

        if (name is null)
        {
            // The unkeyed IOptions<OnnxEmbeddingOptions> the generator reads. TryAdd so a second
            // AddOnnxEmbeddings call does not silently replace the first registration's options.
            builder.Services.TryAddSingleton(accessor);

            var generatorBuilder = builder.Services.AddEmbeddingGenerator<string, Embedding<float>>(
                sp => Create(sp, accessor));

            pipeline?.Invoke(generatorBuilder);
        }
        else
        {
            builder.Services.TryAddKeyedSingleton(name, accessor);

            var keyedBuilder = builder.Services.AddKeyedEmbeddingGenerator<string, Embedding<float>>(
                name,
                sp => Create(sp, accessor));

            pipeline?.Invoke(keyedBuilder);
        }

        return builder;
    }

    private static OnnxEmbeddingGenerator Create(IServiceProvider services, IOptions<OnnxEmbeddingOptions> accessor)
        => new(
            services.GetRequiredService<IOnnxSessionHost>(),
            services.GetRequiredService<IEdgeTokenizerProvider>(),
            services.GetRequiredService<IEdgeResourceMonitor>(),
            accessor,
            accessor.Value.DefaultInputKind,
            services.GetRequiredService<ILogger<OnnxEmbeddingGenerator>>(),
            services.GetService<TimeProvider>() ?? TimeProvider.System);

    /// <summary>
    /// Copies the settings this package owns onto the per-model session options
    /// <c>AddOnnxModel</c> built from the consumer's execution-provider policy. The policy itself
    /// is deliberately NOT copied: it is configured through <c>UseExecutionProviderPolicy</c>, and
    /// overwriting it here with this options object's untouched defaults would silently discard
    /// the consumer's choice. The one execution-provider flag that IS carried across is
    /// <c>RequireStaticInputShapes</c>, because only pinning makes it meaningful and only this
    /// package knows whether pinning happened.
    /// </summary>
    private static void ApplyEmbeddingSessionSettings(OnnxEmbeddingOptions options, OnnxSessionOptions session)
    {
        session.DropOnMemoryPressure = options.Session.DropOnMemoryPressure;
        session.MemoryHeadroomFactor = options.Session.MemoryHeadroomFactor;
        session.MemoryHeadroomBytes = options.Session.MemoryHeadroomBytes;

        foreach (var (key, value) in options.Session.FreeDimensionOverrides)
        {
            session.FreeDimensionOverrides[key] = value;
        }

        foreach (var (key, value) in options.Session.SessionConfigEntries)
        {
            session.SessionConfigEntries[key] = value;
        }

        if (options.PinnedSequenceLength is not null)
        {
            session.ExecutionProviders.CoreMl.RequireStaticInputShapes = true;
        }
    }
}
