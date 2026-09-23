using System.Globalization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Qavren.Edge.Benchmarks.Infrastructure;
using Qavren.Edge.Ingestion;
using Qavren.Edge.VectorData;

namespace Qavren.Edge.Benchmarks.Ingestion;

/// <summary>
/// <c>Qavren.Edge.Ingestion</c> over the 200-document synthetic Markdown corpus (see
/// <see cref="Corpus"/>): the two core extractors, the three chunkers, content hashing, and an
/// incremental re-run of the whole pipeline over an unchanged corpus. Every benchmark processes
/// all 200 documents per invocation.
/// </summary>
/// <remarks>
/// The chunk tokenizer is <see cref="EdgeTokenCounter.CreateWordPiece(Stream, int, bool)"/> over a
/// vocabulary generated from the corpus (every word is one token), at MiniLM's 256-token ceiling, so
/// the chunkers run the real WordPiece path with no model download. The pipeline's embedding
/// generator is <see cref="HashingEmbeddingGenerator"/>; the re-run embeds nothing either way.
/// </remarks>
[BenchmarkCategory("Ingestion")]
public class IngestionBenchmarks
{
    private const string SourceId = "bench-corpus";

    private readonly PlainTextExtractor _text = new();
    private readonly MarkdownExtractor _markdown = new();
    private readonly PlainChunker _plain = new();
    private readonly MarkdownHeadingChunker _heading = new();
    private readonly TokenWindowChunker _window = new();

    private EdgeHostScope? _host;
    private IIngestionPipeline? _pipeline;
    private IChunkTokenizer? _tokenizer;
    private ResolvedChunkOptions? _budget;
    private ExtractionContext? _context;
    private DocumentSourceItem[] _memoryItems = [];
    private DocumentSourceItem[] _fileItems = [];
    private ExtractedDocument[] _extracted = [];
    private byte[][] _bytes = [];
    private int _opens;

    /// <summary>Documents per invocation, for the throughput column.</summary>
    public static int Documents => Corpus.DocumentCount;

    [GlobalSetup]
    public void Setup()
    {
        var vocabulary = Corpus.Vocabulary();
        var generator = new HashingEmbeddingGenerator();

        _host = EdgeHostScope.Start("ingestion", sqlite: true, (edge, _) =>
        {
            edge.AddVectorStore();
            edge.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(generator);
            edge.UseChunkTokenizer(_ => EdgeTokenCounter.CreateWordPiece(
                new MemoryStream(vocabulary, writable: false), ChunkModelProfile.MiniLmL6V2Int8.MaxSequenceLength));
            edge.AddIngestion(1, o => o.Model = ChunkModelProfile.MiniLmL6V2Int8);
        });

        _pipeline = _host.Services.GetRequiredService<IIngestionPipeline>();
        _tokenizer = _host.Services.GetRequiredService<IChunkTokenizer>();
        _budget = new ChunkOptions().Resolve(ChunkModelProfile.MiniLmL6V2Int8, _tokenizer);
        _context = new ExtractionContext(SourceId, "chunks", 32L * 1024 * 1024, new ExtractionOptions(), NullLogger.Instance);

        // In-memory items for the extractor benchmarks; files on disk for the pipeline.
        var folder = Path.Combine(_host.Paths.Root, "corpus");
        Directory.CreateDirectory(folder);
        _memoryItems = new DocumentSourceItem[Corpus.DocumentCount];
        _fileItems = new DocumentSourceItem[Corpus.DocumentCount];
        for (var i = 0; i < Corpus.DocumentCount; i++)
        {
            var bytes = Corpus.Documents[i];
            var name = Corpus.FileName(i);
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, bytes);

            _memoryItems[i] = new DocumentSourceItem(
                name, IngestionMediaTypes.Markdown, _ => new ValueTask<Stream>(new MemoryStream(bytes, writable: false)))
            {
                SizeBytes = bytes.Length,
            };
            _fileItems[i] = new DocumentSourceItem(name, IngestionMediaTypes.Markdown, OpenCounted(path), Path: path)
            {
                SizeBytes = bytes.Length,
                LastModifiedUtc = File.GetLastWriteTimeUtc(path),
            };
        }

