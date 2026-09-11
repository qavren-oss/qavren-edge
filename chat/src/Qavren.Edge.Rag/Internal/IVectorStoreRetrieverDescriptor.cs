namespace Qavren.Edge.Rag.Internal;

/// <summary>
/// The two facts spec 14.3's report wants out of a <c>VectorStoreRetriever&lt;TKey, TRecord&gt;</c>,
/// in a shape a non-generic contributor can read. It exists so the diagnostics block needs no
/// reflection over an open generic - this suite is AOT-safe and a support report is not an excuse.
/// </summary>
internal interface IVectorStoreRetrieverDescriptor
{
    /// <summary>The MEVD collection the retriever searches.</summary>
    string CollectionName { get; }

    /// <summary>Whether the hybrid lane is preferred when the collection can serve one.</summary>
    bool PrefersHybridSearch { get; }
}
