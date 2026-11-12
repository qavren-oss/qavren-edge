using Qavren.Edge.VectorData.Tests.Fakes;
using Qavren.Edge.VectorData.Tests.Records;
using Xunit;

namespace Qavren.Edge.VectorData.Tests;

/// <summary>
/// What a batch upsert costs beyond the rows it writes. Every record is three statements - the row,
/// the old vector out, the new vector in - and a fourth when the store assigns the key. Those are
/// the same three or four statements for every record of the batch, so they are compiled once.
/// </summary>
public sealed class BatchUpsertTests
{
    private static readonly float[] UnitX = [1f, 0f, 0f, 0f];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ABatchCompilesItsStatementsOnceHoweverManyRecordsItCarries()
    {
        using var host = await VectorTestHost.StartAsync();
        var database = new CommandCountingDatabase(host.Database);
        var collection = new EdgeVectorStoreCollection<string, RawVec>(database, "raw");
        await collection.EnsureCollectionExistsAsync(Token);

        var overhead = await CommandsOfAnEmptyTransactionAsync(database);

        var before = database.CommandsCreated;
        await collection.UpsertAsync(new RawVec { Key = "first", Embedding = UnitX }, Token);
        var forOne = database.CommandsCreated - before - overhead;

        before = database.CommandsCreated;
        await collection.UpsertAsync(
            Enumerable.Range(0, 25).Select(i => new RawVec { Key = "k" + i, Embedding = UnitX }),
            Token);
        var forTwentyFive = database.CommandsCreated - before - overhead;

        // Three, and still three: this was seventy-five, each one a statement SQLite parsed,
        // planned and threw away after a single use.
        Assert.Equal(3, forOne);
        Assert.Equal(3, forTwentyFive);

        Assert.Equal(26L, await host.CountAsync("raw"));
        Assert.Equal(26L, await host.CountAsync("raw_vec"));
    }

    [Fact]
    public async Task AStoreAssignedIntegerKeyIsOneMoreStatementForTheBatchNotOneMorePerRecord()
    {
        using var host = await VectorTestHost.StartAsync();
        var database = new CommandCountingDatabase(host.Database);
        var collection = new EdgeVectorStoreCollection<long, AutoKeyVec>(database, "auto");
        await collection.EnsureCollectionExistsAsync(Token);

        var overhead = await CommandsOfAnEmptyTransactionAsync(database);
        var records = Enumerable.Range(0, 10).Select(_ => new AutoKeyVec { Embedding = UnitX }).ToArray();

        var before = database.CommandsCreated;
        await collection.UpsertAsync(records, Token);

        Assert.Equal(4, database.CommandsCreated - before - overhead);

        // MAX + 1 is read through the same statement each time, and each read sees the row the
        // record before it wrote: ten records are the keys one to ten, in order, not ten ones.
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), records.Select(r => r.Key));
        Assert.Equal(10L, await host.CountAsync("auto"));
        Assert.Equal(10L, await host.CountAsync("auto_vec"));
    }

    [Fact]
    public async Task AKeySetByHandInTheMiddleOfABatchMovesWhereTheNextAssignedOneLands()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = new EdgeVectorStoreCollection<long, AutoKeyVec>(host.Database, "auto");
        await collection.EnsureCollectionExistsAsync(Token);

        // Each assigned key is read off the table as it stands when that record's turn comes. A
        // batch that worked the keys out up front would hand the third record a 2.
        AutoKeyVec[] records =
        [
            new() { Embedding = UnitX },
            new() { Key = 40, Embedding = UnitX },
            new() { Embedding = UnitX },
        ];

        await collection.UpsertAsync(records, Token);

        Assert.Equal([1L, 40L, 41L], records.Select(r => r.Key));
    }

    [Fact]
    public async Task ABatchThatFailsPartWayLeavesNothingBehind()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = new EdgeVectorStoreCollection<string, RawVec>(host.Database, "raw");
        await collection.EnsureCollectionExistsAsync(Token);

        // Staged in SQLite rather than in the input: with the vec0 table gone, the batch fails on
        // its first vector write, which comes after its first data-table write.
        await host.ExecuteAsync("DROP TABLE \"raw_vec\"");

        var ex = await Assert.ThrowsAsync<EdgeVectorStoreException>(() => collection.UpsertAsync(
            [
                new RawVec { Key = "a", Embedding = UnitX },
                new RawVec { Key = "b", Embedding = UnitX },
            ],
            Token));

        Assert.Equal(EdgeVectorStoreOperations.Upsert, ex.OperationName);

        // One transaction for the whole batch, so the row that was written did not stay.
        Assert.Equal(0L, await host.CountAsync("raw"));
    }

    /// <summary>
    /// What one transaction costs before the collection has run anything: Microsoft.Data.Sqlite
    /// issues commands of its own to set the connection up, to begin and to commit.
    /// </summary>
    private static async Task<int> CommandsOfAnEmptyTransactionAsync(CommandCountingDatabase database)
    {
        var before = database.CommandsCreated;
        _ = await database.ExecuteInTransactionAsync((_, _, _) => Task.FromResult(true), Token).ConfigureAwait(false);
        return database.CommandsCreated - before;
    }
}
