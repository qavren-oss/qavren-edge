using System.Globalization;
using System.Text;
using Microsoft.Extensions.VectorData.ProviderServices;
using Qavren.Edge.Sqlite.Fts;
using Qavren.Edge.Sqlite.Vec;
using Qavren.Edge.VectorData.Internal;

namespace Qavren.Edge.VectorData;

/// <summary>
/// The DDL and the queries as plain strings, so a consumer can read, log or hand-execute exactly
/// what the provider runs - the same visible-SQL contract as sub-project 1's helpers.
/// <para>
/// Nothing here opens a connection or executes anything. Every statement is emitted with
/// <c>\n</c> line endings so the text is identical on every platform.
/// </para>
/// </summary>
public sealed class EdgeVectorSchema
{
    /// <summary>
    /// The token <see cref="BuildKnnSql"/> and <see cref="BuildHybridRrfSql"/> leave where the
    /// translated filter predicate goes. The caller substitutes the SQL that
    /// <c>EdgeFilterTranslator</c> produced; the surrounding
    /// <c>rowid IN (SELECT "_rowid" FROM &lt;data table&gt; WHERE ...)</c> is already in place,
    /// which is what makes an arbitrary MEVD filter a true vec0 pre-filter.
    /// </summary>
    public const string FilterPlaceholder = "<filter>";

    /// <summary>
    /// The vec0 vector column. Fixed, not derived from the property's storage name: a vec0 table
    /// holds exactly one vector column, and every query in this file names it.
    /// </summary>
    public const string VectorColumnName = "embedding";

    private const char Newline = '\n';

    private readonly IReadOnlyList<string> _dataColumns;
    private readonly IReadOnlyList<string> _indexedColumns;
    private readonly IReadOnlyList<ColumnDefinition> _columnDefinitions;
    private readonly VecMetric _metric;
    private readonly int _chunkSize;
    private readonly FtsTokenizer _tokenizer;
    private readonly int _removeDiacritics;

    /// <summary>Builds the schema for one collection.</summary>
    /// <param name="model">The built collection model.</param>
    /// <param name="collectionName">The collection name; the data table is named after it verbatim.</param>
    /// <param name="options">Store options. Null takes every default.</param>
    /// <param name="alwaysCreateFullTextIndex">
    /// Emit the FTS5 sidecar even when no property is marked <c>IsFullTextIndexed</c>.
    /// </param>
    public EdgeVectorSchema(
        CollectionModel model,
        string collectionName,
        EdgeVectorStoreOptions? options = null,
        bool alwaysCreateFullTextIndex = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        options ??= new EdgeVectorStoreOptions();

        var vector = model.VectorProperty;

        CollectionName = collectionName;
        DataTable = collectionName;
        VectorTable = string.Format(CultureInfo.InvariantCulture, options.VectorTableNameFormat, collectionName);
        KeyColumn = model.KeyProperty.StorageName;
        RowIdColumn = EdgeCollectionModelBuilder.RowIdColumn;
        VectorColumn = VectorColumnName;
        Dimensions = vector.Dimensions;

        FullTextColumns = [.. model.DataProperties.Where(p => p.IsFullTextIndexed).Select(p => p.StorageName)];
        FullTextTable = FullTextColumns.Count > 0 || alwaysCreateFullTextIndex
            ? string.Format(CultureInfo.InvariantCulture, options.FullTextTableNameFormat, collectionName)
            : null;

        _dataColumns = [KeyColumn, .. model.DataProperties.Select(p => p.StorageName)];
        _columnDefinitions =
        [
            Describe(model.KeyProperty, notNull: true),
            .. model.DataProperties.Select(p => Describe(p, notNull: false)),
        ];
        _indexedColumns = [.. model.DataProperties.Where(p => p.IsIndexed).Select(p => p.StorageName)];
        _chunkSize = options.ChunkSize;
        _tokenizer = options.FullTextTokenizer;
        _removeDiacritics = options.FullTextRemoveDiacritics;
        _ = EdgeCollectionModelBuilder.TryMapDistanceFunction(vector.DistanceFunction, out _metric);
    }

    /// <summary>The collection name.</summary>
    public string CollectionName { get; }

    /// <summary>The data table - the collection name, verbatim.</summary>
    public string DataTable { get; }

    /// <summary>The vec0 virtual table.</summary>
    public string VectorTable { get; }

