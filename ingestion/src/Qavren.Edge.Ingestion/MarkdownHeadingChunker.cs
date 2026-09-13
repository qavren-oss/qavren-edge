using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qavren.Edge.Ingestion.Internal;

namespace Qavren.Edge.Ingestion;

/// <summary>
/// Spec 8.2's structure-aware chunker: a <c>string?[7]</c> heading stack, cleared below the current
/// level on each new heading, splitting only at <c>SplitHeadingLevels</c> (H1-H3 by default) so H4+
/// stay inside their parent section - which is how documents are actually written.
/// </summary>
/// <remarks>
/// <para>The seven rules, each of which the prior art got wrong:</para>
/// <list type="number">
/// <item><c>ChunkDraft.HeadingPath</c> is the structural array in memory; what is stored is the
/// rendered breadcrumb, sanitised through <see cref="IngestionColumns.SanitizeHeading"/>.</item>
/// <item>A splitting heading is EXCLUDED from the chunk body and prepended to the EMBED text,
/// never duplicated into the stored text.</item>
/// <item>Content before the first heading becomes a preamble chunk when <c>IncludePreamble</c>.
/// The prior art dropped it and lost every document's lede.</item>
/// <item>A section over budget falls to the token window with overlap preserved and the SAME
/// heading path on every piece.</item>
/// <item>Sections under <c>MinTokens</c> merge into the next sibling, event 911.</item>
/// <item>Tables split row-wise with the header row re-emitted on every piece when
/// <c>RepeatTableHeaderRow</c>.</item>
/// <item>A breadcrumb over <c>HeadingPathTokenBudget</c> is truncated from the LEFT and logged as
/// event 910 - in <c>ChunkAssembly</c>, which owns that rule for every chunker.</item>
/// </list>
/// <para>
/// Two behaviours the spec leaves implicit and this implementation fixes, recorded here because a
/// reader of the prose alone would guess differently. FIRST: a heading BELOW the split levels - an
/// H4 under the default H1-H3 - stays in the body as content, because "H4+ stay inside their parent
/// section" and a heading excluded from the body while also not splitting would simply vanish.
/// SECOND: a merged run's stored text stays the verbatim slice <c>[first.Start, last.End)</c>,
/// which means the intervening splitting headings ARE inside a merged body. Joining the bodies
/// instead would make <c>Text</c> stop being <c>Text[CharStart..CharEnd)</c>, and every offset in
/// every golden depends on that identity. A merged chunk takes the FIRST section's heading path,
/// because that is the path at its <c>CharStart</c>.
/// </para>
/// </remarks>
public sealed class MarkdownHeadingChunker : IChunker
{
    private static readonly Action<ILogger, int, int, Exception?> LogChunkMergedUp =
        LoggerMessage.Define<int, int>(
            LogLevel.Debug,
            new EventId(EdgeIngestionEventIds.ChunkMergedUp, nameof(EdgeIngestionEventIds.ChunkMergedUp)),
            "Merged {Count} sections under MinTokens into one chunk of {Tokens} tokens.");

    private readonly ILogger _logger;

    public MarkdownHeadingChunker()
        : this(null)
    {
    }

    public MarkdownHeadingChunker(ILogger? logger) => _logger = logger ?? NullLogger.Instance;

    public string Id => ChunkerIds.MarkdownHeading;

    public int Version => 1;

    public IEnumerable<ChunkDraft> Chunk(
        ExtractedDocument document, ResolvedChunkOptions options, IChunkTokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokenizer);

