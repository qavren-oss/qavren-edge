using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Qavren.Edge.Diagnostics;

namespace Qavren.Edge.Embeddings.Onnx.Internal;

/// <summary>
/// Spec 14.3's embeddings block. Everything it reports is either an options value or a counter
/// that already exists: <c>vocabSize</c> comes from <see cref="EdgeTokenizerProvider.Find"/> for
/// THIS registration's preset - never <see cref="IEdgeTokenizerProvider.Current"/>, which is the
/// most recently built tokenizer and belongs to whichever preset embedded last - and is null until
/// that preset's first embed or warm-up, so producing this report never forces provisioning.
/// </summary>
/// <remarks>
/// The generator is resolved lazily out of <see cref="IServiceProvider"/> for the same reason the
/// ONNX contributor resolves its session host that way: the generator's construction path reaches
/// the session host, which depends on <c>IEdgeHost</c>, and a constructor dependency here would
/// risk a cycle the moment anything on the host's own construction path asked for diagnostics.
/// <para>
/// <b>The options arrive as the instance <c>AddOnnxEmbeddings</c> built, not as
/// <c>IOptions&lt;OnnxEmbeddingOptions&gt;</c> from the container.</b> A keyed registration
/// registers its options accessor UNDER ITS KEY, so an unkeyed <c>IOptions</c> dependency resolves
/// to a default-constructed instance and the block would report the default preset - 384
/// dimensions, mean pooling - for an app actually running bge or nomic. One contributor is
/// registered per <c>AddOnnxEmbeddings</c> call, each closed over its own options and key.
/// </para>
/// </remarks>
internal sealed class EmbeddingsDiagnosticsContributor(
    IOptions<OnnxEmbeddingOptions> options,
    string? name,
    EdgeTokenizerProvider tokenizers,
    IServiceProvider services) : IEdgeDiagnosticsContributor
{
    /// <summary>
    /// Spec 14.3's name, the bare literal, for every registration keyed or not. A keyed
    /// registration is told apart by the <c>serviceKey</c> detail below rather than by a decorated
    /// component name: 14.3 names this block with one literal, and a report consumer that matches
    /// on the name would stop finding the block the moment an app moved to a keyed registration.
    /// </summary>
    public string ComponentName => "Qavren.Edge.Embeddings.Onnx";

    /// <inheritdoc />
    public string? ComponentVersion
        => typeof(EmbeddingsDiagnosticsContributor).Assembly.GetName().Version?.ToString();

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string?> Describe()
    {
        var value = options.Value;
        var preset = value.Preset;
        var graph = Find(preset.Manifest, preset.ModelFile);

        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Null for the unkeyed registration. This, not the component name, is what tells two
            // blocks apart in an app that calls AddOnnxEmbeddings twice.
            ["serviceKey"] = name,
            ["preset"] = preset.Id,
            ["presetLicense"] = preset.Manifest.SpdxLicense,
            ["modelFile"] = preset.ModelFile,
            ["modelSha256"] = graph,
            ["dimensions"] = preset.Dimensions.ToString(CultureInfo.InvariantCulture),
            ["pooling"] = preset.Pooling.ToString(),
            ["normalize"] = preset.Normalize.ToString(CultureInfo.InvariantCulture),
            ["postPoolLayerNorm"] = preset.PostPoolLayerNorm.ToString(CultureInfo.InvariantCulture),
            ["maxSequenceLength"] = preset.MaxSequenceLength.ToString(CultureInfo.InvariantCulture),
            ["sequenceBuckets"] = string.Join(", ", preset.SequenceBuckets),
            ["queryPrefix"] = preset.QueryPrefix,
            ["documentPrefix"] = preset.DocumentPrefix,
            ["tokenizerKind"] = preset.TokenizerKind.ToString(),
            ["tokenizerFile"] = preset.TokenizerFile,
            // This registration's preset, not whichever preset happened to embed first.
            ["vocabSize"] = tokenizers.Find(preset.Id)?.VocabularySize.ToString(CultureInfo.InvariantCulture),
            ["pinnedSequenceLength"] = value.DescribePinning(),
            ["maxBatchSize"] = value.MaxBatchSize.ToString(CultureInfo.InvariantCulture),
            ["maxConcurrency"] = value.MaxConcurrency.ToString(CultureInfo.InvariantCulture),
            ["truncation"] = value.Truncation.ToString(),
            ["defaultInputKind"] = value.DefaultInputKind.ToString(),
        };

        var registered = name is null
            ? services.GetService<IEmbeddingGenerator<string, Embedding<float>>>()
            : services.GetKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(name);

        var generator = registered?.GetService(typeof(OnnxEmbeddingGenerator)) as OnnxEmbeddingGenerator;

        // Spec 14.3 lists embeddingsGenerated, batchesRun, tokensEncoded, truncatedInputs, runMsP50
        // and runMsP95 as MEMBERS of this block, so every one of the six is present on every path.
        // An absent key and a null key are not the same thing to a report consumer: absent reads as
        // "this build does not report it", null reads as "nothing has happened yet", and only the
        // second is true when the generator has not been resolved.
        var snapshot = generator?.Snapshot();

        details["effectiveBatchSize"] =
            (snapshot?.EffectiveBatchSize ?? value.MaxBatchSize).ToString(CultureInfo.InvariantCulture);
        details["embeddingsGenerated"] = snapshot?.EmbeddingsGenerated.ToString(CultureInfo.InvariantCulture);
        details["batchesRun"] = snapshot?.BatchesRun.ToString(CultureInfo.InvariantCulture);
        details["tokensEncoded"] = snapshot?.TokensEncoded.ToString(CultureInfo.InvariantCulture);
        details["truncatedInputs"] = snapshot?.TruncatedInputs.ToString(CultureInfo.InvariantCulture);
        details["runMsP50"] = snapshot?.RunMsP50?.ToString("F1", CultureInfo.InvariantCulture);
        details["runMsP95"] = snapshot?.RunMsP95?.ToString("F1", CultureInfo.InvariantCulture);

        return details;
    }

    private static string? Find(Qavren.Edge.Onnx.OnnxModelManifest manifest, string relativePath)
    {
        for (var i = 0; i < manifest.Files.Count; i++)
        {
            if (string.Equals(manifest.Files[i].RelativePath, relativePath, StringComparison.Ordinal))
            {
                return manifest.Files[i].Sha256;
            }
        }

        return null;
    }
}
