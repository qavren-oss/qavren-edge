using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Qavren.Edge.Ingestion.Extractors.Tests;

/// <summary>
/// PDFs assembled at test time from committed bytes: the one Flate path that cannot be spelled in
/// ASCII, a padded file for the lazy-read assertion, a malformed file for 6104 (the committed
/// <c>broken-startxref.pdf</c> is RECOVERED by PdfPig 0.1.16, so 6104 needs its own raise site),
/// and a password-protected file for 6103. Every byte is derived from committed input plus a
/// deterministic transform, so each assembly is repeatable — and a test asserts it.
/// </summary>
internal static class TestPdf
{
    private const string Catalog = "<< /Type /Catalog /Pages 2 0 R >>";
    private const string Pages = "<< /Type /Pages /Kids [4 0 R] /Count 1 >>";
    private const string Font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";
    private const string PageDictionary =
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R >>";

    private static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>
    /// <c>flate-content.pdf</c>: the committed <c>flate-content.stream</c> deflated at a fixed
    /// level and spliced into a <c>/Filter /FlateDecode</c> content stream. <c>ZLibStream</c> writes
    /// the RFC 1950 framing PdfPig's Flate filter expects; the same input at the same level yields
    /// the same bytes on every run.
    /// </summary>
    public static byte[] FlateContent()
    {
        var plain = FixtureCorpus.Bytes("pdf/flate-content.stream");

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(plain);
        }

