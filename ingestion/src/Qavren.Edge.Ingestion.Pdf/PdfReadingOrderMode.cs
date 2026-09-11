namespace Qavren.Edge.Ingestion.Pdf;

/// <summary>
/// Which PdfPig reading-order strategy <see cref="PdfTextExtractor"/> runs per page (spec 7.4, 11).
/// This type belongs to the PDF satellite, not the core: it names a PdfPig strategy and the core
/// must not carry a type only one satellite can mean.
/// </summary>
public enum PdfReadingOrderMode
{
    /// <summary>
    /// <c>ContentOrderTextExtractor</c>: letters in the order the content stream draws them, with
    /// paragraph gaps as double newlines. Cheap, single-threaded, and right for the ordinary
    /// single-column document. The default.
    /// </summary>
    ContentOrder,

    /// <summary>
    /// <c>NearestNeighbourWordExtractor</c> → <c>DocstrumBoundingBoxes</c> →
    /// <c>UnsupervisedReadingOrderDetector</c>: a layout analysis for multi-column pages. Materially
    /// more expensive per page; opt in for a corpus that needs it.
    /// </summary>
    Layout,
}
