using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.VectorData;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// <c>IEdgeHost</c>'s contract: every entry point awaits startup first. The pipeline used to read
/// the recipe the order-400 task freezes before anything had awaited that task, so a call made
/// before startup finished - the MAUI bridges call <c>Start()</c> and never await it - came back
/// as 6001, the missing-tokenizer code, with a tokenizer registered.
/// </summary>
public sealed class PipelineStartupTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RunAsync_on_a_host_nobody_started_awaits_startup_and_runs()
    {
        using var provider = BuildUnstarted();
        var source = new RecordingSource().Add("a.txt", "alpha");

        var result = await provider.GetRequiredService<IIngestionPipeline>()
            .RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.DocumentsIndexed);
    }

    [Fact]
    public async Task GetStatusAsync_on_a_host_nobody_started_awaits_startup()
    {
        using var provider = BuildUnstarted();

        var status = await provider.GetRequiredService<IIngestionPipeline>().GetStatusAsync(ct: Token);

        Assert.Equal("chunks", status.CollectionName);
    }

    [Fact]
    public async Task GetRecipeAsync_on_a_host_nobody_started_awaits_startup()
    {
        using var provider = BuildUnstarted();

        var recipe = await provider.GetRequiredService<IIngestionPipeline>().GetRecipeAsync(Token);

        Assert.False(string.IsNullOrEmpty(recipe.Hash));
    }

    [Fact]
    public async Task A_startup_fault_reaches_the_caller_with_its_own_code_and_not_as_6001()
    {
        // 4096 is far over the fake tokenizer's 256-token ceiling, so the order-400 task throws 6003.
        using var provider = BuildUnstarted(o => o.Chunking.MaxTokens = 4096);

        var error = await Assert.ThrowsAsync<EdgeIngestionException>(
            () => provider.GetRequiredService<IIngestionPipeline>()
                .RunAsync(new RecordingSource(), cancellationToken: Token));

        Assert.Equal(EdgeErrorCode.IngestionChunkBudgetInvalid, error.Code);
    }

    private static ServiceProvider BuildUnstarted(Action<IngestionOptions>? configureIngestion = null) =>
        IngestionTestHost.Build(edge =>
        {
            edge.AddVectorStore();
            edge.Services.AddSingleton<IChunkTokenizer>(new FakeChunkTokenizer());
            edge.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                new RecordingEmbeddingGenerator());
            edge.AddIngestion(1, configureIngestion);
        });
}
