using Qavren.Edge.Embeddings.Onnx;
using Xunit;

namespace Qavren.Edge.Embeddings.Tests;

/// <summary>
/// Spec 16.1's mean / CLS / L2 assertions against a hand-computed reference. The hidden state is
/// the exact tensor ORT returned for <c>TinyModels.HiddenStates</c> at
/// <c>input_ids = [[3, 1, 9]]</c>: the fixture embedding table's row <i>t</i> is
/// <c>[t, t+0.5, t+0.25, t+0.75]</c>.
/// </summary>
public class PoolerTests
{
    [Fact]
    public void MeanPoolIgnoresMaskedPositions()
    {
        ReadOnlySpan<float> hidden = [3f, 3.5f, 3.25f, 3.75f, 1f, 1.5f, 1.25f, 1.75f, 9f, 9.5f, 9.25f, 9.75f];
        ReadOnlySpan<long> mask = [1, 1, 0];
        Span<float> destination = stackalloc float[4];

        EmbeddingPooler.MeanPool(hidden, mask, sequenceLength: 3, dimensions: 4, destination);

        Assert.Equal([2f, 2.5f, 2.25f, 2.75f], destination.ToArray());
    }

    [Fact]
    public void ClsPoolTakesRowZero()
    {
        ReadOnlySpan<float> hidden = [3f, 3.5f, 3.25f, 3.75f, 1f, 1.5f, 1.25f, 1.75f, 9f, 9.5f, 9.25f, 9.75f];
        Span<float> destination = stackalloc float[4];

        EmbeddingPooler.ClsPool(hidden, sequenceLength: 3, dimensions: 4, destination);

        Assert.Equal([3f, 3.5f, 3.25f, 3.75f], destination.ToArray());
    }

    [Fact]
    public void AnAllPaddingRowDividesByOneRatherThanZero()
    {
        ReadOnlySpan<float> hidden = [3f, 3.5f, 3.25f, 3.75f];
        ReadOnlySpan<long> mask = [0];
        Span<float> destination = stackalloc float[4];

        EmbeddingPooler.MeanPool(hidden, mask, sequenceLength: 1, dimensions: 4, destination);

        // The sum is 0 and the denominator is max(1, 0).
        Assert.Equal([0f, 0f, 0f, 0f], destination.ToArray());
    }

    [Fact]
    public void L2NormalizeDividesByTheEuclideanNorm()
    {
        // The MeanPool result above. |v| = sqrt(4 + 6.25 + 5.0625 + 7.5625) = sqrt(22.875).
        // The plan writes this constant as 4.783303, which is sqrt(22.880) rather than
        // sqrt(22.875) and is wrong in the 4th decimal - far outside the 5-decimal tolerance the
        // same assertion asks for. 4.7827817 is the real square root.
        Span<float> v = [2f, 2.5f, 2.25f, 2.75f];
        const float Norm = 4.7827817f;

        EmbeddingPooler.L2Normalize(v);

        Assert.Equal(2f / Norm, v[0], 5);
        Assert.Equal(2.5f / Norm, v[1], 5);
        Assert.Equal(2.25f / Norm, v[2], 5);
        Assert.Equal(2.75f / Norm, v[3], 5);

        var lengthSquared = 0f;
        foreach (var x in v)
        {
            lengthSquared += x * x;
        }

        Assert.Equal(1f, lengthSquared, 5);
    }

    [Fact]
    public void L2NormalizeLeavesAZeroVectorAlone()
    {
        // The all-padding MeanPool guard produces this, so the two guards have to compose: dividing
        // by a zero norm here would turn a defined zero vector into four NaNs one call later.
        Span<float> v = [0f, 0f, 0f, 0f];

        EmbeddingPooler.L2Normalize(v);

        Assert.Equal([0f, 0f, 0f, 0f], v.ToArray());
    }

    [Fact]
    public void LayerNormSubtractsTheMeanAndDividesByTheStandardDeviation()
    {
        // mean = 2.5, population variance = 1.25, sqrt(1.25 + 1e-5) = 1.1180384...
        Span<float> v = [1f, 2f, 3f, 4f];
        var scale = 1.0 / Math.Sqrt(1.25 + 1e-5);

        EmbeddingPooler.LayerNorm(v, 1e-5f);

        Assert.Equal((float)(-1.5 * scale), v[0], 5);
        Assert.Equal((float)(-0.5 * scale), v[1], 5);
        Assert.Equal((float)(0.5 * scale), v[2], 5);
        Assert.Equal((float)(1.5 * scale), v[3], 5);
    }

    [Fact]
    public void TheEpsilonIsWhatKeepsAConstantVectorFiniteRatherThanNaN()
    {
        // Zero variance: without the epsilon this is a division by zero and four NaNs.
        Span<float> v = [7f, 7f, 7f, 7f];

        EmbeddingPooler.LayerNorm(v, 1e-5f);

        Assert.All(v.ToArray(), x => Assert.Equal(0f, x, 5));
    }

    [Fact]
    public void TheOrderIsPoolThenLayerNormThenL2AndNotPoolThenL2ThenLayerNorm()
    {
        // Same starting vector down both routes. LayerNorm is not scale-invariant in the way that
        // would make these agree, so a pipeline that normalises before layer-norming ships a
        // different vector with no error anywhere.
        float[] pooled = [1f, 2f, 3f, 4f];

        Span<float> shipped = [.. pooled];
        EmbeddingPooler.LayerNorm(shipped, 1e-5f);
        EmbeddingPooler.L2Normalize(shipped);

        Span<float> reversed = [.. pooled];
        EmbeddingPooler.L2Normalize(reversed);
        EmbeddingPooler.LayerNorm(reversed, 1e-5f);

        Assert.NotEqual(shipped.ToArray(), reversed.ToArray());

        // And the shipped route really is on the unit sphere, which is the property the reversed
        // route silently loses.
        var lengthSquared = 0f;
        foreach (var x in shipped)
        {
            lengthSquared += x * x;
        }

        Assert.Equal(1f, lengthSquared, 5);
    }
}
