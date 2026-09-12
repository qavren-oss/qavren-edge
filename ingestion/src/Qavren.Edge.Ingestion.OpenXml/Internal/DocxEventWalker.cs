using System.Globalization;
using System.Text;

namespace Qavren.Edge.Ingestion.OpenXml.Internal;

/// <summary>
/// The WordprocessingML semantics, as a state machine over start / text / end events. It knows
/// nothing about where the events come from: <see cref="DocxDomFeed"/> walks a materialised
/// part and <see cref="DocxPartReaderFeed"/> pulls the same events from <c>OpenXmlPartReader</c>.
/// Dispatch is on <c>(namespace, local name)</c> rather than on SDK element types so the two
/// feeds cannot disagree about what an element IS.
/// </summary>
/// <remarks>
/// <para>What it does with each construct (spec 7.5):</para>
/// <list type="bullet">
/// <item>Runs are merged: a paragraph's <c>w:t</c> texts are concatenated verbatim, whatever the rsid noise.</item>
/// <item>Heading level comes from <c>w:outlineLvl</c> on the paragraph, else from the paragraph style's outline level
/// resolved through the styles part — never from the style's name. An unknown style is a paragraph.</item>
/// <item>A paragraph carrying <c>w:numPr</c> is a <see cref="DocumentBlockKind.ListItem"/>; the numbering text is never rendered.</item>
/// <item>A table's rows are <see cref="DocumentBlockKind.TableRow"/> blocks, cells joined <c>" | "</c>, header row first; a table nested in a cell flattens into that cell.</item>
/// <item><c>w:txbxContent</c> paragraphs become their own blocks after the paragraph that anchors the box, when <see cref="DocxExtractorOptions.IncludeTextBoxes"/>.</item>
/// <item><c>w:tab</c> is a tab, <c>w:br</c> and <c>w:cr</c> are newlines, tracked deletions are dropped, <c>mc:Fallback</c> is skipped so a text box is read once.</item>
/// </list>
/// </remarks>
internal sealed class DocxEventWalker
{
    public const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private const string CellJoin = " | ";
    private const int NoOutlineLevel = 9;

    private readonly DocxTextBuilder _builder;
    private readonly DocxExtractorOptions _options;
    private readonly IReadOnlyDictionary<string, int> _styleOutlineLevels;
    private readonly DocumentBlockKind? _kindOverride;
    private readonly Stack<ParagraphState> _paragraphs = new();

    private int _depth;
    private int _skipUntilDepth = -1;
    private TableState? _table;

    public DocxEventWalker(
        DocxTextBuilder builder,
        DocxExtractorOptions options,
        IReadOnlyDictionary<string, int> styleOutlineLevels,
        DocumentBlockKind? kindOverride = null)
    {
        _builder = builder;
        _options = options;
        _styleOutlineLevels = styleOutlineLevels;
        _kindOverride = kindOverride;
    }

