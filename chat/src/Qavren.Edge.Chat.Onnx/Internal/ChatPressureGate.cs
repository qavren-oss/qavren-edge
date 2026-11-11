using Qavren.Edge.Onnx;

namespace Qavren.Edge.Chat.Internal;

/// <summary>
/// Whether a memory-pressure event still stops new turns, and new model loads.
/// </summary>
/// <remarks>
/// <para>
/// No platform sends "the pressure is over". The only event that used to lift the pause was
/// <c>Resumed</c>, which follows a trip to the background - and an iOS memory warning arrives in the
/// <b>foreground</b>, usually while a model is loading. The app shed its caches, carried on, and
/// every turn for the rest of the session was refused with 7105 "retry after the app is resumed",
/// which a user can only satisfy by leaving the app and coming back.
/// </para>
/// <para>
/// So the pause now ends on its own, on the first read that finds BOTH that
/// <see cref="Cooldown"/> has passed since the last pressure event and that the device does not
/// report itself low on memory. It does not decide that there is room for a model: the memory
/// budget does that, against a fresh reading, when the next turn loads one.
/// </para>
/// </remarks>
/// <param name="monitor">The device resource monitor.</param>
/// <param name="timeProvider">The clock; a test advances it by hand.</param>
internal sealed class ChatPressureGate(IEdgeResourceMonitor monitor, TimeProvider timeProvider)
{
    /// <summary>
    /// How long after a pressure event turns stay off whatever the device says. Long enough for the
    /// app, and the OS, to finish shedding what the warning asked for; short enough that a user who
    /// waits for the reply box to come back sees it come back.
    /// </summary>
    internal static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    private static readonly Pause s_relieved = new(DateTimeOffset.MinValue);

    // null: no pressure since the last Resumed. s_relieved: there was, and it has passed.
    private Pause? _pause;

    /// <summary>A pressure event arrived. Restarts the cool-down if one was already running.</summary>
    public void OnPressure() => Volatile.Write(ref _pause, new Pause(timeProvider.GetUtcNow()));

    /// <summary><c>Resumed</c>: the pause is over and so is the record that there was one.</summary>
    public void Clear() => Volatile.Write(ref _pause, null);

    /// <summary>Whether new turns are still held back.</summary>
    public bool IsPaused => Evaluate() is { } pause && !ReferenceEquals(pause, s_relieved);

    /// <summary>
    /// Whether a pressure event was seen and has since passed - as opposed to never having been
    /// seen at all. The load path asks this before overriding the monitor's critical latch, which
    /// only <c>Resumed</c> clears: a latch this gate never saw set is not this gate's to ignore.
    /// </summary>
    public bool IsRelieved => ReferenceEquals(Evaluate(), s_relieved);

    private Pause? Evaluate()
    {
        var pause = Volatile.Read(ref _pause);
        if (pause is null || ReferenceEquals(pause, s_relieved))
        {
            return pause;
        }

        if (timeProvider.GetUtcNow() - pause.At < Cooldown || monitor.Read().IsLowMemory == true)
        {
            return pause;
        }

        // Compare-and-swap, so a pressure event that landed while this was being decided restarts
        // the cool-down rather than being erased by it.
        var seen = Interlocked.CompareExchange(ref _pause, s_relieved, pause);
        return ReferenceEquals(seen, pause) ? s_relieved : seen;
    }

    private sealed class Pause(DateTimeOffset at)
    {
        public DateTimeOffset At { get; } = at;
    }
}
