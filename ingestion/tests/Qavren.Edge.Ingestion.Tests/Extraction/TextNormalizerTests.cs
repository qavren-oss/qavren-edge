using System.Text;
using Qavren.Edge.Ingestion.Internal;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Extraction;

/// <summary>
/// Task 3.1 Step 1. CRLF and lone CR to LF, BOM stripped, NFC applied, and — the point of the
/// whole file — offsets recorded afterwards land where the normalised buffer says they do.
/// </summary>
public class TextNormalizerTests
{
    private const char Bom = '\uFEFF';

    /// <summary>
    /// True only when real NFC is available. <c>InvariantGlobalization=true</c> is set repo-wide and
    /// turns <see cref="string.Normalize(NormalizationForm)"/> into a no-op, so the composition
    /// assertion below is conditioned on the measurement rather than on an assumption.
    /// </summary>
    private static bool ComposesNfc => "e\u0301".Normalize(NormalizationForm.FormC).Length == 1;

    [Fact]
    public void CrlfAndLoneCrBothBecomeLf()
    {
        var raw = new UTF8Encoding(false).GetString(FixtureCorpus.Bytes("text/crlf-and-lone-cr.txt"));
        Assert.True(raw.Contains('\r'), "the fixture must still carry its CR bytes");

        var normalised = TextNormalizer.Normalize(raw);

        Assert.False(normalised.Contains('\r'));
        Assert.Equal(
            "line one\nline two\nline three\n\nsecond block after a CRLF blank line\n",
            normalised);
    }

    [Fact]
    public void ALeadingByteOrderMarkIsStripped()
    {
        var raw = FixtureCorpus.Utf8("text/bom.txt");
        Assert.Equal(Bom, raw[0]);

        var normalised = TextNormalizer.Normalize(raw);

        Assert.StartsWith("A document that opens with a UTF-8 byte order mark.", normalised, StringComparison.Ordinal);
        Assert.False(normalised.Contains(Bom));
    }

    [Fact]
    public void NormalisationIsIdempotent()
    {
        var raw = FixtureCorpus.Utf8("text/unicode.txt");

        var once = TextNormalizer.Normalize(raw);
        var twice = TextNormalizer.Normalize(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void NfcComposesWhenTheRuntimeSupportsIt()
    {
        const string Decomposed = "cafe\u0301";

        var normalised = TextNormalizer.Normalize(Decomposed);

        if (ComposesNfc)
        {
            Assert.Equal("caf\u00e9", normalised);
        }
        else
        {
            // InvariantGlobalization: Normalize is a no-op. The buffer is still returned verbatim,
            // which is all the offset contract needs.
            Assert.Equal(Decomposed, normalised);
        }
    }

    [Fact]
    public void NonBreakingAndFigureSpacesFoldToASpaceWithoutMovingOffsets()
    {
        // Issue #30: the PScript5/Distiller shape, U+00A0 as every word separator.
        const string Raw = "AWARD OF CONTRACTS (P102000)";

        var normalised = TextNormalizer.Normalize(Raw);

        Assert.Equal("AWARD OF CONTRACTS (P102000)", normalised);
        Assert.Equal(Raw.Length, normalised.Length);
    }

    [Fact]
    public void EmptyAndCleanTextAreTheIdentity()
    {
        Assert.Equal(string.Empty, TextNormalizer.Normalize(string.Empty));
        Assert.Equal("already clean\n", TextNormalizer.Normalize("already clean\n"));
    }

    [Fact]
    public void OffsetsRecordedAfterNormalisationIndexTheNormalisedBuffer()
    {
        const string Raw = "alpha\r\nbeta\rgamma\r\n\r\ndelta";

        var normalised = TextNormalizer.Normalize(Raw);

        // Every CRLF collapsed, so the last paragraph starts three characters earlier than it did.
        Assert.Equal("alpha\nbeta\ngamma\n\ndelta", normalised);

        var start = normalised.IndexOf("delta", StringComparison.Ordinal);
        Assert.Equal(18, start);
        Assert.Equal("delta", normalised[start..(start + 5)]);
    }
}
