using System.Text;

namespace Qavren.Edge.Ingestion;

/// <summary>
/// Every storage name as a <c>const</c>, plus the breadcrumb separator and the sanitiser that
/// makes the join/split round-trip (spec 8.3). The sanitiser lives beside the separator on
/// purpose: the round-trip is a property of the PAIR, and splitting them across two files is how
/// the two drift.
/// </summary>
public static class IngestionColumns
{
    public const string Key = "key";
    public const string Embedding = "embedding";
    public const string SourceId = "source_id";
    public const string DocumentId = "document_id";
    public const string Ordinal = "ordinal";
    public const string Text = "text";
    public const string HeadingPath = "heading_path";
    public const string ContentHash = "content_hash";
    public const string CharStart = "char_start";
    public const string CharEnd = "char_end";
    public const string TokenCount = "token_count";
    public const string Page = "page";
    public const string BlockKind = "block_kind";
    public const string ExtractorId = "extractor_id";
    public const string MediaType = "media_type";
    public const string CreatedUtc = "created_utc";

    /// <summary>
    /// <c>" \u203A "</c> — U+203A between two spaces. Joins and splits <c>heading_path</c>;
    /// headings are sanitised against it at chunk time so the split round-trips (spec 8.3).
    /// </summary>
    public const string HeadingPathSeparator = " \u203A ";

    /// <summary>
    /// Makes one heading safe to join: control characters go, whitespace runs collapse to a single
    /// space, every occurrence of <see cref="HeadingPathSeparator"/> collapses to a single space,
    /// and the result is trimmed. Heading text is display text, so this is lossless in every way a
    /// reader would notice and lossy in exactly the way that would otherwise corrupt a boundary.
    /// Idempotent, and culture-invariant throughout.
    /// </summary>
    public static string SanitizeHeading(string heading)
    {
        ArgumentNullException.ThrowIfNull(heading);

        var builder = new StringBuilder(heading.Length);
        var pendingSpace = false;
        foreach (var ch in heading)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length != 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        var collapsed = builder.ToString();

        // Removing one separator can expose another - "a > > b" collapses twice - so loop to a
        // fixed point rather than making one pass and hoping.
        string previous;
        do
        {
            previous = collapsed;
            collapsed = collapsed.Replace(HeadingPathSeparator, " ", StringComparison.Ordinal);
        }
        while (!string.Equals(previous, collapsed, StringComparison.Ordinal));

        return collapsed.Trim();
    }

    /// <summary>The rendered breadcrumb for a structural path, or null when the path is empty.</summary>
    public static string? RenderBreadcrumb(IReadOnlyList<string> headingPath)
    {
        ArgumentNullException.ThrowIfNull(headingPath);

        if (headingPath.Count == 0)
        {
            return null;
        }

        return string.Join(HeadingPathSeparator, headingPath);
    }

    /// <summary>The structural path a stored breadcrumb splits back into. Empty when null.</summary>
    public static IReadOnlyList<string> SplitBreadcrumb(string? breadcrumb) =>
        string.IsNullOrEmpty(breadcrumb)
            ? []
            : breadcrumb.Split(HeadingPathSeparator, StringSplitOptions.None);
}
