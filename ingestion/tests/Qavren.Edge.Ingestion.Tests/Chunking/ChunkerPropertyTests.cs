using System.Globalization;
using System.Text;
using CsCheck;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Chunking;

/// <summary>
/// Spec 14.1's chunker properties, over generated strings from an ASCII + CJK + combining-mark +
/// ZWJ alphabet, <c>maxTokens</c> in [24, 512] and <c>overlap</c> in [0, maxTokens / 2).
/// </summary>
/// <remarks>
/// <para>
/// <c>seed:</c> and <c>iter: 500</c> are pinned AT THE CALL SITE - a time-seeded property test in a
/// gate is a flake generator - and the nightly lane widens both through <c>CsCheck_Iter</c> and
/// <c>CsCheck_Seed</c>. A failure prints the seed to replay.
/// </para>
/// <para>
/// Every token count is taken with the SAME <see cref="IChunkTokenizer"/> instance the chunker
/// used, never <c>text.Length / 4</c>. The budget is built as a
/// <see cref="ResolvedChunkOptions"/> directly rather than through
/// <see cref="ChunkOptions.Resolve"/>, because spec 14.1's range reaches 512 while the MiniLM
/// profile's ceiling resolves to 222: these are properties of the CHUNKERS, not of the resolver,
/// whose own arithmetic is pinned by <c>GoldenTests</c> and <c>ChunkerRuleTests</c>.
/// </para>
/// <para>
/// Two of spec 14.1's phrasings are tightened here, and both are recorded rather than quietly
/// changed. OVERLAP EXACTNESS is asserted at CHARACTER level - the shared region is literally the
/// same substring on both sides - plus "the shared region costs at most <c>OverlapTokens</c>".
/// Asserting that the last k TOKENS equal the first k would need token ids, which
/// <see cref="IChunkTokenizer"/> deliberately does not expose, and WordPiece is context-sensitive
/// at a sub-word boundary. MONOTONICITY is asserted across a DOUBLING of <c>MaxTokens</c> rather
/// than across every adjacent pair: a one-token increase can move a sentence-aware nudge onto a
/// different boundary, and "raising MaxTokens never raises the chunk count" is a statement about
/// the budget, not about the nudge.
/// </para>
/// </remarks>
public sealed class ChunkerPropertyTests
{
    private const long DefaultIterations = 500;

    /// <summary>ASCII prose, CJK, an NFD combining mark, a ZWJ family, a Turkish dotted I, and breaks.</summary>
    private static readonly string[] Alphabet =
    [
        "the ", "quick ", "brown ", "fox ", "jumps ", "over ", "a ", "lazy ", "dog ",
        "Mr. ", "e.g. ", "etc. ", ". ", "! ", "? ", "… ", "\n", "\n\n", " ",
        "漢字", "テキスト", "中文",
        "café ", "résumé ",
        "\U0001F468‍\U0001F469‍\U0001F467 ",
        "İ ", "ı ", "I ",
        "supercalifragilisticexpialidocious ",
    ];

    private static Gen<Input> Inputs { get; } = Gen.Select(
        Gen.Int[0, Alphabet.Length - 1].Array[0, 220],
        Gen.Int[24, 512],
        Gen.Int[0, 255],
        (pieces, maxTokens, rawOverlap) => new Input(
            string.Concat(pieces.Select(i => Alphabet[i])),
            maxTokens,
            rawOverlap % Math.Max(1, maxTokens / 2)));

    [Fact]
    public void Every_chunk_is_within_budget_with_truthful_ascending_offsets()
    {
        ChunkingHarness.RequireVocabulary();

        Sample("0000000j4Ji5", input =>
        {
            var options = Options(input);
            foreach (var (chunker, document) in Subjects(input.Text))
            {
                var chunks = chunker.Chunk(document, options, ChunkingHarness.Tokenizer).ToList();

                if (string.IsNullOrWhiteSpace(input.Text))
                {
                    Assert.Empty(chunks);
                    continue;
                }

                var previousStart = -1;
                var previousEnd = -1;
                foreach (var chunk in chunks)
                {
                    Assert.False(string.IsNullOrWhiteSpace(chunk.Text));
                    Assert.True(chunk.CharStart < chunk.CharEnd);
                    Assert.True(chunk.CharEnd <= document.Text.Length);
                    Assert.Equal(document.Text[chunk.CharStart..chunk.CharEnd], chunk.Text);
                    Assert.True(
                        chunk.TokenCount <= options.MaxTokens,
                        $"{chunker.Id}: {chunk.TokenCount} tokens over a budget of {options.MaxTokens}");
                    Assert.Equal(
                        chunk.TokenCount,
                        ChunkingHarness.Tokenizer.CountTokens(chunk.Text.AsSpan()));

                    Assert.True(chunk.CharStart > previousStart, "offsets must strictly ascend");
                    Assert.True(chunk.CharEnd > previousEnd, "offsets must strictly ascend");
                    previousStart = chunk.CharStart;
                    previousEnd = chunk.CharEnd;
                }
            }
        });
    }

