using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Chat.Tests.Fakes;
using Qavren.Edge.Lifecycle;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>Spec section 14.2's six events, against a fake host and with no natives.</summary>
public class LifecycleTests
{
    private sealed record Harness(
        ChatLifecycleObserver Observer,
        FakeChatModelHost Host,
        ChatEnvironmentState Environment,
        CountingDisposable Handle,
        ServiceProvider Provider);

    private static Harness Build(Action<EdgeChatOptions>? configure = null, bool ownsHandle = true)
    {
        var preset = ChatPresets.Llama32_1BInstructInt4;
        var options = new EdgeChatOptions { Preset = preset };
        configure?.Invoke(options);

        var host = new FakeChatModelHost();
        var services = new ServiceCollection();
        services.AddSingleton<IChatLifecycleTarget>(host);
        var provider = services.BuildServiceProvider();

        var handle = new CountingDisposable();
        var environment = new ChatEnvironmentState();
        environment.MarkStarted(
            StubDeviceProfile.Desktop(32L * 1024 * 1024 * 1024),
            ortEnvCreatedBeforeGenAi: true,
            runtimeHandle: ownsHandle ? handle : null,
            telemetryDisabled: true);

        var observer = new ChatLifecycleObserver(
            provider,
            environment,
            Options.Create(new EdgeGenAiOptions()),
            new ChatRegistration(null, preset, options),
            new RecordingLogger<ChatLifecycleObserver>());

        return new Harness(observer, host, environment, handle, provider);
    }

