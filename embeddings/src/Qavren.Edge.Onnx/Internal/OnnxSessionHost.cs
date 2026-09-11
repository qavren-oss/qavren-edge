using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Onnx.Internal;

/// <summary>
/// What a caller says the graph must declare. Built by the embeddings package from the preset it is
/// about to run; null here means "record the signature, assert nothing", which is the only honest
/// answer when nothing has stated an expectation.
/// </summary>
/// <param name="InputNames">
/// Every input the caller can supply. Matched BY NAME: nomic's export declares
/// <c>input_ids, token_type_ids, attention_mask</c> and a positional match would feed the mask as
/// token-type ids and silently change the vectors.
/// </param>
/// <param name="OutputName">The output the caller reads.</param>
/// <param name="Dimensions">The declared embedding width, or null to skip the width check.</param>
internal sealed record OnnxSessionExpectation(
    IReadOnlyList<string> InputNames,
    string OutputName,
    int? Dimensions);

/// <summary>One registered model: the manifest, its session options and what it must declare.</summary>
internal sealed class OnnxModelRegistration
{
    /// <summary>The id a consumer acquires by.</summary>
    public required string ModelId { get; init; }

    /// <summary>The manifest, or null for the byte-array fixture registration.</summary>
    public OnnxModelManifest? Manifest { get; init; }

    /// <summary>The per-model session settings.</summary>
    public required OnnxSessionOptions SessionOptions { get; init; }

    /// <summary>What the graph must declare, or null to record the signature without asserting.</summary>
    public OnnxSessionExpectation? Expectation { get; init; }

    /// <summary>
    /// The tier-1 fixture's bytes. The ONLY path in this package that creates a session from a byte
    /// array, reachable only through <c>InternalsVisibleTo</c>; see the comment in AssemblyInfo.cs.
    /// </summary>
    public byte[]? TestGraph { get; init; }

    /// <summary>What the memory pre-flight budgets against.</summary>
    public long ModelBytes => TestGraph?.LongLength ?? Manifest?.TotalSizeBytes ?? 0;
}

/// <summary>Spec 9.1's ref-counted session host.</summary>
internal sealed class OnnxSessionHost : IOnnxSessionHost, IDisposable
{
    private static readonly Action<ILogger, string, string, double, Exception?> s_loaded =
        LoggerMessage.Define<string, string, double>(
            LogLevel.Information,
            new EventId(EdgeAiEventIds.SessionLoaded, nameof(EdgeAiEventIds.SessionLoaded)),
            "Session for {ModelId} created over {GraphPath} in {ElapsedMs} ms.");

