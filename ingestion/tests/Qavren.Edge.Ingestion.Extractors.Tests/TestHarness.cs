using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Qavren.Edge.Ingestion.Extractors.Tests;

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

    public string MessageOf(int eventId) => Records.Find(r => r.EventId == eventId).Message ?? string.Empty;
}

/// <summary>
/// A seekable stream over a byte array that counts what is read through it. It is what proves
/// <c>PdfDocument.Open(Stream)</c> does not slurp the file: the count stays far below the length.
/// </summary>
internal sealed class CountingSeekableStream : Stream
{
    private readonly MemoryStream _inner;

    public CountingSeekableStream(byte[] content) => _inner = new MemoryStream(content, writable: false);

    public long BytesRead { get; private set; }

    public int LargestSingleRead { get; private set; }

    public int SeekCount { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set
        {
            SeekCount++;
            _inner.Position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        BytesRead += read;
        LargestSingleRead = Math.Max(LargestSingleRead, read);
        return read;
    }

    public override int ReadByte()
    {
        var value = _inner.ReadByte();
        if (value >= 0)
        {
            BytesRead++;
            LargestSingleRead = Math.Max(LargestSingleRead, 1);
        }

        return value;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        SeekCount++;
        return _inner.Seek(offset, origin);
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Shared construction for the extractor suites.</summary>
internal static class TestHarness
{
    public const string SourceId = "test-source";

    public static ExtractionContext Context(CapturingLogger logger, ExtractionOptions? options = null) =>
        new(SourceId, "chunks", 32L * 1024 * 1024, options ?? new ExtractionOptions(), logger);

    /// <summary>A verbatim slice of a document's text for one block. The offset contract, in one call.</summary>
    public static string Slice(ExtractedDocument document, DocumentBlock block) =>
        document.Text[block.Start..block.End];

    /// <summary>Every block's slice, in order.</summary>
    public static string[] Slices(ExtractedDocument document) =>
        [.. document.Blocks.Select(b => Slice(document, b))];
}

/// <summary>
/// Locates a source folder under the repository for the source-grep tests. The repo is found from
/// THIS file's compile-time path rather than from <c>AppContext.BaseDirectory</c>, because the
/// parallel phases redirect <c>bin/</c> under <c>-p:ArtifactsPath</c>, outside the repo entirely.
/// On a device lane there is no source tree and the caller skips.
/// </summary>
internal static class RepoSources
{
    public static string? Locate(string relativeFolder, [CallerFilePath] string callerFilePath = "")
    {
        var directory = Path.GetDirectoryName(callerFilePath);
        while (!string.IsNullOrEmpty(directory))
        {
            var candidate = Path.Combine(directory, "ingestion", "src", relativeFolder);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    /// <summary>Every <c>.cs</c> under the folder, <c>bin/</c> and <c>obj/</c> excluded.</summary>
    public static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(file =>
            !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>
    /// The lines of a file with comment lines dropped: the bans are on CALLS, not on mentions, and
    /// the XML docs state the rules they would otherwise indict.
    /// </summary>
    public static IEnumerable<string> CodeLines(string file)
    {
        var inBlockComment = false;
        foreach (var line in File.ReadLines(file))
        {
            var trimmed = line.TrimStart();

            if (inBlockComment)
            {
                inBlockComment = !trimmed.Contains("*/", StringComparison.Ordinal);
                continue;
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                inBlockComment = !trimmed.Contains("*/", StringComparison.Ordinal);
                continue;
            }

            yield return line;
        }
    }
}
