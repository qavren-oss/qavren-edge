using Qavren.Edge.VectorData.Tests.Fakes;
using Qavren.Edge.VectorData.Tests.Records;
using Xunit;

namespace Qavren.Edge.VectorData.Tests;

/// <summary>
/// The exclusion in <c>ListCollectionNamesAsync</c> is structural, not by name, and these tests
/// assert <b>both</b> directions - which is the only way to catch a name-prefix implementation,
/// because such an implementation passes one and fails the other.
/// </summary>
public sealed class ListCollectionNamesTests
{
    private static readonly string[] JustNotes = ["notes"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASidecarWithNoConventionalSuffixIsStillHidden()
    {
        using var host = await VectorTestHost.StartAsync();

        // VectorTableName lets a sidecar be called anything, so a name-prefix filter would leak
        // this one into the listing.
        var collection = new EdgeVectorStoreCollection<string, PlainNote>(
            host.Database,
            "notes",
            new EdgeVectorStoreCollectionOptions { VectorTableName = "zzz" });
        await collection.EnsureCollectionExistsAsync(Token);

        var names = await Store(host).ListCollectionNamesAsync(Token).ToListAsync(Token);

        Assert.Contains("notes", names);
        Assert.DoesNotContain("zzz", names);
        Assert.DoesNotContain(names, n => n.StartsWith("zzz_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AUserTableLiterallyCalledFooVecIsStillListed()
    {
        using var host = await VectorTestHost.StartAsync();

        // A consumer's own data table may legitimately be called foo_vec, and no vec0 table named
        // "foo" exists, so nothing structural hides it.
        await host.ExecuteAsync("CREATE TABLE \"foo_vec\" (\"id\" INTEGER PRIMARY KEY, \"x\" TEXT)");

        var names = await Store(host).ListCollectionNamesAsync(Token).ToListAsync(Token);

        Assert.Contains("foo_vec", names);
    }

    [Fact]
    public async Task ATableNamedAfterAVec0SidecarButNotOneOfItsShadowsIsStillListed()
    {
        using var host = await VectorTestHost.StartAsync();

        var collection = new EdgeVectorStoreCollection<string, PlainNote>(host.Database, "notes");
        await collection.EnsureCollectionExistsAsync(Token);

        // "notes_vec" IS a vec0 virtual table here, so "a child of a vec0 table" would hide this
        // one - the same false negative the foo_vec test guards in the other direction. The rule
        // is vec0's KNOWN child names only, exactly as spec 8 states FTS5's five.
        await host.ExecuteAsync("CREATE TABLE \"notes_vec_archive\" (\"id\" INTEGER PRIMARY KEY, \"x\" TEXT)");

        var names = await Store(host).ListCollectionNamesAsync(Token).ToListAsync(Token);

        Assert.Contains("notes_vec_archive", names);
        Assert.DoesNotContain("notes_vec_chunks", names);
        Assert.DoesNotContain("notes_vec_rowids", names);
        Assert.DoesNotContain(names, n => n.StartsWith("notes_vec_vector_chunks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheVec0AndFts5SidecarsAndEveryShadowTableAreHidden()
    {
        using var host = await VectorTestHost.StartAsync();
        var collection = new EdgeVectorStoreCollection<string, Note>(
            host.Database,
            "notes",
            new EdgeVectorStoreCollectionOptions
            {
                EmbeddingGenerator = new DeterministicEmbeddingGenerator(384),
            });
        await collection.EnsureCollectionExistsAsync(Token);

        var names = await Store(host).ListCollectionNamesAsync(Token).ToListAsync(Token);

        Assert.Equal(JustNotes, names);

        // The shadow tables really are in sqlite_master - this asserts the filter did the work,
        // not that they were never there.
        var raw = await host.AllTableNamesAsync();
        Assert.Contains("notes_fts_docsize", raw);
        Assert.Contains("notes_fts_config", raw);
        Assert.Contains("notes_vec_chunks", raw);
    }

    [Fact]
    public async Task SqliteInternalTablesAreExcluded()
    {
        using var host = await VectorTestHost.StartAsync();

        // AUTOINCREMENT is what makes SQLite materialise sqlite_sequence.
        await host.ExecuteAsync("CREATE TABLE \"counted\" (\"id\" INTEGER PRIMARY KEY AUTOINCREMENT)");
        await host.ExecuteAsync("INSERT INTO \"counted\" DEFAULT VALUES");

        var raw = await host.AllTableNamesAsync();
        Assert.Contains("sqlite_sequence", raw);

        var names = await Store(host).ListCollectionNamesAsync(Token).ToListAsync(Token);

        Assert.Contains("counted", names);
        Assert.DoesNotContain(names, n => n.StartsWith("sqlite_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CollectionExistsAgreesWithTheListing()
    {
        using var host = await VectorTestHost.StartAsync();
        using var store = Store(host);

        Assert.False(await store.CollectionExistsAsync("notes", Token));

        var collection = new EdgeVectorStoreCollection<string, PlainNote>(host.Database, "notes");
        await collection.EnsureCollectionExistsAsync(Token);

        Assert.True(await store.CollectionExistsAsync("notes", Token));

        // A sidecar is not a collection, however it is spelled.
        Assert.False(await store.CollectionExistsAsync("notes_vec", Token));
    }

    [Fact]
    public async Task EnsureCollectionDeletedFromTheStoreDropsTheSidecarsToo()
    {
        using var host = await VectorTestHost.StartAsync();
        using var store = Store(host);

        var collection = new EdgeVectorStoreCollection<string, PlainNote>(host.Database, "notes");
        await collection.EnsureCollectionExistsAsync(Token);

        await store.EnsureCollectionDeletedAsync("notes", Token);

        var raw = await host.AllTableNamesAsync();
        Assert.DoesNotContain("notes", raw);
        Assert.DoesNotContain("notes_vec", raw);
        Assert.Empty(await store.ListCollectionNamesAsync(Token).ToListAsync(Token));
    }

    private static EdgeVectorStore Store(VectorTestHost host) => new(host.Database);
}
