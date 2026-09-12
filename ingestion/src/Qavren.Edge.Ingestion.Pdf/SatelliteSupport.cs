using System.Buffers;
using System.Globalization;
using System.Text;

namespace Qavren.Edge.Ingestion.Pdf;

/// <summary>
/// Spec 7.1's seekability contract, enforced satellite-side. The core's <c>SourceStream</c> is
/// internal and the core is frozen from the wave-5 close on, so the satellite carries its own copy
/// of the two rules rather than a grant: a non-seekable stream is buffered ONCE while it fits under
/// <see cref="ExtractionOptions.NonSeekableBufferLimitBytes"/>, and refused with 6053 above it —
/// because a non-seekable stream handed to <c>PdfDocument.Open(Stream)</c> makes PdfPig itself copy
/// the whole file into memory, which is exactly the allocation the stream overload exists to avoid.
/// </summary>
internal static class SatelliteSourceStream
{
    private const int BufferSize = 64 * 1024;

    public static async ValueTask<Stream> OpenSeekableAsync(
        DocumentSourceItem item, ExtractionOptions options, string sourceId, CancellationToken cancellationToken)
    {
        Stream raw;
        try
        {
            raw = await item.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw Unreadable(item, sourceId, "the source stream could not be opened", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw Unreadable(item, sourceId, "the source stream could not be opened", ex);
        }

        if (raw is null)
        {
            throw Unreadable(item, sourceId, "OpenAsync returned null", innerException: null);
        }

        if (raw.CanSeek)
        {
            if (raw.Position != 0)
            {
                raw.Seek(0, SeekOrigin.Begin);
            }

            return raw;
        }

        var limit = options.NonSeekableBufferLimitBytes;
        await using (raw.ConfigureAwait(false))
        {
            if (item.SizeBytes is { } declared && declared > limit)
            {
                throw TooLargeToBuffer(item, sourceId, declared, limit);
            }

            var buffer = new MemoryStream();
            var rented = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                long total = 0;
                int read;
                while ((read = await raw.ReadAsync(rented.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > limit)
                    {
                        await buffer.DisposeAsync().ConfigureAwait(false);
                        throw TooLargeToBuffer(item, sourceId, total, limit);
                    }

                    buffer.Write(rented, 0, read);
                }
            }
            catch (IOException ex)
            {
                await buffer.DisposeAsync().ConfigureAwait(false);
                throw Unreadable(item, sourceId, "the source stream faulted while being buffered", ex);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            buffer.Position = 0;
            return buffer;
        }
    }

    private static EdgeIngestionException TooLargeToBuffer(DocumentSourceItem item, string sourceId, long size, long limit) =>
        new(
            EdgeErrorCode.IngestionDocumentUnreadable,
            string.Format(
                CultureInfo.InvariantCulture,
                "Document '{0}' was opened as a non-seekable stream of at least {1} bytes, over the {2}-byte non-seekable buffer limit.",
                item.DocumentId,
                size,
                limit))
        {
            SourceId = sourceId,
            DocumentId = item.DocumentId,
            SizeBytes = size,
            Remediation =
                "Copy the content to a file or another seekable stream first and return that from OpenAsync, or raise " +
                "IngestionOptions.Extraction.NonSeekableBufferLimitBytes if buffering it in memory really is acceptable.",
        };

    private static EdgeIngestionException Unreadable(
        DocumentSourceItem item, string sourceId, string what, Exception? innerException) =>
        new(
            EdgeErrorCode.IngestionDocumentUnreadable,
            string.Format(CultureInfo.InvariantCulture, "Document '{0}' could not be read: {1}.", item.DocumentId, what),
            innerException)
        {
            SourceId = sourceId,
            DocumentId = item.DocumentId,
            Remediation =
                "A file deleted or locked between enumeration and open is the common case; the document is recorded as " +
                "failed and the run continues. Check that DocumentSourceItem.OpenAsync is re-openable.",
        };
}

/// <summary>
/// Spec 6's normalisation — CRLF and lone CR to LF, a leading BOM stripped, NFC — applied to each
/// page's text BEFORE its block offsets are computed. Same rules as the core's internal
/// <c>TextNormalizer</c>, and the same caveat: under <c>InvariantGlobalization</c> the NFC step is
/// a no-op, and nothing downstream depends on composition having happened.
/// </summary>
internal static class SatelliteTextNormalizer
{
    public static string Normalize(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var stripped = text[0] == '\uFEFF' ? text[1..] : text;
        var fixedEndings = FixLineEndings(stripped);

        return fixedEndings.IsNormalized(NormalizationForm.FormC)
            ? fixedEndings
            : fixedEndings.Normalize(NormalizationForm.FormC);
    }

    private static string FixLineEndings(string text)
    {
        if (text.IndexOf('\r', StringComparison.Ordinal) < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\r')
            {
                builder.Append(c);
                continue;
            }

            builder.Append('\n');
            if (i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }
        }

        return builder.ToString();
    }
}
