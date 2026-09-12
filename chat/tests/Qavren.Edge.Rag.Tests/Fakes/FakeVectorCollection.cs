using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using MEVD = Microsoft.Extensions.VectorData;

namespace Qavren.Edge.Rag.Tests.Fakes;

/// <summary>The record the fake collection holds. Deliberately plain: MEVD never models it here.</summary>
public sealed class TestRecord
{
    /// <summary>The key.</summary>
    public required string Key { get; init; }

    /// <summary>The chunk body.</summary>
    public required string Text { get; init; }

    /// <summary>An optional title.</summary>
    public string? Title { get; init; }
}

/// <summary>One row in the fake collection, with a score for each lane.</summary>
/// <param name="Record">The record.</param>
/// <param name="VectorScore">What the vector lane returns - a DISTANCE, lower is better.</param>
/// <param name="HybridScore">What the hybrid lane returns - an RRF RELEVANCE, higher is better.</param>
public sealed record FakeRow(TestRecord Record, double VectorScore, double HybridScore);

/// <summary>
/// A <c>VectorStoreCollection</c> written against
/// <b><c>Microsoft.Extensions.VectorData.Abstractions</c> alone</b> - this test project references
/// no <c>Qavren.Edge.VectorData</c> (plan adjustment 19), which is what makes "point it at Qdrant
/// and it behaves identically" a property of the test project rather than a sentence in a README.
/// <para>
/// This one has <b>no</b> keyword lane. <see cref="FakeHybridVectorCollection"/> is the half that
/// implements <c>IKeywordHybridSearchable&lt;TRecord&gt;</c>.
/// </para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "It IS a VectorStoreCollection; the MEVD base type ends in Collection and the fake must read as one.")]
public class FakeVectorCollection : MEVD.VectorStoreCollection<string, TestRecord>
{
    /// <summary>Creates the collection over a fixed row set.</summary>
    /// <param name="name">The collection name.</param>
    /// <param name="rows">The rows, in any order - each lane sorts them itself.</param>
    public FakeVectorCollection(string name, IEnumerable<FakeRow> rows)
    {
        Name = name;
        Rows = [.. rows];
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <summary>The rows, for the derived hybrid collection.</summary>
    protected IReadOnlyList<FakeRow> Rows { get; }

    /// <summary>Which lane the retriever actually took: "vector", "hybrid", or null.</summary>
    public string? LastLane { get; protected set; }

    /// <summary>The <c>top</c> the retriever asked for.</summary>
    public int LastTop { get; protected set; }

    /// <summary>The <c>Skip</c> the retriever asked for.</summary>
    public int LastSkip { get; protected set; }

    /// <summary>The question the retriever passed - a string, never an embedding.</summary>
    public object? LastSearchValue { get; protected set; }

    /// <summary>The keywords the hybrid lane received.</summary>
    public IReadOnlyList<string> LastKeywords { get; protected set; } = [];

    /// <summary>The vector lane. Ascending by distance, because lower is better.</summary>
    /// <typeparam name="TInput">The search input type.</typeparam>
    /// <param name="searchValue">The question.</param>
    /// <param name="top">How many rows.</param>
    /// <param name="options">Skip, filter, threshold.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The rows, nearest first.</returns>
    public override async IAsyncEnumerable<MEVD.VectorSearchResult<TestRecord>> SearchAsync<TInput>(
        TInput searchValue,
        int top,
        MEVD.VectorSearchOptions<TestRecord>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastLane = "vector";
        LastTop = top;
        LastSkip = options?.Skip ?? 0;
        LastSearchValue = searchValue;
        LastKeywords = [];

        foreach (var row in Rows.OrderBy(r => r.VectorScore).Skip(LastSkip).Take(top))
        {
            yield return new MEVD.VectorSearchResult<TestRecord>(row.Record, row.VectorScore);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    /// <inheritdoc />
    public override Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public override Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public override Task<TestRecord?> GetAsync(
        string key, MEVD.RecordRetrievalOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Rows.FirstOrDefault(r => r.Record.Key == key)?.Record);

    /// <inheritdoc />
    public override async IAsyncEnumerable<TestRecord> GetAsync(
        IEnumerable<string> keys,
        MEVD.RecordRetrievalOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var key in keys)
        {
            var row = Rows.FirstOrDefault(r => r.Record.Key == key);
            if (row is not null)
            {
                yield return row.Record;
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override IAsyncEnumerable<TestRecord> GetAsync(
        System.Linq.Expressions.Expression<Func<TestRecord, bool>> filter,
        int top,
        MEVD.FilteredRecordRetrievalOptions<TestRecord>? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The fake collection is read-only through the two search lanes.");

    /// <inheritdoc />
    public override Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The fake collection is read-only through the two search lanes.");

    /// <inheritdoc />
    public override Task DeleteAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The fake collection is read-only through the two search lanes.");

    /// <inheritdoc />
    public override Task UpsertAsync(TestRecord record, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The fake collection is read-only through the two search lanes.");

    /// <inheritdoc />
    public override Task UpsertAsync(IEnumerable<TestRecord> records, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The fake collection is read-only through the two search lanes.");

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }
}

/// <summary>
/// The same fake, plus the keyword lane. The hybrid lane returns the RRF score - higher is better -
/// which is the opposite polarity to the vector lane, and the whole reason
/// <see cref="RetrievalScoreKind"/> exists.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "It IS a VectorStoreCollection; the MEVD base type ends in Collection and the fake must read as one.")]
public sealed class FakeHybridVectorCollection : FakeVectorCollection, MEVD.IKeywordHybridSearchable<TestRecord>
{
    /// <summary>Creates the hybrid-capable collection.</summary>
    /// <param name="name">The collection name.</param>
    /// <param name="rows">The rows.</param>
    public FakeHybridVectorCollection(string name, IEnumerable<FakeRow> rows)
        : base(name, rows)
    {
    }

    /// <summary>The hybrid lane. Descending by relevance, because higher is better.</summary>
    /// <typeparam name="TInput">The search input type.</typeparam>
    /// <param name="searchValue">The question.</param>
    /// <param name="keywords">The keyword half.</param>
    /// <param name="top">How many rows.</param>
    /// <param name="options">Skip, filter, threshold.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The rows, best first.</returns>
    public async IAsyncEnumerable<MEVD.VectorSearchResult<TestRecord>> HybridSearchAsync<TInput>(
        TInput searchValue,
        ICollection<string> keywords,
        int top,
        MEVD.HybridSearchOptions<TestRecord>? options = default,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where TInput : notnull
    {
        LastLane = "hybrid";
        LastTop = top;
        LastSkip = options?.Skip ?? 0;
        LastSearchValue = searchValue;
        LastKeywords = [.. keywords];

        foreach (var row in Rows.OrderByDescending(r => r.HybridScore).Skip(LastSkip).Take(top))
        {
            yield return new MEVD.VectorSearchResult<TestRecord>(row.Record, row.HybridScore);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>
/// An MEVD <c>VectorStore</c> over one named collection, for
/// <c>AddVectorStoreRetriever</c>'s <c>storeName</c> arms. Written against the abstractions alone.
/// </summary>
public sealed class FakeVectorStore : MEVD.VectorStore
{
    private readonly Dictionary<string, FakeVectorCollection> _collections;

    /// <summary>Creates the store over a set of collections, keyed by name.</summary>
    /// <param name="collections">The collections this store serves.</param>
    public FakeVectorStore(params FakeVectorCollection[] collections)
    {
        _collections = collections.ToDictionary(c => c.Name, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public override MEVD.VectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(
        string name, MEVD.VectorStoreCollectionDefinition? definition = null) =>
        _collections.TryGetValue(name, out var collection)
            ? (MEVD.VectorStoreCollection<TKey, TRecord>)(object)collection
            : throw new InvalidOperationException($"The fake store holds no collection named '{name}'.");

    /// <inheritdoc />
    public override MEVD.VectorStoreCollection<object, Dictionary<string, object?>> GetDynamicCollection(
        string name, MEVD.VectorStoreCollectionDefinition definition) =>
        throw new NotSupportedException("The fake store serves typed collections only.");

    /// <inheritdoc />
    public override async IAsyncEnumerable<string> ListCollectionNamesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var name in _collections.Keys)
        {
            yield return name;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(_collections.ContainsKey(name));

    /// <inheritdoc />
    public override Task EnsureCollectionDeletedAsync(string name, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }
}
