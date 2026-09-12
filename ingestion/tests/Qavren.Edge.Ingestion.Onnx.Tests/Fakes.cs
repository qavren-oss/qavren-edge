using Microsoft.Extensions.AI;
using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Ingestion.Onnx.Tests;

/// <summary>
/// A whitespace <see cref="IEdgeTokenizer"/>, so a test about registration or arithmetic never
/// reads a vocabulary. <see cref="IndexByTokenCount"/> deliberately returns nonsense: the bridge
/// must never call it, and a test that sees 999 back knows it did.
/// </summary>
internal sealed class FakeEdgeTokenizer(int maxSequenceLength = 256) : IEdgeTokenizer
{
    public const int WrongIndex = 999;

    public EdgeTokenizerKind Kind => EdgeTokenizerKind.WordPieceVocabTxt;

    public int VocabularySize => 42;

    public int MaxSequenceLength => maxSequenceLength;

    public int PadTokenId => 0;

    public int CountCalls { get; private set; }

    public int Encode(ReadOnlySpan<char> text, int maxTokens, Span<int> destination, out int charsConsumed) =>
        throw new NotSupportedException();

    public TokenizedBatch EncodeBatch(IReadOnlyList<string> texts, int maxSequenceLength, IReadOnlyList<int> buckets) =>
        throw new NotSupportedException();

    public int CountTokens(ReadOnlySpan<char> text)
    {
        CountCalls++;
        var count = 0;
        var inWord = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                inWord = false;
                continue;
            }

            if (!inWord)
            {
                count++;
                inWord = true;
            }
        }

        return count;
    }

    public int IndexByTokenCount(string text, int maxTokens, out int tokenCount)
    {
        tokenCount = WrongIndex;
        return WrongIndex;
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// A generator that publishes a preset and, optionally, a tokenizer through <c>GetService</c> -
/// the two routes <c>AddOnnxIngestion</c> reads - and never embeds anything.
/// </summary>
internal sealed class PresetGenerator(EmbeddingPreset? preset, IEdgeTokenizer? tokenizer = null)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Registration tests never embed.");

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(EmbeddingPreset))
        {
            return preset;
        }

        if (serviceType == typeof(IEdgeTokenizer))
        {
            return tokenizer;
        }

        if (serviceType == typeof(EmbeddingGeneratorMetadata) && preset is not null)
        {
            return new EmbeddingGeneratorMetadata("preset", defaultModelDimensions: preset.Dimensions);
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }
}

/// <summary>An <see cref="IEdgeResourceMonitor"/> that reads whatever the test set.</summary>
internal sealed class StubResourceMonitor : IEdgeResourceMonitor
{
    public EdgeResourceSnapshot Next { get; set; } = new(
        AvailableMemoryBytes: 1024L * 1024 * 1024,
        IsLowMemory: false,
        EdgeThermalState.Nominal,
        ThermalHeadroom: null,
        IsLowPowerMode: false,
        LastPressure: null);

    public int ReadCount { get; private set; }

    public EdgeMemoryPressure? LastPressure => Next.LastPressure;

    public EdgeResourceSnapshot Read()
    {
        ReadCount++;
        return Next;
    }

    public void SetPressure(EdgeMemoryPressure? level) => Next = Next with { LastPressure = level };
}

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
