using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Qavren.Edge.Embeddings.Onnx;

namespace Qavren.Edge.Embeddings.Tests.Tier3;

/// <summary>One pinned sentence and the vector a known-good run produced for it.</summary>
/// <param name="Text">The sentence.</param>
/// <param name="Vector">Its 384 floats, at shortest round-trip precision.</param>
internal sealed record ReferenceVector(string Text, float[] Vector);

/// <summary>
/// The committed baseline: twelve short sentences and their vectors, generated once from a
/// known-good run of the pinned int8 MiniLM and read back beside the assembly.
/// </summary>
/// <remarks>
/// Read and written with <c>Utf8JsonWriter</c>/<c>JsonDocument</c> rather than
/// <c>JsonSerializer</c>: no reflection, no source generator, nothing for the trimmer to guess at,
/// and the file stays a shape a human can read in a diff.
/// </remarks>
internal static class ReferenceVectors
{
    /// <summary>The file name, in the source folder and beside the assembly alike.</summary>
    public const string FileName = "reference-vectors.json";

    /// <summary>
    /// The twelve sentences. Short, domestic, and deliberately the seed set Task 7.2's Search page
    /// uses, so a human can put the sample app and this baseline side by side.
    /// </summary>
    public static IReadOnlyList<string> Texts { get; } =
    [
        "water is coming through the roof",
        "a roof leak after the storm",
        "the gutter is blocked with leaves",
        "the front tyres are worn",
        "the boiler stopped heating water",
        "a cracked window in the back bedroom",
        "the kitchen tap drips all night",
        "the fence blew down in the wind",
        "damp is spreading up the hallway wall",
        "the garage door will not close",
        "a burst pipe under the sink",
        "the smoke alarm keeps chirping",
    ];

    /// <summary>
    /// Whether this run regenerates the baseline instead of asserting against it. Set by the
    /// documented one-off command, never by CI.
    /// </summary>
    public static bool WriteRequested =>
        Environment.GetEnvironmentVariable("QAVREN_EDGE_WRITE_REFERENCE") is { Length: > 0 } value
        && !string.Equals(value, "0", StringComparison.Ordinal);

    /// <summary>The copy beside the assembly, put there by <c>CopyToOutputDirectory</c>.</summary>
    public static string OutputPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "Tier3", FileName);

    /// <summary>Reads the committed baseline.</summary>
    /// <returns>The twelve pinned vectors, in file order.</returns>
    public static IReadOnlyList<ReferenceVector> Load()
    {
        if (!File.Exists(OutputPath))
        {
            throw new FileNotFoundException(
                $"'{OutputPath}' is missing. It is committed beside the sources and copied to the " +
                "test output; a regeneration is a PR of its own (see Tier3/README.md).",
                OutputPath);
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(OutputPath));
        var root = document.RootElement;

        var preset = root.GetProperty("preset").GetString();
        if (!string.Equals(preset, EmbeddingPresets.MiniLmL6V2Int8.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The baseline was generated for preset '{preset}', not " +
                $"'{EmbeddingPresets.MiniLmL6V2Int8.Id}'.");
        }

        var dimensions = root.GetProperty("dimensions").GetInt32();
        var vectors = new List<ReferenceVector>();

        foreach (var entry in root.GetProperty("vectors").EnumerateArray())
        {
            var floats = new float[dimensions];
            var index = 0;

            foreach (var component in entry.GetProperty("vector").EnumerateArray())
            {
                floats[index++] = component.GetSingle();
            }

            if (index != dimensions)
            {
                throw new InvalidOperationException(
                    $"A baseline vector carries {index} components, not {dimensions}.");
            }

            vectors.Add(new ReferenceVector(entry.GetProperty("text").GetString()!, floats));
        }

        return vectors;
    }

    /// <summary>
    /// Writes the baseline into the SOURCE folder, and refuses to overwrite one that already
    /// exists. A regeneration that silently overwrote the baseline would turn a real regression
    /// into a green run, which is the one way a pinned-baseline test can be worse than no test.
    /// </summary>
    /// <param name="vectors">The vectors a known-good run produced.</param>
    /// <param name="graphSha256">The digest of the graph that produced them.</param>
    /// <returns>The path written.</returns>
    public static string Write(IReadOnlyList<ReferenceVector> vectors, string graphSha256)
    {
        ArgumentNullException.ThrowIfNull(vectors);

        var path = SourcePath();
        if (File.Exists(path))
        {
            throw new InvalidOperationException(
                $"'{path}' already exists and QAVREN_EDGE_WRITE_REFERENCE refuses to overwrite it. " +
                "Regenerating the baseline is a PR of its own: delete the file deliberately, with " +
                "the reason in the PR body. See Tier3/README.md.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteString("preset", EmbeddingPresets.MiniLmL6V2Int8.Id);
        writer.WriteString("modelId", EmbeddingPresets.MiniLmL6V2Int8.Manifest.ModelId);
        writer.WriteString("revision", EmbeddingPresets.MiniLmL6V2Int8.Manifest.HuggingFaceRevision);
        writer.WriteString("graphSha256", graphSha256);
        writer.WriteNumber("dimensions", EmbeddingPresets.MiniLmL6V2Int8.Dimensions);
        writer.WriteString(
            "generatedUtc",
            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        writer.WriteStartArray("vectors");
        foreach (var vector in vectors)
        {
            writer.WriteStartObject();
            writer.WriteString("text", vector.Text);
            writer.WriteStartArray("vector");
            foreach (var component in vector.Vector)
            {
                writer.WriteNumberValue(component);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();

        return path;
    }

    /// <summary>
    /// The source folder's copy, resolved from this file's own compile-time path. The write path is
    /// a developer's one-off on the box that holds the sources; CI never takes it, and a deterministic
    /// CI build's mapped paths therefore never matter here.
    /// </summary>
    /// <param name="thisFile">Supplied by the compiler.</param>
    /// <returns>The source path.</returns>
    private static string SourcePath([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, FileName);
}
