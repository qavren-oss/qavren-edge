using System.Globalization;
using Xunit;

namespace Qavren.Edge.Ingestion.Onnx.Tests;

/// <summary>
/// <see cref="EdgeChunkTokenizer"/> against the real <c>bert-base-uncased</c> vocabulary when it is
/// present: the offset contract on NFD, ZWJ, Turkish-I and mixed-case non-ASCII input, and
/// <b>equality with <c>MlChunkTokenizer</c></b> over the same vocabulary - the two implementations
/// return the SAME index for the same input, which is what plan adjustment 1 buys.
/// </summary>
public sealed class EdgeChunkTokenizerTests
{
    private const string Nfd = "café café café hello world, and then some more words follow here";
    private const string Zwj =
        "family \U0001F468‍\U0001F469‍\U0001F467 went home; the \U0001F468‍\U0001F469‍\U0001F467 stayed";
    private const string TurkishI = "İstanbul'da ışık ve DİYARBAKIR arasında ILIK bir akşam vardı, ıslak sokaklar";
    private const string MixedNonAscii = "Ärger ÜBER die Straße: naïve Café-Besucher und ØRESUND-Brücke, ÉCOLE normale";
    private const string Ascii = "the quick brown fox jumps over the lazy dog hello world and keeps on running far";

    public static TheoryData<string> Inputs => new() { Nfd, Zwj, TurkishI, MixedNonAscii, Ascii };

    [Fact]
    public void SpecialTokenOverhead_is_the_measured_two_and_the_ceiling_forwards()
    {
        var tokenizer = new EdgeChunkTokenizer(new FakeEdgeTokenizer(maxSequenceLength: 512));

        Assert.Equal(2, tokenizer.SpecialTokenOverhead);
        Assert.Equal(512, tokenizer.MaxSequenceLength);
        Assert.Equal("wordpiece:512:uncased", tokenizer.Id);
        Assert.Equal("wordpiece:512:cased", new EdgeChunkTokenizer(new FakeEdgeTokenizer(512), lowerCase: false).Id);
    }

    [Fact]
    public void The_id_is_spelled_exactly_as_the_ONNX_free_path_spells_it()
    {
        // Same vocabulary, same settings, same recipe hash: a consumer who swaps AddOnnxIngestion
        // for UseChunkTokenizer must not re-index. The core's id comes from a probe tokenizer over
        // an in-memory vocab; only the spelling is under test here.
        using var vocab = new MemoryStream("[PAD]\n[UNK]\n[CLS]\n[SEP]\n[MASK]\nhello\nworld\n"u8.ToArray());
        var core = EdgeTokenCounter.CreateWordPiece(vocab, 256, lowerCase: true);

        Assert.Equal(core.Id, new EdgeChunkTokenizer(new FakeEdgeTokenizer(256)).Id, StringComparer.Ordinal);
    }

