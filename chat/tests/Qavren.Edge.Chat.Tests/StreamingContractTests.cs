using Microsoft.Extensions.AI;
using Qavren.Edge.Chat.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Spec section 10(g) and (h): the shape of the stream, and the producer/consumer contract that is
/// a contract rather than an illustration.
/// </summary>
public class StreamingContractTests
{
    private static ChatMessage[] Ask() => [new ChatMessage(ChatRole.User, "hi")];

    private static async Task<List<ChatResponseUpdate>> CollectAsync(ClientHarness harness, ChatOptions? options = null)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in harness.Client.GetStreamingResponseAsync(Ask(), options, TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            updates.Add(update);
        }

        return updates;
    }

    [Fact]
    public async Task OneMessageIdAcrossEveryUpdateAndExactlyOneFinalMetadataOnlyUpdate()
    {
        using var harness = ClientHarness.Build(script: ["a", "b", "c"]);

        var updates = await CollectAsync(harness).ConfigureAwait(true);

        Assert.Equal(4, updates.Count);
        Assert.Single(updates.Select(u => u.MessageId).Distinct(StringComparer.Ordinal));
        Assert.Single(updates.Select(u => u.ResponseId).Distinct(StringComparer.Ordinal));
        Assert.All(updates, u => Assert.Equal(ChatRole.Assistant, u.Role));
        Assert.All(updates, u => Assert.Equal(harness.Client.ModelId, u.ModelId));

        var final = updates[^1];
        Assert.Equal(string.Empty, final.Text);
        var usage = Assert.Single(final.Contents.OfType<UsageContent>());
        Assert.Equal(3, usage.Details.OutputTokenCount);
        Assert.Equal(usage.Details.InputTokenCount + 3, usage.Details.TotalTokenCount);

        var status = final.GetTurnStatus();
        Assert.NotNull(status);
        Assert.Equal(EdgeChatStopReason.Completed, status.StopReason);
        Assert.Equal(3, status.GeneratedTokens);
        Assert.Equal(harness.Options.Preset.DefaultMaxContextTokens, status.ContextTokens);

        // Only the final update carries usage or a turn status.
        Assert.All(updates.Take(3), u => Assert.Empty(u.Contents.OfType<UsageContent>()));
        Assert.All(updates.Take(3), u => Assert.Null(u.GetTurnStatus()));
    }

    [Fact]
    public async Task FinishReasonIsLengthWhenTheOutputCapBindsAndStopOtherwise()
    {
        using var capped = ClientHarness.Build(o => o.MaxOutputTokens = 2, script: ["a", "b", "c"]);
        var updates = await CollectAsync(capped).ConfigureAwait(true);
        Assert.Equal(ChatFinishReason.Length, updates[^1].FinishReason);
        Assert.Equal(EdgeChatStopReason.MaxOutputTokens, updates[^1].GetTurnStatus()!.StopReason);
        Assert.All(updates.Take(updates.Count - 1), u => Assert.Null(u.FinishReason));

        using var whole = ClientHarness.Build(script: ["a", "b", "c"]);
        updates = await CollectAsync(whole).ConfigureAwait(true);
        Assert.Equal(ChatFinishReason.Stop, updates[^1].FinishReason);
    }

    [Fact]
    public async Task GetResponseAsyncIsStructurallyTheAggregatedStream()
    {
        using var streamed = ClientHarness.Build(script: ["Hel", "lo", " there"]);
        var aggregated = (await CollectAsync(streamed).ConfigureAwait(true)).ToChatResponse();

        using var direct = ClientHarness.Build(script: ["Hel", "lo", " there"]);
        var response = await direct.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(aggregated.Text, response.Text);
        Assert.Equal("Hello there", response.Text);
        Assert.Equal(aggregated.FinishReason, response.FinishReason);
        Assert.Equal(aggregated.Usage!.InputTokenCount, response.Usage!.InputTokenCount);
        Assert.Equal(aggregated.Usage.OutputTokenCount, response.Usage.OutputTokenCount);
        Assert.Equal(aggregated.GetTurnStatus()!.StopReason, response.GetTurnStatus()!.StopReason);
        Assert.Equal(aggregated.GetTurnStatus()!.GeneratedTokens, response.GetTurnStatus()!.GeneratedTokens);
        Assert.Single(response.Messages);
        Assert.Equal(ChatRole.Assistant, response.Messages[0].Role);
    }

    [Fact]
    public async Task OnSuccessTheProducerCompletesAndTheIteratorObservesIt()
    {
        using var harness = ClientHarness.Build(o => o.EnableConversationCache = false);

        await CollectAsync(harness).ConfigureAwait(true);

        // FinishTurn ran, which only happens after the producer task was awaited.
        Assert.True(harness.Session.LastGenerator.Disposed);
        Assert.False(harness.Host.HasActiveGeneration);
        Assert.Equal(0, harness.Host.ActiveLeases);
        Assert.Equal(1, harness.Client.Statistics.Turns);
    }

    [Fact]
    public async Task OnCancelTheFinalUpdateStillCarriesCancelledAndTheRealCountsBeforeTheExceptionSurfaces()
    {
        using var harness = ClientHarness.Build(script: ["a", "b", "c", "d", "e"]);
        using var cts = new CancellationTokenSource();
        harness.Session.OnGeneratorCreated = generator => generator.BeforeGenerate = (_, index) =>
        {
            if (index == 2)
            {
                cts.Cancel();
            }
        };

        var updates = new List<ChatResponseUpdate>();
        var surfaced = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in harness.Client.GetStreamingResponseAsync(Ask(), null, cts.Token).ConfigureAwait(true))
            {
                updates.Add(update);
            }
        }).ConfigureAwait(true);

        Assert.NotNull(surfaced);

        // The two tokens produced before the cancel, then the honest final update.
        var final = updates[^1];
        var status = final.GetTurnStatus();
        Assert.NotNull(status);
        Assert.Equal(EdgeChatStopReason.Cancelled, status.StopReason);
        Assert.Equal(2, status.GeneratedTokens);
        Assert.Equal(["a", "b"], updates.Take(updates.Count - 1).Select(u => u.Text));

        // Cancellation reached the generator inside native code, and the producer was observed.
        Assert.True(harness.Session.LastGenerator.Terminated);
        Assert.True(harness.Session.LastGenerator.Disposed);
        Assert.False(harness.Host.HasActiveGeneration);
        Assert.Equal(0, harness.Host.ActiveLeases);
    }

    [Fact]
    public async Task AConsumerThatStopsReadingUnblocksTheProducerRatherThanDeadlockingOnTheBoundedChannel()
    {
        // 200 tokens against a 64-slot channel: without the consumer-gone signal the producer would
        // block forever on a full channel once the consumer left.
        using var harness = ClientHarness.Build(script: [.. Enumerable.Range(0, 200).Select(i => $"{i} ")]);

        var seen = 0;
        await foreach (var update in harness.Client.GetStreamingResponseAsync(Ask(), null, TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            _ = update;
            if (++seen == 3)
            {
                break;
            }
        }

        Assert.Equal(3, seen);
        Assert.True(harness.Session.LastGenerator.Disposed);
        Assert.Equal(0, harness.Host.ActiveLeases);

        // And the gate is free for the next turn.
        var response = await harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(200, response.GetTurnStatus()!.GeneratedTokens);
    }

    [Fact]
    public async Task ALifecycleTerminationCompletesTheStreamWithSuspendedOrMemoryPressureAndNeverThrows()
    {
        using var suspended = ClientHarness.Build(script: ["a", "b", "c", "d"]);
        suspended.Session.OnGeneratorCreated = generator => generator.BeforeGenerate = (_, index) =>
        {
            if (index == 2)
            {
                suspended.Host.SuspendRequested = true;
                suspended.Host.TerminateActiveGeneration();
            }
        };

        var response = await suspended.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(EdgeChatStopReason.Suspended, response.GetTurnStatus()!.StopReason);
        Assert.Equal("ab", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.True(suspended.Logger.Logged(EdgeChatEventIds.TurnTerminated));
        Assert.Equal(1, suspended.Client.Statistics.TerminationEvents);

        using var pressure = ClientHarness.Build(script: ["a", "b", "c", "d"]);
        pressure.Session.OnGeneratorCreated = generator => generator.BeforeGenerate = (_, index) =>
        {
            if (index == 1)
            {
                pressure.Host.TerminateActiveGeneration();
            }
        };

        response = await pressure.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(EdgeChatStopReason.MemoryPressure, response.GetTurnStatus()!.StopReason);
        Assert.Equal("a", response.Text);
    }
}
