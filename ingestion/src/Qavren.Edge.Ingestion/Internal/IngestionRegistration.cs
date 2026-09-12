using System.Globalization;
using Qavren.Edge.VectorData;
using MEVD = Microsoft.Extensions.VectorData;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>Everything one <c>AddIngestion</c> call decided, plus what startup froze on it.</summary>
internal sealed class IngestionRegistration
{
    public IngestionRegistration(
        IngestionOptions options,
        MEVD.VectorStoreCollectionDefinition definition,
        EdgeVectorStoreCollectionOptions collectionOptions,
        int migrationVersion)
    {
        Options = options;
        Definition = definition;
        CollectionOptions = collectionOptions;
        MigrationVersion = migrationVersion;
    }

    public IngestionOptions Options { get; }

    public MEVD.VectorStoreCollectionDefinition Definition { get; }

    /// <summary>The shaping values that produced the migration DDL. The 6011 check's left side.</summary>
    public EdgeVectorStoreCollectionOptions CollectionOptions { get; }

    /// <summary>The collection claims this; the state claims this + 1 (plan adjustment 2).</summary>
    public int MigrationVersion { get; }

    public string CollectionName => Options.CollectionName;

    /// <summary>Frozen by the order-400 startup task.</summary>
    public ResolvedChunkOptions? Chunking { get; set; }

    /// <summary>Resolved by the order-400 startup task.</summary>
    public IChunkTokenizer? Tokenizer { get; set; }

    /// <summary>
    /// The extractor-INDEPENDENT baseline recipe, computed once by the order-400 startup task. Its
    /// <see cref="IngestionRecipe.ExtractorFingerprint"/> is <see cref="RecipeComposition.Baseline"/>;
    /// the runner substitutes the selected extractor's fingerprint per document (spec 9.2).
    /// </summary>
    public IngestionRecipe? Recipe { get; set; }

    /// <summary>
    /// Spec 9.5's <c>tokensEmbeddedSource</c>: <c>"usage"</c> when the last window's token count came
    /// from <c>GeneratedEmbeddings.Usage.InputTokenCount</c>, <c>"counted"</c> when it came from
    /// SP3's own <c>IChunkTokenizer.CountTokens</c> sum. Null until a window has been embedded.
    /// </summary>
    public string? TokensEmbeddedSource { get; set; }

    /// <summary>
    /// Spec 12's <c>lastThrottleReason</c>: the last reason string an <see cref="IIngestionThrottle"/>
    /// returned, pause or adjustment. Null until a throttle has said anything.
    /// </summary>
    public string? LastThrottleReason { get; set; }

    /// <summary>
    /// The live run's progress, or null between runs. Spec 12's three <c>activeRun*</c> keys read
    /// it, and it is carried HERE rather than on the pipeline because
    /// <c>IEdgeDiagnosticsContributor.Describe</c> is synchronous and per collection.
    /// </summary>
    public IngestionProgress? ActiveProgress { get; set; }

    /// <summary>
    /// The last run that finished in THIS process, or null. Spec 12's <c>lastRun*</c> keys read it.
    /// Two of its members — <c>EmbedCalls</c> and <c>TokensEmbedded</c> — have no column in
    /// <c>&lt;prefix&gt;_run</c>, so the snapshot is in-memory rather than a query: a
    /// <c>Describe()</c> that opened a connection for keys nobody asked for would defeat the whole
    /// point of <see cref="IngestionOptions.IncludeCountsInDiagnostics"/> being off by default.
    /// </summary>
    public IngestionLastRun? LastRun { get; set; }

    /// <summary>The collection's data table, frozen by the order-400 startup task.</summary>
    public string? DataTable { get; set; }

    /// <summary>The collection's <c>vec0</c> table, frozen by the order-400 startup task.</summary>
    public string? VectorTable { get; set; }

    /// <summary>The collection's FTS5 sidecar, or null when it has none.</summary>
    public string? FullTextTable { get; set; }
}

