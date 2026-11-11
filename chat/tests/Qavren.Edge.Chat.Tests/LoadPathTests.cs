using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Chat.Tests.Fakes;
using Qavren.Edge.Chat.Tests.Fixtures;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Spec section 9.1's load path, step for step, with <b>no natives and no model</b>: the model
/// factory is an injected seam and every arm here is a refusal that happens before it, or the
/// failure of the factory itself.
/// </summary>
public sealed class LoadPathTests : IDisposable
{
    /// <summary>A desktop reading whose 60% fraction leaves room for the full 4096-token context.</summary>
    private const long PlentyOfMemory = 10_000_000_000L;

    /// <summary>
    /// 2,833,333,334 x 0.60 = 1,700,000,000 usable. Llama needs 1,761,107,258 at 4096 and
    /// 1,727,552,826 at 3072, and fits at 2048 with 1,693,998,394 - so the ladder descends exactly
    /// two rungs. Every one of those figures is weights + KV + 192 MiB workspace + 192 MiB reserve.
    /// </summary>
    private const long MemoryThatFitsOnlyTheTwoThousandRung = 2_833_333_334L;

    /// <summary>600,000,000 usable, below even the 1024-token rung's 1,660,443,962.</summary>
    private const long MemoryThatFitsNothing = 1_000_000_000L;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "qedge-chat-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory a virus scanner still has open is not a test failure.
        }
    }

    private sealed record Harness(
        ChatModelHost Host,
        EdgeChatOptions Options,
        FakeModelStore Store,
        RecordingLogger Logger,
        string ModelDirectory);

    private Harness Build(
        ChatPreset preset,
        string? configJson,
        long? availableMemory = PlentyOfMemory,
        EdgeChatDeviceProfile? profile = null,
        ChatModelFactory? factory = null,
        Action<EdgeChatOptions>? configure = null,
        bool provisioned = true,
        TimeProvider? clock = null)
    {
        var modelDirectory = Path.Combine(_root, preset.Id);
        Directory.CreateDirectory(modelDirectory);

        if (configJson is not null)
        {
            File.WriteAllText(Path.Combine(modelDirectory, "genai_config.json"), configJson);
        }

        var options = new EdgeChatOptions { Preset = preset };
        if (provisioned)
        {
            options.ModelDirectoryOverride = modelDirectory;
        }

        configure?.Invoke(options);

        var logger = new RecordingLogger();
        var paths = new FakeModelPaths(_root);
        var store = new FakeModelStore(modelDirectory) { Provisioned = false };
        var provisioner = new ChatModelProvisioner(
            preset, options, store, paths, logger, _ => long.MaxValue);

        var environment = new ChatEnvironmentState();
        environment.MarkStarted(
            profile ?? StubDeviceProfile.Desktop(32L * 1024 * 1024 * 1024),
            ortEnvCreatedBeforeGenAi: true,
            runtimeHandle: null,
            telemetryDisabled: true);

        var host = new ChatModelHost(
            new ChatRegistration(null, preset, options),
            new NoOpEdgeHost(),
            provisioner,
            new StubResourceMonitor(availableMemory),
            environment,
            logger,
            factory ?? Throwing("the model factory was reached"),
            clock);

        return new Harness(host, options, store, logger, modelDirectory);
    }

    private static ChatModelFactory Throwing(string message) =>
        (_, _, _) => throw new InvalidOperationException(message);

    /// <summary>A factory that records the composed overlay and then refuses to build anything.</summary>
    private static ChatModelFactory Capturing(List<string> overlays) =>
        (_, overlay, _) =>
        {
            overlays.Add(overlay);
            throw new InvalidOperationException("captured");
        };

    private static async Task<EdgeChatException> LoadAsync(Harness harness) =>
        await Assert.ThrowsAsync<EdgeChatException>(
            () => harness.Host.PreloadAsync(TestContext.Current.CancellationToken).AsTask())
            .ConfigureAwait(true);

    [Fact]
    public async Task AnAbsentModelIs7051CarryingTheBundleSizeAndNeverDownloads()
    {
        var harness = Build(ChatPresets.Llama32_1BInstructInt4, GenAiConfigFixtures.LlamaConfigJson, provisioned: false);

        var exception = await LoadAsync(harness).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatModelNotProvisioned, exception.Code);
        Assert.Equal(ChatPresets.Llama32_1BInstructInt4.Manifest.TotalSizeBytes, exception.RequiredBytes);
        Assert.Contains("Plan()", exception.Remediation!, StringComparison.Ordinal);
        Assert.Contains("ProvisionAsync", exception.Remediation!, StringComparison.Ordinal);

        // NEVER a download.
        Assert.Equal(0, harness.Store.Ensures);
    }

    [Fact]
    public async Task AMissingConfigIs7007NamingTheFile()
    {
        var harness = Build(ChatPresets.Llama32_1BInstructInt4, configJson: null);

        var exception = await LoadAsync(harness).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatConfigurationInvalid, exception.Code);
        Assert.Contains("genai_config.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnparseableConfigIs7007NamingTheField()
    {
        var harness = Build(
            ChatPresets.Llama32_1BInstructInt4,
            """{"model":{"type":"llama","context_length":"four thousand"}}""");

        var exception = await LoadAsync(harness).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatConfigurationInvalid, exception.Code);
        Assert.Contains("context_length", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConfigThatDisagreesWithThePresetIs7008NamingTheField()
    {
        var harness = Build(
            ChatPresets.Llama32_1BInstructInt4,
            GenAiConfigFixtures.LlamaConfigJson.Replace("\"num_hidden_layers\": 16", "\"num_hidden_layers\": 32", StringComparison.Ordinal));

        var exception = await LoadAsync(harness).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatModelShapeMismatch, exception.Code);
        Assert.Contains("num_hidden_layers", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeviceBelowThePresetsFloorIs7006()
    {
        var harness = Build(
            ChatPresets.Llama32_1BInstructInt4,
            GenAiConfigFixtures.LlamaConfigJson,
            availableMemory: PlentyOfMemory,
            profile: StubDeviceProfile.Android(3_000_000_000L));

        var exception = await LoadAsync(harness).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatDeviceTooSmall, exception.Code);
        Assert.Equal(3_000_000_000L, exception.TotalMemoryBytes);
        Assert.Contains("MinTotalMemoryBytes", exception.Remediation!, StringComparison.Ordinal);
        Assert.True(harness.Logger.Logged(EdgeChatEventIds.BudgetRefused));
    }

    [Fact]
    public async Task NoRungFittingIs7005CarryingEveryTerm()
    {
        var harness = Build(
            ChatPresets.Llama32_1BInstructInt4,
            GenAiConfigFixtures.LlamaConfigJson,
            availableMemory: MemoryThatFitsNothing);

        var exception = await LoadAsync(harness).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatInsufficientMemory, exception.Code);
        Assert.Equal(MemoryThatFitsNothing, exception.AvailableBytes);
        Assert.Equal(1_660_443_962L, exception.RequiredBytes);
        Assert.Equal(EdgeMemoryBudgetKind.SystemWide, exception.BudgetKind);
        Assert.Equal(4096, exception.RequestedContextTokens);

        // Nothing fitted, so there is no fitting context to name - and naming one would be a lie.
        Assert.Null(exception.FittingContextTokens);

        // The remediation names the smaller preset FIRST, because for a weight-dominated preset the
        // ladder is a 6% lever and a smaller preset is the real one.
        Assert.Contains("SMALLER PRESET", exception.Remediation!, StringComparison.Ordinal);
        Assert.Contains("increased-memory-limit", exception.Remediation!, StringComparison.Ordinal);
        Assert.Contains("engineering estimates", exception.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReducedRungLogs921AndWritesThatRungIntoTheOverlay()
    {
        var overlays = new List<string>();
        var harness = Build(
            ChatPresets.Llama32_1BInstructInt4,
            GenAiConfigFixtures.LlamaConfigJson,
            availableMemory: MemoryThatFitsOnlyTheTwoThousandRung,
            factory: Capturing(overlays));

        var exception = await LoadAsync(harness).ConfigureAwait(true);

        // The budget allowed a shorter context and the load then failed at the factory, which is
        // the seam - so 921 is what proves the ladder descended.
        Assert.Equal(EdgeErrorCode.ChatModelLoadFailed, exception.Code);

        var reduced = Assert.Single(harness.Logger.For(EdgeChatEventIds.BudgetReduced));
        Assert.Contains("2048", reduced.Message, StringComparison.Ordinal);
        Assert.Contains("4096", reduced.Message, StringComparison.Ordinal);

        var overlay = Assert.Single(overlays);
        Assert.Contains("\"max_length\":2048", overlay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownAvailableReadingLogs923AndProceeds()
    {
        // Sub-project 2's rule and Apple's own os_proc_available_memory() contract: a reading
        // nobody can make is never a refusal.
        var overlays = new List<string>();
        var harness = Build(
            ChatPresets.Llama32_1BInstructInt4,
            GenAiConfigFixtures.LlamaConfigJson,
            availableMemory: null,
            factory: Capturing(overlays));

        var exception = await LoadAsync(harness).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatModelLoadFailed, exception.Code);

        var skipped = Assert.Single(harness.Logger.For(EdgeChatEventIds.BudgetUnknown));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, skipped.Level);
        Assert.Contains("\"max_length\":4096", Assert.Single(overlays), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExplicitMaxContextTokensTurnsTheBudgetIntoANamedRefusal()
    {
        // Setting the value is what makes a context that does not fit a REFUSAL rather than a cap,
        // and the refusal carries the largest rung that would have fitted.
        var harness = Build(
            ChatPresets.Llama32_1BInstructInt4,
            GenAiConfigFixtures.LlamaConfigJson,
            availableMemory: MemoryThatFitsOnlyTheTwoThousandRung,
            configure: o => o.MaxContextTokens = 4096);

        var exception = await LoadAsync(harness).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatInsufficientMemory, exception.Code);
        Assert.Equal(4096, exception.RequestedContextTokens);
        Assert.Equal(2048, exception.FittingContextTokens);
    }

    [Fact]
    public async Task QwensRequestedContextIsClampedToItsPresetDefaultAndNeverTo40960()
    {
        // Its declared context_length is 40960, so a generator built with no max_length would
        // allocate 4,480 MiB of KV cache on a phone. That is the jetsam scenario, priced.
        Assert.Equal(40_960, ChatPresets.Qwen3_600MInt4.Shape.ContextLength);

        var overlays = new List<string>();
        var harness = Build(
            ChatPresets.Qwen3_600MInt4,
            GenAiConfigFixtures.QwenConfigJson,
            availableMemory: PlentyOfMemory,
            factory: Capturing(overlays));

        await LoadAsync(harness).ConfigureAwait(true);

        var overlay = Assert.Single(overlays);
        Assert.Contains("\"max_length\":4096", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("40960", overlay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFactoryThatThrowsIs7002PreservingTheInnerMessage()
    {
        var harness = Build(
            ChatPresets.Llama32_1BInstructInt4,
            GenAiConfigFixtures.LlamaConfigJson,
            factory: Throwing("model.onnx.data is truncated"));

        var exception = await LoadAsync(harness).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatModelLoadFailed, exception.Code);
        Assert.Contains(harness.ModelDirectory, exception.Message, StringComparison.Ordinal);

        var innerVisible =
            exception.Message.Contains("model.onnx.data is truncated", StringComparison.Ordinal)
            || exception.InnerException?.Message.Contains("model.onnx.data is truncated", StringComparison.Ordinal) == true;
        Assert.True(innerVisible);
        Assert.True(harness.Logger.Logged(EdgeChatEventIds.ChatModelLoadFailed));
    }

    [Fact]
    public async Task ALoadTimeoutIs7002NamingTheTimeout()
    {
        // There is no load-cancellation flag in ORT GenAI, so the load keeps running and the host
        // says so rather than pretending it aborted one.
        using var release = new ManualResetEventSlim(initialState: false);

        try
        {
            var harness = Build(
                ChatPresets.Llama32_1BInstructInt4,
                GenAiConfigFixtures.LlamaConfigJson,
                configure: o => o.LoadTimeout = TimeSpan.FromMilliseconds(1),
                factory: (_, _, _) =>
                {
                    release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None);
                    throw new InvalidOperationException("released");
                });

            var exception = await LoadAsync(harness).ConfigureAwait(true);

            Assert.Equal(EdgeErrorCode.ChatModelLoadFailed, exception.Code);
            Assert.Contains("LoadTimeout", exception.Message, StringComparison.Ordinal);
            Assert.Contains("SetLoadCancellationFlag", exception.Message, StringComparison.Ordinal);
            Assert.IsType<TimeoutException>(exception.InnerException);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task ARetryAfterALoadTimeoutTakesOverTheLoadStillInFlightRatherThanStartingASecond()
    {
        // The load cannot be stopped, so it was left running - and the retry started ANOTHER one
        // beside it: twice the weights being mapped at once, on the device already too slow to map
        // them once. And a load that needs longer than LoadTimeout could never succeed at all,
        // because every retry began again from nothing.
        using var release = new ManualResetEventSlim(initialState: false);
        var clock = new TimerCountingTimeProvider();
        var calls = 0;

        try
        {
            var harness = Build(
                ChatPresets.Llama32_1BInstructInt4,
                GenAiConfigFixtures.LlamaConfigJson,
                configure: o => o.LoadTimeout = TimeSpan.FromMilliseconds(1),
                clock: clock,
                factory: (_, _, _) =>
                {
                    Interlocked.Increment(ref calls);
                    release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None);
                    throw new InvalidOperationException("the one load there ever was");
                });

            var first = await LoadAsync(harness).ConfigureAwait(true);
            Assert.IsType<TimeoutException>(first.InnerException);
            Assert.Contains("takes it over", first.Message, StringComparison.Ordinal);

            var second = await LoadAsync(harness).ConfigureAwait(true);
            Assert.IsType<TimeoutException>(second.InnerException);

            // Given room to wait, the retry gets the outcome of that same load - here its failure,
            // which is the only outcome a factory with no model on disk can have. The load is let
            // go only once the third caller is waiting on it (its timed wait is the next timer):
            // one that has already failed is, rightly, not taken over.
            harness.Options.LoadTimeout = TimeSpan.FromSeconds(30);
            var timersBefore = clock.Timers;
            var third = harness.Host.PreloadAsync(TestContext.Current.CancellationToken).AsTask();
            Assert.True(clock.WaitForTimers(timersBefore + 1));
            release.Set();

            var outcome = await Assert.ThrowsAsync<EdgeChatException>(() => third).ConfigureAwait(true);
            Assert.Equal(EdgeErrorCode.ChatModelLoadFailed, outcome.Code);
            Assert.Contains("the one load there ever was", outcome.Message, StringComparison.Ordinal);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task ACancelledLoadIsTakenOverByTheNextAcquireToo()
    {
        using var entered = new ManualResetEventSlim(initialState: false);
        using var release = new ManualResetEventSlim(initialState: false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = new TimerCountingTimeProvider();
        var calls = 0;

        try
        {
            var harness = Build(
                ChatPresets.Llama32_1BInstructInt4,
                GenAiConfigFixtures.LlamaConfigJson,
                configure: o => o.LoadTimeout = TimeSpan.FromSeconds(30),
                clock: clock,
                factory: (_, _, _) =>
                {
                    Interlocked.Increment(ref calls);
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None);
                    throw new InvalidOperationException("still the first load");
                });

            var first = harness.Host.PreloadAsync(cts.Token).AsTask();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            await cts.CancelAsync().ConfigureAwait(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first).ConfigureAwait(true);

            // The user who backed out of the chat page and came straight back. The next timer is the
            // second caller's timed wait on the load it took over.
            // Counted from here, not from zero: a cancel that lands before the first caller has
            // started its timed wait leaves that caller with no timer at all.
            var timersBefore = clock.Timers;
            var second = harness.Host.PreloadAsync(TestContext.Current.CancellationToken).AsTask();
            Assert.True(clock.WaitForTimers(timersBefore + 1));
            release.Set();

            var outcome = await Assert.ThrowsAsync<EdgeChatException>(() => second).ConfigureAwait(true);
            Assert.Contains("still the first load", outcome.Message, StringComparison.Ordinal);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task ALoadForADifferentContextWaitsForTheAbandonedOneToEndBeforeItStarts()
    {
        using var release = new ManualResetEventSlim(initialState: false);
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var clock = new TimerCountingTimeProvider();
        var calls = 0;

        try
        {
            var harness = Build(
                ChatPresets.Llama32_1BInstructInt4,
                GenAiConfigFixtures.LlamaConfigJson,
                configure: o => o.LoadTimeout = TimeSpan.FromMilliseconds(1),
                clock: clock,
                factory: (_, overlay, _) =>
                {
                    var call = Interlocked.Increment(ref calls);
                    events.Enqueue($"start {call}");

                    if (call == 1)
                    {
                        release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None);
                    }

                    events.Enqueue($"end {call}");
                    throw new InvalidOperationException($"load {call} was asked for {overlay}");
                });

            await LoadAsync(harness).ConfigureAwait(true);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, TimeSpan.FromSeconds(10)));

            // A different context this time, so a different overlay: the load still running is the
            // wrong shape to take over, and it cannot be stopped either.
            harness.Options.MaxContextTokens = 2048;
            harness.Options.LoadTimeout = TimeSpan.FromSeconds(30);
            var timersBefore = clock.Timers;
            var second = harness.Host.PreloadAsync(TestContext.Current.CancellationToken).AsTask();

            // The next timer is the second caller's timed wait - on the abandoned load ending,
            // not on a load of its own, because it has not started one.
            Assert.True(clock.WaitForTimers(timersBefore + 1));
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.False(second.IsCompleted);

            release.Set();

            var outcome = await Assert.ThrowsAsync<EdgeChatException>(() => second).ConfigureAwait(true);
            Assert.Contains("load 2", outcome.Message, StringComparison.Ordinal);
            Assert.Contains("\"max_length\":2048", outcome.Message, StringComparison.Ordinal);

            // Never two at once: the second starts only after the first has ended.
            Assert.Equal(["start 1", "end 1", "start 2", "end 2"], events.ToArray());
        }
        finally
        {
            release.Set();
        }
    }

    private ChatModelHost HostOver(
        Harness harness,
        StubResourceMonitor monitor,
        FixedTimeProvider clock,
        ChatModelFactory factory) =>
        new(
            new ChatRegistration(null, ChatPresets.Llama32_1BInstructInt4, harness.Options),
            new NoOpEdgeHost(),
            new ChatModelProvisioner(
                ChatPresets.Llama32_1BInstructInt4,
                harness.Options,
                harness.Store,
                new FakeModelPaths(_root),
                harness.Logger,
                _ => long.MaxValue),
            monitor,
            Started(),
            harness.Logger,
            factory,
            clock);

    [Fact]
    public async Task AMemoryPressurePauseEndsOnItsOwnAndTakesTheCriticalLatchWithIt()
    {
        // An iOS memory warning arrives in the FOREGROUND, usually while a model is loading, and is
        // never followed by a Resumed. Resumed used to be the only thing that turned turns back on
        // and the only thing that cleared the monitor's critical latch, so one warning ended chat
        // for the rest of the session.
        var harness = Build(ChatPresets.Llama32_1BInstructInt4, GenAiConfigFixtures.LlamaConfigJson);
        var monitor = new StubResourceMonitor(PlentyOfMemory);
        var clock = new FixedTimeProvider();
        var host = HostOver(harness, monitor, clock, Throwing("the factory was reached"));

        // What the two observers do on MemoryPressure(Critical): sub-project 2's latches the
        // monitor, sub-project 4's pauses this host.
        monitor.SetPressure(Qavren.Edge.Lifecycle.EdgeMemoryPressure.Critical);
        host.PauseTurnsForMemoryPressure();

        Assert.False(host.IsAcceptingTurns);
        var refused = await Assert.ThrowsAsync<EdgeChatException>(
            () => host.PreloadAsync(TestContext.Current.CancellationToken).AsTask()).ConfigureAwait(true);
        Assert.Equal(EdgeErrorCode.ChatBusy, refused.Code);

        clock.Advance(ChatPressureGate.Cooldown - TimeSpan.FromMilliseconds(1));
        Assert.False(host.IsAcceptingTurns);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(host.IsAcceptingTurns);

        // No longer ChatBusy: the load got as far as the model factory, which is where the memory
        // budget - against a fresh reading - had already said yes.
        var loaded = await Assert.ThrowsAsync<EdgeChatException>(
            () => host.PreloadAsync(TestContext.Current.CancellationToken).AsTask()).ConfigureAwait(true);
        Assert.Equal(EdgeErrorCode.ChatModelLoadFailed, loaded.Code);
        Assert.Contains("the factory was reached", loaded.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResumedEndsAPressurePauseAtOnceAndStoppingIsNotAPauseAtAll()
    {
        var harness = Build(ChatPresets.Llama32_1BInstructInt4, GenAiConfigFixtures.LlamaConfigJson);
        var clock = new FixedTimeProvider();
        var host = HostOver(harness, new StubResourceMonitor(PlentyOfMemory), clock, Throwing("unused"));

        host.PauseTurnsForMemoryPressure();
        Assert.False(host.IsAcceptingTurns);

        host.SetAcceptingTurns(true);
        Assert.True(host.IsAcceptingTurns);

        // Stopping. No cool-down turns a stopping app's turns back on.
        host.SetAcceptingTurns(false);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.False(host.IsAcceptingTurns);
    }

    [Fact]
    public async Task ACriticalPressureLatchRefusesToStartALoadRatherThanPretendingItCanAbortOne()
    {
        var harness = Build(ChatPresets.Llama32_1BInstructInt4, GenAiConfigFixtures.LlamaConfigJson);

        // The monitor the harness built is the one the host reads.
        var monitor = new StubResourceMonitor(PlentyOfMemory);
        monitor.SetPressure(Qavren.Edge.Lifecycle.EdgeMemoryPressure.Critical);

        var host = new ChatModelHost(
            new ChatRegistration(null, ChatPresets.Llama32_1BInstructInt4, harness.Options),
            new NoOpEdgeHost(),
            new ChatModelProvisioner(
                ChatPresets.Llama32_1BInstructInt4,
                harness.Options,
                harness.Store,
                new FakeModelPaths(_root),
                harness.Logger,
                _ => long.MaxValue),
            monitor,
            Started(),
            harness.Logger,
            Throwing("the factory must not be reached"));

        var exception = await Assert.ThrowsAsync<EdgeChatException>(
            () => host.PreloadAsync(TestContext.Current.CancellationToken).AsTask()).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatBusy, exception.Code);
        Assert.Contains("SetLoadCancellationFlag", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaxLengthInSearchOptionsIsRefusedAtLoadAndNotOnTheFirstTurn()
    {
        var harness = Build(
            ChatPresets.Llama32_1BInstructInt4,
            GenAiConfigFixtures.LlamaConfigJson,
            configure: o => o.SearchOptions["max_length"] = 8192);

        var exception = await LoadAsync(harness).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatOptionUnsupported, exception.Code);
    }

    private static ChatEnvironmentState Started()
    {
        var environment = new ChatEnvironmentState();
        environment.MarkStarted(
            StubDeviceProfile.Desktop(32L * 1024 * 1024 * 1024),
            ortEnvCreatedBeforeGenAi: true,
            runtimeHandle: null,
            telemetryDisabled: true);
        return environment;
    }
}
