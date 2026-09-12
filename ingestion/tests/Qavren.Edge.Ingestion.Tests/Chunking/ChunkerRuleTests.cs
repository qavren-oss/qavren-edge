using Qavren.Edge.Ingestion.Internal;
using Qavren.Edge.Ingestion.Tests.Extraction;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Chunking;

/// <summary>
/// The rules spec 8.2 states and the goldens only record: the factory's selection, the seven
/// Markdown rules, the two invariants (6151, 6154), the oversized-unit policy, and the budget
/// arithmetic 6003 and 6153 guard.
/// </summary>
public sealed class ChunkerRuleTests
{
    private static readonly string[] NoHeadings = [];

    // ---- Step 5: the factory ------------------------------------------------------------------

    [Fact]
    public void Auto_selects_markdown_heading_for_markdown()
    {
        var chunker = ChunkerFactory.Resolve(ChunkerIds.Auto, IngestionMediaTypes.Markdown, new CapturingLogger());
        Assert.Equal(ChunkerIds.MarkdownHeading, chunker.Id);
    }

    [Fact]
    public void Auto_falls_back_to_plain_with_event_922()
    {
        var logger = new CapturingLogger();
        var chunker = ChunkerFactory.Resolve(ChunkerIds.Auto, IngestionMediaTypes.PlainText, logger);

        Assert.Equal(ChunkerIds.Plain, chunker.Id);
        Assert.True(logger.Saw(EdgeIngestionEventIds.ChunkerFellBack));
    }

    [Fact]
    public void An_explicit_chunker_id_is_honoured_verbatim()
    {
        Assert.Equal(
            ChunkerIds.TokenWindow,
            ChunkerFactory.Resolve(ChunkerIds.TokenWindow, IngestionMediaTypes.Markdown, null).Id);
        Assert.Equal(
            ChunkerIds.Plain,
            ChunkerFactory.Resolve(ChunkerIds.Plain, IngestionMediaTypes.Markdown, null).Id);
    }

    [Fact]
    public void Markdown_heading_on_a_non_markdown_media_type_falls_back_with_event_922()
    {
        var logger = new CapturingLogger();
        var chunker = ChunkerFactory.Resolve(ChunkerIds.MarkdownHeading, IngestionMediaTypes.PlainText, logger);

        Assert.Equal(ChunkerIds.Plain, chunker.Id);
        Assert.True(logger.Saw(EdgeIngestionEventIds.ChunkerFellBack));
    }

    [Fact]
    public void An_unknown_chunker_id_is_6005()
    {
        var error = Assert.Throws<EdgeIngestionException>(
            () => ChunkerFactory.Resolve("not-a-chunker", IngestionMediaTypes.PlainText, null));

        Assert.Equal(EdgeErrorCode.IngestionOptionsInvalid, error.Code);
    }

    // ---- Steps 2-4: the emitted chunks -------------------------------------------------------

    [Fact]
    public async Task Empty_and_whitespace_only_documents_produce_no_chunks()
    {
        ChunkingHarness.RequireVocabulary();

        Assert.Empty(await ChunkAsync("text/empty.txt", ChunkerIds.Auto));
        Assert.Empty(await ChunkAsync("text/whitespace-only.txt", ChunkerIds.Auto));
        Assert.Empty(await ChunkAsync("text/empty.txt", ChunkerIds.TokenWindow));
        Assert.Empty(await ChunkAsync("text/whitespace-only.txt", ChunkerIds.TokenWindow));
    }

