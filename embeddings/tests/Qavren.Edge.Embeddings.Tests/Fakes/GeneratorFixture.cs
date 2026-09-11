using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Embeddings.Onnx.Internal;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Onnx;
using Qavren.Edge.Onnx.Internal;
using Qavren.Edge.Tests.Fixtures;

namespace Qavren.Edge.Embeddings.Tests.Fakes;

/// <summary>SP1's paths, pointed at a scratch directory.</summary>
internal sealed class TempPaths(string root) : IEdgePaths
{
    public string Data { get; } = root;

    public string Cache { get; } = Path.Combine(root, "cache");
}

/// <summary>The model and ORT-cache roots, pointed at a scratch directory.</summary>
internal sealed class TempModelPaths(string root) : IEdgeModelPaths
{
    public string Models => Ensure("models");

    public string OrtCache => Ensure("ort-cache");

    private string Ensure(string leaf)
    {
        var directory = Path.Combine(root, leaf);
        Directory.CreateDirectory(directory);
        return directory;
    }
}

/// <summary>
/// A resource monitor that reads nothing and remembers everything, so a test about batching never
/// depends on what this machine's GC happens to report.
/// </summary>
internal sealed class StubResourceMonitor : IEdgeResourceMonitor
{
    public EdgeMemoryPressure? LastPressure { get; private set; }

    public EdgeResourceSnapshot Read() =>
        new(
            AvailableMemoryBytes: 1024L * 1024 * 1024,
            IsLowMemory: false,
            EdgeThermalState.Unknown,
            ThermalHeadroom: null,
            IsLowPowerMode: null,
            LastPressure: LastPressure);

    public void SetPressure(EdgeMemoryPressure? level) => LastPressure = level;
}

/// <summary>
/// A tokenizer that records the texts it was handed, so the prefix assertions can look at what
/// actually reached the encoder rather than inferring it from a vector.
/// </summary>
internal sealed class RecordingTokenizer(IEdgeTokenizer inner) : IEdgeTokenizer
{
    private readonly List<string> _seen = [];

    public IReadOnlyList<string> Seen
    {
        get
        {
            lock (_seen)
            {
                return [.. _seen];
            }
        }
    }

    public EdgeTokenizerKind Kind => inner.Kind;

    public int VocabularySize => inner.VocabularySize;

    public int MaxSequenceLength => inner.MaxSequenceLength;

    public int PadTokenId => inner.PadTokenId;

    public int Encode(ReadOnlySpan<char> text, int maxTokens, Span<int> destination, out int charsConsumed)
        => inner.Encode(text, maxTokens, destination, out charsConsumed);

    public TokenizedBatch EncodeBatch(IReadOnlyList<string> texts, int maxSequenceLength, IReadOnlyList<int> buckets)
    {
        lock (_seen)
        {
            _seen.AddRange(texts);
        }

        return inner.EncodeBatch(texts, maxSequenceLength, buckets);
    }

    public int CountTokens(ReadOnlySpan<char> text) => inner.CountTokens(text);

    public int IndexByTokenCount(string text, int maxTokens, out int tokenCount)
        => inner.IndexByTokenCount(text, maxTokens, out tokenCount);

    public void Dispose() => inner.Dispose();
}

/// <summary>
/// Hands out a tokenizer built over the committed fixture vocabulary. It never touches the model
/// store, because the fixture graph is a byte array with no manifest to provision.
/// </summary>
internal sealed class StubTokenizerProvider(RecordingTokenizer tokenizer) : IEdgeTokenizerProvider
{
    public IEdgeTokenizer? Current { get; private set; }

    public ValueTask<IEdgeTokenizer> GetAsync(EmbeddingPreset preset, CancellationToken cancellationToken = default)
    {
        Current = tokenizer;
        return ValueTask.FromResult<IEdgeTokenizer>(tokenizer);
    }

    /// <summary>
    /// Moves <see cref="Current"/> to some OTHER registration's tokenizer, which is what a second
    /// <c>AddOnnxEmbeddings</c> registration does to the shared provider the moment its generator
    /// embeds. Nothing a generator reports about itself may follow it.
    /// </summary>
    /// <param name="other">The tokenizer the other registration built.</param>
    public void PretendAnotherRegistrationEmbedded(IEdgeTokenizer other) => Current = other;
}

