using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Hosting;
using Qavren.Edge.Ingestion.Tests.Runtime;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Native;
using Qavren.Edge.VectorData;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Integration;

/// <summary>What <see cref="IntegrationHost.StartAsync"/> can be told. Every member is optional.</summary>
internal sealed class IntegrationHostOptions
{
    public Action<IngestionOptions>? Ingestion { get; set; }

    public Action<SqliteOptions>? Sqlite { get; set; }

    public Action<EdgeVectorStoreOptions>? Store { get; set; }

    /// <summary>Runs on the builder BEFORE <c>AddIngestion</c>.</summary>
    public Action<EdgeBuilder>? BeforeIngestion { get; set; }

    /// <summary>Runs on the builder AFTER <c>AddIngestion</c>.</summary>
    public Action<EdgeBuilder>? AfterIngestion { get; set; }

    /// <summary>
    /// Wraps the unkeyed <see cref="IEdgeDatabase"/> SP2's store and SP3's runner resolve. The
    /// wrapper is registered LAST so it wins the unkeyed slot; the keyed <c>"(default)"</c>
    /// registration SP1 made stays the real database underneath.
    /// </summary>
    public Func<IEdgeDatabase, IEdgeDatabase>? WrapDatabase { get; set; }

    public RecordingEmbeddingGenerator? Generator { get; set; }

    public IChunkTokenizer? Tokenizer { get; set; }

    /// <summary>A caller-supplied root is kept on Dispose, so two hosts can share one database.</summary>
    public string? Root { get; set; }

    /// <summary>Null is <see cref="TimeProvider.System"/>: a real clock, for tests that measure one.</summary>
    public TimeProvider? Time { get; set; }

    public int MigrationVersion { get; set; } = 1;
}

/// <summary>
/// The tier-2 host: SP1's SQLite over the real natives, SP2's store, SP3's ingestion, a whitespace
/// tokenizer and a <see cref="RecordingEmbeddingGenerator"/>. Unlike <see cref="IngestionTestHost"/>
/// it exposes the SQLite options (busy timeout), a database wrapper hook and the lifecycle hub,
/// which is what the durability and sleeping suites need and the runtime suite does not.
/// </summary>
internal sealed class IntegrationHost : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly bool _ownsRoot;

    private IntegrationHost(
        ServiceProvider services,
        string root,
        bool ownsRoot,
        RecordingEmbeddingGenerator generator,
        CapturingLoggerProvider logs)
    {
        _services = services;
        Root = root;
        _ownsRoot = ownsRoot;
        Generator = generator;
        Logs = logs;
    }

    public IServiceProvider Services => _services;

    public string Root { get; }

    public IIngestionPipeline Pipeline => _services.GetRequiredService<IIngestionPipeline>();

    /// <summary>The unkeyed database - the wrapper when one was supplied.</summary>
    public IEdgeDatabase Database => _services.GetRequiredService<IEdgeDatabase>();

    public IEdgeLifecycle Lifecycle => _services.GetRequiredService<IEdgeLifecycle>();

    public RecordingEmbeddingGenerator Generator { get; }

    public CapturingLoggerProvider Logs { get; }

    public static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "qedge-ingestion-integration", Guid.NewGuid().ToString("N"));

    /// <summary>Builds the container without starting it, so a startup fault can be asserted.</summary>
    public static ServiceProvider Build(IntegrationHostOptions options, out RecordingEmbeddingGenerator generator, out CapturingLoggerProvider logs)
    {
        ArgumentNullException.ThrowIfNull(options);

        var root = options.Root ?? NewRoot();
        Directory.CreateDirectory(root);
        var recording = options.Generator ?? new RecordingEmbeddingGenerator();
        var capturing = new CapturingLoggerProvider();
        var tokenizer = options.Tokenizer ?? new FakeChunkTokenizer();

        var services = new ServiceCollection();

        // Trace, explicitly: several assertions here read Debug-level events (916, 905, SP2's 803)
        // and a default filter that dropped them would turn those assertions into silent no-ops.
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(capturing));
        services.AddSingleton<IEdgePaths>(new FixedPaths(root));
        services.AddSingleton(options.Time ?? TimeProvider.System);

        services.AddQavrenEdge(edge =>
        {
            edge.UseSqliteNative();
            edge.AddSqlite(o =>
            {
                o.DatabaseName = "integration.db";
                o.Directory = root;
                options.Sqlite?.Invoke(o);
            });
            edge.AddVectorStore(options.Store);
            edge.Services.AddSingleton(tokenizer);
            edge.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(recording);
            options.BeforeIngestion?.Invoke(edge);
            edge.AddIngestion(options.MigrationVersion, options.Ingestion);
            options.AfterIngestion?.Invoke(edge);

            if (options.WrapDatabase is { } wrap)
            {
                edge.Services.AddSingleton<IEdgeDatabase>(sp =>
                    wrap(sp.GetRequiredKeyedService<IEdgeDatabase>("(default)")));
            }
        });

        generator = recording;
        logs = capturing;
        return services.BuildServiceProvider();
    }

    public static async Task<IntegrationHost> StartAsync(IntegrationHostOptions? options = null)
    {
        options ??= new IntegrationHostOptions();
        var ownsRoot = options.Root is null;
        options.Root ??= NewRoot();

        var provider = Build(options, out var generator, out var logs);
        try
        {
            await provider.GetRequiredService<IEdgeHost>()
                .EnsureStartedAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            provider.Dispose();
            throw;
        }

        return new IntegrationHost(provider, options.Root, ownsRoot, generator, logs);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        if (!_ownsRoot)
        {
            return;
        }

        DeleteRoot(Root);
    }

    public static void DeleteRoot(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A WAL file may still be mapped on Windows; a leftover temp directory is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FixedPaths : IEdgePaths
    {
        public FixedPaths(string root)
        {
            Data = root;
            Cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(Cache);
        }

        public string Data { get; }

        public string Cache { get; }
    }
}

