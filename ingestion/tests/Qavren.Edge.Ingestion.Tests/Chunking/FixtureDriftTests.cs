using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qavren.Edge.Ingestion.Tests.Extraction;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Chunking;

/// <summary>
/// Task 1.3's manifest check, re-run from the EMBEDDED resources so the guard holds on a device as
/// well as on the host: every entry's byte length and SHA-256 match <c>manifest.json</c>, and the
/// twelve text and Markdown entries match the literal table the plan pins.
/// </summary>
/// <remarks>
/// A BOM or an eol flip fails here, loudly, rather than confusingly at chunk 7 of a golden.
/// </remarks>
public sealed class FixtureDriftTests
{
    /// <summary>
    /// The twelve text and Markdown fixtures, byte length and SHA-256, exactly as plan Task 1.3
    /// Step 5 writes them. Nothing computes these; they are the pinned values.
    /// </summary>
    private static readonly (string Path, int Bytes, string Sha256)[] Pinned =
    [
        ("corpus/text/empty.txt", 0, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"),
        ("corpus/text/whitespace-only.txt", 7, "3018a85eec1be9ff91c1060df01a9c0474bf9faf334bf9f363a7649b82a16fe4"),
        ("corpus/text/three-paragraphs.txt", 190, "9469d9507868eaf0a931eb7fde7aeda5bc34cbb251c92f2c89c03a0b6bed9e99"),
        ("corpus/text/crlf-and-lone-cr.txt", 71, "8f452179905c47b2baa6a17f3709e2c008bea9a4f591f24c355362a852e37ec9"),
        ("corpus/text/long-token.txt", 5000, "260679791fa8da4dddc6aa3b243c514025e83d3a2f60800b9734b990be5d11a0"),
        ("corpus/text/unicode.txt", 136, "e6b237d83b94fd4add1b7389d3e188436791b27cc351f8ae970ee7460e3498dc"),
        ("corpus/text/bom.txt", 74, "c1ea8c926fadbbabcbac1cd47ef9f6baeeca3595a612ff0abe62e04750d029d6"),
        ("corpus/markdown/headings.md", 402, "0bbb67d71f47465d3daaf4053b831736d2c16efa2a59a0887f808f5bdc048bdd"),
        ("corpus/markdown/fences.md", 248, "4a1099e8a352413935074d858ca6b8ca1971495701df344fec74cbe2dff071d2"),
        ("corpus/markdown/tables-lists.md", 190, "41724421125458cfb0422f87602881b664368e66c6856bd125f7715ad1e61e70"),
        ("corpus/markdown/raw-html.md", 163, "a5be2e6cc68aa802d0e99dcab9015538315d10e99e789cde24245c4d69dadaf9"),
        ("corpus/markdown/giant-heading-section.md", 3866, "e706d8fcc5701cb7d3a6b0bbf5c5252eab80331fa86429a19b5dd02f93be6a37"),
    ];

    [Fact]
    public void Every_manifest_entry_matches_the_embedded_bytes()
    {
        var entries = Manifest();
        Assert.NotEmpty(entries);

        foreach (var (path, bytes, sha256) in entries)
        {
            var content = FixtureCorpus.Bytes(path["corpus/".Length..]);
            Assert.Equal(bytes, content.Length);
            Assert.Equal(sha256, Hex(content));
        }
    }

    [Fact]
    public void The_twelve_text_and_markdown_fixtures_match_the_pinned_table()
    {
        foreach (var (path, bytes, sha256) in Pinned)
        {
            var content = FixtureCorpus.Bytes(path["corpus/".Length..]);
            Assert.Equal(bytes, content.Length);
            Assert.Equal(sha256, Hex(content));
        }
    }

    [Fact]
    public void The_manifest_covers_the_pinned_table()
    {
        var manifest = Manifest().ToDictionary(e => e.Path, e => (e.Bytes, e.Sha256), StringComparer.Ordinal);

        foreach (var (path, bytes, sha256) in Pinned)
        {
            Assert.True(manifest.TryGetValue(path, out var entry), $"manifest.json has no entry for '{path}'.");
            Assert.Equal(bytes, entry.Bytes);
            Assert.Equal(sha256, entry.Sha256);
        }
    }

    private static string Hex(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static List<(string Path, int Bytes, string Sha256)> Manifest()
    {
        var assembly = typeof(FixtureDriftTests).Assembly;
        var name = assembly.GetManifestResourceNames()
            .First(n => n.Replace('\\', '/').EndsWith("fixtures/manifest.json", StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        using var document = JsonDocument.Parse(reader.ReadToEnd());

        var entries = new List<(string, int, string)>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            entries.Add((
                element.GetProperty("path").GetString()!,
                element.GetProperty("bytes").GetInt32(),
                element.GetProperty("sha256").GetString()!));
        }

        return entries;
    }
}
