using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Qavren.Edge.Sqlite.Fts;

public enum FtsTokenizer
{
    Unicode61,
    Porter,
    Ascii,
    Trigram,
}

/// <summary>
/// The FTS5 options a plain column list cannot express. Added for sub-project 2's vector-store
/// sidecar, which keys on an explicit <c>"_rowid"</c> column rather than the implicit rowid alias.
/// </summary>
public sealed record FtsTableOptions
{
    /// <summary>External-content table. Null omits <c>content=</c> entirely.</summary>
    public string? ContentTable { get; init; }

    /// <summary>Emitted as <c>content_rowid=</c>. Only meaningful with <see cref="ContentTable"/>.</summary>
    public string ContentRowId { get; init; } = "rowid";

    /// <summary>
    /// 0 | 1 | 2. Default 2: unicode61's own default of 1 has a known multi-diacritic bug.
    /// Emitted only for <c>unicode61</c> and <c>trigram</c>, the only tokenizers that accept it.
    /// </summary>
    public int RemoveDiacritics { get; init; } = 2;

    /// <summary>Emitted as <c>prefix='...'</c>, e.g. <c>"2 3"</c>. Null omits the option.</summary>
    public string? Prefix { get; init; }
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

    /// <summary>
    /// Options-taking overload. <paramref name="options"/> is required and non-nullable so this
    /// never becomes ambiguous with the <c>string? contentTable</c> overload above.
    /// </summary>
    public static string BuildCreateSql(
        string name,
        IReadOnlyList<string> columns,
        FtsTokenizer tokenizer,
        FtsTableOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(options);
        if (columns.Count == 0)
        {
            throw new ArgumentException("An FTS5 table needs at least one column.", nameof(columns));
        }

        if (options.RemoveDiacritics is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.RemoveDiacritics, "remove_diacritics must be 0, 1 or 2.");
        }

        var parts = new List<string>(columns);
        if (!string.IsNullOrWhiteSpace(options.ContentTable))
        {
            parts.Add($"content='{options.ContentTable}'");
            parts.Add($"content_rowid='{options.ContentRowId}'");
        }

        if (!string.IsNullOrWhiteSpace(options.Prefix))
        {
            parts.Add($"prefix='{options.Prefix}'");
        }

        var token = TokenizerToken(tokenizer);
        // remove_diacritics is accepted by unicode61 and trigram only; anywhere else it is a
        // CREATE VIRTUAL TABLE constructor error.
        if (tokenizer is FtsTokenizer.Unicode61 or FtsTokenizer.Trigram)
        {
            token += " remove_diacritics " +
                     options.RemoveDiacritics.ToString(CultureInfo.InvariantCulture);
        }

        parts.Add($"tokenize='{token}'");
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

    /// <summary>
    /// Sync triggers for an external-content table whose rowid alias is named
    /// <paramref name="contentRowId"/> rather than <c>rowid</c>.
    /// </summary>
    public static IReadOnlyList<string> BuildSyncTriggerSql(
        string ftsTable,
        string contentTable,
        IReadOnlyList<string> columns,
        string contentRowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ftsTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRowId);
        ArgumentNullException.ThrowIfNull(columns);

        var rowid = $"\"{contentRowId}\"";
        var columnList = string.Join(", ", columns);
        var newValues = string.Join(", ", columns.Select(c => "new." + c));
        var oldValues = string.Join(", ", columns.Select(c => "old." + c));

        return
        [
            $"CREATE TRIGGER IF NOT EXISTS \"{ftsTable}_ai\" AFTER INSERT ON \"{contentTable}\" BEGIN " +
            $"INSERT INTO \"{ftsTable}\"(rowid, {columnList}) VALUES (new.{rowid}, {newValues}); END",

            $"CREATE TRIGGER IF NOT EXISTS \"{ftsTable}_ad\" AFTER DELETE ON \"{contentTable}\" BEGIN " +
            $"INSERT INTO \"{ftsTable}\"(\"{ftsTable}\", rowid, {columnList}) VALUES ('delete', old.{rowid}, {oldValues}); END",

            $"CREATE TRIGGER IF NOT EXISTS \"{ftsTable}_au\" AFTER UPDATE ON \"{contentTable}\" BEGIN " +
            $"INSERT INTO \"{ftsTable}\"(\"{ftsTable}\", rowid, {columnList}) VALUES ('delete', old.{rowid}, {oldValues}); " +
            $"INSERT INTO \"{ftsTable}\"(rowid, {columnList}) VALUES (new.{rowid}, {newValues}); END",
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

    /// <summary>Options-taking overload; see <see cref="BuildCreateSql(string, IReadOnlyList{string}, FtsTokenizer, FtsTableOptions)"/>.</summary>
    public static async Task CreateAsync(
        SqliteConnection connection,
        string name,
        IReadOnlyList<string> columns,
        FtsTokenizer tokenizer,
        FtsTableOptions options,
        CancellationToken cancellationToken = default)
    {
        await connection.ExecuteAsync(
            BuildCreateSql(name, columns, tokenizer, options),
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