    /// <summary>The FTS5 sidecar, or null when the collection has no full-text property.</summary>
    public string? FullTextTable { get; }

    /// <summary>The storage name of the key property.</summary>
    public string KeyColumn { get; }

    /// <summary>Always <c>_rowid</c>: the INTEGER PRIMARY KEY all three tables join on.</summary>
    public string RowIdColumn { get; }

    /// <summary>Always <c>embedding</c>: the single vec0 vector column.</summary>
    public string VectorColumn { get; }

    /// <summary>The declared vector width.</summary>
    public int Dimensions { get; }

    /// <summary>The storage names of the full-text-indexed data properties, in model order.</summary>
    public IReadOnlyList<string> FullTextColumns { get; }

    /// <summary>
    /// The create-collection statements, in execution order: data table, key index, one index per
    /// <c>IsIndexed</c> property, the vec0 table, the FTS5 sidecar, its three sync triggers, and
    /// the vec0 delete cascade.
    /// </summary>
    /// <param name="model">The built collection model.</param>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="options">Store options. Null takes every default.</param>
    /// <returns>The statements, in order.</returns>
    public static IReadOnlyList<string> BuildCreateSql(
        CollectionModel model,
        string collectionName,
        EdgeVectorStoreOptions? options = null) =>
        new EdgeVectorSchema(model, collectionName, options).BuildCreateSql();

    /// <summary>
    /// Quotes each keyword as an FTS5 string literal, doubling any embedded quote, and joins them.
    /// Quoting is what makes FTS5 treat a token as a phrase, so <c>AND</c>, <c>OR</c>, <c>NOT</c>,
    /// <c>NEAR</c>, <c>*</c>, <c>^</c>, <c>:</c> and parentheses inside a keyword are inert rather
    /// than operators. This is the package's one injection surface.
    /// </summary>
    /// <param name="keywords">The keywords. At least one.</param>
    /// <param name="combinator">How they are joined.</param>
    /// <param name="columnFilter">Optional FTS5 column filter, e.g. <c>Body</c>.</param>
    /// <returns>The MATCH expression.</returns>
    public static string BuildMatchExpression(
        ICollection<string> keywords,
        KeywordCombinator combinator,
        string? columnFilter = null)
    {
        ArgumentNullException.ThrowIfNull(keywords);
        if (keywords.Count == 0)
        {
            throw new ArgumentException("A hybrid search needs at least one keyword.", nameof(keywords));
        }

        var joiner = combinator == KeywordCombinator.And ? " AND " : " OR ";
        var body = string.Join(joiner, keywords.Select(QuoteKeyword));

        return string.IsNullOrEmpty(columnFilter)
            ? body
            : $"{{{columnFilter}}} : ({body})";
    }

    /// <summary>Emits the create-collection statements. See the static overload for the order.</summary>
    /// <returns>The statements, in order.</returns>
    public IReadOnlyList<string> BuildCreateSql()
    {
        var statements = new List<string>
        {
            BuildDataTableSql(),
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"{DataTable}_{KeyColumn}_index\" ON \"{DataTable}\"(\"{KeyColumn}\")",
        };

        statements.AddRange(_indexedColumns.Select(c =>
            $"CREATE INDEX IF NOT EXISTS \"{DataTable}_{c}_index\" ON \"{DataTable}\"(\"{c}\")"));

        // Straight through to sub-project 1. The 1..8192 dimension ceiling is VecTable's
        // (SQLITE_VEC_VEC0_MAX_DIMENSIONS) and is deliberately NOT re-checked, re-messaged or
        // wrapped here: a second copy of the ceiling would drift from the first.
        statements.Add(VecTable.BuildCreateSql(
            VectorTable,
            Dimensions,
            _metric,
            VecElementType.Float32,
            chunkSize: _chunkSize,
            vectorColumn: VectorColumn));

        if (FullTextTable is { } fts && FullTextColumns.Count > 0)
        {
            statements.Add(FtsTable.BuildCreateSql(
                fts,
                FullTextColumns,
                _tokenizer,
                new FtsTableOptions
                {
                    ContentTable = DataTable,
                    ContentRowId = RowIdColumn,
                    RemoveDiacritics = _removeDiacritics,
                }));

            statements.AddRange(FtsTable.BuildSyncTriggerSql(fts, DataTable, FullTextColumns, RowIdColumn));
        }

        // vec0 has no foreign keys, so the cascade is a trigger rather than provider code - which
        // also means an app deleting rows with its own SQL cannot orphan a vector.
        statements.Add(
            $"CREATE TRIGGER IF NOT EXISTS \"{VectorTable}_ad\" AFTER DELETE ON \"{DataTable}\" BEGIN" + Newline +
            $"  DELETE FROM \"{VectorTable}\" WHERE rowid = old.\"{RowIdColumn}\"; END");

        return statements;
    }

