using System.Collections.Concurrent;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Ingestion.Internal;
using Qavren.Edge.Sqlite;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// A hand-rolled <see cref="TimeProvider"/> with manual advance. The repo pins no
/// <c>Microsoft.Extensions.TimeProvider.Testing</c> and adding one would be a root change, so the
/// forty lines live here.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private readonly Lock _gate = new();
    private DateTimeOffset _now = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now.UtcTicks;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by)
    {
        ManualTimer[] due;
        lock (_gate)
        {
            _now += by;
            due = [.. _timers.Where(t => t.IsDue(_now))];
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, GetUtcNow() + dueTime);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(
        ManualTimeProvider owner, TimerCallback callback, object? state, DateTimeOffset due) : ITimer
    {
        private DateTimeOffset _due = due;
        private bool _fired;

        public bool IsDue(DateTimeOffset now) => !_fired && now >= _due;

        public void Fire()
        {
            _fired = true;
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _due = owner.GetUtcNow() + dueTime;
            _fired = false;
            return true;
        }

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// A whitespace tokenizer shaped like <see cref="ChunkModelProfile.MiniLmL6V2Int8"/>: a 256-token
/// ceiling and an overhead of 2, so <c>ChunkOptions.Resolve</c> produces the real 222 / 32 / 27.
/// </summary>
public sealed class FakeChunkTokenizer(int maxSequenceLength = 256, int specialTokenOverhead = 2)
    : IChunkTokenizer
{
    public string Id => "fake-whitespace";

    public int MaxSequenceLength => maxSequenceLength;

    public int SpecialTokenOverhead => specialTokenOverhead;

    public int CountTokens(ReadOnlySpan<char> text)
    {
        var count = 0;
        var inWord = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                inWord = false;
                continue;
            }

            if (!inWord)
            {
                count++;
                inWord = true;
            }
        }

        return count;
    }

    public int IndexByTokenCount(string text, int maxTokens, out int tokenCount)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TokenIndexSearch.Find(CountTokens, text, maxTokens, out tokenCount);
    }
}

/// <summary>Records every <c>GenerateAsync</c> call so a test can count them and read their inputs.</summary>
public sealed class RecordingEmbeddingGenerator(int dimensions = 384, bool publishUsage = false)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly ConcurrentQueue<IReadOnlyList<string>> _calls = new();

    public IReadOnlyList<IReadOnlyList<string>> Calls => [.. _calls];

    public int CallCount => _calls.Count;

    /// <summary>When set, the next call throws it once and then clears.</summary>
    public Exception? FailOnce { get; set; }

    /// <summary>When set, every call throws it.</summary>
    public Exception? FailAlways { get; set; }

    /// <summary>Invoked before each call, so a test can record ordering against the collection.</summary>
    public Action? OnCall { get; set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var inputs = values.ToList();
        _calls.Enqueue(inputs);
        OnCall?.Invoke();

        if (FailAlways is { } always)
        {
            throw always;
        }

        if (FailOnce is { } once)
        {
            FailOnce = null;
            throw once;
        }

        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in inputs)
        {
            var vector = new float[dimensions];
            var seed = value.Length == 0 ? 1 : value[0];
            for (var i = 0; i < dimensions; i++)
            {
                vector[i] = (float)Math.Sin((seed + i) * 0.001);
            }

            result.Add(new Embedding<float>(vector));
        }

        if (publishUsage)
        {
            result.Usage = new UsageDetails { InputTokenCount = inputs.Sum(v => v.Length) };
        }

        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType == typeof(EmbeddingGeneratorMetadata)
            ? new EmbeddingGeneratorMetadata("recording", defaultModelDimensions: dimensions)
            : serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// An <see cref="IEdgeDatabase"/> decorator that records every open and every transaction, and
/// <b>fails if an open happens inside a transaction callback</b> — the spec 9.5 invariant, asserted
/// rather than documented.
/// </summary>
public sealed class SpyEdgeDatabase(IEdgeDatabase inner) : IEdgeDatabase
{
    private readonly AsyncLocal<bool> _insideTransaction = new();

    public int OpenCount { get; private set; }

    public int TransactionCount { get; private set; }

    public bool NestedOpenObserved { get; private set; }

    /// <summary>An ordered sink a test shares with the generator, to assert call ORDER.</summary>
    public Action<string>? OnEvent { get; set; }

    public string Name => inner.Name;

    public string Path => inner.Path;

    public bool IsEncrypted => inner.IsEncrypted;

    public ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        OpenCount++;
        OnEvent?.Invoke("open");
        if (_insideTransaction.Value)
        {
            NestedOpenObserved = true;
        }

