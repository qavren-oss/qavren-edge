using Qavren.Edge.Onnx;

namespace Qavren.Edge.Chat;

/// <summary>Everything the memory gate reads, and the one hook that replaces it.</summary>
public sealed class ChatMemoryBudgetOptions
{
    /// <summary>
    /// Contexts tried in order until one fits. The first entry at or below the requested length is
    /// the starting rung. An explicit ladder rather than repeated halving, so the diagnostics block
    /// reports a value a reader recognises.
    /// </summary>
    public IReadOnlyList<int> ContextLadder { get; set; } = [4096, 3072, 2048, 1536, 1024];

    /// <summary>Below this, refuse rather than degrade further.</summary>
    public int MinContextTokens { get; set; } = 1024;

    /// <summary>
    /// Transient allocation beyond weights and KV: the logits buffer, the sampler copy, ORT arenas,
    /// the tokenizer. Default 192 MiB. An engineering estimate, and the refusal message says so in
    /// the same words sub-project 2's pre-flight already uses.
    /// </summary>
    public long WorkspaceBytes { get; set; } = 192L * 1024 * 1024;

    /// <summary>Headroom left to the rest of the app: UI, images, the SQLite page cache. Default 192 MiB.</summary>
    public long ReserveBytes { get; set; } = 192L * 1024 * 1024;

    /// <summary>
    /// What fraction of a <see cref="EdgeMemoryBudgetKind.SystemWide"/> reading to believe.
    /// Default 0.60. Ignored for <see cref="EdgeMemoryBudgetKind.PerProcess"/>.
    /// </summary>
    public double SystemWideMemoryFraction { get; set; } = 0.60;

    /// <summary>
    /// Refuse outright on a device whose total memory is below this, whatever it claims is free.
    /// Return null to disable the gate for a preset. Applied only when
    /// <see cref="EdgeChatDeviceProfile.TotalMemoryBytes"/> is known <b>and</b>
    /// <see cref="EdgeChatDeviceProfile.TotalMemorySource"/> is a real physical-memory reading -
    /// see spec section 9.3, which skips the floor entirely for
    /// <see cref="EdgeTotalMemorySource.GcMemoryInfo"/>.
    /// <para>
    /// <b>The floor is compared against a number the platforms do not agree on, so it is set
    /// below the nominal figure on purpose.</b> Android's <c>ActivityManager.MemoryInfo.TotalMem</c>
    /// excludes memory the kernel reserved before userspace saw it and reports roughly 5.4-5.7 GiB
    /// on a device marketed as 6 GB; Apple's <c>NSProcessInfo.PhysicalMemory</c> reports the full
    /// nominal 6,442,450,944. A floor of exactly 6 GiB would therefore refuse the default preset on
    /// essentially every nominal-6 GB Android device while admitting every nominal-6 GB iPhone -
    /// the precise opposite of the intended calibration. The defaults are set at about 0.85x
    /// nominal so the same constant means the same device class on both platforms:
    /// </para>
    /// <list type="bullet">
    /// <item>weights over 1 GiB  -&gt; <b>5.0 GiB</b> (a nominal-6 GB device, by either reading)</item>
    /// <item>weights over 200 MiB -&gt; <b>3.4 GiB</b> (a nominal-4 GB device)</item>
    /// <item>weights at or below 200 MiB -&gt; <b>null</b>, no floor at all</item>
    /// </list>
    /// <para>
    /// The third band is not a courtesy. It is what keeps the gate from refusing the tier-2 test
    /// fixture: a ~500 KB random-weight model has no device floor worth enforcing, and a default
    /// Android emulator on a hosted runner reports about 2 GiB of <c>TotalMem</c>, so a blanket
    /// floor would specify the sub-project's highest-value device assertion into a guaranteed 7006.
    /// A floor is a property of the preset's weight class, and below 200 MiB there is no class to
    /// gate.
    /// </para>
    /// <para>
    /// Spec section 19 item 10 is the measurement that calibrates all three numbers against real
    /// <c>TotalMem</c> and <c>PhysicalMemory</c> readings from the device lanes, because until then
    /// the 0.85x is an engineering estimate like every other constant here.
    /// </para>
    /// </summary>
    public Func<ChatModelShape, long?> MinTotalMemoryBytes { get; set; } =
        static shape => shape.WeightsBytes > 1024L * 1024 * 1024 ? 5_368_709_120L   // 5.0 GiB
                      : shape.WeightsBytes > 200L * 1024 * 1024 ? 3_650_722_201L    // 3.4 GiB
                      : null;