    /// <summary>
    /// The drop statements, in the reverse of <see cref="BuildCreateSql()"/>'s order. The triggers
    /// go first because a trigger on a dropped table is an error on some paths and a silent orphan
    /// on others; the FTS5 sidecar goes before its content table because dropping an
    /// external-content table first leaves FTS5's shadow tables referring to nothing. Dropping the
    /// vec0 virtual table also drops its four shadow tables, which is what vec0's own destructor is
    /// for, so none of them is named here.
    /// </summary>
    /// <returns>The statements, in order.</returns>
    public IReadOnlyList<string> BuildDropSql()
    {
        var statements = new List<string> { $"DROP TRIGGER IF EXISTS \"{VectorTable}_ad\"" };

        if (FullTextTable is { } fts && FullTextColumns.Count > 0)
        {
            statements.Add($"DROP TRIGGER IF EXISTS \"{fts}_ad\"");
            statements.Add($"DROP TRIGGER IF EXISTS \"{fts}_au\"");
            statements.Add($"DROP TRIGGER IF EXISTS \"{fts}_ai\"");
            statements.Add($"DROP TABLE IF EXISTS \"{fts}\"");
        }

        statements.Add($"DROP TABLE IF EXISTS \"{VectorTable}\"");
        statements.Add($"DROP TABLE IF EXISTS \"{DataTable}\"");
        return statements;
    }

    /// <summary>
    /// The vec0 KNN query. The filter, when present, is pushed in as
    /// <c>v.rowid IN (SELECT "_rowid" FROM &lt;data table&gt; WHERE ...)</c>: vec0's BestIndex
    /// claims a <c>rowid IN (...)</c> constraint with <c>omit = 1</c> and ANDs the bitmap into the
    /// chunk validity bitmap before any distance is computed, so this is a true pre-filter and
    /// never degrades to a client-side pass.
    /// </summary>
    /// <param name="hasFilter">Emit the rowid pre-filter bracket, with <see cref="FilterPlaceholder"/> inside it.</param>
    /// <param name="hasScoreThreshold">Emit the <c>$scoreThreshold</c> bracket.</param>
    /// <param name="includeVectors">Project the stored vector.</param>
    /// <returns>The query.</returns>
    public string BuildKnnSql(bool hasFilter, bool hasScoreThreshold, bool includeVectors)
    {
        var projection = string.Join(", ", _dataColumns.Select(c => $"d.\"{c}\""));
        var sql = new StringBuilder();

        sql.Append("SELECT d.\"").Append(RowIdColumn).Append("\", ").Append(projection).Append(", v.distance");
        if (includeVectors)
        {
            sql.Append(", v.\"").Append(VectorColumn).Append('"');
        }

        sql.Append(Newline).Append("FROM \"").Append(VectorTable).Append("\" v");
        sql.Append(Newline).Append("JOIN \"").Append(DataTable).Append("\" d ON d.\"").Append(RowIdColumn).Append("\" = v.rowid");
        sql.Append(Newline).Append("WHERE v.\"").Append(VectorColumn).Append("\" MATCH $query AND v.k = $k");

        if (hasFilter)
        {
            sql.Append(Newline).Append("  AND v.rowid IN (").Append(FilterSubquery()).Append(')');
        }

        if (hasScoreThreshold)
        {
            sql.Append(Newline).Append("  AND v.distance <= $scoreThreshold");
        }

        sql.Append(Newline).Append("ORDER BY v.distance");
        return sql.ToString();
    }