/// <summary>Direct SQL over the tables under test. The assertions the collection API cannot make.</summary>
internal static class Db
{
    public const string DataTable = "chunks";
    public const string VectorTable = "chunks_vec";
    public const string FullTextTable = "chunks_fts";
    public const string DocumentTable = "qedge_ingest_document";
    public const string RunTable = "qedge_ingest_run";
    public const string MetaTable = "qedge_ingest_meta";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static async Task<long> CountAsync(IEdgeDatabase database, string table)
    {
        var connection = await database.OpenConnectionAsync(Token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.ScalarAsync<long>(
                $"SELECT count(*) FROM \"{table}\"", cancellationToken: Token).ConfigureAwait(false);
        }
    }

    public static async Task<bool> TableExistsAsync(IEdgeDatabase database, string table)
    {
        var connection = await database.OpenConnectionAsync(Token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var count = await connection.ScalarAsync<long>(
                "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $n",
                [new SqliteParameter("$n", table)],
                Token).ConfigureAwait(false);
            return count == 1;
        }
    }

    /// <summary>Every chunk key, optionally under one document, in ordinal order.</summary>
    public static async Task<IReadOnlyList<string>> KeysAsync(
        IEdgeDatabase database, string? documentId = null)
    {
        var connection = await database.OpenConnectionAsync(Token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var sql = documentId is null
                ? $"SELECT \"key\" FROM \"{DataTable}\" ORDER BY \"document_id\", \"ordinal\""
                : $"SELECT \"key\" FROM \"{DataTable}\" WHERE \"document_id\" = $d ORDER BY \"ordinal\"";
            return await connection.QueryAsync(
                sql,
                r => r.GetString(0),
                documentId is null ? null : [new SqliteParameter("$d", documentId)],
                Token).ConfigureAwait(false);
        }
    }

