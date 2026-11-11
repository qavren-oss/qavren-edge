using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Sqlite;

/// <summary>
/// One physical database. Connection strings are always built through
/// <see cref="SqliteConnectionStringBuilder"/> from this single place: Microsoft.Data.Sqlite keys
/// its pool groups on the raw connection-string text, so two textually different but semantically
/// identical strings fragment the pool and hold duplicate keyed handles open.
/// </summary>
public sealed class EdgeDatabase : IEdgeDatabase, IDisposable
{
    // CA1848: inline logger calls are errors under this repo's TreatWarningsAsErrors +
    // latest-recommended analysis level, so the plan's call is expressed as a cached delegate
    // with the same event id, level and message template. Same accommodation as EdgeHost.
    private static readonly Action<ILogger, string, Exception?> s_checkpointFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            EdgeEventIds.CheckpointFailed,
            "Checkpoint of database {Database} failed.");

    private const string InMemory = ":memory:";

    private readonly SqliteOptions _options;
    private readonly Func<IEdgeHost> _host;
    private readonly Func<ISqliteNativeProvider> _native;
    private readonly ILogger<EdgeDatabase> _logger;
    private readonly SemaphoreSlim _keyGate = new(1, 1);

    /// <summary>
    /// Null for a file. For <c>:memory:</c>, the data source every connection of this database
    /// opens instead - see <see cref="GetConnectionStringAsync"/>.
    /// </summary>
    private readonly string? _memoryDataSource;

    private string? _connectionString;
    private SqliteKey? _resolvedKey;

    /// <summary>
    /// The connection that keeps an in-memory database in existence. Opened with the first
    /// connection string and closed by <see cref="Dispose"/>; nothing ever runs a statement on it.
    /// </summary>
    private SqliteConnection? _memoryAnchor;

    /// <summary>
    /// Creates a database over resolved options, paths, the host and the single native provider.
    /// </summary>
    /// <remarks>
    /// The host and the native provider arrive as accessors, not instances, and that is load-bearing.
    /// The startup tasks depend on this database, so resolving <see cref="IEdgeHost"/> in this
    /// constructor closes a container cycle (database -> host -> startup tasks -> database) that
    /// MSDI cannot see through a factory registration and that hangs instead of failing. Deferring
    /// also keeps "no native provider registered" a startup fault, as the plan's Step 8 note requires,
    /// rather than a failure to resolve <see cref="IEdgeDatabase"/> at all. The provider accessor is
    /// called when the connection string is built - on the first open, and again after a rekey -
    /// whether or not a key is configured.
    /// </remarks>
    public EdgeDatabase(
        string name,
        SqliteOptions options,
        Func<IEdgeHost> host,
        IEdgePaths paths,
        Func<ISqliteNativeProvider> native,
        ILogger<EdgeDatabase> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);

        Name = name;
        _options = options;
        _host = host;
        _native = native;
        _logger = logger;

        if (string.Equals(options.DatabaseName, InMemory, StringComparison.Ordinal))
        {
            Path = InMemory;

            // SQLite's own ":memory:" is a new, empty database for every connection that opens it,
            // and this class opens one connection per call: the migrator's tables were in a
            // database nobody could reach again. The memdb VFS holds a database in memory under a
            // name instead, and a name that starts with '/' is one database for every connection
            // in the process that asks for it. The name is this instance's alone, so two
            // registrations, or two hosts in one test run, never meet.
            _memoryDataSource = "file:/qedge-" + Guid.NewGuid().ToString("N") + "?vfs=memdb";
        }
        else
        {
            Path = System.IO.Path.Combine(options.Directory ?? paths.Data, options.DatabaseName);
        }

        IsEncrypted = options.Key is not null || options.KeyProvider is not null;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Path { get; }

    /// <inheritdoc />
    public bool IsEncrypted { get; }

    /// <inheritdoc />
    public async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        await _host().EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return await OpenCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens without awaiting startup. Only startup tasks may call this.</summary>
    internal async Task<SqliteConnection> OpenCoreAsync(CancellationToken cancellationToken)
    {
        var connectionString = await GetConnectionStringAsync(cancellationToken).ConfigureAwait(false);
        var connection = new SqliteConnection(connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ApplyPerOpenPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (SqliteException ex) when (IsEncrypted && ex.SqliteErrorCode == 26 /* SQLITE_NOTADB */)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new EdgeDatabaseKeyException(Name, ex);
        }
        catch
        {
            // Nobody else holds this connection yet, so nobody else can dispose it. A cancellation
            // between the open and the pragmas would otherwise leave an open pooled handle - and,
            // on Windows, a file nothing can delete - until a finaliser got round to it.
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Issued on every logical open. Microsoft.Data.Sqlite does exactly this for
    /// <c>foreign_keys</c> and <c>recursive_triggers</c>, and never resets a pragma when a pooled
    /// connection is returned, so re-issuing is both cheap and correct.
    /// <c>journal_mode</c> is deliberately absent: it lives in the file header and is set once, at creation.
    /// </summary>
    private async Task ApplyPerOpenPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var busyMs = (int)_options.BusyTimeout.TotalMilliseconds;
        await connection.ExecuteAsync(
            $"PRAGMA busy_timeout = {busyMs.ToString(CultureInfo.InvariantCulture)};",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            $"PRAGMA synchronous = {((int)_options.Synchronous).ToString(CultureInfo.InvariantCulture)};",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            $"PRAGMA cache_size = -{_options.CacheSizeKiB.ToString(CultureInfo.InvariantCulture)};",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applied once, at creation. Safe to re-run: setting the same journal_mode is a no-op.</summary>
    internal async Task ApplyCreationPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (_memoryDataSource is not null)
        {
            return;
        }

        await connection.ExecuteAsync(
            $"PRAGMA journal_mode = {_options.JournalMode.ToString().ToUpperInvariant()};",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal async Task<string> GetConnectionStringAsync(CancellationToken cancellationToken)
    {
        if (_connectionString is not null)
        {
            return _connectionString;
        }

        await _keyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connectionString is not null)
            {
                return _connectionString;
            }

            // Asked for on every database, not only a keyed one. This is the first thing any open
            // does, and it is where "no native provider" and "two of them" become the documented
            // configuration error - for the open task, and equally for a startup task ordered ahead
            // of it, which the host lets straight through to here.
            var native = _native();

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _memoryDataSource ?? Path,

                // Never pooled in memory. A pooled handle outlives this object until somebody
                // clears the pools, and a handle is all it takes to keep the whole database alive.
                Pooling = _memoryDataSource is null && _options.Pooling,
                ForeignKeys = _options.ForeignKeys,
                Mode = SqliteOpenMode.ReadWriteCreate,
            };

            if (IsEncrypted)
            {
                if (!native.SupportsEncryption)
                {
                    throw new EdgeConfigurationException(
                        EdgeErrorCode.EncryptionKeyWithoutCipherProvider,
                        $"Database '{Name}' has a key configured, but the registered native provider " +
                        $"'{native.Name}' has no codec. Reference Qavren.Edge.Sqlite.Native.Cipher and " +
                        "call UseSqliteNativeCipher() instead of UseSqliteNative().");
                }

                // ??=, not =: after RekeyAsync the file is on the new key, but neither
                // _options.Key nor KeyProvider knows that, so re-resolving here would rebuild the
                // connection string around the old, now-wrong key. This is the one read of
                // _resolvedKey, and it is what makes RekeyAsync's write of it load-bearing.
                _resolvedKey ??= _options.Key
                    ?? await _options.KeyProvider!(cancellationToken).ConfigureAwait(false);
                builder.Password = _resolvedKey.ToConnectionStringPassword();
            }

            var connectionString = builder.ToString();

            if (_memoryDataSource is not null && _memoryAnchor is null)
            {
                // An in-memory database exists for as long as one connection to it does. Callers
                // open and close theirs around every operation, so this one stays open underneath
                // them all - through a pool clear too, which only ever closes idle pooled handles -
                // until the container disposes this object.
                var anchor = new SqliteConnection(connectionString);
                try
                {
                    await anchor.OpenAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await anchor.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                _memoryAnchor = anchor;
            }

            _connectionString = connectionString;
            return _connectionString;
        }
        finally
        {
            _keyGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var result = await work(connection, transaction, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }
        }
    }

    /// <inheritdoc />
    public async Task<SqliteDatabaseInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await ReadInfoAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the info scalars off an already-open connection. Startup task 10 uses this to capture
    /// the info while it still holds its own open connection: calling <see cref="GetInfoAsync"/>
    /// there would re-enter <see cref="IEdgeHost.EnsureStartedAsync"/> from inside startup.
    /// </summary>
    internal async Task<SqliteDatabaseInfo> ReadInfoAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var fileSize = _memoryDataSource is not null || !File.Exists(Path)
            ? 0L
            : new FileInfo(Path).Length;

        return new SqliteDatabaseInfo(
            await connection.ScalarAsync<string>("SELECT sqlite_version()", cancellationToken: cancellationToken).ConfigureAwait(false) ?? "unknown",
            await connection.ScalarAsync<string>("SELECT vec_version()", cancellationToken: cancellationToken).ConfigureAwait(false) ?? "unknown",
            await connection.ScalarAsync<int>("PRAGMA page_size", cancellationToken: cancellationToken).ConfigureAwait(false),
            await connection.ScalarAsync<string>("PRAGMA journal_mode", cancellationToken: cancellationToken).ConfigureAwait(false) ?? "unknown",
            await connection.ScalarAsync<int>("PRAGMA user_version", cancellationToken: cancellationToken).ConfigureAwait(false),
            fileSize,
            IsEncrypted);
    }

    /// <inheritdoc />
    public async Task<SqliteCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var quick = await connection.ScalarAsync<string>("PRAGMA quick_check", cancellationToken: cancellationToken)
                .ConfigureAwait(false) ?? "unknown";
            var vec = await connection.ScalarAsync<string>("SELECT vec_version()", cancellationToken: cancellationToken)
                .ConfigureAwait(false) ?? "unknown";

            return new SqliteCheckResult(string.Equals(quick, "ok", StringComparison.Ordinal), quick, vec);
        }
    }

    /// <inheritdoc />
    public async Task RekeyAsync(SqliteKey newKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newKey);

        if (!IsEncrypted || !_native().SupportsEncryption)
        {
            throw new EdgeConfigurationException(
                EdgeErrorCode.EncryptionKeyWithoutCipherProvider,
                $"Database '{Name}' is not encrypted, so it cannot be rekeyed.");
        }

        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            // SQLite forbids parameters in PRAGMA, so quote through SQLite's own quote() exactly
            // as Microsoft.Data.Sqlite does for PRAGMA key.
            var quoteCommand = connection.CreateCommand();
            await using (quoteCommand.ConfigureAwait(false))
            {
                quoteCommand.CommandText = "SELECT quote($password);";
                quoteCommand.Parameters.AddWithValue("$password", newKey.ToConnectionStringPassword());
                var quoted = (string)(await quoteCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

                await connection.ExecuteAsync("PRAGMA rekey = " + quoted + ";", cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // Pooled handles still hold the old key. Drop them, then rebuild the connection string.
        SqliteConnection.ClearAllPools();

        // NOT the caller's token. The PRAGMA above is the point of no return: the file is on the
        // new key now, and Microsoft.Data.Sqlite runs a command to completion whatever its token
        // does, so a cancellation requested during a long rekey is first SEEN here. Honouring it
        // throws OperationCanceledException at a caller who then keeps the old key, in a process
        // that still holds the old key too - a database nothing can open again.
        await _keyGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _resolvedKey = newKey;
            _connectionString = null;
        }
        finally
        {
            _keyGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task CheckpointAsync(CancellationToken cancellationToken = default)
    {
        if (_memoryDataSource is not null)
        {
            return;
        }

        try
        {
            var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                await connection.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        // Best effort by contract: a checkpoint failure must never fault a lifecycle transition.
        catch (Exception ex)
        {
            s_checkpointFailed(_logger, Name, ex);
        }
    }

    /// <summary>
    /// Releases the key gate and, for an in-memory database, the connection that kept it in
    /// existence. The container owns this object's lifetime.
    /// </summary>
    public void Dispose()
    {
        _memoryAnchor?.Dispose();
        _memoryAnchor = null;
        _keyGate.Dispose();
    }
}
