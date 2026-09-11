using Qavren.Edge.Onnx.Internal;
using Qavren.Edge.Onnx.Tests.Stubs;
using Xunit;

namespace Qavren.Edge.Onnx.Tests;

/// <summary>
/// The L0 half of spec 16.1's session-factory sentence, over a real ORT <c>SessionOptions</c> and
/// no session.
/// </summary>
/// <remarks>
/// The L1 half - "<c>PinnedSequenceLength</c> populates both the override dictionary and the flag"
/// - cannot be asserted from here: <c>PinnedSequenceLength</c> is a member of
/// <c>OnnxEmbeddingOptions</c> in <c>Qavren.Edge.Embeddings.Onnx</c>, which this project does not
/// and must not reference. It is <c>PinnedSequenceLengthTests</c> in the embeddings suite.
/// </remarks>
public class SessionFactoryShapeTests
{
    private const string ModelId = "bge-small-en-v1.5";
    private const string GraphSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void StaticShapesWithoutOverridesIsRefusedAndNamesPinnedSequenceLength()
    {
        var options = new OnnxSessionOptions();
        options.ExecutionProviders.CoreMl.RequireStaticInputShapes = true;

        // Through the real Build, so the refusal is proved over an actual ORT SessionOptions.
        var ex = Assert.Throws<EdgeOnnxException>(() => SessionOptionsFactory.Build(ModelId, options));

        Assert.Equal(EdgeErrorCode.OnnxStaticShapesUnpinned, ex.Code);
        Assert.Contains("PinnedSequenceLength", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticShapesWithOverridesEmitsTheFlagAndPinsEveryDimensionOnce()
    {
        var options = new OnnxSessionOptions();
        options.ExecutionProviders.CoreMl.RequireStaticInputShapes = true;
        options.FreeDimensionOverrides["batch_size"] = 8;
        options.FreeDimensionOverrides["sequence_length"] = 256;

        var sink = new RecordingSessionOptionsSink();

        var report = SessionOptionsFactory.BuildInto(
            sink, ModelId, options,
            ortCacheDirectory: "/tmp/ort-cache",
            graphSha256: GraphSha,
            environment: null,
            registeredModelCount: 1,
            runtimeIdentifier: "ios-arm64");

        var coreMl = Assert.Single(report.Attempts, a => a.Provider == EdgeExecutionProvider.CoreMl);
        Assert.Equal("1", coreMl.Options["RequireStaticInputShapes"]);

        Assert.Equal(2, sink.FreeDimensionOverrides.Count);
        Assert.Contains(("batch_size", 8L), sink.FreeDimensionOverrides);
        Assert.Contains(("sequence_length", 256L), sink.FreeDimensionOverrides);
    }

    [Fact]
    public void SessionConfigEntriesGoThroughTheirOwnOrtApiAndAreNotFreeDimensionOverrides()
    {
        var options = new OnnxSessionOptions();
        options.SessionConfigEntries["session.use_env_allocators"] = "1";
        options.FreeDimensionOverrides["sequence_length"] = 128;

        var sink = new RecordingSessionOptionsSink();

        SessionOptionsFactory.BuildInto(
            sink, ModelId, options,
            ortCacheDirectory: null,
            graphSha256: null,
            environment: null,
            registeredModelCount: 1,
            runtimeIdentifier: "win-x64");

        var entry = Assert.Single(sink.SessionConfigEntries);
        Assert.Equal(("session.use_env_allocators", "1"), entry);

        var dimension = Assert.Single(sink.FreeDimensionOverrides);
        Assert.Equal(("sequence_length", 128L), dimension);
    }

    [Theory]
    [InlineData(true, 1, 1)]
    [InlineData(null, 1, 0)]
    [InlineData(null, 2, 1)]
    [InlineData(false, 5, 0)]
    public void ShareThreadPoolDecidesDisablePerSessionThreads(
        bool? shareThreadPool,
        int registeredModelCount,
        int expectedCalls)
    {
        var options = new OnnxSessionOptions();
        var sink = new RecordingSessionOptionsSink();

        SessionOptionsFactory.BuildInto(
            sink, ModelId, options,
            ortCacheDirectory: null,
            graphSha256: null,
            environment: new OnnxOptions { ShareThreadPool = shareThreadPool },
            registeredModelCount: registeredModelCount,
            runtimeIdentifier: "win-x64");

        Assert.Equal(expectedCalls, sink.DisablePerSessionThreadsCount);
    }

    [Fact]
    public void ADefaultBuildProducesUsableOrtSessionOptions()
    {
        var options = new OnnxSessionOptions();

        var (sessionOptions, report) = SessionOptionsFactory.Build(ModelId, options);
        using (sessionOptions)
        {
            Assert.Equal(EdgeExecutionProvider.Cpu, report.Accepted);
            Assert.Equal(options.ExecutionProviders.GraphOptimization, sessionOptions.GraphOptimizationLevel);
        }
    }
}
