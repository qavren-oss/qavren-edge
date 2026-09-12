using System.Text;

namespace Qavren.Edge.Ingestion.OpenXml.Internal;

/// <summary>
/// The one place blocks are assembled: spec 6's text buffer plus <c>[Start, End)</c> blocks into
/// it. Both traversals — the DOM and the <c>OpenXmlPartReader</c> — feed the same
/// <see cref="DocxEventWalker"/>, which emits through this builder, so the equivalence test
/// measures the two traversals and nothing else.
/// </summary>
internal sealed class DocxTextBuilder
{
    private const string BlockSeparator = "\n\n";

    private readonly StringBuilder _text = new();
    private readonly List<DocumentBlock> _blocks = [];
    private readonly bool _normalize;

    public DocxTextBuilder(bool normalize) => _normalize = normalize;

    public string Text => _text.ToString();

    public IReadOnlyList<DocumentBlock> Blocks => _blocks;

    /// <summary>
    /// Appends one block. Text is normalised BEFORE its offsets are taken (spec 6); a block that
    /// is empty or whitespace after normalisation is not a block.
    /// </summary>
    public void Emit(DocumentBlockKind kind, string text, int? headingLevel)
    {
        var normalised = _normalize ? SatelliteTextNormalizer.Normalize(text) : text;
        if (string.IsNullOrWhiteSpace(normalised))
        {
            return;
        }

        if (_text.Length > 0)
        {
            _text.Append(BlockSeparator);
        }

        var start = _text.Length;
        _text.Append(normalised);
        _blocks.Add(new DocumentBlock(kind, start, _text.Length, kind == DocumentBlockKind.Heading ? headingLevel : null));
    }
}
