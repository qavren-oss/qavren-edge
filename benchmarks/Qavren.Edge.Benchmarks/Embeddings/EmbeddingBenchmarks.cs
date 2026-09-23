using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Benchmarks.Infrastructure;
using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Benchmarks.Embeddings;

/// <summary>
/// The real int8 MiniLM (<see cref="EmbeddingPresets.MiniLmL6V2Int8"/>) staged in
/// <c>QAVREN_EDGE_MODEL_DIR</c>, composed exactly as the tier-3 tests compose it: a
/// <see cref="FileOnnxModelSource"/> over the staged directory, so nothing can download. Gated by
/// <c>Program</c>: this class is selected only when the directory holds the MiniLM layout.
/// </summary>
/// <remarks>
/// The int8 graph's speed depends on the CPU's integer kernels (AVX2 u8s8, AVX-512 VNNI, ARM
/// NEON), so these numbers belong to one kernel class, never to "x64" or "arm64" at large.
/// </remarks>
[BenchmarkCategory("Embeddings")]
public class EmbeddingBenchmarks
{
    /// <summary>The environment variable the tier-3 tests and this class read.</summary>
    public const string ModelDirVariable = "QAVREN_EDGE_MODEL_DIR";

    /// <summary>32 short English sentences, 6-14 words: the shape of a query or a chat turn.</summary>
    internal static readonly string[] Sentences =
    [
        "The warranty covers parts and labour for two years.",
        "How do I reset the thermostat to its factory settings?",
        "Batteries should be replaced every twelve months.",
        "The compressor is covered for seven years from purchase.",
        "Store the device in a cool, dry place away from sunlight.",
        "Our office is closed on public holidays.",
        "The quarterly report shows revenue grew by eight percent.",
        "Please attach the receipt when you file a claim.",
        "A firmware update fixes the pairing problem on older phones.",
        "The river floods most springs after heavy snowmelt.",
        "Clean the filter once a month to keep airflow steady.",
        "Which cables do I need to connect the soundbar?",
        "The meeting was moved to Thursday afternoon.",
        "Shipping takes three to five business days.",
        "The recipe calls for two cups of flour and one egg.",
        "Accidental damage is not covered by the standard plan.",
        "Turn the valve clockwise until it stops.",
        "The library opens at nine and closes at six.",
        "Error code 42 means the water supply is blocked.",
        "The new model uses thirty percent less energy.",
        "Customers can return unopened items within thirty days.",
        "The train to the airport leaves every fifteen minutes.",
        "Keep the child lock enabled when the oven is hot.",
        "The survey asked about sleep, diet and exercise.",
        "Replace the seal if you notice water under the door.",
        "Our support line answers calls around the clock.",
        "The garden needs watering twice a week in summer.",
        "Install the app, then scan the code on the back panel.",
        "The invoice lists the serial number and the date.",
        "Wind speeds reached ninety kilometres per hour overnight.",
        "The manual is available in six languages.",
        "Descale the kettle when white deposits appear.",
    ];

    private static readonly Lazy<IEdgeTokenizer> HostTokenizer = new(CreateTokenizer);

    private EdgeHostScope? _host;
    private IEmbeddingGenerator<string, Embedding<float>>? _generator;
    private string[] _batch = [];

    [Params(1, 8, 32)]
    public int Batch { get; set; }

