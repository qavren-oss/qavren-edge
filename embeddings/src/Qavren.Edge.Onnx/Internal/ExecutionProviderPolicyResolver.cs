using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Qavren.Edge.Onnx.Internal;

/// <summary>The per-model facts the CoreML cache path is built from.</summary>
/// <param name="ModelId">The registered model id.</param>
/// <param name="OrtCacheDirectory">
/// <c>IEdgeModelPaths.OrtCache</c>, or null when the caller has no cache root - in which case no
/// <c>ModelCacheDirectory</c> is emitted at all rather than an empty one.
/// </param>
/// <param name="GraphSha256">
/// The graph's lowercase-hex SHA-256. Its first 16 characters are what make the CoreML cache key
/// content-addressed; null omits <c>ModelCacheDirectory</c> for the same reason as above.
/// </param>
internal readonly record struct ProviderOptionsContext(
    string ModelId,
    string? OrtCacheDirectory,
    string? GraphSha256);

/// <summary>
/// Turns an <see cref="OnnxExecutionProviderPolicy"/> into ORT appends, fail-soft, one provider at
/// a time. Takes an <see cref="ISessionOptionsSink"/> rather than an ORT <c>SessionOptions</c> so
/// the whole policy is assertable with no session and no native library in the path.
/// </summary>
internal static class ExecutionProviderPolicyResolver
{
    /// <summary>ORT's own name for CoreML in the portable string overload.</summary>
    public const string CoreMlProviderName = "CoreML";

    /// <summary>ORT's own name for XNNPACK in the portable string overload.</summary>
    public const string XnnPackProviderName = "XNNPACK";

