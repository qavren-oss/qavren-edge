using Microsoft.Extensions.Logging;

namespace Qavren.Edge.Rag.Tests.Fakes;

/// <summary>One emitted log record.</summary>
/// <param name="Category">The logger category.</param>
/// <param name="Level">The level it was emitted at.</param>
/// <param name="EventId">The event id.</param>
/// <param name="Message">The formatted message.</param>
public sealed record LogRecord(string Category, LogLevel Level, EventId EventId, string Message);

/// <summary>
/// An <see cref="ILoggerFactory"/> that records everything at or above a minimum level. It is how
/// spec 14.4's Trace-only privacy rule is asserted rather than assumed: at <c>Trace</c> the private
/// strings may appear only in <c>Trace</c> records; at <c>Debug</c> they may not appear at all.
/// </summary>
public sealed class RecordingLoggerFactory : ILoggerFactory
{
    private readonly LogLevel _minimum;
    private readonly List<LogRecord> _records = [];

    /// <summary>Creates the factory.</summary>
    /// <param name="minimum">The minimum level the loggers report as enabled.</param>
    public RecordingLoggerFactory(LogLevel minimum) => _minimum = minimum;

    /// <summary>Everything logged so far.</summary>
    public IReadOnlyList<LogRecord> Records
    {
        get
        {
            lock (_records)
            {
                return [.. _records];
            }
        }
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName, _minimum);

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
        // Nothing to add: this factory is the provider.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release.
    }

    internal void Add(LogRecord record)
    {
        lock (_records)
        {
            _records.Add(record);
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly RecordingLoggerFactory _factory;
        private readonly string _category;
        private readonly LogLevel _minimum;

        internal RecordingLogger(RecordingLoggerFactory factory, string category, LogLevel minimum)
        {
            _factory = factory;
            _category = category;
            _minimum = minimum;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimum && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);
            _factory.Add(new LogRecord(_category, logLevel, eventId, formatter(state, exception)));
        }
    }
}
