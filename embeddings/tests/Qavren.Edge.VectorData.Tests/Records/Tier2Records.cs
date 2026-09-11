using Microsoft.Extensions.VectorData;

namespace Qavren.Edge.VectorData.Tests.Records;

/// <summary>
/// What the KNN-vs-brute-force theory drives at both widths. The two widths need two record types
/// because <c>[VectorStoreVector(n)]</c> takes a compile-time constant, and the theory needs one
/// body, so the two share this interface and the test is generic over it.
/// </summary>
public interface IPrecomputedVectorRecord
{
    /// <summary>The key.</summary>
    string Key { get; set; }

    /// <summary>The pre-computed vector: no generator is ever on this path.</summary>
    ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>384-d, the width every shipped MiniLM/BGE preset emits.</summary>
public sealed class Vec384 : IPrecomputedVectorRecord
{
    /// <inheritdoc />
    [VectorStoreKey]
    public string Key { get; set; } = "";

    /// <inheritdoc />
    [VectorStoreVector(384, DistanceFunction = DistanceFunction.CosineDistance)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>
/// 768-d, which is where the wide-vector path is exercised. Tier 1 has no 768-d fixture and cannot
/// cheaply have one - the fixture's output width is the width of its <c>Gather</c> initializer, so
/// the smallest conceivable 768-wide float32 table is already 6,144 bytes of initializer - and
/// pre-computed vectors make the width free here.
/// </summary>
public sealed class Vec768 : IPrecomputedVectorRecord
{
    /// <inheritdoc />
    [VectorStoreKey]
    public string Key { get; set; } = "";

    /// <inheritdoc />
    [VectorStoreVector(768, DistanceFunction = DistanceFunction.CosineDistance)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>
/// A full-text property AND a pre-computed vector, which is the only combination that can assert
/// <c>IncludeVectors</c> on <c>HybridSearchAsync</c>: a model that generates its embeddings from a
/// source property has no vector property to read them back into, and the collection rejects
/// <c>IncludeVectors</c> up front for exactly that reason.
/// </summary>
public sealed class FtsVec
{
    /// <summary>The key.</summary>
    [VectorStoreKey]
    public string Key { get; set; } = "";

    /// <summary>The keyword lane's only column.</summary>
    [VectorStoreData(IsFullTextIndexed = true)]
    public string Body { get; set; } = "";

    /// <summary>The pre-computed vector, four wide so a reader can check the blob by hand.</summary>
    [VectorStoreVector(4, DistanceFunction = DistanceFunction.CosineDistance)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>
/// Spec 4.3's <c>Note</c> with one number changed: four dimensions rather than 384, because the
/// happy-path test runs the REAL <c>AddOnnxEmbeddings</c> path over the committed tier-1 fixture
/// graph, whose <c>Gather</c> initializer is 16 x 4. Nothing else about it differs - a string
/// source property, two full-text properties and one indexed data property - so the four builder
/// calls under test are the four spec 4.3 prints.
/// </summary>
public sealed class TinyNote
{
    /// <summary>The key.</summary>
    [VectorStoreKey]
    public string Key { get; set; } = "";

    /// <summary>An indexed data property, as in spec 4.3.</summary>
    [VectorStoreData(IsIndexed = true)]
    public string? Tag { get; set; }

    /// <summary>A full-text property, as in spec 4.3.</summary>
    [VectorStoreData(IsFullTextIndexed = true)]
    public string Title { get; set; } = "";

    /// <summary>A full-text property, as in spec 4.3.</summary>
    [VectorStoreData(IsFullTextIndexed = true)]
    public string Body { get; set; } = "";

    /// <summary>The string source property the registered generator has to fill.</summary>
    [VectorStoreVector(4, DistanceFunction = DistanceFunction.CosineDistance)]
    public string? Embedding => Body;
}
