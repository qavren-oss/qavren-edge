namespace Qavren.Edge.Chat.Tests.Fakes;

/// <summary>
/// A clock a test advances by hand, so the one-second thermal sampling window and the turn-queue
/// timeout are asserted rather than slept through.
/// </summary>
/// <remarks>
/// It keeps the two clocks a <see cref="TimeProvider"/> has apart, as a device does: the wall clock
/// (<see cref="GetUtcNow"/>) and the monotonic timestamp intervals are measured on
/// (<see cref="GetTimestamp"/>). <see cref="Advance"/> moves both, as real time passing does;
/// <see cref="StepWallClock"/> moves only the wall clock, as NTP or a user changing the time does.
/// Timers are still the system's: <c>Task.Delay</c> through this provider waits in real time.
/// </remarks>
internal sealed class FixedTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _now = start;
    private long _timestamp;

    public FixedTimeProvider()
        : this(new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero))
    {
    }

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
            return _timestamp;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Time passes: both clocks move.</summary>
    public void Advance(TimeSpan amount)
    {
        lock (_gate)
        {
            _now += amount;
            _timestamp += amount.Ticks;
        }
    }

    /// <summary>The wall clock is set, either way, and no time passes.</summary>
    public void StepWallClock(TimeSpan amount)
    {
        lock (_gate)
        {
            _now += amount;
        }
    }
}
