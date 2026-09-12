using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Qavren.Edge.Sqlite;
using Qavren.Edge.VectorData;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// Spec 11.1 step 5. The registration-versus-startup split, asserted directly: a bad budget makes
/// <c>AddIngestion</c> RETURN and the order-400 task throw, and the task itself opens no database.
/// </summary>
public sealed class StartupTaskTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void The_startup_task_runs_at_order_400()
    {
        Assert.Equal(400, EdgeIngestionStartupOrder.Validate);
    }

    [Fact]
    public async Task A_missing_tokenizer_fails_at_startup_with_6001_and_not_at_builder_time()
    {
        // AddIngestion returns: the tokenizer is a START-time fact, not a builder-time one, and
        // AddOnnxIngestion() may not even have been chained yet.
        using var provider = IngestionTestHost.Build(edge =>
        {
            edge.AddVectorStore();
            edge.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                new RecordingEmbeddingGenerator());
            edge.AddIngestion(1);
        });

        var error = await Assert.ThrowsAsync<EdgeConfigurationException>(
            () => provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync(Token).AsTask());

        Assert.Equal(EdgeErrorCode.TokenCounterMissing, error.Code);
        Assert.Contains("AddOnnxIngestion", error.Message, StringComparison.Ordinal);
        Assert.Contains("UseChunkTokenizer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_embedding_generator_fails_at_startup_with_6208()
    {
        using var provider = IngestionTestHost.Build(edge =>
        {
            edge.AddVectorStore();
            edge.Services.AddSingleton<IChunkTokenizer>(new FakeChunkTokenizer());
            edge.AddIngestion(1);
        });

        var error = await Assert.ThrowsAsync<EdgeConfigurationException>(
            () => provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync(Token).AsTask());

        Assert.Equal(EdgeErrorCode.IngestionEmbeddingGeneratorMissing, error.Code);
    }

    [Fact]
    public async Task A_deliberately_bad_budget_makes_AddIngestion_return_and_the_order_400_task_throw_6003()
    {
        EdgeBuilder? captured = null;
        using var provider = IngestionTestHost.Build(edge =>
        {
            edge.AddVectorStore();
            edge.Services.AddSingleton<IChunkTokenizer>(new FakeChunkTokenizer());
            edge.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                new RecordingEmbeddingGenerator());

            // 4096 is far over the 256-token model ceiling; it needs the tokenizer to detect, so
            // AddIngestion cannot and does not.
            edge.AddIngestion(1, o => o.Chunking.MaxTokens = 4096);
            captured = edge;
        });

        Assert.NotNull(captured);

        var error = await Assert.ThrowsAsync<EdgeIngestionException>(
            () => provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync(Token).AsTask());

        Assert.Equal(EdgeErrorCode.IngestionChunkBudgetInvalid, error.Code);
    }

    [Fact]
    public async Task A_tokenizer_ceiling_that_disagrees_with_the_profile_is_6153()
    {
        using var provider = IngestionTestHost.Build(edge =>
        {
            edge.AddVectorStore();
            edge.Services.AddSingleton<IChunkTokenizer>(new FakeChunkTokenizer(maxSequenceLength: 512));
            edge.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                new RecordingEmbeddingGenerator());
            edge.AddIngestion(1);
        });

        var error = await Assert.ThrowsAsync<EdgeChunkingException>(
            () => provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync(Token).AsTask());

        Assert.Equal(EdgeErrorCode.ChunkTokenizerCeilingExceeded, error.Code);
    }

    [Fact]
    public async Task A_generator_declaring_a_different_width_is_6006()
    {
        using var provider = IngestionTestHost.Build(edge =>
        {
            edge.AddVectorStore();
            edge.Services.AddSingleton<IChunkTokenizer>(new FakeChunkTokenizer());
            edge.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                new RecordingEmbeddingGenerator(dimensions: 768));
            edge.AddIngestion(1);
        });

        var error = await Assert.ThrowsAsync<EdgeConfigurationException>(
            () => provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync(Token).AsTask());

        Assert.Equal(EdgeErrorCode.IngestionCollectionDimensionMismatch, error.Code);
    }

    [Fact]
    public async Task The_order_400_task_opens_no_database()
    {
        var refusing = new RefusingEdgeDatabase();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgeDatabase>(refusing);
        services.AddSingleton<IChunkTokenizer>(new FakeChunkTokenizer());
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new RecordingEmbeddingGenerator());

        var registry = new Internal.IngestionRegistry();
        var options = new IngestionOptions();
        var definition = IngestionSchema.BuildDefinition(384, options.DistanceFunction, true);
        registry.Add(new Internal.IngestionRegistration(
            options, definition, new EdgeVectorStoreCollectionOptions { Definition = definition }, 1));
        services.AddSingleton(registry);

        using var provider = services.BuildServiceProvider();
        var task = new Internal.IngestionValidateStartupTask(provider, registry);

        await task.RunAsync(Token);

        Assert.Equal(0, refusing.OpenAttempts);
    }

    [Theory]
    [InlineData("0bad")]
    [InlineData("has space")]
    [InlineData("drop\";--")]
    public void An_invalid_StateTablePrefix_is_6005_at_registration(string prefix)
    {
        var error = Assert.Throws<EdgeConfigurationException>(() =>
            IngestionTestHost.Build(edge => edge.AddIngestion(1, o => o.StateTablePrefix = prefix)).Dispose());

        Assert.Equal(EdgeErrorCode.IngestionOptionsInvalid, error.Code);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public void An_out_of_range_FullTextRemoveDiacritics_is_6005(int value)
    {
        var error = Assert.Throws<EdgeConfigurationException>(() =>
            IngestionTestHost.Build(edge => edge.AddIngestion(1, o => o.FullTextRemoveDiacritics = value)).Dispose());

        Assert.Equal(EdgeErrorCode.IngestionOptionsInvalid, error.Code);
    }

    [Fact]
    public void A_SleepGraceBudget_over_two_seconds_is_6005()
    {
        var error = Assert.Throws<EdgeConfigurationException>(() =>
            IngestionTestHost.Build(edge =>
                edge.AddIngestion(1, o => o.SleepGraceBudget = TimeSpan.FromSeconds(3))).Dispose());

        Assert.Equal(EdgeErrorCode.IngestionOptionsInvalid, error.Code);
    }

    [Fact]
    public void A_second_AddIngestion_for_the_same_collection_is_6007()
    {
        var error = Assert.Throws<EdgeConfigurationException>(() =>
            IngestionTestHost.Build(edge =>
            {
                edge.AddIngestion(1);
                edge.AddIngestion(3);
            }).Dispose());

        Assert.Equal(EdgeErrorCode.IngestionMigrationVersionConflict, error.Code);
    }
}
