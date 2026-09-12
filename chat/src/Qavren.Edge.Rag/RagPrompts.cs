using System.Globalization;
using System.Text;

namespace Qavren.Edge.Rag;

/// <summary>The two pure functions the whole recipe rests on, and the three default prompts.</summary>
public static class RagPrompts
{
    /// <summary>The default instruction prefix. Untrusted retrieved text follows it, as a user message.</summary>
    public const string DefaultContextPrompt =
        "Answer using ONLY the numbered sources below. If they do not contain the answer, say you could not find it. Be concise.";

    /// <summary>The default citation instruction. Names the marker shape the middleware resolves.</summary>
    public const string DefaultCitationsPrompt =
        "Cite every claim with the bracketed number of its source, like [1]. Do not invent source numbers.";

    /// <summary>What the turn answers when retrieval returned nothing and no model was called.</summary>
    public const string DefaultNoContextAnswer =
        "I could not find anything in the available sources that answers that.";

    /// <summary>The block's first line, above <c>RagOptions.ContextPrompt</c>.</summary>
    public const string ContextHeading = "## Additional context";

    /// <summary>Appended to a clamped chunk, so a reader can see the model was shown less than the whole thing.</summary>
    public const string TruncationSuffix = "…(truncated)";

    private const string Indent = "    ";
    private const string Separator = "---";
    private const char Lf = '\n';

    /// <summary>
    /// Numbers, clamps, orders and renders the block. Pure - the tier-1 golden-string target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pipeline is <see cref="Rank"/>, then a per-source clamp to
    /// <c>RagOptions.MaxCharsPerSource</c> with a visible <see cref="TruncationSuffix"/>, then
    /// <c>Ordinal</c> 1..n, then a drop from the <b>tail</b> until the rendered block fits
    /// <c>RagOptions.MaxContextTokens</c> as measured by <c>RagOptions.TokenCounter</c> (null uses a
    /// chars/4 estimate). Survivors keep the numbers they were given, so dropping the third of three
    /// leaves 1 and 2.
    /// </para>
    /// <para>
    /// A source with no <c>Title</c> omits the <c>Title:</c> line; one with no <c>Uri</c> omits the
    /// <c>Source:</c> line; the <c>---</c> separator is always present. An empty
    /// <paramref name="sources"/> list renders the empty string rather than a heading with nothing
    /// under it - the caller short-circuits that turn.
    /// </para>
    /// </remarks>
    /// <exception cref="EdgeRagException">
    /// <see cref="EdgeErrorCode.RagContextBudgetTooSmall"/> (7204) when the budget cannot hold even
    /// one clamped source.
    /// </exception>
    public static string Format(IReadOnlyList<RagSource> sources, RagOptions options) =>
        Format(sources, options, out _);

