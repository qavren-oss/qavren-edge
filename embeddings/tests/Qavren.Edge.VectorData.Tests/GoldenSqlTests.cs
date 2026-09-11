using Microsoft.Extensions.VectorData.ProviderServices;
using Qavren.Edge.VectorData.Tests.Records;
using Xunit;

namespace Qavren.Edge.VectorData.Tests;

/// <summary>
/// Byte-for-byte assertions on every statement the provider emits. Nothing here opens a connection:
/// this is pure string and model work, which is what makes it a golden-SQL suite. A diff in any of
/// these strings is a change to the provider's visible contract and has to be made on purpose.
/// </summary>
public class GoldenSqlTests
{
    private const string Filter = EdgeVectorSchema.FilterPlaceholder;

    private static CollectionModel NoteModel => ModelFactory.ModelFor<Note>();

    private static EdgeVectorSchema NoteSchema => new(NoteModel, "notes");

    private static string Lines(params string[] lines) => string.Join("\n", lines);

    // ---- CREATE -----------------------------------------------------------------------------

    [Fact]
    public void CreateSqlIsTheFiveStatementsPlusFourTriggersInOrder()
    {
        var sql = EdgeVectorSchema.BuildCreateSql(NoteModel, "notes");

        Assert.Equal(
            [
                Lines(
                    "CREATE TABLE IF NOT EXISTS \"notes\" (",
                    "  \"_rowid\" INTEGER PRIMARY KEY,",
                    "  \"Key\"    TEXT NOT NULL,",
                    "  \"Tag\"    TEXT,",
                    "  \"Title\"  TEXT,",
                    "  \"Body\"   TEXT",
                    ")"),
                "CREATE UNIQUE INDEX IF NOT EXISTS \"notes_Key_index\" ON \"notes\"(\"Key\")",
                "CREATE INDEX IF NOT EXISTS \"notes_Tag_index\" ON \"notes\"(\"Tag\")",
                "CREATE VIRTUAL TABLE IF NOT EXISTS \"notes_vec\" USING vec0(embedding float[384] distance_metric=cosine, chunk_size=256)",
                "CREATE VIRTUAL TABLE IF NOT EXISTS \"notes_fts\" USING fts5(Title, Body, content='notes', content_rowid='_rowid', tokenize='unicode61 remove_diacritics 2')",
                "CREATE TRIGGER IF NOT EXISTS \"notes_fts_ai\" AFTER INSERT ON \"notes\" BEGIN INSERT INTO \"notes_fts\"(rowid, Title, Body) VALUES (new.\"_rowid\", new.Title, new.Body); END",
                "CREATE TRIGGER IF NOT EXISTS \"notes_fts_ad\" AFTER DELETE ON \"notes\" BEGIN INSERT INTO \"notes_fts\"(\"notes_fts\", rowid, Title, Body) VALUES ('delete', old.\"_rowid\", old.Title, old.Body); END",
                "CREATE TRIGGER IF NOT EXISTS \"notes_fts_au\" AFTER UPDATE ON \"notes\" BEGIN INSERT INTO \"notes_fts\"(\"notes_fts\", rowid, Title, Body) VALUES ('delete', old.\"_rowid\", old.Title, old.Body); INSERT INTO \"notes_fts\"(rowid, Title, Body) VALUES (new.\"_rowid\", new.Title, new.Body); END",
                Lines(
                    "CREATE TRIGGER IF NOT EXISTS \"notes_vec_ad\" AFTER DELETE ON \"notes\" BEGIN",
                    "  DELETE FROM \"notes_vec\" WHERE rowid = old.\"_rowid\"; END"),
            ],
            sql);
    }

