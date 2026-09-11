using Microsoft.Extensions.AI;

namespace Qavren.Edge.VectorData.Tests.Fakes;

/// <summary>Hash-seeded unit vectors. Deterministic, so every store assertion is exact.</summary>
/// <param name="dimensions">The width of the embeddings it produces.</param>
public sealed class DeterministicEmbeddingGenerator(int dimensions)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <inheritdoc />
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            var rng = new Random(value.GetHashCode(StringComparison.Ordinal));
            var v = new float[dimensions];
            double norm = 0;
            for (var i = 0; i < dimensions; i++)
            {
                v[i] = (float)(rng.NextDouble() - 0.5);
                norm += v[i] * v[i];
            }

            var inv = (float)(1.0 / Math.Sqrt(norm));
            for (var i = 0; i < dimensions; i++)
            {
                v[i] *= inv;
            }

            result.Add(new Embedding<float>(v));
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this
            : serviceType == typeof(EmbeddingGeneratorMetadata)
                ? new EmbeddingGeneratorMetadata("fake", defaultModelDimensions: dimensions)
                : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
