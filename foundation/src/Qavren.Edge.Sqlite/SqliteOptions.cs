namespace Qavren.Edge.Sqlite;

public enum SqliteJournalMode
{
    Delete,
    Truncate,
    Persist,
    Memory,
    Wal,
    Off,
}

public enum SqliteSynchronousMode
{
    Off = 0,
    Normal = 1,
    Full = 2,
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