    /// <summary>
    /// Take <c>max(arithmetic, ChatModelShape.MeasuredPeakBytes + ReserveBytes)</c> when a
    /// measurement exists. Spec section 9.3 is the normative formula; the reserve is added to the
    /// measured term because a measured peak is the model's resident footprint and owes the rest
    /// of the app no headroom.
    /// </summary>
    public bool PreferMeasuredPeak { get; set; } = true;

    /// <summary>
    /// Default false, and that is sub-project 2's rule restated: a reading nobody can make is never
    /// a refusal. Apple documents <c>os_proc_available_memory()</c> returning 0 for "unknown or
    /// already over", and inverting this would make the feature unusable on desktop. True makes an
    /// unknown reading fatal, for a kiosk build that would rather not boot.
    /// </summary>
    public bool RefuseWhenUnknown { get; set; }

    /// <summary>Replaces the whole computation. The last word belongs to whoever measured the device.</summary>
    public Func<ChatBudgetRequest, ChatMemoryDecision>? Override { get; set; }
}

/// <summary>What the memory gate decided.</summary>
public enum ChatMemoryVerdict
{
    /// <summary>The requested context fits.</summary>
    Allowed,

    /// <summary>A shorter context fits; <see cref="ChatMemoryDecision.ContextTokens"/> is the rung that did.</summary>
    AllowedReduced,

    /// <summary>Not even <see cref="ChatMemoryBudgetOptions.MinContextTokens"/> fits.</summary>
    RefusedInsufficientMemory,

    /// <summary>Total RAM is below the floor for this preset.</summary>
    RefusedDeviceTooSmall,

    /// <summary>The platform reported nothing usable and the gate was skipped.</summary>
    SkippedUnknown,
}

/// <summary>One question for the memory gate.</summary>
/// <param name="PresetId">The preset being loaded.</param>
/// <param name="Shape">Its declared geometry.</param>
/// <param name="RequestedContextTokens">
/// <c>min(options.MaxContextTokens ?? preset.DefaultMaxContextTokens, shape.ContextLength,
/// configDeclaredContextLength)</c> - spec section 9.1 step 4 does the clamping, which is what
/// keeps <c>Qwen3_600MInt4</c>'s declared 40960 from ever reaching a generator.
/// </param>
/// <param name="Resources">One <c>IEdgeResourceMonitor.Read()</c>.</param>
/// <param name="Device">The cached device profile.</param>
public readonly record struct ChatBudgetRequest(
    string PresetId,
    ChatModelShape Shape,
    int RequestedContextTokens,
    EdgeResourceSnapshot Resources,
    EdgeChatDeviceProfile Device);

