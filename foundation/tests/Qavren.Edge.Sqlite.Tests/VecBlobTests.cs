using Qavren.Edge.Sqlite.Vec;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

public class VecBlobTests
{
    [Fact]
    public void Float32_RoundTrips()
    {
        float[] source = [0f, 1f, -1f, 3.14159f, float.Epsilon, -0.5f];

        var blob = VecBlob.From(source);
        var back = VecBlob.ToFloats(blob);

        Assert.Equal(source.Length * 4, blob.Length);
        Assert.Equal(source, back);
    }

    [Fact]
    public void Float32_IsLittleEndianRegardlessOfHost()
    {
        // 1.0f is 0x3F800000; little-endian on the wire is 00 00 80 3F.
        var blob = VecBlob.From([1f]);

        Assert.Equal<byte[]>([0x00, 0x00, 0x80, 0x3F], blob);
    }

    [Fact]
    public void Float32_RoundTripsAVectorOfARealWidth()
    {
        // 384 is the MiniLM width. The encode and the decode are block copies on a little-endian
        // host, so this is the case that proves a whole vector moves and not just its first element.
        var source = new float[384];
        for (var i = 0; i < source.Length; i++)
        {
            source[i] = MathF.Sin(i) * (i % 7 == 0 ? -1e-7f : 3.5f);
        }

        Assert.Equal(source, VecBlob.ToFloats(VecBlob.From(source)));
    }

    [Fact]
    public void Float32_DecodesFromAnUnalignedSlice()
    {
        // A blob read out of a larger buffer does not start on a 4-byte boundary. Decoding must not
        // care: 1.0f, 2.0f after one byte of padding.
        byte[] padded = [0xFF, 0x00, 0x00, 0x80, 0x3F, 0x00, 0x00, 0x00, 0x40];

        Assert.Equal<float[]>([1f, 2f], VecBlob.ToFloats(padded.AsSpan(1)));
    }

    [Fact]
    public void Int8_IsTheTwosComplementByteOnTheWire()
        => Assert.Equal<byte[]>([0x00, 0x01, 0xFF, 0x7F, 0x80], VecBlob.FromInt8([0, 1, -1, 127, -128]));

    [Fact]
    public void ToFloats_RejectsBlobsThatAreNotAMultipleOfFour()
        => Assert.Throws<ArgumentException>(() => VecBlob.ToFloats(new byte[7]));

    [Fact]
    public void Int8_RoundTrips()
    {
        sbyte[] source = [0, 1, -1, 127, -128];

        var blob = VecBlob.FromInt8(source);

        Assert.Equal(source.Length, blob.Length);
        Assert.Equal(source, VecBlob.ToInt8(blob));
    }

    [Fact]
    public void Bit_PacksEightValuesPerByte()
    {
        // 16 values: bit 0 and bit 7 of the first byte, bit 0 of the second.
        bool[] source =
        [
            true, false, false, false, false, false, false, true,
            true, false, false, false, false, false, false, false,
        ];

        var blob = VecBlob.FromBits(source);

        Assert.Equal(2, blob.Length);
        Assert.Equal(0b1000_0001, blob[0]);
        Assert.Equal(0b0000_0001, blob[1]);
    }

    [Fact]
    public void Bit_RejectsLengthsThatAreNotAMultipleOfEight()
        => Assert.Throws<ArgumentException>(() => VecBlob.FromBits([true, false, true]));
}
