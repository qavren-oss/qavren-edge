using Qavren.Edge.Ingestion.Pdf;
using Xunit;

namespace Qavren.Edge.Ingestion.Extractors.Tests;

/// <summary>
/// Spec 7.4 over the committed PDF corpus plus the four files assembled at test time (plan Task
/// 6.1 Step 5). Every error code the satellite owns — 6103, 6104, 6105 (as an outcome), 6107 — has
/// a raise site here.
/// </summary>
public class PdfExtractorTests
{
    private const string MinimalFirstLine = "Hello from a minimal PDF.";
    private const string MinimalSecondLine = "A second line of the same paragraph.";

    [Fact]
    public async Task MinimalTextComesBackWithOffsetsIntoTheBuffer()
    {
        var (document, _) = await ExtractAsync(FixtureCorpus.Item("pdf/minimal-text.pdf"));

        Assert.Equal("pdf", document.ExtractorId);
        Assert.Equal(1, document.ExtractorVersion);
        Assert.Equal(IngestionMediaTypes.Pdf, document.MediaType);
        Assert.Equal(1, document.PageCount);
        Assert.True(document.HasTextLayer);
        Assert.StartsWith(MinimalFirstLine, document.Text, StringComparison.Ordinal);
        Assert.Contains(MinimalSecondLine, document.Text, StringComparison.Ordinal);

        var block = Assert.Single(document.Blocks);
        Assert.Equal(DocumentBlockKind.Paragraph, block.Kind);
        Assert.Equal(1, block.PageNumber);
        Assert.Equal(document.Text, TestHarness.Slice(document, block));
        Assert.Equal(0, block.Start);
        Assert.Equal(document.Text.Length, block.End);
    }

    [Fact]
    public async Task PageNumbersReachEveryBlock()
    {
        var (document, _) = await ExtractAsync(FixtureCorpus.Item("pdf/two-pages.pdf"));

        Assert.Equal(2, document.PageCount);
        Assert.Equal(2, document.Blocks.Count);
        Assert.Equal([1, 2], document.Blocks.Select(b => b.PageNumber).ToArray());

        foreach (var block in document.Blocks)
        {
            Assert.Equal("Page content shared by both pages of this fixture.", TestHarness.Slice(document, block));
        }

        // The two pages are separated in the buffer and neither block straddles the gap.
        Assert.True(document.Blocks[0].End < document.Blocks[1].Start);
    }

    [Theory]
    [InlineData(PdfReadingOrderMode.ContentOrder)]
    [InlineData(PdfReadingOrderMode.Layout)]
    public async Task TwoColumnsReadLeftColumnThenRightUnderBothReadingOrders(PdfReadingOrderMode mode)
    {
        var (document, _) = await ExtractAsync(
            FixtureCorpus.Item("pdf/two-columns.pdf"), new PdfExtractorOptions { ReadingOrder = mode });

        var text = document.Text;
        var leftOne = text.IndexOf("Left column line one.", StringComparison.Ordinal);
        var leftTwo = text.IndexOf("Left column line two.", StringComparison.Ordinal);
        var rightOne = text.IndexOf("Right column line one.", StringComparison.Ordinal);
        var rightTwo = text.IndexOf("Right column line two.", StringComparison.Ordinal);

        Assert.True(leftOne >= 0 && leftTwo >= 0 && rightOne >= 0 && rightTwo >= 0, text);
        Assert.True(leftOne < leftTwo, "left column lines are in order");
        Assert.True(rightOne < rightTwo, "right column lines are in order");
        Assert.True(leftTwo < rightOne, $"the whole left column precedes the right column under {mode}: {text}");
        Assert.All(document.Blocks, b => Assert.Equal(1, b.PageNumber));
    }

