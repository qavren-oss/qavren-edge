using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Embeddings.Tests.Fakes;
using Qavren.Edge.Lifecycle;
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

    // ---- What a pinned session is then fed ---------------------------------------------------
    //
    // Everything above stops at the options object. These run the generator against a real ORT
    // session created with the overrides in place: batch_size 4 and sequence_length N are baked
    // into the graph, and ORT refuses a tensor of any other shape before it runs a node.

    /// <summary>The tier-1 preset with two buckets, so the bucket a batch rounds to and the pinned width can differ.</summary>
    private static EmbeddingPreset TwoBuckets() =>
        GeneratorFixture.Preset("fixture-hidden-states") with { SequenceBuckets = [4, 8] };

    private static GeneratorFixture Symbolic() =>
        GeneratorFixture.CreateOverHiddenStatesFixture(TimeProvider.System, TwoBuckets(), o => o.MaxBatchSize = 4);

    private static GeneratorFixture Pinned(int sequenceLength, EmbeddingTruncation truncation = EmbeddingTruncation.Truncate) =>
        GeneratorFixture.CreateOverHiddenStatesFixture(
            TimeProvider.System,
            TwoBuckets(),
            o =>
            {
                o.MaxBatchSize = 4;
                o.PinnedSequenceLength = sequenceLength;
                o.Truncation = truncation;
            });

    [Fact]
    public async Task EveryRunOfAPinnedSessionIsThePinnedShapeAndTheVectorsAreTheSymbolicOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var symbolic = Symbolic();
        using var pinned = Pinned(8);

        // Five inputs at four rows a Run: one full batch, and one that carries a single input.
        string[] inputs = ["b", "c d", "e", "d", "b c"];

        var expected = await symbolic.Generator.GenerateAsync(inputs, cancellationToken: ct);
        var actual = await pinned.Generator.GenerateAsync(inputs, cancellationToken: ct);

        // The symbolic session takes each batch at its own size, rounded up to a bucket. The pinned
        // one has exactly one shape, and a batch of one short string is that shape too.
        Assert.Equal(new[] { (4, 4), (1, 4) }, symbolic.Tokenizer.Shapes);
        Assert.Equal(new[] { (4, 8), (4, 8) }, pinned.Tokenizer.Shapes);

        Assert.Equal(inputs.Length, actual.Count);
        for (var i = 0; i < inputs.Length; i++)
        {
            Assert.Equal(expected[i].Vector.ToArray(), actual[i].Vector.ToArray());
        }

        // The inputs' own tokens. Neither the padding out to eight nor the three rows the second
        // Run was filled up with are anything the caller sent.
        Assert.Equal(expected.Usage!.InputTokenCount, actual.Usage!.InputTokenCount);
        Assert.Equal(inputs.Length, pinned.Generator.Snapshot().EmbeddingsGenerated);
    }

    [Fact]
    public async Task MemoryPressureDoesNotHalveABatchWhoseRowCountIsBakedIntoTheSession()
    {
        var ct = TestContext.Current.CancellationToken;
        using var pinned = Pinned(8);
        pinned.Resources.SetPressure(EdgeMemoryPressure.Moderate);

        var result = await pinned.Generator.GenerateAsync(["b", "c", "d", "e"], cancellationToken: ct);

        // Halving is for a session that runs as many rows as it is handed. This one runs four
        // whatever it is handed, so two inputs a Run would be the same tensor twice.
        Assert.Equal(new[] { (4, 8) }, pinned.Tokenizer.Shapes);
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public async Task AnInputLongerThanThePinnedLengthIsTruncatedToThatLengthAndCounted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var symbolic = Symbolic();
        using var pinned = Pinned(4);

        // [CLS] b c d e [SEP] is six tokens: inside the preset's eight, two more than the session
        // is wide. What fits is [CLS] b c [SEP], which is what "b c" encodes to on its own.
        var actual = await pinned.Generator.GenerateAsync(["b c d e"], cancellationToken: ct);
        var expected = await symbolic.Generator.GenerateAsync(["b c"], cancellationToken: ct);

        Assert.Equal(new[] { (4, 4) }, pinned.Tokenizer.Shapes);
        Assert.Equal(expected[0].Vector.ToArray(), actual[0].Vector.ToArray());
        Assert.Equal(4L, actual.Usage!.InputTokenCount);
        Assert.Equal(1, pinned.Generator.Snapshot().TruncatedInputs);
    }

    [Fact]
    public async Task UnderThrowAnInputLongerThanThePinnedLengthIsRefusedAgainstThatLength()
    {
        using var pinned = Pinned(4, EmbeddingTruncation.Throw);

        var ex = await Assert.ThrowsAsync<EdgeEmbeddingException>(
            () => pinned.Generator.GenerateAsync(["b c d e"], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(EdgeErrorCode.EmbeddingInputTooLong, ex.Code);
        Assert.Equal(4, ex.Expected);
    }
}