    private static readonly Action<ILogger, string, Exception?> s_loadFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(EdgeAiEventIds.SessionLoadFailed, nameof(EdgeAiEventIds.SessionLoadFailed)),
            "Session creation for {ModelId} failed.");

    private static readonly Action<ILogger, string, Exception?> s_dropped =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(EdgeAiEventIds.SessionDropped, nameof(EdgeAiEventIds.SessionDropped)),
            "Session for {ModelId} was disposed.");

    private readonly IEdgeHost _host;
    private readonly IOnnxModelStore _store;
    private readonly IEdgeResourceMonitor _monitor;
    private readonly IEdgeModelPaths _modelPaths;
    private readonly IOptions<OnnxOptions> _options;
    private readonly ILogger<OnnxSessionHost> _logger;
    private readonly Dictionary<string, OnnxModelRegistration> _registrations;
    private readonly ConcurrentDictionary<string, SessionSlot> _slots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SessionOptions> _loading = new(StringComparer.Ordinal);

    /// <summary>Creates the host.</summary>
    /// <param name="host">SP1's host; every acquire awaits its startup first.</param>
    /// <param name="store">The model store.</param>
    /// <param name="monitor">The device resource monitor, for the memory pre-flight.</param>
    /// <param name="modelPaths">The ORT cache root.</param>
    /// <param name="registrations">Every <c>AddOnnxModel</c> registration.</param>
    /// <param name="options">The process-wide ORT options.</param>
    /// <param name="logger">The logger.</param>
    public OnnxSessionHost(
        IEdgeHost host,
        IOnnxModelStore store,
        IEdgeResourceMonitor monitor,
        IEdgeModelPaths modelPaths,
        IEnumerable<OnnxModelRegistration> registrations,
        IOptions<OnnxOptions> options,
        ILogger<OnnxSessionHost> logger)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        _host = host;
        _store = store;
        _monitor = monitor;
        _modelPaths = modelPaths;
        _options = options;
        _logger = logger;

        _registrations = new Dictionary<string, OnnxModelRegistration>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            _registrations[registration.ModelId] = registration;
        }
    }

    /// <summary>Every registered model id, in registration order.</summary>
    internal IReadOnlyCollection<string> RegisteredModelIds => _registrations.Keys;

    /// <inheritdoc />
    public IReadOnlyList<OnnxSessionInfo> Sessions
    {
        get
        {
            var sessions = new List<OnnxSessionInfo>(_slots.Count);
            foreach (var slot in _slots.Values)
            {
                lock (slot.Sync)
                {
                    if (slot.LastInfo is { } info)
                    {
                        sessions.Add(info);
                    }
                }
            }

            return sessions;
        }
    }

    /// <inheritdoc />
    public OnnxSessionInfo? Describe(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        foreach (var slot in _slots.Values)
        {
            lock (slot.Sync)
            {
                if (slot.LastInfo is { } info && string.Equals(info.ModelId, modelId, StringComparison.Ordinal))
                {
                    return info;
                }
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask<OnnxSessionLease> AcquireAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        // SP1's contract: a startup failure surfaces here with its real cause rather than as a
        // mysterious missing native two frames deeper.
        await _host.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return await AcquireCoreAsync(modelId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Everything <see cref="AcquireAsync"/> does EXCEPT awaiting <c>IEdgeHost.Started</c>. The
    /// order-220 warm-up task is itself a startup task, so awaiting startup from inside it would
    /// deadlock on the task's own completion.
    /// </summary>
    /// <param name="modelId">The registered model id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A lease that must be disposed.</returns>
    internal async ValueTask<OnnxSessionLease> AcquireCoreAsync(
        string modelId,
        CancellationToken cancellationToken)
    {
        if (!_registrations.TryGetValue(modelId, out var registration))
        {
            throw new EdgeOnnxException(
                EdgeErrorCode.ModelNotRegistered,
                $"No model is registered as '{modelId}'. Register it with AddOnnxModel(manifest) before " +
                "acquiring a session.")
            {
                ModelId = modelId,
                Remediation = "Call builder.AddOnnxModel(manifest) during composition.",
            };
        }

        var key = KeyFor(modelId, registration.SessionOptions);
        var slot = _slots.GetOrAdd(key, _ => new SessionSlot(registration.SessionOptions.DropOnMemoryPressure));

        if (TryLease(slot, out var lease))
        {
            return lease;
        }

        // Under the load gate, so N concurrent first-callers produce one session rather than N.
        await slot.LoadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryLease(slot, out lease))
            {
                return lease;
            }

            return await LoadAsync(slot, key, registration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            slot.LoadGate.Release();
        }
    }

    /// <inheritdoc />
    public Task<int> DropAsync(bool includePinned = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var released = 0;
        foreach (var slot in _slots.Values)
        {
            if (!includePinned && !slot.DropOnMemoryPressure)
            {
                continue;
            }

            lock (slot.Sync)
            {
                if (slot.Current is not { } entry)
                {
                    continue;
                }

                entry.DropRequested = true;
                slot.Current = null;

                if (entry.Leases == 0)
                {
                    DisposeEntry(slot, entry);
                    released++;
                }
                else
                {
                    // The session survives until the last lease returns: disposing one under an
                    // in-flight Run is a native access violation.
                    slot.LastInfo = entry.Info with { IsLoaded = true, ActiveLeases = entry.Leases };
                }
            }
        }

        return Task.FromResult(released);
    }

    /// <summary>
    /// Wired to both the acquire cancellation token and <c>MemoryPressure(Critical)</c> arriving
    /// mid-load, so a multi-second CoreML compile aborts cooperatively rather than running to
    /// completion into a kill.
    /// </summary>
    internal void CancelLoadsInFlight()
    {
        foreach (var options in _loading.Values)
        {
            options.SetLoadCancellationFlag(true);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var slot in _slots.Values)
        {
            lock (slot.Sync)
            {
                if (slot.Current is { } entry)
                {
                    slot.Current = null;
                    DisposeEntry(slot, entry);
                }
            }

            slot.LoadGate.Dispose();
        }

        _slots.Clear();
    }

    /// <summary>
    /// The dictionary key: the model id plus, when the overrides are non-empty, a canonical
    /// rendering of them. A pinned shape is baked into the session, so two different pinned shapes
    /// are two different sessions and the key has to say so rather than handing the second caller a
    /// session pinned to the first one's shape.
    /// </summary>
    /// <param name="modelId">The registered model id.</param>
    /// <param name="options">The per-model session options.</param>
    /// <returns>The slot key.</returns>
    internal static string KeyFor(string modelId, OnnxSessionOptions options)
    {
        if (options.FreeDimensionOverrides.Count == 0)
        {
            return modelId;
        }

        var parts = new List<string>(options.FreeDimensionOverrides.Count);
        foreach (var (dimension, value) in options.FreeDimensionOverrides)
        {
            parts.Add(dimension + "=" + value.ToString(CultureInfo.InvariantCulture));
        }

        parts.Sort(StringComparer.Ordinal);
        return modelId + "#" + string.Join(',', parts);
    }

    /// <summary>
    /// The tier-1 fixture escape hatch. The fixtures are 533-831-byte base64 graphs, so the two
    /// costs that keep <c>new InferenceSession(byte[])</c> out of the product surface - a doubled
    /// transient and a weakened CoreML cache key - are a kilobyte and an irrelevance here.
    /// </summary>
    /// <param name="model">The serialised ONNX graph.</param>
    /// <returns>The session. The caller disposes it.</returns>
    internal static InferenceSession CreateSessionForTests(byte[] model) => new(model);

    /// <summary>Reads what the loaded graph declares.</summary>
    /// <param name="session">The created session.</param>
    /// <returns>The signature.</returns>
    internal static OnnxSessionSignature ReadSignature(InferenceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var inputTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, metadata) in session.InputMetadata)
        {
            inputTypes[name] = metadata.ElementDataType.ToString();
        }

        var outputShapes = new Dictionary<string, IReadOnlyList<long>>(StringComparer.Ordinal);
        foreach (var (name, metadata) in session.OutputMetadata)
        {
            var dimensions = new List<long>(metadata.Dimensions.Length);
            foreach (var dimension in metadata.Dimensions)
            {
                dimensions.Add(dimension);
            }

            outputShapes[name] = dimensions;
        }

        return new OnnxSessionSignature(
            [.. session.InputNames],
            [.. session.OutputNames],
            inputTypes,
            outputShapes);
    }

    /// <summary>
    /// Eager validation, by name. Every name the caller references must exist; every input the graph
    /// declares must be one the caller can supply, because ORT requires every declared input to be
    /// fed and an unrecognised one can never be satisfied.
    /// </summary>
    /// <param name="modelId">The model id, for the exception.</param>
    /// <param name="graphPath">The file, so the message names it.</param>
    /// <param name="signature">What the graph declares.</param>
    /// <param name="expectation">What the caller expects, or null to assert nothing.</param>
    internal static void ValidateSignature(
        string modelId,
        string graphPath,
        OnnxSessionSignature signature,
        OnnxSessionExpectation? expectation)
    {
        ArgumentNullException.ThrowIfNull(signature);

        if (expectation is null)
        {
            return;
        }

        var declared = new HashSet<string>(signature.InputNames, StringComparer.Ordinal);
        var supplied = new HashSet<string>(expectation.InputNames, StringComparer.Ordinal);

        foreach (var name in expectation.InputNames)
        {
            if (!declared.Contains(name))
            {
                throw Mismatch(modelId, graphPath, signature, expectation,
                    $"the preset references input '{name}', which the graph does not declare");
            }
        }

        foreach (var name in signature.InputNames)
        {
            if (!supplied.Contains(name))
            {
                throw Mismatch(modelId, graphPath, signature, expectation,
                    $"the graph declares input '{name}', which nothing can supply - ORT requires every " +
                    "declared input to be fed");
            }
        }

        if (!signature.OutputShapes.TryGetValue(expectation.OutputName, out var shape))
        {
            throw Mismatch(modelId, graphPath, signature, expectation,
                $"the preset reads output '{expectation.OutputName}', which the graph does not produce");
        }

        if (expectation.Dimensions is { } dimensions && shape.Count > 0)
        {
            var last = shape[^1];

            // A symbolic dimension is -1 and proves nothing; only a static one can disagree.
            if (last > 0 && last != dimensions)
            {
                throw Mismatch(modelId, graphPath, signature, expectation,
                    $"the preset declares {dimensions} dimensions and the graph's '{expectation.OutputName}' " +
                    $"declares {last}");
            }
        }
    }

    private static EdgeOnnxException Mismatch(
        string modelId,
        string graphPath,
        OnnxSessionSignature signature,
        OnnxSessionExpectation expectation,
        string because)
        => new(
            EdgeErrorCode.OnnxModelSignatureMismatch,
            $"'{graphPath}' does not match the preset registered for '{modelId}': {because}. " +
            $"Graph inputs: [{string.Join(", ", signature.InputNames)}]; preset inputs: " +
            $"[{string.Join(", ", expectation.InputNames)}]. Graph outputs: " +
            $"[{string.Join(", ", signature.OutputNames)}]; preset output: '{expectation.OutputName}'.")
        {
            ModelId = modelId,
            Remediation = "Point the preset at the export it was written for, or register the preset that " +
                          "matches this export.",
        };

    private bool TryLease(SessionSlot slot, out OnnxSessionLease lease)
    {
        lock (slot.Sync)
        {
            if (slot.Current is { DropRequested: false } entry)
            {
                entry.Leases++;
                var info = entry.Info with { ActiveLeases = entry.Leases, IsLoaded = true };
                entry.Info = info;
                slot.LastInfo = info;

                lease = new OnnxSessionLease(entry.Session, info, () => Release(slot, entry));
                return true;
            }
        }

        lease = null!;
        return false;
    }

    private void Release(SessionSlot slot, SessionEntry entry)
    {
        lock (slot.Sync)
        {
            entry.Leases--;

            // At zero with the drop flag set, the session is disposed HERE - that is the whole point
            // of the lease. A dropped-but-undisposed session pins the entire native graph until GC.
            if (entry.Leases <= 0 && entry.DropRequested)
            {
                DisposeEntry(slot, entry);
                return;
            }

            entry.Info = entry.Info with { ActiveLeases = Math.Max(0, entry.Leases) };
            if (ReferenceEquals(slot.Current, entry))
            {
                slot.LastInfo = entry.Info;
            }
        }
    }

    private void DisposeEntry(SessionSlot slot, SessionEntry entry)
    {
        if (entry.Disposed)
        {
            return;
        }

        entry.Disposed = true;
        entry.Session.Dispose();
        entry.Info = entry.Info with { IsLoaded = false, ActiveLeases = 0 };

        // Only when nothing live has taken its place: a reload that happened while this one was
        // retiring owns the slot's reported state.
        if (slot.Current is null)
        {
            slot.LastInfo = entry.Info;
        }

        s_dropped(_logger, entry.Info.ModelId, null);
    }

    private async ValueTask<OnnxSessionLease> LoadAsync(
        SessionSlot slot,
        string key,
        OnnxModelRegistration registration,
        CancellationToken cancellationToken)
    {
        var modelId = registration.ModelId;
        var started = Stopwatch.GetTimestamp();

        string graphPath;
        string graphSha;
        if (registration.TestGraph is { } bytes)
        {
            graphPath = $"(bytes:{modelId})";
            graphSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        else
        {
            var provisioned = await _store
                .EnsureAsync(registration.Manifest!, progress: null, cancellationToken)
                .ConfigureAwait(false);

            graphPath = provisioned.GraphPath;
            graphSha = provisioned.GraphSha256;
        }

        PreflightMemory(registration, graphPath);

        var (sessionOptions, report) = SessionOptionsFactory.Build(
            modelId,
            registration.SessionOptions,
            _modelPaths.OrtCache,
            graphSha,
            _options.Value,
            _registrations.Count,
            _logger);

        InferenceSession session;
        try
        {
            _loading[key] = sessionOptions;
            using (cancellationToken.Register(static state => ((SessionOptions)state!).SetLoadCancellationFlag(true), sessionOptions))
            {
                session = registration.TestGraph is { } graph
                    ? new InferenceSession(graph, sessionOptions)
                    : new InferenceSession(graphPath, sessionOptions);
            }
        }
        catch (OnnxRuntimeException ex)
        {
            s_loadFailed(_logger, modelId, ex);
            throw new EdgeOnnxException(
                EdgeErrorCode.OnnxSessionCreationFailed,
                $"ONNX Runtime refused to create a session over '{graphPath}': {ex.Message}",
                ex)
            {
                ModelId = modelId,
                ExecutionProviders = report.Attempts,
            };
        }
        finally
        {
            _loading.TryRemove(key, out _);
            sessionOptions.Dispose();
        }

        try
        {
            var signature = ReadSignature(session);
            ValidateSignature(modelId, graphPath, signature, registration.Expectation);

            var elapsed = Stopwatch.GetElapsedTime(started);
            s_loaded(_logger, modelId, graphPath, elapsed.TotalMilliseconds, null);

            lock (slot.Sync)
            {
                slot.LoadCount++;
                var info = new OnnxSessionInfo(
                    modelId,
                    graphPath,
                    graphSha,
                    signature,
                    report,
                    elapsed,
                    DateTimeOffset.UtcNow,
                    slot.LoadCount,
                    ActiveLeases: 1,
                    IsLoaded: true);

                var entry = new SessionEntry(session, info) { Leases = 1 };
                slot.Current = entry;
                slot.LastInfo = info;

                return new OnnxSessionLease(session, info, () => Release(slot, entry));
            }
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void PreflightMemory(OnnxModelRegistration registration, string graphPath)
    {
        var factor = registration.SessionOptions.MemoryHeadroomFactor;
        if (factor <= 0)
        {
            return;
        }

        var available = _monitor.Read().AvailableMemoryBytes;

        // Null or zero means UNKNOWN and skips the check. Apple's os_proc_available_memory() returns
        // 0 for "unknown or already over", and a reading nobody can make is never a refusal.
        if (available is not > 0)
        {
            return;
        }

        var required = (long)(registration.ModelBytes * factor) + registration.SessionOptions.MemoryHeadroomBytes;
        if (available >= required)
        {
            return;
        }

        throw new EdgeOnnxException(
            EdgeErrorCode.OnnxInsufficientMemory,
            $"Creating a session over '{graphPath}' needs about {required} bytes and the OS reports " +
            $"{available} available. The budget is modelBytes * {factor.ToString(CultureInfo.InvariantCulture)} " +
            $"+ {registration.SessionOptions.MemoryHeadroomBytes}, which is an engineering estimate anchored " +
            "on the roughly 2x transient cost of session creation, not a measurement.")
        {
            ModelId = registration.ModelId,
            RequiredBytes = required,
            AvailableBytes = available,
            Remediation = "Register the int8 preset (MiniLmL6V2Int8) instead of an fp32 one, or raise " +
                          "OnnxSessionOptions.MemoryHeadroomFactor once you have measured this device.",
        };
    }

    private sealed class SessionEntry(InferenceSession session, OnnxSessionInfo info)
    {
        public InferenceSession Session { get; } = session;

        public OnnxSessionInfo Info { get; set; } = info;

        public int Leases { get; set; }

        public bool DropRequested { get; set; }

        public bool Disposed { get; set; }
    }

    private sealed class SessionSlot(bool dropOnMemoryPressure)
    {
        public SemaphoreSlim LoadGate { get; } = new(1, 1);

        public object Sync { get; } = new();

        public bool DropOnMemoryPressure { get; } = dropOnMemoryPressure;

        public SessionEntry? Current { get; set; }

        public OnnxSessionInfo? LastInfo { get; set; }

        public int LoadCount { get; set; }
    }
}
