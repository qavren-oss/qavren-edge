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

            // The first turn takes the gate on the caller's thread and builds its generator on the
            // pool, so "inside the gate" no longer means "has a generator yet".
            await WaitUntilAsync(() => harness.Session.Generators.Count >= 1, "the first turn to build its generator").ConfigureAwait(true);

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
    public async Task TheCallerGetsItsFirstMoveNextBackWhileThePromptIsStillBeingPrepared()
    {
        var ct = TestContext.Current.CancellationToken;
        using var release = new ManualResetEventSlim(initialState: false);
        using var harness = ClientHarness.Build();

        // Stands in for the prefill: the generator exists and the prompt is being appended to it.
        harness.Session.OnGeneratorCreated = _ => release.Wait(TimeSpan.FromSeconds(20));

        var enumerator = harness.Client.GetStreamingResponseAsync(Ask(), null, ct).GetAsyncEnumerator(ct);
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var first = enumerator.MoveNextAsync();
            var cameBackAfter = clock.Elapsed;

            // With a loaded model every await on the prepare path completes synchronously, so the
            // whole of it - tokenizer, history reduction, prefill - used to run inside this call,
            // on the caller's thread. In a MAUI app that thread is the UI.
            Assert.False(first.IsCompleted);
            Assert.True(
                cameBackAfter < TimeSpan.FromSeconds(5),
                $"MoveNextAsync held the caller for {cameBackAfter.TotalSeconds:F1} s while the prompt was prepared.");

            release.Set();
            Assert.True(await first.ConfigureAwait(true));
        }
        finally
        {
            release.Set();
            await enumerator.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task AFailureResumingACachedGeneratorGivesTheGateAndTheLeaseBack()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = ClientHarness.Build(o => o.TurnQueueTimeout = TimeSpan.FromSeconds(2));
        var conversation = new ChatOptions { ConversationId = "c" };

        var first = await harness.Client.GetResponseAsync(Ask(), conversation, ct).ConfigureAwait(true);
        var cached = harness.Session.LastGenerator;

        // The first native call a reused generator sees is the terminate reset, ahead of its
        // prefill. A fresh generator has no native call left between the prefill and the decode
        // loop at all.
        cached.ThrowOnRuntimeOption = FakeGenerator.NativeFailure("Session in Terminated state");

        ChatMessage[] followUp =
        [
            .. Ask(),
            new ChatMessage(ChatRole.Assistant, first.Text),
            new ChatMessage(ChatRole.User, "again"),
        ];

        await Assert.ThrowsAnyAsync<Exception>(
            () => harness.Client.GetResponseAsync(followUp, conversation, ct)).ConfigureAwait(true);

        // A failure on this path used to leave the gate held and the lease out: every later turn
        // then waited out TurnQueueTimeout and was refused as ChatBusy, until the process was
        // restarted. Nor is the host left holding the setter of a generator that has been freed.
        Assert.Equal(0, harness.Host.ActiveLeases);
        Assert.True(cached.Disposed);
        Assert.False(harness.Host.HasActiveGeneration);

        var next = await harness.Client.GetResponseAsync(Ask(), null, ct).ConfigureAwait(true);

        Assert.Equal("Hello world.", next.Text);
    }

    [Fact]
    public async Task ATurnThatQueuedBehindOneStoppedByMemoryPressureIsRefusedRatherThanStarted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var release = new ManualResetEventSlim(initialState: false);
        using var harness = ClientHarness.Build();
        BlockFirstTurn(harness, release);

        try
        {
            var first = harness.Client.GetResponseAsync(Ask(), null, ct);
            await WaitUntilAsync(() => harness.Client.GateEntries == 1, "the first turn to take the gate").ConfigureAwait(true);

            // Passed its pre-flight while turns were still accepted, and is now waiting its turn.
            var queued = harness.Client.GetResponseAsync(Ask(), null, ct);
            await WaitUntilAsync(() => harness.Client.QueuedTurns == 1, "the second turn to queue").ConfigureAwait(true);

            // What MemoryPressure(Critical) does while it waits.
            harness.Host.IsAcceptingTurns = false;
            harness.Host.TerminateActiveGeneration();
            release.Set();

            var stopped = await first.ConfigureAwait(true);
            Assert.Equal(EdgeChatStopReason.MemoryPressure, stopped.GetTurnStatus()!.StopReason);

            // The pre-flight it passed is as old as its wait. It used to be the only one, so the
            // queued turn took the gate, cleared the terminate that had just stopped the turn in
            // front of it, and started a prefill into the pressure that terminate was raised for.
            var refused = await Assert.ThrowsAsync<EdgeChatException>(() => queued).ConfigureAwait(true);

            Assert.Equal(EdgeErrorCode.ChatBusy, refused.Code);
            Assert.Single(harness.Session.Generators);
            Assert.Equal(0, harness.Host.ActiveLeases);
            Assert.Equal(1, harness.Client.Statistics.RejectedTurns);

            // And the gate it took to find that out was given back.
            harness.Host.IsAcceptingTurns = true;
            var next = await harness.Client.GetResponseAsync(Ask(), null, ct).ConfigureAwait(true);
            Assert.Equal("Hello world.", next.Text);
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
