namespace Qavren.Edge.Chat.Tests.Fakes;

/// <summary>
/// The system clock, counting the timers it is asked for. A timed wait creates its timer at the
/// moment it starts waiting, so the count is how a test knows a caller has got as far as waiting -
/// without sleeping and hoping.
/// </summary>
internal sealed class TimerCountingTimeProvider : TimeProvider
{
    private int _timers;

    /// <summary>How many timers have been created.</summary>
    public int Timers => Volatile.Read(ref _timers);

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Interlocked.Increment(ref _timers);
        return base.CreateTimer(callback, state, dueTime, period);
    }

    /// <summary>Waits, briefly and boundedly, for the count to reach <paramref name="timers"/>.</summary>
    /// <param name="timers">The count to wait for.</param>
    /// <returns>Whether it was reached.</returns>
    public bool WaitForTimers(int timers) =>
        SpinWait.SpinUntil(() => Timers >= timers, TimeSpan.FromSeconds(10));
}
