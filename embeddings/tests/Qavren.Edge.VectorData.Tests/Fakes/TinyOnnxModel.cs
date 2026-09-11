using System.Security.Cryptography;
using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Onnx;
using Qavren.Edge.Tests.Fixtures;

namespace Qavren.Edge.VectorData.Tests.Fakes;

/// <summary>
/// The committed tier-1 fixture graph, staged on disk and wired up through the PUBLIC provisioning
/// seam so the happy-path test runs the real <c>AddOnnxEmbeddings</c> pipeline - real
/// <c>OnnxModelStore</c>, real digest verification, a real ORT session, real inference - and still
/// downloads nothing.
/// <para>
/// The graph is <c>TinyModels.HiddenStates</c>: a single <c>Gather</c> over a 16 x 4 float32
/// initializer where row <c>t</c> is <c>[t, t+0.5, t+0.25, t+0.75]</c>. Sixteen rows is why the
/// tokenizer below emits ids below 16, and four columns is why the happy-path record declares a
/// 4-d vector rather than spec 4.3's 384.
/// </para>
/// </summary>
internal sealed class TinyOnnxModel : IDisposable
{
    /// <summary>The id both the manifest and <c>IOnnxSessionHost.AcquireAsync</c> use.</summary>
    public const string ModelId = "qavren-edge-vectordata-fixture";

    /// <summary>The fixture graph's output width, and therefore the store's declared vector width.</summary>
    public const int Dimensions = 4;

    private TinyOnnxModel(string root, EmbeddingPreset preset, IOnnxModelSource source, IEdgeModelPaths paths)
    {
        Root = root;
        Preset = preset;
        Source = source;
        ModelPaths = paths;
        Tokenizers = new RecordingTokenizerProvider(new FixtureTokenizer(preset.MaxSequenceLength));
    }

    /// <summary>The scratch root holding the staging directory and the model store's own tree.</summary>
    public string Root { get; }

    /// <summary>The preset <c>AddOnnxEmbeddings</c> is configured with.</summary>
    public EmbeddingPreset Preset { get; }

    /// <summary>The source that satisfies the manifest from the staging directory.</summary>
    public IOnnxModelSource Source { get; }

    /// <summary>Where the store provisions into. Registered with <c>UseModelPaths</c>.</summary>
    public IEdgeModelPaths ModelPaths { get; }

    /// <summary>
    /// The tokenizer provider to register BEFORE <c>AddOnnxEmbeddings</c>, which registers its own
    /// with <c>TryAddSingleton</c> and therefore leaves this one in place. It records every text it
    /// is handed, which is how the happy-path test sees which prefix reached the encoder.
    /// </summary>
    public RecordingTokenizerProvider Tokenizers { get; }

    /// <summary>Stages the graph and builds the preset around it.</summary>
    /// <param name="queryPrefix">The preset's query-side instruction prefix.</param>
    /// <param name="documentPrefix">The preset's document-side instruction prefix.</param>
    /// <returns>The staged fixture, deleted on dispose.</returns>
    public static TinyOnnxModel Stage(string? queryPrefix = null, string? documentPrefix = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-vectordata-onnx", Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(staging);

        var graph = Convert.FromBase64String(TinyModels.HiddenStates);
        var graphPath = Path.Combine(staging, "model.onnx");
        File.WriteAllBytes(graphPath, graph);

        var manifest = new OnnxModelManifest
        {
            ModelId = ModelId,
            GraphFile = "model.onnx",
            SpdxLicense = "Apache-2.0",
            Files =
            [
                new OnnxModelFile(
                    "model.onnx",
                    OnnxModelFileRole.Graph,
                    graph.Length,
                    Convert.ToHexStringLower(SHA256.HashData(graph))),
            ],
        };

        var preset = new EmbeddingPreset
        {
            Id = "vectordata-tier2-fixture",
            Manifest = manifest,
            ModelFile = "model.onnx",

            // Never read: the tokenizer provider below is registered ahead of the real one, so no
            // vocabulary is ever provisioned. The preset still has to name one, because the field
            // is required and a preset with no tokenizer asset is not a thing the type allows.
            TokenizerFile = "vocab.txt",
            TokenizerKind = EdgeTokenizerKind.WordPieceVocabTxt,
            LowerCase = true,
            Dimensions = Dimensions,
            MaxSequenceLength = 8,
            SequenceBuckets = [8],
            Pooling = EmbeddingPooling.Mean,
            Normalize = true,
            QueryPrefix = queryPrefix,
            DocumentPrefix = documentPrefix,

            // The fixture graph declares (input_ids, attention_mask) and nothing else.
            TokenTypeIdsName = null,
        };

        var paths = new StagedModelPaths(root);
        return new TinyOnnxModel(root, preset, new FileOnnxModelSource(paths, staging), paths);
    }

