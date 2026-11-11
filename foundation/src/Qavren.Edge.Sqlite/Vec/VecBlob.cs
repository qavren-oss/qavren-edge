using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Qavren.Edge.Sqlite.Vec;

/// <summary>
/// Encodes vectors the way sqlite-vec expects them: a little-endian float32 blob for
/// <c>float[N]</c>, a signed-byte blob for <c>int8[N]</c>, and a bit-packed blob for <c>bit[N]</c>.
/// </summary>
public static class VecBlob
{
    /// <summary>Encodes a <c>float[N]</c> vector as a little-endian float32 blob, as sqlite-vec expects.</summary>
    public static byte[] From(ReadOnlySpan<float> values)
    {
        // Every byte is written below, on both paths, so there is nothing for the runtime to zero.
        var blob = GC.AllocateUninitializedArray<byte>(values.Length * sizeof(float));

        if (BitConverter.IsLittleEndian)
        {
            // The wire format is this host's own layout, so the vector is one block copy rather
            // than a conversion per element. This runs for every stored vector and every query.
            MemoryMarshal.AsBytes(values).CopyTo(blob);
            return blob;
        }

        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(blob.AsSpan(i * sizeof(float)), values[i]);
        }

        return blob;
    }

    /// <summary>Decodes a little-endian float32 blob back into a <c>float[N]</c> vector.</summary>
    /// <exception cref="ArgumentException"><paramref name="blob"/>'s length is not a multiple of <c>sizeof(float)</c>.</exception>
    public static float[] ToFloats(ReadOnlySpan<byte> blob)
    {
        if (blob.Length % sizeof(float) != 0)
        {
            throw new ArgumentException(
                $"A float32 vector blob length must be a multiple of {sizeof(float)}; got {blob.Length}.",
                nameof(blob));
        }

        var values = GC.AllocateUninitializedArray<float>(blob.Length / sizeof(float));

        if (BitConverter.IsLittleEndian)
        {
            blob.CopyTo(MemoryMarshal.AsBytes(values.AsSpan()));
            return values;
        }

        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(blob[(i * sizeof(float))..]);
        }

        return values;
    }

    /// <summary>Encodes an <c>int8[N]</c> vector as a signed-byte blob, as sqlite-vec expects.</summary>
    public static byte[] FromInt8(ReadOnlySpan<sbyte> values)
    {
        // A signed byte and a byte are the same eight bits; there is no byte order to respect.
        return MemoryMarshal.AsBytes(values).ToArray();
    }

    /// <summary>Decodes a signed-byte blob back into an <c>int8[N]</c> vector.</summary>
    public static sbyte[] ToInt8(ReadOnlySpan<byte> blob)
    {
        return MemoryMarshal.Cast<byte, sbyte>(blob).ToArray();
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

    /// <summary>Decodes a bit-packed blob back into a <c>bit[N]</c> vector, least significant bit first within each byte.</summary>
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
