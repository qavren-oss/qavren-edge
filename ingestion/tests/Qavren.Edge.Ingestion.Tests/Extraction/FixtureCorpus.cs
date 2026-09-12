using System.Reflection;
using System.Text;

namespace Qavren.Edge.Ingestion.Tests.Extraction;

/// <summary>
/// The committed corpus, reached as embedded resources rather than as files: the device lanes load
/// this same assembly and their filesystem layout is not the host's (spec 14.6).
/// </summary>
/// <remarks>
/// MSBuild expands <c>%(RecursiveDir)</c> with the platform separator, so a logical name can carry
/// a backslash on Windows and a forward slash on Linux. Lookup normalises both sides.
/// </remarks>
internal static class FixtureCorpus
{
    private const string Prefix = "fixtures/corpus/";

    private static readonly Assembly Owner = typeof(FixtureCorpus).Assembly;

    private static readonly Dictionary<string, string> Names = Build();

    /// <summary>Raw bytes, exactly as committed.</summary>
    public static byte[] Bytes(string relativePath)
    {
        var key = Normalise(relativePath);
        if (!Names.TryGetValue(key, out var resource))
        {
            throw new InvalidOperationException(
                $"Fixture '{relativePath}' is not embedded. Embedded: " +
                string.Join(", ", Names.Keys.Order(StringComparer.Ordinal)));
        }

        using var stream = Owner.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Resource '{resource}' has no stream.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>The UTF-8 decode of the raw bytes, BOM included when the file has one.</summary>
    public static string Utf8(string relativePath) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(Bytes(relativePath));

    /// <summary>A source item over one fixture, opened from memory and re-openable.</summary>
    public static DocumentSourceItem Item(string relativePath)
    {
        var bytes = Bytes(relativePath);
        var name = relativePath[(relativePath.LastIndexOf('/') + 1)..];

        return new DocumentSourceItem(
            name,
            IngestionMediaTypes.FromExtension(name),
            _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)),
            Path: relativePath,
            SizeBytes: bytes.Length);
    }

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Owner.GetManifestResourceNames())
        {
            var normalised = Normalise(name);
            if (normalised.StartsWith(Prefix, StringComparison.Ordinal))
            {
                map[normalised[Prefix.Length..]] = name;
            }
        }

        return map;
    }

    private static string Normalise(string path) => path.Replace('\\', '/');
}
