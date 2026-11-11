using System.Globalization;
using System.IO.Compression;
using System.Xml;

namespace Qavren.Edge.Ingestion.OpenXml.Internal;

/// <summary>
/// The two bounds a DOCX is held to before the Open XML SDK builds anything from it: how far one
/// XML part inflates, and how deep its elements nest.
/// </summary>
/// <remarks>
/// <para>
/// A DOCX is a zip of XML, and both layers can be built to exhaust whatever reads them. A part
/// that inflates to gigabytes is a few hundred kilobytes on disk. A part nested a hundred thousand
/// elements deep is a stack overflow: the SDK's DOM load recurses once per level, and so does
/// <see cref="DocxDomFeed"/>, and a stack overflow is not an exception a run can record - it ends
/// the process, every document after this one with it.
/// </para>
/// <para>
/// So every XML part is read once here first, by a plain <see cref="XmlReader"/> that keeps no
/// tree: a part whose declared size is over the ceiling is refused unread, and one whose inflated
/// bytes or nesting cross a ceiling while it is being read is refused there. Neither ceiling is
/// near a real document - Word's own documented limit is 32 MB of text, and ten tables nested in
/// one another come to about fifty levels - and neither is configurable.
/// </para>
/// <para>
/// The price is one extra streaming read of each XML part. For the documents the DOM path serves
/// (under <c>StreamingThresholdBytes</c>) that is a fraction of the DOM load it precedes.
/// </para>
/// </remarks>
internal static class DocxPackageBounds
{
    /// <summary>No XML part may inflate past this. Also handed to the SDK as <c>MaxCharactersInPart</c>.</summary>
    public const long MaxPartBytes = 256L * 1024 * 1024;

    /// <summary>No XML part may nest elements deeper than this; the root element is level 1.</summary>
    public const int MaxDepth = 256;

    private static readonly XmlReaderSettings ScanSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = false,
    };

    /// <summary>Throws 6104 for the first XML part over either ceiling. Leaves the stream at position 0.</summary>
    /// <param name="stream">The seekable package stream.</param>
    /// <param name="item">The document, for the message.</param>
    /// <param name="sourceId">The source, for the exception.</param>
    /// <param name="extractorName">The extractor's id, for the exception.</param>
    /// <exception cref="EdgeExtractionException">6104, naming the part and the ceiling it crossed.</exception>
    /// <exception cref="InvalidDataException">The stream is not a zip; the caller maps that to 6104 as it always has.</exception>
    public static void ThrowIfHostile(Stream stream, DocumentSourceItem item, string sourceId, string extractorName)
    {
        stream.Position = 0;

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            foreach (var entry in zip.Entries)
            {
                if (!IsXmlPart(entry.FullName))
                {
                    continue;
                }

                // The central directory's word for it, which costs nothing to read. It can lie,
                // which is what the counted read below is for.
                if (entry.Length > MaxPartBytes)
                {
                    throw TooLarge(item, sourceId, extractorName, entry.FullName);
                }

                using var counted = new CountedStream(entry.Open(), MaxPartBytes);
                try
                {
                    using var reader = XmlReader.Create(counted, ScanSettings);
                    while (reader.Read())
                    {
                        // Depth is zero-based: the root element is at 0, so level = Depth + 1.
                        if (reader.NodeType == XmlNodeType.Element && reader.Depth >= MaxDepth)
                        {
                            throw TooDeep(item, sourceId, extractorName, entry.FullName);
                        }
                    }
                }
                catch (PartTooLargeException)
                {
                    throw TooLarge(item, sourceId, extractorName, entry.FullName);
                }
            }
        }

        stream.Position = 0;
    }

    private static bool IsXmlPart(string name) =>
        name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);

    private static EdgeExtractionException TooDeep(
        DocumentSourceItem item, string sourceId, string extractorName, string part) =>
        Refused(
            item,
            sourceId,
            extractorName,
            string.Format(
                CultureInfo.InvariantCulture,
                "Document '{0}' was refused before it was parsed: its part '{1}' nests elements more than {2} deep.",
                item.DocumentId,
                part,
                MaxDepth));

    private static EdgeExtractionException TooLarge(
        DocumentSourceItem item, string sourceId, string extractorName, string part) =>
        Refused(
            item,
            sourceId,
            extractorName,
            string.Format(
                CultureInfo.InvariantCulture,
                "Document '{0}' was refused before it was parsed: its part '{1}' inflates past {2} bytes (256 MiB).",
                item.DocumentId,
                part,
                MaxPartBytes));

    private static EdgeExtractionException Refused(
        DocumentSourceItem item, string sourceId, string extractorName, string message) =>
        new(EdgeErrorCode.DocumentMalformed, message)
        {
            ExtractorName = extractorName,
            SourceId = sourceId,
            DocumentId = item.DocumentId,
            Remediation =
                "The document is recorded as failed and the run continues. No word processor writes a part like " +
                "this; if the document is genuine, re-save it from Word or LibreOffice, or exclude it from the " +
                "source's search pattern.",
        };

    /// <summary>Thrown out of <see cref="CountedStream"/> and caught above; never escapes this class.</summary>
    private sealed class PartTooLargeException : Exception
    {
    }

    /// <summary>A read-only stream that throws once more than its ceiling has been read through it.</summary>
    private sealed class CountedStream(Stream inner, long ceiling) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Count(int read)
        {
            _read += read;
            if (_read > ceiling)
            {
                throw new PartTooLargeException();
            }

            return read;
        }
    }
}
