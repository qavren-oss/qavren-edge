using Microsoft.Extensions.AI;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// The stand-in MEVD's model builder takes where a real <see cref="IEmbeddingGenerator"/> would go.
/// SP2 declares its own equivalent <c>internal</c>, so SP3 ships this one.
/// <para>
/// It is passed on every model build and is <b>never</b> invoked on any path, because SP3's vector
/// property is declared <c>ReadOnlyMemory&lt;float&gt;</c> rather than a <c>string</c> source — the
/// refusal that forces the stand-in in SP2 does not even arise here. Passing it anyway costs twelve
/// lines and removes a dependency on whether that MEVD parameter is nullable.
/// </para>
/// </summary>
internal sealed class IngestionSchemaOnlyGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public static IngestionSchemaOnlyGenerator Instance { get; } = new();

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This generator exists only so MEVD's model builder has one to hold while it emits DDL. " +
            "Register a real embedding generator - AddOnnxEmbeddings(), or any " +
            "IEmbeddingGenerator<string, Embedding<float>>.");

    /// <summary>Returns null so nothing can read a width off it.</summary>
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        // Nothing to release: this type holds no model, no session and no handle.
    }
}