    /// <param name="namespaceUri">The element's namespace.</param>
    /// <param name="localName">The element's local name.</param>
    /// <param name="val">The element's <c>w:val</c> attribute, when it has one.</param>
    public void StartElement(string namespaceUri, string localName, string? val)
    {
        _depth++;
        if (_skipUntilDepth >= 0)
        {
            return;
        }

        if (string.Equals(namespaceUri, Mc, StringComparison.Ordinal))
        {
            // AlternateContent carries a Choice (DrawingML) and a Fallback (VML) of the SAME text
            // box. Read the Choice, skip the Fallback, or every box is indexed twice.
            if (string.Equals(localName, "Fallback", StringComparison.Ordinal))
            {
                Skip();
            }

            return;
        }

        if (!string.Equals(namespaceUri, W, StringComparison.Ordinal))
        {
            return;
        }

        switch (localName)
        {
            case "p":
                _paragraphs.Push(new ParagraphState(_depth));
                break;

            case "pPr":
                if (_paragraphs.TryPeek(out var owner) && owner.Depth == _depth - 1)
                {
                    owner.InProperties = true;
                }

                break;

            case "outlineLvl":
                if (InProperties(out var levelled) && int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out var level))
                {
                    levelled.OutlineLevel = level;
                }

                break;

            case "pStyle":
                if (InProperties(out var styled) && !string.IsNullOrEmpty(val))
                {
                    styled.StyleId = val;
                }

                break;

            case "numPr":
                if (InProperties(out var numbered))
                {
                    numbered.HasNumbering = true;
                }

                break;

            case "tab":
                AppendToParagraph("\t");
                break;

            case "br":
            case "cr":
                AppendToParagraph("\n");
                break;

            case "noBreakHyphen":
                AppendToParagraph("-");
                break;

            case "del":
            case "moveFrom":
            case "sectPr":
            case "rPr":
                Skip();
                break;

            case "tbl":
                if (_table is null && _paragraphs.Count == 0)
                {
                    if (!_options.IncludeTables)
                    {
                        Skip();
                        break;
                    }

                    _table = new TableState(_depth);
                }

                // A table nested in a cell, or inside a text box, is descended and flattens.
                break;

            case "tr":
                if (_table is not null && _depth == _table.Depth + 1)
                {
                    _table.Cells = [];
                }

                break;

            case "tc":
                if (_table is not null && _depth == _table.Depth + 2)
                {
                    _table.CellPieces = [];
                }

                break;

            case "txbxContent":
                if (!_options.IncludeTextBoxes)
                {
                    Skip();
                }

                break;

            default:
                break;
        }
    }

    /// <summary>The text of one <c>w:t</c>.</summary>
    public void Text(string text)
    {
        if (_skipUntilDepth >= 0)
        {
            return;
        }

        AppendToParagraph(text);
    }

    public void EndElement(string namespaceUri, string localName)
    {
        if (_skipUntilDepth >= 0)
        {
            if (_depth == _skipUntilDepth)
            {
                _skipUntilDepth = -1;
            }

            _depth--;
            return;
        }

        if (string.Equals(namespaceUri, W, StringComparison.Ordinal))
        {
            switch (localName)
            {
                case "pPr":
                    if (_paragraphs.TryPeek(out var owner) && owner.Depth == _depth - 1)
                    {
                        owner.InProperties = false;
                    }

                    break;

                case "p":
                    if (_paragraphs.TryPeek(out var top) && top.Depth == _depth)
                    {
                        _paragraphs.Pop();
                        CloseParagraph(top);
                    }

                    break;

                case "tc":
                    if (_table is not null && _depth == _table.Depth + 2 && _table.CellPieces is { } pieces)
                    {
                        _table.Cells?.Add(string.Join(' ', pieces));
                        _table.CellPieces = null;
                    }

                    break;

                case "tr":
                    if (_table is not null && _depth == _table.Depth + 1 && _table.Cells is { } cells)
                    {
                        if (cells.Exists(c => !string.IsNullOrWhiteSpace(c)))
                        {
                            _builder.Emit(_kindOverride ?? DocumentBlockKind.TableRow, string.Join(CellJoin, cells), null);
                        }

                        _table.Cells = null;
                    }

                    break;

                case "tbl":
                    if (_table is not null && _depth == _table.Depth)
                    {
                        _table = null;
                    }

                    break;

                default:
                    break;
            }
        }

        _depth--;
    }

    private void CloseParagraph(ParagraphState paragraph)
    {
        var text = paragraph.Text.ToString();
        var (kind, level) = Classify(paragraph);

        // Inside a table cell the paragraph is a piece of the cell, never a block of its own.
        if (_table is not null && _table.CellPieces is { } pieces && paragraph.Depth > _table.Depth)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                pieces.Add(text);
            }

            foreach (var deferred in paragraph.Deferred)
            {
                if (!string.IsNullOrWhiteSpace(deferred.Text))
                {
                    pieces.Add(deferred.Text);
                }
            }

            return;
        }

        // A paragraph inside a text box inside another paragraph: deferred until the anchoring
        // paragraph closes, so the box's text lands AFTER the paragraph that carries it.
        if (_paragraphs.TryPeek(out var anchor))
        {
            anchor.Deferred.Add(new DeferredBlock(kind, level, text));
            anchor.Deferred.AddRange(paragraph.Deferred);
            return;
        }

        _builder.Emit(kind, text, level);
        foreach (var deferred in paragraph.Deferred)
        {
            _builder.Emit(deferred.Kind, deferred.Text, deferred.HeadingLevel);
        }
    }

    /// <summary>
    /// Spec 7.5's priority: the paragraph's own <c>w:outlineLvl</c> (0–8 is a level, 9 is "none"),
    /// then the style's outline level through the styles part. A <c>pStyle</c> naming a style that
    /// is not there degrades to a paragraph. No name is ever inspected.
    /// </summary>
    private (DocumentBlockKind Kind, int? HeadingLevel) Classify(ParagraphState paragraph)
    {
        int? level = null;
        if (paragraph.OutlineLevel is { } own)
        {
            if (own is >= 0 and < NoOutlineLevel)
            {
                level = own + 1;
            }
        }
        else if (paragraph.StyleId is { } styleId
            && _styleOutlineLevels.TryGetValue(styleId, out var fromStyle)
            && fromStyle is >= 0 and < NoOutlineLevel)
        {
            level = fromStyle + 1;
        }

        if (_kindOverride is { } forced)
        {
            return (forced, null);
        }

        if (level is not null)
        {
            return (DocumentBlockKind.Heading, level);
        }

        return (paragraph.HasNumbering ? DocumentBlockKind.ListItem : DocumentBlockKind.Paragraph, null);
    }

    private bool InProperties(out ParagraphState paragraph)
    {
        if (_paragraphs.TryPeek(out var top) && top.InProperties)
        {
            paragraph = top;
            return true;
        }

        paragraph = null!;
        return false;
    }

    private void AppendToParagraph(string text)
    {
        if (_paragraphs.TryPeek(out var top) && !top.InProperties)
        {
            top.Text.Append(text);
        }
    }

    private void Skip() => _skipUntilDepth = _depth;

    private sealed record DeferredBlock(DocumentBlockKind Kind, int? HeadingLevel, string Text);

    private sealed class ParagraphState
    {
        public ParagraphState(int depth) => Depth = depth;

        public int Depth { get; }

        public StringBuilder Text { get; } = new();

        public List<DeferredBlock> Deferred { get; } = [];

        public bool InProperties { get; set; }

        public int? OutlineLevel { get; set; }

        public string? StyleId { get; set; }

        public bool HasNumbering { get; set; }
    }

    private sealed class TableState
    {
        public TableState(int depth) => Depth = depth;

        public int Depth { get; }

        public List<string>? Cells { get; set; }

        public List<string>? CellPieces { get; set; }
    }
}
