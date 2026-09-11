using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using Qavren.Edge.Rag.Internal;
using MEVD = Microsoft.Extensions.VectorData;

namespace Qavren.Edge.Rag;

/// <summary>Everything <see cref="VectorStoreRetriever{TKey, TRecord}"/> lets a caller change.</summary>
/// <typeparam name="TRecord">The MEVD record type the collection holds.</typeparam>
public sealed class VectorStoreRetrieverOptions<TRecord>
{
    /// <summary>
    /// Use <c>IKeywordHybridSearchable&lt;TRecord&gt;</c> when the collection implements it and
    /// keywords are present. Default <see langword="true"/>.
    /// <para>
    /// <b>It changes the scoring polarity.</b> A collection that implements the interface is scored
    /// as <see cref="RetrievalScoreKind.Relevance"/> (higher is better); one that does not is scored
    /// as <see cref="RetrievalScoreKind.Distance"/> (lower is better). That is the whole reason
    /// <see cref="RetrievalScoreKind"/> exists and the reason the retriever re-stamps it after
    /// projection.
    /// </para>
    /// </summary>
    public bool PreferHybridSearch { get; set; } = true;

    /// <summary>
    /// Turn <see cref="PreferHybridSearch"/>'s silent fallback into a refusal. Default
    /// <see langword="false"/>, which is the advisory behaviour: a collection with no keyword lane
    /// takes the vector lane and nothing is thrown.
    /// <para>
    /// <see langword="true"/> throws <see cref="EdgeErrorCode.RagCollectionNotSearchable"/> (7203)
    /// when the collection does not implement <c>IKeywordHybridSearchable&lt;TRecord&gt;</c> - and
    /// it is the only raise site for that code in the suite. Note what it does <b>not</b> do: a
    /// hybrid-capable collection asked a question that produced <b>no</b> keywords still takes the
    /// vector lane without error. The option is about the collection's capability, which is static;
    /// it is not about whether one particular question survived the stop list, which is not.
    /// </para>
    /// </summary>
    public bool RequireHybridSearch { get; set; }

    /// <summary>
    /// Applied with the correct polarity for the lane actually taken - <c>Score &lt;= threshold</c>
    /// on the vector lane, where the score is a distance, and <c>Score &gt;= threshold</c> on the
    /// hybrid lane, where it is a relevance. Null means no threshold, and is the default.
    /// </summary>
    public double? ScoreThreshold { get; set; }

    /// <summary>
    /// Upper bound on <c>Top + Skip</c>. 4096 is vec0's <c>SQLITE_VEC_VEC0_K_MAX</c>, which is what
    /// sub-project 2 enforces; exceeding it throws
    /// <see cref="EdgeErrorCode.RagRetrievalFailed"/> naming the limit, rather than surfacing
    /// sub-project 2's <c>KnnLimitExceeded</c> three frames down. A store with no such limit -
    /// Qdrant, Azure AI Search - is the one case where the default is arbitrary rather than
    /// physical, so it is settable and the refusal names it.
    /// </summary>
    public int MaxCandidates { get; set; } = 4096;

    /// <summary>Server-side filter. Null, the default, makes the whole collection a candidate.</summary>
    public Expression<Func<TRecord, bool>>? Filter { get; set; }
}

