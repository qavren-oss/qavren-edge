using System.Diagnostics;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntimeGenAI;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Chat.Internal;

/// <summary>One <c>AddOnnxChat</c> call: its service key, its preset and its options.</summary>
/// <param name="Name">The service key, or null for the unkeyed registration.</param>
/// <param name="Preset">The preset that registration loads.</param>
/// <param name="Options">That registration's options, already configured.</param>
internal sealed record ChatRegistration(string? Name, ChatPreset Preset, EdgeChatOptions Options);

/// <summary>
/// The three native objects one loaded model owns, disposed in the reverse of the order they were
/// created.
/// </summary>
/// <remarks>
/// A single type rather than three fields so the model factory is one seam: the tests replace it
/// with a lambda that throws or blocks, which is how every load-path failure in spec section 9.1
/// is asserted without a model on disk.
/// </remarks>
internal sealed class GenAiChatModelResources : IDisposable
{
    /// <summary>The configuration the model was built from, overlay already applied.</summary>
    public required Config Config { get; init; }

    /// <summary>The loaded model.</summary>
    public required Model Model { get; init; }

    /// <summary>Its tokenizer.</summary>
    public required Tokenizer Tokenizer { get; init; }

    /// <inheritdoc />
    public void Dispose()
    {
        Tokenizer.Dispose();
        Model.Dispose();
        Config.Dispose();
    }
}

/// <summary>Builds the native objects for one model directory.</summary>
/// <param name="directory">The provisioned directory.</param>
/// <param name="overlayJson">The one composed overlay document.</param>
/// <param name="cancellationToken">
/// Cancellation. Honoured <b>before</b> the load starts and not during it: ORT's
/// <c>SessionOptions.SetLoadCancellationFlag</c> has no GenAI equivalent, so a multi-second model
/// load cannot be aborted once it has begun.
/// </param>
/// <returns>The loaded resources.</returns>
internal delegate GenAiChatModelResources ChatModelFactory(
    string directory,
    string overlayJson,
    CancellationToken cancellationToken);

/// <summary>Spec section 9.1's load path and sub-project 2's drop discipline, line for line.</summary>
internal sealed class ChatModelHost : IChatModelHost, IChatLifecycleTarget, IDisposable
{
    private static readonly Action<ILogger, string, string, double, int, Exception?> s_loaded =
        LoggerMessage.Define<string, string, double, int>(
            LogLevel.Information,
            new EventId(EdgeChatEventIds.ChatModelLoaded, nameof(EdgeChatEventIds.ChatModelLoaded)),
            "Chat model {ModelId} loaded from {Directory} in {ElapsedMs} ms at {ContextTokens} tokens of context.");

