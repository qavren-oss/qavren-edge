using Qavren.Edge.Hosting;

namespace Qavren.Edge.Chat.Internal;

/// <summary>
/// Startup order 420. Loads the model, probes the chat template, and probes guidance when the
/// policy is not <see cref="EdgeGuidancePolicy.Disabled"/>. Off by default, because it forces
/// provisioning.
/// </summary>
/// <remarks>
/// It goes through <c>IChatModelHost.PreloadAsync</c> rather than <c>AcquireAsync</c>: a startup
/// task that awaited <c>IEdgeHost.Started</c> would deadlock on its own completion. Sub-project 2
/// documents that hole as unclosable from L1 because its equivalent is <c>internal</c> to another
/// assembly; sub-project 4's host and this task are in the same assembly, so sub-project 4 does not
/// inherit it.
/// <para>
/// It runs <b>no generation</b>. One token on a 1B model is 30 ms of decode on top of a 1.6 s load,
/// and nothing about the decode loop is proven by doing it twice - the guidance probe is the one
/// exception, and it exists to answer a question a version number cannot.
/// </para>
/// </remarks>
/// <param name="services">The container, so the host is resolved lazily.</param>
/// <param name="name">The registration's service key, or null for the unkeyed one.</param>
internal sealed class ChatWarmUpStartupTask(IServiceProvider services, string? name) : IEdgeStartupTask
{
    /// <inheritdoc />
    public int Order => EdgeChatStartupOrder.ChatWarmUp;

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var host = ChatServiceLocator.Host(services, name);
        await host.PreloadAsync(cancellationToken).ConfigureAwait(false);
    }
}
