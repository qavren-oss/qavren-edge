namespace Qavren.Edge.Rag;

/// <summary>The whole retrieval seam.</summary>
/// <remarks>
/// The shape deliberately mirrors Agent Framework's
/// <c>TextSearchProvider(Func&lt;string, CancellationToken, Task&lt;IEnumerable&lt;TextSearchResult&gt;&gt;&gt;)</c>
/// so an app that later adopts an <c>AIContextProvider</c> hands over the same lambda - while
/// sub-project 4 takes no dependency on <c>Microsoft.Agents.AI</c>, because
/// <c>UseAIContextProviders</c> throws <c>InvalidOperationException</c> at invocation time unless
/// there is a live <c>AIAgent.CurrentRunContext</c>, which a plain <c>IChatClient</c> on a bare
/// <c>ServiceCollection</c> never has.
/// </remarks>
public interface IEdgeRetriever
{
    /// <summary>A stable name for logs, diagnostics and <c>EdgeRagException.RetrieverName</c>.</summary>
    string Name { get; }

    /// <summary>Returns the matching sources, best-first, with <c>Score</c> and <c>ScoreKind</c> stamped.</summary>
    ValueTask<IReadOnlyList<RagSource>> RetrieveAsync(
        string query, RetrievalRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// An <see cref="IEdgeRetriever"/> over a lambda, for a consumer whose corpus is not an MEVD
/// collection - an in-memory list, an HTTP search endpoint, a hand-rolled index.
/// </summary>
public sealed class DelegateRetriever : IEdgeRetriever
{
    private readonly Func<string, RetrievalRequest, CancellationToken, Task<IEnumerable<RagSource>>> _retrieve;

    /// <summary>Creates a retriever that forwards every call to <paramref name="retrieve"/>.</summary>
    public DelegateRetriever(
        string name,
        Func<string, RetrievalRequest, CancellationToken, Task<IEnumerable<RagSource>>> retrieve)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(retrieve);

        Name = name;
        _retrieve = retrieve;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<RagSource>> RetrieveAsync(
        string query, RetrievalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var results = await _retrieve(query, request, cancellationToken).ConfigureAwait(false);
        return results as IReadOnlyList<RagSource> ?? [.. results];
    }
}
