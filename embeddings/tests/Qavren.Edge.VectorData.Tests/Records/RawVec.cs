using Microsoft.Extensions.VectorData;

namespace Qavren.Edge.VectorData.Tests.Records;

/// <summary>
/// The only record in this suite with a <b>pre-computed</b> vector property. Nothing generates its
/// embedding, so the bytes on disk are the bytes <c>RecordMapper</c> wrote and nothing else - which
/// is what makes it the record the <c>VecBlob</c> proof is asserted against, and the record spec
/// 12.5's "a store used only with pre-computed vectors needs no generator" claim is tested with.
/// </summary>
public sealed class RawVec
{
    /// <summary>The key.</summary>
    [VectorStoreKey]
    public string Key { get; set; } = "";

    /// <summary>A four-wide pre-computed vector: no generator is ever on this path.</summary>
    [VectorStoreVector(4, DistanceFunction = DistanceFunction.CosineDistance)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