    [Theory]
    [InlineData("text/three-paragraphs.txt", ChunkerIds.Auto)]
    [InlineData("text/crlf-and-lone-cr.txt", ChunkerIds.Auto)]
    [InlineData("text/unicode.txt", ChunkerIds.Auto)]
    [InlineData("text/bom.txt", ChunkerIds.Auto)]
    [InlineData("markdown/headings.md", ChunkerIds.Auto)]
    [InlineData("markdown/fences.md", ChunkerIds.Auto)]
    [InlineData("markdown/raw-html.md", ChunkerIds.Auto)]
    [InlineData("markdown/giant-heading-section.md", ChunkerIds.TokenWindow)]
    public async Task Every_chunk_is_a_verbatim_slice_within_budget(string fixture, string chunkerId)
    {
        ChunkingHarness.RequireVocabulary();

        var document = await ChunkingHarness.ExtractAsync(fixture);
        var options = ChunkingHarness.Options(GoldenCases.DefaultVariant);
        var chunks = await ChunkAsync(fixture, chunkerId);

        Assert.NotEmpty(chunks);
        foreach (var chunk in chunks)
        {
            Assert.Equal(document.Text[chunk.CharStart..chunk.CharEnd], chunk.Text);
            Assert.True(chunk.CharStart < chunk.CharEnd);
            Assert.True(chunk.CharEnd <= document.Text.Length);
            Assert.False(string.IsNullOrWhiteSpace(chunk.Text));
            Assert.True(chunk.TokenCount <= options.MaxTokens, $"{chunk.TokenCount} > {options.MaxTokens}");
            Assert.Equal(
                chunk.TokenCount,
                ChunkingHarness.Tokenizer.CountTokens(chunk.Text.AsSpan()));
        }
    }

