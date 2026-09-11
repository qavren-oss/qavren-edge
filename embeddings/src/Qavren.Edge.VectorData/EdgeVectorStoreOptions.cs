using Microsoft.Extensions.AI;
using Qavren.Edge.Sqlite.Fts;
using MEVD = Microsoft.Extensions.VectorData;

namespace Qavren.Edge.VectorData;

/// <summary>Store-wide defaults. Every value here decides what SQL is emitted or how results are ordered.</summary>
public sealed class EdgeVectorStoreOptions
{
    /// <summary>The sub-project 1 keyed-database name. Null = the unnamed database.</summary>
    public string? DatabaseName { get; set; }

    /// <summary>
    /// Store-level default; overridable per collection and per vector property. Left null (the
    /// default), <c>AddVectorStore</c> fills it from DI with the registered non-generic
    /// <see cref="IEmbeddingGenerator"/>. Set it explicitly only to override that, or when
    /// constructing the store by hand outside a container.
    /// </summary>
    public IEmbeddingGenerator? EmbeddingGenerator { get; set; }

    /// <summary>
    /// Store-level default for the generator that embeds a search <c>searchValue</c>. Left null,
    /// <c>AddVectorStore</c> fills it with the document generator's
    /// <see cref="EdgeVectorData.QueryGeneratorServiceKey"/> sibling, falling back to the document
    /// generator when it exposes none. Overridable per collection.
    /// </summary>
    public IEmbeddingGenerator? QueryEmbeddingGenerator { get; set; }

    /// <summary><c>{0}</c> = collection name.</summary>
    public string VectorTableNameFormat { get; set; } = "{0}_vec";

    /// <summary><c>{0}</c> = collection name.</summary>
    public string FullTextTableNameFormat { get; set; } = "{0}_fts";

    /// <summary>
    /// vec0 <c>chunk_size</c>: 0 &lt; N &lt;= 4096 and N % 8 == 0 (sub-project 1's <c>VecTable</c>
    /// enforces both). 256, not sqlite-vec's 1024: chunk_size is BOTH the storage unit and the KNN
    /// scan allocation (chunk_size * dims * 4 bytes per chunk read), and a phone corpus is
    /// thousands of rows, not millions.
    /// </summary>
    public int ChunkSize { get; set; } = 256;

    /// <summary>
    /// Half of one decision with <see cref="FullTextRemoveDiacritics"/>: together they render as
    /// <c>tokenize='unicode61 remove_diacritics 2'</c> in the FTS5 DDL.
    /// </summary>
    public FtsTokenizer FullTextTokenizer { get; set; } = FtsTokenizer.Unicode61;

    /// <summary>
    /// 0 | 1 | 2. Default 2: the only value that folds diacritics correctly across the full Unicode
    /// range. 1 is the legacy mode that mishandles several Latin-1 ranges, and 0 leaves accented
    /// and unaccented spellings of the same word as different tokens.
    /// </summary>
    public int FullTextRemoveDiacritics { get; set; } = 2;

    /// <summary>How hybrid-search keywords are joined. <see cref="VectorData.KeywordCombinator.Or"/> by default.</summary>
    public KeywordCombinator KeywordCombinator { get; set; } = KeywordCombinator.Or;

    /// <summary>Reciprocal-rank-fusion defaults for hybrid search.</summary>
    public RrfDefaults Rrf { get; } = new();

    /// <summary>Counting rows is one query per collection; off by default.</summary>
    public bool IncludeRowCountsInDiagnostics { get; set; }
}

/// <summary>
/// The four numbers that compose the fused hybrid score. Getting one of them wrong produces a
/// working query with wrong results, which is why each is transcribed and asserted.
/// </summary>
public sealed class RrfDefaults
{
    /// <summary>
    /// SQLITE_VEC_VEC0_K_MAX. Neither lane may ask vec0 for more rows than this, so it is also the
    /// cap on the candidate count.
    /// </summary>
    public const int MaxCandidateCount = 4096;

    /// <summary>The RRF smoothing constant. 60 is the value sqlite-vec's own reference uses.</summary>
    public int K { get; set; } = 60;

    /// <summary>Weight applied to the vector lane's reciprocal rank. 1.0 = unweighted fusion.</summary>
    public double VectorWeight { get; set; } = 1.0;

    /// <summary>Weight applied to the keyword lane's reciprocal rank. 1.0 = unweighted fusion.</summary>
    public double KeywordWeight { get; set; } = 1.0;

