using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Qavren.Edge.Sqlite.Vec;

public enum VecMetric
{
    L2,
    L1,
    Cosine,
}

[SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "float32/int8/bit are sqlite-vec's own column element-type spellings; renaming them would obscure the mapping to the vec0 DDL.")]
public enum VecElementType
{
    Float32,
    Int8,
    Bit,
}

/// <summary>An auxiliary column, prefixed with <c>+</c>. Selectable, never filterable in a KNN WHERE clause.</summary>
public sealed record VecAuxColumn(string Name, string Type);

/// <summary>A metadata column. Filterable with = != &gt; &gt;= &lt; &lt;= only (boolean: = != only).</summary>
public sealed record VecMetadataColumn(string Name, string Type);

/// <summary>A partition key. Pre-filters shards on <c>=</c> constraints.</summary>
public sealed record VecPartitionKey(string Name, string Type);

/// <summary>Emits the <c>vec0</c> DDL a consumer could have written, with the caps enforced up front.</summary>
public static class VecTable
{
    private const int MaxDimensions = 8192;          // SQLITE_VEC_VEC0_MAX_DIMENSIONS
    private const int MaxChunkSize = 4096;           // SQLITE_VEC_CHUNK_SIZE_MAX
    private const int MaxMetadataColumns = 16;
    private const int MaxAuxColumns = 16;
    private const int MaxPartitionKeys = 4;

    public static string BuildCreateSql(
        string name,
        int dims,
        VecMetric? metric = VecMetric.Cosine,
        VecElementType elementType = VecElementType.Float32,
        IEnumerable<VecAuxColumn>? aux = null,
        IEnumerable<VecMetadataColumn>? metadata = null,
        IEnumerable<VecPartitionKey>? partitions = null,
        int? chunkSize = null,
        string vectorColumn = "embedding")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(vectorColumn);
        ArgumentOutOfRangeException.ThrowIfLessThan(dims, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dims, MaxDimensions);

        if (elementType == VecElementType.Bit && metric is not null)
        {
            // sqlite-vec rejects distance_metric on bit columns outright (constructor error);
            // bit vectors are hamming-distance only.
            throw new ArgumentException(
                "sqlite-vec rejects distance_metric on a bit column; bit vectors use hamming distance only. Pass metric: null.",
                nameof(metric));
        }

        if (chunkSize is { } chunk && (chunk <= 0 || chunk > MaxChunkSize || chunk % 8 != 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunkSize),
                chunkSize,
                $"chunk_size must satisfy 0 < N <= {MaxChunkSize.ToString(CultureInfo.InvariantCulture)} and N % 8 == 0.");
        }

        var auxList = aux?.ToArray() ?? [];
        var metadataList = metadata?.ToArray() ?? [];
        var partitionList = partitions?.ToArray() ?? [];

        ArgumentOutOfRangeException.ThrowIfGreaterThan(auxList.Length, MaxAuxColumns, nameof(aux));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(metadataList.Length, MaxMetadataColumns, nameof(metadata));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(partitionList.Length, MaxPartitionKeys, nameof(partitions));

        var parts = new List<string>
        {
            elementType switch
            {
                VecElementType.Float32 => $"{vectorColumn} float[{dims.ToString(CultureInfo.InvariantCulture)}]",
                VecElementType.Int8 => $"{vectorColumn} int8[{dims.ToString(CultureInfo.InvariantCulture)}]",
                VecElementType.Bit => $"{vectorColumn} bit[{dims.ToString(CultureInfo.InvariantCulture)}]",
                _ => throw new ArgumentOutOfRangeException(nameof(elementType)),
            }
            + (metric is null ? string.Empty : " distance_metric=" + MetricToken(metric.Value)),
        };

        parts.AddRange(partitionList.Select(p => $"{p.Name} {p.Type} partition key"));
        parts.AddRange(metadataList.Select(m => $"{m.Name} {m.Type}"));
        parts.AddRange(auxList.Select(a => $"+{a.Name} {a.Type}"));

        if (chunkSize is { } size)
        {
            parts.Add("chunk_size=" + size.ToString(CultureInfo.InvariantCulture));
        }

        var sb = new StringBuilder("CREATE VIRTUAL TABLE IF NOT EXISTS \"");
        sb.Append(name).Append("\" USING vec0(").Append(string.Join(", ", parts)).Append(')');
        return sb.ToString();
    }

    public static async Task CreateAsync(
        SqliteConnection connection,
        string name,
        int dims,
        VecMetric? metric = VecMetric.Cosine,
        VecElementType elementType = VecElementType.Float32,
        IEnumerable<VecAuxColumn>? aux = null,
        IEnumerable<VecMetadataColumn>? metadata = null,
        IEnumerable<VecPartitionKey>? partitions = null,
        int? chunkSize = null,
        string vectorColumn = "embedding",
        CancellationToken cancellationToken = default)
    {
        var sql = BuildCreateSql(name, dims, metric, elementType, aux, metadata, partitions, chunkSize, vectorColumn);
        await connection.ExecuteAsync(sql, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string MetricToken(VecMetric metric) => metric switch
    {
        VecMetric.L2 => "l2",
        VecMetric.L1 => "l1",
        VecMetric.Cosine => "cosine",
        _ => throw new ArgumentOutOfRangeException(nameof(metric)),
    };
}
