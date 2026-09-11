using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace Qavren.Edge.Onnx.Internal;

/// <summary>
/// The narrow slice of ORT's <see cref="SessionOptions"/> this package writes to. It exists so the
/// policy above it can be asserted with no session, no <c>OrtEnv</c> and no native library: a test
/// records the calls, production forwards them.
/// </summary>
internal interface ISessionOptionsSink
{
    /// <summary>ORT's portable <c>AppendExecutionProvider(string, Dictionary)</c> overload.</summary>
    /// <param name="providerName">"CoreML" or "XNNPACK".</param>
    /// <param name="providerOptions">The provider's string-keyed options.</param>
    void AppendExecutionProvider(string providerName, Dictionary<string, string> providerOptions);

    /// <summary>ORT's <c>AddFreeDimensionOverrideByName</c>: rewrites a declared symbolic dimension.</summary>
    /// <param name="dimensionName">The symbolic dimension name.</param>
    /// <param name="dimensionValue">The fixed value.</param>
    void AddFreeDimensionOverrideByName(string dimensionName, long dimensionValue);

    /// <summary>ORT's <c>AddSessionConfigEntry</c>: a different API from the one above.</summary>
    /// <param name="configKey">The config key.</param>
    /// <param name="configValue">The config value.</param>
    void AddSessionConfigEntry(string configKey, string configValue);

    /// <summary>ORT's <c>DisablePerSessionThreads</c>: every session shares one process pool.</summary>
    void DisablePerSessionThreads();

    /// <summary>ORT's intra-op thread count. 0 lets ORT choose.</summary>
    int IntraOpNumThreads { get; set; }

    /// <summary>ORT's graph-optimization level.</summary>
    GraphOptimizationLevel GraphOptimizationLevel { get; set; }
}

/// <summary>Forwards <see cref="ISessionOptionsSink"/> onto a real ORT <see cref="SessionOptions"/>.</summary>
internal sealed class OrtSessionOptionsSink(SessionOptions options) : ISessionOptionsSink
{
    public void AppendExecutionProvider(string providerName, Dictionary<string, string> providerOptions)
        => options.AppendExecutionProvider(providerName, providerOptions);

    public void AddFreeDimensionOverrideByName(string dimensionName, long dimensionValue)
        => options.AddFreeDimensionOverrideByName(dimensionName, dimensionValue);

    public void AddSessionConfigEntry(string configKey, string configValue)
        => options.AddSessionConfigEntry(configKey, configValue);

    public void DisablePerSessionThreads() => options.DisablePerSessionThreads();

    public int IntraOpNumThreads
    {
        get => options.IntraOpNumThreads;
        set => options.IntraOpNumThreads = value;
    }

    public GraphOptimizationLevel GraphOptimizationLevel
    {
        get => options.GraphOptimizationLevel;
        set => options.GraphOptimizationLevel = value;
    }
}