/// <summary>
/// Adapts <b>any</b> MEVD collection. Nothing here is Qavren-specific: point it at Qdrant, Azure AI
/// Search or <c>EdgeVectorStoreCollection</c> and it behaves identically. It chooses the hybrid
/// lane when the collection implements <c>IKeywordHybridSearchable&lt;TRecord&gt;</c> and keywords
/// are present, the vector lane otherwise, stamps <see cref="RagSource.ScoreKind"/> to match, and
/// always returns best-first.
/// </summary>
/// <typeparam name="TKey">The collection's key type.</typeparam>
/// <typeparam name="TRecord">The collection's record type.</typeparam>
/// <remarks>
/// Query-side embedding is not this package's business: the question is handed to the collection as
/// a <b>string</b> and the store resolves its own query-side generator internally, so sub-project
/// 2's asymmetric query/document prefix convention keeps working with nothing duplicated here.
/// </remarks>
public sealed class VectorStoreRetriever<TKey, TRecord> : IEdgeRetriever, IVectorStoreRetrieverDescriptor
    where TKey : notnull
    where TRecord : class
{
    private const string TrimMessage = "MEVD reflects over TRecord to build a collection model.";

    private readonly MEVD.VectorStoreCollection<TKey, TRecord> _collection;
    private readonly Func<TRecord, RagSource> _project;

    /// <summary>Creates a retriever over one MEVD collection.</summary>
    /// <param name="collection">The collection to search.</param>
    /// <param name="project">
    /// Maps a record to its <b>content</b>: <see cref="RagSource.Id"/>, <see cref="RagSource.Text"/>,
    /// and optionally <see cref="RagSource.Title"/>, <see cref="RagSource.Uri"/> and
    /// <see cref="RagSource.Record"/>. It takes the record alone, and it is the same one-argument
    /// signature <c>AddVectorStoreRetriever</c> takes - there is exactly one projector shape in
    /// this package.
    /// <para>
    /// <b>The retriever, not the projector, supplies the scoring fields.</b> After calling
    /// <paramref name="project"/> the retriever re-stamps the result with
    /// <c>source with { Score = result.Score ?? 0d, ScoreKind = kind, Ordinal = 0 }</c>, where
    /// <c>kind</c> is <see cref="RetrievalScoreKind.Relevance"/> on the hybrid lane and
    /// <see cref="RetrievalScoreKind.Distance"/> on the vector lane. Anything the projector set on
    /// those three <c>init</c> properties is overwritten. That is deliberate: the polarity is a
    /// property of the search lane the retriever chose at runtime, not of the record, and a
    /// projector cannot know which lane ran. A projector that guesses is the exact bug
    /// <see cref="RetrievalScoreKind"/> exists to prevent.
    /// </para>
    /// <para>
    /// <see cref="RagSource.Ordinal"/> stays 0 here and is assigned by <c>RagPrompts.Format</c>
    /// after ranking and clamping, because it is a position in the assembled block rather than a
    /// property of the source.
    /// </para>
    /// </param>
    /// <param name="configure">Configures the retriever options.</param>
    [RequiresDynamicCode(TrimMessage)]
    [RequiresUnreferencedCode(TrimMessage)]
    public VectorStoreRetriever(
        MEVD.VectorStoreCollection<TKey, TRecord> collection,
        Func<TRecord, RagSource> project,
        Action<VectorStoreRetrieverOptions<TRecord>>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(project);

        _collection = collection;
        _project = project;
        Options = new VectorStoreRetrieverOptions<TRecord>();
        configure?.Invoke(Options);
    }

    /// <inheritdoc />
    public string Name => _collection.Name;

    /// <summary>The MEVD collection name, for <c>EdgeRagException.CollectionName</c> and diagnostics.</summary>
    public string CollectionName => _collection.Name;

    /// <summary>The options this retriever was configured with.</summary>
    public VectorStoreRetrieverOptions<TRecord> Options { get; }

    /// <inheritdoc />
    bool IVectorStoreRetrieverDescriptor.PrefersHybridSearch => Options.PreferHybridSearch;

    /// <inheritdoc />
    /// <exception cref="EdgeRagException">
    /// <see cref="EdgeErrorCode.RagRetrievalFailed"/> (7202) when <c>Top + Skip</c> exceeds
    /// <see cref="VectorStoreRetrieverOptions{TRecord}.MaxCandidates"/>;
    /// <see cref="EdgeErrorCode.RagCollectionNotSearchable"/> (7203) when
    /// <see cref="VectorStoreRetrieverOptions{TRecord}.RequireHybridSearch"/> is set and the
    /// collection has no keyword lane.
    /// </exception>
    public async ValueTask<IReadOnlyList<RagSource>> RetrieveAsync(
        string query, RetrievalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Top);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Skip);

        var candidates = (long)request.Top + request.Skip;
        if (candidates > Options.MaxCandidates)
        {
            throw new EdgeRagException(
                EdgeErrorCode.RagRetrievalFailed,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Top + Skip is {0}, above MaxCandidates of {1} - vec0's SQLITE_VEC_VEC0_K_MAX.",
                    candidates,
                    Options.MaxCandidates),
                retrieverName: Name,
                collectionName: CollectionName,
                remediation:
                    "Lower RetrievalRequest.Top or RetrievalRequest.Skip, or - if the backing store " +
                    "has no SQLITE_VEC_VEC0_K_MAX, as Qdrant and Azure AI Search do not - raise " +
                    "VectorStoreRetrieverOptions<TRecord>.MaxCandidates.");
        }

        var hybrid = _collection as MEVD.IKeywordHybridSearchable<TRecord>;

        if (Options.RequireHybridSearch && hybrid is null)
        {
            throw new EdgeRagException(
                EdgeErrorCode.RagCollectionNotSearchable,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "RequireHybridSearch is set, but the collection '{0}' does not implement " +
                    "IKeywordHybridSearchable<{1}>.",
                    _collection.GetType().FullName,
                    typeof(TRecord).Name),
                retrieverName: Name,
                collectionName: CollectionName,
                remediation:
                    "Point the retriever at a collection that implements " +
                    "IKeywordHybridSearchable<TRecord>, or clear " +
                    "VectorStoreRetrieverOptions<TRecord>.RequireHybridSearch and leave " +
                    "PreferHybridSearch advisory, which falls back to the vector lane instead.");
        }

        // The lane choice, in the order spec 7 declares it: capability, then preference, then
        // whether this particular question produced any keywords at all.
        var keywords = Materialise(request.Keywords);
        var useHybrid = hybrid is not null && Options.PreferHybridSearch && keywords.Count > 0;
        var kind = useHybrid ? RetrievalScoreKind.Relevance : RetrievalScoreKind.Distance;
        var threshold = request.ScoreThreshold ?? Options.ScoreThreshold;

        var sources = new List<RagSource>(request.Top);

        if (useHybrid)
        {
            var options = new MEVD.HybridSearchOptions<TRecord>
            {
                Skip = request.Skip,
                Filter = Options.Filter,
                ScoreThreshold = threshold,
            };

            await foreach (var result in hybrid!
                .HybridSearchAsync(query, keywords, request.Top, options, cancellationToken)
                .ConfigureAwait(false))
            {
                Accept(sources, result, kind, threshold);
            }
        }
        else
        {
            var options = new MEVD.VectorSearchOptions<TRecord>
            {
                Skip = request.Skip,
                Filter = Options.Filter,
                ScoreThreshold = threshold,
            };

            await foreach (var result in _collection
                .SearchAsync(query, request.Top, options, cancellationToken)
                .ConfigureAwait(false))
            {
                Accept(sources, result, kind, threshold);
            }
        }

        return sources;
    }

    private static List<string> Materialise(IReadOnlyCollection<string>? keywords)
    {
        if (keywords is null || keywords.Count == 0)
        {
            return [];
        }

        var materialised = new List<string>(keywords.Count);
        foreach (var keyword in keywords)
        {
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                materialised.Add(keyword);
            }
        }

        return materialised;
    }

    // The threshold is pushed into the MEVD options above so a real store can apply it in the
    // query, AND re-applied here with the lane's polarity: one number, two comparisons. The second
    // application is a no-op against a store that honoured the first, and is what makes the
    // polarity a property of this package rather than of whichever store happens to be underneath.
    private void Accept(
        List<RagSource> sources,
        MEVD.VectorSearchResult<TRecord> result,
        RetrievalScoreKind kind,
        double? threshold)
    {
        var score = result.Score ?? 0d;

        if (threshold is { } limit &&
            (kind == RetrievalScoreKind.Distance ? score > limit : score < limit))
        {
            return;
        }

        var projected = _project(result.Record);
        sources.Add(projected with { Score = score, ScoreKind = kind, Ordinal = 0 });
    }
}
