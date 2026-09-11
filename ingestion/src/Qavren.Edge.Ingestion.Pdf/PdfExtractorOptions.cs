namespace Qavren.Edge.Ingestion.Pdf;

/// <summary>Knobs for <see cref="PdfTextExtractor"/> (spec 7.4). Set them through <c>AddPdfExtractor(configure)</c>.</summary>
public sealed class PdfExtractorOptions
{
    /// <summary>The per-page reading-order strategy. <see cref="PdfReadingOrderMode.ContentOrder"/>.</summary>
    public PdfReadingOrderMode ReadingOrder { get; set; } = PdfReadingOrderMode.ContentOrder;

    /// <summary>
    /// ON by default, and that is a behaviour-changing default: on a font-name miss PdfPig otherwise
    /// reads and parses the name table of every file in the system font directory — a multi-second
    /// stall on Android the first time such a PDF appears. Spec 17 item 6 measures what the flag
    /// costs in extraction quality; until it is measured the default stands.
    /// </summary>
    public bool SkipMissingFonts { get; set; } = true;

    /// <summary>Honour <c>/ActualText</c> marked content when present. On.</summary>
    public bool UseActualText { get; set; } = true;

    /// <summary>PdfPig's lenient parser, which recovers a damaged cross-reference by scanning. On.</summary>
    public bool UseLenientParsing { get; set; } = true;

    /// <summary>
    /// Tried in order against an encrypted document, after the empty user password. When none
    /// opens it the document is <see cref="EdgeErrorCode.DocumentEncrypted"/> (6103). Empty.
    /// </summary>
    public IList<string> Passwords { get; } = [];

    /// <summary>
    /// Rejoin a word split by a hyphen at a line break — <c>extra-</c> / <c>ordinary</c> becomes
    /// <c>extraordinary</c> when the next line starts lower-case. On.
    /// </summary>
    public bool JoinHyphenatedLineBreaks { get; set; } = true;

    /// <summary>
    /// Cumulative parse budget for one document, checked BETWEEN pages. Not an abort: PdfPig's
    /// per-page surface is synchronous and takes no <see cref="CancellationToken"/>, so a page
    /// that has started always finishes. When the budget is passed at a page boundary the pages
    /// already parsed are kept, extraction stops, and the document is
    /// <see cref="EdgeErrorCode.DocumentPageBudgetExceeded"/> (6107) naming the page reached. 20 s.
    /// </summary>
    public TimeSpan PageBudget { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>PdfPig's recursion guard for nested resources and forms. 50.</summary>
    public int MaxStackDepth { get; set; } = 50;
}
