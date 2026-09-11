using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Qavren.Edge.Sqlite;
using Qavren.Edge.VectorData;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// Error 6002 (plan adjustment 21). Without this check a typo'd collection name reaches the state
/// store and comes back as <see cref="EdgeErrorCode.IngestionStateMissing"/>, which blames the
/// schema for a typo and sends the consumer to a migration that is already correct.
/// </summary>
public sealed class PipelineCollectionNameTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_unknown_name_is_6002_on_every_method_that_takes_one()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource();

        await AssertNotConfiguredAsync(() => host.Pipeline.RunAsync(source, "typo", null, Token));
        await AssertNotConfiguredAsync(() => host.Pipeline.PruneAsync(source, "typo", Token));
        await AssertNotConfiguredAsync(() => host.Pipeline.RemoveSourceAsync("src", "typo", Token));
        await AssertNotConfiguredAsync(() => host.Pipeline.RemoveDocumentAsync("src", "a.txt", "typo", Token));
        await AssertNotConfiguredAsync(() => host.Pipeline.GetStatusAsync("typo", Token));
    }

    [Fact]
    public async Task The_message_names_the_requested_and_the_configured_collections()
    {
        using var host = await IngestionTestHost.StartAsync();

        var error = await Assert.ThrowsAsync<EdgeIngestionException>(
            () => host.Pipeline.GetStatusAsync("typo", Token));

        Assert.Contains("typo", error.Message, StringComparison.Ordinal);
        Assert.Contains("chunks", error.Message, StringComparison.Ordinal);
        Assert.Contains("AddIngestion", error.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Six_thousand_and_two_is_raised_before_any_database_call()
    {
        var spyHolder = new SpyHolder();
        using var provider = IngestionTestHost.Build(edge =>
        {
            edge.AddVectorStore();
            edge.Services.AddSingleton<IChunkTokenizer>(new FakeChunkTokenizer());
            edge.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                new RecordingEmbeddingGenerator());
            edge.AddIngestion(1);
            edge.Services.AddSingleton<IEdgeDatabase>(sp =>
            {
                spyHolder.Spy = new SpyEdgeDatabase(sp.GetRequiredKeyedService<IEdgeDatabase>("(default)"));
                return spyHolder.Spy;
            });
        });

        await provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync(Token).AsTask();
        var openedAtStart = spyHolder.Spy?.OpenCount ?? 0;

        await Assert.ThrowsAsync<EdgeIngestionException>(
            () => provider.GetRequiredService<IIngestionPipeline>().GetStatusAsync("typo", Token));

        Assert.Equal(openedAtStart, spyHolder.Spy?.OpenCount ?? 0);
    }

    [Fact]
    public async Task Null_with_one_configured_collection_resolves_and_with_two_is_6002()
    {
        using var one = await IngestionTestHost.StartAsync();
        var status = await one.Pipeline.GetStatusAsync(ct: Token);
        Assert.Equal("chunks", status.CollectionName);

        using var two = await IngestionTestHost.StartAsync(
            configure: edge => edge.AddIngestion(3, o => o.CollectionName = "other"));

        var error = await Assert.ThrowsAsync<EdgeIngestionException>(() => two.Pipeline.GetStatusAsync(ct: Token));
        Assert.Equal(EdgeErrorCode.IngestionCollectionNotConfigured, error.Code);
    }

    private static async Task AssertNotConfiguredAsync(Func<Task> act)
    {
        var error = await Assert.ThrowsAsync<EdgeIngestionException>(act).ConfigureAwait(true);
        Assert.Equal(EdgeErrorCode.IngestionCollectionNotConfigured, error.Code);
    }

    private sealed class SpyHolder
    {
        public SpyEdgeDatabase? Spy { get; set; }
    }
}
