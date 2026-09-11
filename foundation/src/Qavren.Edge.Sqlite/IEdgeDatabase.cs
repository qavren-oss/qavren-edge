using Microsoft.Data.Sqlite;

namespace Qavren.Edge.Sqlite;

/// <summary>What one physical database reports about itself.</summary>
public sealed record SqliteDatabaseInfo(
    string SqliteVersion,
    string VecVersion,
    int PageSize,
    string JournalMode,
    int UserVersion,
    long FileSizeBytes,
    bool IsEncrypted);

/// <summary>The result of <see cref="IEdgeDatabase.CheckAsync"/>.</summary>
public sealed record SqliteCheckResult(bool Ok, string QuickCheck, string VecVersion);

/// <summary>One physical database, resolved from DI either unkeyed or by name.</summary>
public interface IEdgeDatabase
{
    /// <summary>The registration name; <c>(default)</c> for the unnamed database.</summary>
    string Name { get; }

    /// <summary>The absolute file path, or <c>:memory:</c>.</summary>
    string Path { get; }

    /// <summary>True when a key or key provider is configured.</summary>
    bool IsEncrypted { get; }

    /// <summary>Awaits startup, opens, applies the key and the per-open pragmas. The caller disposes.</summary>
    ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs <paramref name="work"/> inside one transaction and commits it.</summary>
    Task<T> ExecuteInTransactionAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default);

    /// <summary>Versions, page size, journal mode, <c>user_version</c>, file size and encryption state.</summary>
    Task<SqliteDatabaseInfo> GetInfoAsync(CancellationToken cancellationToken = default);

    /// <summary><c>PRAGMA quick_check</c> plus <c>SELECT vec_version()</c>.</summary>
    Task<SqliteCheckResult> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Cipher builds only; throws <see cref="EdgeConfigurationException"/> otherwise.</summary>
    Task RekeyAsync(SqliteKey newKey, CancellationToken cancellationToken = default);

    /// <summary><c>PRAGMA wal_checkpoint(TRUNCATE)</c>. Best effort: failures are logged, not thrown.</summary>
    Task CheckpointAsync(CancellationToken cancellationToken = default);
}