        return Emit(document, options, tokenizer);
    }

    private IEnumerable<ChunkDraft> Emit(
        ExtractedDocument document, ResolvedChunkOptions options, IChunkTokenizer tokenizer)
    {
        var text = document.Text;
        var sections = Merge(text, Build(document, options), options, tokenizer);
        var ordinal = 0;

        foreach (var section in sections)
        {
            var tokens = tokenizer.CountTokens(text.AsSpan(section.Start, section.End - section.Start));

            if (tokens <= options.MaxTokens)
            {
                yield return ChunkAssembly.CreateSlice(
                    Id, text, section.Start, section.End, tokens, section.Path, options, tokenizer,
                    _logger, ordinal++, section.Kind, section.Page);
                continue;
            }

            if (options.Overflow == ChunkOverflow.Throw)
            {
                throw ChunkerFactory.ContextTooLong(Id, tokens, options.MaxTokens);
            }

            var pieces = options.RepeatTableHeaderRow && IsTable(section)
                ? TableRows(text, section, options, tokenizer, ordinal)
                : Windows(text, section, options, tokenizer, ordinal);

            foreach (var piece in pieces)
            {
                ordinal++;
                yield return piece;

                if (options.Overflow == ChunkOverflow.Truncate)
                {
                    // Cut to budget, and SAY SO (event 921, spec 8.2). The section is over budget
                    // by construction, so the pieces after the first are content leaving the
                    // corpus; a bare break loses them with no record anywhere.
                    ChunkAssembly.ReportTruncated(_logger, Id, tokens, options.MaxTokens);
                    break;
                }
            }
        }
    }

    /// <summary>Rule 4: the token window, with overlap preserved and the same path on every piece.</summary>
    private IEnumerable<ChunkDraft> Windows(
        string text,
        Section section,
        ResolvedChunkOptions options,
        IChunkTokenizer tokenizer,
        int firstOrdinal)
    {
        var ordinal = firstOrdinal;
        foreach (var window in TokenWindow.Windows(text, section.Start, section.End, options, tokenizer, Id))
        {
            yield return ChunkAssembly.CreateSlice(
                Id, text, window.Start, window.End, window.TokenCount, section.Path, options,
                tokenizer, _logger, ordinal++, section.Kind, section.Page);
        }
    }

    /// <summary>Rule 6: row-wise, with the header row re-emitted on every piece.</summary>
    private IEnumerable<ChunkDraft> TableRows(
        string text,
        Section section,
        ResolvedChunkOptions options,
        IChunkTokenizer tokenizer,
        int firstOrdinal)
    {
        var rows = section.Blocks;
        var header = text[rows[0].Start..rows[0].End];
        var ordinal = firstOrdinal;
        var index = 1;

        while (index < rows.Count)
        {
            var start = rows[index].Start;
            var end = rows[index].End;
            var body = header + "\n" + text[start..end];
            var tokens = tokenizer.CountTokens(body.AsSpan());
            index++;

            while (index < rows.Count)
            {
                var candidateEnd = rows[index].End;
                var candidate = header + "\n" + text[start..candidateEnd];
                var candidateTokens = tokenizer.CountTokens(candidate.AsSpan());
                if (candidateTokens > options.MaxTokens)
                {
                    break;
                }

                end = candidateEnd;
                body = candidate;
                tokens = candidateTokens;
                index++;
            }

            if (tokens > options.MaxTokens)
            {
                // One row plus its header does not fit: that row is the oversized unit, so the
                // terminal fallback owns it - without the header, which no longer has room.
                foreach (var window in TokenWindow.Windows(text, start, end, options, tokenizer, Id))
                {
                    yield return ChunkAssembly.CreateSlice(
                        Id, text, window.Start, window.End, window.TokenCount, section.Path,
                        options, tokenizer, _logger, ordinal++, DocumentBlockKind.TableRow, section.Page);
                }

                continue;
            }

            yield return ChunkAssembly.Create(
                Id, body, start, end, tokens, section.Path, options, tokenizer, _logger,
                ordinal++, DocumentBlockKind.TableRow, section.Page);
        }
    }

    private static bool IsTable(Section section) =>
        section.Blocks.Count > 1 && section.Blocks.TrueForAll(b => b.Kind == DocumentBlockKind.TableRow);

    /// <summary>Rule 5: a run under <c>MinTokens</c> absorbs its next siblings while they fit.</summary>
    private List<Section> Merge(
        string text, List<Section> sections, ResolvedChunkOptions options, IChunkTokenizer tokenizer)
    {
        if (!options.MergeShortSections || sections.Count < 2)
        {
            return sections;
        }

        var merged = new List<Section>(sections.Count);
        var i = 0;
        while (i < sections.Count)
        {
            var head = sections[i];
            if (head.IsPreamble)
            {
                // The preamble has no heading path; folding headed content into it would file that
                // content under no heading at all.
                merged.Add(head);
                i++;
                continue;
            }

            var end = head.End;
            var blocks = head.Blocks;
            var tokens = tokenizer.CountTokens(text.AsSpan(head.Start, end - head.Start));
            var absorbed = 1;
            var next = i + 1;

            while (tokens < options.MinTokens && next < sections.Count && !sections[next].IsPreamble)
            {
                var candidateEnd = sections[next].End;
                var candidateTokens = tokenizer.CountTokens(text.AsSpan(head.Start, candidateEnd - head.Start));
                if (candidateTokens > options.MaxTokens)
                {
                    break;
                }

                end = candidateEnd;
                blocks = [.. blocks, .. sections[next].Blocks];
                tokens = candidateTokens;
                absorbed++;
                next++;
            }

            if (absorbed > 1)
            {
                LogChunkMergedUp(_logger, absorbed, tokens, null);
            }

            merged.Add(head with { End = end, Blocks = blocks });
            i = next;
        }

        return merged;
    }

    /// <summary>The heading stack, walked once over the extracted blocks.</summary>
    private static List<Section> Build(ExtractedDocument document, ResolvedChunkOptions options)
    {
        var text = document.Text;
        var stack = new string?[7];
        var sections = new List<Section>();
        var current = new List<DocumentBlock>();
        IReadOnlyList<string> path = [];
        var isPreamble = true;

        void Close()
        {
            if (current.Count == 0)
            {
                return;
            }

            var start = current[0].Start;
            var end = current[^1].End;
            if (!TextSpans.IsWhiteSpace(text, start, end))
            {
                sections.Add(new Section(
                    path, start, end, current[0].Kind, current[0].PageNumber ?? -1, current, isPreamble));
            }

            current = [];
        }

        foreach (var block in document.Blocks)
        {
            if (block.End <= block.Start)
            {
                continue;
            }

            if (block.Kind == DocumentBlockKind.Heading
                && block.HeadingLevel is int level
                && level is >= 1 and <= 6
                && Contains(options.SplitHeadingLevels, level))
            {
                Close();
                isPreamble = false;
                stack[level] = IngestionColumns.SanitizeHeading(HeadingTitle(text, block));
                for (var deeper = level + 1; deeper <= 6; deeper++)
                {
                    stack[deeper] = null;
                }

                path = Render(stack);
                continue;
            }

            current.Add(block);
        }

        Close();

        if (!options.IncludePreamble)
        {
            sections.RemoveAll(s => s.IsPreamble);
        }

        return sections;
    }

    private static bool Contains(IReadOnlyList<int> levels, int level)
    {
        for (var i = 0; i < levels.Count; i++)
        {
            if (levels[i] == level)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> Render(string?[] stack)
    {
        var path = new List<string>(6);
        for (var level = 1; level <= 6; level++)
        {
            var heading = stack[level];
            if (!string.IsNullOrEmpty(heading))
            {
                path.Add(heading);
            }
        }

        return path;
    }

    /// <summary>
    /// The display title of a heading block. ATX drops the leading and trailing <c>#</c> run;
    /// setext keeps the first line and drops the underline.
    /// </summary>
    private static string HeadingTitle(string text, DocumentBlock block)
    {
        var raw = text[block.Start..block.End];
        var newline = raw.IndexOf('\n', StringComparison.Ordinal);
        if (newline >= 0)
        {
            raw = raw[..newline];
        }

        raw = raw.TrimStart('#').Trim();
        return raw.TrimEnd('#').Trim();
    }

    private sealed record Section(
        IReadOnlyList<string> Path,
        int Start,
        int End,
        DocumentBlockKind Kind,
        int Page,
        List<DocumentBlock> Blocks,
        bool IsPreamble);
}
