namespace Qavren.Edge.Chat.Tests.Fakes;

/// <summary>
/// A clock a test advances by hand, so the one-second thermal sampling window and the turn-queue
/// timeout are asserted rather than slept through.
/// </summary>
internal sealed class FixedTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public FixedTimeProvider()
        : this(new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan amount) => _now += amount;
}