    /// <summary>The stored (key, ordinal, char_start, char_end, text, heading_path) rows of one document.</summary>
    public static async Task<IReadOnlyList<StoredChunk>> ChunksAsync(IEdgeDatabase database, string? documentId = null)
    {
        var connection = await database.OpenConnectionAsync(Token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var where = documentId is null ? string.Empty : " WHERE \"document_id\" = $d";
            return await connection.QueryAsync(
                $"""
                 SELECT "key","document_id","ordinal","char_start","char_end","text","heading_path"
                 FROM "{DataTable}"{where}
                 ORDER BY "document_id", "ordinal"
                 """,
                r => new StoredChunk(
                    r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetString(5),
                    r.IsDBNull(6) ? string.Empty : r.GetString(6)),
                documentId is null ? null : [new SqliteParameter("$d", documentId)],
                Token).ConfigureAwait(false);
        }
    }

    /// <summary>The vec0 rows, rowid and raw vector blob, so two states can be compared byte for byte.</summary>
    public static async Task<IReadOnlyList<(long RowId, byte[] Vector)>> VectorRowsAsync(IEdgeDatabase database)
    {
        var connection = await database.OpenConnectionAsync(Token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.QueryAsync(
                $"SELECT rowid, \"embedding\" FROM \"{VectorTable}\" ORDER BY rowid",
                r => (r.GetInt64(0), (byte[])r.GetValue(1)),
                cancellationToken: Token).ConfigureAwait(false);
        }
    }

