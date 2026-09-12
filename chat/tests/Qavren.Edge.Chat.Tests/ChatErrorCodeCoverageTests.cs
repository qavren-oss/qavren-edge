using Microsoft.Extensions.AI;
using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Chat.Tests.Fakes;
using Qavren.Edge.Chat.Tests.Fixtures;
using Qavren.Edge.Onnx;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Plan adjustment 23: every 7000-7199 <c>EdgeErrorCode</c> is raised by a minimal reproduction
/// here, and the key-set guard turns a code added to the enum later red rather than letting it pass
/// silently. Each entry is the same reproduction an earlier test in this project already drives,
/// not a second way of raising the code. The <c>foundation/docs/errors.md</c> anchor test is a
/// different fact below: it reads a file, not a throw, so it covers all 24 codes.
/// </summary>
public sealed class ChatErrorCodeCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "qedge-chat-tests", Guid.NewGuid().ToString("N"));

    private static ChatPreset Preset => ChatPresets.Llama32_1BInstructInt4;

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

    // ---- the host, as LoadPathTests builds it ----------------------------------------------------------

    private ChatModelHost Host(
        string? configJson,
        long? availableMemory = 10_000_000_000L,
        EdgeChatDeviceProfile? profile = null,
        ChatModelFactory? factory = null,
        bool provisioned = true)
    {
        var modelDirectory = Path.Combine(_root, Preset.Id);
        Directory.CreateDirectory(modelDirectory);

        if (configJson is not null)
        {
            File.WriteAllText(Path.Combine(modelDirectory, "genai_config.json"), configJson);
        }

        var options = new EdgeChatOptions { Preset = Preset };
        if (provisioned)
        {
            options.ModelDirectoryOverride = modelDirectory;
        }

        var logger = new RecordingLogger();
        var provisioner = new ChatModelProvisioner(
            Preset, options, new FakeModelStore(modelDirectory), new FakeModelPaths(_root), logger, _ => long.MaxValue);

        var environment = new ChatEnvironmentState();
        environment.MarkStarted(
            profile ?? StubDeviceProfile.Desktop(32L * 1024 * 1024 * 1024),
            ortEnvCreatedBeforeGenAi: true,
            runtimeHandle: null,
            telemetryDisabled: true);

        return new ChatModelHost(
            new ChatRegistration(null, Preset, options),
            new NoOpEdgeHost(),
            provisioner,
            new StubResourceMonitor(availableMemory),
            environment,
            logger,
            factory ?? ((_, _, _) => throw new InvalidOperationException("the model factory was reached")));
    }

    private ChatModelProvisioner Provisioner(long? freeDisk, Action<EdgeChatOptions>? configure = null)
    {
        var options = new EdgeChatOptions { Preset = Preset };
        configure?.Invoke(options);

        var paths = new FakeModelPaths(_root);
        var store = new FakeModelStore(ChatModelProvisioner.DirectoryFor(paths.Models, Preset.Manifest));
        return new ChatModelProvisioner(Preset, options, store, paths, new RecordingLogger(), _ => freeDisk);
    }

    private static Task<ChatResponse> TurnAsync(ClientHarness harness, ChatOptions? options = null, ChatMessage[]? messages = null) =>
        harness.Client.GetResponseAsync(
            messages ?? [new ChatMessage(ChatRole.User, "hi")],
            options,
            TestContext.Current.CancellationToken);

    private IReadOnlyDictionary<EdgeErrorCode, Func<Task>> Reproductions() =>
        new Dictionary<EdgeErrorCode, Func<Task>>
        {
            [EdgeErrorCode.ChatEnvironmentNotStarted] = () =>
            {
                new ChatEnvironmentState().ThrowIfNotStarted();
                return Task.CompletedTask;
            },

            [EdgeErrorCode.ChatModelLoadFailed] = async () =>
                await Host(GenAiConfigFixtures.LlamaConfigJson, factory: (_, _, _) => throw new InvalidOperationException("model.onnx.data is truncated"))
                    .PreloadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true),

            [EdgeErrorCode.ChatModelNotRegistered] = () =>
            {
                ChatPresets.ById("no-such-preset");
                return Task.CompletedTask;
            },

            [EdgeErrorCode.ChatUnsupportedRuntime] = () =>
            {
                GenAiRuntimeSupport.ThrowIfUnsupported(
                    StubDeviceProfile.Desktop(16L * 1024 * 1024 * 1024) with { RuntimeIdentifier = "osx-x64" });
                return Task.CompletedTask;
            },

            [EdgeErrorCode.ChatInsufficientMemory] = async () =>
                await Host(GenAiConfigFixtures.LlamaConfigJson, availableMemory: 1_000_000_000L)
                    .PreloadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true),

            [EdgeErrorCode.ChatDeviceTooSmall] = async () =>
                await Host(GenAiConfigFixtures.LlamaConfigJson, profile: StubDeviceProfile.Android(3_000_000_000L))
                    .PreloadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true),

            [EdgeErrorCode.ChatConfigurationInvalid] = async () =>
                await Host(configJson: null).PreloadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true),

            [EdgeErrorCode.ChatModelShapeMismatch] = async () =>
                await Host(GenAiConfigFixtures.LlamaConfigJson.Replace("\"num_hidden_layers\": 16", "\"num_hidden_layers\": 32", StringComparison.Ordinal))
                    .PreloadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true),

            [EdgeErrorCode.ChatExecutionProviderUnsupported] = () =>
            {
                GenAiConfigOverlay.Compose(
                    GenAiConfigFixtures.LlamaConfigJson,
                    """{"model":{"decoder":{"session_options":{"provider_options":[{"cuda":{}}]}}}}""",
                    2048,
                    mobileTargetFramework: true);
                return Task.CompletedTask;
            },

            [EdgeErrorCode.ChatModelNotProvisioned] = async () =>
                await Host(GenAiConfigFixtures.LlamaConfigJson, provisioned: false)
                    .PreloadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true),

            [EdgeErrorCode.ChatInsufficientDiskSpace] = async () =>
                await Provisioner(freeDisk: 1024)
                    .ProvisionAsync(cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true),

            [EdgeErrorCode.ChatDownloadNotPermitted] = async () =>
                await Provisioner(freeDisk: 100L * 1024 * 1024 * 1024, o => o.Provisioning.IsTransferPermitted = () => false)
                    .ProvisionAsync(cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true),

            [EdgeErrorCode.ChatTemplateUnsupported] = () =>
            {
                ChatTemplateProbe.Probe(
                    (_, _) => throw new InvalidOperationException("minja: parse error"),
                    Preset.Id,
                    requireChatTemplate: true,
                    hasConsumerFormatter: false,
                    new RecordingLogger());
                return Task.CompletedTask;
            },

            [EdgeErrorCode.ChatPromptTooLong] = async () =>
            {
                using var harness = ClientHarness.Build(o => { o.MaxOutputTokens = 32; o.ReservedPromptTokens = 0; }, resolvedContext: 128);
                await TurnAsync(harness, messages: [new ChatMessage(ChatRole.User, new string('q', 200))]).ConfigureAwait(true);
            },

            [EdgeErrorCode.ChatGuidanceUnavailable] = async () =>
            {
                using var harness = ClientHarness.Build();
                await TurnAsync(harness, new ChatOptions { ResponseFormat = ChatResponseFormat.Json }).ConfigureAwait(true);
            },

            [EdgeErrorCode.ChatGenerationFailed] = async () =>
            {
                using var harness = ClientHarness.Build();
                harness.Session.OnGeneratorCreated = g => g.ThrowAt = (0, FakeGenerator.NativeFailure("native fault"));
                await TurnAsync(harness).ConfigureAwait(true);
            },

            [EdgeErrorCode.ChatBusy] = async () =>
            {
                using var harness = ClientHarness.Build();
                harness.Host.IsAcceptingTurns = false;
                await TurnAsync(harness).ConfigureAwait(true);
            },

            [EdgeErrorCode.ChatThermalAbort] = async () =>
            {
                using var harness = ClientHarness.Build(thermal: EdgeThermalState.Critical);
                await TurnAsync(harness).ConfigureAwait(true);
            },

            [EdgeErrorCode.ChatToolCallingUnsupported] = async () =>
            {
                using var harness = ClientHarness.Build();
                await TurnAsync(harness, new ChatOptions { ToolMode = ChatToolMode.RequireAny }).ConfigureAwait(true);
            },

            [EdgeErrorCode.ChatOptionUnsupported] = async () =>
            {
                using var harness = ClientHarness.Build();
                await TurnAsync(harness, messages: [new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1 }, "image/png")])]).ConfigureAwait(true);
            },
        };

    public static TheoryData<EdgeErrorCode> Codes()
    {
        var data = new TheoryData<EdgeErrorCode>();
        foreach (var code in Enum.GetValues<EdgeErrorCode>().Where(c => (int)c is >= 7000 and <= 7199))
        {
            data.Add(code);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public async Task EveryChatErrorCodeIsRaisedByItsMinimalReproduction(EdgeErrorCode code)
    {
        var reproductions = Reproductions();
        Assert.True(reproductions.ContainsKey(code), $"{code} has no reproduction in this table.");

        var exception = await Assert
            .ThrowsAsync<EdgeChatException>(async () => await reproductions[code]().ConfigureAwait(true))
            .ConfigureAwait(true);

        Assert.Equal(code, exception.Code);
    }

    [Fact]
    public void TheKeySetIsExactlyTheAssemblysRangeOfTheEnumTwentyCodes()
    {
        var declared = Enum.GetValues<EdgeErrorCode>()
            .Where(c => (int)c is >= 7000 and <= 7199)
            .OrderBy(c => (int)c)
            .ToList();

        var covered = Reproductions().Keys.OrderBy(c => (int)c).ToList();

        Assert.Equal(declared, covered);
        Assert.Equal(20, covered.Count);
    }

    [Fact]
    public void EveryOneOfTheTwentyFourCodesHasAnErrorsMdHeadingWithARemediation()
    {
        var lines = ReadErrorsMd();

        foreach (var code in Enum.GetValues<EdgeErrorCode>().Where(c => (int)c is >= 7000 and <= 7299))
        {
            var number = ((int)code).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var heading = Array.FindIndex(lines, l => l.StartsWith("## " + number, StringComparison.Ordinal));
            Assert.True(heading >= 0, $"foundation/docs/errors.md has no '## {number}' heading for {code}.");

            var next = Array.FindIndex(lines, heading + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
            var body = string.Join('\n', lines.Skip(heading + 1).Take((next < 0 ? lines.Length : next) - heading - 1));

            Assert.Contains("Remediation", body, StringComparison.OrdinalIgnoreCase);
            Assert.True(body.Trim().Length > 40, $"The {number} section carries no remediation text.");
        }
    }

    /// <summary>
    /// <c>foundation/docs/errors.md</c>, embedded by the test csproj so every host and device lane
    /// reads the same committed file whatever its output layout.
    /// </summary>
    private static string[] ReadErrorsMd()
    {
        using var stream = typeof(ChatErrorCodeCoverageTests).Assembly.GetManifestResourceStream("errors.md")
            ?? throw new FileNotFoundException("errors.md is not embedded in the test assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
    }
}
