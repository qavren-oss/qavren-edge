using System.Globalization;
using System.Security.Cryptography;
using Qavren.Edge.Embeddings.Onnx;

namespace Qavren.Edge.Ingestion.Onnx.Tests;

/// <summary>
/// The digest-checked <c>bert-base-uncased</c> vocabulary, resolved in the order plan
/// "Environment ground truth" pins: <c>%QAVREN_EDGE_VOCAB%</c>, then
/// <c>%QAVREN_EDGE_MODEL_DIR%\vocab.txt</c>, then SP2's model cache. <see cref="Available"/> is the
/// <c>SkipUnless</c> gate; a present file whose SHA-256 is not the pinned one FAILS rather than
/// skips, because equality between two tokenizers over an unverified vocabulary proves nothing.
/// </summary>
public static class Vocabulary
{
    /// <summary>The digest CI's <c>model-tests</c> job and the core's chunking harness pin.</summary>
    public const string Sha256 = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3";

    /// <summary>The MiniLM ceiling every comparison here runs at.</summary>
    public const int MaxSequenceLength = 256;

    /// <summary>The printed reason when this lane has no vocabulary, with the re-provisioning hint.</summary>
    public const string SkipReason =
        "No bert-base-uncased vocab.txt on this lane. Set QAVREN_EDGE_VOCAB or QAVREN_EDGE_MODEL_DIR, " +
        "or fetch it once: sentence-transformers/all-MiniLM-L6-v2 @ 1110a243fdf4706b3f48f1d95db1a4f5529b4d41 vocab.txt.";

    private static readonly Lazy<IEdgeTokenizer> LazyEdge = new(CreateEdge);
    private static readonly Lazy<IChunkTokenizer> LazyMl = new(CreateMl);

    /// <summary>The resolved path, or null when this lane has none.</summary>
    public static string? Path { get; } = Resolve();

    /// <summary>The <c>SkipUnless</c> property. Evaluated at runtime, touches nothing but the filesystem.</summary>
    public static bool Available => Path is not null;

    /// <summary>SP2's tokenizer over the real vocabulary, at the MiniLM ceiling, lower-cased.</summary>
    public static IEdgeTokenizer Edge => LazyEdge.Value;

    /// <summary>The core's ONNX-free tokenizer over the SAME vocabulary and settings.</summary>
    public static IChunkTokenizer Ml => LazyMl.Value;

    private static IEdgeTokenizer CreateEdge() =>
        EdgeTokenizer.CreateWordPiece(
            Verified(),
            new WordPieceTokenizerOptions { LowerCase = true, MaxSequenceLength = MaxSequenceLength });

    private static IChunkTokenizer CreateMl() =>
        EdgeTokenCounter.CreateWordPiece(Verified(), MaxSequenceLength, lowerCase: true);

    private static string Verified()
    {
        var path = Path ?? throw new InvalidOperationException("Gate on Vocabulary.Available first.");
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        if (!string.Equals(actual, Sha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "vocab.txt at '{0}' hashes to {1}, not the pinned {2}. Refusing to compare tokenizers over an unverified vocabulary.",
                path,
                actual,
                Sha256));
        }

        return path;
    }

    private static string? Resolve()
    {
        var explicitPath = Environment.GetEnvironmentVariable("QAVREN_EDGE_VOCAB");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var modelDir = Environment.GetEnvironmentVariable("QAVREN_EDGE_MODEL_DIR");
        if (!string.IsNullOrWhiteSpace(modelDir))
        {
            var candidate = System.IO.Path.Combine(modelDir, "vocab.txt");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var cached = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp",
            "qedge-model",
            "vocab.txt");

        return File.Exists(cached) ? cached : null;
    }
}
