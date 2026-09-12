using Microsoft.ML.OnnxRuntimeGenAI;
using Xunit;

namespace Qavren.Edge.Chat.Tests.Tier2;

/// <summary>
/// Spec 16.2's first bullet, over real natives: the process-wide handle constructs and disposes,
/// telemetry can be switched off, and - the 0.15.0 re-initialisation claim, proved for a
/// <b>model</b> here where Environment ground truth proved it for the handle alone - a fresh load
/// after <c>OgaShutdown</c> succeeds.
/// </summary>
[Collection(TinyChatModelCollectionDefinition.Name)]
public sealed class GenAiRuntimeTests(TinyChatModelFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void OgaHandleConstructsAndDisposesAndDisableTelemetryEventsDoesNotThrow()
    {
        using (var handle = new OgaHandle())
        {
            Assert.NotNull(handle);
            Utils.DisableTelemetryEvents();
        }

        // A second handle after the first one's OgaShutdown: the marker is re-creatable.
        using var again = new OgaHandle();
        Assert.NotNull(again);
    }

    [Fact]
    public async Task AFreshLoadAfterOgaShutdownSucceeds()
    {
        // Nothing of ours may be live across the shutdown: the container's host is unloaded first
        // (every other test disposes its own host), then the runtime is shut down, then a real
        // model is loaded through the ordinary load path.
        await fixture.ContainerHost.UnloadAsync(Token).ConfigureAwait(true);

        using (new OgaHandle())
        {
            // Disposing calls OgaShutdown(), which returns GenAI to a just-loaded state.
        }

        using var host = fixture.NewHost();
        await host.Host.PreloadAsync(Token).ConfigureAwait(true);

        var info = host.Host.Describe();
        Assert.NotNull(info);
        Assert.True(info.IsLoaded);
        Assert.Equal(TinyChatModelFixture.ContextLength, info.Shape.ContextLength);
        Assert.True(host.Logs.Logged(EdgeChatEventIds.ChatModelLoaded));
    }
}