    /// <summary>Real (unpadded) tokens in one batch, special tokens included, for the throughput column.</summary>
    public static Func<IReadOnlyDictionary<string, object>, double> TokensInBatch =>
        parameters => CountTokens(
            Sentences.Take(Convert.ToInt32(parameters["Batch"], System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Real tokens across all 32 sentences, for the encode benchmark.</summary>
    internal static double TokensInCorpus => CountTokens(Sentences);

    /// <summary>Sentences per encode invocation.</summary>
    internal static int SentenceCount => Sentences.Length;

    /// <summary>True when <see cref="ModelDirVariable"/> holds the MiniLM layout the tier-3 tests expect.</summary>
    public static bool ModelAvailable =>
        Environment.GetEnvironmentVariable(ModelDirVariable) is { Length: > 0 } dir
        && File.Exists(Path.Combine(dir, "onnx", "model_qint8_arm64.onnx"))
        && File.Exists(Path.Combine(dir, "vocab.txt"));

    private static string StagedDirectory =>
        Environment.GetEnvironmentVariable(ModelDirVariable) is { Length: > 0 } dir
            ? dir
            : throw new InvalidOperationException(ModelDirVariable + " is not set.");

    [GlobalSetup]
    public void Setup()
    {
        var staged = StagedDirectory;
        _host = EdgeHostScope.Start("embeddings", sqlite: false, (edge, paths) =>
        {
            edge.UseModelPaths(paths);
            edge.AddOnnxEmbeddings(o =>
            {
                o.Preset = EmbeddingPresets.MiniLmL6V2Int8;
                o.ModelSource = new FileOnnxModelSource(paths, staged);
            });
        });

        _generator = _host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        _batch = [.. Sentences.Take(Batch)];

        // Provision and load the session outside the measurement.
        var warm = _generator.GenerateAsync(_batch).GetAwaiter().GetResult();
        if (warm.Count != Batch || warm[0].Vector.Length != EmbeddingPresets.MiniLmL6V2Int8.Dimensions)
        {
            throw new InvalidOperationException($"the warm-up returned {warm.Count} embeddings for {Batch} inputs.");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _host?.Dispose();
    }

    /// <summary>One <c>GenerateAsync</c> call over a batch of <see cref="Batch"/> sentences.</summary>
    [Benchmark]
    [Throughput("sentences", nameof(Batch))]
    [Throughput("tokens", nameof(TokensInBatch))]
    public async Task<int> Generate()
    {
        var embeddings = await _generator!.GenerateAsync(_batch).ConfigureAwait(false);
        return embeddings.Count;
    }

    internal static IEdgeTokenizer CreateTokenizer()
    {
        var preset = EmbeddingPresets.MiniLmL6V2Int8;
        return EdgeTokenizer.CreateWordPiece(
            Path.Combine(StagedDirectory, "vocab.txt"),
            new WordPieceTokenizerOptions
            {
                LowerCase = preset.LowerCase,
                MaxSequenceLength = preset.MaxSequenceLength,
            });
    }

    private static double CountTokens(IEnumerable<string> sentences)
    {
        var tokenizer = HostTokenizer.Value;
        var ids = new int[EmbeddingPresets.MiniLmL6V2Int8.MaxSequenceLength];
        var total = 0;
        foreach (var sentence in sentences)
        {
            total += tokenizer.Encode(sentence, ids.Length, ids, out _);
        }

        return total;
    }
}

/// <summary>
/// WordPiece encode of the same 32 sentences with the MiniLM vocabulary, through the public
/// <see cref="EdgeTokenizer"/> - the step every embedding call pays before the graph runs.
/// </summary>
[BenchmarkCategory("Embeddings")]
public class TokenizerBenchmarks
{
    private IEdgeTokenizer? _tokenizer;
    private int[] _ids = [];

    /// <summary>Sentences per invocation.</summary>
    public static int SentenceCount => EmbeddingBenchmarks.SentenceCount;

    /// <summary>Real tokens per invocation.</summary>
    public static double TokensInCorpus => EmbeddingBenchmarks.TokensInCorpus;

    [GlobalSetup]
    public void Setup()
    {
        _tokenizer = EmbeddingBenchmarks.CreateTokenizer();
        _ids = new int[EmbeddingPresets.MiniLmL6V2Int8.MaxSequenceLength];
    }

    [GlobalCleanup]
    public void Cleanup() => _tokenizer?.Dispose();

    [Benchmark]
    [Throughput("sentences", nameof(SentenceCount))]
    [Throughput("tokens", nameof(TokensInCorpus))]
    public int Encode32()
    {
        var total = 0;
        foreach (var sentence in EmbeddingBenchmarks.Sentences)
        {
            total += _tokenizer!.Encode(sentence, _ids.Length, _ids, out _);
        }

        return total;
    }
}
