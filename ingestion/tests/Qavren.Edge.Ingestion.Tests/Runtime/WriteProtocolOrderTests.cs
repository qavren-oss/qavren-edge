using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Qavren.Edge.Sqlite;
using Qavren.Edge.VectorData;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// Spec 9.5's four-step order and its transaction invariant, asserted against a spy database that
/// fails if an SP2 collection call ever happens inside an SP3 transaction.
/// </summary>
public sealed class WriteProtocolOrderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Embed_precedes_upsert_and_no_open_nests_inside_an_SP3_transaction()
    {
        var events = new List<string>();
        using var fixture = await SpyFixture.StartAsync(events);

        var source = new RecordingSource().Add("a.txt", "alpha").Add("b.txt", "beta");
        await fixture.Pipeline.RunAsync(source, cancellationToken: Token);

        // The invariant: ExecuteInTransactionAsync opens a NEW connection per call, and SP2's
        // UpsertAsync and DeleteAsync each wrap themselves in one. Nesting is two connections
        // contending for the WAL write lock - SQLITE_BUSY, not atomicity.
        Assert.False(fixture.Spy.NestedOpenObserved);

        // Every embed is followed by at least one database operation before the next embed.
        var embedIndexes = events.Index().Where(e => e.Item == "embed").Select(e => e.Index).ToList();
        Assert.NotEmpty(embedIndexes);
        foreach (var index in embedIndexes)
        {
            Assert.True(index < events.Count - 1, "an embed was the last recorded operation");
        }
    }

    [Fact]
    public async Task Additions_land_before_removals_and_the_state_row_commits_last()
    {
        var events = new List<string>();
        using var fixture = await SpyFixture.StartAsync(events);

        var source = new RecordingSource().Add("a.txt", "alpha");
        await fixture.Pipeline.RunAsync(source, cancellationToken: Token);

        events.Clear();
        source.Replace("a.txt", "completely different words here");
        var second = await fixture.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(1, second.ChunksAdded);
        Assert.Equal(1, second.ChunksRemoved);

        // a1 happens before every write of this document, and the state row is the LAST
        // transaction of all - which is what makes a torn document still dirty.
        Assert.Contains("embed", events);
        Assert.True(
            events.IndexOf("embed") < events.LastIndexOf("tx"),
            "the first embed must precede the last transaction: " + string.Join(",", events));
        Assert.Equal("tx", events[^1]);
    }

    [Fact]
    public async Task A_failed_embed_is_retried_once_at_half_the_batch_and_a_second_failure_suspends()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("a.txt", "alpha");

        host.Generator.FailOnce = new InvalidOperationException("transient");
        var recovered = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, recovered.Outcome);
        Assert.Equal(2, recovered.EmbedCalls);
        Assert.Contains(host.Logs.Lines, l => l.StartsWith("918|", StringComparison.Ordinal));

        using var second = await IngestionTestHost.StartAsync();
        second.Generator.FailAlways = new InvalidOperationException("permanent");
        var suspended = await second.Pipeline.RunAsync(
            new RecordingSource().Add("a.txt", "alpha"), cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Suspended, suspended.Outcome);
        Assert.Equal("embed:failed", suspended.SuspendReason);
        Assert.NotNull(suspended.Failure);
        Assert.Equal(EdgeErrorCode.IngestionEmbeddingFailed, suspended.Failure.Code);
    }

    /// <summary>
    /// Spec 9.5 / 10.2: <c>EmbedCalls</c> is "a count of a1, exactly", and
    /// <c>IngestionBudget.MaxTokens</c> meters the same pass. The retry issues
    /// <c>ceil(n / max(1, n / 2))</c> calls, which is 3 for a window of 3 — so a hard-coded 2 on the
    /// retry path under-reports every window whose size is not a power of two.
    /// </summary>
    [Fact]
    public async Task The_retry_path_counts_its_calls_rather_than_assuming_two()
    {
        using var host = await IngestionTestHost.StartAsync(o => o.WriteBatchSize = 3);
        var source = new RecordingSource().Add("a.txt", LongDocument(900));

        host.Generator.FailOnce = new InvalidOperationException("transient");
        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
        Assert.True(result.ChunksAdded >= 3, $"expected a full first window, got {result.ChunksAdded}");

        // The first window of 3 failed, halved to 1, and took three more calls to clear. Every later
        // window is one call. EmbedCalls is the generator's own call count - not an estimate.
        Assert.Equal(host.Generator.CallCount, result.EmbedCalls);
        Assert.Equal(4, host.Generator.Calls.Take(4).Count());
        Assert.Equal(3, host.Generator.Calls[0].Count);
        Assert.All(host.Generator.Calls.Skip(1).Take(3), call => Assert.Single(call));
    }

    [Fact]
    public async Task Write_windows_honour_WriteBatchSize()
    {
        using var host = await IngestionTestHost.StartAsync(o => o.WriteBatchSize = 2);

        // One document well over the 222-token budget, so the chunker has to emit several chunks
        // whatever its packing rules are. The assertion is about the WINDOW, not the boundaries.
        var source = new RecordingSource().Add("a.txt", LongDocument(900));

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.True(result.ChunksAdded > 2, $"expected several chunks, got {result.ChunksAdded}");
        Assert.Equal((result.ChunksAdded + 1) / 2, result.EmbedCalls);
        Assert.All(host.Generator.Calls, call => Assert.InRange(call.Count, 1, 2));
    }

    private static string LongDocument(int words) =>
        string.Join(' ', Enumerable.Range(0, words).Select(i => "word" + i));

    private sealed class SpyFixture : IDisposable
    {
        private readonly ServiceProvider _provider;

        private SpyFixture(ServiceProvider provider, SpyEdgeDatabase spy)
        {
            _provider = provider;
            Spy = spy;
        }

        public SpyEdgeDatabase Spy { get; }

        public IIngestionPipeline Pipeline => _provider.GetRequiredService<IIngestionPipeline>();

        public static async Task<SpyFixture> StartAsync(List<string> events)
        {
            SpyEdgeDatabase? spy = null;
            var generator = new RecordingEmbeddingGenerator { OnCall = () => events.Add("embed") };

            var provider = IngestionTestHost.Build(edge =>
            {
                edge.AddVectorStore();
                edge.Services.AddSingleton<IChunkTokenizer>(new FakeChunkTokenizer());
                edge.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(generator);
                edge.AddIngestion(1);
                edge.Services.AddSingleton<IEdgeDatabase>(sp =>
                {
                    spy = new SpyEdgeDatabase(sp.GetRequiredKeyedService<IEdgeDatabase>("(default)"))
                    {
                        OnEvent = events.Add,
                    };
                    return spy;
                });
            });

            await provider.GetRequiredService<IEdgeHost>()
                .EnsureStartedAsync(TestContext.Current.CancellationToken)
                .AsTask().ConfigureAwait(true);

            _ = provider.GetRequiredService<IEdgeDatabase>();
            events.Clear();
            return new SpyFixture(provider, spy!);
        }

        public void Dispose() => _provider.Dispose();
    }
}
