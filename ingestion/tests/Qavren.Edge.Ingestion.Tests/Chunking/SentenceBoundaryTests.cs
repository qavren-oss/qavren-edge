using Qavren.Edge.Ingestion.Internal;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Chunking;

/// <summary>
/// <see cref="SentenceBoundary.FindBackwards"/> on its own (spec 8.2 step 1). The twenty goldens
/// exercise it only through a chunker, and the two whose table row names the nudge —
/// <c>three-paragraphs</c> at 38 tokens and <c>long-token</c> at one [UNK] — never reach a cut at
/// the pinned 222-token budget, so the preference order, the abbreviation stop-list and the
/// look-back bound are pinned here directly.
/// </summary>
public sealed class SentenceBoundaryTests
{
    [Fact]
    public void A_paragraph_break_wins_over_a_sentence_end()
    {
        const string Text = "First para ends. Still first.\n\nSecond para runs on and on.";

        var boundary = SentenceBoundary.FindBackwards(Text, 0, Text.Length, Text.Length);

        Assert.Equal(Text.IndexOf("\n\n", StringComparison.Ordinal), boundary);
        Assert.EndsWith("Still first.", Text[..boundary], StringComparison.Ordinal);
    }

    [Fact]
    public void A_sentence_end_wins_over_plain_whitespace_and_is_the_exclusive_end()
    {
        const string Text = "Alpha beta. Gamma delta";

        var boundary = SentenceBoundary.FindBackwards(Text, 0, Text.Length, Text.Length);

        Assert.Equal(11, boundary);
        Assert.EndsWith(".", Text[..boundary], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("。")]
    [InlineData("！")]
    [InlineData("？")]
    [InlineData("…")]
    [InlineData("!")]
    [InlineData("?")]
    public void Every_terminator_in_the_list_ends_a_sentence(string terminator)
    {
        var text = "Alpha beta" + terminator + " Gamma delta";

        var boundary = SentenceBoundary.FindBackwards(text, 0, text.Length, text.Length);

        Assert.Equal(11, boundary);
    }

    [Fact]
    public void A_terminator_with_no_whitespace_after_it_is_not_a_sentence_end()
    {
        // The dot inside a version number is the classic false positive.
        const string Text = "Release version 1.5 ships";

        var boundary = SentenceBoundary.FindBackwards(Text, 0, Text.Length, Text.Length);

        Assert.Equal(Text.LastIndexOf(' '), boundary);
    }

    [Theory]
    [InlineData("Ships in October, see e.g. the notes")]
    [InlineData("Ships in October, i.e. the notes")]
    [InlineData("Ships in October, etc. the notes")]
    [InlineData("Compare alpha vs. the notes")]
    [InlineData("Ask Dr. Alpha about the notes")]
    [InlineData("Read J. R. R. about the notes")]
    public void An_abbreviation_dot_is_not_a_sentence_end(string text)
    {
        var boundary = SentenceBoundary.FindBackwards(text, 0, text.Length, text.Length);

        // The whitespace fallback, not the abbreviation's dot.
        Assert.Equal(text.LastIndexOf(' '), boundary);
    }

    [Fact]
    public void Whitespace_is_the_last_resort_and_returns_the_run_start()
    {
        const string Text = "alpha   beta";

        var boundary = SentenceBoundary.FindBackwards(Text, 0, Text.Length, Text.Length);

        Assert.Equal(5, boundary);
        Assert.Equal("alpha", Text[..boundary]);
    }

    [Fact]
    public void A_window_holding_no_boundary_returns_minus_one()
    {
        const string Text = "alpha-beta-gamma-delta";

        Assert.Equal(-1, SentenceBoundary.FindBackwards(Text, 0, Text.Length, Text.Length));
    }

    [Fact]
    public void The_search_never_reaches_further_back_than_the_look_back_window()
    {
        // The terminator needs the whitespace after it; the 50 x's are the run that puts it out of
        // reach of a narrow look-back.
        var text = "Alpha beta. " + new string('x', 50);

        Assert.Equal(-1, SentenceBoundary.FindBackwards(text, 0, text.Length, lookBack: 5));
        Assert.Equal(11, SentenceBoundary.FindBackwards(text, 0, text.Length, lookBack: text.Length));
    }

    [Fact]
    public void A_boundary_at_or_before_the_range_start_is_not_a_boundary()
    {
        const string Text = " alpha beta";

        // The only whitespace is at rangeStart, so trimming to it would advance nothing.
        Assert.Equal(-1, SentenceBoundary.FindBackwards(Text, 0, 6, 6));
        Assert.Equal(-1, SentenceBoundary.FindBackwards(Text, 3, 3, 8));
    }
}
