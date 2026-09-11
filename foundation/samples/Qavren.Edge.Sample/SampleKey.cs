using System.Security.Cryptography;
using Qavren.Edge.Sqlite;

namespace Qavren.Edge.Sample;

/// <summary>A 32-byte raw key kept in SecureStorage. Generated once, never printed.</summary>
public static class SampleKey
{
    private const string StorageKey = "qavren.edge.sample.dbkey";

    public static async ValueTask<SqliteKey> GetAsync(CancellationToken cancellationToken)
    {
        var existing = await SecureStorage.Default.GetAsync(StorageKey).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(existing))
        {
            return SqliteKey.FromRawBytes(Convert.FromHexString(existing));
        }

        var bytes = RandomNumberGenerator.GetBytes(32);
        await SecureStorage.Default.SetAsync(StorageKey, Convert.ToHexString(bytes)).ConfigureAwait(false);
        return SqliteKey.FromRawBytes(bytes);
    }

    public static Task<bool> HasKeyAsync()
        => SecureStorage.Default.GetAsync(StorageKey).ContinueWith(
            t => !string.IsNullOrEmpty(t.Result), TaskScheduler.Default);
}
