using Qavren.Edge.Chat.Internal;

namespace Qavren.Edge.Chat.Tests.Fakes;

/// <summary>
/// A chat model host that records what a lifecycle event asked it to do, with no natives and no
/// model.
/// </summary>
/// <remarks>
/// Every one of spec section 14.2's six events has to be provable without 1.241 GB of weights on
/// disk, which is why <c>ChatLifecycleObserver</c> talks to <see cref="IChatLifecycleTarget"/>
/// rather than to the concrete host. <see cref="AcquireAsync"/> and <see cref="PreloadAsync"/>
/// throw: nothing in the lifecycle path may load a model, and a fake that quietly returned one
/// would hide that.
/// </remarks>
internal sealed class FakeChatModelHost : IChatModelHost, IChatLifecycleTarget
{
    /// <summary>Completes <see cref="UnloadAsync"/>. Left pending to exercise the drain budget.</summary>
    public TaskCompletionSource<bool> UnloadCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether <see cref="UnloadAsync"/> should return immediately.</summary>
    public bool UnloadCompletesImmediately { get; set; } = true;

    /// <summary>How many times the model was asked to unload.</summary>
    public int Unloads { get; private set; }

    /// <summary>How many times the conversation cache was dropped.</summary>
    public int CacheDrops { get; private set; }

    /// <summary>How many times generation was cooperatively terminated.</summary>
    public int Terminations { get; private set; }

    /// <summary>The accepting-turns flips, in order.</summary>
    public List<bool> AcceptingTurnsChanges { get; } = [];

    /// <summary>The suspend-requested flips, in order.</summary>
    public List<bool> SuspendChanges { get; } = [];

    /// <inheritdoc />
    public bool IsAcceptingTurns { get; private set; } = true;

    /// <summary>Whether a suspend is in progress.</summary>
    public bool SuspendRequested { get; private set; }

    /// <inheritdoc />
    public ValueTask<ChatModelLease> AcquireAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("No lifecycle event may load a model.");

    /// <inheritdoc />
    public ValueTask PreloadAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("No lifecycle event may load a model.");

    /// <inheritdoc />
    public ChatModelInfo? Describe() => null;

    /// <inheritdoc />
    public Task<bool> UnloadAsync(CancellationToken cancellationToken = default)
    {
        Unloads++;
        return UnloadCompletesImmediately ? Task.FromResult(true) : UnloadCompletion.Task;
    }

    /// <inheritdoc />
    public void TerminateActiveGeneration() => Terminations++;

    /// <inheritdoc />
    public void DropConversationCache() => CacheDrops++;

    /// <inheritdoc />
    public void SetAcceptingTurns(bool accepting)
    {
        IsAcceptingTurns = accepting;
        AcceptingTurnsChanges.Add(accepting);
    }

    /// <inheritdoc />
    public void SetSuspendRequested(bool suspended)
    {
        SuspendRequested = suspended;
        SuspendChanges.Add(suspended);
    }
}

/// <summary>An <see cref="IDisposable"/> that counts its own disposals.</summary>
/// <remarks>
/// It stands in for the process-wide <c>OgaHandle</c>, which <c>ChatEnvironmentState</c> holds as an
/// <see cref="IDisposable"/> precisely so nothing on the composition path names a GenAI type.
/// </remarks>
internal sealed class CountingDisposable : IDisposable
{
    /// <summary>How many times <see cref="Dispose"/> actually ran.</summary>
    public int Disposals { get; private set; }

    /// <inheritdoc />
    public void Dispose() => Disposals++;
}
