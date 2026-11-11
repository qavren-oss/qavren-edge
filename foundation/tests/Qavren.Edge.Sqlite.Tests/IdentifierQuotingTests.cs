using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite.Fts;
using Qavren.Edge.Sqlite.Vec;
using Xunit;

namespace Qavren.Edge.Sqlite.Tests;

/// <summary>
/// The builders write table and column names into SQL text. These tests hold them to two things at
/// once, against the real library: a name that could not be written before now works, and a name
/// that already worked is still written exactly as it was - the text of a trigger is stored in the
/// database file, so "equivalent" is not good enough for a schema somebody already has.
/// </summary>
public class IdentifierQuotingTests
{
    /// <summary>
    /// Column names that are not one plain word, next to three that are. None holds a character an
    /// FTS5 option or a quoted name is spelled with; a name that does has to arrive quoted, and
    /// <see cref="ANameTheCallerAlreadyQuotedIsWrittenAsItWasGiven"/> covers those.
    /// </summary>
    private static readonly string[] AwkwardNames =
    [
        "From", "Order", "Group", "created-at", "my col", "1st", "a$b", "semi;colon", "dot.ted",
        "über", "naïve col", "Title", "body_text", "col2",
    ];

    // ---- What the builders emit, as text -------------------------------------------------------

    [Fact]
    public void AKeywordOrANameThatIsNotOneWordIsQuotedInTheTriggersAndAPlainNameIsLeftAlone()
    {
        var triggers = FtsTable.BuildSyncTriggerSql("notes_fts", "notes", ["From", "created-at", "Title"], "_rowid");

        Assert.Equal(
            "CREATE TRIGGER IF NOT EXISTS \"notes_fts_ai\" AFTER INSERT ON \"notes\" BEGIN " +
            "INSERT INTO \"notes_fts\"(rowid, \"From\", \"created-at\", Title) " +
            "VALUES (new.\"_rowid\", new.\"From\", new.\"created-at\", new.Title); END",
            triggers[0]);

        Assert.Equal(
            "CREATE TRIGGER IF NOT EXISTS \"notes_fts_ad\" AFTER DELETE ON \"notes\" BEGIN " +
            "INSERT INTO \"notes_fts\"(\"notes_fts\", rowid, \"From\", \"created-at\", Title) " +
            "VALUES ('delete', old.\"_rowid\", old.\"From\", old.\"created-at\", old.Title); END",
            triggers[1]);
    }

    [Fact]
    public void Fts5ReadsAKeywordAsAColumnNameSoOnlyANameItCannotReadBareIsQuotedInItsColumnList()
    {
        // The column list is FTS5's to parse, not SQLite's: FROM is a word like any other there,
        // and a standalone table created with it has always worked.
        Assert.Equal(
            "CREATE VIRTUAL TABLE IF NOT EXISTS \"notes_fts\" USING fts5(From, \"created-at\", tokenize='unicode61')",
            FtsTable.BuildCreateSql("notes_fts", ["From", "created-at"]));
    }

