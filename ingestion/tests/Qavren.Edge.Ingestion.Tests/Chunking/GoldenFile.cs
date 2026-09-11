using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Qavren.Edge.Ingestion.Tests.Chunking;

/// <summary>
/// The on-disk shape of a golden: one JSON array holding
/// <c>{ index, startChar, endChar, tokenCount, headingPath, breadcrumb, text, embedText }</c> per
/// chunk — FULL text, because the fixtures are small and a moved boundary should be legible in the
/// diff rather than a changed hash.
/// </summary>
/// <remarks>
/// <c>breadcrumb</c> and <c>embedText</c> are a declared SUPERSET of the six fields plan Task 4.1
/// Step 6 lists. Without them a <c>.no-breadcrumb</c> golden is byte-identical to its <c>auto</c>
/// counterpart whenever no boundary moves — <c>PrependHeadingPath</c> changes the EMBED text and
/// nothing else — so goldens 15 and 16 would record none of what their row of that table claims.
/// The six pinned fields are all still written, in the pinned order.
/// </remarks>
internal static class GoldenFile
{
    private const string Prefix = "fixtures/golden/";

    private static readonly Assembly Owner = typeof(GoldenFile).Assembly;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        NewLine = "\n",
    };

    /// <summary>
    /// Writes one golden, and ONLY when both gates open: <c>QAVREN_EDGE_WRITE_GOLDEN=1</c> is set,
    /// and no file is there already. Returns whether it wrote. Deleting a golden is the deliberate
    /// act; regeneration is its own PR with the reason in the body.
    /// </summary>
    internal static bool TryWrite(string directory, string golden, string content)
    {
        if (Environment.GetEnvironmentVariable("QAVREN_EDGE_WRITE_GOLDEN") != "1")
        {
            return false;
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, golden);
        if (File.Exists(path))
        {
            return false;
        }

        File.WriteAllText(path, content, new UTF8Encoding(false));
        return true;
    }

    /// <summary>The golden text embedded in this assembly, or null when the file is not committed yet.</summary>
    internal static string? Embedded(string golden)
    {
        foreach (var name in Owner.GetManifestResourceNames())
        {
            var normalised = name.Replace('\\', '/');
            if (!normalised.StartsWith(Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(normalised[Prefix.Length..], golden, StringComparison.Ordinal))
            {
                using var stream = Owner.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                return reader.ReadToEnd();
            }
        }

        return null;
    }

    /// <summary>Render drafts to the committed JSON shape.</summary>
    internal static string Render(IReadOnlyList<ChunkDraft> drafts)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartArray();
            foreach (var draft in drafts)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", draft.Ordinal);
                writer.WriteNumber("startChar", draft.CharStart);
                writer.WriteNumber("endChar", draft.CharEnd);
                writer.WriteNumber("tokenCount", draft.TokenCount);
                writer.WriteStartArray("headingPath");
                foreach (var heading in draft.HeadingPath)
                {
                    writer.WriteStringValue(heading);
                }

                writer.WriteEndArray();
                if (draft.Breadcrumb is null)
                {
                    writer.WriteNull("breadcrumb");
                }
                else
                {
                    writer.WriteString("breadcrumb", draft.Breadcrumb);
                }

                writer.WriteString("text", draft.Text);
                writer.WriteString("embedText", draft.EmbedText);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return new UTF8Encoding(false).GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>
    /// Re-render committed JSON through the same writer, so a comparison is structural: a line
    /// ending or an indent that an editor changed can never fail a golden, and a moved boundary
    /// always does.
    /// </summary>
    internal static string Normalise(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            document.RootElement.WriteTo(writer);
        }

        return new UTF8Encoding(false).GetString(buffer.ToArray()) + "\n";
    }
}
