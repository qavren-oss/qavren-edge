using System.Diagnostics;
using System.Globalization;
using System.Text;
using CsCheck;
using Qavren.Edge.Ingestion.Internal;
using Qavren.Edge.Ingestion.Tests.Runtime;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Chunking;

/// <summary>
/// The window engine used to re-read the rest of a block for every window it cut out of it: one
/// whole-remainder token count, one whole-remainder grapheme walk and one whole-remainder copy per
/// window, so a megabyte paragraph cost a megabyte several hundred times over. These tests pin the
/// two halves of the repair: the cuts are EXACTLY the ones 1.0.0 made (against
/// <see cref="LegacyTokenWindow"/>, a verbatim copy of it), and the work is now proportional to the
/// text.
/// </summary>
/// <remarks>
/// None of this needs the real vocabulary, so none of it skips. The three tokenizers cover the
/// three ways a prefix can cost: one token per word (monotone), a made-up cost that is additive
/// across whitespace but wildly non-monotone inside a word, and a real <c>BertTokenizer</c> over a
/// small synthetic vocabulary, whose 100-character word ceiling makes a prefix of a long token cost
/// a hundred sub-words where the whole token costs one <c>[UNK]</c>. That last shape is the one the
/// search has to be careful about, and the alphabet is built to hit it.
/// </remarks>
public sealed class TokenWindowScalingTests
{
    private const long DefaultIterations = 500;

    private static readonly string[] Alphabet = BuildAlphabet();

    private static readonly Lazy<(string Name, IChunkTokenizer Tokenizer)[]> LazySubjects = new(() =>
    {
        var subjects = new List<(string Name, IChunkTokenizer Tokenizer)>
        {
            ("whitespace words, asked through the public member", new FakeChunkTokenizer()),
            ("non-monotone inside a word", new PartialWordTokenizer()),
            ("WordPiece over a synthetic vocabulary", CreateWordPiece()),
        };

        // The shipped vocabulary as well, on a lane that has it. The three above are what makes
        // the comparison run everywhere.
        if (ChunkingHarness.VocabPath is not null)
        {
            subjects.Add(("WordPiece over bert-base-uncased", ChunkingHarness.Tokenizer));
        }

        return [.. subjects];
    });

    private static Gen<Input> Inputs { get; } = Gen.Select(
        Gen.Int[0, Alphabet.Length - 1].Array[0, 300],
        Gen.Int[4, 96],
        Gen.Int[0, 1 << 20].Array[3],
        Gen.Bool,
        (pieces, maxTokens, raw, sentenceAware) => new Input(
            string.Concat(pieces.Select(i => Alphabet[i])),
            maxTokens,
            raw[0] % Math.Max(1, maxTokens / 2),
            sentenceAware,
            raw[1],
            raw[2]));

    [Fact]
    public void Every_window_is_cut_exactly_where_the_whole_block_search_cut_it()
    {
        Sample("0000000j4Ji5", input =>
        {
            var options = Options(input.MaxTokens, input.Overlap, input.SentenceAware);
            foreach (var (name, tokenizer) in LazySubjects.Value)
            {
                var expected = LegacyTokenWindow
                    .Windows(input.Text, 0, input.Text.Length, options, tokenizer)
                    .ToList();
                var actual = TokenWindow
                    .Windows(input.Text, 0, input.Text.Length, options, tokenizer, ChunkerIds.TokenWindow)
                    .ToList();

                Assert.True(
                    expected.SequenceEqual(actual),
                    $"{name}: expected [{Render(expected)}] but cut [{Render(actual)}]");
            }
        });
    }

    [Fact]
    public void The_search_over_a_range_returns_what_the_search_over_that_substring_returned()
    {
        Sample("0000000mUSk6", input =>
        {
            var from = input.RangeA % (input.Text.Length + 1);
            var to = input.RangeB % (input.Text.Length + 1);
            if (from > to)
            {
                (from, to) = (to, from);
            }

            // Budgets from zero up, so the degenerate ends of the search are in the sample too.
            var budget = input.MaxTokens - 4;
            foreach (var (name, tokenizer) in LazySubjects.Value)
            {
                var expectedIndex = LegacyTokenWindow.Find(
                    tokenizer.CountTokens, input.Text[from..to], budget, out var expectedCount);
                var actualIndex = TokenIndexSearch.Find(
                    tokenizer.CountTokens, input.Text, from, to, budget, out var actualCount);

                Assert.True(
                    expectedIndex == actualIndex && expectedCount == actualCount,
                    $"{name} over [{from}, {to}) at {budget} tokens: expected ({expectedIndex}, {expectedCount}) " +
                    $"but found ({actualIndex}, {actualCount})");
            }
        });
    }