/// <summary>The gate's answer, with every term it read.</summary>
/// <param name="Verdict">What was decided.</param>
/// <param name="ContextTokens">The context to run at, or 0 on a refusal.</param>
/// <param name="RequiredBytes">What that context needs.</param>
/// <param name="WeightsBytes">The resident-weight term.</param>
/// <param name="KvCacheBytes">The KV term at <paramref name="ContextTokens"/>.</param>
/// <param name="WorkspaceBytes">The workspace term.</param>
/// <param name="ReserveBytes">The reserve term.</param>
/// <param name="AvailableBytes">What the monitor reported, or null when it would not say.</param>
/// <param name="UsableBytes">
/// <paramref name="AvailableBytes"/> after
/// <see cref="ChatMemoryBudgetOptions.SystemWideMemoryFraction"/>, or unchanged on a
/// <see cref="EdgeMemoryBudgetKind.PerProcess"/> reading.
/// </param>
/// <param name="TotalMemoryBytes">The device profile's total, whatever its source.</param>
/// <param name="BudgetKind">How to read <paramref name="AvailableBytes"/>.</param>
/// <param name="UsedMeasuredPeak">
/// Whether <see cref="ChatModelShape.MeasuredPeakBytes"/> plus the reserve was the term that bound,
/// rather than the arithmetic.
/// </param>
/// <param name="Explanation">
/// One sentence naming every term and which one bound. It is what the refusal message says and
/// what the diagnostics block publishes, so a consumer can see why they got 2048 tokens on one
/// phone and 4096 on another without attaching a debugger.
/// </param>
public sealed record ChatMemoryDecision(
    ChatMemoryVerdict Verdict,
    int ContextTokens,
    long RequiredBytes,
    long WeightsBytes,
    long KvCacheBytes,
    long WorkspaceBytes,
    long ReserveBytes,
    long? AvailableBytes,
    long? UsableBytes,
    long? TotalMemoryBytes,
    EdgeMemoryBudgetKind BudgetKind,
    bool UsedMeasuredPeak,
    string Explanation);

/// <summary>
/// Pure, static, no ORT, no GenAI, no I/O - so the entire gate is a tier-1 unit test over a
/// hand-written <see cref="EdgeResourceSnapshot"/> with no natives at all.
/// </summary>
public static class ChatMemoryBudget
{
    /// <summary>Applies spec section 9.3's rules, in the order they apply.</summary>
    /// <param name="request">The preset, its geometry, the requested context and the two readings.</param>
    /// <param name="options">The budget's constants.</param>
    /// <returns>The verdict, the context to run at, and every term that was read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static ChatMemoryDecision Resolve(ChatBudgetRequest request, ChatMemoryBudgetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Override is { } replacement)
        {
            return replacement(request);
        }

        var shape = request.Shape;
        var device = request.Device;
        var kind = device.AvailableMemoryKind;
        var requested = request.RequestedContextTokens;

        // Rule 1. The device floor, applied only to a real physical reading. A nominal-4 GB phone
        // that transiently reports 2.5 GiB free passes an availability check and is still killed;
        // total memory is the gate that catches it. It is SKIPPED for GcMemoryInfo, because a
        // container limit is not a device class.
        var floor = options.MinTotalMemoryBytes?.Invoke(shape);
        var floorApplies = device.TotalMemorySource
            is EdgeTotalMemorySource.AndroidActivityManager
            or EdgeTotalMemorySource.ApplePhysicalMemory;

        if (floorApplies && device.TotalMemoryBytes is { } totalBytes && floor is { } minimum && totalBytes < minimum)
        {
            return Refused(
                ChatMemoryVerdict.RefusedDeviceTooSmall,
                request,
                options,
                requested,
                available: request.Resources.AvailableMemoryBytes,
                usable: null,

                    $"Device total memory {totalBytes} bytes ({device.TotalMemorySource}) is below the " +
                    $"{minimum}-byte floor this preset's {shape.WeightsBytes} bytes of weights require, " +
                    $"so the device floor bound and no context was tried. Set " +
                    $"ChatMemoryBudgetOptions.MinTotalMemoryBytes to null to try anyway, at the risk " +
                    $"of an OS kill.");
        }

        // Rule 2. A hard refusal only: Android documents IsLowRamDevice as "1GB or less of RAM",
        // so it is false on every phone that is still far too small for a 1.24 GB model.
        if (device.IsLowRamDevice == true)
        {
            return Refused(
                ChatMemoryVerdict.RefusedDeviceTooSmall,
                request,
                options,
                requested,
                available: request.Resources.AvailableMemoryBytes,
                usable: null,

                    $"The platform reports IsLowRamDevice = true, which bound before any context was " +
                    $"tried; the preset's {shape.WeightsBytes} bytes of weights cannot be hosted on a " +
                    $"device the OS itself classifies that way.");
        }

