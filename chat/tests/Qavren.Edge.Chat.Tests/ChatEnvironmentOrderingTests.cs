using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Chat.Tests.Fakes;
using Qavren.Edge.Hosting;
using Qavren.Edge.Onnx;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Spec section 8.1's ordering rule, the composition half.
/// </summary>
/// <remarks>
/// ORT GenAI creates ORT's process-wide <c>OrtEnv</c> from <b>native</b> code on its first call, and
/// sub-project 2's <c>OrtEnv.IsCreated</c> guard is managed and cannot see that. So GenAI going
/// first silently costs sub-project 2 its log id, its severity and its <c>DOrtLoggingFunction</c>
/// bridge, with no error anywhere - which is why the rule is "no GenAI type during composition or
/// before order 400" and why it is asserted rather than hoped for.
/// </remarks>
public class ChatEnvironmentOrderingTests
{
    [Fact]
    public void ResolvingIChatClientTouchesNoGenAiTypeAndLeavesOrtEnvUncreated()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge.AddOnnxChat(ChatPresets.Llama32_1BInstructInt4));

        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IChatClient>();
        Assert.NotNull(client);

        // The whole rule, in one assertion: composition constructed nothing native.
        Assert.False(OrtEnv.IsCreated);
    }

    [Fact]
    public void TheChatOrdersSortAfterSubProject2sOrder200()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge
            .AddOnnxChat(ChatPresets.Llama32_1BInstructInt4)
            .RequireChatModelAtStartup()
            .WarmUpChatAtStartup());

        using var provider = services.BuildServiceProvider();
        var orders = provider.GetServices<IEdgeStartupTask>().Select(t => t.Order).ToList();

        Assert.Contains(EdgeAiStartupOrder.OnnxEnvironment, orders);
        Assert.Contains(EdgeChatStartupOrder.ChatEnvironment, orders);
        Assert.Contains(EdgeChatStartupOrder.ChatModelProvisioning, orders);
        Assert.Contains(EdgeChatStartupOrder.ChatWarmUp, orders);

        Assert.True(EdgeChatStartupOrder.ChatEnvironment > EdgeAiStartupOrder.OnnxEnvironment);
        Assert.True(EdgeChatStartupOrder.ChatModelProvisioning > EdgeChatStartupOrder.ChatEnvironment);
        Assert.True(EdgeChatStartupOrder.ChatWarmUp > EdgeChatStartupOrder.ChatModelProvisioning);
    }

    [Fact]
    public void AddOnnxIsRegisteredExactlyOnceWhenBothPackagesCallIt()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge
            .AddOnnx()
            .AddOnnxChat(ChatPresets.Llama32_1BInstructInt4)
            .AddOnnxChat(ChatPresets.Llama32_1BInstructInt4));

        using var provider = services.BuildServiceProvider();
        var tasks = provider.GetServices<IEdgeStartupTask>().ToList();

        Assert.Single(tasks, t => t.Order == EdgeAiStartupOrder.OnnxEnvironment);
        Assert.Single(tasks, t => t.Order == EdgeChatStartupOrder.ChatEnvironment);
        Assert.Single(provider.GetServices<IEdgeResourceMonitor>());
        Assert.Single(provider.GetServices<IChatModelHost>());
    }

    [Fact]
    public void WarmUpChatAtStartupTwiceRegistersOneTaskAndSuppressesNobodyElses()
    {
        // TryAddEnumerable on an enumerable service dedupes by IMPLEMENTATION TYPE, so it would
        // suppress every OTHER package's task rather than a duplicate of this one. Sub-project 2's
        // order-220 task is the one that would vanish, so it is asserted here by name.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge => edge
            .AddOnnxChat(ChatPresets.Llama32_1BInstructInt4)
            .WarmUpSessionAtStartup("some-embedding-model")
            .WarmUpChatAtStartup()
            .WarmUpChatAtStartup()
            .RequireChatModelAtStartup()
            .RequireChatModelAtStartup());

        using var provider = services.BuildServiceProvider();
        var tasks = provider.GetServices<IEdgeStartupTask>().ToList();

        Assert.Single(tasks, t => t.Order == EdgeChatStartupOrder.ChatWarmUp);
        Assert.Single(tasks, t => t.Order == EdgeChatStartupOrder.ChatModelProvisioning);
        Assert.Single(tasks, t => t.Order == EdgeAiStartupOrder.SessionWarmUp);
    }

    [Fact]
    public async Task AHostWhoseOrder400TaskHasNotRunIs7001NamingAddOnnxChatAndTheContainer()
    {
        // Constructed directly rather than through EnsureStartedAsync, because that is precisely the
        // mistake the code exists to catch.
        var preset = ChatPresets.Llama32_1BInstructInt4;
        var options = new EdgeChatOptions { Preset = preset };
        var root = Path.Combine(Path.GetTempPath(), "qedge-chat-tests", Guid.NewGuid().ToString("N"));
        var logger = new RecordingLogger();
        var paths = new FakeModelPaths(root);

        var host = new ChatModelHost(
            new ChatRegistration(null, preset, options),
            new NoOpEdgeHost(),
            new ChatModelProvisioner(preset, options, new FakeModelStore(root), paths, logger),
            new StubResourceMonitor(10_000_000_000L),
            new ChatEnvironmentState(),
            logger);

        var exception = await Assert.ThrowsAsync<EdgeChatException>(
            () => host.PreloadAsync(TestContext.Current.CancellationToken).AsTask()).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatEnvironmentNotStarted, exception.Code);
        Assert.Contains("AddOnnxChat()", exception.Remediation!, StringComparison.Ordinal);
        Assert.Contains("resolve IChatClient from the container", exception.Remediation!, StringComparison.Ordinal);

        // 7001 lands before anything touches the filesystem, so the root may never have been made.
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TheOrder400TaskSamplesOrtEnvChecksTheRidAndOwnsTheRuntimeHandle()
    {
        // The ONE place in this suite that loads the real GenAI natives. It loads no model.
        var state = new ChatEnvironmentState();
        var logger = new RecordingLogger<ChatEnvironmentStartupTask>();
        var task = new ChatEnvironmentStartupTask(
            new PortableChatDeviceProfileProvider(),
            state,
            Options.Create(new EdgeGenAiOptions()),
            logger);

        Assert.Equal(EdgeChatStartupOrder.ChatEnvironment, task.Order);

        await task.RunAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(state.Started);
        Assert.True(state.OgaHandleOwned);
        Assert.True(state.TelemetryDisabled);
        Assert.True(logger.Logged(EdgeChatEventIds.GenAiRuntimeInitialized));
        Assert.True(logger.Logged(EdgeChatEventIds.GenAiTelemetryDisabled));

        // Creating an OgaHandle leaves OrtEnv.IsCreated FALSE: GenAI creates ORT's environment from
        // native code, where the managed guard cannot see it. That is section 8.1's trap, measured.
        Assert.False(OrtEnv.IsCreated);

        // 903 fires here because nothing ran sub-project 2's order-200 task first, which is exactly
        // what the warning is for.
        Assert.True(logger.Logged(EdgeChatEventIds.GenAiEnvironmentOutOfOrder));
        Assert.False(state.OrtEnvCreatedBeforeGenAi);

        // OgaShutdown runs exactly once, however many observers ask.
        Assert.True(state.ShutdownRuntime());
        Assert.False(state.ShutdownRuntime());
        Assert.Equal(1, state.ShutdownCount);
    }

    [Fact]
    public async Task TheOrder400TaskCanBeToldToOwnNeitherTheHandleNorTheTelemetrySwitch()
    {
        var state = new ChatEnvironmentState();
        var logger = new RecordingLogger<ChatEnvironmentStartupTask>();

        await new ChatEnvironmentStartupTask(
            new PortableChatDeviceProfileProvider(),
            state,
            Options.Create(new EdgeGenAiOptions { OwnRuntimeHandle = false, DisableTelemetry = false }),
            logger).RunAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(state.Started);
        Assert.False(state.OgaHandleOwned);
        Assert.False(state.TelemetryDisabled);
        Assert.False(state.ShutdownRuntime());
    }

    [Fact]
    public async Task AnUnsupportedAbiFaultsStartupRatherThanTheFirstMessage()
    {
        var state = new ChatEnvironmentState();

        var task = new ChatEnvironmentStartupTask(
            new StubDeviceProfile(StubDeviceProfile.Android(StubDeviceProfile.Android8Gb) with
            {
                RuntimeIdentifier = "android-arm",
                Abi = "armeabi-v7a",
            }),
            state,
            Options.Create(new EdgeGenAiOptions()),
            new RecordingLogger<ChatEnvironmentStartupTask>());

        var exception = await Assert
            .ThrowsAsync<EdgeChatException>(() => task.RunAsync(TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatUnsupportedRuntime, exception.Code);

        // The handle is never created for a device that cannot run the natives.
        Assert.False(state.Started);
    }

    [Fact]
    public void TheDefaultsAreTheOnesThatChangeBehaviour()
    {
        var options = new EdgeGenAiOptions();

        // GenAI 0.15.0 made 1DS telemetry opt-OUT, and the AAR merges INTERNET,
        // ACCESS_NETWORK_STATE and a telemetry content provider into every consuming APK.
        Assert.True(options.DisableTelemetry);
        Assert.True(options.OwnRuntimeHandle);

        // Safe since 0.15.0's transparent re-initialisation, proved on this box.
        Assert.True(options.ShutdownOnStopping);
    }
}
