namespace Qavren.Edge.Sqlite;

/// <summary>SQLite's <c>journal_mode</c> pragma values, applied once at database creation.</summary>
public enum SqliteJournalMode
{
    /// <summary>The rollback journal is deleted at the end of each transaction (SQLite's historical default).</summary>
    Delete,

    /// <summary>The rollback journal is truncated to zero length instead of deleted.</summary>
    Truncate,

    /// <summary>The rollback journal is left on disk but its header is overwritten, avoiding a filesystem delete.</summary>
    Persist,

    /// <summary>The rollback journal is held in memory instead of on disk. Not crash-safe.</summary>
    Memory,

    /// <summary>Write-ahead logging. Qavren.Edge's default: readers do not block writers.</summary>
    Wal,

    /// <summary>No rollback journal at all. A crash mid-transaction can corrupt the database.</summary>
    Off,
}

/// <summary>SQLite's <c>synchronous</c> pragma values, issued on every logical open.</summary>
public enum SqliteSynchronousMode
{
    /// <summary>SQLite does not call <c>fsync</c>. Fastest, and unsafe against a power loss or OS crash.</summary>
    Off = 0,

    /// <summary>SQLite syncs at the most critical moments. Qavren.Edge's default; safe with <see cref="SqliteJournalMode.Wal"/>.</summary>
    Normal = 1,

    /// <summary>SQLite syncs the database file on every write.</summary>
    Full = 2,

    /// <summary>Like <see cref="Full"/>, and also syncs the rollback journal before deleting it.</summary>
    Extra = 3,
}

/// <summary>Per-database configuration. Bound as a named <c>IOptions</c> instance keyed by database name.</summary>
public sealed class SqliteOptions
{
    /// <summary>File name under <see cref="Directory"/>. <c>:memory:</c> is allowed.</summary>
    public string DatabaseName { get; set; } = "edge.db";

    /// <summary>Overrides <c>IEdgePaths.Data</c>. Useful for tests and shared containers.</summary>
    public string? Directory { get; set; }

    /// <summary>
    /// Applied once, at database creation. <c>journal_mode</c> is persisted in the file header,
    /// so re-issuing it per open is pointless, and issuing it inside a transaction is an error.
    /// </summary>
    public SqliteJournalMode JournalMode { get; set; } = SqliteJournalMode.Wal;

    /// <summary>Issued per logical open. Microsoft.Data.Sqlite 10.x has no <c>Synchronous</c> connection-string keyword.</summary>
    public SqliteSynchronousMode Synchronous { get; set; } = SqliteSynchronousMode.Normal;

    /// <summary>
    /// Issued per logical open as <c>PRAGMA busy_timeout</c>. This is SQLite's own busy handler and is
    /// distinct from Microsoft.Data.Sqlite's <c>Default Timeout</c>, which drives its 150 ms retry loop.
    /// Keep this below the MDS timeout so SQLite backs off before MDS starts spinning.
    /// </summary>
    public TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Applied through the <c>Foreign Keys</c> connection-string keyword, which MDS re-issues on every open.</summary>
    public bool ForeignKeys { get; set; } = true;

    /// <summary>Microsoft.Data.Sqlite connection pooling. Safe to leave on even with a raw key; see <see cref="SqliteKey"/>.</summary>
    public bool Pooling { get; set; } = true;

    /// <summary>Issued per logical open as a negative <c>PRAGMA cache_size</c>, i.e. kibibytes rather than pages.</summary>
    public int CacheSizeKiB { get; set; } = 8192;

    /// <summary>A fixed key. Requires the Cipher native package. Mutually exclusive with <see cref="KeyProvider"/>.</summary>
    public SqliteKey? Key { get; set; }

    /// <summary>A lazily resolved key, e.g. from secure storage. Mutually exclusive with <see cref="Key"/>.</summary>
    public SqliteKeyProvider? KeyProvider { get; set; }
}
