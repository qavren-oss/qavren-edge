using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace Qavren.Edge.VectorData.Tests.Records;

/// <summary>A collection with no full-text property: the FTS5 sidecar and its triggers disappear.</summary>
public sealed class PlainNote
{
    [VectorStoreKey]
    public string Key { get; set; } = "";

    [VectorStoreData(IsIndexed = true)]
    public string? Tag { get; set; }

    [VectorStoreVector(384, DistanceFunction = DistanceFunction.CosineDistance)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>One dimension past SQLITE_VEC_VEC0_MAX_DIMENSIONS.</summary>
public sealed class WideVector
{
    [VectorStoreKey]
    public string Key { get; set; } = "";

    [VectorStoreVector(8193)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>Exactly SQLITE_VEC_VEC0_MAX_DIMENSIONS.</summary>
public sealed class EdgeWidthVector
{
    [VectorStoreKey]
    public string Key { get; set; } = "";

    [VectorStoreVector(8192)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>A record whose property is literally called <c>_rowid</c>.</summary>
public sealed class RowIdNamedRecord
{
    [VectorStoreKey]
    public string Key { get; set; } = "";

#pragma warning disable CA1707 // underscore is the whole point of this fixture
    [VectorStoreData]
    public string? _rowid { get; set; }
#pragma warning restore CA1707

    [VectorStoreVector(4)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>A record that asks for the reserved storage name explicitly.</summary>
public sealed class RowIdStorageNamedRecord
{
    [VectorStoreKey]
    public string Key { get; set; } = "";

    [VectorStoreData(StorageName = "_rowid")]
    public string? Label { get; set; }

    [VectorStoreVector(4)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>A KEY that asks for the reserved storage name. MEVD's own option would have allowed this.</summary>
public sealed class RowIdKeyRecord
{
    [VectorStoreKey(StorageName = "_rowid")]
    public string Key { get; set; } = "";

    [VectorStoreVector(4)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>A data property of a type SQLite has no column for.</summary>
public sealed class UnsupportedDataTypeRecord
{
    [VectorStoreKey]
    public string Key { get; set; } = "";

    [VectorStoreData]
    public Uri? Link { get; set; }

    [VectorStoreVector(4)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>A key of a type this provider cannot store.</summary>
public sealed class UnsupportedKeyTypeRecord
{
    [VectorStoreKey]
    public double Key { get; set; }

    [VectorStoreVector(4)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>A distance function vec0 cannot compute.</summary>
public sealed class UnsupportedDistanceRecord
{
    [VectorStoreKey]
    public string Key { get; set; } = "";

    [VectorStoreVector(4, DistanceFunction = DistanceFunction.DotProductSimilarity)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>A nullable vector property: vec0 reads SQL NULL on a vector column as "no change".</summary>
public sealed class NullableVectorRecord
{
    [VectorStoreKey]
    public string Key { get; set; } = "";

    [VectorStoreVector(4)]
    public ReadOnlyMemory<float>? Embedding { get; set; }
}

/// <summary>Two vectors means two vec0 tables and a materially worse hybrid query.</summary>
public sealed class TwoVectorRecord
{
    [VectorStoreKey]
    public string Key { get; set; } = "";

    [VectorStoreVector(4)]
    public ReadOnlyMemory<float> First { get; set; }

    [VectorStoreVector(4)]
    public ReadOnlyMemory<float> Second { get; set; }
}

/// <summary>An index kind vec0 does not have. Accepted, ignored, and logged (event 804).</summary>
public sealed class HnswRecord
{
    [VectorStoreKey]
    public string Key { get; set; } = "";

    [VectorStoreVector(4, IndexKind = IndexKind.Hnsw)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>Every data type the provider claims to store, one column each.</summary>
public sealed class TypedRecord
{
    [VectorStoreKey]
    public Guid Key { get; set; }

    [VectorStoreData]
    public int Count { get; set; }

    [VectorStoreData]
    public long Size { get; set; }

    [VectorStoreData]
    public short Small { get; set; }

    [VectorStoreData]
    public bool Flag { get; set; }

    [VectorStoreData]
    public float Ratio { get; set; }

    [VectorStoreData]
    public double Precise { get; set; }

    [VectorStoreData]
    public string? Text { get; set; }

    [VectorStoreData]
    public Guid Correlation { get; set; }

    [VectorStoreData]
    public DateTime Created { get; set; }

    [VectorStoreData]
    public DateTimeOffset Updated { get; set; }

    [VectorStoreData]
    public DateOnly Day { get; set; }

    [VectorStoreData]
    public TimeOnly Time { get; set; }

    [VectorStoreData]
    public byte[]? Payload { get; set; }

    [VectorStoreVector(4)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>The record the filter tests translate expressions against.</summary>
public sealed class FilterRecord
{
    [VectorStoreKey]
    public string Key { get; set; } = "";

    [VectorStoreData(IsIndexed = true)]
    public string? Tag { get; set; }

    [VectorStoreData]
    public string Title { get; set; } = "";

    [VectorStoreData]
    public int Count { get; set; }

    [VectorStoreData]
    public bool Flag { get; set; }

    /// <summary>Carries no attribute, so it maps to no column and no filter may name it.</summary>
    public string Unmapped { get; set; } = "";

    [VectorStoreVector(4)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>
/// A generator that returns zero vectors of the requested width. Model building only asks it what
/// it can produce; nothing here is ever executed against a database.
/// </summary>
public sealed class FakeStringEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly int _dimensions;

    /// <summary>Creates the generator.</summary>
    /// <param name="dimensions">The width of the embeddings it claims to produce.</param>
    public FakeStringEmbeddingGenerator(int dimensions) => _dimensions = dimensions;

    /// <inheritdoc />
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            values.Select(_ => new Embedding<float>(new float[_dimensions]))));
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
