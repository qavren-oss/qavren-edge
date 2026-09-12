using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Logging;

namespace Qavren.Edge.Ingestion.DataIngestion.Tests;

/// <summary>
/// A whitespace tokenizer shaped like <see cref="ChunkModelProfile.MiniLmL6V2Int8"/>: a 256-token
/// ceiling and an overhead of 2, so <see cref="ChunkOptions.Resolve"/> produces the real 222 / 32 / 27.
/// The prefix search is a plain scan — this project cannot see the core's internal
/// <c>TokenIndexSearch</c>, and forty lines of scan are cheaper than an <c>InternalsVisibleTo</c>
/// that would be a wave-2 write.
/// </summary>
internal sealed class WhitespaceTokenizer(int maxSequenceLength = 256, int specialTokenOverhead = 2)
    : IChunkTokenizer
{
    public string Id => "fake-whitespace";

    public int MaxSequenceLength => maxSequenceLength;

    public int SpecialTokenOverhead => specialTokenOverhead;

    public int CountTokens(ReadOnlySpan<char> text)
    {
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
        ArgumentNullException.ThrowIfNull(text);

        // The largest i such that text[0..i) costs at most maxTokens: walk words, stop before the
        // word that would be one too many.
        var count = 0;
        var inWord = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                inWord = false;
                continue;
            }

            if (!inWord)
            {
                if (count == maxTokens)
                {
                    tokenCount = count;
                    return i;
                }

                count++;
                inWord = true;
            }
        }

        tokenCount = count;
        return text.Length;
    }
}

/// <summary>Records every <c>GenerateAsync</c> call, and embeds deterministically off the first char.</summary>
internal sealed class RecordingEmbeddingGenerator(int dimensions = 384)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly ConcurrentQueue<IReadOnlyList<string>> _calls = new();

    public IReadOnlyList<IReadOnlyList<string>> Calls => [.. _calls];

    public int CallCount => _calls.Count;

    /// <summary>Deterministic and distinct per text: a phase shift seeded by a hash of every char.</summary>
    public static float[] VectorFor(string value, int dimensions = 384)
    {
        var vector = new float[dimensions];
        var seed = 17;
        foreach (var ch in value)
        {
            seed = unchecked((seed * 31) + ch) % 100_000;
        }

        for (var i = 0; i < dimensions; i++)
        {
            vector[i] = (float)Math.Sin((seed + i) * 0.001);
        }

        return vector;
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var inputs = values.ToList();
        _calls.Enqueue(inputs);

        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in inputs)
        {
            result.Add(new Embedding<float>(VectorFor(value, dimensions)));
        }

        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType == typeof(EmbeddingGeneratorMetadata)
            ? new EmbeddingGeneratorMetadata("recording", defaultModelDimensions: dimensions)
            : serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }
}

/// <summary>An <see cref="ILogger"/> that keeps every record, so an event id can be counted.</summary>
internal sealed class CapturingLogger : ILogger
{
    public List<(int EventId, LogLevel Level, string Message)> Records { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        Records.Add((eventId.Id, logLevel, formatter(state, exception)));
    }

    public int Count(int eventId) => Records.Count(r => r.EventId == eventId);
}

/// <summary>
/// The fifteen-line reader spec 14.4 asks for: SP3's own <see cref="MarkdownExtractor"/> behind
/// MEDI's reader contract, through <see cref="EdgeDocumentConverter.ToMedi"/>. It exists so the
/// conformance test never restores <c>Microsoft.Extensions.DataIngestion.Markdig</c> — see README.md.
/// </summary>
internal sealed class MarkdownExtractorReader : IngestionDocumentReader
{
    private readonly MarkdownExtractor _extractor = new();

    public List<string> Identifiers { get; } = [];

    public override async Task<IngestionDocument> ReadAsync(
        Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
    {
        Identifiers.Add(identifier);
        var item = new DocumentSourceItem(identifier, mediaType, _ => new ValueTask<Stream>(source));
        var context = new ExtractionContext(
            "medi-conformance", "chunks", 32L * 1024 * 1024, new ExtractionOptions(), new CapturingLogger());
        var extracted = await _extractor.ExtractAsync(item, context, cancellationToken).ConfigureAwait(false);
        return EdgeDocumentConverter.ToMedi(extracted);
    }
}

/// <summary>A reader that hands back a pre-built document and records what it was asked for.</summary>
internal sealed class StubReader(IngestionDocument document) : IngestionDocumentReader
{
    public List<(string Identifier, string MediaType, long Length)> Reads { get; } = [];

    public override Task<IngestionDocument> ReadAsync(
        Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Reads.Add((identifier, mediaType, source.Length));
        return Task.FromResult(document);
    }
}
