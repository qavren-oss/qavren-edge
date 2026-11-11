using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Qavren.Edge.Sqlite;
using Qavren.Edge.VectorData;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// <c>RemoveDocumentAsync</c>, <c>RemoveSourceAsync</c> and <c>PruneAsync</c>: a document's chunks
/// and its state row go in ONE transaction, and the three take the gate a run takes. They used to
/// commit the chunks and the row separately and to run alongside whatever else was running - so a
/// removal that stopped half way left a row saying <c>Indexed</c> over nothing, which the hash gate
/// then skipped for ever, and a sweep could read the state rows while a run was still writing them.
/// </summary>
public sealed class RemovalTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RemoveDocument_takes_the_chunks_and_the_state_row_in_one_transaction()
    {
        using var provider = BuildWithSpy(out var spy);
        await provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync(Token).AsTask();
        var pipeline = provider.GetRequiredService<IIngestionPipeline>();
        await pipeline.RunAsync(new RecordingSource().Add("a.txt", "alpha").Add("b.txt", "beta"), cancellationToken: Token);

        var before = spy().TransactionCount;
        Assert.Equal(1, await pipeline.RemoveDocumentAsync("memory", "a.txt", ct: Token));
        Assert.Equal(1, spy().TransactionCount - before);

        before = spy().TransactionCount;
        Assert.Equal(1, await pipeline.RemoveSourceAsync("memory", ct: Token));
        Assert.Equal(1, spy().TransactionCount - before);

        var status = await pipeline.GetStatusAsync(ct: Token);
        Assert.Equal(0, status.DocumentCount);
        Assert.Equal(0, status.ChunkCount);
    }

    [Theory]
    [InlineData("document")]
    [InlineData("source")]
    [InlineData("prune")]
    public async Task A_removal_that_cannot_delete_the_state_row_leaves_the_chunks_where_they_were(string removal)
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("a.txt", "alpha beta gamma");
        await host.Pipeline.RunAsync(source, cancellationToken: Token);

        // The state delete is refused, which stands in for anything that stops a removal after its
        // first statement: a full disk, a lock, the process going away.
        await ExecuteAsync(
            host,
            """
            CREATE TRIGGER "refuse_state_delete" BEFORE DELETE ON "qedge_ingest_document"
            BEGIN SELECT RAISE(ABORT, 'refused'); END
            """);

        source.Remove("a.txt");
        await Assert.ThrowsAnyAsync<Exception>(() => removal switch
        {
            "document" => host.Pipeline.RemoveDocumentAsync("memory", "a.txt", ct: Token),
            "source" => host.Pipeline.RemoveSourceAsync("memory", ct: Token),
            _ => host.Pipeline.PruneAsync(source, cancellationToken: Token),
        });

        // Both halves or neither. The chunk used to be gone by now, under a row still saying Indexed.
        var status = await host.Pipeline.GetStatusAsync(ct: Token);
        Assert.Equal(1, status.DocumentCount);
        Assert.Equal(1, status.ChunkCount);

        // And the gate is open again: a refused removal does not wedge the pipeline.
        await ExecuteAsync(host, """DROP TRIGGER "refuse_state_delete" """);
        Assert.Equal(1, await host.Pipeline.RemoveDocumentAsync("memory", "a.txt", ct: Token));
        Assert.Equal(0, (await host.Pipeline.GetStatusAsync(ct: Token)).ChunkCount);
    }

    [Theory]
    [InlineData("document")]
    [InlineData("source")]
    [InlineData("prune")]
    public async Task A_removal_during_a_run_is_6008_naming_the_run(string removal)
    {
        using var host = await IngestionTestHost.StartAsync();
        await host.Pipeline.RunAsync(new RecordingSource().Add("a.txt", "alpha"), cancellationToken: Token);

        var release = new TaskCompletionSource();
        var run = host.Pipeline.RunAsync(new BlockingSource(release.Task), cancellationToken: Token);

        var error = await Assert.ThrowsAsync<EdgeIngestionException>(() => removal switch
        {
            "document" => host.Pipeline.RemoveDocumentAsync("memory", "a.txt", ct: Token),
            "source" => host.Pipeline.RemoveSourceAsync("memory", ct: Token),
            _ => host.Pipeline.PruneAsync(new RecordingSource(), cancellationToken: Token),
        });
        Assert.Equal(EdgeErrorCode.IngestionRunAlreadyActive, error.Code);

        release.SetResult();
        var completed = await run.ConfigureAwait(true);
        Assert.Equal(completed.RunId, error.RunId);

        // Nothing was removed behind the run's back.
        Assert.Equal(1, (await host.Pipeline.GetStatusAsync(ct: Token)).ChunkCount);
    }

    [Fact]
    public async Task A_run_started_while_a_sweep_is_enumerating_is_6008_naming_the_sweep()
    {
        using var host = await IngestionTestHost.StartAsync();

        var release = new TaskCompletionSource();
        var sweep = host.Pipeline.PruneAsync(new BlockingSource(release.Task), cancellationToken: Token);

        var error = await Assert.ThrowsAsync<EdgeIngestionException>(
            () => host.Pipeline.RunAsync(new RecordingSource().Add("a.txt", "alpha"), cancellationToken: Token));

        // A removal has no run id, so the message names the member that holds the gate.
        Assert.Equal(EdgeErrorCode.IngestionRunAlreadyActive, error.Code);
        Assert.Null(error.RunId);
        Assert.Contains(nameof(IIngestionPipeline.PruneAsync), error.Message, StringComparison.Ordinal);

        release.SetResult();
        Assert.Equal(0, await sweep.ConfigureAwait(true));

        var after = await host.Pipeline.RunAsync(new RecordingSource().Add("a.txt", "alpha"), cancellationToken: Token);
        Assert.Equal(IngestionRunOutcome.Completed, after.Outcome);
    }

    [Fact]
    public async Task RemoveSource_takes_chunks_no_state_row_accounts_for()
    {
        using var host = await IngestionTestHost.StartAsync();
        await host.Pipeline.RunAsync(new RecordingSource().Add("a.txt", "alpha"), cancellationToken: Token);

        // What a run suspended in the middle of a document leaves behind: chunks, and no row yet.
        await ExecuteAsync(host, """DELETE FROM "qedge_ingest_document" """);

        Assert.Equal(0, await host.Pipeline.RemoveSourceAsync("memory", ct: Token));
        Assert.Equal(0, (await host.Pipeline.GetStatusAsync(ct: Token)).ChunkCount);
    }

    /// <summary>The canonical wiring with the database wrapped in a spy that counts transactions.</summary>
    private static ServiceProvider BuildWithSpy(out Func<SpyEdgeDatabase> spy)
    {
        SpyEdgeDatabase? created = null;
        spy = () => created ?? throw new InvalidOperationException("Nothing has resolved the database yet.");

        return IngestionTestHost.Build(edge =>
        {
            edge.AddVectorStore();
            edge.Services.AddSingleton<IChunkTokenizer>(new FakeChunkTokenizer());
            edge.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                new RecordingEmbeddingGenerator());
            edge.AddIngestion(1);
            edge.Services.AddSingleton<IEdgeDatabase>(sp =>
            {
                created = new SpyEdgeDatabase(sp.GetRequiredKeyedService<IEdgeDatabase>("(default)"));
                return created;
            });
        });
    }

    private static async Task ExecuteAsync(IngestionTestHost host, string sql)
    {
        var connection = await host.Database.OpenConnectionAsync(Token).ConfigureAwait(true);
        await using (connection.ConfigureAwait(true))
        {
            await connection.ExecuteAsync(sql, Array.Empty<SqliteParameter>(), Token).ConfigureAwait(true);
        }
    }

    /// <summary>Enumerates nothing, and not until it is told to.</summary>
    private sealed class BlockingSource(Task gate) : IngestionSource
    {
        public override string Id => "blocking";

        public override async IAsyncEnumerable<DocumentSourceItem> EnumerateAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await gate.ConfigureAwait(false);
            yield break;
        }
    }
}