    [Fact]
    public void CountTokens_forwards_and_IndexByTokenCount_never_forwards()
    {
        var edge = new FakeEdgeTokenizer();
        var tokenizer = new EdgeChunkTokenizer(edge);

        Assert.Equal(3, tokenizer.CountTokens("one two three"));
        Assert.Equal(1, edge.CountCalls);

        var index = tokenizer.IndexByTokenCount("one two three four five", 2, out var count);

        // The LARGEST prefix costing at most two whitespace tokens includes the trailing space.
        Assert.NotEqual(FakeEdgeTokenizer.WrongIndex, index);
        Assert.Equal("one two ".Length, index);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Empty_text_or_a_zero_budget_is_index_zero()
    {
        var tokenizer = new EdgeChunkTokenizer(new FakeEdgeTokenizer());

        Assert.Equal(0, tokenizer.IndexByTokenCount(string.Empty, 5, out var a));
        Assert.Equal(0, a);
        Assert.Equal(0, tokenizer.IndexByTokenCount("one two", 0, out var b));
        Assert.Equal(0, b);
        Assert.Throws<ArgumentNullException>(() => tokenizer.IndexByTokenCount(null!, 1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => tokenizer.IndexByTokenCount("x", -1, out _));
    }

    [Theory(Skip = Vocabulary.SkipReason, SkipUnless = nameof(Vocabulary.Available), SkipType = typeof(Vocabulary))]
    [MemberData(nameof(Inputs))]
    public void The_offset_contract_holds_on_the_real_vocabulary(string text)
    {
        var tokenizer = new EdgeChunkTokenizer(Vocabulary.Edge);
        var total = tokenizer.CountTokens(text);
        Assert.True(total > 3, "the input must be long enough to cut");

        var boundaries = GraphemeBoundaries(text);

        for (var budget = 1; budget <= total; budget++)
        {
            var index = tokenizer.IndexByTokenCount(text, budget, out var count);

            Assert.InRange(index, 0, text.Length);
            Assert.Contains(index, boundaries);

            // The index is into the string AS PASSED: the prefix it names costs what was reported,
            // and that cost is within budget.
            var prefixCost = tokenizer.CountTokens(text.AsSpan(0, index));
            Assert.Equal(prefixCost, count);
            Assert.True(count <= budget, $"budget {budget}: prefix of {index} chars costs {count}");

            if (budget == total)
            {
                Assert.Equal(text.Length, index);
            }
        }
    }

    [Theory(Skip = Vocabulary.SkipReason, SkipUnless = nameof(Vocabulary.Available), SkipType = typeof(Vocabulary))]
    [MemberData(nameof(Inputs))]
    public void EdgeChunkTokenizer_and_MlChunkTokenizer_agree_on_every_index_and_count(string text)
    {
        var edge = new EdgeChunkTokenizer(Vocabulary.Edge);
        var ml = Vocabulary.Ml;

        Assert.Equal(ml.CountTokens(text), edge.CountTokens(text));
        Assert.Equal(ml.SpecialTokenOverhead, edge.SpecialTokenOverhead);
        Assert.Equal(ml.MaxSequenceLength, edge.MaxSequenceLength);
        Assert.Equal(ml.Id, edge.Id, StringComparer.Ordinal);

        var total = edge.CountTokens(text);
        for (var budget = 1; budget <= total + 1; budget++)
        {
            var edgeIndex = edge.IndexByTokenCount(text, budget, out var edgeCount);
            var mlIndex = ml.IndexByTokenCount(text, budget, out var mlCount);

            Assert.True(
                edgeIndex == mlIndex && edgeCount == mlCount,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "budget {0}: Edge -> ({1}, {2}), Ml -> ({3}, {4})",
                    budget,
                    edgeIndex,
                    edgeCount,
                    mlIndex,
                    mlCount));
        }
    }

    [Fact(Skip = Vocabulary.SkipReason, SkipUnless = nameof(Vocabulary.Available), SkipType = typeof(Vocabulary))]
    public void The_NFD_cut_lands_in_the_original_not_a_normalised_copy()
    {
        // Spec 8.1's motivating case: GetIndexByTokenCount at the default returned 15 into a
        // 26-char normalised copy of this 29-char string, which is mid-word in the original.
        const string nfd = "café café café hello world";
        var tokenizer = new EdgeChunkTokenizer(Vocabulary.Edge);

        var index = tokenizer.IndexByTokenCount(nfd, 5, out _);
        var prefix = nfd[..index];

        // Never splits a combining mark from its base.
        Assert.False(index < nfd.Length && CharUnicodeInfo.GetUnicodeCategory(nfd[index]) == UnicodeCategory.NonSpacingMark);
        Assert.Contains(index, GraphemeBoundaries(nfd));
        Assert.True(tokenizer.CountTokens(prefix) <= 5);
    }

    private static HashSet<int> GraphemeBoundaries(string text)
    {
        var boundaries = new HashSet<int> { 0 };
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            boundaries.Add(enumerator.ElementIndex + enumerator.GetTextElement().Length);
        }

        return boundaries;
    }
}
