using Microsoft.Extensions.Logging;

namespace Qavren.Edge.Chat.Tests.Fakes;

/// <summary>
/// An <see cref="ILoggerProvider"/> for a whole container, so an assertion over a real
/// <c>AddQavrenEdge</c> composition can name an event id - "no 903 was logged" - rather than a
/// substring. Thread-safe, because <c>EdgeHost</c> runs its startup tasks on the thread pool.
/// </summary>
/// <remarks>
/// <see cref="RecordingLogger"/> is the single-logger recorder the tier-1 harnesses hand to one
/// constructor; this is the provider shape <c>services.AddLogging(b =&gt; b.AddProvider(...))</c>
/// takes, and every category lands in one list.
/// </remarks>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly Lock _sync = new();
    private readonly List<LogRecord> _records = [];

    /// <summary>Everything written so far, in order, as a snapshot.</summary>
    public IReadOnlyList<LogRecord> Records
    {
        get
        {
            lock (_sync)
            {
                return [.. _records];
            }
        }
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Sink(this);

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release.
    }

    /// <summary>Whether a line with this event id was written by any category.</summary>
    /// <param name="eventId">The numeric event id.</param>
    /// <returns><see langword="true"/> when at least one line carries it.</returns>
    public bool Logged(int eventId) => Records.Any(r => r.EventId.Id == eventId);

    /// <summary>Every line carrying an event id.</summary>
    /// <param name="eventId">The numeric event id.</param>
    /// <returns>The matching lines.</returns>
    public IReadOnlyList<LogRecord> For(int eventId) => [.. Records.Where(r => r.EventId.Id == eventId)];

    private void Add(LogRecord record)
    {
        lock (_sync)
        {
            _records.Add(record);
        }
    }

    private sealed class Sink(RecordingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (!IsEnabled(logLevel))
            {
                return;
            }

            owner.Add(new LogRecord(logLevel, eventId, formatter(state, exception), exception));
        }
    }
}
