using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntimeGenAI;
using Xunit;
using GenAiModel = Microsoft.ML.OnnxRuntimeGenAI.Model;

namespace Qavren.Edge.Chat.Tests.Tier2;

/// <summary>
/// Spec 16.2's streaming bullets over real natives: the shape of one turn, the output cap with the
/// cache off and on, the upstream <c>max_length</c> defect asserted from the outside, cancellation
/// mid-stream and during prefill, and the cooperative abort from another thread under a stress
/// loop (spec 19 item 7).
/// </summary>
/// <remarks>
/// Mechanics only, never text: the weights are random. Nothing here reads a decoded string for
/// meaning; token counts, ids, reasons and search options are what the assertions are about.
/// </remarks>
[Collection(TinyChatModelCollectionDefinition.Name)]
public sealed class StreamingTurnTests(TinyChatModelFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ChatMessage[] Ask(string text = "hi") => [new ChatMessage(ChatRole.User, text)];

    private static async Task<List<ChatResponseUpdate>> CollectAsync(
        TierTwoClient client, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.Client
            .GetStreamingResponseAsync(Ask(), options, cancellationToken)
            .ConfigureAwait(true))
        {
            updates.Add(update);
        }

        return updates;
    }

    [Fact]
    public async Task OneTurnYieldsTextUpdatesThenExactlyOneFinalUpdateSharingOneMessageId()
    {
        using var client = fixture.NewClient();

        var updates = await CollectAsync(client, cancellationToken: Token).ConfigureAwait(true);

        Assert.NotEmpty(updates);
        var final = updates[^1];
        var text = updates.Take(updates.Count - 1).ToList();

        // Exactly one final update carrying UsageContent, a FinishReason and a ChatTurnStatus.
        var usage = Assert.Single(final.Contents.OfType<UsageContent>());
        Assert.NotNull(final.FinishReason);
        var status = final.GetTurnStatus();
        Assert.NotNull(status);
        Assert.Equal(string.Empty, final.Text);

        // ...and only the final one.
        Assert.All(text, u => Assert.Empty(u.Contents.OfType<UsageContent>()));
        Assert.All(text, u => Assert.Null(u.GetTurnStatus()));
        Assert.All(text, u => Assert.Null(u.FinishReason));
        Assert.All(text, u => Assert.NotEmpty(u.Text));

        // Every update shares one MessageId, one ResponseId, one role, one model id.
        Assert.Single(updates.Select(u => u.MessageId).Distinct(StringComparer.Ordinal));
        Assert.Single(updates.Select(u => u.ResponseId).Distinct(StringComparer.Ordinal));
        Assert.All(updates, u => Assert.Equal(ChatRole.Assistant, u.Role));
        Assert.All(updates, u => Assert.Equal(client.Client.ModelId, u.ModelId));

        // The counts agree with each other and with the fixture's output cap.
        Assert.Equal(TinyChatModelFixture.DefaultMaxOutputTokens, status.GeneratedTokens);
        Assert.Equal(status.GeneratedTokens, usage.Details.OutputTokenCount);
        Assert.Equal(status.PromptTokens, usage.Details.InputTokenCount);
        Assert.Equal(status.PromptTokens + status.GeneratedTokens, usage.Details.TotalTokenCount);
        Assert.True(status.PromptTokens > 0);
        Assert.Equal(TinyChatModelFixture.ContextLength, status.ContextTokens);
        Assert.True(status.Duration > TimeSpan.Zero);

        // ToChatResponseAsync groups them into one message.
        var response = updates.ToChatResponse();
        Assert.Single(response.Messages);
        Assert.Equal(ChatRole.Assistant, response.Messages[0].Role);
        Assert.Equal(string.Concat(text.Select(u => u.Text)), response.Text);
        Assert.Equal(final.FinishReason, response.FinishReason);

        Assert.True(client.Logs.Logged(EdgeChatEventIds.TurnStarted));
        Assert.True(client.Logs.Logged(EdgeChatEventIds.TurnCompleted));
        Assert.Equal(1, client.Client.Statistics.Turns);
    }

    [Fact]
    public async Task MaxOutputTokensBindsAtExactlyNWithTheConversationCacheOff()
    {
        const int N = 7;
        using var client = fixture.NewClient(o =>
        {
            o.EnableConversationCache = false;
            o.MaxOutputTokens = N;
        });

        var updates = await CollectAsync(client, cancellationToken: Token).ConfigureAwait(true);
        var status = updates[^1].GetTurnStatus()!;

        Assert.Equal(ChatFinishReason.Length, updates[^1].FinishReason);
        Assert.Equal(EdgeChatStopReason.MaxOutputTokens, status.StopReason);
        Assert.Equal(N, status.GeneratedTokens);

        // With the cache off, max_length and the managed counter agree: the generator was built
        // to exactly what this turn needs.
        Assert.Equal(status.PromptTokens + N, client.Sessions.MaxLength(0));
    }

    [Fact]
    public async Task MaxOutputTokensBindsAtExactlyNWithTheConversationCacheOnWhereOnlyTheCounterCan()
    {
        const int N = 7;
        using var client = fixture.NewClient(o => o.MaxOutputTokens = N);

        var updates = await CollectAsync(client, cancellationToken: Token).ConfigureAwait(true);
        var status = updates[^1].GetTurnStatus()!;

        Assert.Equal(ChatFinishReason.Length, updates[^1].FinishReason);
        Assert.Equal(EdgeChatStopReason.MaxOutputTokens, status.StopReason);
        Assert.Equal(N, status.GeneratedTokens);

        // With the cache on, the generator was built to the budget's whole answer, so max_length
        // could NOT have bound at N - only the managed counter can.
        Assert.Equal(TinyChatModelFixture.ContextLength, client.Sessions.MaxLength(0));
        Assert.True(client.Client.HasCachedConversation);
    }

    [Fact]
    public async Task WithTheCacheOnTheGeneratorsMaxLengthIsTheBudgetsAnswerAndNotTheDeclaredContextLength()
    {
        // The upstream bug, asserted from the outside: a cached generator must be built to the
        // budget's resolvedContextTokens, never to the model's declared context_length.
        const int Resolved = 256;
        using var client = fixture.NewClient(o =>
        {
            o.MaxContextTokens = Resolved;
            o.MaxOutputTokens = 4;
        });

        var response = await client.Client.GetResponseAsync(Ask(), null, Token).ConfigureAwait(true);

        var info = client.Client.Model;
        Assert.NotNull(info);
        Assert.Equal(Resolved, info.ResolvedContextTokens);
        Assert.Equal(TinyChatModelFixture.ContextLength, info.Shape.ContextLength);
        Assert.NotEqual(info.Shape.ContextLength, info.ResolvedContextTokens);

        Assert.Equal(Resolved, client.Sessions.MaxLength(0));
        Assert.Equal(Resolved, response.GetTurnStatus()!.ContextTokens);

        // And the config the model was built from carries the same answer (the belt to the
        // braces): a GeneratorParams over the live model reads search.max_length back.
        var model = client.Client.GetService<GenAiModel>();
        Assert.NotNull(model);
        using var parameters = new GeneratorParams(model);
        Assert.Equal(Resolved, parameters.GetSearchNumber("max_length"));
    }

    [Fact]
    public async Task CancellationMidStreamThrowsWithinOneTokenAndTheFinalUpdateStillCarriesTheCounts()
    {
        const int CancelAfterUpdates = 3;
        using var client = fixture.NewClient(o =>
        {
            o.EnableConversationCache = false;
            o.MaxOutputTokens = 400;
        });

        using var cts = new CancellationTokenSource();
        var updates = new List<ChatResponseUpdate>();
        var decodedAtCancel = -1;

        var surfaced = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in client.Client.GetStreamingResponseAsync(Ask(), null, cts.Token).ConfigureAwait(true))
            {
                updates.Add(update);
                if (updates.Count == CancelAfterUpdates)
                {
                    // Cancel first, then read the producer's position: after the cancel it can
                    // decode at most one more token (the one in flight inside native code).
                    await cts.CancelAsync().ConfigureAwait(true);
                    decodedAtCancel = CountDecodedTokens(client);
                }
            }
        }).ConfigureAwait(true);

        Assert.NotNull(surfaced);
        Assert.True(decodedAtCancel >= 0, "the cancel never happened");

        var status = updates[^1].GetTurnStatus();
        Assert.NotNull(status);
        Assert.Equal(EdgeChatStopReason.Cancelled, status.StopReason);
        Assert.InRange(status.GeneratedTokens, 1, decodedAtCancel + 1);
        Assert.True(status.GeneratedTokens < 400, "the turn ran to its cap instead of stopping");

        // The lease went back and the gate is free.
        Assert.Equal(0, client.Host.Host.Describe()!.ActiveLeases);
        var next = await client.Client.GetResponseAsync(Ask(), new ChatOptions { MaxOutputTokens = 2 }, Token).ConfigureAwait(true);
        Assert.Equal(2, next.GetTurnStatus()!.GeneratedTokens);
    }

    [Fact]
    public async Task CancellationDuringPrefillIsBracketedAndBuildsNoGenerator()
    {
        // (1) A token cancelled before the turn: refused before the model is even borrowed.
        using var first = fixture.NewClient();
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync().ConfigureAwait(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => first.Client.GetResponseAsync(Ask(), null, cancelled.Token)).ConfigureAwait(true);
        }

        Assert.Equal(0, first.Sessions.GeneratorsBuilt);

        // (2) A token cancelled INSIDE prefill - from the prompt formatter, which runs after the
        // model is borrowed and before Encode/AppendTokens. The bracket after formatting catches
        // it: no generator is built, nothing is appended, and the lease goes back.
        using var cts = new CancellationTokenSource();
        using var second = fixture.NewClient(o => o.PromptFormatter = (messages, _, _) =>
        {
            cts.Cancel();
            return "<|user|>\n" + messages[^1].Text + "\n<|assistant|>\n";
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => second.Client.GetResponseAsync(Ask(), null, cts.Token)).ConfigureAwait(true);

        Assert.Equal(1, second.Sessions.Sessions);
        Assert.Equal(0, second.Sessions.GeneratorsBuilt);
        Assert.Equal(0, second.Host.Host.Describe()!.ActiveLeases);
    }

    [Fact]
    public async Task TerminateActiveGenerationFromAnotherThreadEndsTheTurnWithinABoundedTimeUnderAStressLoop()
    {
        // Spec 19 item 7: ort_genai_c.h says the API is not thread safe, and terminate_session is
        // the one call SP4 makes outside the turn gate. Five rounds, each hammering the call from
        // another thread while native decode is running.
        const int Rounds = 5;
        const int HammerCalls = 200;
        const int Cap = 440;

        for (var round = 0; round < Rounds; round++)
        {
            using var client = fixture.NewClient(o =>
            {
                o.EnableConversationCache = false;
                o.MaxOutputTokens = Cap;
            });

            var host = client.Host.Host;
            var updates = new List<ChatResponseUpdate>();
            Task? hammer = null;
            var terminatedAt = 0L;

            await foreach (var update in client.Client.GetStreamingResponseAsync(Ask(), null, Token).ConfigureAwait(true))
            {
                updates.Add(update);
                if (hammer is null)
                {
                    terminatedAt = Stopwatch.GetTimestamp();
                    hammer = Task.Run(
                        () =>
                        {
                            for (var i = 0; i < HammerCalls; i++)
                            {
                                host.TerminateActiveGeneration();
                            }
                        },
                        CancellationToken.None);
                }
            }

            Assert.NotNull(hammer);
            await hammer.ConfigureAwait(true);
            var elapsed = Stopwatch.GetElapsedTime(terminatedAt);

            var status = updates[^1].GetTurnStatus();
            Assert.NotNull(status);
            Assert.Equal(EdgeChatStopReason.MemoryPressure, status.StopReason);
            Assert.True(status.GeneratedTokens < Cap, $"round {round}: the turn ran to its cap ({status.GeneratedTokens})");
            Assert.True(elapsed < TimeSpan.FromSeconds(10), $"round {round}: termination took {elapsed}");
            Assert.True(client.Logs.Logged(EdgeChatEventIds.TurnTerminated));
            Assert.Equal(1, client.Client.Statistics.TerminationEvents);
            Assert.Equal(0, host.Describe()!.ActiveLeases);

            JobSummary.Record("tier2-terminate", $"round{round}.generatedBeforeTermination", status.GeneratedTokens);
            JobSummary.Record("tier2-terminate", $"round{round}.terminationMs", elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// How many tokens the producer has decoded so far, read from the Trace line the decode loop
    /// writes after every token - the producer's own position, not the consumer's.
    /// </summary>
    private static int CountDecodedTokens(TierTwoClient client) =>
        client.Logs.Records.Count(r =>
            r.Level == LogLevel.Trace
            && r.EventId.Id == EdgeChatEventIds.TurnCompleted
            && r.Message.StartsWith("Decoded token", StringComparison.Ordinal));
}
