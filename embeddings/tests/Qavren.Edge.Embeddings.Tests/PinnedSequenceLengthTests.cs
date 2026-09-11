using Qavren.Edge.Embeddings.Onnx;
using Xunit;

namespace Qavren.Edge.Embeddings.Tests;

/// <summary>
/// The L1 half of spec 16.1's session-factory sentence. Task 2.2's <c>SessionFactoryShapeTests</c>
/// asserts the L0 half - raw <c>FreeDimensionOverrides</c> against the
/// <c>OnnxStaticShapesUnpinned</c> guard - from a project that cannot see
/// <see cref="OnnxEmbeddingOptions"/>. This is the other half, and it is the only place in the
/// suite where both types are visible.
/// </summary>
public class PinnedSequenceLengthTests
{
    [Fact]
    public void PinnedSequenceLengthPopulatesBothTheOverrideDictionaryAndTheFlag()
    {
        var options = new OnnxEmbeddingOptions
        {
            Preset = EmbeddingPresets.MiniLmL6V2Int8,
            MaxBatchSize = 16,
            PinnedSequenceLength = 128,
        };

        OnnxEmbeddingOptions.ApplyPinning(options);

        Assert.Equal(128, options.Session.FreeDimensionOverrides["sequence_length"]);
        Assert.Equal(16, options.Session.FreeDimensionOverrides["batch_size"]);
        Assert.True(options.Session.ExecutionProviders.CoreMl.RequireStaticInputShapes);
    }

    [Fact]
    public void LeavingItNullLeavesTheGraphSymbolicAndCoreMlPartitioningNormally()
    {
        var options = new OnnxEmbeddingOptions { Preset = EmbeddingPresets.MiniLmL6V2Int8 };

        OnnxEmbeddingOptions.ApplyPinning(options);

        Assert.Empty(options.Session.FreeDimensionOverrides);
        Assert.False(options.Session.ExecutionProviders.CoreMl.RequireStaticInputShapes);
    }

    [Fact]
    public void APinnedLengthThatIsNotOneOfThePresetsBucketsIsRejected()
    {
        var options = new OnnxEmbeddingOptions
        {
            Preset = EmbeddingPresets.MiniLmL6V2Int8,
            PinnedSequenceLength = 100,
        };

        // ArgumentOutOfRangeException, NOT an EdgeEmbeddingException: spec 15.1 allocates this
        // package exactly five codes and none of them means "that is not one of the buckets".
        // Inventing a sixth would be an SP1 EdgeErrorCode edit, and the plan permits two, both
        // already spent. This is a registration-time argument error anyway - it is raised from
        // AddOnnxEmbeddings, before a container is built, before any model exists.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => OnnxEmbeddingOptions.ApplyPinning(options));

        Assert.Contains("SequenceBuckets", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PinningTracksMaxBatchSizeRatherThanAFixedSixteen()
    {
        var options = new OnnxEmbeddingOptions
        {
            Preset = EmbeddingPresets.MiniLmL6V2Int8,
            MaxBatchSize = 4,
            PinnedSequenceLength = 64,
        };

        OnnxEmbeddingOptions.ApplyPinning(options);

        Assert.Equal(4, options.Session.FreeDimensionOverrides["batch_size"]);
    }
}
