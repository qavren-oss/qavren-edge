namespace Qavren.Edge.Chat.Internal;

/// <summary>
/// ONE cached generator per client, keyed by conversation id, so a follow-up turn skips prefill.
/// </summary>
/// <remarks>
/// <para>
/// The entry owns the generator <b>and the lease it was built under</b>: a generator outliving its
/// model is a native access violation on the next token, so the model cannot be dropped while a
/// cached generator exists. The lifecycle observer drops this cache before it unloads, in that
/// order, which is what makes the arrangement safe rather than merely usual.
/// </para>
/// <para>
/// A turn <see cref="Take"/>s the entry - whatever its id - and either <see cref="Store"/>s a new
/// one at the end or disposes what it took. <see cref="Drop"/> from another thread while a turn
/// holds the entry cannot dispose a generator that may be inside <c>GenerateNextToken</c>; it marks
/// the drop pending and the turn honours it at <see cref="Store"/>.
/// </para>
/// </remarks>
internal sealed class ConversationCache : IDisposable
{
    private readonly Lock _sync = new();
    private Entry? _entry;
    private bool _inUse;
    private bool _dropPending;
    private bool _disposed;

    /// <summary>Whether an entry is cached and not currently taken.</summary>
    public bool HasEntry
    {
        get
        {
            lock (_sync)
            {
                return _entry is not null;
            }
        }
    }

    /// <summary>The cached entry's lease, for <c>GetService</c>, or null.</summary>
    public ChatModelLease? PeekLease()
    {
        lock (_sync)
        {
            return _entry?.Lease;
        }
    }

    /// <summary>
    /// Removes and returns the cached entry, if any, marking the cache in use. The caller owns it
    /// until <see cref="Store"/> or <see cref="Release"/>.
    /// </summary>
    /// <returns>The entry, or null.</returns>
    public Entry? Take()
    {
        lock (_sync)
        {
            var entry = _entry;
            _entry = null;
            _inUse = true;
            return entry;
        }
    }

    /// <summary>Caches an entry at the end of a turn, unless a drop arrived meanwhile.</summary>
    /// <param name="entry">The entry to keep.</param>
    /// <returns><see langword="true"/> when it was kept; false when it was disposed instead.</returns>
    public bool Store(Entry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Entry? evicted;
        bool kept;

        lock (_sync)
        {
            _inUse = false;

            if (_disposed || _dropPending)
            {
                _dropPending = false;
                evicted = entry;
                kept = false;
            }
            else
            {
                evicted = _entry;
                _entry = entry;
                kept = true;
            }
        }

        evicted?.Dispose();
        return kept;
    }

    /// <summary>The turn finished without storing anything.</summary>
    public void Release()
    {
        lock (_sync)
        {
            _inUse = false;
            _dropPending = false;
        }
    }

    /// <summary>
    /// Drops the cached generator - the KV cache, hundreds of megabytes - without touching the
    /// model. Safe from any thread.
    /// </summary>
    public void Drop()
    {
        Entry? entry;

        lock (_sync)
        {
            if (_inUse)
            {
                _dropPending = true;
                return;
            }

            entry = _entry;
            _entry = null;
        }

        entry?.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }

        Drop();
    }

    /// <summary>One cached generator and everything it depends on.</summary>
    /// <param name="conversationId">The id it belongs to. Never null: a null id never hits.</param>
    /// <param name="lease">The lease the generator was built under.</param>
    /// <param name="session">The session over that lease.</param>
    /// <param name="generator">The generator holding the KV cache.</param>
    /// <param name="cachedText">
    /// The exact character sequence the generator has consumed and produced: the rendered prompt of
    /// the previous turn plus the decoded text it generated.
    /// </param>
    /// <param name="maxLength">The <c>max_length</c> the generator was built with.</param>
    public sealed class Entry(
        string conversationId,
        ChatModelLease lease,
        IChatModelSession session,
        IChatGenerator generator,
        string cachedText,
        int maxLength) : IDisposable
    {
        /// <summary>The conversation this generator holds.</summary>
        public string ConversationId { get; } = conversationId;

        /// <summary>The lease the generator was built under.</summary>
        public ChatModelLease Lease { get; } = lease;

        /// <summary>The session over that lease.</summary>
        public IChatModelSession Session { get; } = session;

        /// <summary>The generator holding the KV cache.</summary>
        public IChatGenerator Generator { get; } = generator;

        /// <summary>What the generator has consumed and produced, character for character.</summary>
        public string CachedText { get; } = cachedText;

        /// <summary>The <c>max_length</c> the generator was built with.</summary>
        public int MaxLength { get; } = maxLength;

        /// <inheritdoc />
        public void Dispose()
        {
            Generator.Dispose();
            Lease.Dispose();
        }
    }
}