    /// <summary>
    /// The case random text finds once in a few thousand tries, written down. Sixty characters cost
    /// about sixty tokens, and two such runs joined by a character the tokenizer DROPS are one word
    /// over its 100-character ceiling, costing one. <c>BertTokenizer</c> drops every control
    /// character, newline and tab included, so "is this prefix followed by whitespace" is not
    /// evidence that the rest of the text can only add tokens. An earlier draft of the search took
    /// a newline for a word break and cut a 121-character range the 1.0.0 search returned whole.
    /// </summary>
    [Fact]
    public void Two_long_runs_joined_by_any_separator_are_cut_exactly_where_they_were()
    {
        var run = new string('q', 60);
        string[] separators =
        [
            " ", "\n", "\r", "\r\n", "\t", "\n\n", ",", string.Empty, "  ",
            ((char)0x00A0).ToString(),
            ((char)0x2028).ToString(),
            ((char)0x000C).ToString(),
            ((char)0x3000).ToString(),
            ((char)0x200D).ToString(),
            " " + ((char)0x0301).ToString(),
        ];
        string[] tails = [string.Empty, " the quick brown fox", "\nthe quick brown fox\n" + run, " " + run + "\t" + run];
        int[] budgets = [4, 10, 30, 48, 59, 60, 61, 64, 96, 130];

        foreach (var separator in separators)
        {
            foreach (var tail in tails)
            {
                var text = run + separator + run + tail;
                foreach (var budget in budgets)
                {
                    var options = Options(budget, budget / 4, sentenceAware: true);
                    foreach (var (name, tokenizer) in LazySubjects.Value)
                    {
                        var expectedIndex = LegacyTokenWindow.Find(tokenizer.CountTokens, text, budget, out var expectedCount);
                        var actualIndex = TokenIndexSearch.Find(
                            tokenizer.CountTokens, text, 0, text.Length, budget, out var actualCount);
                        var expected = LegacyTokenWindow.Windows(text, 0, text.Length, options, tokenizer).ToList();
                        var actual = TokenWindow
                            .Windows(text, 0, text.Length, options, tokenizer, ChunkerIds.TokenWindow)
                            .ToList();

                        Assert.True(
                            expectedIndex == actualIndex && expectedCount == actualCount && expected.SequenceEqual(actual),
                            $"{name}, separator U+{Codes(separator)}, tail of {tail.Length}, budget {budget}: search expected " +
                            $"({expectedIndex}, {expectedCount}) and found ({actualIndex}, {actualCount}); windows expected " +
                            $"[{Render(expected)}] and cut [{Render(actual)}]");
                    }
                }
            }
        }
    }

