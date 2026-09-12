using Qavren.Edge.Hosting;

namespace Qavren.Edge.Chat.Internal;

/// <summary>
/// Startup order 410. Verifies presence and faults startup when the model is absent. Off by
/// default, and it <b>never downloads</b>.
/// </summary>
/// <remarks>
/// Blocking <c>IEdgeHost.Started</c> on a 1.241 GB transfer also blocks every
/// <c>IEdgeDatabase.OpenConnectionAsync</c>, because sub-project 1's contract is that every entry
/// point awaits startup first. So this task asks a question and never answers it by fetching.
/// </remarks>
/// <param name="services">The container, so the provisioner is resolved lazily.</param>
/// <param name="name">The registration's service key, or null for the unkeyed one.</param>
internal sealed class ChatProvisioningStartupTask(IServiceProvider services, string? name) : IEdgeStartupTask
{
    /// <inheritdoc />
    public int Order => EdgeChatStartupOrder.ChatModelProvisioning;

    /// <inheritdoc />
    public Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var provisioner = ChatServiceLocator.Provisioner(services, name);
        if (provisioner.IsProvisioned)
        {
            return Task.CompletedTask;
        }

        var plan = provisioner.Plan();

        throw new EdgeChatException(
            EdgeErrorCode.ChatModelNotProvisioned,
            $"RequireChatModelAtStartup is registered for '{plan.PresetId}' and its " +
            $"{plan.TotalBytes}-byte bundle is not on disk at '{plan.Directory}'. Startup will " +
            "not fetch it: that would block every IEdgeDatabase.OpenConnectionAsync behind a " +
            "gigabyte of transfer.")
        {
            PresetId = plan.PresetId,
            RequiredBytes = plan.BytesToTransfer,
            Remediation =
                "Call IChatModelProvisioner.Plan(), show the user the size and the licence, and " +
                "call ProvisionAsync() - then restart, or drop RequireChatModelAtStartup and let " +
                "the first turn load lazily.",
        };
    }
}
