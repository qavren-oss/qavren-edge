using Qavren.Edge.Hosting;

namespace Qavren.Edge.Chat.Internal;

/// <summary>
/// Startup order 420. Loads the model, probes the chat template, and probes guidance when the
/// policy is not <see cref="EdgeGuidancePolicy.Disabled"/>. Off by default, because it forces
/// provisioning.
/// </summary>
/// <remarks>
/// It goes through <c>IChatModelHost.PreloadAsync</c>, which loads the model and hands no lease
/// back. <c>AcquireAsync</c> would not deadlock here either: <c>IEdgeHost.EnsureStartedAsync</c>
/// returns at once when a startup task calls it.
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
