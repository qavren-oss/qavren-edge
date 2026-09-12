using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qavren.Edge.Lifecycle;

namespace Qavren.Edge.Chat.Internal;

/// <summary>
/// What a lifecycle event can do to one registration's model. Deliberately narrower than
/// <see cref="IChatModelHost"/>, and deliberately an interface: the tier-1 lifecycle assertions run
/// against a fake, because every one of these six events has to be provable without 1.241 GB of
/// weights on disk.
/// </summary>
internal interface IChatLifecycleTarget
{
    /// <summary>Turns new turns off, or back on.</summary>
    /// <param name="accepting">Whether turns are accepted.</param>
    void SetAcceptingTurns(bool accepting);

    /// <summary>The cooperative abort: the abort flag plus <c>terminate_session</c>.</summary>
    void TerminateActiveGeneration();

    /// <summary>
    /// Drops the cached <c>Generator</c> - the whole KV cache, up to hundreds of megabytes -
    /// without touching the model and without paying a reload.
    /// </summary>
    void DropConversationCache();

    /// <summary>Latches "the OS is suspending us", so the decode loop can stop honestly.</summary>
    /// <param name="suspended">Whether a suspend is in progress.</param>
    void SetSuspendRequested(bool suspended);

    /// <summary>Marks the model for drop and disposes it once the last lease returns.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><see langword="true"/> when a loaded model was marked or disposed.</returns>
    Task<bool> UnloadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Spec section 14.2's chat observer. One per <c>AddOnnxChat</c> registration.</summary>
/// <remarks>
/// It resolves its target <b>lazily</b> out of <see cref="IServiceProvider"/> rather than taking it
/// in the constructor: <c>EdgeLifecycleHub</c> materialises every observer in its own constructor
/// and the model host depends on <c>IEdgeHost</c>, so a constructor dependency would be a DI cycle.
/// Sub-project 2 hit this exact wall, and its observer is registered first, so ORT sessions drop
/// before chat models - the right order, because the embedding session is the cheaper one to
/// rebuild.
/// </remarks>
internal sealed class ChatLifecycleObserver(
    IServiceProvider services,
    ChatEnvironmentState environment,
    IOptions<EdgeGenAiOptions> genAiOptions,
    ChatRegistration registration,
    ILogger<ChatLifecycleObserver> logger) : EdgeLifecycleObserver
{
    /// <summary>
    /// How long the drain may hold an OS memory warning. Sub-project 2's number and sub-project 2's
    /// reason: the hub awaits its observers, and an iOS memory warning is not a place to block.
    /// </summary>
    internal static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(2);

    private static readonly Action<ILogger, Exception?> s_runtimeShutdown =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(EdgeChatEventIds.GenAiRuntimeShutdown, nameof(EdgeChatEventIds.GenAiRuntimeShutdown)),
            "ORT GenAI runtime handle disposed (OgaShutdown). ORT's own OrtEnv is deliberately left " +
            "alone: it is a process-wide singleton with a one-shot options hook.");

    /// <inheritdoc />
    /// <remarks>
    /// <c>Low</c> does nothing at all - sub-project 2's observer latches it and sub-project 4 reads
    /// the latch. <c>Moderate</c> stops accepting turns and drops the KV cache. <c>Critical</c>
    /// terminates first, so a multi-second prefill aborts rather than running to completion into a
    /// kill, and only then races the unload against the drain budget.
    /// </remarks>
    public override async Task OnMemoryPressureAsync(
        EdgeMemoryPressure level,
        CancellationToken cancellationToken)
    {
        if (level == EdgeMemoryPressure.Low || TryGetTarget() is not { } target)
        {
            return;
        }

        if (level == EdgeMemoryPressure.Moderate)
        {
            target.SetAcceptingTurns(false);
            target.DropConversationCache();
            return;
        }

        // Critical. Terminate BEFORE anything else: it is the only thing that can stop a prefill.
        target.SetAcceptingTurns(false);
        target.TerminateActiveGeneration();
        target.DropConversationCache();

        // DropOnMemoryPressure = false still terminates. It only spares the weights.
        if (!registration.Options.DropOnMemoryPressure)
        {
            return;
        }

        var unload = target.UnloadAsync(cancellationToken);
        await Task.WhenAny(unload, Task.Delay(DrainBudget, cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stops the in-flight turn at the next token boundary; the partial text reaches the caller
    /// with the real counts. Unloads only when <c>UnloadOnSleeping</c>, which is <b>false</b> by
    /// default: an app backgrounded for two seconds should not pay a 1.6 s reload.
    /// </remarks>
    public override async Task OnSleepingAsync(CancellationToken cancellationToken)
    {
        if (TryGetTarget() is not { } target)
        {
            return;
        }

        target.SetSuspendRequested(true);
        target.TerminateActiveGeneration();

        if (registration.Options.DropConversationCacheOnSleep)
        {
            target.DropConversationCache();
        }

        if (registration.Options.UnloadOnSleeping)
        {
            await target.UnloadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Nothing is pre-warmed. A resume that immediately reloads 1.241 GB is a resume that stutters.
    /// </remarks>
    public override Task OnResumedAsync(CancellationToken cancellationToken)
    {
        if (TryGetTarget() is { } target)
        {
            target.SetSuspendRequested(false);
            target.SetAcceptingTurns(true);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Terminate, unload unconditionally, then dispose the <c>OgaHandle</c> exactly once behind an
    /// <c>Interlocked</c> guard - however many registrations there are, and therefore however many
    /// of these observers the hub runs. ORT's <c>OrtEnv</c> is <b>not</b> disposed.
    /// </remarks>
    public override async Task OnStoppingAsync(CancellationToken cancellationToken)
    {
        if (TryGetTarget() is { } target)
        {
            target.SetAcceptingTurns(false);
            target.TerminateActiveGeneration();
            target.DropConversationCache();
            await target.UnloadAsync(cancellationToken).ConfigureAwait(false);
        }

        if (genAiOptions.Value.ShutdownOnStopping && environment.ShutdownRuntime())
        {
            s_runtimeShutdown(logger, null);
        }
    }

    /// <summary>
    /// Null when nothing is registered under this key, or when the container is already being torn
    /// down - a shutdown race is not a reason to throw out of a lifecycle observer.
    /// </summary>
    private IChatLifecycleTarget? TryGetTarget()
    {
        try
        {
            return registration.Name is null
                ? services.GetService<IChatLifecycleTarget>()
                : services.GetKeyedService<IChatLifecycleTarget>(registration.Name);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }
}
