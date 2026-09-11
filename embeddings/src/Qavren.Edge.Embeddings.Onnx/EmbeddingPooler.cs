namespace Qavren.Edge.Embeddings.Onnx;

/// <summary>
/// Allocation-free pooling over a session output span. Public because it is worth testing alone.
/// </summary>
/// <remarks>
/// The order is fixed and it matters: pool, then <see cref="LayerNorm"/> when the preset asks for
/// it, then <see cref="L2Normalize"/>. Layer-norming a unit vector is not the same operation as
/// unit-normalising a layer-normed one, and only the second matches nomic-embed-text-v1.5's
/// reference pipeline.
/// </remarks>
public static class EmbeddingPooler
{
    /// <summary>
    /// The masked mean: <c>sum(h[t] * mask[t]) / max(1, sum(mask))</c>. The denominator's
    /// <c>max(1, ...)</c> is what keeps an all-padding row a defined zero vector rather than
    /// <c>dimensions</c> NaNs.
    /// </summary>
    /// <param name="lastHiddenState">One row of the output tensor, <c>[sequenceLength, dimensions]</c>, row-major.</param>
    /// <param name="attentionMask">That row's mask, <c>[sequenceLength]</c>. 1 real, 0 padding.</param>
    /// <param name="sequenceLength">The padded width.</param>
    /// <param name="dimensions">The embedding width.</param>
    /// <param name="destination">Where the pooled vector is written. At least <paramref name="dimensions"/> long.</param>
    public static void MeanPool(
        ReadOnlySpan<float> lastHiddenState,
        ReadOnlySpan<long> attentionMask,
        int sequenceLength,
        int dimensions,
        Span<float> destination)
    {
        Validate(lastHiddenState, sequenceLength, dimensions, destination);
        if (attentionMask.Length < sequenceLength)
        {
            throw new ArgumentException(
                $"The attention mask holds {attentionMask.Length} entries but the sequence is {sequenceLength} long.",
                nameof(attentionMask));
        }

        destination[..dimensions].Clear();

        long real = 0;
        for (var t = 0; t < sequenceLength; t++)
        {
            if (attentionMask[t] == 0)
            {
                continue;
            }

            real++;
            var row = lastHiddenState.Slice(t * dimensions, dimensions);
            for (var d = 0; d < dimensions; d++)
            {
                destination[d] += row[d];
            }
        }

        var denominator = (float)Math.Max(1L, real);
        for (var d = 0; d < dimensions; d++)
        {
            destination[d] /= denominator;
        }
    }

    /// <summary>Copies row zero, the <c>[CLS]</c> position. bge-small-en-v1.5 pools this way.</summary>
    /// <param name="lastHiddenState">One row of the output tensor, <c>[sequenceLength, dimensions]</c>, row-major.</param>
    /// <param name="sequenceLength">The padded width.</param>
    /// <param name="dimensions">The embedding width.</param>
    /// <param name="destination">Where the pooled vector is written. At least <paramref name="dimensions"/> long.</param>
    public static void ClsPool(
        ReadOnlySpan<float> lastHiddenState,
        int sequenceLength,
        int dimensions,
        Span<float> destination)
    {
        Validate(lastHiddenState, sequenceLength, dimensions, destination);
        lastHiddenState[..dimensions].CopyTo(destination);
    }

    /// <summary>
    /// Mean/variance normalisation over the pooled vector, matching PyTorch
    /// <c>F.layer_norm(x, (dim,))</c> with no learned weight or bias: subtract the mean, divide by
    /// <c>sqrt(variance + epsilon)</c>. Required by nomic-embed-text-v1.5 and by nothing else
    /// here; see <see cref="EmbeddingPreset.PostPoolLayerNorm"/>.
    /// </summary>
    /// <param name="vector">The pooled vector, normalised in place.</param>
    /// <param name="epsilon">The variance floor. 1e-5 is PyTorch's default.</param>
    public static void LayerNorm(Span<float> vector, float epsilon)
    {
        if (vector.Length == 0)
        {
            return;
        }

        double sum = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            sum += vector[i];
        }

        var mean = sum / vector.Length;

        double variance = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            var delta = vector[i] - mean;
            variance += delta * delta;
        }

        variance /= vector.Length;

        var scale = 1.0 / Math.Sqrt(variance + epsilon);
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)((vector[i] - mean) * scale);
        }
    }

    /// <summary>
    /// Divides by the Euclidean norm, in place. A zero vector is left alone: the all-padding
    /// <see cref="MeanPool"/> guard produces one, and dividing by a zero norm here would turn a
    /// defined zero vector into NaNs one call later.
    /// </summary>
    /// <param name="vector">The vector, normalised in place.</param>
    public static void L2Normalize(Span<float> vector)
    {
        double lengthSquared = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            lengthSquared += (double)vector[i] * vector[i];
        }

        if (lengthSquared <= 0)
        {
            return;
        }

        var norm = (float)Math.Sqrt(lengthSquared);
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }
    }

    private static void Validate(
        ReadOnlySpan<float> lastHiddenState,
        int sequenceLength,
        int dimensions,
        Span<float> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequenceLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);

        if (lastHiddenState.Length < sequenceLength * dimensions)
        {
            throw new ArgumentException(
                $"The hidden state holds {lastHiddenState.Length} floats but {sequenceLength} x {dimensions} were expected.",
                nameof(lastHiddenState));
        }

        if (destination.Length < dimensions)
        {
            throw new ArgumentException(
                $"The destination holds {destination.Length} floats but {dimensions} were expected.",
                nameof(destination));
        }
    }
}
