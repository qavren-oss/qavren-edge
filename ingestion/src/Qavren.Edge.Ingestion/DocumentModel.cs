using Microsoft.Extensions.Logging;

namespace Qavren.Edge.Ingestion;

/// <summary>
/// MEDI's element vocabulary, so the shim is lossless both ways. No image kind (spec 6).
/// </summary>
public enum DocumentBlockKind
{
    /// <summary>Ordinary body text.</summary>
    Paragraph,

    /// <summary>A section heading.</summary>
    Heading,

    /// <summary>One item of a bulleted or numbered list.</summary>
    ListItem,

    /// <summary>One row of a table.</summary>
    TableRow,

    /// <summary>A code block.</summary>
    Code,

    /// <summary>A figure or table caption.</summary>
    Caption,

    /// <summary>A block quotation.</summary>
    Quote,

    /// <summary>Page footer text.</summary>
    Footer,
}

/// <summary><c>[Start, End)</c> half-open into <see cref="ExtractedDocument.Text"/>.</summary>
public readonly record struct DocumentBlock(
    DocumentBlockKind Kind,
    int Start,
    int End,
    int? HeadingLevel = null,
    int? PageNumber = null);

/// <summary>One reported (not thrown) failure. Carries the code so a caller can switch on it.</summary>
public sealed record IngestionFailure(
    EdgeErrorCode Code,
    string Message,
    string? ExtractorId = null,
    string? Remediation = null);

/// <summary>
/// One normalised text buffer plus blocks indexing into it. CRLF and lone CR to LF, NFC, BOM
/// stripped, U+00A0/U+2007/U+202F folded to a space, when <see cref="ExtractionOptions.NormalizeText"/> is on (spec 6).
/// </summary>
public sealed record ExtractedDocument(
    string DocumentId,
    string ExtractorId,
    int ExtractorVersion,
    string MediaType,
    string Text,
    IReadOnlyList<DocumentBlock> Blocks,
    IReadOnlyDictionary<string, string> Metadata,
    int? PageCount = null,
    bool HasTextLayer = true,
    IReadOnlyList<IngestionFailure>? Warnings = null);

/// <summary>
/// What an extractor is handed. Read-only: warnings come back on
/// <see cref="ExtractedDocument.Warnings"/>.
/// </summary>
public sealed record ExtractionContext(
    string SourceId,
    string CollectionName,
    long MaxDocumentBytes,
    ExtractionOptions Options,
    ILogger Logger);