        // Rule 3. A reading nobody can make is never a refusal - sub-project 2's rule, and Apple's
        // own os_proc_available_memory() contract.
        var available = request.Resources.AvailableMemoryBytes;
        if (available is not > 0)
        {
            var terms = Terms(shape, requested, options);

            if (options.RefuseWhenUnknown)
            {
                return new ChatMemoryDecision(
                    ChatMemoryVerdict.RefusedInsufficientMemory,
                    0,
                    terms.Required,
                    shape.WeightsBytes,
                    terms.KvCacheBytes,
                    options.WorkspaceBytes,
                    options.ReserveBytes,
                    available,
                    null,
                    device.TotalMemoryBytes,
                    kind,
                    terms.UsedMeasuredPeak,

                        $"The platform reported no usable available-memory figure and " +
                        $"RefuseWhenUnknown is set, so the unknown reading bound: " +
                        $"{requested} tokens would have needed {terms.Required} bytes " +
                        $"(weights {shape.WeightsBytes} + KV {terms.KvCacheBytes} + workspace " +
                        $"{options.WorkspaceBytes} + reserve {options.ReserveBytes}).");
            }

            return new ChatMemoryDecision(
                ChatMemoryVerdict.SkippedUnknown,
                requested,
                terms.Required,
                shape.WeightsBytes,
                terms.KvCacheBytes,
                options.WorkspaceBytes,
                options.ReserveBytes,
                available,
                null,
                device.TotalMemoryBytes,
                kind,
                terms.UsedMeasuredPeak,

                    $"The platform reported no usable available-memory figure, so nothing bound and " +
                    $"the gate was skipped: proceeding at {requested} tokens, which needs " +
                    $"{terms.Required} bytes (weights {shape.WeightsBytes} + KV " +
                    $"{terms.KvCacheBytes} + workspace {options.WorkspaceBytes} + reserve " +
                    $"{options.ReserveBytes}). Every constant here is an engineering estimate.");
        }

        var usable = kind == EdgeMemoryBudgetKind.SystemWide
            ? (long)(available.Value * options.SystemWideMemoryFraction)
            : available.Value;

        // Rule 4. Walk the ladder from the first rung at or below the requested length. An
        // off-ladder request that is still at or above MinContextTokens is evaluated on its own,
        // so a consumer who asks for 2500 tokens is not silently given nothing to try.
        var candidates = Candidates(options, requested);

        (long Required, long KvCacheBytes, bool UsedMeasuredPeak) smallest = default;
        var smallestContext = 0;

        foreach (var context in candidates)
        {
            var terms = Terms(shape, context, options);
            smallest = terms;
            smallestContext = context;

            if (terms.Required > usable)
            {
                continue;
            }

            var verdict = context >= requested ? ChatMemoryVerdict.Allowed : ChatMemoryVerdict.AllowedReduced;
            var bound = verdict == ChatMemoryVerdict.Allowed
                ? "the requested context fits"
                : $"the requested {requested} tokens did not fit and {context} did";

            return new ChatMemoryDecision(
                verdict,
                context,
                terms.Required,
                shape.WeightsBytes,
                terms.KvCacheBytes,
                options.WorkspaceBytes,
                options.ReserveBytes,
                available,
                usable,
                device.TotalMemoryBytes,
                kind,
                terms.UsedMeasuredPeak,

                    $"{context} tokens need {terms.Required} bytes ({RequiredTerms(shape, terms, options)}) " +
                    $"against {available.Value} available ({kind}) = {usable} usable, so {bound}. " +
                    $"Every constant here is an engineering estimate.");
        }

        if (smallestContext == 0)
        {
            // Nothing at or above MinContextTokens was even a candidate.
            smallest = Terms(shape, options.MinContextTokens, options);
            smallestContext = options.MinContextTokens;
        }

