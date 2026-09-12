using System.Globalization;
using System.Text;

namespace Qavren.Edge.Ingestion.Tests.Platforms;

/// <summary>
/// A many-page PDF built in memory, so the device lane needs no 200-page fixture in git. Each page
/// is a classic page object with an uncompressed content stream of prose set in Helvetica, which
/// is deliberately NOT embedded: on a device that is the PdfPig asset trap's input, and here it is
/// simply the smallest thing a real text layer can be. ASCII throughout, so offsets are bytes.
/// </summary>
internal static class GeneratedPdf
{
    private const int LinesPerPage = 20;

    /// <summary>The prose on page <paramref name="page"/> (1-based), line <paramref name="line"/>.</summary>
    public static string Line(int page, int line) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Page {0} line {1}: the ingestion budget is metered before every document and every write window.",
            page,
            line);

    /// <summary>Builds a PDF of <paramref name="pages"/> pages.</summary>
    public static byte[] Build(int pages)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pages, 1);

        // Object numbering: 1 catalog, 2 pages, 3 font, then (page, content) pairs from 4.
        var objectCount = 3 + (2 * pages);
        var offsets = new long[objectCount + 1];
        using var stream = new MemoryStream();

        Write(stream, "%PDF-1.4\n%âãÏÓ\n");

        var kids = new StringBuilder();
        for (var page = 0; page < pages; page++)
        {
            kids.Append(CultureInfo.InvariantCulture, $"{4 + (2 * page)} 0 R ");
        }

        BeginObject(stream, offsets, 1);
        Write(stream, "<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        BeginObject(stream, offsets, 2);
        Write(stream, string.Format(CultureInfo.InvariantCulture, "<< /Type /Pages /Kids [ {0}] /Count {1} >>\nendobj\n", kids, pages));

        BeginObject(stream, offsets, 3);
        Write(stream, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        for (var page = 0; page < pages; page++)
        {
            var pageObject = 4 + (2 * page);
            var contentObject = pageObject + 1;

            BeginObject(stream, offsets, pageObject);
            Write(stream, string.Format(
                CultureInfo.InvariantCulture,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {0} 0 R >>\nendobj\n",
                contentObject));

            var content = new StringBuilder("BT /F1 10 Tf 12 TL 40 750 Td\n");
            for (var line = 1; line <= LinesPerPage; line++)
            {
                content.Append('(').Append(Line(page + 1, line)).Append(") Tj T*\n");
            }

            content.Append("ET\n");
            var contentBytes = Encoding.ASCII.GetBytes(content.ToString());

            BeginObject(stream, offsets, contentObject);
            Write(stream, string.Format(CultureInfo.InvariantCulture, "<< /Length {0} >>\nstream\n", contentBytes.Length));
            stream.Write(contentBytes);
            Write(stream, "\nendstream\nendobj\n");
        }

        var xref = stream.Position;
        Write(stream, string.Format(CultureInfo.InvariantCulture, "xref\n0 {0}\n0000000000 65535 f \n", objectCount + 1));
        for (var i = 1; i <= objectCount; i++)
        {
            Write(stream, string.Format(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offsets[i]));
        }

        Write(stream, string.Format(
            CultureInfo.InvariantCulture,
            "trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            objectCount + 1,
            xref));

        return stream.ToArray();
    }

    private static void BeginObject(MemoryStream stream, long[] offsets, int number)
    {
        offsets[number] = stream.Position;
        Write(stream, string.Format(CultureInfo.InvariantCulture, "{0} 0 obj\n", number));
    }

    private static void Write(MemoryStream stream, string text) => stream.Write(Encoding.Latin1.GetBytes(text));
}
