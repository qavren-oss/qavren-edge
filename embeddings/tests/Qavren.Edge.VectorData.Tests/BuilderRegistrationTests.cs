using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Sqlite;
using Qavren.Edge.Sqlite.Native;
using Qavren.Edge.VectorData.Tests.Fakes;
using Qavren.Edge.VectorData.Tests.Records;
using Xunit;
using MEVD = Microsoft.Extensions.VectorData;

namespace Qavren.Edge.VectorData.Tests;

/// <summary>
/// Steps 4 and 5 of the task - the builder extensions, the lifecycle observer and the diagnostics
/// contributor - have no other coverage in this wave. The end-to-end four-call happy path with the
/// real ONNX generator is wave 5's; this asserts the registrations themselves resolve and do what
/// they claim, so a broken factory is found here rather than there.
/// </summary>
public sealed class BuilderRegistrationTests
{
    private static readonly float[] UnitX = [1f, 0f, 0f, 0f];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AddVectorStoreResolvesTheDiRegisteredGeneratorWithoutBeingHandedOne()
    {
        using var host = await VectorTestHost.StartAsync(edge =>
        {
            edge.Services.AddSingleton<IEmbeddingGenerator>(new DeterministicEmbeddingGenerator(384));
            edge.AddVectorStore();
            edge.AddVectorCollectionMigration<string, Note>(version: 1, "notes");
        });

        var store = host.Services.GetRequiredService<EdgeVectorStore>();
        Assert.Same(store, host.Services.GetRequiredService<MEVD.VectorStore>());

        // The DDL ran under the sub-project 1 migrator at startup order 100, not on first use.
        Assert.True(await store.CollectionExistsAsync("notes", Token));

        // A string source property round-trips, which is only possible if the factory found the
        // container's generator and handed it to the store.
        var collection = store.GetCollection<string, Note>("notes");
        await collection.UpsertAsync(new Note { Key = "a", Title = "t", Body = "b" }, Token);
        Assert.NotNull(await collection.GetAsync("a", cancellationToken: Token));
    }

    [Fact]
    public async Task AStoreWithNoGeneratorInTheContainerStillResolves()
    {
        // GetService, never GetRequiredService: a store used only with pre-computed vectors needs
        // no generator, and demanding one would break that.
        using var host = await VectorTestHost.StartAsync(edge =>
        {
            edge.AddVectorStore();
            edge.AddVectorCollectionMigration<string, RawVec>(version: 1, "raw");
        });

        var store = host.Services.GetRequiredService<EdgeVectorStore>();
        var collection = store.GetCollection<string, RawVec>("raw");

        await collection.UpsertAsync(new RawVec { Key = "k", Embedding = UnitX }, Token);

        Assert.NotNull(await collection.GetAsync("k", cancellationToken: Token));
    }

    [Fact]
    public void AddVectorCollectionRegistersOneAdHocSchemaTaskPerCollectionAtStartupOrder300()
    {
        // This test deliberately BUILDS the container and never starts the host. AddVectorCollection
        // ships a known limitation, unresolved as of 2026-09-11 and documented on the method: the
        // task it registers calls IEdgeDatabase.OpenConnectionAsync, which awaits
        // IEdgeHost.EnsureStartedAsync, and that barrier only lifts once every startup task -
        // including this one - has returned. Starting a host with this registration therefore hangs
        // forever, so what is asserted here is the registration shape: order 300, one task per
        // collection, and the store's own registrations untouched. The execution half needs
        // sub-project 1 to expose a startup-safe open (EdgeDatabase.OpenCoreAsync is internal).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge =>
        {
            edge.UseSqliteNative();
            edge.AddSqlite(o =>
            {
                o.DatabaseName = "test.db";
                o.Directory = Path.GetTempPath();
            });
            edge.AddVectorStore();
            edge.AddVectorCollection<string, Note>("notes");
            edge.AddVectorCollection<string, RawVec>("raw");
        });

        using var provider = services.BuildServiceProvider();

        var tasks = provider.GetServices<IEdgeStartupTask>()
            .Where(t => t.GetType().Name == "VectorSchemaStartupTask")
            .ToArray();

        Assert.Equal(2, tasks.Length);
        Assert.All(tasks, t => Assert.Equal(300, t.Order));

        // RegisterShared still ran exactly once, however many collections were added.
        Assert.Single(
            provider.GetServices<IEdgeLifecycleObserver>(),
            o => o.GetType().Name == "VectorDataLifecycleObserver");
        Assert.Single(
            provider.GetServices<IEdgeDiagnosticsContributor>(),
            c => c.ComponentName == "Qavren.Edge.VectorData");
    }

    [Fact]
    public async Task TheDiagnosticsContributorReportsEveryRegisteredCollection()
    {
        using var host = await VectorTestHost.StartAsync(edge =>
        {
            edge.Services.AddSingleton<IEmbeddingGenerator>(new DeterministicEmbeddingGenerator(384));
            edge.AddVectorStore(o => o.IncludeRowCountsInDiagnostics = true);
            edge.AddVectorCollectionMigration<string, Note>(version: 1, "notes");
        });

        // Resolving the store is what pushes IncludeRowCountsInDiagnostics onto the registry.
        _ = host.Services.GetRequiredService<EdgeVectorStore>();

        var contributor = host.Services.GetServices<IEdgeDiagnosticsContributor>()
            .Single(c => c.ComponentName == "Qavren.Edge.VectorData");
        var details = contributor.Describe();

        Assert.Equal("notes", details["collection[notes].dataTable"]);
        Assert.Equal("notes_vec", details["collection[notes].vecTable"]);
        Assert.Equal("notes_fts", details["collection[notes].ftsTable"]);
        Assert.Equal("384", details["collection[notes].dimensions"]);
        Assert.Equal("60", details["collection[notes].rrfK"]);
        Assert.Equal("0", details["collection[notes].rowCount"]);
        Assert.Equal("0", details["collection[notes].vecRowCount"]);

        // vec_version() returns "v0.1.9" - WITH the leading v, as sub-project 1 documents.
        Assert.StartsWith("v", details["database[(default)].vecVersion"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFtsMergeObserverIsRegisteredFirstAndSurvivesASleep()
    {
        using var host = await VectorTestHost.StartAsync(edge =>
        {
            edge.Services.AddSingleton<IEmbeddingGenerator>(new DeterministicEmbeddingGenerator(384));
            edge.AddVectorStore();
            edge.AddVectorCollectionMigration<string, Note>(version: 1, "notes");
        });

        var observers = host.Services.GetServices<IEdgeLifecycleObserver>().ToArray();

        // Sub-project 1's observer checkpoints WAL on the same event, and the merge has to run
        // first so its pages land in that checkpoint.
        Assert.Equal("VectorDataLifecycleObserver", observers[0].GetType().Name);

        var store = host.Services.GetRequiredService<EdgeVectorStore>();
        var collection = store.GetCollection<string, Note>("notes");
        await collection.UpsertAsync(
            Enumerable.Range(0, 20).Select(i => new Note { Key = "k" + i, Title = "t", Body = "body " + i }),
            Token);

        await host.Services.GetRequiredService<IEdgeLifecycle>().RaiseSleepingAsync(Token);

        // The merge is a no-op on the data, and every row is still searchable afterwards.
        Assert.Equal(20L, await host.CountAsync("notes_fts_docsize"));
    }
}