    private static readonly Action<ILogger, string, Exception?> s_loadFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(EdgeChatEventIds.ChatModelLoadFailed, nameof(EdgeChatEventIds.ChatModelLoadFailed)),
            "Chat model {ModelId} could not be loaded.");

    private static readonly Action<ILogger, string, Exception?> s_dropped =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(EdgeChatEventIds.ChatModelDropped, nameof(EdgeChatEventIds.ChatModelDropped)),
            "Chat model {ModelId} was disposed.");

    private static readonly Action<ILogger, int, string, Exception?> s_budgetResolved =
        LoggerMessage.Define<int, string>(
            LogLevel.Information,
            new EventId(EdgeChatEventIds.BudgetResolved, nameof(EdgeChatEventIds.BudgetResolved)),
            "Memory budget allowed {ContextTokens} tokens: {Explanation}");

    private static readonly Action<ILogger, int, int, string, Exception?> s_budgetReduced =
        LoggerMessage.Define<int, int, string>(
            LogLevel.Information,
            new EventId(EdgeChatEventIds.BudgetReduced, nameof(EdgeChatEventIds.BudgetReduced)),
            "Memory budget reduced the context from {RequestedContextTokens} to the {ContextTokens}-token " +
            "rung: {Explanation}");

    private static readonly Action<ILogger, string, Exception?> s_budgetRefused =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(EdgeChatEventIds.BudgetRefused, nameof(EdgeChatEventIds.BudgetRefused)),
            "Memory budget refused: {Explanation}");

    private static readonly Action<ILogger, int, string, Exception?> s_budgetUnknown =
        LoggerMessage.Define<int, string>(
            LogLevel.Warning,
            new EventId(EdgeChatEventIds.BudgetUnknown, nameof(EdgeChatEventIds.BudgetUnknown)),
            "The platform reported no usable available-memory figure, so the memory gate was skipped " +
            "and the load proceeds at {ContextTokens} tokens: {Explanation}");

    private readonly ChatRegistration _registration;
    private readonly IEdgeHost _host;
    private readonly ChatModelProvisioner _provisioner;
    private readonly IEdgeResourceMonitor _monitor;
    private readonly ChatEnvironmentState _environment;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly Lock _sync = new();

    // Left null until the first load rather than defaulted in the constructor: building the default
    // delegate would load the GenAI types during composition, and spec section 8.1's rule is that
    // nothing DI builds eagerly names one.
    private ChatModelFactory? _modelFactory;
    private ModelEntry? _current;
    private ChatModelInfo? _lastInfo;
    private int _loadCount;
    private volatile bool _acceptingTurns = true;
    private volatile bool _terminationRequested;
    private volatile bool _suspendRequested;
    private Action<string, string>? _activeGenerationRuntimeOption;
    private Action? _dropConversationCache;

    /// <summary>Creates the host for one registration.</summary>
    /// <param name="registration">The preset and options this host loads.</param>
    /// <param name="host">Sub-project 1's host; every acquire awaits its startup first.</param>
    /// <param name="provisioner">This registration's provisioner.</param>
    /// <param name="monitor">The device resource monitor.</param>
    /// <param name="environment">What the order-400 task learned.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="modelFactory">
    /// Builds the native objects. Null takes the real ORT GenAI one; the tests inject a lambda,
    /// which is how every spec section 9.1 failure is asserted with no model on disk.
    /// </param>
    public ChatModelHost(
        ChatRegistration registration,
        IEdgeHost host,
        ChatModelProvisioner provisioner,
        IEdgeResourceMonitor monitor,
        ChatEnvironmentState environment,
        ILogger logger,
        ChatModelFactory? modelFactory = null)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        _registration = registration;
        _host = host;
        _provisioner = provisioner;
        _monitor = monitor;
        _environment = environment;
        _logger = logger;
        _modelFactory = modelFactory;
    }

    /// <summary>The preset this host loads.</summary>
    public ChatPreset Preset => _registration.Preset;

    /// <summary>This registration's options.</summary>
    public EdgeChatOptions Options => _registration.Options;

    /// <summary>This registration's service key, or null.</summary>
    public string? Name => _registration.Name;

    /// <summary>Whether <see cref="TerminateActiveGeneration"/> has been called since the last turn began.</summary>
    public bool TerminationRequested => _terminationRequested;

    /// <inheritdoc />
    public bool IsAcceptingTurns => _acceptingTurns;

    /// <inheritdoc />
    public async ValueTask<ChatModelLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        // Sub-project 1's contract: a startup failure surfaces here with its real cause rather than
        // as a mysterious missing native two frames deeper.
        await _host.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return await AcquireCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask PreloadAsync(CancellationToken cancellationToken = default)
    {
        var lease = await AcquireCoreAsync(cancellationToken).ConfigureAwait(false);
        lease.Dispose();
    }

    /// <inheritdoc />
    public ChatModelInfo? Describe()
    {
        lock (_sync)
        {
            return _lastInfo;
        }
    }

    /// <inheritdoc />
    public Task<bool> UnloadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_current is not { } entry)
            {
                return Task.FromResult(false);
            }

            entry.DropRequested = true;
            _current = null;

            if (entry.Leases == 0)
            {
                DisposeEntry(entry);
                return Task.FromResult(true);
            }

            // The model survives until the last lease returns: disposing it under a running
            // Generator is a native access violation.
            _lastInfo = entry.Info with { IsLoaded = true, ActiveLeases = entry.Leases };
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public void TerminateActiveGeneration()
    {
        _terminationRequested = true;

        // The documented cooperative abort, and the only thing that can stop a multi-second
        // prefill. Deliberately called outside the turn gate; spec section 20 records that as a
        // stated risk rather than pretending it is free.
        var setRuntimeOption = Volatile.Read(ref _activeGenerationRuntimeOption);
        try
        {
            setRuntimeOption?.Invoke("terminate_session", "1");
        }
        catch (OnnxRuntimeGenAIException)
        {
            // The generator finished between the read and the call. Terminating a turn that has
            // already ended is not a failure worth propagating into a lifecycle observer.
        }
        catch (ObjectDisposedException)
        {
            // Same race, the managed half of it.
        }
    }

    /// <summary>
    /// Registers the live generator's <c>SetRuntimeOption</c>, so
    /// <see cref="TerminateActiveGeneration"/> can reach it. Null clears it.
    /// </summary>
    /// <param name="setRuntimeOption">The live generator's runtime-option setter, or null.</param>
    /// <remarks>The decode loop owns both calls; the host only holds the reference.</remarks>
    public void SetActiveGeneration(Action<string, string>? setRuntimeOption)
    {
        Volatile.Write(ref _activeGenerationRuntimeOption, setRuntimeOption);
        if (setRuntimeOption is not null)
        {
            _terminationRequested = false;
        }
    }

    /// <summary>Registers the conversation cache's drop, so the lifecycle observer can reach it.</summary>
    /// <param name="drop">The cache's drop, or null.</param>
    public void SetConversationCache(Action? drop) => Volatile.Write(ref _dropConversationCache, drop);

    /// <inheritdoc />
    public void DropConversationCache() => Volatile.Read(ref _dropConversationCache)?.Invoke();

    /// <inheritdoc />
    public void SetAcceptingTurns(bool accepting) => _acceptingTurns = accepting;

    /// <inheritdoc />
    public void SetSuspendRequested(bool suspended) => _suspendRequested = suspended;

    /// <summary>
    /// Whether the OS is suspending the app. The decode loop reads it to report
    /// <c>StopReason = Suspended</c> rather than <c>MemoryPressure</c> for the same cooperative
    /// abort.
    /// </summary>
    public bool SuspendRequested => _suspendRequested;

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_current is { } entry)
            {
                _current = null;
                DisposeEntry(entry);
            }
        }

        _loadGate.Dispose();
    }

    /// <summary>
    /// Everything <see cref="AcquireAsync"/> does EXCEPT awaiting <c>IEdgeHost.Started</c>. The
    /// order-420 warm-up task is itself a startup task, so awaiting startup from inside it would
    /// deadlock on the task's own completion.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A lease that must be disposed.</returns>
    internal async ValueTask<ChatModelLease> AcquireCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (TryLease(out var lease))
        {
            return lease;
        }

        // Under the load gate, so N concurrent first callers produce one model rather than N.
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryLease(out lease))
            {
                return lease;
            }

            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async ValueTask<ChatModelLease> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var preset = _registration.Preset;
        var options = _registration.Options;
        var started = Stopwatch.GetTimestamp();

        // 1. The environment, and the runtime. Re-asserted here for a host used without the
        //    startup task, which is exactly what a test does.
        _environment.ThrowIfNotStarted();
        GenAiRuntimeSupport.ThrowIfUnsupported(_environment.Profile);
        GenAiConfigOverlay.ThrowIfSearchOptionsDeclareMaxLength(options.SearchOptions);

        // There is no load-cancellation flag in GenAI, so the mitigation is to refuse to START a
        // load under critical pressure rather than to pretend one can be aborted.
        if (_monitor.LastPressure == EdgeMemoryPressure.Critical)
        {
            throw new EdgeChatException(
                EdgeErrorCode.ChatBusy,
                "The lifecycle hub last reported critical memory pressure, so this host refuses to " +
                "start a model load. ORT GenAI has no equivalent of ORT's " +
                "SessionOptions.SetLoadCancellationFlag, so a load that had started could not be " +
                "aborted - refusing to start one is the whole mitigation.")
            {
                PresetId = preset.Id,
                Remediation = "Retry after the app is resumed, which clears the pressure latch.",
            };
        }

        // 2. Provisioning. Never a download.
        var directory = _provisioner.Directory;
        if (!_provisioner.IsProvisioned)
        {
            throw new EdgeChatException(
                EdgeErrorCode.ChatModelNotProvisioned,
                $"The chat model '{preset.Id}' is not on disk at '{directory}'. Its bundle is " +
                $"{preset.Manifest.TotalSizeBytes} bytes and nothing downloads implicitly.")
            {
                PresetId = preset.Id,
                ModelId = preset.Manifest.ModelId,
                RequiredBytes = preset.Manifest.TotalSizeBytes,
                Remediation =
                    "Call IChatModelProvisioner.Plan() to get the transfer size and the licence, " +
                    "show the user a consent sheet, and then call ProvisionAsync().",
            };
        }

        // 3. Shape, from the provisioned config, then the field-by-field cross-check.
        var configPath = ChatModelProvisioner.FilePath(directory, preset.GenAiConfigFile);
        string configJson;
        try
        {
            configJson = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw ConfigurationInvalid(preset, configPath, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw ConfigurationInvalid(preset, configPath, ex);
        }

        var fromConfig = ChatModelShape.FromGenAiConfig(configJson, preset.Shape.WeightsBytes);
        ChatModelShape.ThrowIfMismatched(preset.Id, preset.Shape, fromConfig);

        // Plan adjustment 29: the only other reader of this text, and it reads the copy already in
        // hand rather than opening the file again.
        var intraOpNumThreads = ChatSessionOptionsReader.ReadIntraOpNumThreads(configJson);

        // 4. The budget.
        var requested = Math.Min(
            options.MaxContextTokens ?? preset.DefaultMaxContextTokens,
            Math.Min(preset.Shape.ContextLength, fromConfig.ContextLength));

        var decision = ChatMemoryBudget.Resolve(
            new ChatBudgetRequest(preset.Id, preset.Shape, requested, _monitor.Read(), _environment.Profile),
            options.Memory);

        var resolvedContext = ApplyBudget(preset, options, requested, decision);

        // 5. Config and model: exactly one Overlay call, with the document composed in managed code.
        var overlay = GenAiConfigOverlay.Compose(
            configJson,
            options.ConfigOverlayJson,
            resolvedContext,
            GenAiConfigOverlay.IsMobileTargetFramework);

        var resources = await CreateResourcesAsync(preset, options, directory, overlay, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // 6. The two probes.
            var template = ChatTemplateProbe.Probe(
                (messagesJson, addGenerationPrompt) =>
                    resources.Tokenizer.ApplyChatTemplate(null!, messagesJson, null!, addGenerationPrompt),
                preset.Id,
                options.RequireChatTemplate,
                options.PromptFormatter is not null,
                _logger);

            var guidance = options.Guidance == EdgeGuidancePolicy.Disabled
                ? EdgeGuidanceProbeResult.Unprobed
                : GuidanceProbe.Probe(
                    (type, data, maxTokens) => RunGuidanceProbe(resources, type, data, maxTokens),
                    preset.Id,
                    _logger);

            var elapsed = Stopwatch.GetElapsedTime(started);

            // 7. Record, hand out the lease, log 910.
            lock (_sync)
            {
                _loadCount++;

                var info = new ChatModelInfo(
                    preset.Id,
                    preset.Manifest.ModelId,
                    directory,
                    ChatModelProvisioner.Sha16(preset.Manifest),
                    fromConfig,
                    decision,
                    resolvedContext,
                    new ChatBackendReport(
                        GenAiVersion,
                        OrtVersion,
                        _environment.Profile.RuntimeIdentifier,
                        _environment.Profile.Abi,
                        ReadProviders(configJson),
                        template.Supported,
                        guidance,
                        template.PromptFormatter),
                    elapsed,
                    DateTimeOffset.UtcNow,
                    _loadCount,
                    ActiveLeases: 1,
                    IsLoaded: true)
                {
                    IntraOpNumThreads = intraOpNumThreads,
                };

                var entry = new ModelEntry(resources, info) { Leases = 1 };
                _current = entry;
                _lastInfo = info;

                s_loaded(
                    _logger,
                    info.ModelId,
                    directory,
                    elapsed.TotalMilliseconds,
                    resolvedContext,
                    null);

                return new ChatModelLease(
                    resources.Model,
                    resources.Tokenizer,
                    resources.Config,
                    info,
                    () => Release(entry));
            }
        }
        catch
        {
            resources.Dispose();
            throw;
        }
    }

    private async ValueTask<GenAiChatModelResources> CreateResourcesAsync(
        ChatPreset preset,
        EdgeChatOptions options,
        string directory,
        string overlay,
        CancellationToken cancellationToken)
    {
        // Cancelled BEFORE the load, which is the only place cancellation can be honoured.
        cancellationToken.ThrowIfCancellationRequested();

        var factory = _modelFactory ??= CreateGenAiResources;
        var load = Task.Run(() => factory(directory, overlay, cancellationToken), CancellationToken.None);

        try
        {
            return await load.WaitAsync(options.LoadTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            // The load itself keeps running - nothing can stop it - so whatever it produces is
            // disposed rather than leaked. That is the honest half of "there is no load
            // cancellation flag".
            DisposeWhenItArrives(load);

            s_loadFailed(_logger, preset.Manifest.ModelId, ex);
            throw new EdgeChatException(
                EdgeErrorCode.ChatModelLoadFailed,
                $"Loading the chat model in '{directory}' did not finish inside the " +
                $"{options.LoadTimeout} EdgeChatOptions.LoadTimeout. ORT GenAI has no equivalent " +
                "of ORT's SessionOptions.SetLoadCancellationFlag, so the load is still running " +
                "and its result will be disposed when it arrives.",
                ex)
            {
                PresetId = preset.Id,
                ModelId = preset.Manifest.ModelId,
                Remediation =
                    "Raise EdgeChatOptions.LoadTimeout, or pick a preset whose weights this device " +
                    "can map faster.",
            };
        }
        catch (OperationCanceledException)
        {
            DisposeWhenItArrives(load);
            throw;
        }
#pragma warning disable CA1031 // Every load failure becomes 7002 with the inner message preserved.
        catch (Exception ex) when (ex is not EdgeChatException)
#pragma warning restore CA1031
        {
            s_loadFailed(_logger, preset.Manifest.ModelId, ex);
            throw new EdgeChatException(
                EdgeErrorCode.ChatModelLoadFailed,
                FormattableString.Invariant(
                    $"ORT GenAI refused to load the chat model in '{directory}': {ex.Message}"),
                ex)
            {
                PresetId = preset.Id,
                ModelId = preset.Manifest.ModelId,
                Remediation =
                    "Re-provision the model folder: a truncated model.onnx.data or a config that " +
                    "names a decoder file that is not there both surface here.",
            };
        }
    }

    private static void DisposeWhenItArrives(Task<GenAiChatModelResources> load) =>
        _ = load.ContinueWith(
            static completed => completed.Result.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);

    private int ApplyBudget(
        ChatPreset preset,
        EdgeChatOptions options,
        int requested,
        ChatMemoryDecision decision)
    {
        switch (decision.Verdict)
        {
            case ChatMemoryVerdict.RefusedDeviceTooSmall:
                s_budgetRefused(_logger, decision.Explanation, null);
                throw new EdgeChatException(EdgeErrorCode.ChatDeviceTooSmall, decision.Explanation)
                {
                    PresetId = preset.Id,
                    ModelId = preset.Manifest.ModelId,
                    RequiredBytes = decision.RequiredBytes,
                    AvailableBytes = decision.AvailableBytes,
                    TotalMemoryBytes = decision.TotalMemoryBytes,
                    BudgetKind = decision.BudgetKind,
                    RequestedContextTokens = requested,
                    Remediation =
                        "Set ChatMemoryBudgetOptions.MinTotalMemoryBytes to null to try anyway, at " +
                        "the risk of an OS kill, or register a preset in a smaller weight class.",
                };

            case ChatMemoryVerdict.RefusedInsufficientMemory:
                s_budgetRefused(_logger, decision.Explanation, null);
                throw InsufficientMemory(preset, requested, decision, fitting: null);

            case ChatMemoryVerdict.SkippedUnknown:
                s_budgetUnknown(_logger, decision.ContextTokens, decision.Explanation, null);
                return decision.ContextTokens;

            case ChatMemoryVerdict.AllowedReduced:
                // Setting MaxContextTokens turns the budget from a cap into a named refusal, which
                // is the point of setting it.
                if (options.MaxContextTokens is not null)
                {
                    s_budgetRefused(_logger, decision.Explanation, null);
                    throw InsufficientMemory(preset, requested, decision, decision.ContextTokens);
                }

                s_budgetReduced(_logger, requested, decision.ContextTokens, decision.Explanation, null);
                return decision.ContextTokens;

            default:
                s_budgetResolved(_logger, decision.ContextTokens, decision.Explanation, null);
                return decision.ContextTokens;
        }
    }

    private static EdgeChatException InsufficientMemory(
        ChatPreset preset,
        int requested,
        ChatMemoryDecision decision,
        int? fitting)
        => new(EdgeErrorCode.ChatInsufficientMemory, decision.Explanation)
        {
            PresetId = preset.Id,
            ModelId = preset.Manifest.ModelId,
            RequiredBytes = decision.RequiredBytes,
            AvailableBytes = decision.AvailableBytes,
            TotalMemoryBytes = decision.TotalMemoryBytes,
            BudgetKind = decision.BudgetKind,
            RequestedContextTokens = requested,
            FittingContextTokens = fitting,
            Remediation =
                "For a weight-dominated preset the lever is a SMALLER PRESET, not a shorter " +
                "context - the whole ladder buys about 6% for Llama 3.2 1B. Otherwise lower " +
                "EdgeChatOptions.MaxContextTokens, and on iOS add the " +
                "com.apple.developer.kernel.increased-memory-limit and " +
                "increased-debugging-memory-limit entitlements. The workspace and reserve terms " +
                "are engineering estimates, not measurements.",
        };

    private static EdgeChatException ConfigurationInvalid(ChatPreset preset, string configPath, Exception inner)
        => new(
            EdgeErrorCode.ChatConfigurationInvalid,
            $"'{configPath}' could not be read, so there is no ORT GenAI configuration to load " +
            $"'{preset.Id}' from: {inner.Message}",
            inner)
        {
            PresetId = preset.Id,
            ModelId = preset.Manifest.ModelId,
            Remediation =
                "Re-provision the model folder; genai_config.json is one of the manifest's files " +
                "and a verified provisioning always writes it.",
        };

    /// <summary>The package version, from the assembly metadata the csproj emits out of the CPM pin.</summary>
    /// <remarks>
    /// Plan adjustment 1. <c>Microsoft.ML.OnnxRuntimeGenAI.dll</c> reports <c>AssemblyVersion</c>
    /// 0.0.0.0, <c>FileVersion</c> 0.0.0.0 and carries no
    /// <c>AssemblyInformationalVersionAttribute</c>; there is no version API anywhere on its
    /// managed surface.
    /// </remarks>
    internal static string GenAiVersion { get; } =
        typeof(ChatModelHost).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "OnnxRuntimeGenAIVersion", StringComparison.Ordinal))
            ?.Value ?? "(unknown)";

    /// <summary>The ONNX Runtime assembly version loaded in this process.</summary>
    internal static string OrtVersion { get; } =
        typeof(OrtEnv).Assembly.GetName().Version?.ToString() ?? "(unknown)";

    /// <summary>
    /// The execution providers the resolved configuration names. CPU when it names none, which is
    /// what both shipped presets do and what the correct answer is on every platform this suite
    /// ships to.
    /// </summary>
    /// <param name="configJson">The provisioned config's text.</param>
    /// <returns>The provider names.</returns>
    internal static IReadOnlyList<string> ReadProviders(string configJson)
    {
        var providers = new List<string>();

        try
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(configJson);
            var options = root?["model"]?["decoder"]?["session_options"];

            if (options?["provider_options"] is System.Text.Json.Nodes.JsonArray entries)
            {
                foreach (var entry in entries)
                {
                    if (entry is System.Text.Json.Nodes.JsonObject named)
                    {
                        foreach (var (provider, _) in named)
                        {
                            providers.Add(provider);
                        }
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // The shape reader has already accepted this text, so a failure here is not worth
            // failing a load over; an empty provider list reads as "CPU", which it is.
        }

        return providers.Count == 0 ? ["cpu"] : providers;
    }

    private static GenAiChatModelResources CreateGenAiResources(
        string directory,
        string overlayJson,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Config? config = null;
        Model? model = null;
        Tokenizer? tokenizer = null;

        try
        {
            config = new Config(directory);

            // EXACTLY ONE Overlay call, with the document already composed in managed code.
            config.Overlay(overlayJson);

            model = new Model(config);
            tokenizer = new Tokenizer(model);

            return new GenAiChatModelResources
            {
                Config = config,
                Model = model,
                Tokenizer = tokenizer,
            };
        }
        catch
        {
            tokenizer?.Dispose();
            model?.Dispose();
            config?.Dispose();
            throw;
        }
    }

    private static string RunGuidanceProbe(
        GenAiChatModelResources resources,
        string guidanceType,
        string guidanceData,
        int maxTokens)
    {
        using var prompt = resources.Tokenizer.Encode("qedge");
        var promptTokens = prompt[0].Length;

        using var parameters = new GeneratorParams(resources.Model);
        parameters.SetSearchOption("max_length", promptTokens + maxTokens);
        parameters.SetSearchOption("do_sample", false);

        // TWO arguments. enableFFTokens stays at its default false (plan adjustment 3): a control
        // that changes what is emitted is not a control.
        parameters.SetGuidance(guidanceType, guidanceData);

        using var generator = new Generator(resources.Model, parameters);
        generator.AppendTokenSequences(prompt);

        using var stream = resources.Tokenizer.CreateStream();
        var decoded = new StringBuilder();

        for (var i = 0; i < maxTokens && !generator.IsDone(); i++)
        {
            generator.GenerateNextToken();
            var next = generator.GetNextTokens();
            if (next.Length > 0)
            {
                decoded.Append(stream.Decode(next[^1]));
            }
        }

        return decoded.ToString();
    }

    private bool TryLease(out ChatModelLease lease)
    {
        lock (_sync)
        {
            if (_current is { DropRequested: false } entry)
            {
                entry.Leases++;
                var info = entry.Info with { ActiveLeases = entry.Leases, IsLoaded = true };
                entry.Info = info;
                _lastInfo = info;

                lease = new ChatModelLease(
                    entry.Resources.Model,
                    entry.Resources.Tokenizer,
                    entry.Resources.Config,
                    info,
                    () => Release(entry));
                return true;
            }
        }

        lease = null!;
        return false;
    }

    private void Release(ModelEntry entry)
    {
        lock (_sync)
        {
            entry.Leases--;

            // At zero with the drop flag set, the model is disposed HERE - that is the whole point
            // of the lease. Every GenAI wrapper type has a finalizer, so a dropped-but-undisposed
            // model pins the entire native graph until GC.
            if (entry.Leases <= 0 && entry.DropRequested)
            {
                DisposeEntry(entry);
                return;
            }

            entry.Info = entry.Info with { ActiveLeases = Math.Max(0, entry.Leases) };
            if (ReferenceEquals(_current, entry))
            {
                _lastInfo = entry.Info;
            }
        }
    }

    private void DisposeEntry(ModelEntry entry)
    {
        if (entry.Disposed)
        {
            return;
        }

        entry.Disposed = true;
        entry.Resources.Dispose();
        entry.Info = entry.Info with { IsLoaded = false, ActiveLeases = 0 };

        if (_current is null)
        {
            _lastInfo = entry.Info;
        }

        s_dropped(_logger, entry.Info.ModelId, null);
    }

    private sealed class ModelEntry(GenAiChatModelResources resources, ChatModelInfo info)
    {
        public GenAiChatModelResources Resources { get; } = resources;

        public ChatModelInfo Info { get; set; } = info;

        public int Leases { get; set; }

        public bool DropRequested { get; set; }

        public bool Disposed { get; set; }
    }
}
