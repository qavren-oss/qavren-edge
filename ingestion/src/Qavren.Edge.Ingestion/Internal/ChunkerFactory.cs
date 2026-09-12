using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// Resolves a chunker id plus a media type to one <see cref="IChunker"/> (spec 8.2).
/// <see cref="ChunkerIds.Auto"/> selects <see cref="MarkdownHeadingChunker"/> for
/// <c>text/markdown</c> and <see cref="PlainChunker"/> otherwise; an explicit id is honoured
/// verbatim, and a structure-aware chunker asked to handle a media type it cannot parse falls back
/// with event 922 rather than silently mis-parsing it.
/// </summary>
internal static class ChunkerFactory
{
    private static readonly Action<ILogger, string, string, string, Exception?> LogChunkerFellBack =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Debug,
            new EventId(EdgeIngestionEventIds.ChunkerFellBack, nameof(EdgeIngestionEventIds.ChunkerFellBack)),
            "Chunker '{Requested}' does not apply to media type '{MediaType}'; using '{Selected}'.");

    internal static IChunker Resolve(string? chunkerId, string? mediaType, ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        var id = string.IsNullOrWhiteSpace(chunkerId) ? ChunkerIds.Auto : chunkerId;
        var isMarkdown = string.Equals(mediaType, IngestionMediaTypes.Markdown, StringComparison.OrdinalIgnoreCase);

        switch (id)
        {
            case ChunkerIds.Plain:
                return new PlainChunker(log);

            case ChunkerIds.TokenWindow:
                return new TokenWindowChunker(log);

            case ChunkerIds.MarkdownHeading:
                if (!isMarkdown)
                {
                    LogChunkerFellBack(log, ChunkerIds.MarkdownHeading, mediaType ?? string.Empty, ChunkerIds.Plain, null);
                    return new PlainChunker(log);
                }

                return new MarkdownHeadingChunker(log);

            case ChunkerIds.Auto:
                if (isMarkdown)
                {
                    return new MarkdownHeadingChunker(log);
                }

                LogChunkerFellBack(log, ChunkerIds.Auto, mediaType ?? string.Empty, ChunkerIds.Plain, null);
                return new PlainChunker(log);

            default:
                throw new EdgeIngestionException(
                    EdgeErrorCode.IngestionOptionsInvalid,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "'{0}' is not a known chunker id. Use one of ChunkerIds.Auto, .Plain, .MarkdownHeading, .TokenWindow.",
                        id))
                {
                    Remediation = "Set IngestionOptions.ChunkerId to one of the four ChunkerIds constants.",
                };
        }
    }

    /// <summary>6152 - an INPUT unit bigger than the budget, under <see cref="ChunkOverflow.Throw"/>.</summary>
    internal static EdgeChunkingException ContextTooLong(string chunkerId, int requiredTokens, int budgetTokens) =>
        new(
            EdgeErrorCode.ChunkContextTooLong,
            string.Format(
                CultureInfo.InvariantCulture,
                "Chunker '{0}' was handed a unit of {1} tokens against a frozen budget of {2}, and ChunkOverflow.Throw is configured.",
                chunkerId,
                requiredTokens,
                budgetTokens))
        {
            ChunkerId = chunkerId,
            RequiredTokens = requiredTokens,
            BudgetTokens = budgetTokens,
            Remediation = "Set ChunkOptions.Overflow to Split or Truncate, or raise the budget.",
        };
}