    /// <summary>
    /// Each lane fetches <c>(top + skip) * this</c>, capped at <see cref="MaxCandidateCount"/>.
    /// Fewer candidates per lane and a row that ranks 5th in one lane and 40th in the other never
    /// enters the fusion at all - the failure mode RRF exists to avoid.
    /// </summary>
    public int CandidateMultiplier { get; set; } = 4;

    /// <summary>
    /// The value bound to <c>$cand</c> in
    /// <see cref="EdgeVectorSchema.BuildHybridRrfSql(bool, bool)"/>:
    /// <c>(top + skip) * CandidateMultiplier</c>, capped at <see cref="MaxCandidateCount"/>.
    /// </summary>
    /// <param name="top">The number of rows the caller asked for.</param>
    /// <param name="skip">The number of rows the caller asked to skip.</param>
    /// <returns>The per-lane candidate count.</returns>
    public int ResolveCandidateCount(int top, int skip)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(top);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);

        var wanted = (long)(top + skip) * CandidateMultiplier;
        return wanted > MaxCandidateCount ? MaxCandidateCount : (int)wanted;
    }
}

/// <summary>Per-collection overrides. Every null member falls back to the store-level default.</summary>
public sealed class EdgeVectorStoreCollectionOptions : MEVD.VectorStoreCollectionOptions
{
    /// <summary>Overrides <see cref="EdgeVectorStoreOptions.VectorTableNameFormat"/> for this collection.</summary>
    public string? VectorTableName { get; set; }

    /// <summary>Overrides <see cref="EdgeVectorStoreOptions.FullTextTableNameFormat"/> for this collection.</summary>
    public string? FullTextTableName { get; set; }

    /// <summary>Overrides <see cref="EdgeVectorStoreOptions.ChunkSize"/> for this collection.</summary>
    public int? ChunkSize { get; set; }

    /// <summary>Overrides <see cref="EdgeVectorStoreOptions.FullTextTokenizer"/> for this collection.</summary>
    public FtsTokenizer? FullTextTokenizer { get; set; }

    /// <summary>Overrides <see cref="EdgeVectorStoreOptions.KeywordCombinator"/> for this collection.</summary>
    public KeywordCombinator? KeywordCombinator { get; set; }

    /// <summary>
    /// Carries <see cref="EdgeVectorStoreOptions.FullTextRemoveDiacritics"/> down from the store
    /// when <see cref="EdgeVectorStore"/> builds a collection. Internal on purpose: it is a store
    /// -level decision, not a per-collection one, and spec 8 lists no public member for it.
    /// </summary>
    internal int? RemoveDiacritics { get; set; }

    /// <summary>Overrides <see cref="EdgeVectorStoreOptions.Rrf"/> for this collection.</summary>
    public RrfDefaults? Rrf { get; set; }

    /// <summary>Create the FTS5 sidecar even with no full-text property. Default false.</summary>
    public bool AlwaysCreateFullTextIndex { get; set; }

    /// <summary>
    /// Embeds the <c>searchValue</c> of a search when it must differ from the generator that
    /// embedded the stored records. Null resolves the document generator's
    /// <see cref="EdgeVectorData.QueryGeneratorServiceKey"/> sibling. The only correct way to drive
    /// a model with asymmetric prefixes, because MEVD always calls <c>GenerateAsync</c> with NO
    /// options.
    /// </summary>
    public IEmbeddingGenerator? QueryEmbeddingGenerator { get; set; }
}

/// <summary>
/// Per-query RRF tuning. A caller passing the plain MEVD <c>HybridSearchOptions&lt;TRecord&gt;</c>
/// gets the store defaults and never sees this type.
/// </summary>
/// <typeparam name="TRecord">The record type of the collection being searched.</typeparam>
public sealed class EdgeHybridSearchOptions<TRecord> : MEVD.HybridSearchOptions<TRecord>
{
    /// <summary>Overrides <see cref="RrfDefaults.K"/> for this query.</summary>
    public int? RrfK { get; set; }

    /// <summary>Overrides <see cref="RrfDefaults.VectorWeight"/> for this query.</summary>
    public double? VectorWeight { get; set; }

    /// <summary>Overrides <see cref="RrfDefaults.KeywordWeight"/> for this query.</summary>
    public double? KeywordWeight { get; set; }

    /// <summary>
    /// Rows pulled from each lane before fusion. Null =
    /// <c>(top + Skip) * </c><see cref="RrfDefaults.CandidateMultiplier"/>.
    /// </summary>
    public int? CandidateCount { get; set; }

    /// <summary>Overrides <see cref="EdgeVectorStoreOptions.KeywordCombinator"/> for this query.</summary>
    public KeywordCombinator? KeywordCombinator { get; set; }
}
