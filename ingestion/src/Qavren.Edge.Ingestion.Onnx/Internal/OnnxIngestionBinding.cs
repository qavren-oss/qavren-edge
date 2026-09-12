using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Ingestion.Internal;

namespace Qavren.Edge.Ingestion.Onnx.Internal;

/// <summary>
/// What one <c>AddOnnxIngestion</c> call left behind: the registration it binds to, and - once
/// resolved - the tokenizer. Registered as an instance, so the idempotence scan is a walk of the
/// collection rather than a resolve, and shared by the <see cref="IChunkTokenizer"/> factory and
/// <see cref="OnnxIngestionStartupTask"/> so both hand back the same instance.
/// </summary>
internal sealed class OnnxIngestionBinding(string? embeddingsName)
{
    private readonly Lock _gate = new();
    private EdgeChunkTokenizer? _tokenizer;

    /// <summary>The keyed <c>AddOnnxEmbeddings</c> registration, or null for the unkeyed one.</summary>
    public string? EmbeddingsName => embeddingsName;

    /// <summary>The tokenizer once built, or null.</summary>
    public EdgeChunkTokenizer? Tokenizer
    {
        get
        {
            lock (_gate)
            {
                return _tokenizer;
            }
        }
    }

    /// <summary>
    /// The synchronous path the <see cref="IChunkTokenizer"/> factory takes. On a started host the
    /// startup task has already built the tokenizer and this returns it without blocking; before
    /// start it blocks on SP2's provisioning, which awaits nothing on the host.
    /// </summary>
    public EdgeChunkTokenizer Resolve(IServiceProvider services) =>
        Tokenizer ?? ResolveAsync(services, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <summary>Resolves the generator, applies its preset to the matching registrations, builds the tokenizer.</summary>
    public async ValueTask<EdgeChunkTokenizer> ResolveAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (Tokenizer is { } built)
        {
            return built;
        }

        var generator = IngestionServiceResolution.FindGenerator(services, embeddingsName)
            ?? throw IngestionServiceResolution.GeneratorMissing(embeddingsName);
        var preset = ResolvePreset(generator);

        ApplyToRegistrations(services, preset);

        // THIS generator's tokenizer when it has one (built by a warm-up or a first embed), else
        // SP2's provider - which provisions the vocabulary and caches the build per preset.
        var edge = generator.GetService(typeof(IEdgeTokenizer)) as IEdgeTokenizer;
        if (edge is null)
        {
            var provider = services.GetService<IEdgeTokenizerProvider>()
                ?? throw NotOnnx("no IEdgeTokenizerProvider is registered");
            edge = await provider.GetAsync(preset, cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            return _tokenizer ??= new EdgeChunkTokenizer(edge, preset.LowerCase);
        }
    }

    /// <summary>
    /// The preset of the generator this binding names, or null when there is no such generator.
    /// Used by the startup task, which stays silent where the core's own order-400 task will
    /// report the absence as 6208.
    /// </summary>
    public EmbeddingPreset? TryResolvePreset(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var generator = IngestionServiceResolution.FindGenerator(services, embeddingsName);
        return generator is null ? null : ResolvePreset(generator);
    }

    /// <summary>Spec 11.2's projection, applied to every registration bound to this generator.</summary>
    public void ApplyToRegistrations(IServiceProvider services, EmbeddingPreset preset)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(preset);

        var registry = services.GetService<IngestionRegistry>();
        if (registry is null)
        {
            return;
        }

        foreach (var registration in registry.All)
        {
            if (string.Equals(registration.Options.StoreName, embeddingsName, StringComparison.Ordinal))
            {
                OnnxIngestionBuilderExtensions.ApplyPreset(registration.Options, preset);
            }
        }
    }

    private EmbeddingPreset ResolvePreset(IEmbeddingGenerator<string, Embedding<float>> generator) =>
        generator.GetService(typeof(EmbeddingPreset)) as EmbeddingPreset
            ?? throw NotOnnx("the registered generator publishes no EmbeddingPreset");

    private EdgeConfigurationException NotOnnx(string because) =>
        new(
            EdgeErrorCode.IngestionOptionsInvalid,
            $"AddOnnxIngestion({(embeddingsName is null ? string.Empty : "\"" + embeddingsName + "\"")}) needs " +
            $"the generator AddOnnxEmbeddings registers, but {because}. Call AddOnnxEmbeddings() under the same " +
            "name, or supply a tokenizer with UseChunkTokenizer() and a throttle with UseIngestionThrottle().");
}
