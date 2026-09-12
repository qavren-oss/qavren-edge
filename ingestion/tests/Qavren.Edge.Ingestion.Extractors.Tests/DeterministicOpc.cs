using System.IO.Compression;

namespace Qavren.Edge.Ingestion.Extractors.Tests;

/// <summary>
/// Assembles a committed part-XML tree into an OPC package <b>byte-identically across runs</b>
/// (spec 14.2). Three rules, and the third throws if you get it wrong: entries are added in a
/// declared, fixed order; <c>CreateEntry(name, CompressionLevel.Optimal)</c>; and
/// <c>LastWriteTime = 1980-01-01T00:00:00Z</c> is set BETWEEN <c>CreateEntry</c> and
/// <c>Open()</c> — setting it afterwards throws in Create mode.
/// </summary>
internal static class DeterministicOpc
{
    private static readonly DateTimeOffset Epoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The fixed leading order; everything else follows ordinal-sorted.</summary>
    private static readonly string[] Leading = ["[Content_Types].xml", "_rels/.rels", "word/document.xml"];

    /// <summary>The parts of one committed tree under <c>corpus/docx/</c>, in package order.</summary>
    public static IReadOnlyList<(string Name, byte[] Content)> Parts(string tree)
    {
        var prefix = $"docx/{tree}/";
        var names = FixtureCorpus.Under(prefix).Select(k => k[prefix.Length..]).ToList();
        if (names.Count == 0)
        {
            throw new InvalidOperationException($"No committed parts under corpus/{prefix}.");
        }

        var ordered = new List<string>();
        foreach (var lead in Leading)
        {
            if (names.Remove(lead))
            {
                ordered.Add(lead);
            }
        }

        ordered.AddRange(names.Order(StringComparer.Ordinal));
        return [.. ordered.Select(name => (name, FixtureCorpus.Bytes(prefix + name)))];
    }

    /// <summary>One committed tree, zipped.</summary>
    public static byte[] Build(string tree) => Build(Parts(tree));

    /// <summary>Arbitrary parts, zipped in the order given.</summary>
    public static byte[] Build(IReadOnlyList<(string Name, byte[] Content)> parts)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in parts)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = Epoch;
                using var stream = entry.Open();
                stream.Write(content);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>A re-openable in-memory source item over one committed tree.</summary>
    public static DocumentSourceItem Item(string tree) => FixtureCorpus.Item($"{tree}.docx", Build(tree), $"docx/{tree}");
}