    [Fact]
    public void TheKeyColumnKeepsItsOwnNameAndIsNotRenamedToTheReservedRowIdAlias()
    {
        // MEVD's CollectionModelBuildingOptions.ReservedKeyStorageName does not mean "reserve this
        // name against the key" - it means "the key's storage name IS this". Setting it to "_rowid"
        // emits the column twice. The reservation is enforced in ValidateProperty instead.
        Assert.Equal("Key", NoteModel.KeyProperty.StorageName);
        Assert.Equal("Key", NoteSchema.KeyColumn);
        Assert.Equal("_rowid", NoteSchema.RowIdColumn);
    }

    [Fact]
    public void ACollectionWithNoFullTextPropertyEmitsNoSidecarAndNoSyncTriggers()
    {
        var sql = EdgeVectorSchema.BuildCreateSql(ModelFactory.ModelFor<PlainNote>(), "notes");

        Assert.Equal(
            [
                Lines(
                    "CREATE TABLE IF NOT EXISTS \"notes\" (",
                    "  \"_rowid\" INTEGER PRIMARY KEY,",
                    "  \"Key\"    TEXT NOT NULL,",
                    "  \"Tag\"    TEXT",
                    ")"),
                "CREATE UNIQUE INDEX IF NOT EXISTS \"notes_Key_index\" ON \"notes\"(\"Key\")",
                "CREATE INDEX IF NOT EXISTS \"notes_Tag_index\" ON \"notes\"(\"Tag\")",
                "CREATE VIRTUAL TABLE IF NOT EXISTS \"notes_vec\" USING vec0(embedding float[384] distance_metric=cosine, chunk_size=256)",
                Lines(
                    "CREATE TRIGGER IF NOT EXISTS \"notes_vec_ad\" AFTER DELETE ON \"notes\" BEGIN",
                    "  DELETE FROM \"notes_vec\" WHERE rowid = old.\"_rowid\"; END"),
            ],
            sql);

        Assert.Null(new EdgeVectorSchema(ModelFactory.ModelFor<PlainNote>(), "notes").FullTextTable);
    }

    [Fact]
    public void EverySupportedClrTypeGetsItsSqliteColumnType()
    {
        var sql = EdgeVectorSchema.BuildCreateSql(
            ModelFactory.ModelFor<TypedRecord>("typed", typeof(Guid)),
            "typed")[0];

        Assert.Equal(
            Lines(
                "CREATE TABLE IF NOT EXISTS \"typed\" (",
                "  \"_rowid\"      INTEGER PRIMARY KEY,",
                "  \"Key\"         TEXT NOT NULL,",
                "  \"Count\"       INTEGER,",
                "  \"Size\"        INTEGER,",
                "  \"Small\"       INTEGER,",
                "  \"Flag\"        INTEGER,",
                "  \"Ratio\"       REAL,",
                "  \"Precise\"     REAL,",
                "  \"Text\"        TEXT,",
                "  \"Correlation\" TEXT,",
                "  \"Created\"     TEXT,",
                "  \"Updated\"     TEXT,",
                "  \"Day\"         TEXT,",
                "  \"Time\"        TEXT,",
                "  \"Payload\"     BLOB",
                ")"),
            sql);
    }

    // ---- The 8192 ceiling, which is SP1's and not ours --------------------------------------

