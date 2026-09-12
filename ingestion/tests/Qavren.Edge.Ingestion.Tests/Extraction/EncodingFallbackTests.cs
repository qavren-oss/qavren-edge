using System.Text;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Extraction;

/// <summary>
/// Spec 7.2's three encoding layers: BOM, then a strict UTF-8 probe, then byte-preserving Latin-1
/// logged as event 914 - or 6106 when <c>StrictUtf8</c> is set.
/// </summary>
public class EncodingFallbackTests
{
    private const char Bom = '\uFEFF';

    private static readonly byte[] InvalidUtf8 = [(byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n'];

    public static TheoryData<string> BomCases() =>
        ["utf-8", "utf-16le", "utf-16be", "utf-32le", "utf-32be"];

    [Theory]
    [MemberData(nameof(BomCases))]
    public async Task EveryByteOrderMarkIsDetectedAndStripped(string label)
    {
        const string Content = "Alpha beta.\n\nGamma delta.\n";
        var encoding = EncodingFor(label);

        var bytes = new List<byte>(encoding.GetPreamble());
        bytes.AddRange(encoding.GetBytes(Content));

        var (document, logger) = await ExtractAsync([.. bytes], new PlainTextExtractorOptions());

        Assert.Equal(Content, document.Text);
        Assert.False(document.Text.StartsWith(Bom));
        Assert.False(logger.Saw(EdgeIngestionEventIds.EncodingFallback));
        Assert.Equal(2, document.Blocks.Count);
    }

    [Fact]
    public async Task ValidUtf8WithoutABomDecodesAsUtf8()
    {
        var bytes = FixtureCorpus.Bytes("text/unicode.txt");

        var (document, logger) = await ExtractAsync(bytes, new PlainTextExtractorOptions());

        Assert.Contains("CJK:", document.Text, StringComparison.Ordinal);
        Assert.False(logger.Saw(EdgeIngestionEventIds.EncodingFallback));
        Assert.Null(document.Warnings);
    }

    [Fact]
    public async Task InvalidUtf8FallsBackToLatin1AndLogsEvent914()
    {
        // 0xE9 alone is a valid Latin-1 'e with acute' and an invalid UTF-8 sequence.
        var (document, logger) = await ExtractAsync(InvalidUtf8, new PlainTextExtractorOptions());

        Assert.Equal("caf\u00e9\n", document.Text);
        Assert.True(logger.Saw(EdgeIngestionEventIds.EncodingFallback));
    }

    /// <summary>
    /// Spec 13.3: 6106 is "unreachable on defaults (Latin-1 fallback, event 914)". The fallback
    /// is therefore a LOG event and not a recorded failure - a consumer filtering
    /// ExtractedDocument.Warnings on DocumentEncodingUndecodable must never see it on the happy
    /// path, only under StrictUtf8 where it is thrown.
    /// </summary>
    [Fact]
    public async Task TheDefaultFallbackRecordsNoSixOneZeroSixWarning()
    {
        var (document, logger) = await ExtractAsync(InvalidUtf8, new PlainTextExtractorOptions());

        Assert.True(logger.Saw(EdgeIngestionEventIds.EncodingFallback));
        Assert.Null(document.Warnings);
    }

    [Fact]
    public async Task StrictUtf8RaisesSixOneZeroSixInsteadOfGuessing()
    {
        var logger = new CapturingLogger();

        var thrown = await ThrowsAsync(InvalidUtf8, new PlainTextExtractorOptions { StrictUtf8 = true }, logger);

        Assert.Equal(EdgeErrorCode.DocumentEncodingUndecodable, thrown.Code);
        Assert.NotNull(thrown.Remediation);
        Assert.False(logger.Saw(EdgeIngestionEventIds.EncodingFallback));
    }

    private static Encoding EncodingFor(string label) => label switch
    {
        "utf-8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        "utf-16le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
        "utf-16be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
        "utf-32le" => new UTF32Encoding(bigEndian: false, byteOrderMark: true),
        _ => new UTF32Encoding(bigEndian: true, byteOrderMark: true),
    };

    private static DocumentSourceItem ItemOver(byte[] bytes) =>
        new(
            "probe.txt",
            IngestionMediaTypes.PlainText,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)),
            SizeBytes: bytes.Length);

    private static async Task<(ExtractedDocument Document, CapturingLogger Logger)> ExtractAsync(
        byte[] bytes, PlainTextExtractorOptions options)
    {
        var logger = new CapturingLogger();
        var extractor = new PlainTextExtractor(options);
        var document = await extractor
            .ExtractAsync(ItemOver(bytes), TestHarness.Context(logger), TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        return (document, logger);
    }

    private static async Task<EdgeExtractionException> ThrowsAsync(
        byte[] bytes, PlainTextExtractorOptions options, CapturingLogger logger)
    {
        var extractor = new PlainTextExtractor(options);
        var item = ItemOver(bytes);

        return await Assert.ThrowsAsync<EdgeExtractionException>(
            () => extractor
                .ExtractAsync(item, TestHarness.Context(logger), TestContext.Current.CancellationToken)
                .AsTask()).ConfigureAwait(false);
    }
}