/// <summary>
/// Another registration's tokenizer: a distinct <see cref="VocabularySize"/> and nothing else. It
/// throws from every encode member, so a generator that reached for it instead of its own would
/// fail loudly in a test rather than quietly in production.
/// </summary>
internal sealed class ForeignTokenizer(int vocabularySize) : IEdgeTokenizer
{
    public EdgeTokenizerKind Kind => EdgeTokenizerKind.WordPieceVocabTxt;

    public int VocabularySize => vocabularySize;

    public int MaxSequenceLength => 512;

    public int PadTokenId => 0;

    public int Encode(ReadOnlySpan<char> text, int maxTokens, Span<int> destination, out int charsConsumed)
        => throw new InvalidOperationException("Another registration's tokenizer was used to encode.");

    public TokenizedBatch EncodeBatch(IReadOnlyList<string> texts, int maxSequenceLength, IReadOnlyList<int> buckets)
        => throw new InvalidOperationException("Another registration's tokenizer was used to encode.");

    public int CountTokens(ReadOnlySpan<char> text)
        => throw new InvalidOperationException("Another registration's tokenizer was used to count.");

    public int IndexByTokenCount(string text, int maxTokens, out int tokenCount)
        => throw new InvalidOperationException("Another registration's tokenizer was used to index.");

    public void Dispose()
    {
    }
}

/// <summary>
/// A model store that provisions nothing and hands every manifest the same on-disk fixture
/// vocabulary. It exists so <see cref="EdgeTokenizerProvider"/> can be exercised with two presets
/// without a download: the real store's job - fetch, verify, place - is Task 3.1's, not this one's.
/// </summary>
internal sealed class StubModelStore(string vocabularyPath) : IOnnxModelStore
{
    private readonly List<string> _provisioned = [];

    public IReadOnlyList<string> ProvisionedModelIds
    {
        get
        {
            lock (_provisioned)
            {
                return [.. _provisioned];
            }
        }
    }