    /// <summary>
    /// The hybrid query: a vec0 KNN lane and an FTS5 bm25 lane, fused by reciprocal rank.
    /// <para>
    /// Both lanes rank <b>ascending</b>. FTS5's <c>bm25()</c> is the standard score multiplied by
    /// -1, so a better match is numerically lower, and vec0's <c>distance</c> is a distance. The
    /// hidden <c>rank</c> column is used rather than calling <c>bm25(f)</c>, which SQLite's own
    /// docs say is faster.
    /// </para>
    /// <para>
    /// The keyword lane says <c>f."&lt;fts table&gt;" MATCH $keywords</c> - the alias
    /// <b>qualifying the table-named hidden column</b> - and not the bare alias
    /// <c>f MATCH $keywords</c>. FTS5 gives every table a hidden column named after the table, and
    /// <c>&lt;x&gt; MATCH &lt;expr&gt;</c> is an ordinary comparison against a column, so a bare
    /// alias resolves as a column reference and SQLite answers <c>no such column: f</c>. Measured
    /// against SQLite 3.53.4 + FTS5 on 2026-09-11; spec 13.3's claim that the bare alias is the
    /// only spelling that parses is wrong in both directions, and the spec is the side that is
    /// being corrected.
    /// </para>
    /// <para>
    /// The fused <c>score</c> is a <b>similarity</b> - higher is better - the opposite polarity to
    /// <see cref="BuildKnnSql"/>'s distance.
    /// </para>
    /// </summary>
    /// <param name="hasFilter">Emit the rowid pre-filter in BOTH lanes.</param>
    /// <param name="includeVectors">Emit both halves of the vector bracket: the LEFT JOIN and the projection.</param>
    /// <returns>The query.</returns>
    public string BuildHybridRrfSql(bool hasFilter, bool includeVectors)
    {
        if (FullTextTable is not { } fts || FullTextColumns.Count == 0)
        {
            throw new EdgeVectorModelException(
                EdgeErrorCode.FullTextPropertyMissing,
                CollectionName,
                null,
                $"Collection '{CollectionName}' has no full-text index, so it cannot run a hybrid search. " +
                "Mark at least one string property with [VectorStoreData(IsFullTextIndexed = true)].");
        }

        var projection = string.Join(", ", _dataColumns.Select(c => $"d.\"{c}\""));
        var sql = new StringBuilder();

        sql.Append("WITH vec AS (").Append(Newline);
        sql.Append("  SELECT v.rowid AS id,").Append(Newline);
        sql.Append("         ROW_NUMBER() OVER (ORDER BY v.distance) AS rank,").Append(Newline);
        sql.Append("         v.distance AS distance").Append(Newline);
        sql.Append("  FROM \"").Append(VectorTable).Append("\" v").Append(Newline);
        sql.Append("  WHERE v.\"").Append(VectorColumn).Append("\" MATCH $query AND v.k = $cand");
        if (hasFilter)
        {
            sql.Append(Newline).Append("    AND v.rowid IN (").Append(FilterSubquery()).Append(')');
        }

        sql.Append(Newline).Append("),").Append(Newline);
        sql.Append("fts AS (").Append(Newline);
        sql.Append("  SELECT f.rowid AS id,").Append(Newline);
        sql.Append("         ROW_NUMBER() OVER (ORDER BY f.rank) AS rank,").Append(Newline);
        sql.Append("         f.rank AS bm25").Append(Newline);
        sql.Append("  FROM \"").Append(fts).Append("\" f").Append(Newline);
        // "f.\"notes_fts\"", not the bare alias. FTS5 gives every table a hidden column named
        // after the TABLE, and `<name> MATCH <expr>` is an ordinary comparison against that
        // column - so a bare alias resolves as a column reference and SQLite answers
        // "no such column: f". Measured against sqlite 3.53.4 + FTS5 on 2026-09-11; spec 13.3's
        // "the alias is the only spelling that parses" is wrong in both directions.
        sql.Append("  WHERE f.\"").Append(fts).Append("\" MATCH $keywords");
        if (hasFilter)
        {
            // Unary plus keeps the IN out of FTS5's xBestIndex. Offered as a rowid constraint,
            // SQLite runs xFilter once per IN value - the whole MATCH re-evaluated for every
            // filtered row (issue #36: 3.3-3.6x the unfiltered cost). As a residual predicate the
            // MATCH runs once and the IN is a probe against the materialised subquery.
            sql.Append(Newline).Append("    AND +f.rowid IN (").Append(FilterSubquery()).Append(')');
        }

        sql.Append(Newline).Append("  ORDER BY f.rank").Append(Newline);
        sql.Append("  LIMIT $cand").Append(Newline);
        sql.Append(')').Append(Newline);

        sql.Append("SELECT d.\"").Append(RowIdColumn).Append("\", ").Append(projection).Append(',').Append(Newline);
        sql.Append("       COALESCE(1.0 / ($rrfK + fts.rank), 0.0) * $wKeyword").Append(Newline);
        sql.Append("     + COALESCE(1.0 / ($rrfK + vec.rank), 0.0) * $wVector AS score,").Append(Newline);
        sql.Append("       vec.distance, fts.bm25");
        if (includeVectors)
        {
            sql.Append(", nv.\"").Append(VectorColumn).Append('"');
        }

        sql.Append(Newline).Append("FROM fts").Append(Newline);
        sql.Append("FULL OUTER JOIN vec ON vec.id = fts.id").Append(Newline);
        sql.Append("JOIN \"").Append(DataTable).Append("\" d ON d.\"").Append(RowIdColumn).Append("\" = COALESCE(fts.id, vec.id)").Append(Newline);
        if (includeVectors)
        {
            sql.Append("LEFT JOIN \"").Append(VectorTable).Append("\" nv ON nv.rowid = d.\"").Append(RowIdColumn).Append('"').Append(Newline);
        }

        sql.Append("ORDER BY score DESC").Append(Newline);
        sql.Append("LIMIT $top OFFSET $skip");
        return sql.ToString();
    }

