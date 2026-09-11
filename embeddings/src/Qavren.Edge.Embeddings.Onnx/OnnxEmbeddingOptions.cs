using System.Globalization;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Embeddings.Onnx;

/// <summary>Everything <c>AddOnnxEmbeddings</c> configures. Every default here changes behaviour.</summary>
public sealed class OnnxEmbeddingOptions
{
    /// <summary>The preset. Defaults to <see cref="EmbeddingPresets.MiniLmL6V2Int8"/>.</summary>
    public EmbeddingPreset Preset { get; set; } = EmbeddingPresets.MiniLmL6V2Int8;

    /// <summary>Null uses the HTTP model source over the preset's manifest.</summary>
    public IOnnxModelSource? ModelSource { get; set; }

    /// <summary>How many inputs one ORT <c>Run</c> carries. 16 by default.</summary>
    public int MaxBatchSize { get; set; } = 16;

    /// <summary>
    /// Null (the default) uses <see cref="EmbeddingPreset.SequenceBuckets"/>: the graph keeps its
    /// declared symbolic <c>sequence_length</c>, CoreML partitions normally, and each batch is
    /// padded to the smallest bucket that fits it.
    /// <para>
    /// Set to one of the buckets to pin the shape instead. That emits
    /// <c>AddFreeDimensionOverrideByName("sequence_length", N)</c> and
    /// <c>("batch_size", MaxBatchSize)</c> into
    /// <see cref="OnnxSessionOptions.FreeDimensionOverrides"/> and turns
    /// <c>CoreMlProviderOptions.RequireStaticInputShapes</c> on. One pinned shape means one
    /// session and one CoreML compile, and every batch - including a batch of one short string -
    /// pays a full N-token run. It is a measured trade, not a default.
    /// </para>
    /// </summary>
    public int? PinnedSequenceLength { get; set; }

    /// <summary>
    /// Concurrent ORT Runs. 1 by default: ORT already parallelises intra-op, and a second
    /// inference doubles peak native memory on a phone.
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>What happens to an over-long input. Truncate-and-log by default.</summary>
    public EmbeddingTruncation Truncation { get; set; } = EmbeddingTruncation.Truncate;

    /// <summary>
    /// Which prefix the unkeyed generator applies. The query sibling is reached through
    /// <see cref="EdgeEmbeddings.QueryServiceKey"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="EmbeddingInputKind.Document"/> is the right default because the UNKEYED
    /// generator is the one a vector store's upsert path resolves, and upsert embeds documents.
    /// The sibling registered under <see cref="EdgeEmbeddings.QueryServiceKey"/> is constructed
    /// with <see cref="EmbeddingInputKind.Query"/> regardless of this option, so flipping the
    /// default to <see cref="EmbeddingInputKind.Query"/> gives an app whose documents get the
    /// query prefix and whose queries still get the query prefix - two wrong halves, no exception,
    /// and a retrieval-quality drop that looks like a bad model.
    /// </remarks>
    public EmbeddingInputKind DefaultInputKind { get; set; } = EmbeddingInputKind.Document;

    /// <summary>Halve the effective batch size after a latched memory pressure until it clears.</summary>
    public bool ShrinkBatchUnderMemoryPressure { get; set; } = true;

    /// <summary>The per-model session settings this preset's model is registered with.</summary>
    public OnnxSessionOptions Session { get; } = new();

    /// <summary>
    /// What <c>AddOnnxEmbeddings</c> calls at registration when
    /// <see cref="PinnedSequenceLength"/> is set. A method rather than a setter side effect,
    /// because a setter that mutates three other properties is invisible to a reader of the
    /// options object and untestable without a container.
    /// </summary>
    /// <param name="options">The options to pin.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="PinnedSequenceLength"/> is not one of the preset's
    /// <see cref="EmbeddingPreset.SequenceBuckets"/>. Deliberately NOT an
    /// <see cref="EdgeEmbeddingException"/>: spec 15.1 allocates this package exactly five codes
    /// and none of them means "that is not one of the buckets". This is a registration-time
    /// argument error anyway - it is raised from <c>AddOnnxEmbeddings</c>, before a container is
    /// built and before any model exists.
    /// </exception>
    internal static void ApplyPinning(OnnxEmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.PinnedSequenceLength is not { } pinned)
        {
            return;
        }

        var buckets = options.Preset.SequenceBuckets;
        var known = false;
        for (var i = 0; i < buckets.Count; i++)
        {
            if (buckets[i] == pinned)
            {
                known = true;
                break;
            }
        }

        if (!known)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                pinned,
                $"PinnedSequenceLength must be one of the preset's SequenceBuckets " +
                $"[{string.Join(", ", buckets)}] for preset '{options.Preset.Id}'.");
        }

        options.Session.FreeDimensionOverrides["sequence_length"] = pinned;
        options.Session.FreeDimensionOverrides["batch_size"] = options.MaxBatchSize;

        // Only NOW is RequireStaticInputShapes meaningful: CoreML partitions from the shapes the
        // graph declares, and the two overrides above are what make those shapes static.
        options.Session.ExecutionProviders.CoreMl.RequireStaticInputShapes = true;
    }

    internal string DescribePinning() =>
        PinnedSequenceLength?.ToString(CultureInfo.InvariantCulture) ?? "(symbolic)";
}
