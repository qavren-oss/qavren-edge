using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

namespace Qavren.Edge.VectorData.Internal;

/// <summary>
/// The CLR-to-SQLite type mapping, and the only place the supported sets are written down. The
/// column affinities match what Microsoft.Data.Sqlite reads back, so a round trip never needs a
/// converter.
/// </summary>
public static class SqliteTypeMap
{
    /// <summary>The data-property types this provider stores, rendered for an error message.</summary>
    public const string SupportedDataTypes =
        "int, long, short, bool, float, double, string, Guid, DateTime, DateTimeOffset, DateOnly, TimeOnly, byte[]";

    /// <summary>The key types this provider supports, rendered for an error message.</summary>
    public const string SupportedKeyTypes = "int, long, string, Guid";

    /// <summary>The vector-property types this provider supports, rendered for an error message.</summary>
    public const string SupportedVectorTypes =
        "ReadOnlyMemory<float>, Embedding<float>, float[], or string/DataContent with an embedding generator configured";

    /// <summary>
    /// The SQLite column type for a data or key property.
    /// </summary>
    /// <param name="type">The CLR type, nullable wrapper already unwrapped or not.</param>
    /// <param name="sqliteType">The column type: INTEGER, REAL, TEXT or BLOB.</param>
    /// <returns><see langword="true"/> when the type is supported.</returns>
    public static bool TryGetColumnType(Type type, [NotNullWhen(true)] out string? sqliteType)
    {
        ArgumentNullException.ThrowIfNull(type);

        var unwrapped = Nullable.GetUnderlyingType(type) ?? type;

        sqliteType = unwrapped switch
        {
            _ when unwrapped == typeof(int) => "INTEGER",
            _ when unwrapped == typeof(long) => "INTEGER",
            _ when unwrapped == typeof(short) => "INTEGER",
            _ when unwrapped == typeof(bool) => "INTEGER",
            _ when unwrapped == typeof(float) => "REAL",
            _ when unwrapped == typeof(double) => "REAL",
            _ when unwrapped == typeof(string) => "TEXT",
            _ when unwrapped == typeof(Guid) => "TEXT",
            _ when unwrapped == typeof(DateTime) => "TEXT",
            _ when unwrapped == typeof(DateTimeOffset) => "TEXT",
            _ when unwrapped == typeof(DateOnly) => "TEXT",
            _ when unwrapped == typeof(TimeOnly) => "TEXT",
            _ when unwrapped == typeof(byte[]) => "BLOB",
            _ => null,
        };

        return sqliteType is not null;
    }

    /// <summary>Whether a data property of this CLR type can be stored.</summary>
    /// <param name="type">The CLR type.</param>
    /// <returns><see langword="true"/> when the type is supported.</returns>
    public static bool IsSupportedDataType(Type type) => TryGetColumnType(type, out _);

    /// <summary>Whether a key property of this CLR type is supported.</summary>
    /// <param name="type">The CLR type.</param>
    /// <returns><see langword="true"/> when the type is supported.</returns>
    public static bool IsSupportedKeyType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var unwrapped = Nullable.GetUnderlyingType(type) ?? type;
        return unwrapped == typeof(int)
            || unwrapped == typeof(long)
            || unwrapped == typeof(string)
            || unwrapped == typeof(Guid);
    }

    /// <summary>
    /// Whether a key of this CLR type can be generated. <see cref="Guid"/> is generated client-side
    /// with <see cref="Guid.CreateVersion7()"/> - time-ordered, so TEXT keys cluster; int and long
    /// are left to SQLite's rowid and read back with <c>RETURNING</c>.
    /// </summary>
    /// <param name="type">The CLR type.</param>
    /// <returns><see langword="true"/> when auto-generation is supported.</returns>
    public static bool IsAutoGeneratableKeyType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var unwrapped = Nullable.GetUnderlyingType(type) ?? type;
        return unwrapped == typeof(Guid) || unwrapped == typeof(int) || unwrapped == typeof(long);
    }

    /// <summary>
    /// Whether a vector property of this CLR type is storable as a float32 vec0 blob. A
    /// <see cref="Nullable{T}"/> wrapper is accepted here and rejected separately with
    /// <see cref="EdgeErrorCode.NullableVectorProperty"/>, so the caller gets the specific
    /// diagnostic rather than "unsupported type".
    /// </summary>
    /// <param name="type">The CLR type.</param>
    /// <returns><see langword="true"/> when the type is a float32 vector shape.</returns>
    public static bool IsSupportedVectorType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var unwrapped = Nullable.GetUnderlyingType(type) ?? type;
        return unwrapped == typeof(ReadOnlyMemory<float>)
            || unwrapped == typeof(Embedding<float>)
            || unwrapped == typeof(float[]);
    }
}