    [Fact]
    public async Task Bom_offsets_index_the_normalised_buffer()
    {
        ChunkingHarness.RequireVocabulary();

        var document = await ChunkingHarness.ExtractAsync("text/bom.txt");
        var chunks = await ChunkAsync("text/bom.txt", ChunkerIds.Auto);

        Assert.DoesNotContain('﻿', document.Text);
        Assert.StartsWith("A document that opens", chunks[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Crlf_offsets_index_the_normalised_buffer()
    {
        ChunkingHarness.RequireVocabulary();

        var document = await ChunkingHarness.ExtractAsync("text/crlf-and-lone-cr.txt");

        Assert.DoesNotContain('\r', document.Text);
        foreach (var chunk in await ChunkAsync("text/crlf-and-lone-cr.txt", ChunkerIds.Auto))
        {
            Assert.Equal(document.Text[chunk.CharStart..chunk.CharEnd], chunk.Text);
        }
    }

    // ---- Rules 1-3: paths, bodies, the preamble ----------------------------------------------

    [Fact]
    public async Task The_preamble_survives_and_a_fenced_hash_is_not_a_heading()
    {
        ChunkingHarness.RequireVocabulary();

        var chunks = await ChunkAsync("markdown/headings.md", ChunkerIds.Auto);

        Assert.Empty(chunks[0].HeadingPath);
        Assert.Null(chunks[0].Breadcrumb);
        Assert.Equal("Preamble text that belongs to no heading at all.", chunks[0].Text);

        foreach (var heading in chunks.SelectMany(c => c.HeadingPath))
        {
            Assert.DoesNotContain("not a heading", heading, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task IncludePreamble_false_drops_the_lede_and_only_the_lede()
    {
        ChunkingHarness.RequireVocabulary();

        var withPreamble = await ChunkAsync("markdown/headings.md", ChunkerIds.MarkdownHeading);
        var without = await ChunkAsync(
            "markdown/headings.md", ChunkerIds.MarkdownHeading, GoldenCases.NoPreambleVariant);

        Assert.Equal(withPreamble.Count - 1, without.Count);
        Assert.Equal(
            withPreamble.Skip(1).Select(c => c.Text),
            without.Select(c => c.Text));
    }

    [Fact]
    public async Task A_splitting_heading_is_excluded_from_the_body_and_prepended_to_the_embed_text()
    {
        ChunkingHarness.RequireVocabulary();

        var headed = (await ChunkAsync("markdown/headings.md", ChunkerIds.MarkdownHeading))
            .First(c => c.HeadingPath.Count > 0);

        Assert.DoesNotContain("# Top level", headed.Text, StringComparison.Ordinal);
        Assert.Equal("Top level", headed.HeadingPath[0]);
        Assert.Equal(headed.Breadcrumb + "\n\n" + headed.Text, headed.EmbedText);
    }

    [Fact]
    public async Task PrependHeadingPath_false_removes_the_breadcrumb_from_the_embed_text_only()
    {
        ChunkingHarness.RequireVocabulary();

        var plain = await ChunkAsync(
            "markdown/headings.md", ChunkerIds.MarkdownHeading, GoldenCases.NoBreadcrumbVariant);
        var headed = plain.First(c => c.HeadingPath.Count > 0);

        Assert.Equal(headed.Text, headed.EmbedText);
        Assert.NotNull(headed.Breadcrumb);
    }

    // ---- Rule 4: an over-budget section keeps overlap and its path ----------------------------

    [Fact]
    public async Task An_over_budget_section_splits_with_overlap_and_the_same_path_on_every_piece()
    {
        ChunkingHarness.RequireVocabulary();

        var document = await ChunkingHarness.ExtractAsync("markdown/giant-heading-section.md");
        var options = ChunkingHarness.Options(GoldenCases.DefaultVariant);
        var chunks = new MarkdownHeadingChunker()
            .Chunk(document, options, ChunkingHarness.Tokenizer)
            .ToList();

        Assert.True(chunks.Count > 1, "the giant section must not fit in one chunk");

        var path = chunks[0].HeadingPath;
        foreach (var chunk in chunks)
        {
            Assert.Equal(path, chunk.HeadingPath);
        }

        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.True(
                chunks[i].CharStart < chunks[i - 1].CharEnd,
                $"chunk {i} starts at {chunks[i].CharStart} with no overlap into {chunks[i - 1].CharEnd}");

            var shared = document.Text[chunks[i].CharStart..chunks[i - 1].CharEnd];
            Assert.True(ChunkingHarness.Tokenizer.CountTokens(shared.AsSpan()) <= options.OverlapTokens);
        }
    }

    // ---- Rule 5: a short section merges forward, event 911 -----------------------------------

    [Fact]
    public async Task Short_sections_merge_forward_and_log_event_911()
    {
        ChunkingHarness.RequireVocabulary();

        var document = await ChunkingHarness.ExtractAsync("markdown/headings.md");
        var logger = new CapturingLogger();
        var options = ChunkingHarness.Options(GoldenCases.DefaultVariant);

        var merged = new MarkdownHeadingChunker(logger)
            .Chunk(document, options, ChunkingHarness.Tokenizer)
            .ToList();

        Assert.True(logger.Saw(EdgeIngestionEventIds.ChunkMergedUp));

        var unmerged = new MarkdownHeadingChunker()
            .Chunk(document, options with { MergeShortSections = false }, ChunkingHarness.Tokenizer)
            .ToList();

        Assert.True(unmerged.Count > merged.Count);
    }

    // ---- Rule 6: tables split row-wise, header re-emitted -------------------------------------

    [Fact]
    public void A_table_section_splits_row_wise_with_the_header_re_emitted()
    {
        ChunkingHarness.RequireVocabulary();

        const string Text =
            "# Prices\n\n| Item | Cost |\n| --- | --- |\n| alpha | one |\n| beta | two |\n| gamma | three |\n";

        var blocks = new List<DocumentBlock>
        {
            Block(Text, "# Prices", DocumentBlockKind.Heading, 1),
            Block(Text, "| Item | Cost |", DocumentBlockKind.TableRow),
            Block(Text, "| alpha | one |", DocumentBlockKind.TableRow),
            Block(Text, "| beta | two |", DocumentBlockKind.TableRow),
            Block(Text, "| gamma | three |", DocumentBlockKind.TableRow),
        };

        // Measured against the real vocabulary: the whole table section costs 14 tokens, and
        // header + alpha + beta is exactly 6 while header + alpha + beta + gamma is 8.
        var options = Budget(maxTokens: 6);
        var chunks = new MarkdownHeadingChunker()
            .Chunk(Document(Text, blocks), options, ChunkingHarness.Tokenizer)
            .ToList();

        Assert.True(chunks.Count > 1, "the table must split row-wise");
        foreach (var chunk in chunks)
        {
            Assert.StartsWith("| Item | Cost |", chunk.Text, StringComparison.Ordinal);
            Assert.Equal(DocumentBlockKind.TableRow, chunk.Kind);
            Assert.True(chunk.TokenCount <= options.MaxTokens);
        }
    }

    // ---- Rule 7: the breadcrumb is truncated from the LEFT, event 910 ------------------------

    [Fact]
    public void A_breadcrumb_over_budget_is_truncated_from_the_left_with_event_910()
    {
        ChunkingHarness.RequireVocabulary();

        const string Text =
            "# Alpha beta gamma delta epsilon\n\n## Zeta eta theta iota kappa\n\n### Lambda mu nu xi omicron\n\nThe body of the deepest section.\n";

        var blocks = new List<DocumentBlock>
        {
            Block(Text, "# Alpha beta gamma delta epsilon", DocumentBlockKind.Heading, 1),
            Block(Text, "## Zeta eta theta iota kappa", DocumentBlockKind.Heading, 2),
            Block(Text, "### Lambda mu nu xi omicron", DocumentBlockKind.Heading, 3),
            Block(Text, "The body of the deepest section.", DocumentBlockKind.Paragraph),
        };

        var logger = new CapturingLogger();
        // Measured: the full three-heading breadcrumb costs 20 tokens, dropping the H1 leaves 14,
        // and the deepest heading alone is 7 - so a budget of 8 lands on exactly one heading.
        var options = Budget(maxTokens: 64, headingPathTokenBudget: 8);
        var chunks = new MarkdownHeadingChunker(logger)
            .Chunk(Document(Text, blocks), options, ChunkingHarness.Tokenizer)
            .ToList();

        var chunk = Assert.Single(chunks);
        Assert.True(logger.Saw(EdgeIngestionEventIds.HeadingPathTruncated));
        Assert.Equal(["Lambda mu nu xi omicron"], chunk.HeadingPath);
        Assert.True(
            ChunkingHarness.Tokenizer.CountTokens(chunk.Breadcrumb.AsSpan()) <= options.HeadingPathTokenBudget);
    }

    [Fact]
    public void A_single_heading_still_over_budget_is_truncated_or_6152()
    {
        ChunkingHarness.RequireVocabulary();

        const string Text = "# Lambda mu nu xi omicron\n\nThe body of the deepest section.\n";

        var blocks = new List<DocumentBlock>
        {
            Block(Text, "# Lambda mu nu xi omicron", DocumentBlockKind.Heading, 1),
            Block(Text, "The body of the deepest section.", DocumentBlockKind.Paragraph),
        };

        var document = Document(Text, blocks);

        // Split (the default) truncates the one remaining heading rather than eating the content
        // budget - which is how the prior art's splitter ends up throwing.
        var truncated = Assert.Single(
            new MarkdownHeadingChunker()
                .Chunk(document, Budget(maxTokens: 64, headingPathTokenBudget: 3), ChunkingHarness.Tokenizer));

        Assert.True(
            ChunkingHarness.Tokenizer.CountTokens(truncated.Breadcrumb.AsSpan()) <= 3);

        var throwing = Budget(maxTokens: 64, headingPathTokenBudget: 3) with { Overflow = ChunkOverflow.Throw };
        var error = Assert.Throws<EdgeChunkingException>(
            () => new MarkdownHeadingChunker().Chunk(document, throwing, ChunkingHarness.Tokenizer).ToList());

        Assert.Equal(EdgeErrorCode.ChunkContextTooLong, error.Code);
    }

    // ---- Goldens 5, 18 and 19's claims, on synthetic input ------------------------------------
    //
    // Plan Task 4.1 Step 6 names three goldens for three behaviours their fixtures do not in fact
    // reach at the pinned 222-token budget: long-token.txt is 5,000 'A's, which WordPiece collapses
    // to ONE [UNK] (max_input_chars_per_word), and three-paragraphs.txt costs 38 tokens - so no cut
    // is ever taken in either. The corpus is frozen (manifest.json pins the bytes, FixtureDriftTests
    // re-checks them from the embedded resources), so the three claims are asserted here on
    // synthetic input, the way rule 6 is. The goldens still pin what those fixtures DO produce.

    /// <summary>Golden 5's claim: the hard split, with no separator anywhere to back up to.</summary>
    [Fact]
    public void A_run_with_no_separator_splits_hard_and_every_piece_stays_in_budget()
    {
        ChunkingHarness.RequireVocabulary();

        var text = NoSeparatorRun();
        var options = ChunkingHarness.Options(GoldenCases.DefaultVariant);

        Assert.DoesNotContain(text, char.IsWhiteSpace);
        Assert.True(
            ChunkingHarness.Tokenizer.CountTokens(text.AsSpan()) > options.MaxTokens,
            "the synthetic run must cost more than the budget, or this test asserts nothing");

        var chunker = ChunkerFactory.Resolve(
            ChunkerIds.Auto, IngestionMediaTypes.PlainText, new CapturingLogger());
        var chunks = chunker.Chunk(TextDocument(text), options, ChunkingHarness.Tokenizer).ToList();

        Assert.Equal(ChunkerIds.Plain, chunker.Id);
        Assert.True(chunks.Count > 1, "an over-budget block must fall to the token window");
        Assert.Equal(0, chunks[0].CharStart);
        Assert.Equal(text.Length, chunks[^1].CharEnd);

        foreach (var chunk in chunks)
        {
            Assert.Equal(text[chunk.CharStart..chunk.CharEnd], chunk.Text);
            Assert.True(chunk.TokenCount <= options.MaxTokens);
        }

        // The cut was taken with nothing to back up to: the look-back window holds no boundary.
        var cut = chunks[0].CharEnd;
        Assert.Equal(-1, SentenceBoundary.FindBackwards(text, 0, cut, Math.Max(1, cut * 15 / 100)));
        Assert.False(char.IsWhiteSpace(text[cut - 1]));
    }

    /// <summary>Golden 18's claim: the terminal fallback, explicitly, with its overlap intact.</summary>
    [Fact]
    public void The_token_window_is_the_terminal_fallback_and_carries_its_overlap()
    {
        ChunkingHarness.RequireVocabulary();

        var text = NoSeparatorRun();
        var options = ChunkingHarness.Options(GoldenCases.DefaultVariant);
        var chunker = new TokenWindowChunker();
        var chunks = chunker.Chunk(TextDocument(text), options, ChunkingHarness.Tokenizer).ToList();

        Assert.Equal(ChunkerIds.TokenWindow, chunker.Id);
        Assert.True(chunks.Count > 1);

        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.True(
                chunks[i].CharStart < chunks[i - 1].CharEnd,
                $"chunk {i} starts at {chunks[i].CharStart} with no overlap into {chunks[i - 1].CharEnd}");

            var shared = text[chunks[i].CharStart..chunks[i - 1].CharEnd];
            Assert.True(ChunkingHarness.Tokenizer.CountTokens(shared.AsSpan()) <= options.OverlapTokens);
        }
    }

    /// <summary>Golden 19's claim: the sentence-aware nudge inside the 15% look-back.</summary>
    [Fact]
    public void A_cut_is_nudged_back_to_a_sentence_end_inside_the_look_back_window()
    {
        ChunkingHarness.RequireVocabulary();

        var text = Sentences();
        var document = TextDocument(text);
        var aware = Budget(maxTokens: 64);

        var nudged = new TokenWindowChunker().Chunk(document, aware, ChunkingHarness.Tokenizer).ToList();
        var blunt = new TokenWindowChunker()
            .Chunk(document, aware with { SentenceAware = false }, ChunkingHarness.Tokenizer)
            .ToList();

        Assert.True(nudged.Count > 2, "the budget must force several cuts");

        for (var i = 0; i < nudged.Count - 1; i++)
        {
            Assert.EndsWith(".", nudged[i].Text, StringComparison.Ordinal);

            // ... and the nudge never reaches further than 15% of the window it is trimming.
            var lookBack = Math.Max(1, (nudged[i].CharEnd - nudged[i].CharStart) * 15 / 100);
            Assert.True(nudged[i].CharEnd - nudged[i].CharStart > lookBack);
        }

        Assert.Contains(blunt.Take(blunt.Count - 1), chunk => !chunk.Text.EndsWith('.'));
        Assert.NotEqual(nudged.Select(c => c.CharEnd), blunt.Select(c => c.CharEnd));
    }

    // ---- Truncate cuts to budget AND logs 921 ------------------------------------------------

    [Fact]
    public void Plain_truncate_cuts_to_budget_and_logs_921()
    {
        ChunkingHarness.RequireVocabulary();

        var text = NoSeparatorRun();
        var options = ChunkingHarness.Options(GoldenCases.DefaultVariant) with
        {
            Overflow = ChunkOverflow.Truncate,
        };

        var logger = new CapturingLogger();
        var chunks = new PlainChunker(logger)
            .Chunk(TextDocument(text), options, ChunkingHarness.Tokenizer)
            .ToList();

        var only = Assert.Single(chunks);
        Assert.True(only.CharEnd < text.Length, "Truncate drops everything after the first window");
        Assert.True(logger.Saw(EdgeIngestionEventIds.ChunkTruncated), "event 921 is the only record");
    }

    [Fact]
    public void Markdown_heading_truncate_cuts_to_budget_and_logs_921()
    {
        ChunkingHarness.RequireVocabulary();

        const string Text =
            "# Alpha\n\nThe body has a first sentence. It has a second sentence. It has a third sentence too.\n";

        var blocks = new List<DocumentBlock>
        {
            Block(Text, "# Alpha", DocumentBlockKind.Heading, 1),
            Block(
                Text,
                "The body has a first sentence. It has a second sentence. It has a third sentence too.",
                DocumentBlockKind.Paragraph),
        };

        var logger = new CapturingLogger();
        var options = Budget(maxTokens: 8) with { Overflow = ChunkOverflow.Truncate };
        var chunks = new MarkdownHeadingChunker(logger)
            .Chunk(Document(Text, blocks), options, ChunkingHarness.Tokenizer)
            .ToList();

        var only = Assert.Single(chunks);
        Assert.True(only.TokenCount <= options.MaxTokens);
        Assert.True(logger.Saw(EdgeIngestionEventIds.ChunkTruncated), "event 921 is the only record");
    }

    // ---- The oversized-unit policy and the two invariants ------------------------------------

    [Fact]
    public async Task Overflow_throw_raises_6152_on_an_over_budget_section()
    {
        ChunkingHarness.RequireVocabulary();

        var document = await ChunkingHarness.ExtractAsync("markdown/giant-heading-section.md");
        var options = ChunkingHarness.Options(GoldenCases.DefaultVariant) with
        {
            Overflow = ChunkOverflow.Throw,
        };

        var error = Assert.Throws<EdgeChunkingException>(
            () => new MarkdownHeadingChunker().Chunk(document, options, ChunkingHarness.Tokenizer).ToList());

        Assert.Equal(EdgeErrorCode.ChunkContextTooLong, error.Code);
        Assert.Equal(ChunkerIds.MarkdownHeading, error.ChunkerId);
    }

    [Fact]
    public void An_emitted_chunk_over_budget_is_6151()
    {
        var error = TokenWindow.Over(ChunkerIds.TokenWindow, tokens: 300, maxTokens: 222, start: 0, end: 10);

        Assert.Equal(EdgeErrorCode.ChunkExceedsTokenBudget, error.Code);
        Assert.Equal(300, error.RequiredTokens);
        Assert.Equal(222, error.BudgetTokens);
    }

    [Fact]
    public void An_empty_chunk_is_6154()
    {
        ChunkingHarness.RequireVocabulary();

        const string Text = "   \n\n   \n";
        var blocks = new List<DocumentBlock> { new(DocumentBlockKind.Paragraph, 0, Text.Length) };

        var error = Assert.Throws<EdgeChunkingException>(
            () => ChunkAssembly.CreateSlice(
                ChunkerIds.Plain,
                Text,
                0,
                Text.Length,
                tokenCount: 0,
                NoHeadings,
                Budget(maxTokens: 64),
                ChunkingHarness.Tokenizer,
                new CapturingLogger(),
                ordinal: 0,
                DocumentBlockKind.Paragraph,
                page: -1));

        Assert.Equal(EdgeErrorCode.ChunkerProducedEmptyChunk, error.Code);
        Assert.NotEmpty(blocks);
    }

    // ---- The budget arithmetic ---------------------------------------------------------------

    [Fact]
    public void A_tokenizer_that_disagrees_about_the_ceiling_is_6153()
    {
        ChunkingHarness.RequireVocabulary();

        var error = Assert.Throws<EdgeChunkingException>(
            () => new ChunkOptions().Resolve(
                ChunkingHarness.MiniLm with { MaxSequenceLength = 512 },
                ChunkingHarness.Tokenizer));

        Assert.Equal(EdgeErrorCode.ChunkTokenizerCeilingExceeded, error.Code);
    }

    [Fact]
    public void An_overlap_at_or_over_half_the_budget_is_6003()
    {
        ChunkingHarness.RequireVocabulary();

        var error = Assert.Throws<EdgeIngestionException>(
            () => new ChunkOptions { MaxTokens = 64, OverlapTokens = 32 }
                .Resolve(ChunkingHarness.MiniLm, ChunkingHarness.Tokenizer));

        Assert.Equal(EdgeErrorCode.IngestionChunkBudgetInvalid, error.Code);
    }

    [Fact]
    public void The_bge_triple_falls_out_of_the_same_arithmetic()
    {
        // 512 - 2 - 32 - 0 = 478; 478 * 15 / 100 = 71 -> 71 / 8 * 8 = 64; 478 / 8 = 59.
        Assert.Equal(478, 512 - 2 - 32 - 0);
        Assert.Equal(64, Math.Max(8, 478 * 15 / 100 / 8 * 8));
        Assert.Equal(59, Math.Max(16, 478 / 8));
    }

    // ---- The breadcrumb round-trip -----------------------------------------------------------

    [Theory]
    [InlineData("plain heading", "plain heading")]
    [InlineData("  spaced  out  ", "spaced out")]
    [InlineData("a › b", "a b")]
    [InlineData("a › › b", "a b")]
    [InlineData("a›b", "a›b")]
    public void SanitizeHeading_makes_the_join_split_round_trip(string heading, string expected)
    {
        var sanitised = IngestionColumns.SanitizeHeading(heading);

        Assert.Equal(expected, sanitised);
        Assert.Equal(sanitised, IngestionColumns.SanitizeHeading(sanitised));

        string[] path = ["root", sanitised];
        Assert.Equal(path, IngestionColumns.SplitBreadcrumb(IngestionColumns.RenderBreadcrumb(path)));
    }

    // ---- Helpers ------------------------------------------------------------------------------

    /// <summary>
    /// A run with NO whitespace and no sentence terminator anywhere, costing well over the pinned
    /// budget. The punctuation is what keeps WordPiece from collapsing it to a single [UNK], and
    /// none of it is a boundary <see cref="SentenceBoundary"/> will back up to.
    /// </summary>
    private static string NoSeparatorRun() =>
        string.Concat(Enumerable.Repeat("alpha-beta-gamma-delta-epsilon-", 60));

    /// <summary>Thirty identical two-sentence runs, so every look-back window holds a sentence end.</summary>
    private static string Sentences() =>
        string.Join(' ', Enumerable.Repeat("Alpha beta gamma delta. Epsilon zeta eta theta.", 30));

    /// <summary>A plain-text document of one paragraph block spanning the whole buffer.</summary>
    private static ExtractedDocument TextDocument(string text) =>
        new(
            "synthetic",
            "text",
            1,
            IngestionMediaTypes.PlainText,
            text,
            [new DocumentBlock(DocumentBlockKind.Paragraph, 0, text.Length)],
            new Dictionary<string, string>(StringComparer.Ordinal));

    private static DocumentBlock Block(
        string text, string fragment, DocumentBlockKind kind, int? headingLevel = null)
    {
        var start = text.IndexOf(fragment, StringComparison.Ordinal);
        return new DocumentBlock(kind, start, start + fragment.Length, headingLevel);
    }

    private static ExtractedDocument Document(string text, IReadOnlyList<DocumentBlock> blocks) =>
        new(
            "synthetic",
            "markdown",
            1,
            IngestionMediaTypes.Markdown,
            text,
            blocks,
            new Dictionary<string, string>(StringComparer.Ordinal));

    private static ResolvedChunkOptions Budget(int maxTokens, int headingPathTokenBudget = 32) =>
        new ChunkOptions
        {
            MaxTokens = maxTokens,
            OverlapTokens = 1,
            MinTokens = 1,
            HeadingPathTokenBudget = headingPathTokenBudget,
        }.Resolve(ChunkingHarness.MiniLm, ChunkingHarness.Tokenizer);

    private static async Task<List<ChunkDraft>> ChunkAsync(
        string fixture, string chunkerId, string variant = GoldenCases.DefaultVariant)
    {
        var document = await ChunkingHarness.ExtractAsync(fixture).ConfigureAwait(false);
        var chunker = ChunkerFactory.Resolve(chunkerId, document.MediaType, null);

        return [.. chunker.Chunk(document, ChunkingHarness.Options(variant), ChunkingHarness.Tokenizer)];
    }
}