/// <summary>
/// What spec 12's <c>lastRun*</c> keys report: the last run that finished in this process.
/// </summary>
/// <param name="RunId">Its id.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="SuspendReason">Why it suspended, or null.</param>
/// <param name="FinishedUtc">When it ended.</param>
/// <param name="Duration">How long it took.</param>
/// <param name="DocumentsIndexed">Documents written.</param>
/// <param name="DocumentsSkipped">Documents the spec 9.4 gates skipped.</param>
/// <param name="DocumentsFailed">Documents recorded <c>Failed</c>.</param>
/// <param name="ChunksAdded">Chunks upserted.</param>
/// <param name="ChunksRemoved">Chunks deleted.</param>
/// <param name="EmbedCalls">Spec 9.5 step a1 calls.</param>
/// <param name="TokensEmbedded">Tokens embedded, from usage or counted.</param>
internal sealed record IngestionLastRun(
    string RunId,
    IngestionRunOutcome Outcome,
    string? SuspendReason,
    DateTimeOffset FinishedUtc,
    TimeSpan Duration,
    int DocumentsIndexed,
    int DocumentsSkipped,
    int DocumentsFailed,
    int ChunksAdded,
    int ChunksRemoved,
    int EmbedCalls,
    long TokensEmbedded);

/// <summary>
/// The set of collections <c>AddIngestion</c> configured. A builder-time singleton instance, the
/// same idiom SP2's <c>EdgeVectorCollectionRegistry</c> uses.
/// </summary>
internal sealed class IngestionRegistry
{
    private readonly Dictionary<string, IngestionRegistration> _byName = new(StringComparer.Ordinal);

    public IReadOnlyCollection<IngestionRegistration> All => _byName.Values;

    /// <summary>Adds one registration. A second one for the same collection is 6007.</summary>
    public void Add(IngestionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        if (!_byName.TryAdd(registration.CollectionName, registration))
        {
            throw new EdgeConfigurationException(
                EdgeErrorCode.IngestionMigrationVersionConflict,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "AddIngestion was called twice for collection '{0}'. Each collection is configured once, and " +
                    "each call claims migration versions {1} and {2}.",
                    registration.CollectionName,
                    registration.MigrationVersion,
                    registration.MigrationVersion + 1));
        }
    }

    public bool Contains(string collectionName) => _byName.ContainsKey(collectionName);

    /// <summary>
    /// Spec 11's <c>collectionName</c> resolution, and error 6002 (plan adjustment 21). A null name
    /// with exactly one configured collection resolves to it; a null name with more than one is
    /// 6002, because guessing is worse. An unknown name is 6002 <b>before</b> any enumeration, any
    /// open and any write — without this check a typo reaches the state store and comes back as
    /// <see cref="EdgeErrorCode.IngestionStateMissing"/>, which blames the schema for a typo.
    /// </summary>
    public IngestionRegistration Resolve(string? collectionName)
    {
        if (collectionName is null)
        {
            if (_byName.Count == 1)
            {
                foreach (var only in _byName.Values)
                {
                    return only;
                }
            }

            throw NotConfigured("(null)");
        }

        return _byName.TryGetValue(collectionName, out var registration)
            ? registration
            : throw NotConfigured(collectionName);
    }

    private EdgeIngestionException NotConfigured(string requested)
    {
        var configured = _byName.Count == 0
            ? "(none)"
            : string.Join(", ", _byName.Keys.Order(StringComparer.Ordinal));

        return new EdgeIngestionException(
            EdgeErrorCode.IngestionCollectionNotConfigured,
            string.Format(
                CultureInfo.InvariantCulture,
                "Collection '{0}' is not configured for ingestion. Configured collections: {1}.",
                requested,
                configured))
        {
            Remediation =
                "Call AddIngestion for that collection, or pass null to use the single configured one.",
        };
    }
}

