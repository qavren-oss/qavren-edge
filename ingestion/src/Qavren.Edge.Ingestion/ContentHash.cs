using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace Qavren.Edge.Ingestion;

/// <summary>
/// A 128-bit xxHash128 content hash. Persisted big-endian as <c>BLOB(16)</c> (spec 9.1).
/// </summary>
/// <param name="Value">The raw 128-bit digest.</param>
public readonly record struct ContentHash(UInt128 Value)
{
    /// <summary>The blob width, in bytes. Sixteen, always.</summary>
    public const int BlobLength = 16;

    /// <summary>The value stored in <c>qedge_ingest_meta.hash_algorithm</c>.</summary>
    public const string AlgorithmId = "xxh128-v1";

    /// <summary>The 0x1F unit separator <see cref="Combine"/> and spec 9.2's recipe hash use.</summary>
    internal const byte Separator = 0x1F;

    /// <summary>The all-zero hash. Means "no hash recorded", never "hash of nothing".</summary>
    public static ContentHash Zero => default;

    /// <summary>True when this is <see cref="Zero"/>.</summary>
    public bool IsZero => Value == UInt128.Zero;

    /// <summary>Hashes raw bytes.</summary>
    public static ContentHash OfBytes(ReadOnlySpan<byte> bytes) => new(XxHash128.HashToUInt128(bytes));

    /// <summary>Hashes <paramref name="text"/> as UTF-8 with no BOM.</summary>
    public static ContentHash OfText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var max = Encoding.UTF8.GetMaxByteCount(text.Length);
        byte[]? rented = null;
        var buffer = max <= 1024
            ? stackalloc byte[1024]
            : (rented = ArrayPool<byte>.Shared.Rent(max)).AsSpan();
        try
        {
            var written = Encoding.UTF8.GetBytes(text, buffer);
            return OfBytes(buffer[..written]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// Spec 9.1's <b>counted read</b>. Streams <paramref name="stream"/> in
    /// <paramref name="bufferSize"/> blocks, carries a running byte count, and returns
    /// <see langword="null"/> the moment the count exceeds <paramref name="maxBytes"/>.
    /// <para>
    /// A null result is NOT a hash of a truncated read and must never be persisted as one: storing
    /// the hash of a partial read would make a later shrink of the file look unchanged. At most
    /// <c>maxBytes + bufferSize</c> bytes are ever read and nothing is buffered.
    /// </para>
    /// </summary>
    /// <param name="stream">The stream to hash. Read from its current position.</param>
    /// <param name="maxBytes">The source-byte ceiling. Non-negative.</param>
    /// <param name="bufferSize">The read block. 64 KiB by default.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The hash, or <see langword="null"/> when the stream is over the ceiling.</returns>
    public static async ValueTask<ContentHash?> OfStreamAsync(
        Stream stream, long maxBytes, int bufferSize = 65536, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);

        var hasher = new XxHash128();
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    return null;
                }

                hasher.Append(buffer.AsSpan(0, read));
            }

            return new ContentHash(hasher.GetCurrentHashAsUInt128());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Order-sensitive combination over the 0x1F-separated big-endian renderings of
    /// <paramref name="parts"/>. This is spec 9.3's key derivation.
    /// </summary>
    public static ContentHash Combine(ReadOnlySpan<ContentHash> parts)
    {
        var hasher = new XxHash128();
        Span<byte> slot = stackalloc byte[BlobLength];
        for (var i = 0; i < parts.Length; i++)
        {
            if (i != 0)
            {
                hasher.Append([Separator]);
            }

            BinaryPrimitives.WriteUInt128BigEndian(slot, parts[i].Value);
            hasher.Append(slot);
        }

        return new ContentHash(hasher.GetCurrentHashAsUInt128());
    }

    /// <summary>The persisted form: sixteen bytes, big-endian.</summary>
    public byte[] ToBlob()
    {
        var blob = new byte[BlobLength];
        BinaryPrimitives.WriteUInt128BigEndian(blob, Value);
        return blob;
    }

    /// <summary>Reads the persisted form. Any length but sixteen is 6204.</summary>
    /// <exception cref="EdgeIngestionStateException">The blob is not sixteen bytes.</exception>
    public static ContentHash FromBlob(ReadOnlySpan<byte> blob)
    {
        if (blob.Length != BlobLength)
        {
            throw new EdgeIngestionStateException(
                EdgeErrorCode.IngestionStateCorrupt,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "A stored content hash is {0} bytes; every xxHash128 blob is exactly {1}.",
                    blob.Length,
                    BlobLength))
            {
                Remediation =
                    "The ingestion state table has been written by something other than Qavren.Edge.Ingestion. " +
                    "Delete the state rows for this collection and re-run the ingestion.",
            };
        }

        return new ContentHash(BinaryPrimitives.ReadUInt128BigEndian(blob));
    }

    /// <summary>Thirty-two lowercase hex characters.</summary>
    public string ToHex() => Convert.ToHexStringLower(ToBlob());

    /// <inheritdoc />
    public override string ToString() => ToHex();

    /// <summary>Parses <see cref="ToHex"/>'s output. Case-insensitive.</summary>
    public static bool TryParseHex(ReadOnlySpan<char> hex, out ContentHash hash)
    {
        hash = Zero;
        if (hex.Length != BlobLength * 2)
        {
            return false;
        }

        if (!UInt128.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        hash = new ContentHash(value);
        return true;
    }
}
