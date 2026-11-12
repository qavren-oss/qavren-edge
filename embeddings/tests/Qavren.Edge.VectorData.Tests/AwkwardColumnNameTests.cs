using System.Linq.Expressions;
using Microsoft.Extensions.VectorData;
using Qavren.Edge.VectorData.Internal;
using Qavren.Edge.VectorData.Tests.Fakes;
using Qavren.Edge.VectorData.Tests.Records;
using Xunit;

namespace Qavren.Edge.VectorData.Tests;

/// <summary>
/// A storage name is the developer's to choose, and SQLite lets a column be called nearly anything.
/// The data table has always been written with its names quoted; the full-text sidecar, its
/// triggers, the upsert's parameters and the hybrid search's column filter were not, so a record
/// stored under <c>From</c> or <c>created-at</c> failed at whichever of those it reached first.
/// </summary>
public sealed class AwkwardColumnNameTests
{
    private static readonly float[] UnitX = [1f, 0f, 0f, 0f];
    private static readonly float[] UnitY = [0f, 1f, 0f, 0f];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static EdgeVectorSchema MailSchema => new(ModelFactory.ModelFor<AwkwardNote>("mail"), "mail");

    private static string Lines(params string[] lines) => string.Join("\n", lines);

    // ---- The text -------------------------------------------------------------------------------

    [Fact]
    public void TheSidecarAndItsTriggersQuoteANameWhereverSqliteOrFts5WouldNotReadItBare()
    {
        var sql = MailSchema.BuildCreateSql();

        // FTS5 parses its own column list and reads a keyword as a name, so From and OR stay bare
        // there; two words do not.
        Assert.Equal(
            "CREATE VIRTUAL TABLE IF NOT EXISTS \"mail_fts\" USING fts5(From, \"message body\", OR, " +
            "content='mail', content_rowid='_rowid', tokenize='unicode61 remove_diacritics 2')",
            sql[4]);

        // The trigger body is SQLite's, where all three need their quotes.
        Assert.Equal(
            "CREATE TRIGGER IF NOT EXISTS \"mail_fts_ai\" AFTER INSERT ON \"mail\" BEGIN " +
            "INSERT INTO \"mail_fts\"(rowid, \"From\", \"message body\", \"OR\") " +
            "VALUES (new.\"_rowid\", new.\"From\", new.\"message body\", new.\"OR\"); END",
            sql[5]);
    }

    [Fact]
    public void TheUpsertBindsByPositionOnceAnyColumnNameIsNotAPlainWord()
    {
        // "$created-at" is the parameter $created, minus the column at. A name is not something a
        // parameter can be called after unless every name in the row is a plain word - which is the
        // case GoldenSqlTests pins, and that statement is unchanged.
        Assert.Equal(
            Lines(
                "INSERT INTO \"mail\"(\"note-id\",\"From\",\"created-at\",\"message body\",\"OR\",\"it's [odd]\") VALUES ($p0,$p1,$p2,$p3,$p4,$p5)",
                "  ON CONFLICT(\"note-id\") DO UPDATE SET \"From\"=excluded.\"From\",\"created-at\"=excluded.\"created-at\",\"message body\"=excluded.\"message body\",\"OR\"=excluded.\"OR\",\"it's [odd]\"=excluded.\"it's [odd]\"",
                "  RETURNING \"_rowid\""),
            MailSchema.BuildUpsertSql());
    }

    [Fact]
    public void AFilterOverAColumnWhoseNameHoldsAQuoteWritesTheQuoteDoubled()
    {
        // No schema accepts such a name, so no collection gets this far. The translator is public
        // and takes any model, though, and it writes the name between quotes like everything else.
        Expression<Func<QuotedColumnRecord, bool>> filter = r => r.Weird == "x";

        var sql = new EdgeFilterTranslator().Translate(filter, ModelFactory.ModelFor<QuotedColumnRecord>("q")).Sql;

        Assert.Equal("\"we\"\"ird\" = 'x'", sql);
        Assert.Throws<ArgumentException>(
            () => new EdgeVectorSchema(ModelFactory.ModelFor<QuotedColumnRecord>("q"), "q"));
    }

    // ---- The same, run ---------------------------------------------------------------------------

    [Fact]
    public async Task ARecordStoredUnderKeywordsAndNamesThatAreNotOneWordGoesThroughEveryOperation()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = new EdgeVectorStoreCollection<string, AwkwardNote>(host.Database, "mail");
        await collection.EnsureCollectionExistsAsync(Token);

        await collection.UpsertAsync(
            [
                new AwkwardNote
                {
                    Key = "m1", Sender = "alice", Created = "2026-01-01", Body = "the roof is leaking",
                    Alternative = "plumber", Odd = "x", Embedding = UnitX,
                },
                new AwkwardNote
                {
                    Key = "m2", Sender = "bob", Created = "2026-02-01", Body = "the gutter is blocked",
                    Alternative = "roofer", Odd = null, Embedding = UnitY,
                },
            ],
            Token);