    [Fact]
    public void Coverage_is_contiguous_modulo_overlap_with_no_gaps()
    {
        ChunkingHarness.RequireVocabulary();

        Sample("0000000mUSk6", input =>
        {
            var options = Options(input);
            var document = PlainDocument(input.Text);
            var chunks = new TokenWindowChunker().Chunk(document, options, ChunkingHarness.Tokenizer).ToList();

            for (var i = 1; i < chunks.Count; i++)
            {
                Assert.True(
                    chunks[i].CharStart <= chunks[i - 1].CharEnd,
                    $"gap between chars {chunks[i - 1].CharEnd} and {chunks[i].CharStart}");
            }

            if (chunks.Count > 0)
            {
                Assert.True(
                    string.IsNullOrWhiteSpace(document.Text[..chunks[0].CharStart]),
                    "only whitespace may precede the first chunk");
                Assert.True(
                    string.IsNullOrWhiteSpace(document.Text[chunks[^1].CharEnd..]),
                    "only whitespace may follow the last chunk");
            }
        });
    }

    [Fact]
    public void The_shared_region_is_the_same_characters_and_costs_at_most_the_overlap()
    {
        ChunkingHarness.RequireVocabulary();

        Sample("0000000qI-m7", input =>
        {
            var options = Options(input);
            var document = PlainDocument(input.Text);
            var chunks = new TokenWindowChunker().Chunk(document, options, ChunkingHarness.Tokenizer).ToList();

            for (var i = 1; i < chunks.Count; i++)
            {
                var overlapLength = chunks[i - 1].CharEnd - chunks[i].CharStart;
                if (overlapLength <= 0)
                {
                    continue;
                }

                var tailOfPrevious = chunks[i - 1].Text[^overlapLength..];
                var headOfCurrent = chunks[i].Text[..overlapLength];
                Assert.Equal(tailOfPrevious, headOfCurrent);
                Assert.True(
                    ChunkingHarness.Tokenizer.CountTokens(tailOfPrevious.AsSpan()) <= options.OverlapTokens,
                    "the shared region must fit inside OverlapTokens");
            }
        });
    }

    [Fact]
    public void De_overlapped_reassembly_equals_the_covered_input_character_for_character()
    {
        ChunkingHarness.RequireVocabulary();

        Sample("0000000ux8o8", input =>
        {
            var options = Options(input);
            var document = PlainDocument(input.Text);
            var chunks = new TokenWindowChunker().Chunk(document, options, ChunkingHarness.Tokenizer).ToList();
            if (chunks.Count == 0)
            {
                return;
            }

            var rebuilt = new StringBuilder(chunks[0].Text);
            var cursor = chunks[0].CharEnd;
            for (var i = 1; i < chunks.Count; i++)
            {
                if (chunks[i].CharEnd > cursor)
                {
                    rebuilt.Append(document.Text[Math.Max(cursor, chunks[i].CharStart)..chunks[i].CharEnd]);
                    cursor = chunks[i].CharEnd;
                }
            }

            Assert.Equal(document.Text[chunks[0].CharStart..chunks[^1].CharEnd], rebuilt.ToString());
        });
    }

    [Fact]
    public void No_boundary_splits_a_surrogate_pair_or_a_grapheme_cluster()
    {
        ChunkingHarness.RequireVocabulary();

        Sample("0000000ylhq9", input =>
        {
            var boundaries = GraphemeBoundaries(input.Text);
            var options = Options(input);

            foreach (var (chunker, document) in Subjects(input.Text))
            {
                foreach (var chunk in chunker.Chunk(document, options, ChunkingHarness.Tokenizer))
                {
                    Assert.Contains(chunk.CharStart, boundaries);
                    Assert.Contains(chunk.CharEnd, boundaries);
                }
            }
        });
    }

    [Fact]
    public void Raising_MaxTokens_never_raises_the_chunk_count()
    {
        ChunkingHarness.RequireVocabulary();

        Sample("0000000C9qsa", input =>
        {
            var narrow = Options(input);
            var wide = narrow with { MaxTokens = narrow.MaxTokens * 2 };
            if (wide.OverlapTokens >= wide.MaxTokens / 2)
            {
                return;
            }

            var document = PlainDocument(input.Text);
            var chunker = new TokenWindowChunker();
            var narrowCount = chunker.Chunk(document, narrow, ChunkingHarness.Tokenizer).Count();
            var wideCount = chunker.Chunk(document, wide, ChunkingHarness.Tokenizer).Count();

            Assert.True(
                wideCount <= narrowCount,
                $"MaxTokens {narrow.MaxTokens} -> {narrowCount} chunks, {wide.MaxTokens} -> {wideCount}");
        });
    }

    [Fact]
    public void Chunking_is_pure()
    {
        ChunkingHarness.RequireVocabulary();

        Sample("00000003Q9a1", input =>
        {
            var options = Options(input);
            foreach (var (chunker, document) in Subjects(input.Text))
            {
                var first = chunker.Chunk(document, options, ChunkingHarness.Tokenizer).ToList();
                var second = chunker.Chunk(document, options, ChunkingHarness.Tokenizer).ToList();

                Assert.Equal(GoldenFile.Render(first), GoldenFile.Render(second));
            }
        });
    }