    /// <summary>
    /// The bound is on characters handed to <c>CountTokens</c>, not on a stopwatch: it is the same
    /// number on a phone and on a loaded build agent. Each window costs a few dozen probes of about
    /// a window's length, so the whole block costs a few dozen times its own length. The search
    /// this replaced cost half the block per window: over this text, about 800 times its length.
    /// </summary>
    [Fact]
    public void A_megabyte_block_is_chunked_in_work_proportional_to_its_length()
    {
        var text = MegabyteOfRows();
        var document = new ExtractedDocument(
            "rows.csv",
            "text",
            1,
            IngestionMediaTypes.PlainText,
            text,
            [new DocumentBlock(DocumentBlockKind.Paragraph, 0, text.Length)],
            new Dictionary<string, string>(StringComparer.Ordinal));
        var tokenizer = new CountingWordTokenizer();
        var options = Options(maxTokens: 96, overlap: 12, sentenceAware: true);

        var stopwatch = Stopwatch.StartNew();
        var chunks = new PlainChunker().Chunk(document, options, tokenizer).ToList();
        stopwatch.Stop();

        Assert.True(chunks.Count > 1000, $"only {chunks.Count} chunks: the block is not exercising the window engine");
        Assert.Equal(0, chunks[0].CharStart);
        Assert.Equal(text.TrimEnd().Length, chunks[^1].CharEnd);

        Assert.True(
            tokenizer.CharactersCounted <= 128L * text.Length,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0:n0} characters were tokenized to chunk {1:n0}: {2:n0} times the text, where a pass per window is about {3:n0} times.",
                tokenizer.CharactersCounted,
                text.Length,
                tokenizer.CharactersCounted / text.Length,
                chunks.Count / 2));

        // A backstop, not the measurement. The run is well under a second on a laptop.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"chunking took {stopwatch.Elapsed}");
    }

    // ---- Harness ------------------------------------------------------------------------------

    private static void Sample(string pinnedSeed, Action<Input> assert) =>
        Inputs.Sample(assert, seed: Seed(pinnedSeed), iter: Iterations, threads: 1);

    private static long Iterations =>
        long.TryParse(
            Environment.GetEnvironmentVariable("CsCheck_Iter"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var iterations) && iterations > 0
            ? iterations
            : DefaultIterations;

    private static string Seed(string pinned)
    {
        var widened = Environment.GetEnvironmentVariable("CsCheck_Seed");
        return string.IsNullOrWhiteSpace(widened) ? pinned : widened;
    }

    private static ResolvedChunkOptions Options(int maxTokens, int overlap, bool sentenceAware) =>
        new(
            maxTokens,
            overlap,
            Math.Max(1, maxTokens / 8),
            HeadingPathTokenBudget: 32,
            SpecialTokenOverhead: 2,
            DocumentPrefixTokens: 0,
            SplitHeadingLevels: [1, 2, 3],
            PrependHeadingPath: true,
            IncludePreamble: true,
            MergeShortSections: true,
            SentenceAware: sentenceAware,
            RepeatTableHeaderRow: true,
            Overflow: ChunkOverflow.Split);

    private static string Render(IEnumerable<TokenWindowSpan> windows) =>
        string.Join(", ", windows.Select(w => $"{w.Start}..{w.End}={w.TokenCount}"));

    private static string Codes(string text) =>
        text.Length == 0
            ? "(none)"
            : string.Join("+", text.Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture)));

    /// <summary>
    /// Prose, CSV rows, CJK, a combining mark, a ZWJ family and a flag, every kind of break — and
    /// the two pieces the repair turns on: a 130-character word, over WordPiece's 100-character
    /// ceiling, and 60 unbroken characters with no break after them.
    /// </summary>
    /// <remarks>
    /// The non-ASCII pieces are built from code points rather than typed, so no invisible character
    /// has to survive an editor: a literal U+2028 inside a string literal is a compile error.
    /// </remarks>
    private static string[] BuildAlphabet()
    {
        var combiningAcute = ((char)0x0301).ToString();
        var zeroWidthJoiner = ((char)0x200D).ToString();
        var family = string.Concat(
            char.ConvertFromUtf32(0x1F468),
            zeroWidthJoiner,
            char.ConvertFromUtf32(0x1F469),
            zeroWidthJoiner,
            char.ConvertFromUtf32(0x1F467));

        return
        [
            "the ", "quick ", "brown ", "fox ", "jumps ", "over ", "a ", "lazy ", "dog ",
            "Mr. ", "e.g. ", ". ", "! ", "? ", ",", ";", "\n", "\n\n", "\r\n", "\t", " ", "  ",
            "id,name,value\n", "42,alpha,3.14\n",
            "supercalifragilisticexpialidocious ",
            new string('x', 130) + " ",
            new string('q', 60),
            "cafe" + combiningAcute + " ",
            " " + combiningAcute + "x ",
            char.ConvertFromUtf32(0x6F22) + char.ConvertFromUtf32(0x5B57),
            family + " ",
            char.ConvertFromUtf32(0x1F1FA) + char.ConvertFromUtf32(0x1F1F8),
            ((char)0x2028).ToString(),
            ((char)0x00A0).ToString(),
            ((char)0x3000).ToString(),
            ((char)0x000C).ToString(),
        ];
    }

    /// <summary>
    /// A real <c>BertTokenizer</c>, through the same factory a consumer uses, over a vocabulary
    /// small enough to write down: every letter and digit as a word and as a continuation, so any
    /// ASCII word tokenizes into pieces, and a handful of whole words so prefixes cost differently.
    /// </summary>
    private static IChunkTokenizer CreateWordPiece()
    {
        var vocabulary = new List<string> { "[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]" };
        for (var c = 'a'; c <= 'z'; c++)
        {
            vocabulary.Add(c.ToString());
            vocabulary.Add("##" + c);
        }

        for (var c = '0'; c <= '9'; c++)
        {
            vocabulary.Add(c.ToString());
            vocabulary.Add("##" + c);
        }

        vocabulary.AddRange(
        [
            "the", "quick", "brown", "fox", "jump", "over", "lazy", "dog", "mr", "id", "name", "value", "alpha",
            "cafe", "##ing", "##ed", "super", "##cali", ".", ",", "!", "?", ";",
        ]);

        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', vocabulary) + "\n");
        return EdgeTokenCounter.CreateWordPiece(new MemoryStream(bytes), maxSequenceLength: 256, lowerCase: true);
    }

    /// <summary>About a mebibyte of comma-separated rows with no blank line: one block.</summary>
    private static string MegabyteOfRows()
    {
        var builder = new StringBuilder(1100 * 1024);
        for (var row = 0; builder.Length < 1024 * 1024; row++)
        {
            builder
                .Append(row.ToString(CultureInfo.InvariantCulture))
                .Append(", order ")
                .Append((row * 7919 % 10007).ToString(CultureInfo.InvariantCulture))
                .Append(", shipped on day ")
                .Append((row % 365).ToString(CultureInfo.InvariantCulture))
                .Append(" to depot ")
                .Append((row % 41).ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        return builder.ToString();
    }

    internal sealed record Input(string Text, int MaxTokens, int Overlap, bool SentenceAware, int RangeA, int RangeB)
    {
        public override string ToString() =>
            string.Format(
                CultureInfo.InvariantCulture,
                "maxTokens={0} overlap={1} sentenceAware={2} range=({3},{4}) chars={5}: {6}",
                MaxTokens,
                Overlap,
                SentenceAware,
                RangeA,
                RangeB,
                Text.Length,
                Text.Length <= 240 ? Text : Text[..240] + "...");
    }

    /// <summary>
    /// Additive across whitespace and deliberately perverse inside a word: a word whose length is
    /// a multiple of four costs one token, any other costs its length, and one over 24 characters
    /// costs one. So a prefix that stops mid-word is routinely dearer than the word it stops in.
    /// </summary>
    private sealed class PartialWordTokenizer : IChunkTokenizer, IRangeTokenIndex
    {
        public string Id => "partial-word";

        public int MaxSequenceLength => 256;

        public int SpecialTokenOverhead => 2;

        public int CountTokens(ReadOnlySpan<char> text)
        {
            var total = 0;
            var length = 0;
            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch))
                {
                    total += Cost(length);
                    length = 0;
                }
                else
                {
                    length++;
                }
            }

            return total + Cost(length);
        }

        public int IndexByTokenCount(string text, int maxTokens, out int tokenCount) =>
            TokenIndexSearch.Find(CountTokens, text, maxTokens, out tokenCount);

        int IRangeTokenIndex.IndexByTokenCount(string text, int start, int end, int maxTokens, out int tokenCount) =>
            TokenIndexSearch.Find(CountTokens, text, start, end, maxTokens, out tokenCount);

        private static int Cost(int length) => length switch
        {
            0 => 0,
            > 24 => 1,
            _ => length % 4 == 0 ? 1 : length,
        };
    }

    /// <summary>One token per whitespace-separated word, and a tally of every character it was shown.</summary>
    private sealed class CountingWordTokenizer : IChunkTokenizer, IRangeTokenIndex
    {
        public long CharactersCounted { get; private set; }

        public string Id => "counting-words";

        public int MaxSequenceLength => 256;

        public int SpecialTokenOverhead => 2;

        public int CountTokens(ReadOnlySpan<char> text)
        {
            CharactersCounted += text.Length;

            var count = 0;
            var inWord = false;
            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch))
                {
                    inWord = false;
                }
                else if (!inWord)
                {
                    count++;
                    inWord = true;
                }
            }

            return count;
        }

        public int IndexByTokenCount(string text, int maxTokens, out int tokenCount) =>
            TokenIndexSearch.Find(CountTokens, text, maxTokens, out tokenCount);

        int IRangeTokenIndex.IndexByTokenCount(string text, int start, int end, int maxTokens, out int tokenCount) =>
            TokenIndexSearch.Find(CountTokens, text, start, end, maxTokens, out tokenCount);
    }
}