    /// <summary>Deletes the scratch root.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked ORT cache file must not fail a green test run.
        }
    }

    private sealed class StagedModelPaths(string root) : IEdgeModelPaths
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
}

/// <summary>
/// Hands out one tokenizer and remembers every text that reached it. Registered before
/// <c>AddOnnxEmbeddings</c>, whose own provider registration is a <c>TryAddSingleton</c>.
/// </summary>
internal sealed class RecordingTokenizerProvider(FixtureTokenizer tokenizer) : IEdgeTokenizerProvider
{
    /// <summary>Every text handed to the encoder, in order, prefix included.</summary>
    public IReadOnlyList<string> Seen => tokenizer.Seen;

    /// <inheritdoc />
    public IEdgeTokenizer? Current { get; private set; }

    /// <inheritdoc />
    public ValueTask<IEdgeTokenizer> GetAsync(EmbeddingPreset preset, CancellationToken cancellationToken = default)
    {
        Current = tokenizer;
        return ValueTask.FromResult<IEdgeTokenizer>(tokenizer);
    }
}

/// <summary>
/// A whitespace tokenizer over the fixture graph's 16-row embedding table. It exists so the
/// happy-path test needs no vocabulary asset: ids are hashed into 3..14, which keeps every
/// <c>Gather</c> index inside the table, and 0 is the pad id whose row the attention mask removes.
/// </summary>
internal sealed class FixtureTokenizer(int maxSequenceLength) : IEdgeTokenizer
{
    private const int PadId = 0;
    private const int ClassificationId = 1;
    private const int SeparatorId = 2;
    private const int FirstWordId = 3;
    private const int WordIdCount = 12;

    private readonly List<string> _seen = [];

    /// <summary>Every text this tokenizer encoded, in order.</summary>
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

    /// <inheritdoc />
    public EdgeTokenizerKind Kind => EdgeTokenizerKind.WordPieceVocabTxt;

    /// <inheritdoc />
    public int VocabularySize => 16;

    /// <inheritdoc />
    public int MaxSequenceLength => maxSequenceLength;

    /// <inheritdoc />
    public int PadTokenId => PadId;

    /// <inheritdoc />
    public int Encode(ReadOnlySpan<char> text, int maxTokens, Span<int> destination, out int charsConsumed)
    {
        var ids = Ids(text.ToString(), maxTokens, out var truncated);
        ids.CopyTo(destination);
        charsConsumed = truncated ? 0 : text.Length;
        return ids.Length;
    }

    /// <inheritdoc />
    public TokenizedBatch EncodeBatch(IReadOnlyList<string> texts, int maxSequenceLength, IReadOnlyList<int> buckets)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(buckets);

        lock (_seen)
        {
            _seen.AddRange(texts);
        }

        var encoded = new int[texts.Count][];
        var truncated = new bool[texts.Count];
        var counts = new int[texts.Count];
        var longest = 1;
        for (var i = 0; i < texts.Count; i++)
        {
            encoded[i] = Ids(texts[i], maxSequenceLength, out truncated[i]);
            counts[i] = encoded[i].Length;
            longest = Math.Max(longest, encoded[i].Length);
        }

        var width = buckets.FirstOrDefault(b => b >= longest, maxSequenceLength);
        var length = texts.Count * width;
        var ids = new long[length];
        var mask = new long[length];
        var types = new long[length];

        for (var row = 0; row < texts.Count; row++)
        {
            for (var column = 0; column < encoded[row].Length; column++)
            {
                ids[(row * width) + column] = encoded[row][column];
                mask[(row * width) + column] = 1;
            }
        }

        return new TokenizedBatch(ids, mask, types, texts.Count, width, counts, truncated);
    }

    /// <inheritdoc />
    public int CountTokens(ReadOnlySpan<char> text) => Ids(text.ToString(), int.MaxValue, out _).Length;

    /// <inheritdoc />
    public int IndexByTokenCount(string text, int maxTokens, out int tokenCount)
    {
        ArgumentNullException.ThrowIfNull(text);

        tokenCount = Math.Min(CountTokens(text), maxTokens);
        return text.Length;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private static int[] Ids(string text, int maxTokens, out bool truncated)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var wanted = words.Length + 2;
        truncated = wanted > maxTokens;

        var room = Math.Max(0, Math.Min(wanted, maxTokens) - 2);
        var ids = new int[room + 2];
        ids[0] = ClassificationId;
        for (var i = 0; i < room; i++)
        {
            ids[i + 1] = FirstWordId + (int)(Hash(words[i]) % WordIdCount);
        }

        ids[^1] = SeparatorId;
        return ids;
    }

    private static uint Hash(string word)
    {
        var hash = 2166136261u;
        foreach (var c in word)
        {
            unchecked
            {
                hash = (hash ^ char.ToLowerInvariant(c)) * 16777619u;
            }
        }

        return hash;
    }
}
