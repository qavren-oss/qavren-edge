using Microsoft.Extensions.AI;
using Qavren.Edge.Chat.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Thread safety by serialisation: one gate per model, a second turn waits, a queue past
/// <c>MaxQueuedTurns</c> is refused with 7105 and logged 933, and a gate timeout is 7105.
/// </summary>
public class QueueingTests
{
    private static ChatMessage[] Ask() => [new ChatMessage(ChatRole.User, "hi")];

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(5, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>Blocks the FIRST generator's first token until <paramref name="release"/> is set.</summary>
    private static void BlockFirstTurn(ClientHarness harness, ManualResetEventSlim release)
    {
        var first = true;
        harness.Session.OnGeneratorCreated = generator =>
        {
            if (!first)
            {
                return;
            }

            first = false;
            generator.BeforeGenerate = (_, index) =>
            {
                if (index == 0)
                {
                    release.Wait(TimeSpan.FromSeconds(30));
                }
            };
        };
    }

    [Fact]
    public async Task ASecondTurnWaitsAndAFifthIsRefusedWith7105AndLogged933()
    {
        using var release = new ManualResetEventSlim(initialState: false);
        using var harness = ClientHarness.Build(o => o.MaxQueuedTurns = 3);
        BlockFirstTurn(harness, release);

        try
        {
            var first = harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => harness.Client.GateEntries == 1, "the first turn to take the gate").ConfigureAwait(true);

            var second = harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken);
            var third = harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken);
            var fourth = harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => harness.Client.QueuedTurns == 3, "three turns to queue").ConfigureAwait(true);

            // None of the queued turns has run: the second waits rather than allocating a
            // generator of its own.
            Assert.Single(harness.Session.Generators);

            var refused = await Assert.ThrowsAsync<EdgeChatException>(
                () => harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken)).ConfigureAwait(true);

            Assert.Equal(EdgeErrorCode.ChatBusy, refused.Code);
            Assert.Contains("MaxQueuedTurns", refused.Message, StringComparison.Ordinal);
            Assert.True(harness.Logger.Logged(EdgeChatEventIds.TurnRejected));
            Assert.True(harness.Logger.Logged(EdgeChatEventIds.TurnQueued));
            Assert.Equal(1, harness.Client.Statistics.RejectedTurns);

            release.Set();

            var responses = await Task.WhenAll(first, second, third, fourth).ConfigureAwait(true);
            Assert.All(responses, r => Assert.Equal("Hello world.", r.Text));
            Assert.Equal(4, harness.Client.Statistics.Turns);
            Assert.Equal(0, harness.Client.QueuedTurns);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task AGateTimeoutIs7105NamingTurnQueueTimeout()
    {
        using var release = new ManualResetEventSlim(initialState: false);
        using var harness = ClientHarness.Build(o => o.TurnQueueTimeout = TimeSpan.FromMilliseconds(50));
        BlockFirstTurn(harness, release);

        try
        {
            var first = harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => harness.Client.GateEntries == 1, "the first turn to take the gate").ConfigureAwait(true);

            var timedOut = await Assert.ThrowsAsync<EdgeChatException>(
                () => harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken)).ConfigureAwait(true);

            Assert.Equal(EdgeErrorCode.ChatBusy, timedOut.Code);
            Assert.Contains("TurnQueueTimeout", timedOut.Message, StringComparison.Ordinal);
            Assert.Contains("not thread safe", timedOut.Message, StringComparison.Ordinal);

            release.Set();
            Assert.Equal("Hello world.", (await first.ConfigureAwait(true)).Text);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task TurnsNotAcceptedIs7105BeforeTheGate()
    {
        using var harness = ClientHarness.Build();
        harness.Host.IsAcceptingTurns = false;

        var refused = await Assert.ThrowsAsync<EdgeChatException>(
            () => harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatBusy, refused.Code);
        Assert.Contains("not accepting turns", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.Client.GateEntries);
    }
}
