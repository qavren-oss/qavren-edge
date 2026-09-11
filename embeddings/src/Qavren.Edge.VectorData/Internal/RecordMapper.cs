using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData.ProviderServices;
using Qavren.Edge.Sqlite.Vec;

namespace Qavren.Edge.VectorData.Internal;

/// <summary>
/// Moves values between a record and a SQLite row. It is the only file in this package that
/// touches a vector's bytes, and it does so exclusively through sub-project 1's
/// <see cref="VecBlob"/>.
/// <para>
/// <b>The vector encoding is SP1's and SP2 writes no blob code at all.</b> Spec 12.2: "Storage is
/// the raw little-endian float32 blob through SP1's <c>VecBlob.From</c>/<c>ToFloats</c> - exactly
/// vec0's wire format, and zero new blob code in SP2." So writing goes through
/// <see cref="VecBlob.From(ReadOnlySpan{float})"/> and reading through
/// <see cref="VecBlob.ToFloats(ReadOnlySpan{byte})"/>, whose <see cref="ArgumentException"/> on a
/// length that is not a multiple of four is the only length validation this package needs.
/// </para>
/// <para>
/// Forbidden here, and the prohibition is the point: no <c>BitConverter.GetBytes</c> loop, no
/// <c>MemoryMarshal.AsBytes</c>/<c>Cast&lt;float, byte&gt;</c>, no
/// <c>BinaryPrimitives.WriteSingleLittleEndian</c> and no <c>unsafe</c> reinterpret. Several of
/// those are correct on every RID this suite targets, which is exactly why the rule is written
/// down - a hand-rolled encoder passes a round-trip test against its own decoder and quietly
/// becomes a second definition of vec0's wire format. <c>MemoryMarshal.Cast</c> in particular
/// looks like an optimisation and silently encodes host-endian rather than little-endian.
/// </para>
/// </summary>
internal static class RecordMapper
{
    /// <summary>
    /// Converts whatever a vector property holds into the floats vec0 stores. Accepts the three
    /// pre-computed shapes plus the <see cref="Embedding{T}"/> MEVD's dispatcher produces from a
    /// source property.
    /// </summary>
    /// <param name="value">The vector property's value.</param>
    /// <param name="vector">The floats.</param>
    /// <returns><see langword="true"/> when the value was one of the known vector shapes.</returns>
    public static bool TryGetVector(object? value, out ReadOnlyMemory<float> vector)
    {
        switch (value)
        {
            case ReadOnlyMemory<float> memory:
                vector = memory;
                return true;
            case Memory<float> memory:
                vector = memory;
                return true;
            case float[] array:
                vector = array;
                return true;
            case Embedding<float> embedding:
                vector = embedding.Vector;
                return true;
            default:
                vector = default;
                return false;
        }
    }

    /// <summary>Encodes a vector exactly as sub-project 1 does. The only write path for vec0 bytes.</summary>
    /// <param name="vector">The floats.</param>
    /// <returns>The raw little-endian float32 blob.</returns>
    public static byte[] EncodeVector(ReadOnlyMemory<float> vector) => VecBlob.From(vector.Span);

    /// <summary>Decodes a vec0 blob exactly as sub-project 1 does. The only read path for vec0 bytes.</summary>
    /// <param name="blob">The stored blob.</param>
    /// <returns>The floats.</returns>
    public static float[] DecodeVector(ReadOnlySpan<byte> blob) => VecBlob.ToFloats(blob);

    /// <summary>Binds one data or key value, mapping <see langword="null"/> onto <see cref="DBNull"/>.</summary>
    /// <param name="name">The parameter name, including its leading <c>$</c>.</param>
    /// <param name="value">The CLR value.</param>
    /// <returns>The parameter.</returns>
    public static SqliteParameter Parameter(string name, object? value) =>
        new(name, value ?? DBNull.Value);

    /// <summary>
    /// Reads one column back into its CLR type. Microsoft.Data.Sqlite owns every conversion, so a
    /// value written by this provider and one written by an app's own SQL read back identically.
    /// The switch is over <c>typeof</c> tokens rather than reflection, so it is AOT-safe.
    /// </summary>
    /// <param name="reader">The open reader.</param>
    /// <param name="ordinal">The column ordinal.</param>
    /// <param name="clrType">The property's CLR type.</param>
    /// <returns>The value, or <see langword="null"/> when the column is NULL.</returns>
    public static object? ReadValue(SqliteDataReader reader, int ordinal, Type clrType)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(clrType);

        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (type == typeof(int))
        {
            return reader.GetInt32(ordinal);
        }

        if (type == typeof(long))
        {
            return reader.GetInt64(ordinal);
        }

        if (type == typeof(short))
        {
            return reader.GetInt16(ordinal);
        }

        if (type == typeof(bool))
        {
            return reader.GetBoolean(ordinal);
        }

        if (type == typeof(float))
        {
            return reader.GetFloat(ordinal);
        }

        if (type == typeof(double))
        {
            return reader.GetDouble(ordinal);
        }

        if (type == typeof(string))
        {
            return reader.GetString(ordinal);
        }

        if (type == typeof(Guid))
        {
            return reader.GetGuid(ordinal);
        }

        if (type == typeof(DateTime))
        {
            return reader.GetDateTime(ordinal);
        }

        if (type == typeof(DateTimeOffset))
        {
            return reader.GetFieldValue<DateTimeOffset>(ordinal);
        }

        if (type == typeof(DateOnly))
        {
            return reader.GetFieldValue<DateOnly>(ordinal);
        }

        if (type == typeof(TimeOnly))
        {
            return reader.GetFieldValue<TimeOnly>(ordinal);
        }

        if (type == typeof(byte[]))
        {
            return reader.GetFieldValue<byte[]>(ordinal);
        }

        throw new NotSupportedException(
            $"This provider cannot read a column back into '{type}'. Supported types: {SqliteTypeMap.SupportedDataTypes}.");
    }

    /// <summary>
    /// Writes a decoded vector onto a record's vector property, in whichever of the three
    /// pre-computed shapes the property declares. A source property (a <see cref="string"/> whose
    /// embedding is generated) has nowhere to put it and is left alone; the caller rejects
    /// <c>IncludeVectors</c> on such a model before reaching here.
    /// </summary>
    /// <param name="property">The vector property model.</param>
    /// <param name="record">The record being materialised.</param>
    /// <param name="vector">The decoded floats.</param>
    public static void SetVector(VectorPropertyModel property, object record, float[] vector)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(record);

        var type = Nullable.GetUnderlyingType(property.Type) ?? property.Type;

        if (type == typeof(ReadOnlyMemory<float>))
        {
            property.SetValueAsObject(record, new ReadOnlyMemory<float>(vector));
        }
        else if (type == typeof(float[]))
        {
            property.SetValueAsObject(record, vector);
        }
        else if (type == typeof(Embedding<float>))
        {
            property.SetValueAsObject(record, new Embedding<float>(vector));
        }
    }
}
