using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Qavren.Edge.Sqlite;
using Qavren.Edge.VectorData;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// Spec 9.5's invariant, asserted rather than documented: <b>no SP3 write nests an SP2 collection
/// call inside an SP3 transaction.</b> <c>EdgeDatabase.ExecuteInTransactionAsync</c> opens a NEW
/// connection per call and SP2's <c>UpsertAsync</c> and <c>DeleteAsync</c> each wrap themselves in
/// one, so nesting is two connections contending for the WAL write lock — <c>SQLITE_BUSY</c>, not
/// atomicity.
/// </summary>
public sealed class TransactionInvariantTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task No_SP3_path_opens_a_connection_inside_an_SP3_transaction(bool repairOrdinals)
    {
        SpyEdgeDatabase? spy = null;
        using var provider = IngestionTestHost.Build(edge =>
        {
            edge.AddVectorStore();
            edge.Services.AddSingleton<IChunkTokenizer>(new FakeChunkTokenizer());
            edge.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                new RecordingEmbeddingGenerator());
            edge.AddIngestion(1, o => o.RepairOrdinals = repairOrdinals);
            edge.Services.AddSingleton<IEdgeDatabase>(sp =>
            {
                spy = new SpyEdgeDatabase(sp.GetRequiredKeyedService<IEdgeDatabase>("(default)"));
                return spy;
            });
        });

        await provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync(Token).AsTask();
        var pipeline = provider.GetRequiredService<IIngestionPipeline>();

        // Every write phase in one run: additions, then a removal, then a repair, then the prune.
        var source = new RecordingSource()
            .Add("a.md", "first paragraph\n\nsecond paragraph\n\nthird paragraph", IngestionMediaTypes.Markdown)
            .Add("b.txt", "beta");

        await pipeline.RunAsync(source, cancellationToken: Token);

        source.Replace("a.md", "inserted paragraph\n\nfirst paragraph\n\nsecond paragraph\n\nthird paragraph");
        source.Remove("b.txt");
        await pipeline.RunAsync(source, cancellationToken: Token);

        Assert.NotNull(spy);
        Assert.True(spy.TransactionCount > 0, "the run opened no transaction at all");
        Assert.False(spy.NestedOpenObserved, "an SP2 collection call happened inside an SP3 transaction");
    }

    [Fact]
    public async Task RemoveSource_and_RemoveDocument_keep_the_invariant_too()
    {
        SpyEdgeDatabase? spy = null;
        using var provider = IngestionTestHost.Build(edge =>
        {
            edge.AddVectorStore();
            edge.Services.AddSingleton<IChunkTokenizer>(new FakeChunkTokenizer());
            edge.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                new RecordingEmbeddingGenerator());
            edge.AddIngestion(1);
            edge.Services.AddSingleton<IEdgeDatabase>(sp =>
            {
                spy = new SpyEdgeDatabase(sp.GetRequiredKeyedService<IEdgeDatabase>("(default)"));
                return spy;
            });
        });

        await provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync(Token).AsTask();
        var pipeline = provider.GetRequiredService<IIngestionPipeline>();
        var source = new RecordingSource().Add("a.txt", "alpha").Add("b.txt", "beta");

        await pipeline.RunAsync(source, cancellationToken: Token);
        Assert.Equal(1, await pipeline.RemoveDocumentAsync("memory", "a.txt", ct: Token));
        Assert.Equal(1, await pipeline.RemoveSourceAsync("memory", ct: Token));

        Assert.NotNull(spy);
        Assert.False(spy.NestedOpenObserved);

        var status = await pipeline.GetStatusAsync(ct: Token);
        Assert.Equal(0, status.DocumentCount);
        Assert.Equal(0, status.ChunkCount);
    }
}
