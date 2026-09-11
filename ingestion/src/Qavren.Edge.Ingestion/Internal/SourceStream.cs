using System.Buffers;
using System.Globalization;
using System.Text;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// Spec 7.1's seekability contract, enforced. <c>OpenAsync</c> SHOULD return a seekable stream at
/// position 0 and MUST be re-openable, because the pipeline calls it twice per document (spec 9.4).
/// A non-seekable stream is buffered ONCE, and only while it fits under
/// <see cref="ExtractionOptions.NonSeekableBufferLimitBytes"/>; above it the read stops and 6053 is
/// raised, because the point is to refuse the allocation, not to relocate it.
/// </summary>
/// <remarks>
/// The reason the limit is low and the failure is loud: a non-seekable stream handed to
/// <c>PdfDocument.Open(Stream)</c> makes PdfPig itself copy the whole PDF into a
/// <see cref="MemoryStream"/>. Doing it silently on SP3's side for a 200 MB scan would be the same
/// bug wearing our name.
/// </remarks>
internal static class SourceStream
{
    /// <summary>The counted-read buffer. Also the ceiling overshoot: at most limit + this.</summary>
    public const int BufferSize = 64 * 1024;

    /// <summary>
    /// Opens <paramref name="item"/> and hands back a seekable stream positioned at 0. The caller
    /// owns and disposes it.
    /// </summary>
    public static async ValueTask<Stream> OpenSeekableAsync(
        DocumentSourceItem item,
        ExtractionOptions options,
        string sourceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(options);

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

        return await BufferAsync(raw, item, options, sourceId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Decodes a seekable stream to text with spec 7.2's three encoding layers. Streams in char
    /// blocks rather than materialising the bytes, and returns the DECODE verbatim - line endings
    /// and all - so a caller that has <see cref="ExtractionOptions.NormalizeText"/> off gets
    /// offsets into the raw decode.
    /// </summary>
    /// <remarks>
    /// DEVIATION from spec 6 and plan step 5, recorded here until they are amended: they say
    /// "line-at-a-time over a StreamReader", and this reads 8 KiB CHAR blocks instead.
    /// <c>ReadLine</c> drops the terminator, so a line loop cannot reproduce the raw decode that
    /// the <c>NormalizeText = false</c> offset path is defined against - it would silently rewrite
    /// every CRLF to LF and move every offset recorded after it. The read is still streaming and
    /// still never materialises the file's bytes, which is what both documents are actually
    /// protecting.
    /// </remarks>
    public static async ValueTask<(string Text, Encoding Encoding, bool UsedLatin1Fallback)> ReadTextAsync(
        Stream stream, bool strictUtf8, DocumentSourceItem item, string sourceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(item);

        var (encoding, detectFromBom, usedFallback) =
            await ResolveEncodingAsync(stream, strictUtf8, item, sourceId, cancellationToken).ConfigureAwait(false);

        stream.Seek(0, SeekOrigin.Begin);

        using var reader = new StreamReader(
            stream, encoding, detectEncodingFromByteOrderMarks: detectFromBom, bufferSize: BufferSize, leaveOpen: true);

        var builder = new StringBuilder();
        var block = ArrayPool<char>.Shared.Rent(8 * 1024);
        try
        {
            int read;
            while ((read = await reader.ReadAsync(block.AsMemory(0, 8 * 1024), cancellationToken).ConfigureAwait(false)) > 0)
            {
                builder.Append(block, 0, read);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(block);
        }

        return (builder.ToString(), reader.CurrentEncoding, usedFallback);
    }

    private static async ValueTask<(Encoding Encoding, bool DetectFromBom, bool UsedLatin1Fallback)> ResolveEncodingAsync(
        Stream stream, bool strictUtf8, DocumentSourceItem item, string sourceId, CancellationToken cancellationToken)
    {
        // Layer 1: a BOM. StreamReader recognises UTF-8, UTF-16 LE/BE and UTF-32 LE/BE from the
        // first four bytes, so when one is present the detection IS the answer.
        stream.Seek(0, SeekOrigin.Begin);
        var probe = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var probed = await stream.ReadAtLeastAsync(
                probe.AsMemory(0, 64 * 1024), 64 * 1024, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);

            if (HasByteOrderMark(probe.AsSpan(0, probed)))
            {
                return (Encoding.UTF8, true, false);
            }

            // Layer 2: a strict UTF-8 probe over the first 64 KiB.
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            try
            {
                // Trim a trailing partial sequence: a 64 KiB cut can land mid-character and that is
                // not evidence of invalid UTF-8.
                var usable = TrimTrailingPartialUtf8(probe.AsSpan(0, probed), truncated: probed == 64 * 1024);
                _ = strict.GetCharCount(usable);
                return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false), true, false);
            }
            catch (DecoderFallbackException ex)
            {
                // Layer 3: Latin-1. Byte-preserving and in the BCL. UTF.Unknown is not taken -
                // MPL-1.1 in an MIT suite, and it drags the legacy code-page tables into a phone.
                if (strictUtf8)
                {
                    throw new EdgeExtractionException(
                        EdgeErrorCode.DocumentEncodingUndecodable,
                        $"Document '{item.DocumentId}' is not valid UTF-8 and PlainTextExtractorOptions.StrictUtf8 is set.",
                        ex)
                    {
                        SourceId = sourceId,
                        DocumentId = item.DocumentId,
                        Remediation =
                            "Re-save the document as UTF-8, or clear PlainTextExtractorOptions.StrictUtf8 to accept the " +
                            "byte-preserving Latin-1 fallback (logged as event 914).",
                    };
                }

                return (Encoding.Latin1, false, true);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(probe);
        }
    }

    private static bool HasByteOrderMark(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 4 && head[0] == 0xFF && head[1] == 0xFE && head[2] == 0x00 && head[3] == 0x00)
        {
            return true;    // UTF-32 LE
        }

        if (head.Length >= 4 && head[0] == 0x00 && head[1] == 0x00 && head[2] == 0xFE && head[3] == 0xFF)
        {
            return true;    // UTF-32 BE
        }

        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
        {
            return true;    // UTF-8
        }

        if (head.Length >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF)))
        {
            return true;    // UTF-16 LE / BE
        }

        return false;
    }

    private static ReadOnlySpan<byte> TrimTrailingPartialUtf8(ReadOnlySpan<byte> bytes, bool truncated)
    {
        if (!truncated || bytes.IsEmpty)
        {
            return bytes;
        }

        // Walk back over continuation bytes to the last lead byte; drop an incomplete sequence.
        var end = bytes.Length;
        var back = 0;
        while (end - 1 - back >= 0 && back < 4 && (bytes[end - 1 - back] & 0b1100_0000) == 0b1000_0000)
        {
            back++;
        }

        var leadIndex = end - 1 - back;
        if (leadIndex < 0)
        {
            return bytes;
        }

        var lead = bytes[leadIndex];
        var expected = lead switch
        {
            < 0x80 => 1,
            >= 0xC0 and < 0xE0 => 2,
            >= 0xE0 and < 0xF0 => 3,
            >= 0xF0 => 4,
            _ => 1,
        };

        return back + 1 < expected ? bytes[..leadIndex] : bytes;
    }

    private static async ValueTask<Stream> BufferAsync(
        Stream raw,
        DocumentSourceItem item,
        ExtractionOptions options,
        string sourceId,
        CancellationToken cancellationToken)
    {
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
                        // Stop HERE. Not one more read: the refusal is the point.
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

    private static EdgeIngestionException TooLargeToBuffer(
        DocumentSourceItem item, string sourceId, long size, long limit) =>
        new(
            EdgeErrorCode.IngestionDocumentUnreadable,
            $"Document '{item.DocumentId}' was opened as a non-seekable stream of at least " +
            size.ToString(CultureInfo.InvariantCulture) + " bytes, over the " +
            limit.ToString(CultureInfo.InvariantCulture) + "-byte non-seekable buffer limit.")
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
            $"Document '{item.DocumentId}' could not be read: {what}.",
            innerException)
        {
            SourceId = sourceId,
            DocumentId = item.DocumentId,
            Remediation =
                "A file deleted or locked between enumeration and open is the common case; the document is recorded as " +
                "failed and the run continues. Check that DocumentSourceItem.OpenAsync is re-openable.",
        };
}