    /// <summary>
    /// The record write. <c>INTEGER PRIMARY KEY</c> is a true rowid alias and is stable across
    /// <c>ON CONFLICT DO UPDATE</c>, so the returned <c>_rowid</c> is the same integer the vec0 and
    /// FTS5 sides are keyed on.
    /// </summary>
    /// <returns>The upsert statement.</returns>
    public string BuildUpsertSql()
    {
        var columns = string.Join(",", _dataColumns.Select(c => $"\"{c}\""));
        var values = string.Join(",", _dataColumns.Select(c => $"${c}"));

        // A record with no data properties at all - a key and a pre-computed vector and nothing
        // else - would otherwise emit "DO UPDATE SET" with an empty assignment list, which is a
        // syntax error at the RETURNING that follows. Re-assigning the key is a no-op that keeps
        // the conflicting row RETURNING its rowid, which the vec0 write then needs.
        var updates = _dataColumns.Count > 1
            ? string.Join(",", _dataColumns.Skip(1).Select(c => $"\"{c}\"=excluded.\"{c}\""))
            : $"\"{KeyColumn}\"=excluded.\"{KeyColumn}\"";

        return $"INSERT INTO \"{DataTable}\"({columns}) VALUES ({values})" + Newline +
               $"  ON CONFLICT(\"{KeyColumn}\") DO UPDATE SET {updates}" + Newline +
               $"  RETURNING \"{RowIdColumn}\"";
    }

    private static string QuoteKeyword(string keyword) =>
        "\"" + (keyword ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private string FilterSubquery() =>
        $"SELECT \"{RowIdColumn}\" FROM \"{DataTable}\" WHERE {FilterPlaceholder}";

    private string BuildDataTableSql()
    {
        // "_rowid" is the widest name in the common case, and every type token lines up one column
        // past the longest quoted name - the shape a hand-written CREATE TABLE has.
        var width = _columnDefinitions
            .Select(c => Quote(c.Name).Length)
            .Append(Quote(RowIdColumn).Length)
            .Max() + 1;

        var lines = new List<string> { "  " + Quote(RowIdColumn).PadRight(width) + "INTEGER PRIMARY KEY" };
        lines.AddRange(_columnDefinitions.Select(c =>
            "  " + Quote(c.Name).PadRight(width) + c.SqliteType + (c.NotNull ? " NOT NULL" : string.Empty)));

        return $"CREATE TABLE IF NOT EXISTS \"{DataTable}\" (" + Newline +
               string.Join("," + Newline, lines) + Newline + ")";
    }

    private static string Quote(string name) => "\"" + name + "\"";

    private static ColumnDefinition Describe(PropertyModel property, bool notNull)
    {
        var sqliteType = SqliteTypeMap.TryGetColumnType(property.Type, out var mapped) ? mapped : "TEXT";
        return new ColumnDefinition(property.StorageName, sqliteType, notNull);
    }

    /// <summary>One data-table column: the storage name, its SQLite type, and whether it is NOT NULL.</summary>
    private sealed record ColumnDefinition(string Name, string SqliteType, bool NotNull);
}
