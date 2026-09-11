using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Embeddings.Onnx;

/// <summary>What one generator is, as diagnostics and a consumer see it.</summary>
/// <param name="PresetId">The preset's id.</param>
/// <param name="ModelId">The manifest's model id, the same string <c>OnnxSessionInfo.ModelId</c> carries.</param>
/// <param name="Dimensions">The fixed embedding width.</param>
/// <param name="MaxSequenceLength">The preset's token ceiling.</param>
/// <param name="Pooling">How the batch pools.</param>
/// <param name="Normalize">Whether the pooled vector is L2-normalised.</param>
/// <param name="QueryPrefix">The query-side instruction prefix, or null.</param>
/// <param name="DocumentPrefix">The document-side instruction prefix, or null.</param>
/// <param name="TokenizerKind">Which tokenizer family the preset uses.</param>
/// <param name="VocabularySize">The vocabulary size, or 0 before the first embed.</param>
/// <param name="GraphPath">The absolute path ORT was handed, or empty before the first embed.</param>
/// <param name="GraphSha256">The graph's digest, or empty before the first embed.</param>
/// <param name="ExecutionProviders">Every provider attempt, in order, and the one that was accepted.</param>
public sealed record OnnxEmbeddingGeneratorInfo(
    string PresetId,
    string ModelId,
    int Dimensions,
    int MaxSequenceLength,
    EmbeddingPooling Pooling,
    bool Normalize,
    string? QueryPrefix,
    string? DocumentPrefix,
    EdgeTokenizerKind TokenizerKind,
    int VocabularySize,
    string GraphPath,
    string GraphSha256,
    ExecutionProviderReport ExecutionProviders);

