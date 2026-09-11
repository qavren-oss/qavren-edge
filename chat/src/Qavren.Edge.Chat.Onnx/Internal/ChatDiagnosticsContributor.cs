using System.Diagnostics;
using System.Globalization;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Chat.Internal;

/// <summary>
/// Spec section 14.3's chat block. The component name deliberately does <b>not</b> contain
/// "Native", so sub-project 1's <c>EdgeDiagnostics.Report()</c> keeps picking the SQLite native
/// block for <c>EdgeDiagnosticsReport.Native</c> - sub-project 2's rule, obeyed.
/// </summary>
/// <remarks>
/// Keyed registrations get one contributor each, told apart by the <c>serviceKey</c> detail rather
/// than by a decorated component name, matching sub-project 2's embeddings contributor. Every read
/// goes through <see cref="Safe"/>, and a read whose <i>resolution</i> throws makes every value
/// taken through it the same sentinel - because a factory that fails must not make the whole block
/// read "not registered".
/// <para>
/// <b>Three keys pay for themselves.</b> <c>guidanceEnforced</c> and <c>chatTemplateSupported</c>
/// are properties of the shipped native build and of the specific model, neither knowable from a
/// version number, and both fail silently if nobody looks. <c>budgetExplanation</c> is why a
/// consumer got 2048 tokens on one phone and 4096 on another.
/// </para>
/// </remarks>
internal sealed class ChatDiagnosticsContributor(
    IServiceProvider services,
    ChatRegistration registration,
    ChatEnvironmentState environment,
    IEdgeResourceMonitor monitor) : IEdgeDiagnosticsContributor
{
    /// <summary>Every key this block publishes, in section 14.3's order.</summary>
    /// <remarks>
    /// Public to the test assembly on purpose: the tier-1 assertion compares the contributor's key
    /// set against a literal list written in the test file, and a "present" check that reads the
    /// contributor's own dictionary keys would prove nothing.
    /// </remarks>
    internal static IReadOnlyList<string> Keys { get; } =
    [
        "serviceKey",

        // Environment
        "genAiVersion", "genAiManagedAsset", "ortVersionInProcess", "ortEnvCreatedBeforeGenAi",
        "ogaHandleOwned", "telemetryDisabled", "runtimeIdentifier", "abi", "chatSupportedOnThisAbi",
        "totalMemoryBytes", "availableMemoryKind", "isLowRamDevice", "chatIntraOpNumThreads",

        // Model
        "presetId", "modelId", "displayName", "spdxLicense", "huggingFaceRepo",
        "huggingFaceRevision", "modelDirectory", "provisioned", "bundleBytes", "weightsBytes",
        "freeDiskBytes", "modelType", "configContextLength", "vocabSize", "numHiddenLayers",
        "numKeyValueHeads", "headSize", "slidingWindow", "kvBytesPerToken", "kvBytesPerElement",
        "providers", "chatTemplateSupported", "promptFormatter", "guidanceEnforced", "loaded",
        "loadMs", "loadCount", "leases",

        // Budget
        "budgetVerdict", "budgetExplanation", "requestedContextTokens", "resolvedContextTokens",
        "requiredBytes", "kvCacheBytes", "workspaceBytes", "reserveBytes", "availableBytes",
        "usableBytes", "usedMeasuredPeak", "measuredPeakBytes", "measuredOn",

        // Runtime honesty
        "turns", "rejectedTurns", "tokensGenerated", "tokensPerSecondP50", "tokensPerSecondP95",
        "lastTokensPerSecond", "lastTtftMs", "lastStopReason", "historyReductions",
        "thermalThrottleEvents", "thermalAbortEvents", "terminationEvents", "unloadEvents",
        "processWorkingSetBytes", "peakWorkingSetBytes", "thermalState", "thermalHeadroom",
        "isLowPowerMode", "lastMemoryPressure",
    ];

    /// <inheritdoc />
    public string ComponentName => "Qavren.Edge.Chat.Onnx";

    /// <inheritdoc />
    public string? ComponentVersion
        => typeof(ChatDiagnosticsContributor).Assembly.GetName().Version?.ToString();

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string?> Describe()
    {
        var preset = registration.Preset;
        var options = registration.Options;
        var profile = environment.Profile;
        var snapshot = monitor.Read();

        var host = Resolve(() => ChatServiceLocator.TryHost(services, registration.Name)?.Describe());
        var plan = Resolve(() => ChatServiceLocator.TryProvisioner(services, registration.Name)?.Plan());
        var statistics = Resolve(() => services.GetService(typeof(IChatRuntimeStatistics)) as IChatRuntimeStatistics);

        var info = host.Value;
        var shape = info?.Shape ?? preset.Shape;
        var budget = info?.Budget;

        var requestedContext = Math.Min(
            options.MaxContextTokens ?? preset.DefaultMaxContextTokens,
            Math.Min(preset.Shape.ContextLength, shape.ContextLength));

        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // The registration this block describes. A key, not a decorated component name.
            ["serviceKey"] = registration.Name,

            // ---- Environment ----
            ["genAiVersion"] = ChatModelHost.GenAiVersion,
            ["genAiManagedAsset"] = Safe(ReadGenAiManagedAsset),
            ["ortVersionInProcess"] = ChatModelHost.OrtVersion,
            ["ortEnvCreatedBeforeGenAi"] = Text(environment.OrtEnvCreatedBeforeGenAi),
            ["ogaHandleOwned"] = Text(environment.OgaHandleOwned),
            ["telemetryDisabled"] = Text(environment.TelemetryDisabled),
            ["runtimeIdentifier"] = profile.RuntimeIdentifier,
            ["abi"] = profile.Abi,
            ["chatSupportedOnThisAbi"] = Safe(() =>
                Text(GenAiRuntimeSupport.IsSupported(profile.RuntimeIdentifier ?? string.Empty, profile.Abi))),
            ["totalMemoryBytes"] = Text(profile.TotalMemoryBytes),
            ["availableMemoryKind"] = profile.AvailableMemoryKind.ToString(),
            ["isLowRamDevice"] = Text(profile.IsLowRamDevice),

            // Plan adjustment 29. Never "0": zero is ORT's "pick for me" sentinel and would read as
            // a declared answer rather than as an absent one.
            ["chatIntraOpNumThreads"] = host.Failure
                ?? ChatSessionOptionsReader.Describe(info?.IntraOpNumThreads),

            // ---- Model ----
            ["presetId"] = preset.Id,
            ["modelId"] = preset.Manifest.ModelId,
            ["displayName"] = preset.DisplayName,
            ["spdxLicense"] = preset.Manifest.SpdxLicense,
            ["huggingFaceRepo"] = preset.Manifest.HuggingFaceRepo,
            ["huggingFaceRevision"] = preset.Manifest.HuggingFaceRevision,
            ["modelDirectory"] = info?.Directory ?? plan.Map(static p => p.Directory),
            ["provisioned"] = plan.Map(static p => Text(p.Provisioned)),
            ["bundleBytes"] = Text(preset.Manifest.TotalSizeBytes),
            ["weightsBytes"] = Text(shape.WeightsBytes),
            ["freeDiskBytes"] = plan.Map(static p => Text(p.FreeDiskBytes)),
            ["modelType"] = shape.ModelType,
            ["configContextLength"] = Text(shape.ContextLength),
            ["vocabSize"] = Text(shape.VocabSize),
            ["numHiddenLayers"] = Text(shape.NumHiddenLayers),
            ["numKeyValueHeads"] = Text(shape.NumKeyValueHeads),
            ["headSize"] = Text(shape.HeadSize),
            ["slidingWindow"] = Text(shape.SlidingWindow),
            ["kvBytesPerToken"] = Text(shape.KvCacheBytesPerToken),
            ["kvBytesPerElement"] = Text(shape.KvCacheBytesPerElement),
            ["providers"] = host.Map(static i => string.Join(", ", i.Backend.Providers)),
            ["chatTemplateSupported"] = host.Map(static i => Text(i.Backend.ChatTemplateSupported)),
            ["promptFormatter"] = host.Map(static i => i.Backend.PromptFormatter),
            ["guidanceEnforced"] = host.Failure
                ?? (info?.Backend.Guidance ?? EdgeGuidanceProbeResult.Unprobed).ToString(),
            ["loaded"] = host.Failure ?? Text(info?.IsLoaded ?? false),
            ["loadMs"] = host.Map(static i =>
                i.LoadDuration.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)),
            ["loadCount"] = host.Map(static i => Text(i.LoadCount)),
            ["leases"] = host.Map(static i => Text(i.ActiveLeases)),

            // ---- Budget ----
            ["budgetVerdict"] = host.Map(static i => i.Budget.Verdict.ToString()),
            ["budgetExplanation"] = host.Map(static i => i.Budget.Explanation),
            ["requestedContextTokens"] = Text(requestedContext),
            ["resolvedContextTokens"] = host.Map(static i => Text(i.ResolvedContextTokens)),
            ["requiredBytes"] = host.Map(static i => Text(i.Budget.RequiredBytes)),
            ["kvCacheBytes"] = host.Map(static i => Text(i.Budget.KvCacheBytes)),
            ["workspaceBytes"] = Text(budget?.WorkspaceBytes ?? options.Memory.WorkspaceBytes),
            ["reserveBytes"] = Text(budget?.ReserveBytes ?? options.Memory.ReserveBytes),
            ["availableBytes"] = Text(budget?.AvailableBytes ?? snapshot.AvailableMemoryBytes),
            ["usableBytes"] = host.Map(static i => Text(i.Budget.UsableBytes)),
            ["usedMeasuredPeak"] = host.Map(static i => Text(i.Budget.UsedMeasuredPeak)),
            ["measuredPeakBytes"] = Text(shape.MeasuredPeakBytes),
            ["measuredOn"] = shape.MeasuredOn,

            // ---- Runtime honesty ----
            // The per-turn counters are written by Task 5.1's ChatStatistics, which is the only
            // thing that can count a turn. They are published as keys from here so the block's shape
            // is the one spec section 14.3 declares whether or not a turn has run - a key that
            // appears only after the first turn is a key nobody can look for - and they read null
            // rather than zero until then, because a zero would claim a turn ran and produced
            // nothing.
            ["turns"] = statistics.Map(static s => Text(s.Turns)),
            ["rejectedTurns"] = statistics.Map(static s => Text(s.RejectedTurns)),
            ["tokensGenerated"] = statistics.Map(static s => Text(s.TokensGenerated)),
            ["tokensPerSecondP50"] = statistics.Map(static s => Rate(s.TokensPerSecondP50)),
            ["tokensPerSecondP95"] = statistics.Map(static s => Rate(s.TokensPerSecondP95)),
            ["lastTokensPerSecond"] = statistics.Map(static s => Rate(s.LastTokensPerSecond)),
            ["lastTtftMs"] = statistics.Map(static s => Rate(s.LastTtftMs)),
            ["lastStopReason"] = statistics.Map(static s => s.LastStopReason),
            ["historyReductions"] = statistics.Map(static s => Text(s.HistoryReductions)),
            ["thermalThrottleEvents"] = statistics.Map(static s => Text(s.ThermalThrottleEvents)),
            ["thermalAbortEvents"] = statistics.Map(static s => Text(s.ThermalAbortEvents)),
            ["terminationEvents"] = statistics.Map(static s => Text(s.TerminationEvents)),
            ["unloadEvents"] = host.Map(static i => Text(i.LoadCount > 1 ? i.LoadCount - 1 : 0)),

            // Advisory, and labelled so in the README: Environment.WorkingSet is unreliable on some
            // platforms and is not what jetsam measures.
            ["processWorkingSetBytes"] = Safe(() => Text(Environment.WorkingSet)),
            ["peakWorkingSetBytes"] = Safe(ReadPeakWorkingSet),

            // Read straight from the monitor, so both blocks agree.
            ["thermalState"] = snapshot.Thermal.ToString(),
            ["thermalHeadroom"] = snapshot.ThermalHeadroom?.ToString(CultureInfo.InvariantCulture),
            ["isLowPowerMode"] = Text(snapshot.IsLowPowerMode),
            ["lastMemoryPressure"] = snapshot.LastPressure?.ToString(),
        };
    }

    /// <summary>
    /// Plan adjustment 2: the <c>TargetFrameworkAttribute</c> section 14.3 names does not exist.
    /// </summary>
    /// <returns><c>"&lt;mvid&gt; (System.Runtime/&lt;v&gt;[, android])"</c>.</returns>
    /// <remarks>
    /// None of the five <c>lib/</c> assets carries a <c>TargetFrameworkAttribute</c> or an
    /// <c>AssemblyInformationalVersionAttribute</c>, and all five report <c>Version=0.0.0.0</c>.
    /// Worse, the iOS and Mac Catalyst assets have byte-different bodies with <i>identical</i>
    /// referenced-assembly sets, so references alone cannot separate the two this key exists to
    /// distinguish. The MVID can, and the five are listed in <c>chat/README.md</c> so a reader can
    /// look one up.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification =
            "Assembly.GetReferencedAssemblies is the ONLY way to read the System.Runtime reference " +
            "that separates the five GenAI managed assets, because none of them carries a " +
            "TargetFrameworkAttribute (plan adjustment 2). Under trimming the reference set may be " +
            "smaller, which degrades this ONE diagnostics string - the whole read is inside Safe() " +
            "and no behaviour depends on it. Recorded in ADR 0009 with this call site.")]
    internal static string ReadGenAiManagedAsset()
    {
        var assembly = typeof(Microsoft.ML.OnnxRuntimeGenAI.OgaHandle).Assembly;
        var referenced = assembly.GetReferencedAssemblies();

        var systemRuntime = referenced
            .FirstOrDefault(a => string.Equals(a.Name, "System.Runtime", StringComparison.Ordinal))
            ?.Version?.ToString() ?? "(none)";

        var android = referenced.Any(a =>
            a.Name is { } name && name.Contains("Android", StringComparison.Ordinal));

        return $"{assembly.ManifestModule.ModuleVersionId} (System.Runtime/{systemRuntime}" +
            $"{(android ? ", android" : string.Empty)})";
    }

    private static string ReadPeakWorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        return Text(process.PeakWorkingSet64);
    }

    /// <summary>
    /// One lazily-resolved dependency: the value, or the sentinel its resolution failed with.
    /// </summary>
    /// <typeparam name="T">The resolved type.</typeparam>
    /// <param name="Value">The resolved value, or null when nothing is registered.</param>
    /// <param name="Failure">
    /// <c>"(unavailable: &lt;TypeName&gt;)"</c> when the resolution threw. Null is reserved for
    /// "nothing registered", and a report reader has to be able to tell those two apart.
    /// </param>
    private readonly record struct Resolved<T>(T? Value, string? Failure)
        where T : class
    {
        /// <summary>Projects the value, propagating a resolution failure to every key taken through it.</summary>
        /// <param name="read">The projection.</param>
        /// <returns>The sentinel, the projected value, or null when nothing is registered.</returns>
        public string? Map(Func<T, string?> read)
        {
            if (Failure is { } failure)
            {
                return failure;
            }

            // Copied to a local first: a lambda inside a struct cannot capture `this`.
            var value = Value;
            return value is null ? null : Safe(() => read(value));
        }
    }

    private static Resolved<T> Resolve<T>(Func<T?> resolve)
        where T : class
    {
        try
        {
            return new Resolved<T>(resolve(), null);
        }
#pragma warning disable CA1031 // A diagnostics report must never be the thing that throws.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new Resolved<T>(null, Unavailable(ex));
        }
    }

    private static string? Safe(Func<string?> read)
    {
        try
        {
            return read();
        }
#pragma warning disable CA1031 // A diagnostics report must never be the thing that throws.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Unavailable(ex);
        }
    }

    private static string Unavailable(Exception ex) => $"(unavailable: {ex.GetType().Name})";

    private static string Text(bool value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Text(bool? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Text(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Text(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Rate(double? value) => value?.ToString("F2", CultureInfo.InvariantCulture);
}

/// <summary>
/// The per-turn counters spec section 14.3's "runtime honesty" block publishes.
/// </summary>
/// <remarks>
/// Declared here and implemented by Task 5.1's <c>ChatStatistics</c>: the contributor owns the key
/// names and the sentinel rules, and the client owns the counting. Until a client is registered the
/// service is absent and every counter reads null, which is the honest answer - a zero would claim
/// a turn has run and returned nothing.
/// </remarks>
internal interface IChatRuntimeStatistics
{
    /// <summary>Turns started.</summary>
    int Turns { get; }

    /// <summary>Turns refused - busy, thermal, or an unsupported option.</summary>
    int RejectedTurns { get; }

    /// <summary>Tokens generated across every turn.</summary>
    long TokensGenerated { get; }

    /// <summary>The median decode rate over the bounded sample ring.</summary>
    double? TokensPerSecondP50 { get; }

    /// <summary>The 95th-percentile decode rate over the bounded sample ring.</summary>
    double? TokensPerSecondP95 { get; }

    /// <summary>The last turn's decode rate.</summary>
    double? LastTokensPerSecond { get; }

    /// <summary>The last turn's time to first token, in milliseconds.</summary>
    double? LastTtftMs { get; }

    /// <summary>The last turn's stop reason.</summary>
    string? LastStopReason { get; }

    /// <summary>Turns whose history was reduced.</summary>
    int HistoryReductions { get; }

    /// <summary>Turns paced by thermal pressure.</summary>
    int ThermalThrottleEvents { get; }

    /// <summary>Turns aborted by thermal pressure.</summary>
    int ThermalAbortEvents { get; }

    /// <summary>Cooperative terminations.</summary>
    int TerminationEvents { get; }
}