    [Fact]
    public async Task AHyphenatedLineBreakIsRejoinedByDefault()
    {
        var (document, _) = await ExtractAsync(FixtureCorpus.Item("pdf/hyphen-linebreak.pdf"));

        Assert.Contains("extraordinary hyphenated line break.", document.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("extra-", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHyphenatedLineBreakIsKeptWithTheFlagOff()
    {
        var (document, _) = await ExtractAsync(
            FixtureCorpus.Item("pdf/hyphen-linebreak.pdf"), new PdfExtractorOptions { JoinHyphenatedLineBreaks = false });

        Assert.Contains("extra-\nordinary", document.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("extraordinary", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AScannedPageIsAnOutcomeNotAnException()
    {
        var (document, _) = await ExtractAsync(FixtureCorpus.Item("pdf/no-text-layer.pdf"));

        // HasTextLayer = false is what the runner turns into status NoTextLayer (6105) with the
        // content hash STORED, so the file is not re-parsed every run. That recording is the
        // runner's and is asserted in the pipeline suite; the extractor's contract is this shape.
        Assert.False(document.HasTextLayer);
        Assert.Equal(string.Empty, document.Text);
        Assert.Empty(document.Blocks);
        Assert.Equal(1, document.PageCount);
    }

    [Fact]
    public async Task ACrossReferenceStreamWithAnObjectStreamIsParsed()
    {
        // The page dictionary of this fixture exists ONLY inside the /Type /ObjStm object, so the
        // text below is reachable only if the object stream was decoded.
        var (document, _) = await ExtractAsync(FixtureCorpus.Item("pdf/xref-stream.pdf"));

        Assert.Equal(1, document.PageCount);
        Assert.Contains("A PDF 1.5 file whose cross-reference is a stream.", document.Text, StringComparison.Ordinal);
        Assert.Contains("The page dictionary lives in an object stream.", document.Text, StringComparison.Ordinal);
        Assert.NotEmpty(document.Blocks);
    }

    [Fact]
    public async Task ABrokenStartxrefIsRecoveredByTheLenientParser()
    {
        // Task 1.3 Step 3b observed "NOTE broken-startxref.pdf RECOVERED and yielded 1 page(s)", so
        // this fixture asserts the recovery, and 6104's raise site is TruncatedObject() below.
        var (document, _) = await ExtractAsync(FixtureCorpus.Item("pdf/broken-startxref.pdf"));

        Assert.Equal(1, document.PageCount);
        Assert.StartsWith(MinimalFirstLine, document.Text, StringComparison.Ordinal);
        Assert.Contains(MinimalSecondLine, document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATruncatedObjectIsSixOneZeroFourAndNamesTheFile()
    {
        var thrown = await Assert.ThrowsAsync<EdgeExtractionException>(
            () => ExtractAsync(FixtureCorpus.Item("truncated.pdf", TestPdf.TruncatedObject())));

        Assert.Equal(EdgeErrorCode.DocumentMalformed, thrown.Code);
        Assert.Contains("truncated.pdf", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("pdf", thrown.ExtractorName);
        Assert.Equal("truncated.pdf", thrown.DocumentId);
        Assert.NotNull(thrown.InnerException);
        Assert.NotNull(thrown.Remediation);
    }

    [Fact]
    public async Task AFileWithNoHeaderIsSixOneZeroFour()
    {
        var thrown = await Assert.ThrowsAsync<EdgeExtractionException>(
            () => ExtractAsync(FixtureCorpus.Item("not-a.pdf", TestPdf.NotAPdf())));

        Assert.Equal(EdgeErrorCode.DocumentMalformed, thrown.Code);
        Assert.NotNull(thrown.InnerException);
    }

    [Fact]
    public void FlateContentAssemblesIdenticallyTwice()
    {
        Assert.Equal(TestPdf.FlateContent(), TestPdf.FlateContent());
    }

    [Fact]
    public async Task FlateContentExtractsTheCommittedStream()
    {
        var (document, _) = await ExtractAsync(FixtureCorpus.Item("flate-content.pdf", TestPdf.FlateContent()));

        Assert.Contains("This content stream is stored with FlateDecode.", document.Text, StringComparison.Ordinal);
        Assert.Contains("Two lines, one paragraph.", document.Text, StringComparison.Ordinal);
        var block = Assert.Single(document.Blocks);
        Assert.Equal(document.Text, TestHarness.Slice(document, block));
    }

    [Fact]
    public async Task AnEncryptedDocumentIsSixOneZeroThreeAfterThePasswordsAreTried()
    {
        var bytes = TestPdf.Encrypted("secret");
        var options = new PdfExtractorOptions();
        options.Passwords.Add("wrong");

        var thrown = await Assert.ThrowsAsync<EdgeExtractionException>(
            () => ExtractAsync(FixtureCorpus.Item("locked.pdf", bytes), options));

        Assert.Equal(EdgeErrorCode.DocumentEncrypted, thrown.Code);
        Assert.Contains("1 configured password", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("locked.pdf", thrown.DocumentId);
        Assert.NotNull(thrown.InnerException);
    }

    [Fact]
    public async Task TheRightPasswordOpensAnEncryptedDocument()
    {
        var bytes = TestPdf.Encrypted("secret");
        var options = new PdfExtractorOptions();
        options.Passwords.Add("wrong");
        options.Passwords.Add("secret");

        var (document, _) = await ExtractAsync(FixtureCorpus.Item("locked.pdf", bytes), options);

        Assert.StartsWith(MinimalFirstLine, document.Text, StringComparison.Ordinal);
        Assert.Contains(MinimalSecondLine, document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEncryptedFixtureAssemblesIdenticallyTwice()
    {
        Assert.Equal(TestPdf.Encrypted("secret"), TestPdf.Encrypted("secret"));
    }

    [Fact]
    public async Task AZeroPageBudgetIsSixOneZeroSevenNamingThePageReached()
    {
        var logger = new CapturingLogger();
        var thrown = await Assert.ThrowsAsync<EdgeExtractionException>(
            () => ExtractAsync(
                FixtureCorpus.Item("pdf/two-pages.pdf"), new PdfExtractorOptions { PageBudget = TimeSpan.Zero }, logger));

        Assert.Equal(EdgeErrorCode.DocumentPageBudgetExceeded, thrown.Code);
        Assert.Equal(1, thrown.PageNumber);
        Assert.Contains("after 1 of 2 pages", thrown.Message, StringComparison.Ordinal);
        Assert.True(logger.Saw(EdgeIngestionEventIds.PageTimedOut), "event 924 was logged");
    }

    [Fact]
    public async Task TheBudgetIsCheckedBetweenPagesSoASinglePageDocumentAlwaysCompletes()
    {
        // A one-page document has no boundary after its first page at which a budget could
        // stop it, and nothing in-process can interrupt the page itself (spec 7.4).
        var (document, logger) = await ExtractAsync(
            FixtureCorpus.Item("pdf/minimal-text.pdf"), new PdfExtractorOptions { PageBudget = TimeSpan.Zero });

        Assert.StartsWith(MinimalFirstLine, document.Text, StringComparison.Ordinal);
        Assert.False(logger.Saw(EdgeIngestionEventIds.PageTimedOut));
    }

    [Fact]
    public async Task CancellationIsHonouredAtThePageBoundary()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var extractor = new PdfTextExtractor();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            extractor.ExtractAsync(
                FixtureCorpus.Item("pdf/two-pages.pdf"), TestHarness.Context(new CapturingLogger()), cancelled.Token).AsTask());
    }

    [Fact]
    public async Task ANonSeekableSourceUnderTheLimitIsBufferedOnce()
    {
        var bytes = FixtureCorpus.Bytes("pdf/minimal-text.pdf");
        var item = new DocumentSourceItem(
            "piped.pdf",
            IngestionMediaTypes.Pdf,
            _ => ValueTask.FromResult<Stream>(new NonSeekableStream(bytes)),
            SizeBytes: bytes.Length);

        var (document, _) = await ExtractAsync(item);

        Assert.StartsWith(MinimalFirstLine, document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonSeekableSourceOverTheLimitIsSixZeroFiveThreeNotRead()
    {
        var bytes = TestPdf.Padded();
        var item = new DocumentSourceItem(
            "huge.pdf",
            IngestionMediaTypes.Pdf,
            _ => ValueTask.FromResult<Stream>(new NonSeekableStream(bytes)));

        var thrown = await Assert.ThrowsAsync<EdgeIngestionException>(() => ExtractAsync(
            item, options: null, logger: null, extraction: new ExtractionOptions { NonSeekableBufferLimitBytes = 4096 }));

        Assert.Equal(EdgeErrorCode.IngestionDocumentUnreadable, thrown.Code);
        Assert.NotNull(thrown.SizeBytes);
        Assert.True(thrown.SizeBytes < bytes.Length, "the read stopped at the ceiling rather than draining the stream");
    }

    [Fact]
    public void TheDefaultsAreTheSpecsDefaults()
    {
        var options = new PdfExtractorOptions();

        Assert.Equal(PdfReadingOrderMode.ContentOrder, options.ReadingOrder);
        Assert.True(options.SkipMissingFonts);
        Assert.True(options.UseActualText);
        Assert.True(options.UseLenientParsing);
        Assert.Empty(options.Passwords);
        Assert.True(options.JoinHyphenatedLineBreaks);
        Assert.Equal(TimeSpan.FromSeconds(20), options.PageBudget);
        Assert.Equal(50, options.MaxStackDepth);

        var extractor = new PdfTextExtractor();
        Assert.Equal("pdf", extractor.Id);
        Assert.Equal(1, extractor.Version);
        Assert.Equal([".pdf"], extractor.Extensions);
        Assert.Equal([IngestionMediaTypes.Pdf], extractor.MediaTypes);
    }

    internal static async Task<(ExtractedDocument Document, CapturingLogger Logger)> ExtractAsync(
        DocumentSourceItem item,
        PdfExtractorOptions? options = null,
        CapturingLogger? logger = null,
        ExtractionOptions? extraction = null)
    {
        logger ??= new CapturingLogger();
        var extractor = options is null ? new PdfTextExtractor() : new PdfTextExtractor(options);
        var document = await extractor.ExtractAsync(item, TestHarness.Context(logger, extraction), CancellationToken.None)
            .ConfigureAwait(false);
        return (document, logger);
    }

    private sealed class NonSeekableStream : Stream
    {
        private readonly byte[] _content;
        private int _position;

        public NonSeekableStream(byte[] content) => _content = content;

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
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