/// <summary>
/// The single public leaf. It implements the LITERAL closed generic MEVD pattern-matches; a
/// generator typed to any other input type is silently unresolvable by a vector store.
/// Thread-safe: concurrent calls serialise on one semaphore around ORT <c>Run</c>.
/// </summary>
public sealed class OnnxEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private static readonly ExecutionProviderReport s_noSession =
        new(EdgeExecutionProvider.Cpu, []);

    private static readonly Action<ILogger, string, int, int, double, Exception?> s_batchCompleted =
        LoggerMessage.Define<string, int, int, double>(
            LogLevel.Debug,
            new EventId(EdgeAiEventIds.EmbeddingBatchCompleted, nameof(EdgeAiEventIds.EmbeddingBatchCompleted)),
            "Embedded {Count} inputs for {PresetId} at sequence length {SequenceLength} in {ElapsedMs} ms.");

    private static readonly Action<ILogger, string, int, int, Exception?> s_inputTruncated =
        LoggerMessage.Define<string, int, int>(
            LogLevel.Warning,
            new EventId(EdgeAiEventIds.EmbeddingInputTruncated, nameof(EdgeAiEventIds.EmbeddingInputTruncated)),
            "An input to {PresetId} was truncated to {TokenCount} tokens at the preset maximum of {MaxSequenceLength}.");

    private static readonly Action<ILogger, string, int, int, Exception?> s_batchShrunk =
        LoggerMessage.Define<string, int, int>(
            LogLevel.Information,
            new EventId(EdgeAiEventIds.EmbeddingBatchShrunk, nameof(EdgeAiEventIds.EmbeddingBatchShrunk)),
            "Memory pressure shrank the effective batch size for {PresetId} from {MaxBatchSize} to {EffectiveBatchSize}.");

    private readonly IOnnxSessionHost _sessionHost;
    private readonly IEdgeTokenizerProvider _tokenizers;
    private readonly IEdgeResourceMonitor _resources;
    private readonly IOptions<OnnxEmbeddingOptions> _options;
    private readonly EmbeddingInputKind _inputKind;
    private readonly ILogger<OnnxEmbeddingGenerator> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly EmbeddingPreset _preset;
    private readonly SemaphoreSlim _gate;
    private readonly EmbeddingGeneratorMetadata _metadata;
    private readonly Lock _statistics = new();
    private readonly List<double> _runMilliseconds = [];

    private OnnxEmbeddingGenerator? _querySibling;
    private OnnxSessionInfo? _lastSession;

    // THIS generator's tokenizer, latched by the first GenerateAsync, and the only tokenizer any
    // member of this class reads. Never IEdgeTokenizerProvider.Current: that property is documented
    // as the MOST RECENTLY BUILT tokenizer, so in a process with two registrations - say
    // AddOnnxEmbeddings() on MiniLM at 256 tokens plus AddOnnxEmbeddings("bge", ... BgeSmallEnV15,
    // 512) - whichever generator embedded last would own it, and this one's GetService,
    // Info.VocabularySize and Snapshot would report the other preset's vocabulary. No exception,
    // plausible and wrong, which is the failure class the provider's per-preset cache exists to stop.
    private IEdgeTokenizer? _tokenizer;
    private long _embeddingsGenerated;
    private long _batchesRun;
    private long _tokensEncoded;
    private long _truncatedInputs;
    private int _effectiveBatchSize;
    private bool _disposed;

    /// <summary>Creates a generator for one input kind.</summary>
    /// <param name="sessionHost">Owns every ONNX session in the process.</param>
    /// <param name="tokenizers">Builds the tokenizer lazily on first use.</param>
    /// <param name="resources">The device monitor whose latched pressure shrinks the batch.</param>
    /// <param name="options">The embedding options.</param>
    /// <param name="inputKind">Which prefix this instance applies.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock every <c>Embedding.CreatedAt</c> is read from.</param>
    public OnnxEmbeddingGenerator(
        IOnnxSessionHost sessionHost,
        IEdgeTokenizerProvider tokenizers,
        IEdgeResourceMonitor resources,
        IOptions<OnnxEmbeddingOptions> options,
        EmbeddingInputKind inputKind,
        ILogger<OnnxEmbeddingGenerator> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(sessionHost);
        ArgumentNullException.ThrowIfNull(tokenizers);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _sessionHost = sessionHost;
        _tokenizers = tokenizers;
        _resources = resources;
        _options = options;
        _inputKind = inputKind;
        _logger = logger;
        _timeProvider = timeProvider;

        _preset = options.Value.Preset;
        _gate = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrency));
        _effectiveBatchSize = Math.Max(1, options.Value.MaxBatchSize);
        _metadata = new EmbeddingGeneratorMetadata(
            providerName: "Qavren.Edge.Embeddings.Onnx",
            providerUri: null,
            defaultModelId: _preset.Manifest.ModelId,
            defaultModelDimensions: _preset.Dimensions);
    }

    /// <summary>The fixed embedding width.</summary>
    public int Dimensions => _preset.Dimensions;

    /// <summary>
    /// Which prefix this instance applies. INTERNAL, not public: spec 7's
    /// <c>OnnxEmbeddingGenerator</c> declaration does not carry it, and a consumer that needs the
    /// query side asks for it by <see cref="EdgeEmbeddings.QueryServiceKey"/> rather than reading a
    /// flag off the document generator. The test assembly sees it through <c>InternalsVisibleTo</c>.
    /// </summary>
    internal EmbeddingInputKind InputKind => _inputKind;

    /// <summary>Everything a support request needs about this generator.</summary>
    public OnnxEmbeddingGeneratorInfo Info
    {
        get
        {
            var session = Volatile.Read(ref _lastSession);
            return new OnnxEmbeddingGeneratorInfo(
                _preset.Id,
                _preset.Manifest.ModelId,
                _preset.Dimensions,
                _preset.MaxSequenceLength,
                _preset.Pooling,
                _preset.Normalize,
                _preset.QueryPrefix,
                _preset.DocumentPrefix,
                _preset.TokenizerKind,
                Volatile.Read(ref _tokenizer)?.VocabularySize ?? 0,
                session?.GraphPath ?? string.Empty,
                session?.GraphSha256 ?? string.Empty,
                session?.ExecutionProviders ?? s_noSession);
        }
    }

    /// <summary>
    /// Exactly one embedding per input, in input order. An empty sequence returns an empty
    /// collection without touching ORT.
    /// </summary>
    /// <param name="values">The inputs.</param>
    /// <param name="options">Generation options, or null.</param>
    /// <param name="cancellationToken">Cancellation, honoured BETWEEN batches. A single ORT Run is not interruptible.</param>
    /// <returns>One embedding per input, plus the unpadded token count in <c>Usage</c>.</returns>
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Spec 11, options handling. Dimensions and ModelId are checked; AdditionalProperties and
        // RawRepresentationFactory are IGNORED, and that is a decision rather than an omission.
        // AdditionalProperties is an untyped bag with no meaning for a fixed local graph - there is
        // no vendor parameter to forward it to - and RawRepresentationFactory exists so a consumer
        // can attach a provider's native request object, which here is a SessionOptions and three
        // OrtValues that are disposed before this method returns. Handing a consumer a reference to
        // disposed native memory is worse than handing them nothing. Do not "wire them up".
        ValidateOptions(options);

        var inputs = values as IReadOnlyList<string> ?? [.. values];
        var results = new GeneratedEmbeddings<Embedding<float>>(inputs.Count);
        if (inputs.Count == 0)
        {
            return results;
        }

        var tokenizer = await _tokenizers.GetAsync(_preset, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _tokenizer, tokenizer);
        using var lease = await _sessionHost
            .AcquireAsync(_preset.Manifest.ModelId, cancellationToken)
            .ConfigureAwait(false);

        Volatile.Write(ref _lastSession, lease.Info);

        long tokens = 0;
        var start = 0;
        while (start < inputs.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var size = Math.Min(EffectiveBatchSize(), inputs.Count - start);
            var texts = new string[size];
            for (var i = 0; i < size; i++)
            {
                texts[i] = ApplyPrefix(inputs[start + i]);
            }

            // One timestamp per batch, shared by every embedding in it: a per-embedding clock read
            // buys nothing and costs a syscall each.
            var createdAt = _timeProvider.GetUtcNow();

            // The tokenizer is guarded by the SAME semaphore as ORT Run: Microsoft.ML.Tokenizers
            // states no thread-safety guarantee.
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            BatchOutcome outcome;
            try
            {
                outcome = await Task
                    .Run(() => RunBatch(lease, tokenizer, texts), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            // Appended in the order the inputs arrived, never in completion order.
            for (var i = 0; i < outcome.Vectors.Length; i++)
            {
                results.Add(new Embedding<float>(outcome.Vectors[i])
                {
                    ModelId = _preset.Manifest.ModelId,
                    CreatedAt = createdAt,
                });
            }

            tokens += outcome.TokenCount;
            start += size;
        }

        // Unpadded, including [CLS] and [SEP]: the padded BatchSize * SequenceLength is 512 for a
        // one-word input and would make ingestion cost look ~50x worse than it is. OutputTokenCount
        // and TotalTokenCount stay null - an embedding model emits no output tokens, and a total
        // equal to the input would be a fabricated third number.
        results.Usage = new UsageDetails { InputTokenCount = tokens };

        Interlocked.Add(ref _embeddingsGenerated, results.Count);
        Interlocked.Add(ref _tokensEncoded, tokens);
        return results;
    }

    /// <summary>
    /// Returns, in order: <c>this</c> when assignable; <see cref="EmbeddingGeneratorMetadata"/>;
    /// <see cref="OnnxEmbeddingGeneratorInfo"/>; <see cref="EmbeddingPreset"/>;
    /// <see cref="OnnxSessionInfo"/>; <see cref="IEdgeTokenizer"/>; and, for serviceKey
    /// <see cref="EdgeEmbeddings.QueryServiceKey"/>, the query-prefixed sibling generator -
    /// returned for a <paramref name="serviceType"/> of either the non-generic
    /// <see cref="IEmbeddingGenerator"/> or the closed generic, because the vector store asks with
    /// the non-generic type while a direct caller usually asks with the closed one.
    /// </summary>
    /// <param name="serviceType">The requested service type.</param>
    /// <param name="serviceKey">The service key, or null.</param>
    /// <returns>The service, or null.</returns>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            var wantsQuery = serviceKey is string key
                && string.Equals(key, EdgeEmbeddings.QueryServiceKey, StringComparison.Ordinal)
                && (serviceType == typeof(IEmbeddingGenerator)
                    || serviceType == typeof(IEmbeddingGenerator<string, Embedding<float>>));

            return wantsQuery ? QuerySibling() : null;
        }

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        if (serviceType == typeof(EmbeddingGeneratorMetadata))
        {
            return _metadata;
        }

        if (serviceType == typeof(OnnxEmbeddingGeneratorInfo))
        {
            return Info;
        }

        if (serviceType == typeof(EmbeddingPreset))
        {
            return _preset;
        }

        if (serviceType == typeof(OnnxSessionInfo))
        {
            return Volatile.Read(ref _lastSession) ?? _sessionHost.Describe(_preset.Manifest.ModelId);
        }

        if (serviceType == typeof(IEdgeTokenizer))
        {
            // THIS generator's preset's tokenizer, never the provider's most-recently-built one.
            // Null until the first GenerateAsync, which is the honest answer: handing back another
            // registration's tokenizer would apply that preset's MaxSequenceLength and LowerCase.
            return Volatile.Read(ref _tokenizer);
        }

        return null;
    }

    /// <summary>
    /// Releases this generator's session lease. Does NOT dispose the shared session - the session
    /// host owns it - so wrapping this in a delegating generator, which disposes its inner by
    /// default, is safe.
    /// </summary>
    /// <remarks>
    /// Leases are taken per call and returned before <c>GenerateAsync</c> returns, so there is no
    /// long-lived lease left to release here; what this disposes is the concurrency gate and the
    /// query sibling this instance created.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _querySibling, null)?.Dispose();
        _gate.Dispose();
    }

    internal EmbeddingDiagnosticsSnapshot Snapshot()
    {
        lock (_statistics)
        {
            var samples = _runMilliseconds.Count == 0 ? [] : _runMilliseconds.ToArray();
            Array.Sort(samples);

            return new EmbeddingDiagnosticsSnapshot(
                Interlocked.Read(ref _embeddingsGenerated),
                Interlocked.Read(ref _batchesRun),
                Interlocked.Read(ref _tokensEncoded),
                Interlocked.Read(ref _truncatedInputs),
                Percentile(samples, 0.50),
                Percentile(samples, 0.95),
                Volatile.Read(ref _effectiveBatchSize),
                Volatile.Read(ref _tokenizer)?.VocabularySize);
        }
    }

    private static double? Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 0)
        {
            return null;
        }

        var index = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private void ValidateOptions(EmbeddingGenerationOptions? options)
    {
        if (options is null)
        {
            return;
        }

        if (options.Dimensions is { } requested && requested != _preset.Dimensions)
        {
            throw new EdgeEmbeddingException(
                EdgeErrorCode.EmbeddingDimensionMismatch,
                _preset.Id,
                $"Preset '{_preset.Id}' emits a fixed {_preset.Dimensions}-dimension vector; " +
                $"{requested} was requested. Matryoshka truncation is not implemented.")
            {
                Expected = _preset.Dimensions,
                Actual = requested,
            };
        }

        if (options.ModelId is { } modelId
            && !string.Equals(modelId, _preset.Manifest.ModelId, StringComparison.Ordinal))
        {
            throw new EdgeEmbeddingException(
                EdgeErrorCode.EmbeddingPresetNotFound,
                _preset.Id,
                $"This generator serves model '{_preset.Manifest.ModelId}'; '{modelId}' was requested. " +
                "Register a second keyed generator rather than passing a different model id.");
        }
    }

    private string ApplyPrefix(string value)
    {
        var text = value ?? string.Empty;
        var prefix = _inputKind == EmbeddingInputKind.Query ? _preset.QueryPrefix : _preset.DocumentPrefix;
        return string.IsNullOrEmpty(prefix) ? text : prefix + text;
    }

    private int EffectiveBatchSize()
    {
        var configured = Math.Max(1, _options.Value.MaxBatchSize);
        var effective = configured;

        // Spec 11 names a LATCHED Moderate as the only trigger, and it is deliberately only that
        // one: Critical's reaction lives in L0, where the lifecycle observer drops sessions
        // (DropOnMemoryPressure), and halving a batch at the same moment the session underneath it
        // is being released buys nothing. The latch clears back to null on Resumed, which is what
        // makes reading LastPressure per batch cheap and stable.
        if (_options.Value.ShrinkBatchUnderMemoryPressure
            && _resources.LastPressure == EdgeMemoryPressure.Moderate)
        {
            effective = Math.Max(1, configured / 2);
        }

        if (Interlocked.Exchange(ref _effectiveBatchSize, effective) != effective && effective != configured)
        {
            s_batchShrunk(_logger, _preset.Id, configured, effective, null);
        }

        return effective;
    }

    private OnnxEmbeddingGenerator QuerySibling()
    {
        if (_inputKind == EmbeddingInputKind.Query)
        {
            return this;
        }

        var existing = Volatile.Read(ref _querySibling);
        if (existing is not null)
        {
            return existing;
        }

        var created = new OnnxEmbeddingGenerator(
            _sessionHost,
            _tokenizers,
            _resources,
            _options,
            EmbeddingInputKind.Query,
            _logger,
            _timeProvider);

        var raced = Interlocked.CompareExchange(ref _querySibling, created, null);
        if (raced is null)
        {
            return created;
        }

        created.Dispose();
        return raced;
    }

    private BatchOutcome RunBatch(OnnxSessionLease lease, IEdgeTokenizer tokenizer, string[] texts)
    {
        var batch = tokenizer.EncodeBatch(texts, _preset.MaxSequenceLength, _preset.SequenceBuckets);

        try
        {
            return RunBatchCore(lease, batch);
        }
        finally
        {
            // The three tensor buffers are rented from ArrayPool<long>.Shared (spec 11), and every
            // OrtValue built over them has been disposed by the time RunBatchCore returns or
            // throws - CreateTensorValueFromMemory pins the managed memory for the value's
            // lifetime, so returning a pinned buffer to the pool would be worse than never pooling.
            batch.ReturnBuffers();
        }
    }

    private BatchOutcome RunBatchCore(OnnxSessionLease lease, TokenizedBatch batch)
    {
        long tokenCount = 0;
        for (var b = 0; b < batch.BatchSize; b++)
        {
            tokenCount += batch.TokenCounts[b];

            if (!batch.Truncated[b])
            {
                continue;
            }

            Interlocked.Increment(ref _truncatedInputs);

            if (_options.Value.Truncation == EmbeddingTruncation.Throw)
            {
                throw new EdgeEmbeddingException(
                    EdgeErrorCode.EmbeddingInputTooLong,
                    _preset.Id,
                    $"An input exceeded the {_preset.MaxSequenceLength}-token maximum of preset " +
                    $"'{_preset.Id}' and Truncation is {nameof(EmbeddingTruncation.Throw)}.")
                {
                    Expected = _preset.MaxSequenceLength,
                    Actual = batch.TokenCounts[b],
                };
            }

            s_inputTruncated(_logger, _preset.Id, batch.TokenCounts[b], _preset.MaxSequenceLength, null);
        }

        long[] shape = [batch.BatchSize, batch.SequenceLength];
        var declared = lease.Info.Signature.InputNames;

        var names = new List<string>(3);
        var values = new List<OrtValue>(3);

        OrtValue? ids = null;
        OrtValue? mask = null;
        OrtValue? types = null;

        try
        {
            // AsMemory(0, TensorLength), never AsMemory(): the buffers are rented and Rent may hand
            // back an array longer than the batch, whose tail is the previous renter's bytes.
            ids = OrtValue.CreateTensorValueFromMemory(
                OrtMemoryInfo.DefaultInstance, batch.InputIds.AsMemory(0, batch.TensorLength), shape);
            names.Add(_preset.InputIdsName);
            values.Add(ids);

            mask = OrtValue.CreateTensorValueFromMemory(
                OrtMemoryInfo.DefaultInstance, batch.AttentionMask.AsMemory(0, batch.TensorLength), shape);
            names.Add(_preset.AttentionMaskName);
            values.Add(mask);

            // Bound only when the graph declares it. A preset that names token_type_ids against a
            // graph that has none is the UnusedTokenTypeIds fixture's whole point.
            if (_preset.TokenTypeIdsName is { } tokenTypes && Declares(declared, tokenTypes))
            {
                types = OrtValue.CreateTensorValueFromMemory(
                    OrtMemoryInfo.DefaultInstance, batch.TokenTypeIds.AsMemory(0, batch.TensorLength), shape);
                names.Add(tokenTypes);
                values.Add(types);
            }

            using var runOptions = new RunOptions();
            var started = Stopwatch.GetTimestamp();
            using var outputs = lease.Session.Run(runOptions, names, values, [_preset.OutputName]);
            var elapsed = Stopwatch.GetElapsedTime(started);

            var vectors = Pool(outputs[0], batch);

            RecordRun(elapsed);
            s_batchCompleted(_logger, _preset.Id, batch.BatchSize, batch.SequenceLength, elapsed.TotalMilliseconds, null);

            return new BatchOutcome(vectors, tokenCount);
        }
        finally
        {
            // CreateTensorValueFromMemory PINS the managed memory for the OrtValue's lifetime, so
            // every one is disposed before this method returns.
            types?.Dispose();
            mask?.Dispose();
            ids?.Dispose();
        }
    }

    private static bool Declares(IReadOnlyList<string> declared, string name)
    {
        for (var i = 0; i < declared.Count; i++)
        {
            if (string.Equals(declared[i], name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Pools one batch's output into one <c>float[Dimensions]</c> per input.
    /// </summary>
    /// <remarks>
    /// Two output ranks are accepted, and the difference is deliberate rather than incidental.
    /// A rank-3 <c>[batch, sequence, dim]</c> output is <c>last_hidden_state</c>, and pooling is
    /// OURS: mean or CLS, from <see cref="EmbeddingPreset.Pooling"/>, never a default. A rank-2
    /// <c>[batch, dim]</c> output is a graph that already pooled internally - a
    /// <c>sentence_embedding</c> / <c>pooler_output</c> head - so there is no sequence axis left to
    /// pool over and the row is taken verbatim; <see cref="EmbeddingPreset.Pooling"/> is NOT
    /// applied, because applying it would mean pooling a vector that is already one vector.
    /// <para>
    /// <b>None of the four shipped presets takes that second path</b> - spec 11 says so flatly, and
    /// <c>PresetCatalogueTests</c> pins all four to <c>last_hidden_state</c>. It exists so a
    /// consumer's own in-graph-pooled model produces a correct vector instead of a
    /// <see cref="ArgumentOutOfRangeException"/> from a stride computed for three ranks, and
    /// <c>GeneratorTests.AGraphThatPoolsInItsOwnDeclaredOutputIsUsedVerbatim</c> pins what it does.
    /// Layer-norm and L2 still run afterwards on both paths.
    /// </para>
    /// </remarks>
    /// <param name="output">The output value ORT produced.</param>
    /// <param name="batch">The batch that produced it.</param>
    /// <returns>One vector per input, in input order.</returns>
    private float[][] Pool(OrtValue output, TokenizedBatch batch)
    {
        var shape = output.GetTensorTypeAndShape().Shape;
        var dimensions = shape.Length == 0 ? 0 : (int)shape[^1];

        if (dimensions != _preset.Dimensions)
        {
            throw new EdgeEmbeddingException(
                EdgeErrorCode.EmbeddingDimensionMismatch,
                _preset.Id,
                $"The graph for '{_preset.Manifest.ModelId}' emitted a {dimensions}-wide " +
                $"'{_preset.OutputName}' but preset '{_preset.Id}' declares {_preset.Dimensions}.")
            {
                Expected = _preset.Dimensions,
                Actual = dimensions,
            };
        }

        // The span points at native memory the OrtValue owns, so every value is copied into a
        // fresh float[] before the caller's using-block disposes it.
        var data = output.GetTensorDataAsSpan<float>();

        // See the remarks above: rank 2 is a graph that pooled in its own output, which no shipped
        // preset does, and its row is taken verbatim because there is no sequence axis to pool.
        var pooledInGraph = shape.Length == 2;
        var stride = pooledInGraph ? dimensions : batch.SequenceLength * dimensions;

        var vectors = new float[batch.BatchSize][];
        for (var b = 0; b < batch.BatchSize; b++)
        {
            var vector = new float[dimensions];
            var row = data.Slice(b * stride, stride);

            if (pooledInGraph)
            {
                row.CopyTo(vector);
            }
            else if (_preset.Pooling == EmbeddingPooling.Cls)
            {
                EmbeddingPooler.ClsPool(row, batch.SequenceLength, dimensions, vector);
            }
            else
            {
                var rowMask = batch.AttentionMask.AsSpan(b * batch.SequenceLength, batch.SequenceLength);
                EmbeddingPooler.MeanPool(row, rowMask, batch.SequenceLength, dimensions, vector);
            }

            if (_preset.PostPoolLayerNorm)
            {
                EmbeddingPooler.LayerNorm(vector, _preset.LayerNormEpsilon);
            }

            if (_preset.Normalize)
            {
                EmbeddingPooler.L2Normalize(vector);
            }

            vectors[b] = vector;
        }

        return vectors;
    }

    private void RecordRun(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _batchesRun);

        lock (_statistics)
        {
            if (_runMilliseconds.Count == 256)
            {
                _runMilliseconds.RemoveAt(0);
            }

            _runMilliseconds.Add(elapsed.TotalMilliseconds);
        }
    }

    private sealed record BatchOutcome(float[][] Vectors, long TokenCount);
}

/// <summary>The rolling counters spec 14.3's embeddings block reports.</summary>
/// <param name="EmbeddingsGenerated">How many embeddings this generator has emitted.</param>
/// <param name="BatchesRun">How many ORT Runs it has issued.</param>
/// <param name="TokensEncoded">How many unpadded tokens it has encoded.</param>
/// <param name="TruncatedInputs">How many inputs were truncated.</param>
/// <param name="RunMsP50">The median Run duration, or null before the first batch.</param>
/// <param name="RunMsP95">The 95th-percentile Run duration, or null before the first batch.</param>
/// <param name="EffectiveBatchSize">The batch size the next batch will use.</param>
/// <param name="VocabularySize">The vocabulary size, or null before the tokenizer is built.</param>
internal sealed record EmbeddingDiagnosticsSnapshot(
    long EmbeddingsGenerated,
    long BatchesRun,
    long TokensEncoded,
    long TruncatedInputs,
    double? RunMsP50,
    double? RunMsP95,
    int EffectiveBatchSize,
    int? VocabularySize);