/// <summary>
/// The stop flag and the write-batch shrink the lifecycle observer sets and the runner reads, plus
/// the committed-checkpoint signal the grace budget waits on. One instance per pipeline.
/// </summary>
internal sealed class IngestionRunControl
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _checkpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _stopReason;
    private bool _stopFromLifecycle;
    private int _shrinkDivisor = 1;

    /// <summary>The reason a stop was requested, or null.</summary>
    public string? StopReason
    {
        get
        {
            lock (_gate)
            {
                return _stopReason;
            }
        }
    }

    /// <summary>
    /// The stop reason and whether it came from the lifecycle observer, read together under one
    /// lock. Spec 10.2 step 1 turns on that distinction: a stop that came from lifecycle suspends,
    /// and <c>Cancelled</c> is the caller-cancelled case. It is recorded as a FLAG rather than
    /// inferred from a <c>"lifecycle:"</c> prefix on the reason, because two of the observer's own
    /// stops — <c>memory:critical</c> being the one that bit — carry spec 10.2's own reason strings
    /// and have no such prefix.
    /// </summary>
    public (string? Reason, bool FromLifecycle) Stop
    {
        get
        {
            lock (_gate)
            {
                return (_stopReason, _stopFromLifecycle);
            }
        }
    }

    /// <summary>True while a run is between its first document and its last.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>The write batch after memory pressure, floored at four.</summary>
    public int ApplyShrink(int batchSize)
    {
        lock (_gate)
        {
            return Math.Max(4, batchSize / _shrinkDivisor);
        }
    }

    /// <summary>Halves the effective batch. Called on <c>MemoryPressure(Moderate | Critical)</c>.</summary>
    public void Shrink()
    {
        lock (_gate)
        {
            _shrinkDivisor = Math.Min(_shrinkDivisor * 2, 1024);
        }
    }

    /// <summary>Clears the shrink. Called on <c>Resumed</c>.</summary>
    public void ClearShrink()
    {
        lock (_gate)
        {
            _shrinkDivisor = 1;
        }
    }

    /// <summary>Asks the run to stop on its next committed boundary.</summary>
    /// <param name="reason">One of spec 10.2's <c>SuspendReason</c> strings.</param>
    /// <param name="fromLifecycle">
    /// True only for the lifecycle observer's own stops, which suspend rather than cancel.
    /// </param>
    public void RequestStop(string reason, bool fromLifecycle = false)
    {
        lock (_gate)
        {
            if (_stopReason is not null)
            {
                return;
            }

            _stopReason = reason;
            _stopFromLifecycle = fromLifecycle;
        }
    }

    /// <summary>Marks a run started and clears any stale stop reason.</summary>
    public void BeginRun()
    {
        lock (_gate)
        {
            _stopReason = null;
            _stopFromLifecycle = false;
            IsRunning = true;
        }
    }

    /// <summary>Marks the run finished and releases anyone waiting on a checkpoint.</summary>
    public void EndRun()
    {
        lock (_gate)
        {
            IsRunning = false;
        }

        SignalCheckpoint();
    }

    /// <summary>Called by the runner after every committed state-row write.</summary>
    public void SignalCheckpoint()
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            previous = _checkpoint;
            _checkpoint = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        previous.TrySetResult();
    }

    /// <summary>
    /// Waits up to <paramref name="budget"/> for the runner's next committed checkpoint. Returns
    /// false when the window elapsed first — which logs and returns immediately, because SP1's
    /// platform bridges raise this on the callback thread.
    /// </summary>
    public async Task<bool> WaitForCheckpointAsync(
        TimeSpan budget, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        TaskCompletionSource pending;
        lock (_gate)
        {
            if (!IsRunning)
            {
                return true;
            }

            pending = _checkpoint;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(budget, timeProvider, timeoutSource.Token);
        var finished = await Task.WhenAny(pending.Task, delay).ConfigureAwait(false);
        if (finished == pending.Task)
        {
            await timeoutSource.CancelAsync().ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
