using Qavren.Edge.Onnx.Internal;
using Qavren.Edge.Onnx.Tests.Stubs;
using Xunit;

namespace Qavren.Edge.Onnx.Tests;

/// <summary>
/// Spec 9.2's per-RID table and every option it produces, asserted with no ORT session created.
/// </summary>
public class ExecutionProviderPolicyTests
{
    private const string ModelId = "bge-small-en-v1.5";
    private const string OrtCache = "/tmp/ort-cache";
    private const string GraphSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static ProviderOptionsContext Context() => new(ModelId, OrtCache, GraphSha);

    [Theory]
    [InlineData("ios-arm64")]
    [InlineData("iossimulator-arm64")]
    [InlineData("maccatalyst-arm64")]
    public void AppleRidsDefaultToCoreMlThenCpu(string rid)
    {
        Assert.Equal(
            new[] { EdgeExecutionProvider.CoreMl, EdgeExecutionProvider.Cpu },
            ExecutionProviderPolicyResolver.DefaultOrder(rid));
    }

    [Theory]
    [InlineData("android-arm64")]
    [InlineData("android-x64")]
    public void AndroidDefaultsToXnnPackThenCpu(string rid)
    {
        Assert.Equal(
            new[] { EdgeExecutionProvider.XnnPack, EdgeExecutionProvider.Cpu },
            ExecutionProviderPolicyResolver.DefaultOrder(rid));
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    [InlineData("linux-x64")]
    [InlineData("linux-musl-arm64")]
    [InlineData("osx-arm64")]
    public void DesktopRidsDefaultToCpuOnly(string rid)
    {
        Assert.Equal(new[] { EdgeExecutionProvider.Cpu }, ExecutionProviderPolicyResolver.DefaultOrder(rid));
    }

    [Fact]
    public void IntelMacIsReportedUnsupported()
    {
        Assert.False(ExecutionProviderPolicyResolver.IsRuntimeSupported("osx-x64"));

        var ex = Assert.Throws<EdgeOnnxException>(
            () => ExecutionProviderPolicyResolver.DefaultOrder("osx-x64"));

        Assert.Equal(EdgeErrorCode.OnnxUnsupportedRuntime, ex.Code);
        Assert.Equal("osx-x64", ex.RuntimeIdentifier);
    }

    [Fact]
    public void CoreMlDefaultsAreMlProgramAneAndAContentAddressedCache()
    {
        var policy = new OnnxExecutionProviderPolicy();

        var options = ExecutionProviderPolicyResolver.BuildProviderOptions(
            EdgeExecutionProvider.CoreMl, policy, Context());

        Assert.Equal("MLProgram", options["ModelFormat"]);
        Assert.Equal("CPUAndNeuralEngine", options["MLComputeUnits"]);
        Assert.Equal(
            Path.Combine(OrtCache, ModelId, GraphSha[..16]),
            options["ModelCacheDirectory"]);

        // Load-bearing: the flag is never set on its own, so the default dictionary must not carry it.
        Assert.False(options.ContainsKey("RequireStaticInputShapes"));
        Assert.False(options.ContainsKey("SpecializationStrategy"));
        Assert.False(options.ContainsKey("ProfileComputePlan"));
    }

    [Fact]
    public void DisablingTheModelCacheOmitsTheKeyRatherThanEmptyingIt()
    {
        var policy = new OnnxExecutionProviderPolicy();
        policy.CoreMl.EnableModelCache = false;

        var options = ExecutionProviderPolicyResolver.BuildProviderOptions(
            EdgeExecutionProvider.CoreMl, policy, Context());

        Assert.False(options.ContainsKey("ModelCacheDirectory"));
    }

    [Fact]
    public void FastPredictionEmitsTheSpecializationStrategy()
    {
        var policy = new OnnxExecutionProviderPolicy();
        policy.CoreMl.FastPrediction = true;

        var options = ExecutionProviderPolicyResolver.BuildProviderOptions(
            EdgeExecutionProvider.CoreMl, policy, Context());

        Assert.Equal("FastPrediction", options["SpecializationStrategy"]);
    }

    [Fact]
    public void ProfileComputePlanEmitsItsFlag()
    {
        var policy = new OnnxExecutionProviderPolicy();
        policy.CoreMl.ProfileComputePlan = true;

        var options = ExecutionProviderPolicyResolver.BuildProviderOptions(
            EdgeExecutionProvider.CoreMl, policy, Context());

        Assert.Equal("1", options["ProfileComputePlan"]);
    }

    [Fact]
    public void XnnPackClampsItsOwnPoolAndForcesOrtsIntraOpThreadsToOne()
    {
        var policy = new OnnxExecutionProviderPolicy { IntraOpNumThreads = 8 };
        var sink = new RecordingSessionOptionsSink();

        var report = ExecutionProviderPolicyResolver.Apply(policy, "android-arm64", sink, Context());

        var expected = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        var appended = Assert.Single(sink.Appended);
        Assert.Equal("XNNPACK", appended.Provider);
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), appended.Options["intra_op_num_threads"]);
        Assert.Equal(1, sink.IntraOpNumThreads);
        Assert.Equal(EdgeExecutionProvider.XnnPack, report.Accepted);
    }

    [Fact]
    public void AProviderThatThrowsIsRecordedAndTheLoopFallsThroughToCpu()
    {
        var policy = new OnnxExecutionProviderPolicy();
        var sink = new RecordingSessionOptionsSink(
            name => name == "CoreML" ? new InvalidOperationException("no CoreML here") : null);

        var report = ExecutionProviderPolicyResolver.Apply(policy, "ios-arm64", sink, Context());

        Assert.Equal(EdgeExecutionProvider.Cpu, report.Accepted);

        var coreMl = Assert.Single(report.Attempts, a => a.Provider == EdgeExecutionProvider.CoreMl);
        Assert.False(coreMl.Accepted);
        Assert.NotNull(coreMl.Failure);
        Assert.Contains("no CoreML here", coreMl.Failure, StringComparison.Ordinal);

        var cpu = Assert.Single(report.Attempts, a => a.Provider == EdgeExecutionProvider.Cpu);
        Assert.True(cpu.Accepted);
    }

    [Fact]
    public void ARequiredProviderThatFailsThrowsInsteadOfFallingThrough()
    {
        var policy = new OnnxExecutionProviderPolicy
        {
            Required = [EdgeExecutionProvider.CoreMl],
        };

        var sink = new RecordingSessionOptionsSink(
            name => name == "CoreML" ? new InvalidOperationException("no CoreML here") : null);

        var ex = Assert.Throws<EdgeOnnxException>(
            () => ExecutionProviderPolicyResolver.Apply(policy, "ios-arm64", sink, Context()));

        Assert.Equal(EdgeErrorCode.OnnxExecutionProviderRequired, ex.Code);
        Assert.NotNull(ex.ExecutionProviders);
        Assert.Contains(ex.ExecutionProviders, a => a.Provider == EdgeExecutionProvider.CoreMl && !a.Accepted);
    }

    [Fact]
    public void OverridesMergeOverTheComputedDictionary()
    {
        var policy = new OnnxExecutionProviderPolicy();
        policy.Overrides["CoreML"] = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MLComputeUnits"] = "CPUOnly",
            ["AllowLowPrecisionAccumulationOnGPU"] = "1",
        };

        var options = ExecutionProviderPolicyResolver.BuildProviderOptions(
            EdgeExecutionProvider.CoreMl, policy, Context());

        Assert.Equal("CPUOnly", options["MLComputeUnits"]);
        Assert.Equal("1", options["AllowLowPrecisionAccumulationOnGPU"]);
        Assert.Equal("MLProgram", options["ModelFormat"]);
    }

    [Fact]
    public void NnapiIsRejectedAsAnUnknownProviderName()
    {
        var policy = new OnnxExecutionProviderPolicy();
        policy.Overrides["NNAPI"] = new Dictionary<string, string>(StringComparer.Ordinal);

        var sink = new RecordingSessionOptionsSink();

        var ex = Assert.Throws<ArgumentException>(
            () => ExecutionProviderPolicyResolver.Apply(policy, "android-arm64", sink, Context()));

        Assert.Contains("NNAPI", ex.Message, StringComparison.Ordinal);
        Assert.Empty(sink.Appended);
    }
}
