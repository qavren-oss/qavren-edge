using System.Text;

namespace Qavren.Edge.Chat.Internal;

/// <summary>
/// Matches stop sequences over a <b>rolling decoded-character buffer</b>, so a stop string that
/// arrives as several tokens - <c>&lt;|eot_id|&gt;</c> as three fragments - actually fires.
/// </summary>
/// <remarks>
/// <para>
/// Upstream compares each decoded token against each stop string by equality, so a multi-token
/// stop never fires there. This matcher keeps back any suffix of the accumulated text that could
/// still be the start of a stop sequence, emits everything before it, and holds at most
/// <c>longest - 1</c> characters - which is what "sized to the longest configured stop string"
/// means in practice.
/// </para>
/// <para>
/// The stop set is longest-first (see <c>EdgeChatOptions.ComposeStopSequences</c>), and the match
/// reported is the <b>earliest</b> one; at one position the longest wins, and a shorter stop is not
/// reported while a longer one starting at the same position could still complete.
/// </para>
/// </remarks>
internal sealed class StopSequenceMatcher
{
    private readonly IReadOnlyList<string> _stops;
    private readonly StringBuilder _pending = new();

    /// <summary>Creates the matcher.</summary>
    /// <param name="stops">The effective stop set, longest-first. Empty disables matching.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stops"/> is null.</exception>
    public StopSequenceMatcher(IReadOnlyList<string> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        _stops = stops;

        var longest = 0;
        foreach (var stop in stops)
        {
            longest = Math.Max(longest, stop.Length);
        }

        Longest = longest;
    }

    /// <summary>The longest configured stop string, which bounds the buffer.</summary>
    public int Longest { get; }

    /// <summary>The characters currently held back. Trace-only when logged: it is completion text.</summary>
    public string Pending => _pending.ToString();

    /// <summary>Feeds one decoded fragment.</summary>
    /// <param name="fragment">The text the last token completed.</param>
    /// <param name="matched">Whether a stop sequence fired.</param>
    /// <returns>
    /// The text safe to emit: everything before the stop when one fired, otherwise everything that
    /// cannot be the start of one.
    /// </returns>
    public string Push(string fragment, out bool matched)
    {
        _pending.Append(fragment);

        if (_stops.Count == 0)
        {
            matched = false;
            var all = _pending.ToString();
            _pending.Clear();
            return all;
        }

        var text = _pending.ToString();

        if (TryFindEarliest(text, out var index, out var stop) && !CouldExtend(text, index, stop))
        {
            matched = true;
            _pending.Clear();
            return text[..index];
        }

        matched = false;
        var hold = index >= 0 ? text.Length - index : HoldBack(text);
        var emit = text[..^hold];

        _pending.Clear();
        _pending.Append(text, text.Length - hold, hold);
        return emit;
    }

    /// <summary>The stream ended: releases what was held back, checking it one last time.</summary>
    /// <param name="matched">Whether the held text is itself a complete stop sequence.</param>
    /// <returns>The text to emit.</returns>
    public string Finish(out bool matched)
    {
        var text = _pending.ToString();
        _pending.Clear();

        if (TryFindEarliest(text, out var index, out _))
        {
            matched = true;
            return text[..index];
        }

        matched = false;
        return text;
    }

    private bool TryFindEarliest(string text, out int index, out string stop)
    {
        index = -1;
        stop = string.Empty;

        // Longest-first, strict "<": at one position the first (longest) stop found stays.
        foreach (var candidate in _stops)
        {
            var at = text.IndexOf(candidate, StringComparison.Ordinal);
            if (at >= 0 && (index < 0 || at < index))
            {
                index = at;
                stop = candidate;
            }
        }

        return index >= 0;
    }

    /// <summary>
    /// Whether a longer stop starting at the same position could still complete, in which case
    /// the shorter match waits rather than firing.
    /// </summary>
    private bool CouldExtend(string text, int index, string stop)
    {
        var remaining = text.AsSpan(index);
        foreach (var candidate in _stops)
        {
            if (candidate.Length > stop.Length
                && remaining.Length < candidate.Length
                && candidate.AsSpan(0, remaining.Length).SequenceEqual(remaining))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The longest suffix of <paramref name="text"/> that is a proper prefix of a stop.</summary>
    private int HoldBack(string text)
    {
        var hold = 0;
        foreach (var stop in _stops)
        {
            var max = Math.Min(stop.Length - 1, text.Length);
            for (var length = max; length > hold; length--)
            {
                if (text.AsSpan(text.Length - length).SequenceEqual(stop.AsSpan(0, length)))
                {
                    hold = length;
                    break;
                }
            }
        }

        return hold;
    }
}