    [Fact]
    public void Chunking_is_culture_independent()
    {
        ChunkingHarness.RequireVocabulary();

        CultureInfo? turkish = null;
        try
        {
            turkish = new CultureInfo("tr-TR");
        }
        catch (CultureNotFoundException)
        {
            // InvariantGlobalization is on repo-wide and PredefinedCulturesOnly follows it, so the
            // tr-TR half of this property is unavailable on this build. The invariant half still
            // runs, and the dotted-I inputs are still generated either way.
        }

        Sample("00000007Eic2", input =>
        {
            var options = Options(input);
            var document = PlainDocument(input.Text);
            var chunker = new TokenWindowChunker();

            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                var invariant = GoldenFile.Render(
                    [.. chunker.Chunk(document, options, ChunkingHarness.Tokenizer)]);

                if (turkish is not null)
                {
                    CultureInfo.CurrentCulture = turkish;
                    Assert.Equal(
                        invariant,
                        GoldenFile.Render([.. chunker.Chunk(document, options, ChunkingHarness.Tokenizer)]));
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        });
    }

    [Fact]
    public void Empty_input_yields_no_chunks_and_non_empty_input_yields_no_empty_chunk()
    {
        ChunkingHarness.RequireVocabulary();

        Sample("0000000bsre3", input =>
        {
            var options = Options(input);
            foreach (var (chunker, document) in Subjects(input.Text))
            {
                var chunks = chunker.Chunk(document, options, ChunkingHarness.Tokenizer).ToList();

                if (string.IsNullOrWhiteSpace(document.Text))
                {
                    Assert.Empty(chunks);
                }

                Assert.DoesNotContain(chunks, c => string.IsNullOrWhiteSpace(c.Text));
            }
        });
    }

    // ---- Harness ------------------------------------------------------------------------------

    /// <summary>
    /// <c>threads: 1</c> is not a performance choice: CsCheck spreads a sample across the thread
    /// pool by default, and a pinned seed then reproduces a RUN but not a particular thread's
    /// sequence. A gate whose failures cannot be replayed from the printed seed is a flake
    /// generator, which is the whole reason the seed is pinned at the call site.
    /// </summary>
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

    private static ResolvedChunkOptions Options(Input input) =>
        new(
            input.MaxTokens,
            input.Overlap,
            Math.Max(1, input.MaxTokens / 8),
            HeadingPathTokenBudget: 32,
            SpecialTokenOverhead: 2,
            DocumentPrefixTokens: 0,
            SplitHeadingLevels: [1, 2, 3],
            PrependHeadingPath: true,
            IncludePreamble: true,
            MergeShortSections: true,
            SentenceAware: true,
            RepeatTableHeaderRow: true,
            Overflow: ChunkOverflow.Split);

    /// <summary>The two lossless chunkers over the same generated text.</summary>
    private static IEnumerable<(IChunker Chunker, ExtractedDocument Document)> Subjects(string text)
    {
        var document = PlainDocument(text);
        yield return (new TokenWindowChunker(), document);
        yield return (new PlainChunker(), document);
    }

    /// <summary>Paragraph blocks split on blank lines, the shape <see cref="PlainTextExtractor"/> emits.</summary>
    private static ExtractedDocument PlainDocument(string text)
    {
        var blocks = new List<DocumentBlock>();
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index >= text.Length)
            {
                break;
            }

            var end = text.IndexOf("\n\n", index, StringComparison.Ordinal);
            var stop = end < 0 ? text.Length : end;
            var trimmed = stop;
            while (trimmed > index && char.IsWhiteSpace(text[trimmed - 1]))
            {
                trimmed--;
            }

            if (trimmed > index)
            {
                blocks.Add(new DocumentBlock(DocumentBlockKind.Paragraph, index, trimmed));
            }

            index = end < 0 ? text.Length : end + 2;
        }

        return new ExtractedDocument(
            "generated",
            "text",
            1,
            IngestionMediaTypes.PlainText,
            text,
            blocks,
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static HashSet<int> GraphemeBoundaries(string text)
    {
        var boundaries = new HashSet<int> { 0 };
        var cursor = 0;
        while (cursor < text.Length)
        {
            var length = StringInfo.GetNextTextElementLength(text.AsSpan(cursor));
            cursor += length <= 0 ? 1 : length;
            boundaries.Add(Math.Min(cursor, text.Length));
        }

        return boundaries;
    }

    internal sealed record Input(string Text, int MaxTokens, int Overlap)
    {
        public override string ToString() =>
            string.Format(
                CultureInfo.InvariantCulture,
                "maxTokens={0} overlap={1} chars={2}: {3}",
                MaxTokens,
                Overlap,
                Text.Length,
                Text.Length <= 240 ? Text : Text[..240] + "...");
    }
}