/// <summary>
/// Builds a configured ORT <see cref="SessionOptions"/>. The single place
/// AddFreeDimensionOverrideByName, AddSessionConfigEntry, DisablePerSessionThreads,
/// GraphOptimizationLevel, IntraOpNumThreads and the execution-provider append loop meet, so the
/// OnnxStaticShapesUnpinned guard has exactly one home. Internal: a consumer configures this
/// through OnnxSessionOptions and never touches SessionOptions directly.
/// </summary>
internal static class SessionOptionsFactory
{
    /// <summary>
    /// Order is load-bearing. Free-dimension overrides are applied BEFORE the providers are
    /// appended, because CoreML partitions from the declared shapes at append time; applying them
    /// afterwards would leave the graph symbolic for exactly the decision they exist to change.
    /// Throws <see cref="EdgeOnnxException"/> with
    /// <see cref="EdgeErrorCode.OnnxStaticShapesUnpinned"/> when
    /// <c>CoreMlProviderOptions.RequireStaticInputShapes</c> is set and
    /// <c>options.FreeDimensionOverrides</c> is empty.
    /// </summary>
    /// <param name="modelId">The registered model id.</param>
    /// <param name="options">The per-model session options.</param>
    /// <param name="ortCacheDirectory">The CoreML cache root, or null.</param>
    /// <param name="graphSha256">The graph's lowercase-hex SHA-256, or null.</param>
    /// <param name="environment">
    /// The process-wide options. Needed here because <c>ShareThreadPool</c> decides
    /// <c>DisablePerSessionThreads</c>, which is a <see cref="SessionOptions"/> call and therefore
    /// this factory's business. (Plan adjustment 22 moved the factory into wave 2 without
    /// restating its parameter list; this and <paramref name="registeredModelCount"/> are the two
    /// additions the <c>ShareThreadPool</c> assertion in SessionFactoryShapeTests requires.)
    /// </param>
    /// <param name="registeredModelCount">
    /// How many models the host has registered. A null <c>ShareThreadPool</c> resolves to true
    /// once this is greater than one.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>The configured options and the execution-provider report.</returns>
    public static (SessionOptions Options, ExecutionProviderReport Report) Build(
        string modelId,
        OnnxSessionOptions options,
        string? ortCacheDirectory = null,
        string? graphSha256 = null,
        OnnxOptions? environment = null,
        int registeredModelCount = 1,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(options);

        var sessionOptions = new SessionOptions();
        try
        {
            var report = BuildInto(
                new OrtSessionOptionsSink(sessionOptions),
                modelId,
                options,
                ortCacheDirectory,
                graphSha256,
                environment,
                registeredModelCount,
                RuntimeInformation.RuntimeIdentifier,
                logger);

            return (sessionOptions, report);
        }
        catch
        {
            sessionOptions.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The whole of <see cref="Build"/> minus the ORT allocation, so the order and the guard can be
    /// asserted against a recording sink.
    /// </summary>
    /// <param name="sink">Where the calls land.</param>
    /// <param name="modelId">The registered model id.</param>
    /// <param name="options">The per-model session options.</param>
    /// <param name="ortCacheDirectory">The CoreML cache root, or null.</param>
    /// <param name="graphSha256">The graph's lowercase-hex SHA-256, or null.</param>
    /// <param name="environment">The process-wide options, or null.</param>
    /// <param name="registeredModelCount">How many models the host has registered.</param>
    /// <param name="runtimeIdentifier">The host RID.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>The execution-provider report.</returns>
    public static ExecutionProviderReport BuildInto(
        ISessionOptionsSink sink,
        string modelId,
        OnnxSessionOptions options,
        string? ortCacheDirectory,
        string? graphSha256,
        OnnxOptions? environment,
        int registeredModelCount,
        string runtimeIdentifier,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(options);

        if (options.ExecutionProviders.CoreMl.RequireStaticInputShapes &&
            options.FreeDimensionOverrides.Count == 0)
        {
            throw new EdgeOnnxException(
                EdgeErrorCode.OnnxStaticShapesUnpinned,
                "CoreMlProviderOptions.RequireStaticInputShapes is set but OnnxSessionOptions." +
                "FreeDimensionOverrides is empty. CoreML partitions the graph at session-creation time " +
                "from the shapes the graph DECLARES, so requiring static shapes against symbolic " +
                "[batch_size, sequence_length] dimensions moves the whole graph to CPU and nothing " +
                "reports it. Pin the dimensions first: OnnxEmbeddingOptions.PinnedSequenceLength is the " +
                "supported way to get there, and it sets both the overrides and this flag.")
            {
                ModelId = modelId,
                RuntimeIdentifier = runtimeIdentifier,
                Remediation = "Set OnnxEmbeddingOptions.PinnedSequenceLength, or populate OnnxSessionOptions.FreeDimensionOverrides yourself.",
            };
        }

        if (ShouldShareThreadPool(environment?.ShareThreadPool, registeredModelCount))
        {
            sink.DisablePerSessionThreads();
        }

        // BEFORE the append loop, on purpose: see the summary on Build.
        foreach (var (dimension, value) in options.FreeDimensionOverrides)
        {
            sink.AddFreeDimensionOverrideByName(dimension, value);
        }

        // A DIFFERENT ORT API from the loop above, and never merged with it.
        foreach (var (key, value) in options.SessionConfigEntries)
        {
            sink.AddSessionConfigEntry(key, value);
        }

        return ExecutionProviderPolicyResolver.Apply(
            options.ExecutionProviders,
            runtimeIdentifier,
            sink,
            new ProviderOptionsContext(modelId, ortCacheDirectory, graphSha256),
            logger);
    }

    /// <summary>
    /// A null <c>ShareThreadPool</c> means "true once more than one model is registered".
    /// </summary>
    /// <param name="configured">The configured value, or null for the default.</param>
    /// <param name="registeredModelCount">How many models the host has registered.</param>
    /// <returns>Whether <c>DisablePerSessionThreads</c> is called.</returns>
    public static bool ShouldShareThreadPool(bool? configured, int registeredModelCount)
        => configured ?? registeredModelCount > 1;
}
