using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntimeGenAI;
using Qavren.Edge.Chat.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Spec section 15.3's 7104 row, three obligations: the partial text stays streamed, the final
/// update says <c>Error</c> with the real counts, and THEN the exception surfaces with the native
/// message preserved.
/// </summary>
public class GenerationFailureTests
{
    private static ChatMessage[] Ask() => [new ChatMessage(ChatRole.User, "hi")];

    private static ClientHarness FailingAt(int index, string message = "native fault: CUDA fell over")
    {
        var harness = ClientHarness.Build(script: ["one ", "two ", "three ", "four ", "five "]);
        harness.Session.OnGeneratorCreated = generator => generator.ThrowAt = (index, FakeGenerator.NativeFailure(message));
        return harness;
    }

    [Fact]
    public async Task ThePartialTextStaysStreamedTheFinalUpdateSaysErrorAndThen7104Surfaces()
    {
        using var harness = FailingAt(3);
        var received = new List<ChatResponseUpdate>();
        EdgeChatException? surfaced = null;

        try
        {
            await foreach (var update in harness.Client.GetStreamingResponseAsync(Ask(), null, TestContext.Current.CancellationToken).ConfigureAwait(true))
            {
                received.Add(update);
            }
        }
        catch (EdgeChatException ex)
        {
            surfaced = ex;
        }

        // Obligation one: the three tokens already streamed were delivered before anything else.
        Assert.Equal(["one ", "two ", "three "], received.Take(3).Select(u => u.Text));

        // Obligation two: the final update, after them, says Error with the real counts.
        var final = received[^1];
        var status = final.GetTurnStatus();
        Assert.NotNull(status);
        Assert.Equal(EdgeChatStopReason.Error, status.StopReason);
        Assert.Equal(3, status.GeneratedTokens);
        Assert.Equal(4, received.Count);

        // Obligation three: only then the exception, with the native message preserved.
        Assert.NotNull(surfaced);
        Assert.Equal(EdgeErrorCode.ChatGenerationFailed, surfaced.Code);
        Assert.Contains("CUDA fell over", surfaced.Message, StringComparison.Ordinal);
        Assert.IsType<OnnxRuntimeGenAIException>(surfaced.InnerException);
    }

    [Fact]
    public async Task TheNonStreamingPathThrowsTheSame7104()
    {
        using var harness = FailingAt(1);

        var exception = await Assert.ThrowsAsync<EdgeChatException>(
            () => harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatGenerationFailed, exception.Code);
        Assert.Equal(1, harness.Client.Statistics.Turns);
        Assert.Equal(EdgeChatStopReason.Error, harness.Client.Statistics.LastStopReason);
    }

    [Fact]
    public async Task AFailedTurnReleasesTheGateTheLeaseAndTheGeneratorAndTheNextTurnRuns()
    {
        using var harness = FailingAt(0);

        await Assert.ThrowsAsync<EdgeChatException>(
            () => harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.True(harness.Session.Generators[0].Disposed);
        Assert.Equal(0, harness.Host.ActiveLeases);
        Assert.False(harness.Host.HasActiveGeneration);
        Assert.False(harness.Client.HasCachedConversation);

        harness.Session.OnGeneratorCreated = null;
        var response = await harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("one two three four five ", response.Text);
    }
}
