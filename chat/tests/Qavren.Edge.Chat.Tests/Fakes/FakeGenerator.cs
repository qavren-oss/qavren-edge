using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntimeGenAI;
using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Chat.Tests.Fakes;

/// <summary>
/// A scripted generator: each <see cref="GenerateNextToken"/> produces the next fragment of the
/// script, exactly as a real one produces one token. No natives, no model.
/// </summary>
/// <remarks>
/// Tokens the fake hands back are fragment ids that <see cref="FakeChatSession"/>'s stream decodes
/// to the scripted text, so a stop sequence can be split across "tokens" any way a test likes -
/// which is the upstream bug this design exists to fix, made assertable.
/// </remarks>
internal sealed class FakeGenerator : IChatGenerator
{
    private readonly FakeChatSession _session;
    private readonly string[] _script;
    private int _position;
    private int _appended;
    private int _last = -1;

    public FakeGenerator(FakeChatSession session, string[] script)
    {
        _session = session;
        _script = script;
    }

    /// <summary>Every <c>AppendTokens</c> call, in order.</summary>
    public List<int[]> Appended { get; } = [];

    /// <summary>Every runtime option set, in order.</summary>
    public List<(string Key, string Value)> RuntimeOptions { get; } = [];

    /// <summary>Whether <c>terminate_session</c> was set.</summary>
    public bool Terminated { get; private set; }

    /// <summary>Whether the generator was disposed.</summary>
    public bool Disposed { get; private set; }

    /// <summary>How many tokens were generated.</summary>
    public int Generated => _position;

    /// <summary>Called before each token with the index about to be produced. Thermal flips and throws live here.</summary>
    public Action<FakeGenerator, int>? BeforeGenerate { get; set; }

    /// <summary>Throw this from <see cref="GenerateNextToken"/> at the given index.</summary>
    public (int Index, Exception Exception)? ThrowAt { get; set; }

    public bool IsDone() => Terminated || _position >= _script.Length;

    public void AppendTokens(ReadOnlySpan<int> tokens)
    {
        Appended.Add(tokens.ToArray());
        _appended += tokens.Length;
    }

    public ulong TokenCount() => (ulong)(_appended + _position);

    public void GenerateNextToken()
    {
        if (IsDone())
        {
            throw new InvalidOperationException("GenerateNextToken called on a finished generator.");
        }

        BeforeGenerate?.Invoke(this, _position);

        if (ThrowAt is { } at && at.Index == _position)
        {
            throw at.Exception;
        }

        _last = _session.RegisterFragment(_script[_position]);
        _position++;
    }

    public int LastToken() => _last;

    public void SetRuntimeOption(string key, string value)
    {
        RuntimeOptions.Add((key, value));
        if (key == "terminate_session")
        {
            // Tracks the value like the native flag does: "1" terminates, "0" resumes.
            Terminated = !string.Equals(value, "0", StringComparison.Ordinal);
        }
    }

    public void Dispose() => Disposed = true;

    /// <summary>
    /// A genuine <see cref="OnnxRuntimeGenAIException"/>. Its only constructor is internal, and the
    /// 7104 contract is about that exact type, so the test reaches it by reflection - which a test
    /// project may do and the library never does.
    /// </summary>
    public static OnnxRuntimeGenAIException NativeFailure(string message) =>
        (OnnxRuntimeGenAIException)Activator.CreateInstance(
            typeof(OnnxRuntimeGenAIException),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [message],
            CultureInfo.InvariantCulture)!;
}

/// <summary>
/// A fake model session: character-level tokens, a deterministic template, and scripted
/// generators. Everything the pipeline reaches through the native seam, with no native.
/// </summary>
internal sealed class FakeChatSession : IChatModelSession
{
    private const int FragmentBase = 1 << 20;
    private readonly List<string> _fragments = [];

    /// <summary>The script the next generator takes; the last one is reused when the queue runs dry.</summary>
    public Queue<string[]> Scripts { get; } = new();

    /// <summary>The script used when <see cref="Scripts"/> is empty.</summary>
    public string[] DefaultScript { get; set; } = ["Hello", " world", "."];

    /// <summary>Every generator built, in order.</summary>
    public List<FakeGenerator> Generators { get; } = [];

    /// <summary>The search options each generator was built with, in order.</summary>
    public List<IReadOnlyList<KeyValuePair<string, object>>> SearchOptions { get; } = [];

    /// <summary>The guidance each generator was built with, in order.</summary>
    public List<ChatGuidance?> Guidance { get; } = [];

    /// <summary>Every messages JSON the template rendered.</summary>
    public List<string> TemplateCalls { get; } = [];

