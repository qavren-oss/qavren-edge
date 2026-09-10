using System.Globalization;

namespace Qavren.Edge.Sqlite;

/// <summary>Resolves a key lazily, e.g. from platform secure storage.</summary>
public delegate ValueTask<SqliteKey> SqliteKeyProvider(CancellationToken cancellationToken);

/// <summary>
/// An encryption key for a SQLCipher database, in the form Microsoft.Data.Sqlite can carry.
/// </summary>
/// <remarks>
/// Raw keys are delivered as the literal string <c>x'&lt;64 hex&gt;'</c> in
/// <c>SqliteConnectionStringBuilder.Password</c>. Microsoft.Data.Sqlite escapes that with SQLite's
/// own <c>quote()</c> and emits <c>PRAGMA key = 'x''&lt;64 hex&gt;''';</c>; SQLCipher's pragma
/// handler dequotes it back to <c>x'&lt;64 hex&gt;'</c>, which its <c>blob_format</c> test accepts
/// as a raw key and skips PBKDF2. This is why connection pooling can stay enabled and why no
/// per-physical-open interceptor is needed.
/// </remarks>
public sealed class SqliteKey
{
    private const int RawKeyLength = 32;

    private readonly string _password;

    private SqliteKey(string password, bool isRaw)
    {
        _password = password;
        IsRaw = isRaw;
    }

    /// <summary><see langword="true"/> when the key bypasses SQLCipher's KDF.</summary>
    public bool IsRaw { get; }

    /// <summary>A raw 256-bit key. The recommended mobile path: store the bytes in platform secure storage.</summary>
    public static SqliteKey FromRawBytes(ReadOnlySpan<byte> key)
    {
        if (key.Length != RawKeyLength)
        {
            throw new ArgumentException(
                $"A raw SQLCipher key must be exactly {RawKeyLength.ToString(CultureInfo.InvariantCulture)} bytes; got {key.Length.ToString(CultureInfo.InvariantCulture)}.",
                nameof(key));
        }

        return new SqliteKey("x'" + Convert.ToHexString(key) + "'", isRaw: true);
    }

    /// <summary>A passphrase. SQLCipher derives the key with PBKDF2, which is slow by design.</summary>
    public static SqliteKey FromPassphrase(string passphrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passphrase);
        return new SqliteKey(passphrase, isRaw: false);
    }

    /// <summary>The value to assign to <c>SqliteConnectionStringBuilder.Password</c>.</summary>
    public string ToConnectionStringPassword() => _password;
}
