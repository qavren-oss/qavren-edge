using Microsoft.Extensions.Logging;

namespace Qavren.Edge.Chat.Tests.Fakes;

/// <summary>One log line, flattened so an assertion can read the event id and the text.</summary>
/// <param name="Level">The level it was written at.</param>
/// <param name="EventId">The event id - spec section 14.4's 900-959 range.</param>
/// <param name="Message">The formatted message.</param>
/// <param name="Exception">The exception, when one was attached.</param>
internal sealed record LogRecord(LogLevel Level, EventId EventId, string Message, Exception? Exception);

/// <summary>
/// An <see cref="ILogger"/> that keeps what it was told, so an assertion can name an event id
/// rather than a substring.
/// </summary>
/// <remarks>
/// It records at every level regardless of <see cref="MinimumLevel"/> except that
/// <see cref="IsEnabled"/> answers honestly - which is the half spec section 14.4's privacy rule is
/// asserted through: a <c>Trace</c>-declared message is not written at all when the level is
/// <c>Debug</c>.
/// </remarks>
internal sealed class RecordingLogger : ILogger
{
    /// <summary>Everything written, in order.</summary>
    public List<LogRecord> Records { get; } = [];

    /// <summary>The level below which nothing is written. Default <see cref="LogLevel.Trace"/>.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Trace;

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel >= MinimumLevel;

    /// <inheritdoc />
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

        Records.Add(new LogRecord(logLevel, eventId, formatter(state, exception), exception));
    }

    /// <summary>Whether a line with this event id was written.</summary>
    /// <param name="eventId">The numeric event id.</param>
    /// <returns><see langword="true"/> when at least one line carries it.</returns>
    public bool Logged(int eventId) => Records.Exists(r => r.EventId.Id == eventId);

    /// <summary>Every line carrying an event id.</summary>
    /// <param name="eventId">The numeric event id.</param>
    /// <returns>The matching lines.</returns>
    public IReadOnlyList<LogRecord> For(int eventId) => Records.FindAll(r => r.EventId.Id == eventId);
}

/// <summary>The generic face of <see cref="RecordingLogger"/>, for a constructor that wants one.</summary>
/// <typeparam name="T">The category type.</typeparam>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    /// <summary>The recorder every call lands in.</summary>
    public RecordingLogger Inner { get; } = new();

    /// <summary>Everything written, in order.</summary>
    public List<LogRecord> Records => Inner.Records;

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => Inner.BeginScope(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => Inner.IsEnabled(logLevel);

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Inner.Log(logLevel, eventId, state, exception, formatter);

    /// <summary>Whether a line with this event id was written.</summary>
    /// <param name="eventId">The numeric event id.</param>
    /// <returns><see langword="true"/> when at least one line carries it.</returns>
    public bool Logged(int eventId) => Inner.Logged(eventId);
}