    /// <summary>How many times <see cref="Encode"/> ran.</summary>
    public int Encodes { get; private set; }

    /// <summary>Make the template throw, as minja does on a template it cannot parse.</summary>
    public bool TemplateThrows { get; set; }

    /// <summary>Called with each generator as it is built.</summary>
    public Action<FakeGenerator>? OnGeneratorCreated { get; set; }

    /// <summary>The last generator built.</summary>
    public FakeGenerator LastGenerator => Generators[^1];

    /// <summary>The search option a generator was built with, as a double.</summary>
    public static double Number(IReadOnlyList<KeyValuePair<string, object>> options, string key) =>
        (double)Find(options, key);

    /// <summary>The search option a generator was built with, as a bool.</summary>
    public static bool Flag(IReadOnlyList<KeyValuePair<string, object>> options, string key) =>
        (bool)Find(options, key);

    /// <summary>Whether a search option was set at all.</summary>
    public static bool Has(IReadOnlyList<KeyValuePair<string, object>> options, string key) =>
        options.Any(o => o.Key == key);

    private static object Find(IReadOnlyList<KeyValuePair<string, object>> options, string key)
    {
        // Last write wins, exactly as GeneratorParams.SetSearchOption would apply them.
        object? found = null;
        foreach (var (k, v) in options)
        {
            if (k == key)
            {
                found = v;
            }
        }

        return found ?? throw new KeyNotFoundException(key);
    }

    /// <summary>Renders <c>&lt;role&gt;content&lt;/role&gt;\n</c> per message, plus the assistant header.</summary>
    public string ApplyChatTemplate(string messagesJson, bool addGenerationPrompt)
    {
        TemplateCalls.Add(messagesJson);

        if (TemplateThrows)
        {
            throw new InvalidOperationException("minja: unexpected token in template");
        }

        var builder = new StringBuilder();
        using var document = JsonDocument.Parse(messagesJson);
        foreach (var message in document.RootElement.EnumerateArray())
        {
            var role = message.GetProperty("role").GetString();
            var content = message.GetProperty("content").GetString();
            builder.Append('<').Append(role).Append('>').Append(content).Append("</").Append(role).Append(">\n");
        }

        if (addGenerationPrompt)
        {
            builder.Append("<assistant>");
        }

        return builder.ToString();
    }

    /// <summary>One token per character: the code point is the token id.</summary>
    public int[] Encode(string text)
    {
        Encodes++;
        var tokens = new int[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            tokens[i] = text[i];
        }

        return tokens;
    }

    public IChatTokenStream CreateStream() => new Stream(this);

    public IChatGenerator CreateGenerator(
        IReadOnlyList<KeyValuePair<string, object>> searchOptions,
        ChatGuidance? guidance)
    {
        var script = Scripts.Count > 0 ? Scripts.Dequeue() : DefaultScript;
        var generator = new FakeGenerator(this, script);

        Generators.Add(generator);
        SearchOptions.Add(searchOptions);
        Guidance.Add(guidance);
        OnGeneratorCreated?.Invoke(generator);

        return generator;
    }

    internal int RegisterFragment(string fragment)
    {
        _fragments.Add(fragment);
        return FragmentBase + _fragments.Count - 1;
    }

    private sealed class Stream(FakeChatSession session) : IChatTokenStream
    {
        public string Decode(int token) =>
            token >= FragmentBase
                ? session._fragments[token - FragmentBase]
                : ((char)token).ToString();

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// The host as the client sees it, with no model: leases carry null natives - the fake session
/// never touches them - and every flag the lifecycle observer flips is settable.
/// </summary>
internal sealed class FakeTurnHost : IChatTurnHost
{
    private Action<string, string>? _activeGeneration;
    private Action? _dropCache;

    public FakeTurnHost(ChatModelInfo info) => Info = info;

    /// <summary>What <see cref="Describe"/> and every lease report.</summary>
    public ChatModelInfo Info { get; set; }

    /// <summary>Leases currently outstanding.</summary>
    public int ActiveLeases { get; private set; }

    /// <summary>How many leases were ever handed out.</summary>
    public int Acquires { get; private set; }

    /// <summary>Throw this from <see cref="AcquireAsync"/> instead of leasing.</summary>
    public Exception? AcquireFailure { get; set; }

    /// <inheritdoc />
    public bool IsAcceptingTurns { get; set; } = true;

    /// <inheritdoc />
    public bool TerminationRequested { get; set; }

    /// <inheritdoc />
    public bool SuspendRequested { get; set; }

