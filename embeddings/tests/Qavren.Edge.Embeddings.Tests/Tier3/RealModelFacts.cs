using Microsoft.Extensions.AI;
using Qavren.Edge.Embeddings.Onnx;
using Xunit;

namespace Qavren.Edge.Embeddings.Tests.Tier3;

/// <summary>
/// Tier 3. The real int8 MiniLM, on a nightly schedule and never on a PR. Every fact is gated on
/// <see cref="ModelAvailable.Yes"/>, which is evaluated at runtime - so the container, the session
/// and the 23 MB graph are all built inside the test body and a skipped run costs a lane nothing.
/// </summary>
public sealed class RealModelFacts
{
    // No constructor. Nothing is loaded until a test body runs - see ModelAvailable's remarks.

    /// <summary>The digest the generated manifest pins for <c>onnx/model_qint8_arm64.onnx</c>.</summary>
    private const string GraphSha256 =
        "4278337fd0ff3c68bfb6291042cad8ab363e1d9fbc43dcb499fe91c871902474";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact(
        Skip = "QAVREN_EDGE_MODEL_DIR not set",
        SkipUnless = nameof(ModelAvailable.Yes),
        SkipType = typeof(ModelAvailable))]
    public async Task EmbeddingsMatchThePinnedReferenceVectors()
    {
        await using var host = Tier3Host.Build();          // builds the container HERE, not in a ctor
        var generator = host.Generator();

        if (ReferenceVectors.WriteRequested)
        {
            await RegenerateTheBaselineAsync(generator);
            return;
        }

        var reference = ReferenceVectors.Load();           // the committed JSON, beside the assembly

        var produced = await generator.GenerateAsync(reference.Select(r => r.Text), cancellationToken: Token);

        Assert.Equal(reference.Count, produced.Count);
        for (var i = 0; i < reference.Count; i++)
        {
            var cosine = Cosine(reference[i].Vector, produced[i].Vector.Span);

            // 1e-3, and NOT exact equality. ORT CPU bit-determinism across windows-2025 /
            // ubuntu-24.04 / macos-15 and across x64/arm64 is not established, so an exact
            // comparison would be a flake generator rather than a regression detector.
            Assert.True(
                1.0 - cosine < 1e-3,
                $"'{reference[i].Text}': cosine {cosine:F6} against the pinned reference");
        }
    }

    [Fact(
        Skip = "QAVREN_EDGE_MODEL_DIR not set",
        SkipUnless = nameof(ModelAvailable.Yes),
        SkipType = typeof(ModelAvailable))]
    public async Task TheRealGraphsSignatureIsWhatThePresetClaims()
    {
        await using var host = Tier3Host.Build();
        var generator = host.Generator();

        // Info reports the LAST session and the CURRENT tokenizer, both of which are null until
        // something has embedded: VocabularySize reads 0 and GraphSha256 reads empty on a generator
        // that has never run. One short embed is what makes the rest of this test a statement about
        // the real graph rather than about a default-constructed record.
        _ = await generator.GenerateAsync(["a roof leak after the storm"], cancellationToken: Token);

        var info = generator.GetService(typeof(OnnxEmbeddingGeneratorInfo)) as OnnxEmbeddingGeneratorInfo;

        Assert.NotNull(info);
        Assert.Equal(384, info.Dimensions);
        Assert.Equal(EmbeddingPooling.Mean, info.Pooling);
        Assert.Equal(30_522, info.VocabularySize);

        // The provisioned graph's digest is the one the generated manifest pins. If these ever
        // disagree, the cache was poisoned or the revision moved - the two failures the content-
        // addressed cache key exists to make loud.
        Assert.Equal(GraphSha256, info.GraphSha256);
    }

    [Fact(
        Skip = "QAVREN_EDGE_MODEL_DIR not set",
        SkipUnless = nameof(ModelAvailable.Yes),
        SkipType = typeof(ModelAvailable))]
    public async Task NormalisedEmbeddingsAreUnitLength()
    {
        await using var host = Tier3Host.Build();
        var generator = host.Generator();

        var produced = await generator.GenerateAsync(["a roof leak after the storm"], cancellationToken: Token);

        var lengthSquared = 0f;
        foreach (var x in produced[0].Vector.Span)
        {
            lengthSquared += x * x;
        }

        Assert.Equal(1f, lengthSquared, 4);
    }

    /// <summary>
    /// The <c>QAVREN_EDGE_WRITE_REFERENCE</c> path: produce the twelve vectors and write them into
    /// the source folder, refusing to overwrite an existing baseline.
    /// </summary>
    /// <param name="generator">The real generator.</param>
    /// <returns>A task that completes once the file is on disk.</returns>
    private static async Task RegenerateTheBaselineAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        var texts = ReferenceVectors.Texts;
        var produced = await generator.GenerateAsync(texts, cancellationToken: Token)
            .ConfigureAwait(false);

        Assert.Equal(texts.Count, produced.Count);

        var info = generator.GetService(typeof(OnnxEmbeddingGeneratorInfo)) as OnnxEmbeddingGeneratorInfo;
        Assert.NotNull(info);
        Assert.Equal(GraphSha256, info.GraphSha256);

        var vectors = new List<ReferenceVector>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            vectors.Add(new ReferenceVector(texts[i], produced[i].Vector.ToArray()));
        }

        var path = ReferenceVectors.Write(vectors, info.GraphSha256);
        TestContext.Current.TestOutputHelper?.WriteLine($"Wrote the baseline to '{path}'.");
    }

    private static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        Assert.Equal(a.Length, b.Length);

        double dot = 0, left = 0, right = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            left += (double)a[i] * a[i];
            right += (double)b[i] * b[i];
        }

        var magnitude = Math.Sqrt(left) * Math.Sqrt(right);
        return magnitude == 0 ? 0 : dot / magnitude;
    }
}
