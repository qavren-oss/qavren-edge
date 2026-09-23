namespace Qavren.Edge.Ingestion;

/// <summary>
/// Extension-to-media-type mapping for the formats SP3 ships. Used by spec 11.3's sample.
/// </summary>
public static class IngestionMediaTypes
{
    /// <summary>Plain text: <c>.txt</c>, <c>.log</c>, <c>.csv</c>, <c>.text</c>.</summary>
    public const string PlainText = "text/plain";

    /// <summary>Markdown: <c>.md</c>, <c>.markdown</c>.</summary>
    public const string Markdown = "text/markdown";

    /// <summary>PDF: <c>.pdf</c>.</summary>
    public const string Pdf = "application/pdf";

    /// <summary>Word Open XML: <c>.docx</c>.</summary>
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>
    /// The value handed back for anything unmapped. Never null. INTERNAL: spec 11 declares four
    /// public constants and documents this value in FromExtension's prose rather than naming a
    /// fifth, and a published MIT surface is easier to add to than to take away from.
    /// </summary>
    internal const string Unknown = "application/octet-stream";

    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = PlainText,
        [".log"] = PlainText,
        [".csv"] = PlainText,
        [".text"] = PlainText,
        [".md"] = Markdown,
        [".markdown"] = Markdown,
        [".pdf"] = Pdf,
        [".docx"] = Docx,
    };

    /// <summary>
    /// Accepts a bare extension, a file name or a full path; matches the last dot-segment,
    /// ordinal-ignore-case. Returns "application/octet-stream" for anything unmapped, never null,
    /// so a caller can hand the result straight to <see cref="DocumentSourceItem.MediaType"/>.
    /// </summary>
    public static string FromExtension(string fileNameOrExtension)
    {
        ArgumentNullException.ThrowIfNull(fileNameOrExtension);

        var extension = ExtensionOf(fileNameOrExtension);
        return extension.Length != 0 && Map.TryGetValue(extension, out var mediaType) ? mediaType : Unknown;
    }

    /// <summary>
    /// The last dot-segment of a bare extension, a file name or a path, lower-cased and dotted.
    /// Empty when there is none.
    /// </summary>
    internal static string ExtensionOf(string fileNameOrExtension)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrExtension))
        {
            return string.Empty;
        }

        var trimmed = fileNameOrExtension.Trim();
        var lastSeparator = trimmed.LastIndexOfAny(['/', '\\']);
        if (lastSeparator >= 0)
        {
            trimmed = trimmed[(lastSeparator + 1)..];
        }

        var dot = trimmed.LastIndexOf('.');
        if (dot < 0)
        {
            // A bare extension with no dot at all - "txt" - is still an extension.
            return trimmed.Length == 0 ? string.Empty : "." + trimmed.ToLowerInvariant();
        }

        return dot == trimmed.Length - 1 ? string.Empty : trimmed[dot..].ToLowerInvariant();
    }
}
