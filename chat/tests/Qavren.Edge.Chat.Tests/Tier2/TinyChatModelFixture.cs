using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Chat.Tests.Fakes;
using Qavren.Edge.Chat.Tests.Fixtures;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Onnx;
using Xunit;

namespace Qavren.Edge.Chat.Tests.Tier2;

/// <summary>
/// The tier-2 collection. Every class in it shares ONE materialised fixture directory and ONE
/// started container, and runs serially: the GenAI C API is not thread safe, the
/// <c>OgaHandle</c> is process-wide, and ORT's <c>OrtEnv</c> is created exactly once per process -
/// which is what makes the ordering assertion in <c>OrderingTests</c> a statement about the first
/// native touch in this process rather than about whichever test happened to run first.
/// </summary>
[CollectionDefinition(Name)]
public sealed class TinyChatModelCollectionDefinition : ICollectionFixture<TinyChatModelFixture>
{
    /// <summary>
    /// The display name. Its prefix is what <see cref="NativesLastCollectionOrderer"/> keys on to
    /// run this collection after every tier-1 class.
    /// </summary>
    public const string Name = NativesLastCollectionOrderer.NativesPrefix + " tier 2 (committed fixture)";
}

/// <summary>
/// Materialises <c>TinyChatModel.g.cs</c>'s base64 into a temp directory <b>once per collection</b>
/// - <c>Model(string)</c> and <c>Config(string)</c> both need a directory - and starts one real
/// <c>AddQavrenEdge(...AddOnnxChat(...))</c> container over it, so the order-200 <c>OrtEnv</c>
/// task and the order-400 GenAI task run exactly once, in order, against real natives.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fixture preset takes the no-floor band by arithmetic, not by a special case.</b>
/// <c>ChatMemoryBudgetOptions.MinTotalMemoryBytes</c> returns null for any preset under 200 MiB of
/// weights and this one is about 400 KB, so a default GitHub-hosted Android AVD's ~2 GiB of
/// <c>TotalMem</c> never turns the highest-value device assertion into a guaranteed 7006.
/// <c>RefuseWhenUnknown</c> stays at its default <c>false</c>, and <c>MinTotalMemoryBytes</c> stays
/// at its default too - the floor is the arithmetic the spec cares about.
/// </para>
/// <para>
/// What the fixture <i>does</i> have to configure is the ladder and the two working-memory terms.
/// The default ladder <c>[4096, 3072, 2048, 1536, 1024]</c> has no rung at or below a 512-token
/// context and the default <c>MinContextTokens</c> of 1024 would refuse the fixture with 7005
/// before a native was touched; and the default 192 MiB workspace plus 192 MiB reserve are
/// engineering estimates for a 1B-parameter decoder, which an emulator reporting a few hundred
/// megabytes of <c>AvailMem</c> would fail on for a 400 KB model. Both are the budget doing its
/// job on a model that is smaller than its constants, and the fixture's options say so rather
/// than special-casing the budget.
/// </para>
/// <para>
/// Tier 2 asserts <b>mechanics only, never text</b> - the weights are random. Two search options
/// make the mechanics deterministic: greedy decoding (<c>do_sample = false</c>), so one platform
/// decodes the same tokens on every run, and <c>min_length</c> at the context length, so the
/// model's end-of-sequence token cannot fire before the OUTPUT cap does and "exactly N tokens" is
/// an assertion rather than a probability. Both are the model's own search options, applied
/// through the documented escape hatch; neither touches <c>max_length</c>, which the budget owns.
/// </para>
/// </remarks>
public sealed class TinyChatModelFixture : IAsyncLifetime
{
    /// <summary>The context the fixture's <c>genai_config.json</c> declares.</summary>
    public const int ContextLength = 512;

