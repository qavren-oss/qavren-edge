using Microsoft.Extensions.VectorData;

namespace Qavren.Edge.VectorData.Tests.Records;

/// <summary>
/// The spec 4.3 record, verbatim. Every golden SQL string in this test project is derived from it,
/// so a change here is a change to the provider's visible contract.
/// </summary>
public sealed class Note
{
    [VectorStoreKey]
    public string Key { get; set; } = "";

    [VectorStoreData(IsIndexed = true)]
    public string? Tag { get; set; }

    [VectorStoreData(IsFullTextIndexed = true)]
    public string Title { get; set; } = "";

    [VectorStoreData(IsFullTextIndexed = true)]
    public string Body { get; set; } = "";

    [VectorStoreVector(384, DistanceFunction = DistanceFunction.CosineDistance)]
    public string? Embedding => Body;          // string source -> the generator fills it
}
