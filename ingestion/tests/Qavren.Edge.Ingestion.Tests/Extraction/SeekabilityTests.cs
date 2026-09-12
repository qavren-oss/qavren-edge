using System.Text;
using Qavren.Edge.Ingestion.Internal;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Extraction;

/// <summary>
/// Spec 7.1's seekability contract, enforced rather than assumed: a small non-seekable stream is
/// buffered once, an oversized one raises 6053 WITHOUT being read to the end, and
/// <c>OpenAsync</c> is re-openable because the pipeline calls it twice per document (spec 9.4).
/// </summary>
public class SeekabilityTests
{
    [Fact]
    public async Task ASmallNonSeekableStreamIsBufferedOnceAndIngests()
    {
        var bytes = Encoding.UTF8.GetBytes("first paragraph\n\nsecond paragraph\n");
        var source = new CountingNonSeekableStream(bytes);
        var item = Handle("handle://small.txt", source);

        var document = await ExtractAsync(item);

        Assert.Equal("first paragraph\n\nsecond paragraph\n", document.Text);
        Assert.Equal(2, document.Blocks.Count);
        Assert.Equal(bytes.Length, source.BytesRead);
    }

    [Fact]
    public async Task AnOversizedNonSeekableStreamRaisesSixZeroFiveThreeWithoutBeingDrained()
    {
        const int Limit = 4096;
        var bytes = new byte[2 * 1024 * 1024];
        Array.Fill(bytes, (byte)'a');

        var source = new CountingNonSeekableStream(bytes);
        var item = Handle("handle://huge.txt", source);
        var options = new ExtractionOptions { NonSeekableBufferLimitBytes = Limit };

        var thrown = await ThrowsOnOpenAsync(item, options);

        Assert.Equal(EdgeErrorCode.IngestionDocumentUnreadable, thrown.Code);
        Assert.NotNull(thrown.Remediation);

        // The refusal is the point: at most one buffer's overshoot past the limit, never the file.
        Assert.True(
            source.BytesRead <= Limit + SourceStream.BufferSize,
            $"read {source.BytesRead} bytes for a {Limit}-byte limit");
        Assert.True(source.BytesRead < bytes.Length);
    }

    [Fact]
    public async Task ADeclaredSizeOverTheLimitIsRefusedBeforeASingleByteIsRead()
    {
        var bytes = new byte[64 * 1024];
        var source = new CountingNonSeekableStream(bytes);
        var item = Handle("handle://declared.txt", source) with { SizeBytes = bytes.Length };
        var options = new ExtractionOptions { NonSeekableBufferLimitBytes = 1024 };

        var thrown = await ThrowsOnOpenAsync(item, options);

        Assert.Equal(EdgeErrorCode.IngestionDocumentUnreadable, thrown.Code);
        Assert.Equal(0, source.BytesRead);
        Assert.Equal(bytes.Length, thrown.SizeBytes);
    }

    [Fact]
    public async Task ASeekableStreamIsHandedBackAtPositionZeroAndNotCopied()
    {
        var bytes = Encoding.UTF8.GetBytes("body\n");
        var backing = new MemoryStream(bytes, writable: false);
        backing.Seek(3, SeekOrigin.Begin);

        var item = Handle("seekable.txt", backing);

        var opened = await OpenAsync(item, new ExtractionOptions());

        Assert.Same(backing, opened);
        Assert.Equal(0, opened.Position);
    }

    [Fact]
    public async Task AFailingOpenBecomesSixZeroFiveThreeRatherThanAnIoException()
    {
        var item = new DocumentSourceItem(
            "gone.txt",
            IngestionMediaTypes.PlainText,
            _ => throw new FileNotFoundException("deleted between enumeration and open"));

        var thrown = await ThrowsOnOpenAsync(item, new ExtractionOptions());

        Assert.Equal(EdgeErrorCode.IngestionDocumentUnreadable, thrown.Code);
        Assert.IsType<FileNotFoundException>(thrown.InnerException);
        Assert.Equal("gone.txt", thrown.DocumentId);
    }

    /// <summary>
    /// Extraction opens the item exactly once; the pipeline's second call (the hash pass, spec 9.4)
    /// is Task 5.1's. What wave 3 can assert is that the delegate is genuinely RE-OPENABLE, which is
    /// the half of the contract an extractor depends on.
    /// </summary>
    [Fact]
    public async Task OpenAsyncIsCalledOncePerExtractionAndIsReOpenable()
    {
        var bytes = Encoding.UTF8.GetBytes("one\n\ntwo\n");
        var opens = 0;

        var item = new DocumentSourceItem(
            "spy.txt",
            IngestionMediaTypes.PlainText,
            _ =>
            {
                opens++;
                return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
            });

        var first = await ExtractAsync(item);
        Assert.Equal(1, opens);

        var second = await ExtractAsync(item);
        Assert.Equal(2, opens);
        Assert.Equal(first.Text, second.Text);
    }

    private static DocumentSourceItem Handle(string documentId, Stream stream) =>
        new(documentId, IngestionMediaTypes.PlainText, _ => ValueTask.FromResult(stream));

    private static async Task<ExtractedDocument> ExtractAsync(DocumentSourceItem item) =>
        await new PlainTextExtractor()
            .ExtractAsync(item, TestHarness.Context(new CapturingLogger()), TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

    private static async Task<Stream> OpenAsync(DocumentSourceItem item, ExtractionOptions options) =>
        await SourceStream
            .OpenSeekableAsync(item, options, TestHarness.SourceId, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

    private static async Task<EdgeIngestionException> ThrowsOnOpenAsync(
        DocumentSourceItem item, ExtractionOptions options) =>
        await Assert.ThrowsAsync<EdgeIngestionException>(() => OpenAsync(item, options)).ConfigureAwait(false);
}
