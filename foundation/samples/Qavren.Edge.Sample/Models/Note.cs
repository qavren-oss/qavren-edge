using Microsoft.Extensions.VectorData;

namespace Qavren.Edge.Sample.Models;

/// <summary>
/// The spec 4.3 record, verbatim (see the design doc's "consumer's four calls" section and
/// <c>Qavren.Edge.VectorData.Tests.Records.Note</c>, which asserts the same shape byte-for-byte
/// against golden SQL). Named <c>embedding_notes</c> in the sample's schema - not <c>notes</c> -
/// because SP1's sample already owns a plain <c>notes</c> table (see
/// <see cref="Migrations.M001_CreateNotes"/>) with a different shape entirely (int key, no vec0/FTS5
/// via this pipeline). The two coexist in one database without collision.
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
    public string? Embedding => Body; // string source -> the generator fills it
}