    /// <summary>Whether a live generator is registered right now.</summary>
    public bool HasActiveGeneration => _activeGeneration is not null;

    /// <summary>Whether the client registered its conversation cache.</summary>
    public bool HasConversationCache => _dropCache is not null;

    public ValueTask<ChatModelLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (AcquireFailure is { } failure)
        {
            throw failure;
        }

        Acquires++;
        ActiveLeases++;
        var lease = new ChatModelLease(null!, null!, null!, Info, () => ActiveLeases--);
        return ValueTask.FromResult(lease);
    }

    public ValueTask PreloadAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ChatModelInfo? Describe() => Info;

    public Task<bool> UnloadAsync(CancellationToken cancellationToken = default)
    {
        _dropCache?.Invoke();
        return Task.FromResult(true);
    }

    public void TerminateActiveGeneration()
    {
        TerminationRequested = true;
        _activeGeneration?.Invoke("terminate_session", "1");
    }

    public void SetActiveGeneration(Action<string, string>? setRuntimeOption)
    {
        _activeGeneration = setRuntimeOption;
        if (setRuntimeOption is not null)
        {
            TerminationRequested = false;
        }
    }

    public void SetConversationCache(Action? drop) => _dropCache = drop;

    /// <summary>What the lifecycle observer does on <c>Sleeping</c>, in its order.</summary>
    public void DropConversationCache() => _dropCache?.Invoke();
}

/// <summary>One client over the three fakes, built the way every tier-1 client test needs it.</summary>
internal sealed class ClientHarness : IDisposable
{
    private ClientHarness(
        EdgeChatClient client,
        FakeChatSession session,
        FakeTurnHost host,
        StubResourceMonitor monitor,
        FixedTimeProvider clock,
        RecordingLogger logger,
        EdgeChatOptions options,
        ChatStatistics statistics)
    {
        Client = client;
        Session = session;
        Host = host;
        Monitor = monitor;
        Clock = clock;
        Logger = logger;
        Options = options;
        Statistics = statistics;
    }

    public EdgeChatClient Client { get; }

    public FakeChatSession Session { get; }

    public FakeTurnHost Host { get; }

    public StubResourceMonitor Monitor { get; }

    public FixedTimeProvider Clock { get; }

    public RecordingLogger Logger { get; }

    public EdgeChatOptions Options { get; }

    public ChatStatistics Statistics { get; }

    /// <summary>Builds a client over the fakes.</summary>
    /// <param name="configure">Configures the registration's options.</param>
    /// <param name="preset">The preset; Llama by default.</param>
    /// <param name="resolvedContext">The context the budget "allowed"; the preset default otherwise.</param>
    /// <param name="script">The default completion script.</param>
    /// <param name="thermal">The monitor's thermal state.</param>
    /// <param name="minimumLogLevel">The recorder's floor.</param>
    /// <param name="chatTemplateSupported">What the template probe "found".</param>
    /// <param name="guidance">What the guidance probe "found".</param>
    public static ClientHarness Build(
        Action<EdgeChatOptions>? configure = null,
        ChatPreset? preset = null,
        int? resolvedContext = null,
        string[]? script = null,
        EdgeThermalState thermal = EdgeThermalState.Nominal,
        LogLevel minimumLogLevel = LogLevel.Trace,
        bool chatTemplateSupported = true,
        EdgeGuidanceProbeResult guidance = EdgeGuidanceProbeResult.Unprobed)
    {
        preset ??= ChatPresets.Llama32_1BInstructInt4;

        var options = new EdgeChatOptions { Preset = preset };
        configure?.Invoke(options);
        options.Preset = preset;

        var info = TestInfo.Build(preset) with
        {
            ResolvedContextTokens = resolvedContext ?? preset.DefaultMaxContextTokens,
            IsLoaded = true,
            Backend = TestInfo.Build(preset).Backend with
            {
                ChatTemplateSupported = chatTemplateSupported,
                Guidance = guidance,
            },
        };

        var session = new FakeChatSession();
        if (script is not null)
        {
            session.DefaultScript = script;
        }

        var host = new FakeTurnHost(info);
        var monitor = new StubResourceMonitor(10_000_000_000L, thermal);
        var clock = new FixedTimeProvider();
        var logger = new RecordingLogger { MinimumLevel = minimumLogLevel };
        var statistics = new ChatStatistics();

        var client = new EdgeChatClient(
            new ChatRegistration(null, preset, options),
            host,
            monitor,
            statistics,
            clock,
            logger,
            _ => session);

        return new ClientHarness(client, session, host, monitor, clock, logger, options, statistics);
    }

    public void Dispose() => Client.Dispose();
}