    /// <summary>
    /// <see cref="Format(IReadOnlyList{RagSource}, RagOptions)"/>, also handing back the ranked,
    /// clamped, <c>Ordinal</c>-stamped sources that survived the budget drop - the list
    /// <c>RagChatClient</c> puts on the request-side carrier, so the two cannot disagree about
    /// which sources the model was actually shown.
    /// </summary>
    internal static string Format(
        IReadOnlyList<RagSource> sources, RagOptions options, out IReadOnlyList<RagSource> included)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);

        if (sources.Count == 0)
        {
            included = [];
            return options.ContextFormatter is { } empty ? empty(sources, options) : string.Empty;
        }

        var prepared = Prepare(sources, options);

        if (options.ContextFormatter is { } formatter)
        {
            included = prepared;
            return formatter(prepared, options);
        }

        var count = prepared.Count;
        var countTokens = options.TokenCounter ?? EstimateTokens;

        while (count > 0)
        {
            var block = Render(prepared, count, options);
            if (countTokens(block) <= options.MaxContextTokens)
            {
                included = count == prepared.Count ? prepared : [.. prepared.Take(count)];
                return block;
            }

            count--;
        }

        throw new EdgeRagException(
            EdgeErrorCode.RagContextBudgetTooSmall,
            string.Format(
                CultureInfo.InvariantCulture,
                "The retrieved-context budget of {0} token(s) cannot hold even one source clamped to {1} character(s).",
                options.MaxContextTokens,
                options.MaxCharsPerSource),
            remediation:
                "Raise RagOptions.MaxContextTokens, lower RagOptions.MaxCharsPerSource, or shorten " +
                "RagOptions.ContextPrompt and RagOptions.CitationsPrompt - both are inside the same budget.");
    }

    /// <summary>
    /// Normalises to best-first without inventing a number: a <see cref="RetrievalScoreKind.Distance"/>
    /// list is ordered ascending, a <see cref="RetrievalScoreKind.Relevance"/> list descending.
    /// Nothing is rescaled, because there is no defensible scale to rescale onto.
    /// </summary>
    /// <remarks>
    /// No <c>Score</c> and no <c>Ordinal</c> is touched; the same instances come back reordered. The
    /// sort is stable, so equal scores keep the retriever's order, and the key
    /// (<c>Distance ? Score : -Score</c>) means a list that mixes the two kinds still orders
    /// deterministically rather than picking one polarity for both.
    /// </remarks>
    public static IReadOnlyList<RagSource> Rank(IReadOnlyList<RagSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        return sources.Count <= 1
            ? sources
            : [.. sources.OrderBy(static s => s.ScoreKind == RetrievalScoreKind.Distance ? s.Score : -s.Score)];
    }

    /// <summary>The <c>RagOptions.TokenCounter</c> default: a chars/4 estimate, rounded up.</summary>
    internal static int EstimateTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    /// <summary>Clamps one chunk to <paramref name="maxChars"/>, appending a visible truncation marker.</summary>
    internal static string Clamp(string text, int maxChars)
    {
        if (maxChars <= 0)
        {
            return TruncationSuffix;
        }

        // The ends-with guard keeps Clamp - and therefore Prepare and Format - idempotent, so a
        // caller that prepares a list and then formats it does not stamp a second suffix on.
        return text.Length <= maxChars || text.EndsWith(TruncationSuffix, StringComparison.Ordinal)
            ? text
            : string.Concat(text.AsSpan(0, maxChars), TruncationSuffix);
    }

    /// <summary>Ranks, clamps and stamps <c>Ordinal</c> 1..n. The list the block and the carriers share.</summary>
    internal static IReadOnlyList<RagSource> Prepare(IReadOnlyList<RagSource> sources, RagOptions options)
    {
        var ranked = Rank(sources);
        var prepared = new RagSource[ranked.Count];

        for (var i = 0; i < ranked.Count; i++)
        {
            var source = ranked[i];
            prepared[i] = source with
            {
                Text = Clamp(source.Text, options.MaxCharsPerSource),
                Ordinal = i + 1,
            };
        }

        return prepared;
    }

    private static string Render(IReadOnlyList<RagSource> prepared, int count, RagOptions options)
    {
        var builder = new StringBuilder();
        builder.Append(ContextHeading).Append(Lf);

        if (!string.IsNullOrEmpty(options.ContextPrompt))
        {
            builder.Append(options.ContextPrompt).Append(Lf);
        }

        for (var i = 0; i < count; i++)
        {
            var source = prepared[i];

            builder.Append(Lf);
            builder.Append('[').Append(source.Ordinal.ToString(CultureInfo.InvariantCulture)).Append(']');

            if (!string.IsNullOrEmpty(source.Title))
            {
                builder.Append(" Title: ").Append(source.Title);
            }

            builder.Append(Lf);

            if (source.Uri is { } uri)
            {
                builder.Append(Indent).Append("Source: ").Append(uri.ToString()).Append(Lf);
            }

            builder.Append(Indent).Append(Separator).Append(Lf);
            AppendIndented(builder, source.Text);
        }

        if (!string.IsNullOrEmpty(options.CitationsPrompt))
        {
            builder.Append(Lf).Append(options.CitationsPrompt);
        }

        return builder.ToString();
    }

    private static void AppendIndented(StringBuilder builder, string text)
    {
        var start = 0;

        while (true)
        {
            var end = text.IndexOf(Lf, start);
            var line = end < 0 ? text.AsSpan(start) : text.AsSpan(start, end - start);

            builder.Append(Indent).Append(line.TrimEnd('\r')).Append(Lf);

            if (end < 0)
            {
                return;
            }

            start = end + 1;
        }
    }
}