    [Fact]
    public void AQuoteInATableNameIsDoubledByEveryBuilder()
    {
        Assert.Equal(
            "CREATE VIRTUAL TABLE IF NOT EXISTS \"we\"\"ird\" USING vec0(embedding float[4] distance_metric=cosine)",
            VecTable.BuildCreateSql("we\"ird", dims: 4));

        Assert.Equal(
            "SELECT rowid, distance FROM \"we\"\"ird\" WHERE embedding MATCH $query AND k = $k",
            Knn.BuildSql("we\"ird"));

        Assert.Equal(
            "CREATE VIRTUAL TABLE IF NOT EXISTS \"we\"\"ird\" USING fts5(body, content='no\"tes', tokenize='unicode61')",
            FtsTable.BuildCreateSql("we\"ird", ["body"], FtsTokenizer.Unicode61, contentTable: "no\"tes"));

        Assert.All(
            FtsTable.BuildSyncTriggerSql("we\"ird", "no\"tes", ["body"]),
            trigger =>
            {
                Assert.Contains("ON \"no\"\"tes\" BEGIN", trigger, StringComparison.Ordinal);
                Assert.Contains("INSERT INTO \"we\"\"ird\"(", trigger, StringComparison.Ordinal);
                Assert.DoesNotContain("\"we\"ird", trigger, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void AnFts5OptionAndAnUnindexedColumnAreStillWhatTheyWereAndNotNamesToQuote()
    {
        // The column list has always been written as given, so it has always been the way to say
        // these. Quoting them would not fail: it would create a column called "detail=column".
        Assert.Equal(
            "CREATE VIRTUAL TABLE IF NOT EXISTS \"notes_fts\" USING fts5(" +
            "title, extra UNINDEXED, detail=column, \"my col\", [other col], tokenize='unicode61')",
            FtsTable.BuildCreateSql(
                "notes_fts",
                ["title", "extra UNINDEXED", "detail=column", "\"my col\"", "[other col]"]));
    }

    [Fact]
    public void AVectorColumnNamedAfterAKeywordIsQuotedInTheKnnQueryAndNothingElseIs()
    {
        Assert.Equal(
            "SELECT rowid, distance FROM \"notes_vec\" WHERE \"order\" MATCH $query AND k = $k",
            Knn.BuildSql("notes_vec", vectorColumn: "order"));

        // A value where a column is wanted, so MATCH has nothing to match against.
        Assert.Contains(
            "WHERE \"current_time\" MATCH",
            Knn.BuildSql("notes_vec", vectorColumn: "current_time"),
            StringComparison.Ordinal);

        // Already the caller's own SQL, and already working.
        Assert.Contains(
            "WHERE \"order\" MATCH",
            Knn.BuildSql("notes_vec", vectorColumn: "\"order\""),
            StringComparison.Ordinal);
        Assert.Contains(
            "WHERE notes_vec.embedding MATCH",
            Knn.BuildSql("notes_vec", vectorColumn: "notes_vec.embedding"),
            StringComparison.Ordinal);
    }

    // ---- The same, run ---------------------------------------------------------------------------

    [Fact]
    public async Task EveryKeywordAndEveryAwkwardNameWorksAsAFullTextColumnAndWhatWorkedBeforeIsUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await EdgeTestHost.StartAsync(cancellationToken: ct);
        await using var hostScope = host.ConfigureAwait(false);
        var connection = await host.Database.OpenConnectionAsync(ct);
        await using var connectionScope = connection.ConfigureAwait(false);

        // Every keyword the loaded library knows, asked of the library itself: the set that cannot
        // stand as a name is a property of the SQLite version, and an upgrade that moves it has to
        // fail here rather than in somebody's schema.
        string[] names = [.. SqliteKeywords(), .. AwkwardNames];
        var problems = new List<string>();

        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];
            var content = "c" + i;
            var fts = "f" + i;
            var quoted = "\"" + name + "\"";

            await connection.ExecuteAsync(
                $"CREATE TABLE \"{content}\"(\"_rowid\" INTEGER PRIMARY KEY, {quoted} TEXT)", cancellationToken: ct);

            // What this package emitted before it quoted anything: the column name, bare, in all
            // three places.
            var oldCreate =
                $"CREATE VIRTUAL TABLE IF NOT EXISTS \"{fts}\" USING fts5({name}, content='{content}', " +
                "content_rowid='_rowid', tokenize='unicode61 remove_diacritics 2')";
            string[] oldTriggers =
            [
                $"CREATE TRIGGER IF NOT EXISTS \"{fts}_ai\" AFTER INSERT ON \"{content}\" BEGIN " +
                $"INSERT INTO \"{fts}\"(rowid, {name}) VALUES (new.\"_rowid\", new.{name}); END",

                $"CREATE TRIGGER IF NOT EXISTS \"{fts}_ad\" AFTER DELETE ON \"{content}\" BEGIN " +
                $"INSERT INTO \"{fts}\"(\"{fts}\", rowid, {name}) VALUES ('delete', old.\"_rowid\", old.{name}); END",

                $"CREATE TRIGGER IF NOT EXISTS \"{fts}_au\" AFTER UPDATE ON \"{content}\" BEGIN " +
                $"INSERT INTO \"{fts}\"(\"{fts}\", rowid, {name}) VALUES ('delete', old.\"_rowid\", old.{name}); " +
                $"INSERT INTO \"{fts}\"(rowid, {name}) VALUES (new.\"_rowid\", new.{name}); END",
            ];

            var create = FtsTable.BuildCreateSql(
                fts,
                [name],
                FtsTokenizer.Unicode61,
                new FtsTableOptions { ContentTable = content, ContentRowId = "_rowid" });
            var triggers = FtsTable.BuildSyncTriggerSql(fts, content, [name], "_rowid");

            var before = await RoundTripAsync(connection, [oldCreate, .. oldTriggers], content, fts, quoted, ct);
            var now = await RoundTripAsync(connection, [create, .. triggers], content, fts, quoted, ct);

            if (now is not null)
            {
                problems.Add($"[{name}] does not work as a full-text column: {now}");
            }

            if (before is null && (create != oldCreate || !triggers.SequenceEqual(oldTriggers)))
            {
                problems.Add($"[{name}] worked bare and is now written differently: {create} | {triggers[0]}");
            }

            // On its own, with no content table and no trigger, the column list is the only place
            // the name appears - and FTS5 reads more names bare than SQLite does.
            var oldStandalone = $"CREATE VIRTUAL TABLE IF NOT EXISTS \"s{i}\" USING fts5({name}, tokenize='unicode61')";
            var standalone = FtsTable.BuildCreateSql("s" + i, [name]);

            if (await RunsAsync(connection, oldStandalone, keep: false, ct) && standalone != oldStandalone)
            {
                problems.Add($"[{name}] worked bare in a standalone table and is now written differently: {standalone}");
            }

            if (!await RunsAsync(connection, standalone, keep: false, ct))
            {
                problems.Add($"[{name}] does not work in a standalone table: {standalone}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Theory]
    [InlineData("\"my col\"")]
    [InlineData("[my col]")]
    [InlineData("`my col`")]
    [InlineData("\"From\"")]
    public async Task ANameTheCallerAlreadyQuotedIsWrittenAsItWasGiven(string quotedName)
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await EdgeTestHost.StartAsync(cancellationToken: ct);
        await using var hostScope = host.ConfigureAwait(false);
        var connection = await host.Database.OpenConnectionAsync(ct);
        await using var connectionScope = connection.ConfigureAwait(false);

        await connection.ExecuteAsync(
            $"CREATE TABLE \"c\"(\"_rowid\" INTEGER PRIMARY KEY, {quotedName} TEXT)", cancellationToken: ct);

        var create = FtsTable.BuildCreateSql(
            "f",
            [quotedName],
            FtsTokenizer.Unicode61,
            new FtsTableOptions { ContentTable = "c", ContentRowId = "_rowid" });
        var triggers = FtsTable.BuildSyncTriggerSql("f", "c", [quotedName], "_rowid");

        // Quoting it again would be a column whose name has quote characters in it. This was the
        // only way to use such a name before, so somebody's schema is spelled exactly like this.
        Assert.Contains($"fts5({quotedName}, content=", create, StringComparison.Ordinal);
        Assert.Contains($"(rowid, {quotedName}) VALUES (new.\"_rowid\", new.{quotedName}); END", triggers[0], StringComparison.Ordinal);

        Assert.Null(await RoundTripAsync(connection, [create, .. triggers], "c", "f", quotedName, ct));
    }

    [Fact]
    public async Task AVectorColumnWithAnyNameVec0AcceptsCanBeSearchedAndWhatWorkedBeforeIsUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await EdgeTestHost.StartAsync(cancellationToken: ct);
        await using var hostScope = host.ConfigureAwait(false);
        var connection = await host.Database.OpenConnectionAsync(ct);
        await using var connectionScope = connection.ConfigureAwait(false);

        string[] names = ["embedding", "vector", "v1", "Embedding_2", .. SqliteKeywords()];
        float[] query = [1f, 0f, 0f, 0f];
        var problems = new List<string>();
        var onlySearchableQuoted = new List<string>();

        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];
            var table = "v" + i;

            // vec0 parses its own column list, and what it takes as a name there is its business.
            // A name it refuses was never a table anybody had.
            if (!await RunsAsync(connection, VecTable.BuildCreateSql(table, dims: 4, vectorColumn: name), keep: true, ct))
            {
                continue;
            }

            await connection.ExecuteAsync(
                $"INSERT INTO \"{table}\"(rowid, \"{name}\") VALUES (1, $v)",
                [new SqliteParameter("$v", VecBlob.From(query))],
                ct);

            var old = $"SELECT rowid, distance FROM \"{table}\" WHERE {name} MATCH $query AND k = $k";
            var sql = Knn.BuildSql(table, vectorColumn: name);

            if (await RunsAsync(connection, old, keep: false, ct, new SqliteParameter("$query", VecBlob.From(query)), new SqliteParameter("$k", 1)))
            {
                if (sql != old)
                {
                    problems.Add($"[{name}] worked bare and is now written differently: {sql}");
                }
            }
            else
            {
                onlySearchableQuoted.Add(name);
            }

            try
            {
                var hits = await Knn.QueryAsync(connection, table, query.AsMemory(), k: 1, vectorColumn: name, cancellationToken: ct);
                if (hits.Count != 1 || hits[0].RowId != 1)
                {
                    problems.Add($"[{name}] found {hits.Count} rows");
                }
            }
            catch (SqliteException ex)
            {
                problems.Add($"[{name}] cannot be searched: {ex.Message}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        // The loop proves nothing about quoting if vec0 turned every such name away at CREATE.
        Assert.Contains("ORDER", onlySearchableQuoted);
    }

    [Fact]
    public async Task TablesWhoseNamesHoldAQuoteAreCreatedSyncedAndSearched()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await EdgeTestHost.StartAsync(cancellationToken: ct);
        await using var hostScope = host.ConfigureAwait(false);
        var connection = await host.Database.OpenConnectionAsync(ct);
        await using var connectionScope = connection.ConfigureAwait(false);

        // Not a name anyone should choose. But it is a name SQLite allows, and written unescaped it
        // ends its own quotation and the rest of it is read as SQL.
        const string Content = "no\"tes";
        const string Fts = "no\"tes fts";
        const string Vec = "no\"tes_vec";

        await connection.ExecuteAsync(
            "CREATE TABLE \"no\"\"tes\"(id INTEGER PRIMARY KEY, body TEXT)", cancellationToken: ct);
        await FtsTable.CreateAsync(connection, Fts, ["body"], FtsTokenizer.Unicode61, Content, ct);
        await FtsTable.CreateSyncTriggersAsync(connection, Fts, Content, ["body"], ct);
        await VecTable.CreateAsync(connection, Vec, dims: 4, cancellationToken: ct);

        await connection.ExecuteAsync(
            "INSERT INTO \"no\"\"tes\"(id, body) VALUES (1, 'hello sqlite')", cancellationToken: ct);
        await connection.ExecuteAsync(
            "INSERT INTO \"no\"\"tes_vec\"(rowid, embedding) VALUES (1, $v)",
            [new SqliteParameter("$v", VecBlob.From([1f, 0f, 0f, 0f]))],
            ct);

        Assert.Equal(
            1L,
            await connection.ScalarAsync<long>(
                "SELECT count(*) FROM \"no\"\"tes fts\" WHERE \"no\"\"tes fts\" MATCH 'sqlite'", cancellationToken: ct));

        float[] query = [1f, 0f, 0f, 0f];
        var hit = Assert.Single(await Knn.QueryAsync(connection, Vec, query.AsMemory(), k: 1, cancellationToken: ct));
        Assert.Equal(1L, hit.RowId);
    }

    private static string[] SqliteKeywords()
    {
        var keywords = new string[SQLitePCL.raw.sqlite3_keyword_count()];
        for (var i = 0; i < keywords.Length; i++)
        {
            _ = SQLitePCL.raw.sqlite3_keyword_name(i, out keywords[i]);
        }

        return keywords;
    }

    /// <summary>
    /// Creates the sidecar and its triggers, then writes, rewrites and deletes one row through the
    /// content table. Returns what went wrong, or null. Everything is undone either way.
    /// </summary>
    private static async Task<string?> RoundTripAsync(
        SqliteConnection connection,
        string[] schema,
        string content,
        string fts,
        string quotedColumn,
        CancellationToken ct)
    {
        await connection.ExecuteAsync("SAVEPOINT attempt", cancellationToken: ct).ConfigureAwait(false);
        try
        {
            foreach (var statement in schema)
            {
                await connection.ExecuteAsync(statement, cancellationToken: ct).ConfigureAwait(false);
            }

            await connection.ExecuteAsync(
                $"INSERT INTO \"{content}\"(\"_rowid\", {quotedColumn}) VALUES (1, 'hello world')",
                cancellationToken: ct).ConfigureAwait(false);
            if (await MatchesAsync("hello").ConfigureAwait(false) != 1)
            {
                return "the inserted row is not in the index";
            }

            await connection.ExecuteAsync(
                $"UPDATE \"{content}\" SET {quotedColumn} = 'goodbye' WHERE \"_rowid\" = 1",
                cancellationToken: ct).ConfigureAwait(false);
            if (await MatchesAsync("hello").ConfigureAwait(false) != 0 || await MatchesAsync("goodbye").ConfigureAwait(false) != 1)
            {
                return "the update did not reach the index";
            }

            await connection.ExecuteAsync($"DELETE FROM \"{content}\"", cancellationToken: ct).ConfigureAwait(false);
            return await MatchesAsync("goodbye").ConfigureAwait(false) == 0 ? null : "the delete did not reach the index";
        }
        catch (SqliteException ex)
        {
            return ex.Message;
        }
        finally
        {
            await connection.ExecuteAsync("ROLLBACK TO attempt", cancellationToken: ct).ConfigureAwait(false);
            await connection.ExecuteAsync("RELEASE attempt", cancellationToken: ct).ConfigureAwait(false);
        }

        Task<long> MatchesAsync(string term) => connection.ScalarAsync<long>(
            $"SELECT count(*) FROM \"{fts}\" WHERE \"{fts}\" MATCH '{term}'", cancellationToken: ct);
    }

    /// <summary>Whether one statement runs. Undone afterwards unless <paramref name="keep"/> is set.</summary>
    private static async Task<bool> RunsAsync(
        SqliteConnection connection,
        string sql,
        bool keep,
        CancellationToken ct,
        params SqliteParameter[] parameters)
    {
        await connection.ExecuteAsync("SAVEPOINT attempt", cancellationToken: ct).ConfigureAwait(false);
        var ran = true;
        try
        {
            // A reader, read to the end: a SELECT that fails on its first step has not failed until
            // somebody steps it.
            _ = await connection.QueryAsync(sql, _ => 0, parameters, ct).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            ran = false;
        }

        if (!ran || !keep)
        {
            await connection.ExecuteAsync("ROLLBACK TO attempt", cancellationToken: ct).ConfigureAwait(false);
        }

        await connection.ExecuteAsync("RELEASE attempt", cancellationToken: ct).ConfigureAwait(false);
        return ran;
    }
}
