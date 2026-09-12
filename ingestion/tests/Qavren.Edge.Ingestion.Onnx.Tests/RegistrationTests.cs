using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Hosting;
using Qavren.Edge.Ingestion.Onnx.Internal;
using Qavren.Edge.Onnx;
using Qavren.Edge.Sqlite;
using Xunit;

namespace Qavren.Edge.Ingestion.Onnx.Tests;

/// <summary>
/// What <c>AddOnnxIngestion</c> puts in the container, and what resolving it does. Nothing here
/// loads a model or downloads a vocabulary: the preset and the tokenizer come from a
/// <see cref="PresetGenerator"/>, which is the same two <c>GetService</c> routes the real
/// <c>OnnxEmbeddingGenerator</c> answers.
/// </summary>
public sealed class RegistrationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "qedge-ingest-onnx-" + Guid.NewGuid().ToString("N"));

    private IServiceCollection? _lastServices;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Registers_a_tokenizer_a_throttle_and_one_pre_validate_startup_task()
    {
        var services = new ServiceCollection();
        services.AddQavrenEdge(edge => edge.AddOnnxIngestion());

        Assert.Single(services, d => d.ServiceType == typeof(IChunkTokenizer));
        Assert.Single(services, d => d.ServiceType == typeof(IIngestionThrottle));
        Assert.Single(services, d => d.ServiceType == typeof(OnnxIngestionBinding));
        Assert.Single(services, d => d.ServiceType == typeof(IEdgeStartupTask) && d.ImplementationFactory is not null);
    }

    [Fact]
    public void The_startup_task_factory_builds_over_the_binding_the_same_call_registered()
    {
        var services = new ServiceCollection();
        services.AddQavrenEdge(edge => edge.AddOnnxIngestion("bge"));
        using var provider = services.BuildServiceProvider();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IEdgeStartupTask));
        var task = Assert.IsType<OnnxIngestionStartupTask>(descriptor.ImplementationFactory!(provider));

        Assert.Same(provider.GetRequiredService<OnnxIngestionBinding>(), task.Binding);
        Assert.Equal("bge", task.Binding.EmbeddingsName);
    }

    [Fact]
    public void Is_idempotent_per_embeddings_name()
    {
        var services = new ServiceCollection();
        services.AddQavrenEdge(edge => edge
            .AddOnnxIngestion()
            .AddOnnxIngestion()
            .AddOnnxIngestion("bge")
            .AddOnnxIngestion("bge"));

        Assert.Equal(2, services.Count(d => d.ServiceType == typeof(OnnxIngestionBinding)));
        Assert.Equal(2, services.Count(d => d.ServiceType == typeof(IIngestionThrottle)));
        Assert.Single(services, d => d.ServiceType == typeof(IChunkTokenizer));
    }

    [Fact]
    public void A_non_positive_MinBatchSize_is_6005_at_builder_time()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<EdgeConfigurationException>(() =>
            services.AddQavrenEdge(edge => edge.AddOnnxIngestion(configure: o => o.MinBatchSize = 0)));

        Assert.Equal(EdgeErrorCode.IngestionOptionsInvalid, error.Code);
    }

    [Fact]
    public void The_throttle_wins_over_the_core_default_in_either_order_and_carries_the_options()
    {
        using var first = Build(edge => edge.AddIngestion(1).AddOnnxIngestion(configure: o => o.MinBatchSize = 8));
        using var second = Build(edge => edge.AddOnnxIngestion(configure: o => o.MinBatchSize = 8).AddIngestion(1));

        foreach (var provider in new[] { first, second })
        {
            var throttle = Assert.IsType<ResourceMonitorThrottle>(provider.GetRequiredService<IIngestionThrottle>());
            var monitor = (StubResourceMonitor)provider.GetRequiredService<IEdgeResourceMonitor>();
            monitor.SetPressure(Qavren.Edge.Lifecycle.EdgeMemoryPressure.Moderate);

            // 32 / 2 = 16 would be the floor-free answer; MinBatchSize 8 does not bind, so 16.
            Assert.Equal(16, throttle.Evaluate(new IngestionThrottleContext(32, 32, 0, 0, TimeSpan.Zero)).BatchSize);
            // 12 / 2 = 6 is clamped up to the configured floor of 8.
            Assert.Equal(8, throttle.Evaluate(new IngestionThrottleContext(12, 12, 0, 0, TimeSpan.Zero)).BatchSize);
        }
    }

    [Fact]
    public void A_consumer_throttle_chained_after_AddOnnxIngestion_wins()
    {
        using var provider = Build(edge => edge
            .AddIngestion(1)
            .AddOnnxIngestion()
            .UseIngestionThrottle(_ => new FixedIngestionThrottle()));

        Assert.IsType<FixedIngestionThrottle>(provider.GetRequiredService<IIngestionThrottle>());
    }

    [Fact]
    public void A_consumer_tokenizer_wins_in_either_order()
    {
        var own = new EdgeChunkTokenizer(new FakeEdgeTokenizer(128));

        using var before = Build(edge => edge.UseChunkTokenizer(_ => own).AddOnnxIngestion());
        using var after = Build(edge => edge.AddOnnxIngestion().UseChunkTokenizer(_ => own));

        Assert.Same(own, before.GetRequiredService<IChunkTokenizer>());
        Assert.Same(own, after.GetRequiredService<IChunkTokenizer>());
    }

    [Fact]
    public void Resolving_the_tokenizer_projects_the_preset_onto_the_registration_and_wraps_the_generators_tokenizer()
    {
        IngestionOptions? captured = null;
        var edge = new FakeEdgeTokenizer(maxSequenceLength: 512);

        using var provider = Build(
            b => b.AddIngestion(1, o => captured = o).AddOnnxIngestion(),
            new PresetGenerator(EmbeddingPresets.BgeSmallEnV15, edge));

        Assert.NotNull(captured);
        Assert.Same(ChunkModelProfile.MiniLmL6V2Int8, captured.Model);

        var tokenizer = Assert.IsType<EdgeChunkTokenizer>(provider.GetRequiredService<IChunkTokenizer>());

        Assert.Equal(ChunkModelProfile.BgeSmallEnV15, captured.Model);
        Assert.Equal(512, tokenizer.MaxSequenceLength);
        Assert.Equal("wordpiece:512:uncased", tokenizer.Id);
        Assert.Same(tokenizer, provider.GetRequiredService<IChunkTokenizer>());

        var resolved = captured.Chunking.Resolve(captured.Model, tokenizer);
        Assert.Equal(478, resolved.MaxTokens);
    }

    [Fact]
    public void Only_registrations_bound_to_the_named_generator_are_projected()
    {
        IngestionOptions? unkeyed = null;
        IngestionOptions? keyed = null;

        using var provider = Build(
            b => b
                .AddIngestion(1, o => { o.CollectionName = "a"; unkeyed = o; })
                .AddIngestion(3, o => { o.CollectionName = "b"; o.StoreName = "bge"; keyed = o; })
                .AddOnnxIngestion(),
            new PresetGenerator(EmbeddingPresets.MiniLmL6V2Fp32, new FakeEdgeTokenizer()));

        _ = provider.GetRequiredService<IChunkTokenizer>();

        Assert.NotNull(unkeyed);
        Assert.NotNull(keyed);
        Assert.Equal(ChunkModelProfile.MiniLmL6V2Fp32, unkeyed.Model);
        Assert.Same(ChunkModelProfile.MiniLmL6V2Int8, keyed.Model);
    }

    [Fact]
    public void The_6005_guard_fires_on_resolve_for_an_explicit_profile_that_disagrees()
    {
        using var provider = Build(
            b => b
                .AddIngestion(1, o => o.Model = ChunkModelProfile.MiniLmL6V2Int8 with { MaxSequenceLength = 128 })
                .AddOnnxIngestion(),
            new PresetGenerator(EmbeddingPresets.MiniLmL6V2Int8, new FakeEdgeTokenizer()));

        var error = Assert.Throws<EdgeConfigurationException>(() => provider.GetRequiredService<IChunkTokenizer>());

        Assert.Equal(EdgeErrorCode.IngestionOptionsInvalid, error.Code);
        Assert.Contains("MaxSequenceLength", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_generator_at_all_is_6208_on_resolve()
    {
        using var provider = Build(b => b.AddIngestion(1).AddOnnxIngestion());

        var error = Assert.Throws<EdgeConfigurationException>(() => provider.GetRequiredService<IChunkTokenizer>());

        Assert.Equal(EdgeErrorCode.IngestionEmbeddingGeneratorMissing, error.Code);
    }

    [Fact]
    public void A_generator_that_publishes_no_preset_is_6005_naming_AddOnnxEmbeddings()
    {
        using var provider = Build(
            b => b.AddIngestion(1).AddOnnxIngestion(),
            new PresetGenerator(preset: null));

        var error = Assert.Throws<EdgeConfigurationException>(() => provider.GetRequiredService<IChunkTokenizer>());

        Assert.Equal(EdgeErrorCode.IngestionOptionsInvalid, error.Code);
        Assert.Contains("AddOnnxEmbeddings", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_pre_validate_task_runs_before_400_and_leaves_the_tokenizer_cached()
    {
        IngestionOptions? captured = null;

        using var provider = Build(
            b => b.AddIngestion(1, o => captured = o).AddOnnxIngestion(),
            new PresetGenerator(EmbeddingPresets.MiniLmL6V2Int8, new FakeEdgeTokenizer()));

        var task = StartupTask(provider);
        Assert.Equal(390, task.Order);
        Assert.True(task.Order < EdgeIngestionStartupOrder.Validate);
        Assert.Null(task.Binding.Tokenizer);

        await task.RunAsync(Token);

        Assert.NotNull(captured);
        Assert.Equal(ChunkModelProfile.MiniLmL6V2Int8, captured.Model);
        Assert.NotSame(ChunkModelProfile.MiniLmL6V2Int8, captured.Model);
        var built = task.Binding.Tokenizer;
        Assert.NotNull(built);
        Assert.Same(built, provider.GetRequiredService<IChunkTokenizer>());
    }

    [Fact]
    public async Task The_pre_validate_task_is_silent_when_nothing_is_bound()
    {
        // No AddIngestion: nothing consumes the tokenizer, so nothing is provisioned. No
        // generator: the core's order-400 task reports 6208 itself.
        using var noIngestion = Build(b => b.AddOnnxIngestion(), new PresetGenerator(EmbeddingPresets.MiniLmL6V2Int8));
        using var noGenerator = Build(b => b.AddIngestion(1).AddOnnxIngestion());

        foreach (var provider in new[] { noIngestion, noGenerator })
        {
            var task = StartupTask(provider);
            await task.RunAsync(Token);
            Assert.Null(task.Binding.Tokenizer);
        }
    }

    [Fact]
    public void Is_order_independent_relative_to_AddOnnxEmbeddings_and_touches_nothing_at_build()
    {
        // The real SP2 registration, both orders. Nothing is resolved: the whole point is that a
        // built container against the default preset touches neither the network nor disk.
        using var before = Build(edge => edge.AddOnnxIngestion().AddOnnxEmbeddings().AddIngestion(1));
        var beforeServices = _lastServices!;
        using var after = Build(edge => edge.AddOnnxEmbeddings().AddIngestion(1).AddOnnxIngestion());
        var afterServices = _lastServices!;

        foreach (var (provider, services) in new[] { (before, beforeServices), (after, afterServices) })
        {
            Assert.IsType<ResourceMonitorThrottle>(provider.GetRequiredService<IIngestionThrottle>());
            Assert.Contains(services, d => d.ServiceType == typeof(IEmbeddingGenerator<string, Embedding<float>>));
            Assert.Contains(services, d => d.ServiceType == typeof(IEdgeTokenizerProvider));
            Assert.Single(services, d => d.ServiceType == typeof(IChunkTokenizer));
        }
    }

    /// <summary>
    /// The registered task, built the way its factory builds it - over the binding the same call
    /// registered. Not resolved through IEnumerable&lt;IEdgeStartupTask&gt;, which would construct
    /// SP1's native-provider task and refuse for want of Qavren.Edge.Sqlite.Native.
    /// </summary>
    private static OnnxIngestionStartupTask StartupTask(IServiceProvider provider) =>
        new(provider, Assert.Single(provider.GetServices<OnnxIngestionBinding>()));

    private ServiceProvider Build(
        Action<EdgeBuilder> configure,
        IEmbeddingGenerator<string, Embedding<float>>? generator = null)
    {
        Directory.CreateDirectory(_root);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new TempPaths(_root));
        services.AddSingleton<IEdgeResourceMonitor>(new StubResourceMonitor());
        if (generator is not null)
        {
            services.AddSingleton(generator);
        }

        services.AddQavrenEdge(edge =>
        {
            // AddIngestion registers two migrations, and SP1's registry refuses a migration for a
            // database AddSqlite has not declared. Declared, never opened: nothing here starts.
            edge.AddSqlite(o =>
            {
                o.DatabaseName = "test.db";
                o.Directory = _root;
            });
            edge.UseModelPaths(new TempModelPaths(_root));
            configure(edge);
        });

        _lastServices = services;
        return services.BuildServiceProvider();
    }
}