    [Fact]
    public async Task LowPressureDoesNothingAtAll()
    {
        // Sub-project 2's observer latches it; sub-project 4 reads the latch.
        var harness = Build();

        await harness.Observer.OnMemoryPressureAsync(EdgeMemoryPressure.Low, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(harness.Host.IsAcceptingTurns);
        Assert.Equal(0, harness.Host.CacheDrops);
        Assert.Equal(0, harness.Host.Unloads);
        Assert.Equal(0, harness.Host.Terminations);

        await harness.Provider.DisposeAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task ModerateDropsTheConversationGeneratorButNotTheModel()
    {
        // Dropping the cached Generator frees the whole KV cache - hundreds of megabytes - without
        // paying a reload. The running turn finishes; new turns get 7105.
        var harness = Build();

        await harness.Observer.OnMemoryPressureAsync(EdgeMemoryPressure.Moderate, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(harness.Host.IsAcceptingTurns);
        Assert.Equal(1, harness.Host.CacheDrops);
        Assert.Equal(0, harness.Host.Unloads);
        Assert.Equal(0, harness.Host.Terminations);

        await harness.Provider.DisposeAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task CriticalTerminatesFirstAndThenUnloads()
    {
        var harness = Build();

        await harness.Observer.OnMemoryPressureAsync(EdgeMemoryPressure.Critical, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(harness.Host.IsAcceptingTurns);
        Assert.Equal(1, harness.Host.Terminations);
        Assert.Equal(1, harness.Host.CacheDrops);
        Assert.Equal(1, harness.Host.Unloads);

        await harness.Provider.DisposeAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task CriticalWithDropOnMemoryPressureFalseStillTerminates()
    {
        // The switch spares the WEIGHTS. It does not make a multi-second prefill run to completion
        // into a kill.
        var harness = Build(o => o.DropOnMemoryPressure = false);

        await harness.Observer.OnMemoryPressureAsync(EdgeMemoryPressure.Critical, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(1, harness.Host.Terminations);
        Assert.Equal(1, harness.Host.CacheDrops);
        Assert.Equal(0, harness.Host.Unloads);

        await harness.Provider.DisposeAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task CriticalIsBoundedByTheTwoSecondDrainBudget()
    {
        // Sub-project 2's number and sub-project 2's reason: the hub awaits its observers, and an
        // iOS memory warning is not a place to block.
        Assert.Equal(TimeSpan.FromSeconds(2), ChatLifecycleObserver.DrainBudget);

        var harness = Build();
        harness.Host.UnloadCompletesImmediately = false;

        var started = Stopwatch.GetTimestamp();
        await harness.Observer.OnMemoryPressureAsync(EdgeMemoryPressure.Critical, TestContext.Current.CancellationToken).ConfigureAwait(true);
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.Equal(1, harness.Host.Unloads);
        Assert.False(harness.Host.UnloadCompletion.Task.IsCompleted);
        Assert.InRange(elapsed, TimeSpan.FromMilliseconds(1500), TimeSpan.FromSeconds(15));

        harness.Host.UnloadCompletion.TrySetResult(true);
        await harness.Provider.DisposeAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task SleepingStopsTheTurnDropsTheCacheAndDoesNotUnloadByDefault()
    {
        var harness = Build();

        Assert.False(new EdgeChatOptions { Preset = ChatPresets.Llama32_1BInstructInt4 }.UnloadOnSleeping);
        Assert.True(new EdgeChatOptions { Preset = ChatPresets.Llama32_1BInstructInt4 }.DropConversationCacheOnSleep);

        await harness.Observer.OnSleepingAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(harness.Host.SuspendRequested);
        Assert.Equal(1, harness.Host.Terminations);
        Assert.Equal(1, harness.Host.CacheDrops);
        Assert.Equal(0, harness.Host.Unloads);

        await harness.Provider.DisposeAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task SleepingUnloadsOnlyWhenTheAppAsksForIt()
    {
        var harness = Build(o => o.UnloadOnSleeping = true);

        await harness.Observer.OnSleepingAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(1, harness.Host.Unloads);

        await harness.Provider.DisposeAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task ResumedAcceptsTurnsAgainAndPreWarmsNothing()
    {
        // A resume that immediately reloads 1.241 GB is a resume that stutters.
        var harness = Build();
        await harness.Observer.OnSleepingAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        harness.Host.AcceptingTurnsChanges.Clear();

        await harness.Observer.OnResumedAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(harness.Host.SuspendRequested);
        Assert.True(harness.Host.IsAcceptingTurns);
        Assert.Equal([true], harness.Host.AcceptingTurnsChanges);
        Assert.Equal(0, harness.Host.Unloads);

        await harness.Provider.DisposeAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task StoppingUnloadsUnconditionallyAndShutsTheRuntimeDownExactlyOnce()
    {
        var harness = Build(o => o.DropOnMemoryPressure = false);

        // Two observers, as two keyed registrations would produce. The Interlocked guard is what
        // makes OgaShutdown happen once rather than twice.
        var second = new ChatLifecycleObserver(
            harness.Provider,
            harness.Environment,
            Options.Create(new EdgeGenAiOptions()),
            new ChatRegistration(null, ChatPresets.Llama32_1BInstructInt4, new EdgeChatOptions
            {
                Preset = ChatPresets.Llama32_1BInstructInt4,
            }),
            new RecordingLogger<ChatLifecycleObserver>());

        await harness.Observer.OnStoppingAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await second.OnStoppingAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(2, harness.Host.Unloads);
        Assert.Equal(1, harness.Handle.Disposals);
        Assert.Equal(1, harness.Environment.ShutdownCount);

        await harness.Provider.DisposeAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task StoppingLeavesTheRuntimeAloneWhenTheAppTurnedThatOff()
    {
        var host = new FakeChatModelHost();
        var services = new ServiceCollection();
        services.AddSingleton<IChatLifecycleTarget>(host);
        using var provider = services.BuildServiceProvider();

        var handle = new CountingDisposable();
        var environment = new ChatEnvironmentState();
        environment.MarkStarted(
            StubDeviceProfile.Desktop(32L * 1024 * 1024 * 1024), true, handle, true);

        var observer = new ChatLifecycleObserver(
            provider,
            environment,
            Options.Create(new EdgeGenAiOptions { ShutdownOnStopping = false }),
            new ChatRegistration(null, ChatPresets.Llama32_1BInstructInt4, new EdgeChatOptions
            {
                Preset = ChatPresets.Llama32_1BInstructInt4,
            }),
            new RecordingLogger<ChatLifecycleObserver>());

        await observer.OnStoppingAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(1, host.Unloads);
        Assert.Equal(0, handle.Disposals);
    }

    [Fact]
    public async Task AnObserverWithNoTargetRegisteredIsNotAThrow()
    {
        // A shutdown race is not a reason to throw out of a lifecycle observer.
        using var provider = new ServiceCollection().BuildServiceProvider();
        var environment = new ChatEnvironmentState();

        var observer = new ChatLifecycleObserver(
            provider,
            environment,
            Options.Create(new EdgeGenAiOptions()),
            new ChatRegistration(null, ChatPresets.Llama32_1BInstructInt4, new EdgeChatOptions
            {
                Preset = ChatPresets.Llama32_1BInstructInt4,
            }),
            new RecordingLogger<ChatLifecycleObserver>());

        await observer.OnMemoryPressureAsync(EdgeMemoryPressure.Critical, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await observer.OnSleepingAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await observer.OnResumedAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await observer.OnStoppingAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
}
