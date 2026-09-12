using Xunit;

namespace Qavren.Edge.Rag.Tests;

/// <summary>
/// Spec 11 step 4 and spec 7's <c>RagPrompts.Format</c>: the assembled block is a golden string,
/// byte for byte, because it is the only thing the model ever sees of the corpus and a stray blank
/// line or a lost <c>Source:</c> is invisible in every other kind of assertion.
/// </summary>
public class RagPromptsFormatTests
{
    /// <summary>
    /// 39 characters clamps source 2 exactly at a word boundary and leaves sources 1 and 3 whole,
    /// so the golden block carries a truncated chunk and two untruncated ones.
    /// </summary>
    private const int ClampChars = 39;

    private static IReadOnlyList<RagSource> ThreeSources() =>
    [
        new RagSource("c1", "Coverage lasts 24 months from purchase.")
        {
            Title = "Warranty terms",
            Uri = new Uri("https://example.com/warranty"),
            Score = 0.10d,
            ScoreKind = RetrievalScoreKind.Distance,
        },
        new RagSource("c2", "Returns are accepted within thirty days of delivery.")
        {
            Title = "Returns",
            Score = 0.20d,
            ScoreKind = RetrievalScoreKind.Distance,
        },
        new RagSource("c3", "Ships in two days.")
        {
            Uri = new Uri("https://example.com/shipping"),
            Score = 0.30d,
            ScoreKind = RetrievalScoreKind.Distance,
        },
    ];

    [Fact]
    public void FormatRendersTheBlockByteForByteIncludingBothMissingMetadataVariants()
    {
        var options = new RagOptions { MaxCharsPerSource = ClampChars };

        var block = RagPrompts.Format(ThreeSources(), options);

        const string Expected =
            "## Additional context\n" +
            "Answer using ONLY the numbered sources below. If they do not contain the answer, say you could not find it. Be concise.\n" +
            "\n" +
            "[1] Title: Warranty terms\n" +
            "    Source: https://example.com/warranty\n" +
            "    ---\n" +
            "    Coverage lasts 24 months from purchase.\n" +
            "\n" +
            "[2] Title: Returns\n" +
            "    ---\n" +
            "    Returns are accepted within thirty days…(truncated)\n" +
            "\n" +
            "[3]\n" +
            "    Source: https://example.com/shipping\n" +
            "    ---\n" +
            "    Ships in two days.\n" +
            "\n" +
            "Cite every claim with the bracketed number of its source, like [1]. Do not invent source numbers.";

        Assert.Equal(Expected, block);
    }

    [Fact]
    public void TheTruncationSuffixIsVisibleInTheBlockSoAReaderCanSeeTheModelWasShownLess()
    {
        var options = new RagOptions { MaxCharsPerSource = ClampChars };

        var block = RagPrompts.Format(ThreeSources(), options);

        Assert.Contains("thirty days…(truncated)", block, StringComparison.Ordinal);
        Assert.DoesNotContain("of delivery", block, StringComparison.Ordinal);
    }

    [Fact]
    public void ABudgetThatFitsTwoOfThreeDropsTheThirdFromTheTailAndTheSurvivorsAreNumberedOneAndTwo()
    {
        var sources = ThreeSources();
        var twoOnly = new RagOptions { MaxCharsPerSource = ClampChars };
        var expected = RagPrompts.Format([sources[0], sources[1]], twoOnly);

        var options = new RagOptions
        {
            MaxCharsPerSource = ClampChars,
            TokenCounter = static block => block.Length,
            MaxContextTokens = expected.Length,
        };

        var actual = RagPrompts.Format(sources, options);

        Assert.Equal(expected, actual);
        Assert.Contains("[1] Title: Warranty terms", actual, StringComparison.Ordinal);
        Assert.Contains("[2] Title: Returns", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("[3]", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("Ships in two days.", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void ABudgetTooSmallForOneClampedSourceThrowsRagContextBudgetTooSmall()
    {
        var options = new RagOptions { MaxCharsPerSource = ClampChars, MaxContextTokens = 1 };

        var exception = Assert.Throws<EdgeRagException>(() => RagPrompts.Format(ThreeSources(), options));

        Assert.Equal(EdgeErrorCode.RagContextBudgetTooSmall, exception.Code);
        Assert.EndsWith("7204", exception.HelpLink, StringComparison.Ordinal);
        Assert.NotNull(exception.Remediation);
    }

    [Fact]
    public void AnEmptySourceListRendersTheEmptyStringRatherThanAHeadingWithNothingUnderIt()
    {
        Assert.Equal(string.Empty, RagPrompts.Format([], new RagOptions()));
    }

    [Fact]
    public void OrdinalIsAssignedByFormatAndNotByTheRetrieverOrTheProjector()
    {
        var sources = ThreeSources();
        Assert.All(sources, source => Assert.Equal(0, source.Ordinal));

        var block = RagPrompts.Format(sources, new RagOptions { MaxCharsPerSource = ClampChars });

        Assert.Contains("[1]", block, StringComparison.Ordinal);
        Assert.All(sources, source => Assert.Equal(0, source.Ordinal));
    }

    [Fact]
    public void FormattingIsIdempotentSoAPreparedListDoesNotGainASecondTruncationSuffix()
    {
        var options = new RagOptions { MaxCharsPerSource = ClampChars };

        var once = RagPrompts.Format(ThreeSources(), options);
        var twice = RagPrompts.Format(
            [.. ThreeSources().Select((s, i) => s with { Text = ClampLikeFormat(s.Text), Ordinal = i + 1 })],
            options);

        Assert.Equal(once, twice);
    }

    private static string ClampLikeFormat(string text) =>
        text.Length <= ClampChars ? text : text[..ClampChars] + RagPrompts.TruncationSuffix;
}
