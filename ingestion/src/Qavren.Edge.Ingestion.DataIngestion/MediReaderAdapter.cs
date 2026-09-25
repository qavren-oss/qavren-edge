using System.Text;
using Microsoft.Extensions.DataIngestion;

namespace Qavren.Edge.Ingestion.DataIngestion;

/// <summary>
/// Turns any MEDI <see cref="IngestionDocumentReader"/> into an SP3 <see cref="IDocumentExtractor"/>
/// (spec 11, plan task 6.3 step 3). This is how a consumer gets a format SP3 does not ship — HTML,
/// say — without SP3 shipping an extractor for it (spec 18): register the adapter through
/// <c>AddDocumentExtractor</c> or <c>IngestionOptions.Extractors</c> and the registry resolves it
/// like any other extractor, ahead of the built-ins.
/// <para>
/// The source is opened through <see cref="DocumentSourceItem.OpenAsync"/>, as every SP3 extractor
/// must be, and handed to the reader's stream overload. When
/// <see cref="ExtractionOptions.NormalizeText"/> is on, each element's text is normalised (CRLF and
/// lone CR to LF, NFC, BOM stripped, U+00A0/U+2007/U+202F folded to a space) <i>before</i> the text buffer is built, so the offsets
/// <see cref="EdgeDocumentConverter.FromMedi"/> assigns already index the normalised text. The
/// same caveat as the core's normaliser applies: under <c>InvariantGlobalization</c>
/// <see cref="string.Normalize(NormalizationForm)"/> is a no-op, so there the line-ending and BOM
/// rules are the whole of it.
/// </para>
/// </summary>
public sealed class MediReaderAdapter : IDocumentExtractor
{
    private readonly IngestionDocumentReader _reader;

    /// <summary>Creates the adapter.</summary>
    /// <param name="reader">The MEDI reader.</param>
    /// <param name="id">Stable, lower-case; feeds the recipe hash.</param>
    /// <param name="extensions">Lower-case, dotted extensions this reader claims.</param>
    /// <param name="mediaTypes">Media types this reader claims.</param>
    /// <param name="version">Bump when the reader's output changes for identical input.</param>
    public MediReaderAdapter(
        IngestionDocumentReader reader,
        string id,
        IReadOnlyList<string> extensions,
        IReadOnlyList<string> mediaTypes,
        int version = 1)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(extensions);
        ArgumentNullException.ThrowIfNull(mediaTypes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);

        _reader = reader;
        Id = id;
        Version = version;
        Extensions = extensions;
        MediaTypes = mediaTypes;
    }

    /// <summary>The wrapped reader.</summary>
    public IngestionDocumentReader Reader => _reader;

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public int Version { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> MediaTypes { get; }

    /// <summary>Always true: the registry's media-type and extension match is the whole decision.</summary>
    public bool CanExtract(DocumentSourceItem item) => item is not null;

    /// <inheritdoc />
    public async ValueTask<ExtractedDocument> ExtractAsync(
        DocumentSourceItem item, ExtractionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(context);

        IngestionDocument medi;
        var stream = await item.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            medi = await _reader.ReadAsync(stream, item.DocumentId, item.MediaType, cancellationToken)
                .ConfigureAwait(false);
        }

        if (context.Options.NormalizeText)
        {
            foreach (var element in medi.EnumerateContent())
            {
                if (!string.IsNullOrEmpty(element.Text))
                {
                    element.Text = Normalize(element.Text);
                }
            }
        }

        var extracted = EdgeDocumentConverter.FromMedi(medi, context.Logger);
        return extracted with
        {
            DocumentId = item.DocumentId,
            ExtractorId = Id,
            ExtractorVersion = Version,
            MediaType = string.IsNullOrEmpty(item.MediaType) ? extracted.MediaType : item.MediaType,
        };
    }

    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            switch (ch)
            {
                case '\r':
                    builder.Append('\n');
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    break;

                case '\uFEFF':
                    break;

                case '\u00A0' or '\u2007' or '\u202F':
                    builder.Append(' ');
                    break;

                default:
                    builder.Append(ch);
                    break;
            }
        }

        var joined = builder.ToString();
        return joined.IsNormalized(NormalizationForm.FormC) ? joined : joined.Normalize(NormalizationForm.FormC);
    }
}