        return inner.OpenConnectionAsync(cancellationToken);
    }

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        TransactionCount++;
        OnEvent?.Invoke("tx");
        var previous = _insideTransaction.Value;
        _insideTransaction.Value = true;
        try
        {
            return await inner.ExecuteInTransactionAsync(work, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _insideTransaction.Value = previous;
        }
    }

    public Task<SqliteDatabaseInfo> GetInfoAsync(CancellationToken cancellationToken = default) =>
        inner.GetInfoAsync(cancellationToken);

    public Task<SqliteCheckResult> CheckAsync(CancellationToken cancellationToken = default) =>
        inner.CheckAsync(cancellationToken);

    public Task RekeyAsync(SqliteKey newKey, CancellationToken cancellationToken = default) =>
        inner.RekeyAsync(newKey, cancellationToken);

    public Task CheckpointAsync(CancellationToken cancellationToken = default) =>
        inner.CheckpointAsync(cancellationToken);
}

/// <summary>An <see cref="IEdgeDatabase"/> that throws the moment anything opens a connection.</summary>
public sealed class RefusingEdgeDatabase : IEdgeDatabase
{
    public int OpenAttempts { get; private set; }

    public string Name => "(refusing)";

    public string Path => ":memory:";

    public bool IsEncrypted => false;

    public ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        OpenAttempts++;
        throw new InvalidOperationException("Startup must not open a database.");
    }

    public Task<T> ExecuteInTransactionAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        OpenAttempts++;
        throw new InvalidOperationException("Startup must not open a database.");
    }

    public Task<SqliteDatabaseInfo> GetInfoAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<SqliteCheckResult> CheckAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task RekeyAsync(SqliteKey newKey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task CheckpointAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>A source over in-memory documents that counts every <c>OpenAsync</c> per document.</summary>
public sealed class RecordingSource : IngestionSource
{
    private readonly List<(string DocumentId, string MediaType, byte[] Bytes)> _items = [];
    private readonly ConcurrentDictionary<string, int> _opens = new(StringComparer.Ordinal);

    public RecordingSource(string id = "memory") => Id = id;

    public override string Id { get; }

    public IReadOnlyDictionary<string, int> Opens => _opens;

    public RecordingSource Add(string documentId, string text, string mediaType = IngestionMediaTypes.PlainText)
    {
        _items.Add((documentId, mediaType, Encoding.UTF8.GetBytes(text)));
        return this;
    }

    public RecordingSource Replace(string documentId, string text)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (string.Equals(_items[i].DocumentId, documentId, StringComparison.Ordinal))
            {
                _items[i] = (documentId, _items[i].MediaType, Encoding.UTF8.GetBytes(text));
            }
        }

        return this;
    }

    public RecordingSource Remove(string documentId)
    {
        _items.RemoveAll(i => string.Equals(i.DocumentId, documentId, StringComparison.Ordinal));
        return this;
    }

    public void ResetOpens() => _opens.Clear();

#pragma warning disable CS1998
    public override async IAsyncEnumerable<DocumentSourceItem> EnumerateAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var (documentId, mediaType, bytes) in _items.ToList())
        {
            ct.ThrowIfCancellationRequested();
            yield return new DocumentSourceItem(
                documentId,
                mediaType,
                _ =>
                {
                    _opens.AddOrUpdate(documentId, 1, (_, n) => n + 1);
                    return new ValueTask<Stream>(new MemoryStream(bytes, writable: false));
                })
            {
                SizeBytes = bytes.Length,
                LastModifiedUtc = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero),
            };
        }
    }
#pragma warning restore CS1998
}

/// <summary>An extractor that always throws whatever it was handed.</summary>
public sealed class ThrowingExtractor(Exception fault, string id = "throwing") : IDocumentExtractor
{
    public string Id => id;

    public int Version => 1;

    public IReadOnlyList<string> Extensions => [".txt", ".md"];

    public IReadOnlyList<string> MediaTypes => [IngestionMediaTypes.PlainText, IngestionMediaTypes.Markdown];

    public bool CanExtract(DocumentSourceItem item) => true;

    public ValueTask<ExtractedDocument> ExtractAsync(
        DocumentSourceItem item, ExtractionContext context, CancellationToken cancellationToken) =>
        throw fault;
}

/// <summary>
/// An extractor for a format nothing in the runtime corpus uses, so it is registered but never
/// selected. It exists to prove that its presence — and its version — leaves a text corpus's recipe
/// hash alone, which is spec 9.2's per-document fingerprint stated as a test.
/// </summary>
public sealed class WidgetExtractor(string id = "widget", int version = 1) : IDocumentExtractor
{
    public string Id => id;

    public int Version => version;

    public IReadOnlyList<string> Extensions => [".widget"];

    public IReadOnlyList<string> MediaTypes => ["application/x-widget"];

    public bool CanExtract(DocumentSourceItem item) => true;

    public ValueTask<ExtractedDocument> ExtractAsync(
        DocumentSourceItem item, ExtractionContext context, CancellationToken cancellationToken) =>
        throw new NotSupportedException("WidgetExtractor is never selected.");
}

/// <summary>Captures every log line so a test can assert what is - and is not - in them.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => new Capturing(_lines);

    public void Dispose() => GC.SuppressFinalize(this);

    private sealed class Capturing(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lines.Enqueue($"{eventId.Id}|{formatter(state, exception)}");
        }
    }
}