        var body = compressed.ToArray();
        return Assemble(
        [
            Ascii(Catalog),
            Ascii(Pages),
            Ascii(Font),
            Ascii(PageDictionary),
            Stream(body, "/Filter /FlateDecode"),
        ]);
    }

    /// <summary>
    /// <c>minimal-text.pdf</c>'s five objects plus an UNREFERENCED sixth object carrying one MiB
    /// of padding. A parser that opens through the cross-reference table never touches it; a
    /// parser that slurps the file reads it all.
    /// </summary>
    public static byte[] Padded(int paddingBytes = 1024 * 1024)
    {
        var padding = new byte[paddingBytes];
        Array.Fill(padding, (byte)'x');

        return Assemble(
        [
            Ascii(Catalog),
            Ascii(Pages),
            Ascii(Font),
            Ascii(PageDictionary),
            Stream(MinimalContent(), string.Empty),
            Stream(padding, string.Empty),
        ]);
    }

    /// <summary>
    /// <c>minimal-text.pdf</c> cut off twelve bytes into its content stream: no <c>endstream</c>,
    /// no cross-reference table, no trailer. PdfPig 0.1.16's lenient parser cannot recover it
    /// ("Could not find an xref trailer or stream dictionary"), which the committed
    /// <c>broken-startxref.pdf</c> — RECOVERED by brute-force scan — cannot claim. The 6104 raise site.
    /// </summary>
    public static byte[] TruncatedObject()
    {
        var whole = FixtureCorpus.Bytes("pdf/minimal-text.pdf");
        var text = Latin1.GetString(whole);
        var cut = text.IndexOf("stream\n", StringComparison.Ordinal) + "stream\n".Length + 12;
        return whole[..cut];
    }

    /// <summary>A file that is not a PDF at all: the version header is missing.</summary>
    public static byte[] NotAPdf() => Ascii("This is not a PDF. It has no header, no objects, no cross-reference table and no trailer.\n");

    /// <summary>
    /// <c>minimal-text.pdf</c> encrypted with the standard security handler, revision 2, 40-bit
    /// RC4 — the oldest and simplest shape, and one PdfPig opens given the right password. Built
    /// here rather than committed because the plan commits ASCII fixtures only. MD5 and RC4 are
    /// what the PDF 1.4 standard security handler IS; there is no other way to write one.
    /// </summary>
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "The PDF revision-2 standard security handler is defined over MD5 + RC4; this builds a test fixture, not a security boundary.")]
    public static byte[] Encrypted(string userPassword, string ownerPassword = "owner")
    {
        ReadOnlySpan<byte> pad =
        [
            0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
            0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
        ];

        var documentId = MD5.HashData(Ascii("qavren-edge-sp3 encrypted fixture"));
        var permissions = -1;

        var paddedUser = PadPassword(userPassword, pad);
        var paddedOwner = PadPassword(ownerPassword, pad);

        // Algorithm 3: the owner entry.
        var ownerKey = MD5.HashData(paddedOwner)[..5];
        var ownerEntry = Rc4(ownerKey, paddedUser);

        // Algorithm 2: the file key, revision 2.
        using var keyInput = new MemoryStream();
        keyInput.Write(paddedUser);
        keyInput.Write(ownerEntry);
        keyInput.Write(BitConverter.GetBytes(permissions));
        keyInput.Write(documentId);
        var fileKey = MD5.HashData(keyInput.ToArray())[..5];

        // Algorithm 4: the user entry, revision 2.
        var userEntry = Rc4(fileKey, pad.ToArray());

        // The content stream is object 5 generation 0: key = MD5(fileKey + n(3, LE) + g(2, LE))[..10].
        var objectKeyInput = new byte[fileKey.Length + 5];
        fileKey.CopyTo(objectKeyInput, 0);
        objectKeyInput[fileKey.Length] = 5;
        var objectKey = MD5.HashData(objectKeyInput)[..10];
        var encryptedContent = Rc4(objectKey, MinimalContent());

        var encryptDictionary = string.Format(
            CultureInfo.InvariantCulture,
            "<< /Filter /Standard /V 1 /R 2 /Length 40 /P {0} /O <{1}> /U <{2}> >>",
            permissions,
            Convert.ToHexString(ownerEntry),
            Convert.ToHexString(userEntry));

        var idHex = Convert.ToHexString(documentId);
        return Assemble(
            [
                Ascii(Catalog),
                Ascii(Pages),
                Ascii(Font),
                Ascii(PageDictionary),
                Stream(encryptedContent, string.Empty),
                Ascii(encryptDictionary),
            ],
            trailerExtra: $"/Encrypt 6 0 R /ID [<{idHex}> <{idHex}>]");
    }

    /// <summary>
    /// Writes objects 1..n, a classic cross-reference table and a trailer, exactly the shape of the
    /// committed ASCII fixtures. Offsets are computed, never hand-written.
    /// </summary>
    public static byte[] Assemble(IReadOnlyList<byte[]> objects, string trailerExtra = "")
    {
        using var output = new MemoryStream();
        var offsets = new long[objects.Count];

        Write(output, "%PDF-1.4\n");
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i] = output.Position;
            Write(output, string.Format(CultureInfo.InvariantCulture, "{0} 0 obj\n", i + 1));
            output.Write(objects[i]);
            Write(output, "\nendobj\n");
        }

        var xref = output.Position;
        Write(output, string.Format(CultureInfo.InvariantCulture, "xref\n0 {0}\n0000000000 65535 f \n", objects.Count + 1));
        foreach (var offset in offsets)
        {
            Write(output, string.Format(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset));
        }

        Write(output, string.Format(
            CultureInfo.InvariantCulture,
            "trailer\n<< /Size {0} /Root 1 0 R {1}>>\nstartxref\n{2}\n%%EOF\n",
            objects.Count + 1,
            trailerExtra.Length == 0 ? string.Empty : trailerExtra + " ",
            xref));

        return output.ToArray();
    }

    private static byte[] MinimalContent() =>
        Ascii("BT\n/F1 12 Tf\n14 TL\n72 720 Td\n(Hello from a minimal PDF.) Tj T*\n(A second line of the same paragraph.) Tj T*\nET\n");

    private static byte[] Stream(byte[] body, string extraEntries)
    {
        using var output = new MemoryStream();
        Write(output, string.Format(
            CultureInfo.InvariantCulture,
            "<< /Length {0} {1}>>\nstream\n",
            body.Length,
            extraEntries.Length == 0 ? string.Empty : extraEntries + " "));
        output.Write(body);
        Write(output, "\nendstream");
        return output.ToArray();
    }

    private static byte[] PadPassword(string password, ReadOnlySpan<byte> pad)
    {
        var bytes = Latin1.GetBytes(password);
        var padded = new byte[32];
        var take = Math.Min(bytes.Length, 32);
        bytes.AsSpan(0, take).CopyTo(padded);
        pad[..(32 - take)].CopyTo(padded.AsSpan(take));
        return padded;
    }

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            s[i] = (byte)i;
        }

        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var output = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            output[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 0xFF]);
        }

        return output;
    }

    private static byte[] Ascii(string text) => Latin1.GetBytes(text);

    private static void Write(Stream stream, string text) => stream.Write(Ascii(text));
}
