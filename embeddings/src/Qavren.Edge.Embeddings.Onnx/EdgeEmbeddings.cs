using Microsoft.Extensions.AI;

namespace Qavren.Edge.Embeddings.Onnx;

/// <summary>The naming convention that connects a generator to its query-prefixed sibling.</summary>
public static class EdgeEmbeddings
{
    /// <summary>
    /// The service key under which a generator exposes its query-prefixed sibling through
    /// <c>GetService</c>. A named convention, not a private protocol - and deliberately a bare
    /// string constant rather than a shared type, because <c>Qavren.Edge.VectorData</c> cannot
    /// reference this package and must therefore carry its own copy of the same literal in
    /// <c>EdgeVectorData.QueryGeneratorServiceKey</c>. A tier-2 test asserts the two constants are
    /// equal from a project that references both, so they cannot drift.
    /// </summary>
    public const string QueryServiceKey = "qavren.edge.query";

    /// <summary>
    /// Resolves the query sibling, falling back to <paramref name="generator"/> when it exposes
    /// none (a third-party generator, or a preset that declares no prefixes).
    /// </summary>
    /// <param name="generator">The generator to ask.</param>
    /// <returns>The query-side generator, or <paramref name="generator"/> itself.</returns>
    /// <remarks>
    /// Receives and returns the NON-generic <see cref="IEmbeddingGenerator"/> on purpose: the
    /// vector store holds generators as the non-generic interface, a third-party generator may be
    /// typed to a different input type, and a closed-generic receiver would not bind to either
    /// without a cast.
    /// </remarks>
    public static IEmbeddingGenerator AsQueryGenerator(this IEmbeddingGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);

        return generator.GetService(typeof(IEmbeddingGenerator), QueryServiceKey) as IEmbeddingGenerator
            ?? generator;
    }
}
