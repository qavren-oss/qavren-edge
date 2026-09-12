using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Qavren.Edge.Ingestion.Tests.Extraction;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Chunking;

/// <summary>
/// Everything the chunking suites share: the digest-checked <c>bert-base-uncased</c> vocabulary,
/// the MiniLM model profile the twenty goldens are pinned against, and the extraction call that
/// turns a committed fixture into an <see cref="ExtractedDocument"/>.
/// </summary>
/// <remarks>
/// The vocabulary is resolved in the order plan "Environment ground truth" pins:
/// <c>%QAVREN_EDGE_VOCAB%</c>, then <c>%QAVREN_EDGE_MODEL_DIR%\vocab.txt</c>, then the SP2 model
/// cache. A missing vocabulary SKIPS with a printed reason — there is none on a phone and never
/// will be — but a vocabulary whose SHA-256 is not the pinned one FAILS, because a golden
/// generated against a substitute pins boundaries no shipped configuration produces.
/// </remarks>
internal static class ChunkingHarness
{
    /// <summary>The digest plan "Environment ground truth" and CI's <c>model-tests</c> job pin.</summary>
    internal const string VocabSha256 =
        "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3";

    /// <summary>The one model profile every committed golden is generated against.</summary>
    internal static ChunkModelProfile MiniLm { get; } =
        new("all-minilm-l6-v2-int8", Dimensions: 384, MaxSequenceLength: 256, Pooling: "mean");

    /// <summary>The resolved vocabulary, or null when this lane has none.</summary>
    internal static string? VocabPath { get; } = ResolveVocabulary();

    private static readonly Lazy<IChunkTokenizer> LazyTokenizer = new(CreateTokenizer);

    /// <summary>The shared tokenizer. Call <see cref="RequireVocabulary"/> first.</summary>
    internal static IChunkTokenizer Tokenizer => LazyTokenizer.Value;

    /// <summary>Skips the calling test, with the re-provisioning hint, when there is no vocabulary.</summary>
    internal static void RequireVocabulary()
    {
        Assert.SkipWhen(
            VocabPath is null,
            "No bert-base-uncased vocab.txt on this lane. Set QAVREN_EDGE_VOCAB or QAVREN_EDGE_MODEL_DIR, " +
            "or fetch it once: sentence-transformers/all-MiniLM-L6-v2 @ 1110a243fdf4706b3f48f1d95db1a4f5529b4d41 vocab.txt.");
    }

    /// <summary>The frozen budget for a variant. Defaults resolve to the MiniLM triple 222 / 32 / 27.</summary>
    internal static ResolvedChunkOptions Options(string variant)
    {
        var options = new ChunkOptions();
        switch (variant)
        {
            case GoldenCases.DefaultVariant:
                break;

            case GoldenCases.NoPreambleVariant:
                options.IncludePreamble = false;
                break;

            case GoldenCases.NoBreadcrumbVariant:
                options.PrependHeadingPath = false;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown golden variant.");
        }

        return options.Resolve(MiniLm, Tokenizer);
    }

    /// <summary>The extractor for a fixture, chosen by its extension, run over the embedded bytes.</summary>
    internal static async Task<ExtractedDocument> ExtractAsync(string fixtureRelativePath)
    {
        var item = FixtureCorpus.Item(fixtureRelativePath);
        var logger = new CapturingLogger();
        var context = TestHarness.Context(logger);

        IDocumentExtractor extractor =
            item.MediaType == IngestionMediaTypes.Markdown
                ? new MarkdownExtractor()
                : new PlainTextExtractor();

        return await extractor
            .ExtractAsync(item, context, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The repository's <c>ingestion/tests/fixtures</c> directory, for the golden WRITER only.</summary>
    /// <remarks>
    /// Derived from <see cref="CallerFilePathAttribute"/> rather than from
    /// <see cref="AppContext.BaseDirectory"/>, because every parallel-phase verify in this plan
    /// runs under <c>-p:ArtifactsPath=…</c>, which puts the binaries on a different drive from the
    /// repository. <c>QAVREN_EDGE_FIXTURES_DIR</c> overrides it.
    /// </remarks>
    internal static string FixturesDirectory([CallerFilePath] string? thisFile = null)
    {
        var overridden = Environment.GetEnvironmentVariable("QAVREN_EDGE_FIXTURES_DIR");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        var testsDirectory = Path.GetDirectoryName(Path.GetDirectoryName(thisFile!))!;
        return Path.Combine(Path.GetDirectoryName(testsDirectory)!, "fixtures");
    }

    private static IChunkTokenizer CreateTokenizer()
    {
        var path = VocabPath
            ?? throw new InvalidOperationException("RequireVocabulary() must run before Tokenizer.");

        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();

        if (!string.Equals(actual, VocabSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "vocab.txt at '{0}' hashes to {1}, not the pinned {2}. Refusing to chunk against an unverified vocabulary.",
                path,
                actual,
                VocabSha256));
        }

        return EdgeTokenCounter.CreateWordPiece(path, MiniLm.MaxSequenceLength, lowerCase: true);
    }

    private static string? ResolveVocabulary()
    {
        var explicitPath = Environment.GetEnvironmentVariable("QAVREN_EDGE_VOCAB");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var modelDir = Environment.GetEnvironmentVariable("QAVREN_EDGE_MODEL_DIR");
        if (!string.IsNullOrWhiteSpace(modelDir))
        {
            var candidate = Path.Combine(modelDir, "vocab.txt");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var cached = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp",
            "qedge-model",
            "vocab.txt");

        return File.Exists(cached) ? cached : null;
    }
}