    /// <summary>Every data-table row rendered as one line, keyed and ordered, for a whole-collection comparison.</summary>
    public static async Task<IReadOnlyList<string>> DumpAsync(IEdgeDatabase database)
    {
        var connection = await database.OpenConnectionAsync(Token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync(
                $"""
                 SELECT d."key", d."source_id", d."document_id", d."ordinal", d."text", d."heading_path",
                        hex(d."content_hash"), d."char_start", d."char_end", d."token_count", d."page",
                        d."block_kind", d."extractor_id", d."media_type", hex(v."embedding")
                 FROM "{DataTable}" d
                 JOIN "{VectorTable}" v ON v.rowid = d."_rowid"
                 ORDER BY d."key"
                 """,
                r =>
                {
                    var builder = new StringBuilder();
                    for (var i = 0; i < r.FieldCount; i++)
                    {
                        builder.Append(Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)).Append('\u001f');
                    }

                    return builder.ToString();
                },
                cancellationToken: Token).ConfigureAwait(false);
            return rows;
        }
    }

    public static async Task<long> FtsMatchCountAsync(IEdgeDatabase database, string term)
    {
        var connection = await database.OpenConnectionAsync(Token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.ScalarAsync<long>(
                $"SELECT count(*) FROM \"{FullTextTable}\" WHERE \"{FullTextTable}\" MATCH $q",
                [new SqliteParameter("$q", term)],
                Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// FTS5's own external-content audit: it raises <c>SQLITE_CORRUPT_VTAB</c> when the index and
    /// the content table disagree, which is exactly what a broken <c>_au</c> trigger produces.
    /// </summary>
    public static async Task FtsIntegrityCheckAsync(IEdgeDatabase database)
    {
        var connection = await database.OpenConnectionAsync(Token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await connection.ExecuteAsync(
                $"INSERT INTO \"{FullTextTable}\"(\"{FullTextTable}\") VALUES ('integrity-check')",
                cancellationToken: Token).ConfigureAwait(false);
        }
    }

    public static async Task<string?> DocumentColumnAsync(IEdgeDatabase database, string documentId, string column)
    {
        var connection = await database.OpenConnectionAsync(Token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await connection.ScalarAsync<string>(
                $"SELECT CAST(\"{column}\" AS TEXT) FROM \"{DocumentTable}\" WHERE \"document_id\" = $d",
                [new SqliteParameter("$d", documentId)],
                Token).ConfigureAwait(false);
        }
    }

    public static async Task<byte[]?> DocumentContentHashAsync(IEdgeDatabase database, string documentId)
    {
        var connection = await database.OpenConnectionAsync(Token).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync(
                $"SELECT \"content_hash\" FROM \"{DocumentTable}\" WHERE \"document_id\" = $d",
                r => r.IsDBNull(0) ? null : (byte[])r.GetValue(0),
                [new SqliteParameter("$d", documentId)],
                Token).ConfigureAwait(false);
            return rows.Count == 0 ? null : rows[0];
        }
    }
}

/// <summary>One stored chunk row, the columns the tier-2 assertions read.</summary>
internal sealed record StoredChunk(
    string Key, string DocumentId, int Ordinal, int CharStart, int CharEnd, string Text, string HeadingPath);

/// <summary>
/// An <see cref="IEdgeDatabase"/> decorator with two faults on demand, both aimed by
/// <see cref="Arm"/>: after arming, the <see cref="FailOnArmedTransaction"/>-th transaction throws
/// before delegating (a write that never reaches SQLite - spec 14.3's crash injection), or the
/// <see cref="LockOnArmedTransaction"/>-th runs against a second connection holding
/// <c>BEGIN IMMEDIATE</c>, so it meets <c>SQLITE_BUSY</c> (a concurrent writer).
/// <para>
/// Arming from <see cref="RecordingEmbeddingGenerator.OnCall"/> is what makes the aim exact: the
/// first transaction after an embed call is SP2's upsert (spec 9.5 step a2) and, for a one-window
/// document, the second is the state row (step d). Counting from the run's start would depend on
/// how many rows the run and hash gates touched first.
/// </para>
/// </summary>
internal sealed class FaultingEdgeDatabase(IEdgeDatabase inner) : IEdgeDatabase
{
    private bool _armed;

    public int TransactionCount { get; private set; }

    public int OpenCount { get; private set; }

    /// <summary>Transactions since the last <see cref="Arm"/>.</summary>
    public int TransactionsSinceArm { get; private set; }

    /// <summary>1-based index, after arming, of the transaction that throws. Fires once.</summary>
    public int? FailOnArmedTransaction { get; set; }

    /// <summary>1-based index, after arming, of the transaction run under a concurrent writer. Fires once.</summary>
    public int? LockOnArmedTransaction { get; set; }

    /// <summary>How many times a fault actually fired.</summary>
    public int Faults { get; private set; }

    public string Name => inner.Name;

    public string Path => inner.Path;

    public bool IsEncrypted => inner.IsEncrypted;

    /// <summary>Starts counting. The next transaction is number one.</summary>
    public void Arm()
    {
        _armed = true;
        TransactionsSinceArm = 0;
    }

    public ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        OpenCount++;
        return inner.OpenConnectionAsync(cancellationToken);
    }

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        TransactionCount++;

        if (_armed)
        {
            TransactionsSinceArm++;

            if (FailOnArmedTransaction == TransactionsSinceArm)
            {
                _armed = false;
                Faults++;
                throw new InvalidOperationException(
                    "Injected crash: the process died before this write reached SQLite.");
            }

            if (LockOnArmedTransaction == TransactionsSinceArm)
            {
                _armed = false;
                Faults++;
                return await UnderConcurrentWriterAsync(work, cancellationToken).ConfigureAwait(false);
            }
        }

        return await inner.ExecuteInTransactionAsync(work, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// SP1's <c>ExecuteInTransactionAsync</c> shape - open, BEGIN, work, COMMIT - with one
    /// difference: this connection's <see cref="SqliteConnection.DefaultTimeout"/> is one second.
    /// Microsoft.Data.Sqlite spins on <c>SQLITE_BUSY</c> for its command timeout (30 s by default)
    /// AFTER SQLite's own <c>busy_timeout</c> has given up, and a test that waited half a minute to
    /// observe a lock would be a test nobody runs. The locker holds the write lock for the whole
    /// attempt, so the BEGIN cannot succeed and the SqliteException reaches the caller.
    /// </summary>
    private async Task<T> UnderConcurrentWriterAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        var locker = await inner.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (locker.ConfigureAwait(false))
        {
            await locker.ExecuteAsync("BEGIN IMMEDIATE", cancellationToken: cancellationToken).ConfigureAwait(false);
            try
            {
                var connection = await inner.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using (connection.ConfigureAwait(false))
                {
                    connection.DefaultTimeout = 1;
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
            finally
            {
                await locker.ExecuteAsync("ROLLBACK", cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public Task<SqliteDatabaseInfo> GetInfoAsync(CancellationToken cancellationToken = default) =>
        inner.GetInfoAsync(cancellationToken);

    public Task<SqliteCheckResult> CheckAsync(CancellationToken cancellationToken = default) =>
        inner.CheckAsync(cancellationToken);

    public Task RekeyAsync(SqliteKey newKey, CancellationToken cancellationToken = default) =>
        inner.RekeyAsync(newKey, cancellationToken);

    public Task CheckpointAsync(CancellationToken cancellationToken = default) =>
        inner.CheckpointAsync(cancellationToken);
}

/// <summary>
/// A source that yields its documents and, before item <see cref="GateAt"/>, signals
/// <see cref="Reached"/> and then waits on <see cref="Release"/>. It is how a test gets a run into a
/// known mid-enumeration state - one document committed, the next not yet begun - and holds it there.
/// </summary>
internal sealed class GatedSource(int gateAt, string id = "gated") : IngestionSource
{
    private readonly List<(string Id, byte[] Bytes)> _items = [];
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override string Id => id;

    public int GateAt => gateAt;

    /// <summary>Completes when the runner has asked for item <see cref="GateAt"/>.</summary>
    public Task Reached => _reached.Task;

    public GatedSource Add(string documentId, string text)
    {
        _items.Add((documentId, Encoding.UTF8.GetBytes(text)));
        return this;
    }

    public void Release() => _release.TrySetResult();

    public override async IAsyncEnumerable<DocumentSourceItem> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (i == gateAt)
            {
                _reached.TrySetResult();
                await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            var (documentId, bytes) = _items[i];
            yield return new DocumentSourceItem(
                documentId,
                IngestionMediaTypes.FromExtension(documentId),
                _ => new ValueTask<Stream>(new MemoryStream(bytes, writable: false)))
            {
                SizeBytes = bytes.Length,
            };
        }
    }
}

/// <summary>Deterministic prose: paragraph <c>i</c> is <c>words</c> tokens of the form <c>p{i}w{j}</c>.</summary>
internal static class Prose
{
    public static string Paragraph(int index, int words) =>
        string.Join(' ', Enumerable.Range(0, words).Select(j =>
            string.Format(CultureInfo.InvariantCulture, "p{0}w{1}", index, j)));

    public static string Document(int paragraphs, int wordsEach, int firstIndex = 0) =>
        string.Join("\n\n", Enumerable.Range(firstIndex, paragraphs).Select(i => Paragraph(i, wordsEach)));
}

/// <summary>
/// A source that cancels a caller-owned token just before yielding item <see cref="CancelAt"/>,
/// then keeps yielding - the runner, not the source, is what must notice.
/// </summary>
internal sealed class CancelAfterSource(CancellationTokenSource cancellation, int cancelAt, string id = "cancelling")
    : IngestionSource
{
    private readonly List<(string Id, byte[] Bytes)> _items = [];

    public override string Id => id;

    public int CancelAt => cancelAt;

    public CancelAfterSource Add(string documentId, string text)
    {
        _items.Add((documentId, Encoding.UTF8.GetBytes(text)));
        return this;
    }

    /// <summary>The same documents as a plain in-memory source, for the uninterrupted comparison run.</summary>
    public RecordingSource AsRecordingSource()
    {
        var source = new RecordingSource(id);
        foreach (var (documentId, bytes) in _items)
        {
            source.Add(documentId, Encoding.UTF8.GetString(bytes), IngestionMediaTypes.FromExtension(documentId));
        }

        return source;
    }

    public override async IAsyncEnumerable<DocumentSourceItem> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (i == cancelAt)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }

            var (documentId, bytes) = _items[i];
            yield return new DocumentSourceItem(
                documentId,
                IngestionMediaTypes.FromExtension(documentId),
                _ => new ValueTask<Stream>(new MemoryStream(bytes, writable: false)))
            {
                SizeBytes = bytes.Length,
            };
        }
    }
}
