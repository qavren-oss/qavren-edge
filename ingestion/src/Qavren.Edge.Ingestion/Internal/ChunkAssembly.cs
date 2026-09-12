using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// Turns a char range plus a heading stack into a <see cref="ChunkDraft"/>: the breadcrumb is
/// rendered and budgeted here, the embed text is composed here, and the two invariants every
/// chunker shares — 6151 and 6154 — are enforced here, once.
/// </summary>
internal static class ChunkAssembly
{
    private static readonly Action<ILogger, string, int, int, Exception?> LogHeadingPathTruncated =
        LoggerMessage.Define<string, int, int>(
            LogLevel.Debug,
            new EventId(
                EdgeIngestionEventIds.HeadingPathTruncated,
                nameof(EdgeIngestionEventIds.HeadingPathTruncated)),
            "Breadcrumb '{Breadcrumb}' costs {Tokens} tokens against a HeadingPathTokenBudget of {Budget}; truncated from the left.");

    private static readonly Action<ILogger, string, int, int, Exception?> LogChunkTruncated =
        LoggerMessage.Define<string, int, int>(
            LogLevel.Warning,
            new EventId(EdgeIngestionEventIds.ChunkTruncated, nameof(EdgeIngestionEventIds.ChunkTruncated)),
            "Chunker '{ChunkerId}' truncated a unit of {Tokens} tokens to the frozen budget of {Budget}.");

    /// <summary>
    /// Event 921 — <see cref="ChunkOverflow.Truncate"/> cut an over-budget unit to the budget and
    /// DROPPED the rest of it. Spec 8.2 words that policy "cut to budget, log 921"; the log is the
    /// only record that content left the corpus, so it is a warning and it names the unit's cost.
    /// </summary>
    internal static void ReportTruncated(
        ILogger logger, string chunkerId, int unitTokens, int budgetTokens) =>
        LogChunkTruncated(logger, chunkerId, unitTokens, budgetTokens, null);

    /// <summary>A chunk whose stored text is the verbatim slice <c>text[start..end)</c>.</summary>
    internal static ChunkDraft CreateSlice(
        string chunkerId,
        string text,
        int start,
        int end,
        int tokenCount,
        IReadOnlyList<string> headingPath,
        ResolvedChunkOptions options,
        IChunkTokenizer tokenizer,
        ILogger logger,
        int ordinal,
        DocumentBlockKind kind,
        int page) =>
        Create(
            chunkerId, text[start..end], start, end, tokenCount, headingPath, options, tokenizer,
            logger, ordinal, kind, page);

    /// <summary>
    /// A chunk whose stored text is supplied — the one case where it is not a verbatim slice is a
    /// row-wise table split with the header row re-emitted (spec 8.2 rule 6).
    /// </summary>
    internal static ChunkDraft Create(
        string chunkerId,
        string body,
        int start,
        int end,
        int tokenCount,
        IReadOnlyList<string> headingPath,
        ResolvedChunkOptions options,
        IChunkTokenizer tokenizer,
        ILogger logger,
        int ordinal,
        DocumentBlockKind kind,
        int page)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw TokenWindow.Empty(chunkerId, start, end);
        }

        if (tokenCount > options.MaxTokens)
        {
            throw TokenWindow.Over(chunkerId, tokenCount, options.MaxTokens, start, end);
        }

        var path = Budget(chunkerId, headingPath, options, tokenizer, logger);
        var breadcrumb = IngestionColumns.RenderBreadcrumb(path);
        var embedText = options.PrependHeadingPath && breadcrumb is not null
            ? breadcrumb + "\n\n" + body
            : body;

        return new ChunkDraft(
            ordinal, body, embedText, path, breadcrumb, start, end, tokenCount, kind, page);
    }

    /// <summary>
    /// A breadcrumb over <c>HeadingPathTokenBudget</c> is truncated FROM THE LEFT — the deepest
    /// headings are the most specific — and logged as event 910. It is never allowed to eat the
    /// content budget, which is how the prior art's splitter ends up throwing.
    /// </summary>
    private static List<string> Budget(
        string chunkerId,
        IReadOnlyList<string> headingPath,
        ResolvedChunkOptions options,
        IChunkTokenizer tokenizer,
        ILogger logger)
    {
        if (headingPath.Count == 0)
        {
            return [];
        }

        var path = new List<string>(headingPath);
        var rendered = string.Join(IngestionColumns.HeadingPathSeparator, path);
        var cost = tokenizer.CountTokens(rendered.AsSpan());
        if (cost <= options.HeadingPathTokenBudget)
        {
            return path;
        }

        LogHeadingPathTruncated(logger, rendered, cost, options.HeadingPathTokenBudget, null);

        while (path.Count > 1 && cost > options.HeadingPathTokenBudget)
        {
            path.RemoveAt(0);
            rendered = string.Join(IngestionColumns.HeadingPathSeparator, path);
            cost = tokenizer.CountTokens(rendered.AsSpan());
        }

        if (cost <= options.HeadingPathTokenBudget)
        {
            return path;
        }

        // One heading, still over budget. Left-truncation has nothing left to drop.
        switch (options.Overflow)
        {
            case ChunkOverflow.Throw:
                throw new EdgeChunkingException(
                    EdgeErrorCode.ChunkContextTooLong,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Chunker '{0}' has a single heading costing {1} tokens against a HeadingPathTokenBudget of {2}.",
                        chunkerId,
                        cost,
                        options.HeadingPathTokenBudget))
                {
                    ChunkerId = chunkerId,
                    RequiredTokens = cost,
                    BudgetTokens = options.HeadingPathTokenBudget,
                    Remediation = "Raise ChunkOptions.HeadingPathTokenBudget, or shorten the heading.",
                };

            default:
                var index = tokenizer.IndexByTokenCount(
                    path[0], options.HeadingPathTokenBudget, out var truncatedCost);
                path[0] = path[0][..TextSpans.PreviousBoundary(path[0], 0, Math.Max(index, 0))];
                LogChunkTruncated(logger, chunkerId, truncatedCost, options.HeadingPathTokenBudget, null);
                return path.Count == 1 && path[0].Length == 0 ? [] : path;
        }
    }
}
