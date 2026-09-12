using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML.OnnxRuntimeGenAI;
using Qavren.Edge.Lifecycle;
using Xunit;

namespace Qavren.Edge.Chat.Tests.Tier2;

/// <summary>
/// Spec 16.2: a second concurrent turn queues and a fifth gets 7105; unload with a live lease
/// defers disposal until the lease returns and the process does not crash (the access-violation
/// guard); and a simulated <c>MemoryPressure(Critical)</c> through sub-project 1's real lifecycle
/// hub moves the latch, terminates the turn, unloads the model, and the next acquire reloads it -
/// all against real natives.
/// </summary>
[Collection(TinyChatModelCollectionDefinition.Name)]
public sealed class ConcurrencyAndUnloadTests(TinyChatModelFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ChatMessage[] Ask(string text = "hi") => [new ChatMessage(ChatRole.User, text)];

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(5, Token).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task ASecondConcurrentTurnQueuesAndAFifthGets7105()
    {
        // The first turn is held INSIDE the gate by a prompt formatter that blocks until released:
        // formatting runs after the model is borrowed and before a generator exists, which is the
        // only place a real native turn can be parked deterministically.
        using var release = new ManualResetEventSlim(initialState: false);
        var blockedOnce = 0;

        using var client = fixture.NewClient(o =>
        {
            o.MaxQueuedTurns = 3;
            o.EnableConversationCache = false;
            o.MaxOutputTokens = 4;
            o.PromptFormatter = (messages, _, _) =>
            {
                if (Interlocked.Exchange(ref blockedOnce, 1) == 0)
                {
                    release.Wait(TimeSpan.FromSeconds(30));
                }

                return "<|user|>\n" + messages[^1].Text + "\n<|assistant|>\n";
            };
        });

        try
        {
            var first = client.Client.GetResponseAsync(Ask(), null, Token);
            await WaitUntilAsync(() => client.Client.GateEntries == 1, "the first turn to take the gate").ConfigureAwait(true);

            var second = client.Client.GetResponseAsync(Ask(), null, Token);
            var third = client.Client.GetResponseAsync(Ask(), null, Token);
            var fourth = client.Client.GetResponseAsync(Ask(), null, Token);
            await WaitUntilAsync(() => client.Client.QueuedTurns == 3, "three turns to queue").ConfigureAwait(true);

            // Queued, not running: no second generator, no second KV cache.
            Assert.Equal(0, client.Sessions.GeneratorsBuilt);

            var refused = await Assert.ThrowsAsync<EdgeChatException>(
                () => client.Client.GetResponseAsync(Ask(), null, Token)).ConfigureAwait(true);

            Assert.Equal(EdgeErrorCode.ChatBusy, refused.Code);
            Assert.Contains("MaxQueuedTurns", refused.Message, StringComparison.Ordinal);
            Assert.True(client.Logs.Logged(EdgeChatEventIds.TurnQueued));
            Assert.True(client.Logs.Logged(EdgeChatEventIds.TurnRejected));

            release.Set();

            var responses = await Task.WhenAll(first, second, third, fourth).ConfigureAwait(true);
            Assert.All(responses, r => Assert.Equal(4, r.GetTurnStatus()!.GeneratedTokens));
            Assert.Equal(4, client.Sessions.GeneratorsBuilt);
            Assert.Equal(4, client.Client.Statistics.Turns);
            Assert.Equal(1, client.Client.Statistics.RejectedTurns);
            Assert.Equal(0, client.Client.QueuedTurns);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task UnloadWithALiveLeaseDoesNotDisposeUntilTheLeaseReturnsAndTheProcessSurvives()
    {
        using var host = fixture.NewHost();

        var lease = await host.Host.AcquireAsync(Token).ConfigureAwait(true);
        var loadCount = host.Host.Describe()!.LoadCount;

        Assert.True(await host.Host.UnloadAsync(Token).ConfigureAwait(true));

        // Marked, not disposed: the lease is live.
        var marked = host.Host.Describe()!;
        Assert.True(marked.IsLoaded);
        Assert.Equal(1, marked.ActiveLeases);
        Assert.False(host.Logs.Logged(EdgeChatEventIds.ChatModelDropped));

        // The access-violation guard: the model is still usable under the lease. Disposing it
        // under a live Generator would be a native access violation, and this is the assertion
        // that it was not disposed.
        using (var prompt = lease.Tokenizer.Encode("hi"))
        using (var parameters = new GeneratorParams(lease.Model))
        {
            parameters.SetSearchOption("max_length", prompt[0].Length + 4);
            parameters.SetSearchOption("do_sample", false);

            using var generator = new Generator(lease.Model, parameters);
            generator.AppendTokenSequences(prompt);
            for (var i = 0; i < 4 && !generator.IsDone(); i++)
            {
                generator.GenerateNextToken();
            }

            Assert.True(generator.TokenCount() > (ulong)prompt[0].Length);
        }

        // The lease returns: NOW it is disposed.
        lease.Dispose();

        var dropped = host.Host.Describe()!;
        Assert.False(dropped.IsLoaded);
        Assert.Equal(0, dropped.ActiveLeases);
        Assert.True(host.Logs.Logged(EdgeChatEventIds.ChatModelDropped));

        // And the next acquire reloads.
        using var reloaded = await host.Host.AcquireAsync(Token).ConfigureAwait(true);
        Assert.Equal(loadCount + 1, host.Host.Describe()!.LoadCount);
        Assert.True(host.Host.Describe()!.IsLoaded);
    }

    [Fact]
    public async Task UnloadWithNoLeaseDisposesImmediatelyAndAgainReturnsFalse()
    {
        using var host = fixture.NewHost();
        await host.Host.PreloadAsync(Token).ConfigureAwait(true);

        Assert.True(await host.Host.UnloadAsync(Token).ConfigureAwait(true));
        Assert.False(host.Host.Describe()!.IsLoaded);
        Assert.False(await host.Host.UnloadAsync(Token).ConfigureAwait(true));
    }

    [Fact]
    public async Task CriticalMemoryPressureMovesTheLatchTerminatesTheTurnUnloadsTheModelAndTheNextAcquireReloadsIt()
    {
        // The CONTAINER's host and client, because the lifecycle observer reaches the host through
        // the container - spec 16.4's assertion, runnable on the host lane as well as on a device.
        var host = fixture.ContainerHost;
        var client = fixture.Services.GetRequiredService<IChatClient>();
        var monitor = fixture.Monitor;
        var lifecycle = fixture.Lifecycle;

        // Start from a known state, and - whatever happens - leave the shared container in one:
        // the pressure latch is process-wide for this collection and a latched monitor refuses
        // every later load with 7105.
        await lifecycle.RaiseResumedAsync(Token).ConfigureAwait(true);

        try
        {
            await host.PreloadAsync(Token).ConfigureAwait(true);
            var loadsBefore = host.Describe()!.LoadCount;
            Assert.Null(monitor.LastPressure);
            Assert.True(host.IsAcceptingTurns);

            var updates = new List<ChatResponseUpdate>();
            var raised = false;

            await foreach (var update in client
                .GetStreamingResponseAsync(Ask(), new ChatOptions { MaxOutputTokens = 440 }, Token)
                .ConfigureAwait(true))
            {
                updates.Add(update);
                if (!raised)
                {
                    raised = true;

                    // Sub-project 2's observer latches the level; sub-project 4's terminates, drops
                    // the cache and unloads, in that order.
                    await lifecycle.RaiseMemoryPressureAsync(EdgeMemoryPressure.Critical, Token).ConfigureAwait(true);
                }
            }

            var status = updates[^1].GetTurnStatus();
            Assert.NotNull(status);
            Assert.Equal(EdgeChatStopReason.MemoryPressure, status.StopReason);
            Assert.True(status.GeneratedTokens < 440, $"the turn ran to its cap ({status.GeneratedTokens})");

            // The latch moved, turns are refused, and the model is gone once the turn's lease returned.
            Assert.Equal(EdgeMemoryPressure.Critical, monitor.LastPressure);
            Assert.False(host.IsAcceptingTurns);
            Assert.False(host.Describe()!.IsLoaded);
            Assert.Equal(0, host.Describe()!.ActiveLeases);

            var refused = await Assert.ThrowsAsync<EdgeChatException>(
                () => client.GetResponseAsync(Ask(), null, Token)).ConfigureAwait(true);
            Assert.Equal(EdgeErrorCode.ChatBusy, refused.Code);

            // Resumed clears the latch; the next acquire reloads.
            await lifecycle.RaiseResumedAsync(Token).ConfigureAwait(true);
            Assert.Null(monitor.LastPressure);
            Assert.True(host.IsAcceptingTurns);

            using (var lease = await host.AcquireAsync(Token).ConfigureAwait(true))
            {
                Assert.Equal(loadsBefore + 1, host.Describe()!.LoadCount);
                Assert.NotNull(lease.Model);
            }

            Assert.True(host.Describe()!.IsLoaded);
        }
        finally
        {
            await lifecycle.RaiseResumedAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }
}
