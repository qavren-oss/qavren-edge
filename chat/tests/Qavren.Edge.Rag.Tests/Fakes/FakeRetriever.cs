namespace Qavren.Edge.Rag.Tests.Fakes;

/// <summary>
/// An <see cref="IEdgeRetriever"/> that returns a fixed list, or throws. It records what the
/// middleware asked for, which is how the <c>Top</c> and keyword plumbing is asserted.
/// </summary>
public sealed class FakeRetriever : IEdgeRetriever
{
    private readonly IReadOnlyList<RagSource> _sources;
    private readonly Exception? _failure;

    /// <summary>Creates a retriever that returns <paramref name="sources"/>.</summary>
    /// <param name="sources">What every call returns.</param>
    /// <param name="name">The retriever name, for logs and diagnostics.</param>
    public FakeRetriever(IReadOnlyList<RagSource> sources, string name = "fake")
    {
        _sources = sources;
        Name = name;
    }

    private FakeRetriever(Exception failure, string name)
    {
        _sources = [];
        _failure = failure;
        Name = name;
    }

    /// <summary>Creates a retriever that always throws.</summary>
    /// <param name="failure">What it throws.</param>
    /// <param name="name">The retriever name.</param>
    /// <returns>The retriever.</returns>
    public static FakeRetriever Throwing(Exception failure, string name = "fake") => new(failure, name);

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>How many times the middleware asked.</summary>
    public int CallCount { get; private set; }

    /// <summary>The query the middleware built.</summary>
    public string? LastQuery { get; private set; }

    /// <summary>The request the middleware built.</summary>
    public RetrievalRequest? LastRequest { get; private set; }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<RagSource>> RetrieveAsync(
        string query, RetrievalRequest request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastQuery = query;
        LastRequest = request;

        return _failure is not null
            ? ValueTask.FromException<IReadOnlyList<RagSource>>(_failure)
            : ValueTask.FromResult(_sources);
    }
}
