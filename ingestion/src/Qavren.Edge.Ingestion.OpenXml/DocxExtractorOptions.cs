namespace Qavren.Edge.Ingestion.OpenXml;

/// <summary>Knobs for <see cref="DocxTextExtractor"/> (spec 7.5). Set them through <c>AddDocxExtractor(configure)</c>.</summary>
public sealed class DocxExtractorOptions
{
    /// <summary>
    /// ON by default, and that is a behaviour-changing default: page headers and footers repeat on
    /// every page and poison embeddings, so they are dropped. Off, they come back as
    /// <see cref="DocumentBlockKind.Footer"/> blocks — headers first, footers last.
    /// </summary>
    public bool ExcludeHeadersAndFooters { get; set; } = true;

    /// <summary>Reach into <c>w:txbxContent</c> (VML and DrawingML text boxes), which extractors routinely miss. On.</summary>
    public bool IncludeTextBoxes { get; set; } = true;

    /// <summary>Append footnotes and endnotes after the body as <see cref="DocumentBlockKind.Footer"/> blocks. On.</summary>
    public bool IncludeNotes { get; set; } = true;

    /// <summary>Emit tables as <see cref="DocumentBlockKind.TableRow"/> blocks, cells pipe-joined, header row first. On.</summary>
    public bool IncludeTables { get; set; } = true;

    /// <summary>
    /// Above this size the main document part is read with <c>OpenXmlPartReader</c> (SAX) instead
    /// of being materialised as a DOM. 8 MiB. Event 913 records which mode ran.
    /// </summary>
    public long StreamingThresholdBytes { get; set; } = 8L * 1024 * 1024;
}
