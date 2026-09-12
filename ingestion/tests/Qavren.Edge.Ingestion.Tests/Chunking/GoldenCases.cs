namespace Qavren.Edge.Ingestion.Tests.Chunking;

/// <summary>One committed golden: a fixture, a chunker id, and the one option the variant flips.</summary>
internal sealed record GoldenCase(string Golden, string Fixture, string ChunkerId, string Variant);

/// <summary>
/// The twenty goldens plan adjustment 12 authorises, by name: twelve <c>auto</c> goldens over the
/// twelve text and Markdown fixtures, five option-variant goldens under an explicit
/// <c>markdown-heading</c>, and three explicit <c>token-window</c> goldens. 12 + 5 + 3 = 20.
/// </summary>
/// <remarks>
/// The naming convention is <c>&lt;fixture-stem&gt;.&lt;chunker-id&gt;[.&lt;variant&gt;].json</c>,
/// so a diff's file list alone says what moved.
/// </remarks>
internal static class GoldenCases
{
    internal const string DefaultVariant = "default";
    internal const string NoPreambleVariant = "no-preamble";
    internal const string NoBreadcrumbVariant = "no-breadcrumb";

    /// <summary>The twenty, in the order plan Task 4.1 Step 6 tabulates them.</summary>
    internal static IReadOnlyList<GoldenCase> All { get; } =
    [
        // Twelve auto goldens - every fixture, the shipped default configuration.
        new("empty.auto.json", "text/empty.txt", ChunkerIds.Auto, DefaultVariant),
        new("whitespace-only.auto.json", "text/whitespace-only.txt", ChunkerIds.Auto, DefaultVariant),
        new("three-paragraphs.auto.json", "text/three-paragraphs.txt", ChunkerIds.Auto, DefaultVariant),
        new("crlf-and-lone-cr.auto.json", "text/crlf-and-lone-cr.txt", ChunkerIds.Auto, DefaultVariant),
        new("long-token.auto.json", "text/long-token.txt", ChunkerIds.Auto, DefaultVariant),
        new("unicode.auto.json", "text/unicode.txt", ChunkerIds.Auto, DefaultVariant),
        new("bom.auto.json", "text/bom.txt", ChunkerIds.Auto, DefaultVariant),
        new("headings.auto.json", "markdown/headings.md", ChunkerIds.Auto, DefaultVariant),
        new("fences.auto.json", "markdown/fences.md", ChunkerIds.Auto, DefaultVariant),
        new("tables-lists.auto.json", "markdown/tables-lists.md", ChunkerIds.Auto, DefaultVariant),
        new("raw-html.auto.json", "markdown/raw-html.md", ChunkerIds.Auto, DefaultVariant),
        new("giant-heading-section.auto.json", "markdown/giant-heading-section.md", ChunkerIds.Auto, DefaultVariant),

        // Five option-variant goldens - the two options a consumer is most likely to flip.
        new("headings.markdown-heading.no-preamble.json", "markdown/headings.md", ChunkerIds.MarkdownHeading, NoPreambleVariant),
        new("giant-heading-section.markdown-heading.no-preamble.json", "markdown/giant-heading-section.md", ChunkerIds.MarkdownHeading, NoPreambleVariant),
        new("headings.markdown-heading.no-breadcrumb.json", "markdown/headings.md", ChunkerIds.MarkdownHeading, NoBreadcrumbVariant),
        new("tables-lists.markdown-heading.no-breadcrumb.json", "markdown/tables-lists.md", ChunkerIds.MarkdownHeading, NoBreadcrumbVariant),
        new("giant-heading-section.markdown-heading.no-breadcrumb.json", "markdown/giant-heading-section.md", ChunkerIds.MarkdownHeading, NoBreadcrumbVariant),

        // Three token-window goldens - the terminal fallback, explicitly.
        new("long-token.token-window.json", "text/long-token.txt", ChunkerIds.TokenWindow, DefaultVariant),
        new("three-paragraphs.token-window.json", "text/three-paragraphs.txt", ChunkerIds.TokenWindow, DefaultVariant),
        new("giant-heading-section.token-window.json", "markdown/giant-heading-section.md", ChunkerIds.TokenWindow, DefaultVariant),
    ];

    internal static GoldenCase Find(string golden) =>
        All.FirstOrDefault(c => string.Equals(c.Golden, golden, StringComparison.Ordinal))
        ?? throw new ArgumentOutOfRangeException(nameof(golden), golden, "Not one of the twenty goldens.");
}
