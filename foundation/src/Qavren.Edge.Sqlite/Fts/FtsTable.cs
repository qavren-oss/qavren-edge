using Microsoft.Data.Sqlite;

namespace Qavren.Edge.Sqlite.Fts;

public enum FtsTokenizer
{
    Unicode61,
    Porter,
    Ascii,
    Trigram,
}

public static class FtsTable
{
    public static string BuildCreateSql(
        string name,
        IReadOnlyList<string> columns,
        FtsTokenizer tokenizer = FtsTokenizer.Unicode61,
        string? contentTable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new ArgumentException("An FTS5 table needs at least one column.", nameof(columns));
        }

        var parts = new List<string>(columns);
        if (!string.IsNullOrWhiteSpace(contentTable))
        {
            parts.Add($"content='{contentTable}'");
        }

        parts.Add($"tokenize='{TokenizerToken(tokenizer)}'");
        return $"CREATE VIRTUAL TABLE IF NOT EXISTS \"{name}\" USING fts5({string.Join(", ", parts)})";
    }

    /// <summary>External-content tables need triggers; FTS5 does not observe the content table itself.</summary>
    public static IReadOnlyList<string> BuildSyncTriggerSql(
        string ftsTable,
        string contentTable,
        IReadOnlyList<string> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ftsTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentTable);
        ArgumentNullException.ThrowIfNull(columns);

        var columnList = string.Join(", ", columns);
        var newValues = string.Join(", ", columns.Select(c => "new." + c));
        var oldValues = string.Join(", ", columns.Select(c => "old." + c));

        return
        [
            $"CREATE TRIGGER IF NOT EXISTS \"{ftsTable}_ai\" AFTER INSERT ON \"{contentTable}\" BEGIN " +
            $"INSERT INTO \"{ftsTable}\"(rowid, {columnList}) VALUES (new.rowid, {newValues}); END",

            $"CREATE TRIGGER IF NOT EXISTS \"{ftsTable}_ad\" AFTER DELETE ON \"{contentTable}\" BEGIN " +
            $"INSERT INTO \"{ftsTable}\"(\"{ftsTable}\", rowid, {columnList}) VALUES ('delete', old.rowid, {oldValues}); END",

            $"CREATE TRIGGER IF NOT EXISTS \"{ftsTable}_au\" AFTER UPDATE ON \"{contentTable}\" BEGIN " +
            $"INSERT INTO \"{ftsTable}\"(\"{ftsTable}\", rowid, {columnList}) VALUES ('delete', old.rowid, {oldValues}); " +
            $"INSERT INTO \"{ftsTable}\"(rowid, {columnList}) VALUES (new.rowid, {newValues}); END",
        ];
    }

    public static async Task CreateAsync(
        SqliteConnection connection,
        string name,
        IReadOnlyList<string> columns,
        FtsTokenizer tokenizer = FtsTokenizer.Unicode61,
        string? contentTable = null,
        CancellationToken cancellationToken = default)
    {
        await connection.ExecuteAsync(
            BuildCreateSql(name, columns, tokenizer, contentTable),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static async Task CreateSyncTriggersAsync(
        SqliteConnection connection,
        string ftsTable,
        string contentTable,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken = default)
    {
        foreach (var sql in BuildSyncTriggerSql(ftsTable, contentTable, columns))
        {
            await connection.ExecuteAsync(sql, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static string TokenizerToken(FtsTokenizer tokenizer) => tokenizer switch
    {
        FtsTokenizer.Unicode61 => "unicode61",
        FtsTokenizer.Porter => "porter unicode61",
        FtsTokenizer.Ascii => "ascii",
        FtsTokenizer.Trigram => "trigram",
        _ => throw new ArgumentOutOfRangeException(nameof(tokenizer)),
    };
}