    /// <summary>The output cap every tier-2 turn runs under unless a test says otherwise.</summary>
    public const int DefaultMaxOutputTokens = 16;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "qedge-chat-tier2", Guid.NewGuid().ToString("N"));

    private ServiceProvider? _provider;

    /// <summary>Creates the fixture. Nothing native happens until <see cref="InitializeAsync"/>.</summary>
    public TinyChatModelFixture()
    {
        ModelDirectory = Path.Combine(_root, "model");
    }

    /// <summary>
    /// The fixture as a <see cref="ChatPreset"/>: the six files with their real digests, the
    /// geometry the generator wrote into <c>genai_config.json</c> (2 layers, 2 KV heads, head size
    /// 16, vocab 256, context 512), and <c>WeightsBytes</c> as the manifest rule defines it - the
    /// graph plus its external data, and not the tokenizer.
    /// </summary>
    public static ChatPreset Preset { get; } = BuildPreset();

    /// <summary>The materialised model directory.</summary>
    public string ModelDirectory { get; }

    /// <summary>Everything the container logged, every category, thread-safe.</summary>
    internal RecordingLoggerProvider Logs { get; } = new();

    /// <summary>The started container.</summary>
    public IServiceProvider Services =>
        _provider ?? throw new InvalidOperationException("The fixture has not been initialised.");

    /// <summary>What the order-400 task learned - shared by every host this fixture builds.</summary>
    internal ChatEnvironmentState Environment => Services.GetRequiredService<ChatEnvironmentState>();

    /// <summary>The platform's real resource monitor, as <c>AddOnnx</c> registered it.</summary>
    public IEdgeResourceMonitor Monitor => Services.GetRequiredService<IEdgeResourceMonitor>();

    /// <summary>Sub-project 1's diagnostics over the whole container.</summary>
    public IEdgeDiagnostics Diagnostics => Services.GetRequiredService<IEdgeDiagnostics>();

    /// <summary>Sub-project 1's lifecycle hub, for a simulated pressure event.</summary>
    public IEdgeLifecycle Lifecycle => Services.GetRequiredService<IEdgeLifecycle>();

    /// <summary>The container's own model host - the one the lifecycle observer reaches.</summary>
    public IChatModelHost ContainerHost => Services.GetRequiredService<IChatModelHost>();

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        TinyChatModel.Materialise(ModelDirectory);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));

        // AddQavrenEdge registers IEdgePaths with TryAddSingleton, so the temp root goes first.
        services.AddSingleton<IEdgePaths>(new TempEdgePaths(_root));

        services.AddQavrenEdge(edge => edge
            .UseModelPaths(new FakeModelPaths(_root))
            .AddOnnxChat(Preset, Configure));

        _provider = services.BuildServiceProvider();

        // The one EnsureStartedAsync in the collection: order 200 creates OrtEnv, order 400 samples
        // it and creates the OgaHandle. OrderingTests reads the result out of the diagnostics.
        await _provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A memory-mapped model.onnx.data a finalizer has not released yet is not a failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same, the other spelling Windows uses for it.
        }
    }

    /// <summary>The fixture's standard options: the directory, the ladder, the cap, the two search options.</summary>
    /// <param name="options">The options to configure.</param>
    public void Configure(EdgeChatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.ModelDirectoryOverride = ModelDirectory;

        // The default ladder has no rung at or below 512 and MinContextTokens 1024 would be a
        // 7005 before anything native ran. A model smaller than the budget's floor gets a ladder
        // that fits it; the floor itself (MinTotalMemoryBytes) is already null by arithmetic.
        options.Memory.ContextLadder = [ContextLength, 256, 128];
        options.Memory.MinContextTokens = 128;

        // 192 MiB + 192 MiB are the defaults for a 1B decoder. A device lane's emulator can report
        // a few hundred megabytes of AvailMem, and 60% of that would not clear 384 MiB for a
        // 400 KB model - a refusal about the constants, not about the device.
        options.Memory.WorkspaceBytes = 32L * 1024 * 1024;
        options.Memory.ReserveBytes = 32L * 1024 * 1024;

        options.MaxOutputTokens = DefaultMaxOutputTokens;

        // Mechanics only, never text: greedy, and end-of-sequence suppressed until the output cap.
        options.SearchOptions["do_sample"] = false;
        options.SearchOptions["min_length"] = (double)ContextLength;
    }

    /// <summary>
    /// A real <see cref="ChatModelHost"/> over the fixture, built by hand against the container's
    /// environment state and monitor - so a test can vary the options (the preset shape, the
    /// guidance policy, the cache) without a second container and a second <c>OrtEnv</c> race.
    /// </summary>
    /// <param name="configure">Applied after <see cref="Configure"/>.</param>
    /// <param name="preset">The preset; the fixture's own unless a test deliberately breaks it.</param>
    /// <returns>The host and what it logs; dispose it to drop the model.</returns>
    public TierTwoHost NewHost(Action<EdgeChatOptions>? configure = null, ChatPreset? preset = null)
    {
        preset ??= Preset;

        var options = new EdgeChatOptions { Preset = preset };
        Configure(options);
        configure?.Invoke(options);

        var logs = new RecordingLoggerProvider();
        var logger = logs.CreateLogger("tier2");

        var provisioner = new ChatModelProvisioner(
            preset,
            options,
            new FakeModelStore(ModelDirectory),
            new FakeModelPaths(_root),
            logger,
            _ => long.MaxValue);

        var host = new ChatModelHost(
            new ChatRegistration(null, preset, options),
            new NoOpEdgeHost(),
            provisioner,
            Monitor,
            Environment,
            logger);

        return new TierTwoHost(host, preset, options, logs, logger);
    }

    /// <summary>
    /// A real <see cref="EdgeChatClient"/> over a <see cref="NewHost"/>, with the native session
    /// seam wrapped so a test can read the search options each generator was actually built with.
    /// </summary>
    /// <param name="configure">Applied after <see cref="Configure"/>.</param>
    /// <param name="preset">The preset; the fixture's own by default.</param>
    /// <returns>The client, its host, and the recorder; dispose it to release everything.</returns>
    public TierTwoClient NewClient(Action<EdgeChatOptions>? configure = null, ChatPreset? preset = null)
    {
        var host = NewHost(configure, preset);
        var sessions = new RecordingSessionFactory();
        var statistics = new ChatStatistics();

        var client = new EdgeChatClient(
            new ChatRegistration(null, host.Preset, host.Options),
            host.Host,
            Monitor,
            statistics,
            TimeProvider.System,
            host.Logger,
            sessions.Create);

        return new TierTwoClient(client, host, sessions, statistics);
    }

    private static ChatPreset BuildPreset()
    {
        var graph = TinyChatModel.ModelOnnxBytes();
        var data = TinyChatModel.ModelOnnxDataBytes();

        return new ChatPreset
        {
            Id = "tiny-chat-fixture",
            DisplayName = "Tier-2 tiny chat fixture (random weights)",
            Manifest = new OnnxModelManifest
            {
                ModelId = "tiny-chat-fixture",
                GraphFile = TinyChatModel.ModelOnnxFileName,
                SpdxLicense = "MIT",
                Files =
                [
                    Describe(TinyChatModel.ModelOnnxFileName, OnnxModelFileRole.Graph, graph),
                    Describe(TinyChatModel.ModelOnnxDataFileName, OnnxModelFileRole.GraphExternalData, data),
                    Describe(TinyChatModel.GenAiConfigJsonFileName, OnnxModelFileRole.Auxiliary, TinyChatModel.GenAiConfigJsonBytes()),
                    Describe(TinyChatModel.TokenizerJsonFileName, OnnxModelFileRole.Auxiliary, TinyChatModel.TokenizerJsonBytes()),
                    Describe(TinyChatModel.TokenizerConfigJsonFileName, OnnxModelFileRole.Auxiliary, TinyChatModel.TokenizerConfigJsonBytes()),
                    Describe(TinyChatModel.ChatTemplateJinjaFileName, OnnxModelFileRole.Auxiliary, TinyChatModel.ChatTemplateJinjaBytes()),
                ],
            },
            Shape = new ChatModelShape
            {
                ModelType = "llama",
                ContextLength = ContextLength,
                VocabSize = 256,
                NumHiddenLayers = 2,
                NumKeyValueHeads = 2,
                HeadSize = 16,
                SlidingWindow = null,
                DecoderFileName = TinyChatModel.ModelOnnxFileName,
                WeightsBytes = TinyChatModel.ModelOnnxLength + TinyChatModel.ModelOnnxDataLength,
            },
            StopSequences = [],
            DefaultMaxContextTokens = ContextLength,
            DefaultMaxOutputTokens = DefaultMaxOutputTokens,
            DefaultTemperature = 1.0f,
            DefaultTopP = 1.0f,
            DefaultTopK = 50,
        };

        static OnnxModelFile Describe(string name, OnnxModelFileRole role, byte[] bytes) =>
            new(name, role, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}

/// <summary>Sub-project 1's paths, pointed at a temp directory so a run leaves nothing behind.</summary>
/// <param name="root">The root both properties hang off.</param>
internal sealed class TempEdgePaths(string root) : IEdgePaths
{
    /// <inheritdoc />
    public string Data { get; } = root;

    /// <inheritdoc />
    public string Cache { get; } = Path.Combine(root, "cache");
}

/// <summary>A hand-built real host and what it logged.</summary>
public sealed class TierTwoHost : IDisposable
{
    internal TierTwoHost(
        ChatModelHost host,
        ChatPreset preset,
        EdgeChatOptions options,
        RecordingLoggerProvider logs,
        ILogger logger)
    {
        Host = host;
        Preset = preset;
        Options = options;
        Logs = logs;
        Logger = logger;
    }

    /// <summary>The real host over the fixture.</summary>
    internal ChatModelHost Host { get; }

    /// <summary>The preset it loads.</summary>
    public ChatPreset Preset { get; }

    /// <summary>Its options.</summary>
    public EdgeChatOptions Options { get; }

    /// <summary>Everything it logged.</summary>
    internal RecordingLoggerProvider Logs { get; }

    /// <summary>The logger it and any client over it write to.</summary>
    public ILogger Logger { get; }

    /// <inheritdoc />
    public void Dispose() => Host.Dispose();
}

/// <summary>A real client over a <see cref="TierTwoHost"/>, with the native seam recorded.</summary>
public sealed class TierTwoClient : IDisposable
{
    internal TierTwoClient(
        EdgeChatClient client,
        TierTwoHost host,
        RecordingSessionFactory sessions,
        ChatStatistics statistics)
    {
        Client = client;
        Host = host;
        Sessions = sessions;
        Statistics = statistics;
    }

    /// <summary>The client under test.</summary>
    public EdgeChatClient Client { get; }

    /// <summary>The host it borrows the model from.</summary>
    public TierTwoHost Host { get; }

    /// <summary>Every generator the client built, with the search options it was built from.</summary>
    internal RecordingSessionFactory Sessions { get; }

    /// <summary>The client's counters.</summary>
    internal ChatStatistics Statistics { get; }

    /// <summary>Everything the client and its host logged.</summary>
    internal RecordingLoggerProvider Logs => Host.Logs;

    /// <inheritdoc />
    /// <remarks>Client first: it holds the conversation cache's lease, which the host's disposal must not race.</remarks>
    public void Dispose()
    {
        Client.Dispose();
        Host.Dispose();
    }
}

/// <summary>
/// The native seam, recorded: every generator goes to the real <see cref="GenAiChatSession"/>,
/// and the search options it was built from are kept so a test can read <c>max_length</c> from
/// the outside without reaching into the conversation cache. The template's output is kept too,
/// so the conversation-cache seam check (spec 19 item 11) can compare the append path against
/// encoding the whole rendered text.
/// </summary>
/// <remarks>
/// This is how the upstream <c>max_length</c> bug is asserted: with the cache on, the generator a
/// later turn will reuse must be built to the budget's <c>resolvedContextTokens</c> - never to the
/// model's declared <c>context_length</c>, and never to the first turn's own need.
/// </remarks>
internal sealed class RecordingSessionFactory
{
    private readonly Lock _sync = new();
    private readonly List<IReadOnlyList<KeyValuePair<string, object>>> _searchOptions = [];
    private readonly List<ChatGuidance?> _guidance = [];
    private readonly List<string> _templateOutputs = [];
    private IChatModelSession? _lastInner;
    private int _sessions;

    /// <summary>How many sessions were created - one per lease the client took.</summary>
    public int Sessions => Volatile.Read(ref _sessions);

    /// <summary>How many generators were built. A conversation-cache hit builds none.</summary>
    public int GeneratorsBuilt
    {
        get
        {
            lock (_sync)
            {
                return _searchOptions.Count;
            }
        }
    }

    /// <summary>Every rendered prompt the client asked the model's template for, in turn order.</summary>
    public IReadOnlyList<string> TemplateOutputs
    {
        get
        {
            lock (_sync)
            {
                return [.. _templateOutputs];
            }
        }
    }

    /// <summary>The <see cref="ChatSessionFactory"/> to hand the client.</summary>
    /// <param name="lease">The borrowed model.</param>
    /// <returns>A recording session over the real one.</returns>
    public IChatModelSession Create(ChatModelLease lease)
    {
        Interlocked.Increment(ref _sessions);
        var inner = GenAiChatSession.Create(lease);
        lock (_sync)
        {
            _lastInner = inner;
        }

        return new Session(inner, this);
    }

    /// <summary>Encodes with the model's own tokenizer through the most recent session.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The token ids.</returns>
    public int[] Encode(string text)
    {
        IChatModelSession inner;
        lock (_sync)
        {
            inner = _lastInner ?? throw new InvalidOperationException("No session has been created yet.");
        }

        return text.Length == 0 ? [] : inner.Encode(text);
    }

    /// <summary>The <c>max_length</c> the <paramref name="generatorIndex"/>th generator was built with.</summary>
    /// <param name="generatorIndex">Zero-based, in build order.</param>
    /// <returns>The value, as the double <c>SetSearchOption</c> received.</returns>
    public double MaxLength(int generatorIndex) => Number(generatorIndex, GenAiConfigOverlay.MaxLengthKey);

    /// <summary>A numeric search option the <paramref name="generatorIndex"/>th generator was built with.</summary>
    /// <param name="generatorIndex">Zero-based, in build order.</param>
    /// <param name="key">The search option.</param>
    /// <returns>The value; the last write wins, exactly as <c>GeneratorParams</c> applies them.</returns>
    public double Number(int generatorIndex, string key)
    {
        IReadOnlyList<KeyValuePair<string, object>> options;
        lock (_sync)
        {
            options = _searchOptions[generatorIndex];
        }

        object? found = null;
        foreach (var (k, v) in options)
        {
            if (string.Equals(k, key, StringComparison.Ordinal))
            {
                found = v;
            }
        }

        return found is double number
            ? number
            : throw new KeyNotFoundException($"Generator {generatorIndex} was not built with a numeric '{key}'.");
    }

    /// <summary>The guidance the <paramref name="generatorIndex"/>th generator was built with, or null.</summary>
    /// <param name="generatorIndex">Zero-based, in build order.</param>
    /// <returns>The guidance request, or null.</returns>
    public ChatGuidance? Guidance(int generatorIndex)
    {
        lock (_sync)
        {
            return _guidance[generatorIndex];
        }
    }

    private void Record(IReadOnlyList<KeyValuePair<string, object>> searchOptions, ChatGuidance? guidance)
    {
        lock (_sync)
        {
            _searchOptions.Add(searchOptions);
            _guidance.Add(guidance);
        }
    }

    private void RecordTemplate(string rendered)
    {
        lock (_sync)
        {
            _templateOutputs.Add(rendered);
        }
    }

    private sealed class Session(IChatModelSession inner, RecordingSessionFactory owner) : IChatModelSession
    {
        public string ApplyChatTemplate(string messagesJson, bool addGenerationPrompt)
        {
            var rendered = inner.ApplyChatTemplate(messagesJson, addGenerationPrompt);
            owner.RecordTemplate(rendered);
            return rendered;
        }

        public int[] Encode(string text) => inner.Encode(text);

        public IChatTokenStream CreateStream() => inner.CreateStream();

        public IChatGenerator CreateGenerator(
            IReadOnlyList<KeyValuePair<string, object>> searchOptions,
            ChatGuidance? guidance)
        {
            owner.Record(searchOptions, guidance);
            return inner.CreateGenerator(searchOptions, guidance);
        }
    }
}