    private static readonly Action<ILogger, string, string, Exception?> s_providerSkipped =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(EdgeAiEventIds.ExecutionProviderSkipped, nameof(EdgeAiEventIds.ExecutionProviderSkipped)),
            "Execution provider {Provider} was not appended: {Failure}. Falling through.");

    private static readonly Action<ILogger, string, Exception?> s_providerAccepted =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(EdgeAiEventIds.ExecutionProviderAccepted, nameof(EdgeAiEventIds.ExecutionProviderAccepted)),
            "Execution provider {Provider} appended without throwing. That does not mean it took every node.");

    private static readonly Action<ILogger, int, Exception?> s_intraOpForced =
        LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(EdgeAiEventIds.ExecutionProviderAccepted, nameof(EdgeAiEventIds.ExecutionProviderAccepted)),
            "XNNPACK is active, so SessionOptions.IntraOpNumThreads is forced to 1; the configured value {Configured} was ignored, per ORT's anti-contention guidance.");

    /// <summary>Whether ORT 1.30.0 ships a native for this runtime identifier at all.</summary>
    /// <param name="runtimeIdentifier">The host RID, as <c>RuntimeInformation.RuntimeIdentifier</c> spells it.</param>
    /// <returns>False for <c>osx-x64</c> and for any family with no ORT native.</returns>
    public static bool IsRuntimeSupported(string runtimeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        var (family, architecture) = SplitRid(runtimeIdentifier);
        return family switch
        {
            "win" or "linux" or "android" or "ios" or "iossimulator" or "maccatalyst" => true,
            // ORT 1.30.0's runtimes/ folder has no osx-x64 entry; Intel macOS is not shipped.
            "osx" => string.Equals(architecture, "arm64", StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>The named failure a caller gets instead of a <c>DllNotFoundException</c>.</summary>
    /// <param name="runtimeIdentifier">The unsupported RID.</param>
    /// <returns>The exception to throw.</returns>
    public static EdgeOnnxException UnsupportedRuntime(string runtimeIdentifier) =>
        new(
            EdgeErrorCode.OnnxUnsupportedRuntime,
            $"ONNX Runtime 1.30.0 ships no native library for runtime identifier '{runtimeIdentifier}'. " +
            "Qavren.Edge.Onnx supports win-x64, win-arm64, linux-*, osx-arm64, android, ios, " +
            "iossimulator and maccatalyst.")
        {
            RuntimeIdentifier = runtimeIdentifier,
            Remediation = "Run on an Apple-silicon Mac, or host the embedding model on a machine with a shipped ORT native.",
        };

    /// <summary>The per-RID default order from spec 9.2.</summary>
    /// <param name="runtimeIdentifier">The host RID.</param>
    /// <returns>The providers to try, in order, CPU last.</returns>
    public static IReadOnlyList<EdgeExecutionProvider> DefaultOrder(string runtimeIdentifier)
    {
        if (!IsRuntimeSupported(runtimeIdentifier))
        {
            throw UnsupportedRuntime(runtimeIdentifier);
        }

        var (family, _) = SplitRid(runtimeIdentifier);
        return family switch
        {
            "ios" or "iossimulator" or "maccatalyst" => [EdgeExecutionProvider.CoreMl, EdgeExecutionProvider.Cpu],
            "android" => [EdgeExecutionProvider.XnnPack, EdgeExecutionProvider.Cpu],
            _ => [EdgeExecutionProvider.Cpu],
        };
    }

    /// <summary>ORT's string name for a provider, or null for CPU, which is never appended.</summary>
    /// <param name="provider">The provider.</param>
    /// <returns>The name ORT's portable overload expects.</returns>
    public static string? ProviderName(EdgeExecutionProvider provider) => provider switch
    {
        EdgeExecutionProvider.CoreMl => CoreMlProviderName,
        EdgeExecutionProvider.XnnPack => XnnPackProviderName,
        _ => null,
    };

    /// <summary>Builds the option dictionary one provider is handed, overrides already merged.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="policy">The policy.</param>
    /// <param name="context">The per-model cache facts.</param>
    /// <returns>The merged dictionary, ordinal-keyed.</returns>
    public static IReadOnlyDictionary<string, string> BuildProviderOptions(
        EdgeExecutionProvider provider,
        OnnxExecutionProviderPolicy policy,
        ProviderOptionsContext context)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var options = new Dictionary<string, string>(StringComparer.Ordinal);

        switch (provider)
        {
            case EdgeExecutionProvider.CoreMl:
                options["ModelFormat"] = policy.CoreMl.ModelFormat;
                options["MLComputeUnits"] = policy.CoreMl.MLComputeUnits;
                if (policy.CoreMl.RequireStaticInputShapes)
                {
                    options["RequireStaticInputShapes"] = "1";
                }

                var cache = ModelCacheDirectory(policy, context);
                if (cache is not null)
                {
                    options["ModelCacheDirectory"] = cache;
                }

                if (policy.CoreMl.FastPrediction)
                {
                    options["SpecializationStrategy"] = "FastPrediction";
                }

                if (policy.CoreMl.ProfileComputePlan)
                {
                    options["ProfileComputePlan"] = "1";
                }

                break;

            case EdgeExecutionProvider.XnnPack:
                var threads = policy.XnnPack.IntraOpNumThreads
                    ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
                options["intra_op_num_threads"] = threads.ToString(CultureInfo.InvariantCulture);
                break;

            case EdgeExecutionProvider.Cpu:
            default:
                break;
        }

        var name = ProviderName(provider);
        if (name is not null && policy.Overrides.TryGetValue(name, out var overrides))
        {
            foreach (var (key, value) in overrides)
            {
                options[key] = value;
            }
        }

        return options;
    }

    /// <summary>
    /// Applies the policy to <paramref name="sink"/>, fail-soft, one provider at a time.
    /// </summary>
    /// <param name="policy">The policy.</param>
    /// <param name="runtimeIdentifier">The host RID.</param>
    /// <param name="sink">Where the appends land.</param>
    /// <param name="context">The per-model cache facts.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>What was accepted and every attempt that was made.</returns>
    public static ExecutionProviderReport Apply(
        OnnxExecutionProviderPolicy policy,
        string runtimeIdentifier,
        ISessionOptionsSink sink,
        ProviderOptionsContext context,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(sink);

        ValidateOverrideKeys(policy);

        if (!IsRuntimeSupported(runtimeIdentifier))
        {
            throw UnsupportedRuntime(runtimeIdentifier);
        }

        var order = ResolveOrder(policy, runtimeIdentifier);

        sink.GraphOptimizationLevel = policy.GraphOptimization;
        sink.IntraOpNumThreads = policy.IntraOpNumThreads;

        var attempts = new List<ExecutionProviderAttempt>(order.Count);
        EdgeExecutionProvider? accepted = null;
        var xnnPackAccepted = false;

        foreach (var provider in order)
        {
            var options = BuildProviderOptions(provider, policy, context);
            var name = ProviderName(provider);

            if (name is null)
            {
                // CPU is ORT's built-in fallback. There is nothing to append: asking ORT for "CPU"
                // through the portable overload is not a supported provider name.
                attempts.Add(new ExecutionProviderAttempt(provider, true, options, null));
                accepted ??= provider;
                continue;
            }

            try
            {
                sink.AppendExecutionProvider(name, new Dictionary<string, string>(options, StringComparer.Ordinal));
            }
#pragma warning disable CA1031 // Fail-soft is the contract: any append failure falls through to the next provider.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                attempts.Add(new ExecutionProviderAttempt(provider, false, options, ex.Message));
                if (logger is not null)
                {
                    s_providerSkipped(logger, name, ex.Message, ex);
                }

                if (policy.Required.Contains(provider))
                {
                    throw new EdgeOnnxException(
                        EdgeErrorCode.OnnxExecutionProviderRequired,
                        $"Execution provider '{name}' is listed in OnnxExecutionProviderPolicy.Required but " +
                        $"could not be appended: {ex.Message}",
                        ex)
                    {
                        ModelId = context.ModelId,
                        RuntimeIdentifier = runtimeIdentifier,
                        ExecutionProviders = attempts,
                        Remediation = "Remove the provider from Required to fall back to CPU, or run on hardware that supports it.",
                    };
                }

                continue;
            }

            attempts.Add(new ExecutionProviderAttempt(provider, true, options, null));
            accepted ??= provider;
            xnnPackAccepted |= provider == EdgeExecutionProvider.XnnPack;
            if (logger is not null)
            {
                s_providerAccepted(logger, name, null);
            }
        }

        if (xnnPackAccepted)
        {
            if (policy.IntraOpNumThreads is not 0 and not 1 && logger is not null)
            {
                s_intraOpForced(logger, policy.IntraOpNumThreads, null);
            }

            sink.IntraOpNumThreads = 1;
        }

        // CPU is always present in ORT, so a report that recorded nothing still ran on CPU.
        return new ExecutionProviderReport(accepted ?? EdgeExecutionProvider.Cpu, attempts);
    }

    private static List<EdgeExecutionProvider> ResolveOrder(
        OnnxExecutionProviderPolicy policy,
        string runtimeIdentifier)
    {
        var order = new List<EdgeExecutionProvider>();
        var source = policy.Order is { Count: > 0 } explicitOrder
            ? explicitOrder
            : DefaultOrder(runtimeIdentifier);

        foreach (var provider in source)
        {
            if (provider != EdgeExecutionProvider.Cpu && !order.Contains(provider))
            {
                order.Add(provider);
            }
        }

        // CPU is appended LAST whenever FallBackToCpu, never in the middle: ORT tries providers in
        // append order, and a CPU entry ahead of an accelerator would take the whole graph.
        if (policy.FallBackToCpu || source.Contains(EdgeExecutionProvider.Cpu))
        {
            order.Add(EdgeExecutionProvider.Cpu);
        }

        return order;
    }

    private static void ValidateOverrideKeys(OnnxExecutionProviderPolicy policy)
    {
        foreach (var key in policy.Overrides.Keys)
        {
            if (!string.Equals(key, CoreMlProviderName, StringComparison.Ordinal) &&
                !string.Equals(key, XnnPackProviderName, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"'{key}' is not an execution provider this package can append. " +
                    $"OnnxExecutionProviderPolicy.Overrides accepts '{CoreMlProviderName}' and " +
                    $"'{XnnPackProviderName}' only; NNAPI in particular is not reachable through ORT's " +
                    "portable AppendExecutionProvider(string, ...) overload and is not offered (ADR 0004).",
                    nameof(policy));
            }
        }
    }

    private static string? ModelCacheDirectory(
        OnnxExecutionProviderPolicy policy,
        ProviderOptionsContext context)
    {
        if (!policy.CoreMl.EnableModelCache)
        {
            // Omitted ENTIRELY rather than emitted empty: an empty ModelCacheDirectory is not the
            // same request as no ModelCacheDirectory.
            return null;
        }

        if (context.OrtCacheDirectory is not { Length: > 0 } root ||
            context.GraphSha256 is not { Length: >= 16 } sha)
        {
            return null;
        }

        return Path.Combine(root, context.ModelId, sha[..16]);
    }

    private static (string Family, string Architecture) SplitRid(string runtimeIdentifier)
    {
        var separator = runtimeIdentifier.IndexOf('-', StringComparison.Ordinal);
        if (separator < 0)
        {
            return (runtimeIdentifier, string.Empty);
        }

        var family = runtimeIdentifier[..separator];
        var last = runtimeIdentifier.LastIndexOf('-');
        return (family, runtimeIdentifier[(last + 1)..]);
    }
}
