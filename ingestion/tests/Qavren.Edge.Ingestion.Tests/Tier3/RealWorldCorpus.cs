using System.Text.Json;

namespace Qavren.Edge.Ingestion.Tests.Tier3;

/// <summary>One entry of <c>ingestion/tests/fixtures/realworld-corpus.json</c>. A missing field reads as null.</summary>
internal sealed record RealWorldDocument(string? Id, string? Producer, string? Url, string? Sha256, string? Phrase);

/// <summary>
/// Spec 14.5's manifest (plan adjustment 26), read from the embedded copy so the same bytes reach
/// every lane. Parsed with <see cref="JsonDocument"/> rather than a reflection serializer: the
/// repo's only JSON path is source-generated, and a test that reflected over a record type would
/// be the one AOT-unsafe line in the assembly.
/// </summary>
internal static class RealWorldCorpus
{
    /// <summary>The printed reason while the manifest is empty. The owed item, visible on every run.</summary>
    public const string OwedReason =
        "spec 14.5 is owed: ingestion/tests/fixtures/realworld-corpus.json has no entries";

    /// <summary>The three producers spec 14.5 names, and the only values <c>producer</c> may take.</summary>
    public static readonly string[] Producers = ["word", "libreoffice", "acrobat"];

    private const string ResourceName = "fixtures/realworld-corpus.json";

    /// <summary>The raw manifest text.</summary>
    public static string Text()
    {
        var assembly = typeof(RealWorldCorpus).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => string.Equals(n.Replace('\\', '/'), ResourceName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"'{ResourceName}' is not embedded in {assembly.GetName().Name}.");

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Resource '{name}' has no stream.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The <c>documents</c> array, one record per element. Throws on a malformed document.</summary>
    public static IReadOnlyList<RealWorldDocument> Load()
    {
        using var json = JsonDocument.Parse(Text());
        var documents = json.RootElement.GetProperty("documents");
        if (documents.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("'documents' is not an array.");
        }

        var entries = new List<RealWorldDocument>();
        foreach (var element in documents.EnumerateArray())
        {
            entries.Add(new RealWorldDocument(
                Field(element, "id"),
                Field(element, "producer"),
                Field(element, "url"),
                Field(element, "sha256"),
                Field(element, "phrase")));
        }

        return entries;
    }

    private static string? Field(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