        // Read back by key, every column and the vector.
        var read = await collection.GetAsync("m1", new RecordRetrievalOptions { IncludeVectors = true }, Token);
        Assert.NotNull(read);
        Assert.Equal("alice", read.Sender);
        Assert.Equal("2026-01-01", read.Created);
        Assert.Equal("the roof is leaking", read.Body);
        Assert.Equal("plumber", read.Alternative);
        Assert.Equal("x", read.Odd);
        Assert.Equal(UnitX, read.Embedding.ToArray());

        // A filter over the hyphenated column and the keyword one, with a captured value.
        var month = "2026-02-01";
        var filtered = await collection
            .GetAsync(n => n.Created == month && n.Sender != "alice", top: 5, cancellationToken: Token)
            .ToListAsync(Token);
        Assert.Equal("m2", Assert.Single(filtered).Key);

        // KNN behind a pre-filter on the oddest name of the lot.
        var near = await collection
            .SearchAsync(
                new ReadOnlyMemory<float>(UnitY),
                top: 2,
                new VectorSearchOptions<AwkwardNote> { Filter = n => n.Odd == "x" },
                Token)
            .ToListAsync(Token);
        Assert.Equal("m1", Assert.Single(near).Record.Key);

        // Hybrid, with the keyword lane narrowed to each full-text column in turn. The query vector
        // is m2's own, so m1 only comes first when the keyword lane found it - and it only finds
        // it in the column the word is actually in.
        Assert.Equal("m1", await TopOfHybridAsync(collection, "alice", n => n.Sender));
        Assert.Equal("m1", await TopOfHybridAsync(collection, "leaking", n => n.Body));
        Assert.Equal("m1", await TopOfHybridAsync(collection, "plumber", n => n.Alternative));
        Assert.Equal("m2", await TopOfHybridAsync(collection, "alice", n => n.Body));
        Assert.Equal("m2", await TopOfHybridAsync(collection, "leaking", n => n.Alternative));

        // An update is the same row, and the index follows it through the update trigger.
        await collection.UpsertAsync(
            new AwkwardNote
            {
                Key = "m1",
                Sender = "carol",
                Created = "2026-01-02",
                Body = "all fixed now",
                Alternative = "nobody",
                Odd = "y",
                Embedding = UnitX,
            },
            Token);

        Assert.Equal(2L, await host.CountAsync("mail"));
        Assert.Equal("m1", await TopOfHybridAsync(collection, "carol", n => n.Sender));
        Assert.Equal("m2", await TopOfHybridAsync(collection, "alice", n => n.Sender));

        // A delete goes through the delete trigger, and takes the vector with it.
        await collection.DeleteAsync("m1", Token);

        Assert.Equal(1L, await host.CountAsync("mail"));
        Assert.Equal(1L, await host.CountAsync("mail_vec"));
        Assert.Equal(1L, await host.CountAsync("mail_fts_docsize"));

        await collection.EnsureCollectionDeletedAsync(Token);
        Assert.False(await collection.CollectionExistsAsync(Token));
    }

    [Fact]
    public async Task ACollectionCreatedByItsMigrationTakesTheSameNames()
    {
        // The other way a collection's DDL reaches the database: as one migration, at startup.
        using var host = await VectorTestHost.StartAsync(edge =>
        {
            edge.AddVectorStore();
            edge.AddVectorCollectionMigration<string, AwkwardNote>(version: 1, "mail");
        });

        var collection = new EdgeVectorStoreCollection<string, AwkwardNote>(host.Database, "mail");
        await collection.UpsertAsync(
            new AwkwardNote { Key = "m1", Sender = "alice", Body = "hello", Alternative = "x", Embedding = UnitX },
            Token);

        Assert.Equal(1L, await host.CountAsync("mail_fts_docsize"));
    }

    [Fact]
    public async Task AFullTextColumnCalledRankIsFts5sOwnNameAndIsTurnedAwayWhenTheCollectionIsCreated()
    {
        // The one kind of name quoting cannot rescue. FTS5 keeps "rank" and "rowid" for itself and
        // compares them after stripping any quotes, so this still fails - at create, in one
        // transaction, with FTS5's own sentence.
        using var host = await VectorTestHost.StartAsync();
        var collection = new EdgeVectorStoreCollection<string, RankedNote>(host.Database, "ranked");

        var ex = await Assert.ThrowsAsync<EdgeVectorStoreException>(
            () => collection.EnsureCollectionExistsAsync(Token));

        Assert.Contains("reserved fts5 column name", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ranked", await host.AllTableNamesAsync());
    }

    private static async Task<string> TopOfHybridAsync(
        EdgeVectorStoreCollection<string, AwkwardNote> collection,
        string keyword,
        Expression<Func<AwkwardNote, object?>> column)
    {
        var hits = await collection
            .HybridSearchAsync(
                new ReadOnlyMemory<float>(UnitY),
                [keyword],
                top: 2,
                new HybridSearchOptions<AwkwardNote> { AdditionalProperty = column },
                Token)
            .ToListAsync(Token)
            .ConfigureAwait(false);

        return hits[0].Record.Key;
    }
}
