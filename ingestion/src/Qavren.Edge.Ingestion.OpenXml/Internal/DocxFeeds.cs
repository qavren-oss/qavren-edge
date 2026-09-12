using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Qavren.Edge.Ingestion.OpenXml.Internal;

/// <summary>The DOM traversal: a materialised element tree, walked depth-first into a <see cref="DocxEventWalker"/>.</summary>
internal static class DocxDomFeed
{
    public static void Feed(OpenXmlElement root, DocxEventWalker walker)
    {
        walker.StartElement(root.NamespaceUri, root.LocalName, Val(root));

        if (root is Text text)
        {
            walker.Text(text.Text);
        }

        foreach (var child in root.ChildElements)
        {
            Feed(child, walker);
        }

        walker.EndElement(root.NamespaceUri, root.LocalName);
    }

    private static string? Val(OpenXmlElement element)
    {
        if (!element.HasAttributes)
        {
            return null;
        }

        foreach (var attribute in element.GetAttributes())
        {
            if (string.Equals(attribute.LocalName, "val", StringComparison.Ordinal)
                && string.Equals(attribute.NamespaceUri, DocxEventWalker.W, StringComparison.Ordinal))
            {
                return attribute.Value;
            }
        }

        return null;
    }
}

/// <summary>
/// The streaming traversal: <c>OpenXmlPartReader</c> over the main document part, so a 50 MB
/// <c>document.xml</c> is never materialised as <c>MainDocumentPart.Document.Body</c>. Emits the
/// same event sequence <see cref="DocxDomFeed"/> emits, which the equivalence test asserts.
/// </summary>
internal static class DocxPartReaderFeed
{
    private static readonly OpenXmlPartReaderOptions ReaderOptions = new()
    {
        ReadMiscellaneousNodes = false,
    };

    public static void Feed(OpenXmlPart part, DocxEventWalker walker)
    {
        using var reader = new OpenXmlPartReader(part, ReaderOptions);
        while (reader.Read())
        {
            if (reader.IsStartElement)
            {
                var namespaceUri = reader.NamespaceUri;
                var localName = reader.LocalName;
                walker.StartElement(namespaceUri, localName, Val(reader));

                if (string.Equals(localName, "t", StringComparison.Ordinal)
                    && string.Equals(namespaceUri, DocxEventWalker.W, StringComparison.Ordinal))
                {
                    walker.Text(reader.GetText());
                }
            }
            else if (reader.IsEndElement)
            {
                walker.EndElement(reader.NamespaceUri, reader.LocalName);
            }
        }
    }

    private static string? Val(OpenXmlPartReader reader)
    {
        if (!reader.HasAttributes)
        {
            return null;
        }

        foreach (var attribute in reader.Attributes)
        {
            if (string.Equals(attribute.LocalName, "val", StringComparison.Ordinal)
                && string.Equals(attribute.NamespaceUri, DocxEventWalker.W, StringComparison.Ordinal))
            {
                return attribute.Value;
            }
        }

        return null;
    }
}

/// <summary>
/// <c>styleId</c> → outline level (0–9) for every paragraph style in the styles part, with
/// <c>w:basedOn</c> followed so a derived heading style inherits its base's level. Names are
/// never read: "Titre 1" and "Überschrift 2" resolve exactly like "Heading 1".
/// </summary>
internal static class DocxStyleOutlineLevels
{
    private const int MaxBasedOnDepth = 16;

    public static IReadOnlyDictionary<string, int> Load(StyleDefinitionsPart? part)
    {
        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        var styles = part?.Styles;
        if (styles is null)
        {
            return levels;
        }

        var own = new Dictionary<string, int?>(StringComparer.Ordinal);
        var basedOn = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var style in styles.Elements<Style>())
        {
            var id = style.StyleId?.Value;
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            if (style.Type is { } type && type.Value != StyleValues.Paragraph)
            {
                continue;
            }

            own[id] = style.StyleParagraphProperties?.OutlineLevel?.Val?.Value;
            if (style.BasedOn?.Val?.Value is { Length: > 0 } parent)
            {
                basedOn[id] = parent;
            }
        }

        foreach (var id in own.Keys)
        {
            var current = id;
            for (var hop = 0; hop < MaxBasedOnDepth && current is not null; hop++)
            {
                if (own.TryGetValue(current, out var level) && level is { } resolved)
                {
                    levels[id] = resolved;
                    break;
                }

                current = basedOn.TryGetValue(current, out var next) ? next : null;
            }
        }

        return levels;
    }
}