    public ValueTask<ProvisionedModel> EnsureAsync(
        OnnxModelManifest manifest,
        IProgress<ModelProvisioningProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        lock (_provisioned)
        {
            _provisioned.Add(manifest.ModelId);
        }

        var directory = Path.GetDirectoryName(vocabularyPath)!;

        return ValueTask.FromResult(new ProvisionedModel(
            manifest.ModelId,
            directory,
            Path.Combine(directory, manifest.GraphFile),
            GraphSha256: string.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["vocab.txt"] = vocabularyPath },
            ModelProvisioningSource.AlreadyPresent,
            TimeSpan.Zero));
    }

    public bool IsProvisioned(OnnxModelManifest manifest) => true;

    public ProvisionedModel? TryGet(string modelId) => null;

    public ValueTask RemoveAsync(string modelId, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

/// <summary>
/// One generator over one tier-1 fixture graph. The registration hook that creates a session from
/// a byte array is <c>InternalsVisibleTo</c>-scoped to this assembly precisely because a
/// 533-831-byte graph makes both of the costs that keep it off the product surface irrelevant.
/// </summary>
internal sealed class GeneratorFixture : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly string _root;

    private GeneratorFixture(
        ServiceProvider provider,
        OnnxEmbeddingGenerator generator,
        RecordingTokenizer tokenizer,
        StubTokenizerProvider tokenizers,
        StubResourceMonitor resources,
        IOnnxSessionHost host,
        string root)
    {
        _provider = provider;
        _root = root;
        Generator = generator;
        Tokenizer = tokenizer;
        Tokenizers = tokenizers;
        Resources = resources;
        Host = host;
    }

    public OnnxEmbeddingGenerator Generator { get; }

    public RecordingTokenizer Tokenizer { get; }

    /// <summary>The shared provider, so a test can move its <c>Current</c> out from under the generator.</summary>
    public StubTokenizerProvider Tokenizers { get; }

    public StubResourceMonitor Resources { get; }

    public IOnnxSessionHost Host { get; }

    /// <summary>
    /// A four-dimension preset over one of the tier-1 graphs, with a single <c>[8]</c> sequence
    /// bucket so the padded and unpadded token counts differ by a number a reader can check by hand.
    /// </summary>
    public static EmbeddingPreset Preset(
        string modelId,
        string? queryPrefix = null,
        string? documentPrefix = null,
        string? tokenTypeIdsName = null,
        EmbeddingPooling pooling = EmbeddingPooling.Mean,
        bool postPoolLayerNorm = false,
        bool normalize = true) => new()
        {
            Id = "tiny-" + modelId,
            Manifest = new OnnxModelManifest
            {
                ModelId = modelId,
                Files = [],
                GraphFile = "model.onnx",
                SpdxLicense = "Apache-2.0",
            },
            ModelFile = "model.onnx",
            TokenizerFile = "vocab.txt",
            TokenizerKind = EdgeTokenizerKind.WordPieceVocabTxt,
            LowerCase = true,
            Dimensions = 4,
            MaxSequenceLength = 8,
            Pooling = pooling,
            PostPoolLayerNorm = postPoolLayerNorm,
            Normalize = normalize,
            QueryPrefix = queryPrefix,
            DocumentPrefix = documentPrefix,
            TokenTypeIdsName = tokenTypeIdsName,
            SequenceBuckets = [8],
        };

    /// <summary>Builds a generator over <c>TinyModels.HiddenStates</c>.</summary>
    public static GeneratorFixture CreateOverHiddenStatesFixture(
        TimeProvider timeProvider,
        EmbeddingPreset? preset = null,
        Action<OnnxEmbeddingOptions>? configure = null)
        => Create(TinyModels.HiddenStates, ["input_ids", "attention_mask"], preset, timeProvider, configure);

    /// <summary>
    /// Builds a generator over <c>TinyModels.NomicInputOrder</c>, whose graph declares
    /// <c>(input_ids, token_type_ids, attention_mask)</c> - the reversed order that a positional
    /// binding would feed the mask in as token-type ids.
    /// </summary>
    public static GeneratorFixture CreateOverNomicInputOrderFixture(TimeProvider timeProvider)
        => Create(
            TinyModels.NomicInputOrder,
            ["input_ids", "token_type_ids", "attention_mask"],
            Preset("fixture-nomic-order", tokenTypeIdsName: "token_type_ids"),
            timeProvider,
            configure: null);

    public static GeneratorFixture Create(
        string graphBase64,
        string[] declaredInputs,
        EmbeddingPreset? preset,
        TimeProvider timeProvider,
        Action<OnnxEmbeddingOptions>? configure)
    {
        preset ??= Preset("fixture-hidden-states");

        var root = Path.Combine(Path.GetTempPath(), "qedge-embed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var resources = new StubResourceMonitor();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new TempPaths(root));
        services.AddSingleton<IEdgeResourceMonitor>(resources);
        services.AddSingleton(timeProvider);

        services.AddQavrenEdge(edge =>
        {
            edge.AddOnnx();
            edge.UseModelPaths(new TempModelPaths(root));
            edge.AddOnnxModelFromBytes(
                preset.Manifest.ModelId,
                Convert.FromBase64String(graphBase64),
                new OnnxSessionExpectation(declaredInputs, preset.OutputName, preset.Dimensions));
        });

        var provider = services.BuildServiceProvider();

        var options = new OnnxEmbeddingOptions { Preset = preset, MaxBatchSize = 16 };
        configure?.Invoke(options);

        var tokenizer = new RecordingTokenizer(TestVocabulary.Tokenizer(preset.MaxSequenceLength));
        var tokenizers = new StubTokenizerProvider(tokenizer);
        var generator = new OnnxEmbeddingGenerator(
            provider.GetRequiredService<IOnnxSessionHost>(),
            tokenizers,
            resources,
            Options.Create(options),
            options.DefaultInputKind,
            provider.GetRequiredService<ILogger<OnnxEmbeddingGenerator>>(),
            timeProvider);

        return new GeneratorFixture(
            provider,
            generator,
            tokenizer,
            tokenizers,
            resources,
            provider.GetRequiredService<IOnnxSessionHost>(),
            root);
    }

    public void Dispose()
    {
        Generator.Dispose();
        _provider.Dispose();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked ORT cache file must not fail a green test run.
        }
    }
}
