using System.IO.Hashing;
using System.Text;
using Microsoft.Extensions.AI;

namespace Qavren.Edge.Benchmarks.Infrastructure;

/// <summary>
/// A deterministic, model-free <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> for the
/// ingestion benchmarks: each text's xxHash64 seeds a unit vector. It stands in for the ONNX
/// generator so the ingestion numbers measure extraction, chunking, hashing and state rather than
/// the encoder - which the Embeddings benchmarks measure on their own - and so the ingestion
/// benchmarks need no model download. Registering any generator is public surface; this one is not
/// a recommendation.
/// </summary>
internal sealed class HashingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private static readonly EmbeddingGeneratorMetadata Metadata =
        new("qavren-edge-benchmarks", providerUri: null, defaultModelId: "hashing", defaultModelDimensions: Synthetic.Dimensions);

    /// <summary>How many times <see cref="GenerateAsync"/> has been called.</summary>
    public int Calls { get; private set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        Calls++;

        var embeddings = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            var seed = unchecked((int)XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(value)));
            embeddings.Add(new Embedding<float>(Synthetic.UnitVector(new Random(seed))));
        }

        return Task.FromResult(embeddings);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(Metadata) ? Metadata
            : serviceKey is null && serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    public void Dispose()
    {
    }
}
