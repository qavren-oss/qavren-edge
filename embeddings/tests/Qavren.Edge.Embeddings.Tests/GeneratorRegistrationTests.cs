using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Embeddings.Tests.Fakes;
using Qavren.Edge.Hosting;
using Qavren.Edge.Onnx;
using Xunit;

namespace Qavren.Edge.Embeddings.Tests;

/// <summary>
/// What <c>AddOnnxEmbeddings</c> puts in the container. Nothing here loads a model: registration
/// is lazy, so a container built against the default preset touches neither the network nor disk.
/// </summary>
public class GeneratorRegistrationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "qedge-embed-reg-" + Guid.NewGuid().ToString("N"));

    private ServiceProvider Build(Action<EdgeBuilder> configure)
    {
        Directory.CreateDirectory(_root);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new TempPaths(_root));
        services.AddSingleton<IEdgeResourceMonitor>(new StubResourceMonitor());

        services.AddQavrenEdge(edge =>
        {
            edge.UseModelPaths(new TempModelPaths(_root));
            configure(edge);
        });

        return services.BuildServiceProvider();
    }

    [Fact]
    public void BothTheClosedGenericAndTheNonGenericDescriptorAreRegistered()
    {
        // AddOnnxEmbeddings delegates to services.AddEmbeddingGenerator rather than
        // hand-registering. Hand-registering would satisfy only one of these and break half of
        // MEVD and Semantic Kernel.
        using var provider = Build(edge => edge.AddOnnxEmbeddings());

        var closed = provider.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
        var nonGeneric = provider.GetService<IEmbeddingGenerator>();

        Assert.NotNull(closed);
        Assert.NotNull(nonGeneric);
        Assert.Same(closed, nonGeneric);
    }

    [Fact]
    public void TheRegisteredGeneratorIsTheDocumentSideOfTheDefaultPreset()
    {
        using var provider = Build(edge => edge.AddOnnxEmbeddings());

        var generator = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        var onnx = Assert.IsType<OnnxEmbeddingGenerator>(generator.GetService(typeof(OnnxEmbeddingGenerator)));

        Assert.Equal(EmbeddingInputKind.Document, onnx.InputKind);
        Assert.Equal(EmbeddingPresets.MiniLmL6V2Int8.Dimensions, onnx.Dimensions);
        Assert.Equal(EmbeddingPresets.MiniLmL6V2Int8.Manifest.ModelId, onnx.Info.ModelId);
    }

    [Fact]
    public void AsQueryGeneratorResolvesTheSiblingThroughTheNonGenericInterface()
    {
        using var provider = Build(edge => edge.AddOnnxEmbeddings(o => o.Preset = EmbeddingPresets.BgeSmallEnV15));

        var generator = provider.GetRequiredService<IEmbeddingGenerator>();
        var query = generator.AsQueryGenerator();

        Assert.NotSame(generator, query);
        Assert.Equal(
            EmbeddingInputKind.Query,
            ((OnnxEmbeddingGenerator)query.GetService(typeof(OnnxEmbeddingGenerator))!).InputKind);
    }

    [Fact]
    public void AddOnnxEmbeddingsAlsoRegistersTheModelTheTokenizerProviderAndTheDiagnosticsBlock()
    {
        using var provider = Build(edge => edge.AddOnnxEmbeddings());

        var tokenizers = provider.GetRequiredService<IEdgeTokenizerProvider>();
        Assert.Null(tokenizers.Current);

        var report = provider.GetRequiredService<IEdgeDiagnostics>().Report();
        var block = Assert.Single(report.Components, c => c.Name == "Qavren.Edge.Embeddings.Onnx");

        Assert.Equal(EmbeddingPresets.MiniLmL6V2Int8.Id, block.Details["preset"]);
        Assert.Equal("Apache-2.0", block.Details["presetLicense"]);
        Assert.Equal("384", block.Details["dimensions"]);
        Assert.Equal("Mean", block.Details["pooling"]);
        Assert.Equal("(symbolic)", block.Details["pinnedSequenceLength"]);

        // Reporting a vocab size must never force provisioning, so it is null until the first
        // embed or a warm-up.
        Assert.Null(block.Details["vocabSize"]);
    }

    [Fact]
    public void AKeyedRegistrationResolvesUnderItsKeyAndNotUnderNone()
    {
        using var provider = Build(edge => edge.AddOnnxEmbeddings("bge", o => o.Preset = EmbeddingPresets.BgeSmallEnV15));

        Assert.NotNull(provider.GetKeyedService<IEmbeddingGenerator<string, Embedding<float>>>("bge"));
        Assert.Null(provider.GetService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    [Fact]
    public void APipelineOverloadWrapsTheGeneratorWithoutHidingIt()
    {
        using var provider = Build(edge => edge.AddOnnxEmbeddings(
            configure: null,
            pipeline: builder => builder.UseLogging()));

        var generator = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        // Wrapped, but GetService still forwards to the inner generator, which is the whole
        // contract MEVD relies on.
        Assert.IsNotType<OnnxEmbeddingGenerator>(generator);
        Assert.IsType<OnnxEmbeddingGenerator>(generator.GetService(typeof(OnnxEmbeddingGenerator)));
    }

    [Fact]
    public void PinningIsAppliedAtRegistrationAndABadValueFailsThere()
    {
        using var pinned = Build(edge => edge.AddOnnxEmbeddings(o => o.PinnedSequenceLength = 128));

        var registered = pinned.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        Assert.NotNull(registered);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Build(edge => edge.AddOnnxEmbeddings(o => o.PinnedSequenceLength = 100)));

        Assert.Contains("SequenceBuckets", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyedOnlyRegistrationsDiagnosticsBlockDescribesITSPresetRatherThanTheDefault()
    {
        // The keyed overload registers its options accessor UNDER ITS KEY, so a contributor that
        // took an unkeyed IOptions<OnnxEmbeddingOptions> would resolve a default-constructed
        // instance and report all-minilm-l6-v2-int8 / 384 / Mean for an app running bge: right
        // shape, wrong numbers, no error anywhere.
        using var provider = Build(edge =>
            edge.AddOnnxEmbeddings("bge", o => o.Preset = EmbeddingPresets.BgeSmallEnV15));

        var report = provider.GetRequiredService<IEdgeDiagnostics>().Report();

        // Spec 14.3 names this block with ONE literal, keyed or not. A keyed registration is told
        // apart by the serviceKey detail, never by a decorated component name: a report consumer
        // that matched on the name would stop finding the block the day an app went keyed.
        var block = Assert.Single(report.Components, c => c.Name == "Qavren.Edge.Embeddings.Onnx");

        Assert.Equal("bge", block.Details["serviceKey"]);
        Assert.Equal(EmbeddingPresets.BgeSmallEnV15.Id, block.Details["preset"]);
        Assert.Equal("MIT", block.Details["presetLicense"]);
        Assert.Equal("Cls", block.Details["pooling"]);
        Assert.Equal("512", block.Details["maxSequenceLength"]);
    }

    [Fact]
    public void TwoRegistrationsProduceTwoBlocksEachDescribingItsOwnPreset()
    {
        using var provider = Build(edge => edge
            .AddOnnxEmbeddings()
            .AddOnnxEmbeddings("nomic", o => o.Preset = EmbeddingPresets.NomicEmbedTextV15Int8));

        var report = provider.GetRequiredService<IEdgeDiagnostics>().Report();

        var blocks = report.Components
            .Where(c => c.Name == "Qavren.Edge.Embeddings.Onnx")
            .ToArray();

        Assert.Equal(2, blocks.Length);

        var unkeyed = Assert.Single(blocks, b => b.Details["serviceKey"] is null);
        var keyed = Assert.Single(blocks, b => b.Details["serviceKey"] == "nomic");

        Assert.Equal("384", unkeyed.Details["dimensions"]);
        Assert.Equal("768", keyed.Details["dimensions"]);
    }

    [Fact]
    public void TheSixRollingCountersArePRESENTOnEveryPathIncludingBeforeTheFirstEmbed()
    {
        // Spec 14.3 lists these six as MEMBERS of the block, so every one of them is present
        // whether or not the generator has been resolved or has ever run. An absent key and a null
        // key are not the same thing to a report consumer: absent reads as "this build does not
        // report it", null reads as "nothing has happened yet".
        using var provider = Build(edge => edge.AddOnnxEmbeddings());

        var report = provider.GetRequiredService<IEdgeDiagnostics>().Report();
        var block = Assert.Single(report.Components, c => c.Name == "Qavren.Edge.Embeddings.Onnx");

        string[] members =
        [
            "embeddingsGenerated", "batchesRun", "tokensEncoded",
            "truncatedInputs", "runMsP50", "runMsP95", "effectiveBatchSize",
        ];

        foreach (var member in members)
        {
            Assert.True(block.Details.ContainsKey(member), member + " is missing from the block.");
        }

        // Nothing has embedded, so the four counts are a real zero and the two percentiles have no
        // sample to report.
        Assert.Equal("0", block.Details["embeddingsGenerated"]);
        Assert.Equal("0", block.Details["batchesRun"]);
        Assert.Equal("0", block.Details["tokensEncoded"]);
        Assert.Equal("0", block.Details["truncatedInputs"]);
        Assert.Null(block.Details["runMsP50"]);
        Assert.Null(block.Details["runMsP95"]);
        Assert.Equal("16", block.Details["effectiveBatchSize"]);
    }

    [Fact]
    public void WarmUpEmbeddingsAtStartupRegistersONETaskAtOrder220AndIsOffByDefault()
    {
        using var without = Build(edge => edge.AddOnnxEmbeddings());
        Assert.DoesNotContain(
            without.GetServices<IEdgeStartupTask>(),
            t => t.GetType().Name == "EmbeddingsWarmUpStartupTask");

        using var with = Build(edge => edge.AddOnnxEmbeddings().WarmUpEmbeddingsAtStartup());

        var tokenizerHalf = Assert.Single(
            with.GetServices<IEdgeStartupTask>(),
            t => t.GetType().Name == "EmbeddingsWarmUpStartupTask");
        Assert.Equal(EdgeAiStartupOrder.SessionWarmUp, tokenizerHalf.Order);

        // And NOT L0's session task. Spec 7 presents the two warm-ups as independent opt-ins, so
        // this method no longer calls WarmUpSessionAtStartup on the caller's behalf: L0 registers
        // that task with AddSingleton rather than a TryAdd, so an app that opted into both - or
        // called this one twice - collected duplicate order-220 session tasks.
        Assert.DoesNotContain(
            with.GetServices<IEdgeStartupTask>(),
            t => t.GetType().Name == "OnnxWarmUpStartupTask");
    }

    [Fact]
    public void TheTwoWarmUpsAreINDEPENDENTAndNeitherDuplicatesTheOther()
    {
        using var both = Build(edge => edge
            .AddOnnxEmbeddings()
            .WarmUpEmbeddingsAtStartup()
            .WarmUpSessionAtStartup(EmbeddingPresets.MiniLmL6V2Int8.Manifest.ModelId));

        var tasks = both.GetServices<IEdgeStartupTask>().ToArray();

        Assert.Single(tasks, t => t.GetType().Name == "EmbeddingsWarmUpStartupTask");
        Assert.Single(tasks, t => t.GetType().Name == "OnnxWarmUpStartupTask");
    }

    [Fact]
    public void CallingWarmUpEmbeddingsAtStartupTwiceForOneRegistrationAddsOneTask()
    {
        using var provider = Build(edge => edge
            .AddOnnxEmbeddings()
            .WarmUpEmbeddingsAtStartup()
            .WarmUpEmbeddingsAtStartup());

        Assert.Single(
            provider.GetServices<IEdgeStartupTask>(),
            t => t.GetType().Name == "EmbeddingsWarmUpStartupTask");
    }

    [Fact]
    public void WarmUpEmbeddingsAtStartupNeedsItsRegistrationFirst()
    {
        // The model id to warm comes from the registered preset, so calling this first cannot
        // silently warm the default preset an app never asked for.
        var bare = Assert.Throws<InvalidOperationException>(() =>
            Build(edge => edge.WarmUpEmbeddingsAtStartup()));
        Assert.Contains("AddOnnxEmbeddings", bare.Message, StringComparison.Ordinal);

        var wrongName = Assert.Throws<InvalidOperationException>(() =>
            Build(edge => edge.AddOnnxEmbeddings().WarmUpEmbeddingsAtStartup("bge")));
        Assert.Contains("bge", wrongName.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyedWarmUpWarmsTheKeyedRegistrationAndOnlyIt()
    {
        using var provider = Build(edge => edge
            .AddOnnxEmbeddings()
            .AddOnnxEmbeddings("bge", o => o.Preset = EmbeddingPresets.BgeSmallEnV15)
            .WarmUpEmbeddingsAtStartup("bge"));

        var warmUp = Assert.Single(
            provider.GetServices<IEdgeStartupTask>(),
            t => t.Order == EdgeAiStartupOrder.SessionWarmUp);

        Assert.Equal("EmbeddingsWarmUpStartupTask", warmUp.GetType().Name);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked ORT cache file must not fail a green test run.
        }
    }
}
