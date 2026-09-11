namespace Qavren.Edge.Chat.Internal;

/// <summary>
/// One client's counters: spec section 6.6's <see cref="ChatClientStatistics"/> and section 14.3's
/// runtime-honesty block, fed by the same writes.
/// </summary>
/// <remarks>
/// Tokens-per-second samples go into a <b>bounded 64-sample ring</b> for P50/P95, so a client that
/// runs for a week does not grow a list. Every write takes the lock; every read takes a snapshot
/// under it. The diagnostics contributor treats an instance with no activity as absent - a zero
/// there would claim a turn ran and produced nothing.
/// </remarks>
internal sealed class ChatStatistics : IChatRuntimeStatistics
{
    /// <summary>The ring's capacity.</summary>
    public const int RingSize = 64;

    private readonly Lock _sync = new();
    private readonly double[] _ring = new double[RingSize];
    private int _ringCount;
    private int _ringNext;

    private int _turns;
    private int _rejectedTurns;
    private long _tokensGenerated;
    private double? _lastTokensPerSecond;
    private TimeSpan? _lastTimeToFirstToken;
    private long _peakWorkingSetBytes;
    private int _thermalThrottleEvents;
    private int _thermalAbortEvents;
    private int _terminationEvents;
    private int _historyReductions;
    private EdgeChatStopReason? _lastStopReason;

    /// <summary>Whether any turn has run or been refused.</summary>
    public bool HasActivity
    {
        get
        {
            lock (_sync)
            {
                return _turns > 0 || _rejectedTurns > 0;
            }
        }
    }

    /// <inheritdoc />
    public int Turns
    {
        get
        {
            lock (_sync)
            {
                return _turns;
            }
        }
    }

    /// <inheritdoc />
    public int RejectedTurns
    {
        get
        {
            lock (_sync)
            {
                return _rejectedTurns;
            }
        }
    }

    /// <inheritdoc />
    public long TokensGenerated
    {
        get
        {
            lock (_sync)
            {
                return _tokensGenerated;
            }
        }
    }

    /// <inheritdoc />
    public double? TokensPerSecondP50 => Percentile(0.50);

    /// <inheritdoc />
    public double? TokensPerSecondP95 => Percentile(0.95);

    /// <inheritdoc />
    public double? LastTokensPerSecond
    {
        get
        {
            lock (_sync)
            {
                return _lastTokensPerSecond;
            }
        }
    }

    /// <inheritdoc />
    public double? LastTtftMs
    {
        get
        {
            lock (_sync)
            {
                return _lastTimeToFirstToken?.TotalMilliseconds;
            }
        }
    }

    /// <inheritdoc />
    public string? LastStopReason
    {
        get
        {
            lock (_sync)
            {
                return _lastStopReason?.ToString();
            }
        }
    }

    /// <inheritdoc />
    public int HistoryReductions
    {
        get
        {
            lock (_sync)
            {
                return _historyReductions;
            }
        }
    }

    /// <inheritdoc />
    public int ThermalThrottleEvents
    {
        get
        {
            lock (_sync)
            {
                return _thermalThrottleEvents;
            }
        }
    }

    /// <inheritdoc />
    public int ThermalAbortEvents
    {
        get
        {
            lock (_sync)
            {
                return _thermalAbortEvents;
            }
        }
    }

    /// <inheritdoc />
    public int TerminationEvents
    {
        get
        {
            lock (_sync)
            {
                return _terminationEvents;
            }
        }
    }

    /// <summary>A turn was refused by the queue, the gate or a pre-flight.</summary>
    public void TurnRejected()
    {
        lock (_sync)
        {
            _rejectedTurns++;
        }
    }

    /// <summary>Folds one finished turn in.</summary>
    /// <param name="status">The turn's record.</param>
    public void TurnCompleted(ChatTurnStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        lock (_sync)
        {
            _turns++;
            _tokensGenerated += status.GeneratedTokens;
            _lastTokensPerSecond = status.TokensPerSecond;
            _lastTimeToFirstToken = status.TimeToFirstToken;
            _lastStopReason = status.StopReason;

            if (status.GeneratedTokens > 0 && status.TokensPerSecond > 0)
            {
                _ring[_ringNext] = status.TokensPerSecond;
                _ringNext = (_ringNext + 1) % RingSize;
                _ringCount = Math.Min(_ringCount + 1, RingSize);
            }

            if (status.MessagesDropped > 0)
            {
                _historyReductions++;
            }

            if (status.ThermalThrottled)
            {
                _thermalThrottleEvents++;
            }

            switch (status.StopReason)
            {
                case EdgeChatStopReason.Thermal:
                    _thermalAbortEvents++;
                    _terminationEvents++;
                    break;
                case EdgeChatStopReason.Suspended:
                case EdgeChatStopReason.MemoryPressure:
                    _terminationEvents++;
                    break;
            }

            _peakWorkingSetBytes = Math.Max(_peakWorkingSetBytes, ReadWorkingSet() ?? 0);
        }
    }

    /// <summary>The public record, as of now.</summary>
    /// <param name="unloadEvents">How many times the host dropped the model, which only the host knows.</param>
    /// <returns>The snapshot.</returns>
    public ChatClientStatistics Snapshot(int unloadEvents)
    {
        lock (_sync)
        {
            var workingSet = ReadWorkingSet();
            return new ChatClientStatistics(
                _turns,
                _rejectedTurns,
                _tokensGenerated,
                PercentileUnlocked(0.50),
                PercentileUnlocked(0.95),
                _lastTokensPerSecond,
                _lastTimeToFirstToken,
                workingSet,
                _peakWorkingSetBytes > 0 ? Math.Max(_peakWorkingSetBytes, workingSet ?? 0) : workingSet,
                _thermalThrottleEvents,
                _thermalAbortEvents,
                unloadEvents,
                _terminationEvents,
                _historyReductions,
                _lastStopReason);
        }
    }

    private double? Percentile(double fraction)
    {
        lock (_sync)
        {
            return PercentileUnlocked(fraction);
        }
    }

    private double? PercentileUnlocked(double fraction)
    {
        if (_ringCount == 0)
        {
            return null;
        }

        var sorted = new double[_ringCount];
        Array.Copy(_ring, sorted, _ringCount);
        Array.Sort(sorted);

        var rank = (int)Math.Ceiling(fraction * _ringCount) - 1;
        return sorted[Math.Clamp(rank, 0, _ringCount - 1)];
    }

    private static long? ReadWorkingSet()
    {
        try
        {
            var bytes = Environment.WorkingSet;
            return bytes > 0 ? bytes : null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }
}