        return new ChatMemoryDecision(
            ChatMemoryVerdict.RefusedInsufficientMemory,
            0,
            smallest.Required,
            shape.WeightsBytes,
            smallest.KvCacheBytes,
            options.WorkspaceBytes,
            options.ReserveBytes,
            available,
            usable,
            device.TotalMemoryBytes,
            kind,
            smallest.UsedMeasuredPeak,

                $"No rung at or above {options.MinContextTokens} tokens fits: even {smallestContext} " +
                $"tokens need {smallest.Required} bytes ({RequiredTerms(shape, smallest, options)}) " +
                $"against {available.Value} available ({kind}) = {usable} usable, so available memory " +
                $"bound. The ladder is a KV lever only - for a weight-dominated preset the lever is a " +
                $"smaller preset, not a shorter context. Every constant here is an engineering estimate.");
    }

    /// <summary>weights + KV(context) + workspace + reserve, before any measured-peak substitution.</summary>
    /// <param name="shape">The model's geometry.</param>
    /// <param name="contextTokens">The context to size for.</param>
    /// <param name="options">The budget's constants.</param>
    /// <returns>The arithmetic total in bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> or <paramref name="options"/> is null.</exception>
    public static long RequiredBytes(ChatModelShape shape, int contextTokens, ChatMemoryBudgetOptions options)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(options);

        return shape.WeightsBytes + shape.KvCacheBytes(contextTokens) + options.WorkspaceBytes + options.ReserveBytes;
    }

    private static (long Required, long KvCacheBytes, bool UsedMeasuredPeak) Terms(
        ChatModelShape shape,
        int contextTokens,
        ChatMemoryBudgetOptions options)
    {
        var kv = shape.KvCacheBytes(contextTokens);
        var arithmetic = shape.WeightsBytes + kv + options.WorkspaceBytes + options.ReserveBytes;

        if (options.PreferMeasuredPeak && shape.MeasuredPeakBytes is { } measured)
        {
            var measuredTerm = measured + options.ReserveBytes;
            if (measuredTerm > arithmetic)
            {
                return (measuredTerm, kv, true);
            }
        }

        return (arithmetic, kv, false);
    }

    private static List<int> Candidates(ChatMemoryBudgetOptions options, int requested)
    {
        var candidates = new List<int>();

        foreach (var rung in options.ContextLadder)
        {
            if (rung <= requested && rung >= options.MinContextTokens)
            {
                candidates.Add(rung);
            }
        }

        // An off-ladder request - 2500 tokens against the default ladder's 2048 - already has rungs
        // below it, so this only fires when the ladder has nothing at or below the request at all.
        if (candidates.Count == 0 && requested >= options.MinContextTokens)
        {
            candidates.Add(requested);
        }

        return candidates;
    }

    private static ChatMemoryDecision Refused(
        ChatMemoryVerdict verdict,
        ChatBudgetRequest request,
        ChatMemoryBudgetOptions options,
        int requested,
        long? available,
        long? usable,
        string explanation)
    {
        var terms = Terms(request.Shape, requested, options);

        return new ChatMemoryDecision(
            verdict,
            0,
            terms.Required,
            request.Shape.WeightsBytes,
            terms.KvCacheBytes,
            options.WorkspaceBytes,
            options.ReserveBytes,
            available,
            usable,
            request.Device.TotalMemoryBytes,
            request.Device.AvailableMemoryKind,
            terms.UsedMeasuredPeak,
            explanation);
    }

    private static string RequiredTerms(
        ChatModelShape shape,
        (long Required, long KvCacheBytes, bool UsedMeasuredPeak) terms,
        ChatMemoryBudgetOptions options)
        => terms.UsedMeasuredPeak
            ?
                $"measured peak {shape.MeasuredPeakBytes} + reserve {options.ReserveBytes}, which " +
                $"exceeded weights {shape.WeightsBytes} + KV {terms.KvCacheBytes} + workspace " +
                $"{options.WorkspaceBytes} + reserve {options.ReserveBytes}"
            :
                $"weights {shape.WeightsBytes} + KV {terms.KvCacheBytes} + workspace " +
                $"{options.WorkspaceBytes} + reserve {options.ReserveBytes}";
}
