namespace Qavren.Edge.Ingestion;

/// <summary>
/// Extractor-agnostic knobs. Per-format options live on the satellites' own option types.
/// </summary>
public sealed class ExtractionOptions
{
    /// <summary>
    /// A non-seekable source stream (spec 7.1) is buffered once up to this size; above it, 6053.
    /// 4 MiB, deliberately low: the whole point is to refuse the allocation, not to relocate it.
    /// </summary>
    public long NonSeekableBufferLimitBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>Normalise CRLF and lone CR to LF, apply NFC, strip a BOM. On; spec 6 depends on it.</summary>
    public bool NormalizeText { get; set; } = true;
}
