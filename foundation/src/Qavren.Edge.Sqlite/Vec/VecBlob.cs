using System.Buffers.Binary;

namespace Qavren.Edge.Sqlite.Vec;

/// <summary>
/// Encodes vectors the way sqlite-vec expects them: a little-endian float32 blob for
/// <c>float[N]</c>, a signed-byte blob for <c>int8[N]</c>, and a bit-packed blob for <c>bit[N]</c>.
/// </summary>
public static class VecBlob
{
    public static byte[] From(ReadOnlySpan<float> values)
    {
        var blob = new byte[values.Length * sizeof(float)];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(blob.AsSpan(i * sizeof(float)), values[i]);
        }

        return blob;
    }

    public static float[] ToFloats(ReadOnlySpan<byte> blob)
    {
        if (blob.Length % sizeof(float) != 0)
        {
            throw new ArgumentException(
                $"A float32 vector blob length must be a multiple of {sizeof(float)}; got {blob.Length}.",
                nameof(blob));
        }

        var values = new float[blob.Length / sizeof(float)];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(blob[(i * sizeof(float))..]);
        }

        return values;
    }

    public static byte[] FromInt8(ReadOnlySpan<sbyte> values)
    {
        var blob = new byte[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            blob[i] = unchecked((byte)values[i]);
        }

        return blob;
    }

    public static sbyte[] ToInt8(ReadOnlySpan<byte> blob)
    {
        var values = new sbyte[blob.Length];
        for (var i = 0; i < blob.Length; i++)
        {
            values[i] = unchecked((sbyte)blob[i]);
        }

        return values;
    }

    /// <summary>Least significant bit first within each byte, matching <c>vec_bit()</c>.</summary>
    public static byte[] FromBits(ReadOnlySpan<bool> values)
    {
        if (values.Length % 8 != 0)
        {
            throw new ArgumentException(
                $"A bit vector length must be a multiple of 8; got {values.Length}.", nameof(values));
        }

        var blob = new byte[values.Length / 8];
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i])
            {
                blob[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        return blob;
    }

    public static bool[] ToBits(ReadOnlySpan<byte> blob)
    {
        var values = new bool[blob.Length * 8];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (blob[i / 8] & (1 << (i % 8))) != 0;
        }

        return values;
    }
}
