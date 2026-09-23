using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Qavren.Edge.Sqlite.Vec;

/// <summary>sqlite-vec's <c>distance_metric</c> column option.</summary>
public enum VecMetric
{
    /// <summary>Euclidean (L2) distance.</summary>
    L2,

    /// <summary>Manhattan (L1) distance.</summary>
    L1,

    /// <summary>Cosine distance. The default for a float32/int8 column.</summary>
    Cosine,
}

/// <summary>sqlite-vec's column element types.</summary>
[SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "float32/int8/bit are sqlite-vec's own column element-type spellings; renaming them would obscure the mapping to the vec0 DDL.")]
public enum VecElementType
{
    /// <summary>32-bit floating point, sqlite-vec's <c>float</c> column type.</summary>
    Float32,

    /// <summary>8-bit signed integer, sqlite-vec's <c>int8</c> column type.</summary>
    Int8,

    /// <summary>Packed bits, sqlite-vec's <c>bit</c> column type. Hamming distance only; rejects a <see cref="VecMetric"/>.</summary>
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

    /// <summary>Builds the <c>CREATE VIRTUAL TABLE ... USING vec0(...)</c> statement for the given shape, enforcing sqlite-vec's own caps up front.</summary>
    /// <param name="name">The table name.</param>
    /// <param name="dims">The vector column's dimensionality (1 to 8192).</param>
    /// <param name="metric">The distance metric, or <see langword="null"/> for a <see cref="VecElementType.Bit"/> column, which rejects one.</param>
    /// <param name="elementType">The vector column's element type.</param>
    /// <param name="aux">Auxiliary columns, selectable but never filterable in a KNN <c>WHERE</c> clause.</param>
    /// <param name="metadata">Metadata columns, filterable with comparison operators.</param>
    /// <param name="partitions">Partition keys, pre-filtered with <c>=</c> constraints.</param>
    /// <param name="chunkSize">The <c>chunk_size</c> option; must be a positive multiple of 8 up to 4096, or <see langword="null"/> to leave it unset.</param>
    /// <param name="vectorColumn">The vector column's name.</param>
    /// <returns>The complete <c>CREATE VIRTUAL TABLE</c> statement.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="vectorColumn"/> is null, empty, or whitespace; or <paramref name="metric"/> is set on a <see cref="VecElementType.Bit"/> column.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dims"/>, <paramref name="chunkSize"/>, or the size of <paramref name="aux"/>, <paramref name="metadata"/> or <paramref name="partitions"/> is out of range.</exception>
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

    /// <summary>Builds the table's DDL with <see cref="BuildCreateSql"/> and executes it on <paramref name="connection"/>.</summary>
    /// <param name="connection">The open connection to create the table on.</param>
    /// <param name="name">The table name.</param>
    /// <param name="dims">The vector column's dimensionality (1 to 8192).</param>
    /// <param name="metric">The distance metric, or <see langword="null"/> for a <see cref="VecElementType.Bit"/> column, which rejects one.</param>
    /// <param name="elementType">The vector column's element type.</param>
    /// <param name="aux">Auxiliary columns, selectable but never filterable in a KNN <c>WHERE</c> clause.</param>
    /// <param name="metadata">Metadata columns, filterable with comparison operators.</param>
    /// <param name="partitions">Partition keys, pre-filtered with <c>=</c> constraints.</param>
    /// <param name="chunkSize">The <c>chunk_size</c> option; must be a positive multiple of 8 up to 4096, or <see langword="null"/> to leave it unset.</param>
    /// <param name="vectorColumn">The vector column's name.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="vectorColumn"/> is null, empty, or whitespace; or <paramref name="metric"/> is set on a <see cref="VecElementType.Bit"/> column.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dims"/>, <paramref name="chunkSize"/>, or the size of <paramref name="aux"/>, <paramref name="metadata"/> or <paramref name="partitions"/> is out of range.</exception>
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
