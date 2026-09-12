using Microsoft.Extensions.Logging;

namespace Qavren.Edge.Ingestion.Tests.Extraction;

/// <summary>An <see cref="ILogger"/> that keeps every record, so an event id can be asserted.</summary>
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

    public bool Saw(int eventId) => Records.Exists(r => r.EventId == eventId);
}

/// <summary>A stream that is not seekable and counts every byte it hands out.</summary>
internal sealed class CountingNonSeekableStream : Stream
{
    private readonly byte[] _content;
    private int _position;

    public CountingNonSeekableStream(byte[] content) => _content = content;

    public long BytesRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var take = Math.Min(buffer.Length, _content.Length - _position);
        if (take <= 0)
        {
            return 0;
        }

        _content.AsSpan(_position, take).CopyTo(buffer);
        _position += take;
        BytesRead += take;
        return take;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Shared construction for the extraction suites.</summary>
internal static class TestHarness
{
    public const string SourceId = "test-source";

    public static ExtractionContext Context(CapturingLogger logger, ExtractionOptions? options = null) =>
        new(SourceId, "chunks", 32L * 1024 * 1024, options ?? new ExtractionOptions(), logger);

    /// <summary>A verbatim slice of a document's text for one block. The offset contract, in one call.</summary>
    public static string Slice(ExtractedDocument document, DocumentBlock block) =>
        document.Text[block.Start..block.End];
}

/// <summary>An extractor that claims whatever it is told to, for registry ordering tests.</summary>
internal sealed class StubExtractor : IDocumentExtractor
{
    public StubExtractor(string id, int version, IReadOnlyList<string> extensions, IReadOnlyList<string> mediaTypes)
    {
        Id = id;
        Version = version;
        Extensions = extensions;
        MediaTypes = mediaTypes;
    }

    public string Id { get; }

    public int Version { get; }

    public IReadOnlyList<string> Extensions { get; }

    public IReadOnlyList<string> MediaTypes { get; }

    public bool CanExtract(DocumentSourceItem item) => true;

    public ValueTask<ExtractedDocument> ExtractAsync(
        DocumentSourceItem item, ExtractionContext context, CancellationToken cancellationToken) =>
        throw new NotSupportedException("StubExtractor exists for resolution tests only.");
}