    [Fact]
    public void ADeclaredWidthAbove8192IsRejectedBySp1sVecTableAndNotByUs()
    {
        // SQLITE_VEC_VEC0_MAX_DIMENSIONS. Deliberately an ArgumentOutOfRangeException from
        // Qavren.Edge.Sqlite and NOT an EdgeVectorModelException: spec 12.2 puts this ceiling in
        // SP1's VecTable, and spec 15.1 allocates this package no code for it. If this test ever
        // starts seeing an EdgeVectorModelException, someone has written a second ceiling.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => EdgeVectorSchema.BuildCreateSql(
                ModelFactory.ModelFor<WideVector>("wide"), "wide", new EdgeVectorStoreOptions()));

        Assert.Equal("dims", ex.ParamName);
        Assert.Contains("8192", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclaredWidthOfExactly8192IsAccepted()
    {
        var sql = EdgeVectorSchema.BuildCreateSql(
            ModelFactory.ModelFor<EdgeWidthVector>("edge"), "edge", new EdgeVectorStoreOptions());

        Assert.Contains("float[8192]", string.Join("\n", sql), StringComparison.Ordinal);
    }

    // ---- DROP -------------------------------------------------------------------------------

    [Fact]
    public void DropSqlIsTheReverseOfCreateWithTheTriggersFirst()
    {
        Assert.Equal(
            [
                "DROP TRIGGER IF EXISTS \"notes_vec_ad\"",
                "DROP TRIGGER IF EXISTS \"notes_fts_ad\"",
                "DROP TRIGGER IF EXISTS \"notes_fts_au\"",
                "DROP TRIGGER IF EXISTS \"notes_fts_ai\"",
                "DROP TABLE IF EXISTS \"notes_fts\"",
                "DROP TABLE IF EXISTS \"notes_vec\"",
                "DROP TABLE IF EXISTS \"notes\"",
            ],
            NoteSchema.BuildDropSql());
    }

    [Fact]
    public void DropSqlOfACollectionWithNoFullTextTableNamesNoFtsObjects()
    {
        // An unconditional DROP TABLE IF EXISTS "" would be a syntax error, not a no-op.
        Assert.Equal(
            [
                "DROP TRIGGER IF EXISTS \"notes_vec_ad\"",
                "DROP TABLE IF EXISTS \"notes_vec\"",
                "DROP TABLE IF EXISTS \"notes\"",
            ],
            new EdgeVectorSchema(ModelFactory.ModelFor<PlainNote>(), "notes").BuildDropSql());
    }

    // ---- KNN --------------------------------------------------------------------------------

    [Fact]
    public void KnnSqlWithFilterThresholdAndVectors()
    {
        Assert.Equal(
            Lines(
                "SELECT d.\"_rowid\", d.\"Key\", d.\"Tag\", d.\"Title\", d.\"Body\", v.distance, v.\"embedding\"",
                "FROM \"notes_vec\" v",
                "JOIN \"notes\" d ON d.\"_rowid\" = v.rowid",
                "WHERE v.\"embedding\" MATCH $query AND v.k = $k",
                "  AND v.rowid IN (SELECT \"_rowid\" FROM \"notes\" WHERE " + Filter + ")",
                "  AND v.distance <= $scoreThreshold",
                "ORDER BY v.distance"),
            NoteSchema.BuildKnnSql(hasFilter: true, hasScoreThreshold: true, includeVectors: true));
    }

    [Fact]
    public void KnnSqlWithNothingOptionalOn()
    {
        Assert.Equal(
            Lines(
                "SELECT d.\"_rowid\", d.\"Key\", d.\"Tag\", d.\"Title\", d.\"Body\", v.distance",
                "FROM \"notes_vec\" v",
                "JOIN \"notes\" d ON d.\"_rowid\" = v.rowid",
                "WHERE v.\"embedding\" MATCH $query AND v.k = $k",
                "ORDER BY v.distance"),
            NoteSchema.BuildKnnSql(hasFilter: false, hasScoreThreshold: false, includeVectors: false));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void KnnSqlBracketsAppearOnlyWhenAskedFor(bool hasFilter, bool hasScoreThreshold, bool includeVectors)
    {
        var expected = new List<string>
        {
            "SELECT d.\"_rowid\", d.\"Key\", d.\"Tag\", d.\"Title\", d.\"Body\", v.distance" +
                (includeVectors ? ", v.\"embedding\"" : string.Empty),
            "FROM \"notes_vec\" v",
            "JOIN \"notes\" d ON d.\"_rowid\" = v.rowid",
            "WHERE v.\"embedding\" MATCH $query AND v.k = $k",
        };

        if (hasFilter)
        {
            expected.Add("  AND v.rowid IN (SELECT \"_rowid\" FROM \"notes\" WHERE " + Filter + ")");
        }

        if (hasScoreThreshold)
        {
            expected.Add("  AND v.distance <= $scoreThreshold");
        }

        expected.Add("ORDER BY v.distance");

        Assert.Equal(
            string.Join("\n", expected),
            NoteSchema.BuildKnnSql(hasFilter, hasScoreThreshold, includeVectors));
    }

    // ---- Hybrid RRF -------------------------------------------------------------------------

    [Fact]
    public void HybridRrfSqlWithFilterAndVectors()
    {
        Assert.Equal(
            Lines(
                "WITH vec AS (",
                "  SELECT v.rowid AS id,",
                "         ROW_NUMBER() OVER (ORDER BY v.distance) AS rank,",
                "         v.distance AS distance",
                "  FROM \"notes_vec\" v",
                "  WHERE v.\"embedding\" MATCH $query AND v.k = $cand",
                "    AND v.rowid IN (SELECT \"_rowid\" FROM \"notes\" WHERE " + Filter + ")",
                "),",
                "fts AS (",
                "  SELECT f.rowid AS id,",
                "         ROW_NUMBER() OVER (ORDER BY f.rank) AS rank,",
                "         f.rank AS bm25",
                "  FROM \"notes_fts\" f",
                "  WHERE f.\"notes_fts\" MATCH $keywords",
                "    AND f.rowid IN (SELECT \"_rowid\" FROM \"notes\" WHERE " + Filter + ")",
                "  ORDER BY f.rank",
                "  LIMIT $cand",
                ")",
                "SELECT d.\"_rowid\", d.\"Key\", d.\"Tag\", d.\"Title\", d.\"Body\",",
                "       COALESCE(1.0 / ($rrfK + fts.rank), 0.0) * $wKeyword",
                "     + COALESCE(1.0 / ($rrfK + vec.rank), 0.0) * $wVector AS score,",
                "       vec.distance, fts.bm25, nv.\"embedding\"",
                "FROM fts",
                "FULL OUTER JOIN vec ON vec.id = fts.id",
                "JOIN \"notes\" d ON d.\"_rowid\" = COALESCE(fts.id, vec.id)",
                "LEFT JOIN \"notes_vec\" nv ON nv.rowid = d.\"_rowid\"",
                "ORDER BY score DESC",
                "LIMIT $top OFFSET $skip"),
            NoteSchema.BuildHybridRrfSql(hasFilter: true, includeVectors: true));
    }

    [Fact]
    public void HybridRrfSqlWithNeitherFilterNorVectors()
    {
        Assert.Equal(
            Lines(
                "WITH vec AS (",
                "  SELECT v.rowid AS id,",
                "         ROW_NUMBER() OVER (ORDER BY v.distance) AS rank,",
                "         v.distance AS distance",
                "  FROM \"notes_vec\" v",
                "  WHERE v.\"embedding\" MATCH $query AND v.k = $cand",
                "),",
                "fts AS (",
                "  SELECT f.rowid AS id,",
                "         ROW_NUMBER() OVER (ORDER BY f.rank) AS rank,",
                "         f.rank AS bm25",
                "  FROM \"notes_fts\" f",
                "  WHERE f.\"notes_fts\" MATCH $keywords",
                "  ORDER BY f.rank",
                "  LIMIT $cand",
                ")",
                "SELECT d.\"_rowid\", d.\"Key\", d.\"Tag\", d.\"Title\", d.\"Body\",",
                "       COALESCE(1.0 / ($rrfK + fts.rank), 0.0) * $wKeyword",
                "     + COALESCE(1.0 / ($rrfK + vec.rank), 0.0) * $wVector AS score,",
                "       vec.distance, fts.bm25",
                "FROM fts",
                "FULL OUTER JOIN vec ON vec.id = fts.id",
                "JOIN \"notes\" d ON d.\"_rowid\" = COALESCE(fts.id, vec.id)",
                "ORDER BY score DESC",
                "LIMIT $top OFFSET $skip"),
            NoteSchema.BuildHybridRrfSql(hasFilter: false, includeVectors: false));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TheIncludeVectorsBracketIsBothHalvesOrNeither(bool hasFilter, bool includeVectors)
    {
        // A LEFT JOIN with no matching projection costs a join, returns no vectors and throws
        // nothing, because VectorSearchResult simply leaves the vector property at its default.
        var sql = NoteSchema.BuildHybridRrfSql(hasFilter, includeVectors);

        Assert.Equal(includeVectors, sql.Contains("LEFT JOIN \"notes_vec\" nv ON nv.rowid = d.\"_rowid\"", StringComparison.Ordinal));
        Assert.Equal(includeVectors, sql.Contains("       vec.distance, fts.bm25, nv.\"embedding\"", StringComparison.Ordinal));
        Assert.Equal(hasFilter, sql.Contains("    AND v.rowid IN (", StringComparison.Ordinal));
        Assert.Equal(hasFilter, sql.Contains("    AND f.rowid IN (", StringComparison.Ordinal));
    }

    [Fact]
    public void TheKeywordLaneMatchesOnTheHiddenColumnQualifiedByTheAlias()
    {
        // FTS5 gives every table a HIDDEN COLUMN named after the table, and
        // "<name> MATCH <expr>" is an ordinary comparison against that column - it is not a
        // table-level operator. So inside a CTE that says FROM "notes_fts" f, the bare alias
        // resolves as a column reference and SQLite answers "no such column: f"; the column has to
        // be named, qualified by the alias. Measured against sqlite 3.53.4 + FTS5 on 2026-09-11.
        var sql = NoteSchema.BuildHybridRrfSql(hasFilter: false, includeVectors: false);

        Assert.Contains("  WHERE f.\"notes_fts\" MATCH $keywords", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE f MATCH", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BothLanesRankAscendingBecauseBm25AndDistanceAreBothBetterWhenLower()
    {
        // FTS5's bm25() is the standard score multiplied by -1, so a better match is numerically
        // lower; vec0's distance is a distance. The hidden rank column is used rather than calling
        // bm25(f), which SQLite's own docs say is faster.
        var sql = NoteSchema.BuildHybridRrfSql(hasFilter: false, includeVectors: false);

        Assert.Contains("ROW_NUMBER() OVER (ORDER BY v.distance) AS rank", sql, StringComparison.Ordinal);
        Assert.Contains("ROW_NUMBER() OVER (ORDER BY f.rank) AS rank", sql, StringComparison.Ordinal);
        Assert.Contains("  ORDER BY f.rank\n", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("bm25(f)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER BY f.rank DESC", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFusedScoreIsASimilaritySoItSortsDescending_TheOppositePolarityToSearchAsync()
    {
        var hybrid = NoteSchema.BuildHybridRrfSql(hasFilter: false, includeVectors: false);
        var knn = NoteSchema.BuildKnnSql(hasFilter: false, hasScoreThreshold: false, includeVectors: false);

        Assert.EndsWith("ORDER BY score DESC\nLIMIT $top OFFSET $skip", hybrid, StringComparison.Ordinal);
        Assert.EndsWith("ORDER BY v.distance", knn, StringComparison.Ordinal);
    }

    [Fact]
    public void HybridSearchOnACollectionWithNoFullTextPropertyFailsBeforeAnySql()
    {
        var schema = new EdgeVectorSchema(ModelFactory.ModelFor<PlainNote>(), "notes");
        var ex = Assert.Throws<EdgeVectorModelException>(() => schema.BuildHybridRrfSql(false, false));

        Assert.Equal(EdgeErrorCode.FullTextPropertyMissing, ex.Code);
        Assert.Contains("IsFullTextIndexed", ex.Message, StringComparison.Ordinal);
    }

    // ---- UPSERT -----------------------------------------------------------------------------

    [Fact]
    public void UpsertSqlConflictsOnTheKeyAndReturnsTheRowId()
    {
        Assert.Equal(
            Lines(
                "INSERT INTO \"notes\"(\"Key\",\"Tag\",\"Title\",\"Body\") VALUES ($Key,$Tag,$Title,$Body)",
                "  ON CONFLICT(\"Key\") DO UPDATE SET \"Tag\"=excluded.\"Tag\",\"Title\"=excluded.\"Title\",\"Body\"=excluded.\"Body\"",
                "  RETURNING \"_rowid\""),
            NoteSchema.BuildUpsertSql());
    }

    // ---- MATCH expressions ------------------------------------------------------------------

    [Fact]
    public void KeywordsAreQuotedSoFts5OperatorsInsideThemAreInert()
    {
        Assert.Equal(
            "\"a\"\"b\" OR \"OR\" OR \"x*\"",
            EdgeVectorSchema.BuildMatchExpression(["a\"b", "OR", "x*"], KeywordCombinator.Or));
    }

    [Fact]
    public void ColumnFilterNarrowsTheExpression()
    {
        Assert.Equal(
            "{Body} : (\"term one\" OR \"term two\")",
            EdgeVectorSchema.BuildMatchExpression(["term one", "term two"], KeywordCombinator.Or, "Body"));
    }

    [Fact]
    public void TheAndCombinatorJoinsWithAnd()
    {
        Assert.Equal(
            "\"alpha\" AND \"beta\"",
            EdgeVectorSchema.BuildMatchExpression(["alpha", "beta"], KeywordCombinator.And));
    }

    [Fact]
    public void AMatchExpressionNeedsAtLeastOneKeyword() =>
        Assert.Throws<ArgumentException>(
            () => EdgeVectorSchema.BuildMatchExpression([], KeywordCombinator.Or));

    // ---- Defaults that change behaviour ------------------------------------------------------

    [Fact]
    public void TheRrfParameterValuesOfADefaultConfiguredStore()
    {
        // The SQL is parameterised, so a wrong default would not change a single character of it.
        var options = new EdgeVectorStoreOptions();

        Assert.Equal(60, options.Rrf.K);
        Assert.Equal(1.0, options.Rrf.VectorWeight);
        Assert.Equal(1.0, options.Rrf.KeywordWeight);
        Assert.Equal(4, options.Rrf.CandidateMultiplier);
        Assert.Equal((10 + 5) * 4, options.Rrf.ResolveCandidateCount(top: 10, skip: 5));
        Assert.Equal(4096, options.Rrf.ResolveCandidateCount(top: 4000, skip: 1000));
    }

    [Fact]
    public void TheFtsDefaultsAreTheOnesRenderedIntoTheGoldenDdl()
    {
        var options = new EdgeVectorStoreOptions();

        Assert.Equal(Sqlite.Fts.FtsTokenizer.Unicode61, options.FullTextTokenizer);
        Assert.Equal(2, options.FullTextRemoveDiacritics);
        Assert.Equal(256, options.ChunkSize);
        Assert.Equal(KeywordCombinator.Or, options.KeywordCombinator);
        Assert.Equal("{0}_vec", options.VectorTableNameFormat);
        Assert.Equal("{0}_fts", options.FullTextTableNameFormat);
        Assert.False(options.IncludeRowCountsInDiagnostics);
    }

    [Fact]
    public void ChangingTheDiacriticsDefaultChangesTheEmittedTokenizeClause()
    {
        var sql = EdgeVectorSchema.BuildCreateSql(
            NoteModel,
            "notes",
            new EdgeVectorStoreOptions { FullTextRemoveDiacritics = 0 });

        Assert.Contains(
            "tokenize='unicode61 remove_diacritics 0'",
            string.Join("\n", sql),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheQueryGeneratorServiceKeyIsTheDocumentedLiteral() =>
        Assert.Equal("qavren.edge.query", EdgeVectorData.QueryGeneratorServiceKey);
}