        _bytes = [.. Corpus.Documents];
        _extracted = [.. _memoryItems.Select(item => _markdown.ExtractAsync(item, _context, default).AsTask().GetAwaiter().GetResult())];

        // The first run indexes; the second is the one the benchmark repeats. Both are checked, and
        // the second's open count is the README's "one read per file" claim, measured.
        var first = RunPipeline().GetAwaiter().GetResult();
        if (first.DocumentsIndexed != Corpus.DocumentCount)
        {
            throw new InvalidOperationException(
                $"the first run indexed {first.DocumentsIndexed} of {Corpus.DocumentCount} documents ({first.Outcome}).");
        }

        _opens = 0;
        var second = RunPipeline().GetAwaiter().GetResult();
        if (second.DocumentsSkipped != Corpus.DocumentCount || second.EmbedCalls != 0)
        {
            throw new InvalidOperationException(
                $"the re-run skipped {second.DocumentsSkipped} of {Corpus.DocumentCount} and made {second.EmbedCalls} embed calls.");
        }

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "// ingestion: first run {0} docs, {1} chunks, {2} embed calls; re-run {3} skipped, {4} source opens for {5} files",
            first.DocumentsIndexed,
            first.ChunksAdded,
            first.EmbedCalls,
            second.DocumentsSkipped,
            _opens,
            Corpus.DocumentCount));
    }

    [GlobalCleanup]
    public void Cleanup() => _host?.Dispose();

    [Benchmark]
    [Throughput("docs", nameof(Documents))]
    public Task<int> ExtractPlainText() => Extract(_text);

    [Benchmark]
    [Throughput("docs", nameof(Documents))]
    public Task<int> ExtractMarkdown() => Extract(_markdown);

    [Benchmark]
    [Throughput("docs", nameof(Documents))]
    public int ChunkPlain() => Chunk(_plain);

    [Benchmark]
    [Throughput("docs", nameof(Documents))]
    public int ChunkMarkdownHeading() => Chunk(_heading);

    [Benchmark]
    [Throughput("docs", nameof(Documents))]
    public int ChunkTokenWindow() => Chunk(_window);

    [Benchmark]
    [Throughput("docs", nameof(Documents))]
    public UInt128 ContentHashCorpus()
    {
        UInt128 acc = 0;
        foreach (var document in _bytes)
        {
            acc ^= ContentHash.OfBytes(document).Value;
        }

        return acc;
    }

    /// <summary>The whole pipeline over the unchanged 200 files: hash each, find it current, skip it.</summary>
    [Benchmark]
    [Throughput("docs", nameof(Documents))]
    public async Task<int> IncrementalRerunUnchanged()
    {
        var result = await RunPipeline().ConfigureAwait(false);
        return result.DocumentsSkipped;
    }

    private Task<IngestionRunResult> RunPipeline() =>
        _pipeline!.RunAsync(IngestionSource.Items(_fileItems, SourceId));

    private async Task<int> Extract(IDocumentExtractor extractor)
    {
        var characters = 0;
        foreach (var item in _memoryItems)
        {
            var document = await extractor.ExtractAsync(item, _context!, default).ConfigureAwait(false);
            characters += document.Text.Length;
        }

        return characters;
    }

    private int Chunk(IChunker chunker)
    {
        var chunks = 0;
        foreach (var document in _extracted)
        {
            foreach (var _ in chunker.Chunk(document, _budget!, _tokenizer!))
            {
                chunks++;
            }
        }

        return chunks;
    }

    private Func<CancellationToken, ValueTask<Stream>> OpenCounted(string path) => _ =>
    {
        Interlocked.Increment(ref _opens);
        return new ValueTask<Stream>(new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan));
    };
}
