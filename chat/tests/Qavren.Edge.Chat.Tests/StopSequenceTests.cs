using Microsoft.Extensions.AI;
using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Chat.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// The rolling stop-sequence buffer: a multi-token stop fires, the trimmed text excludes it, and
/// the longest match at a position wins. Upstream compares one decoded token by equality, so a
/// multi-token stop never fires there - this file is that bug, asserted the other way round.
/// </summary>
public class StopSequenceTests
{
    private static ChatMessage[] Ask() => [new ChatMessage(ChatRole.User, "hi")];

    [Fact]
    public async Task AMultiTokenStopSequenceAcrossADecodeChunkBoundaryFires()
    {
        // The preset's <|eot_id|> arrives as three fragments, exactly as a BPE tokenizer emits it.
        using var harness = ClientHarness.Build(script: ["Hi", " there", "<|", "eot", "_id|>", "NEVER"]);

        var response = await harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal("Hi there", response.Text);
        Assert.Equal(EdgeChatStopReason.StopSequence, response.GetTurnStatus()!.StopReason);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);

        // The stop tokens were consumed; the token after the stop was never generated.
        Assert.Equal(5, harness.Session.LastGenerator.Generated);
    }

    [Fact]
    public async Task TheTrimmedTextExcludesTheStopStringEvenWhenItArrivesInsideAFragment()
    {
        using var harness = ClientHarness.Build(script: ["Answer<|eot", "_id|>tail", "more"]);

        var response = await harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal("Answer", response.Text);
        Assert.Equal(EdgeChatStopReason.StopSequence, response.GetTurnStatus()!.StopReason);
    }

    [Fact]
    public async Task APerCallStopSequenceIsAddedToThePresetsRatherThanReplacingThem()
    {
        using var harness = ClientHarness.Build(script: ["one", " two", " END", " three"]);

        var response = await harness.Client.GetResponseAsync(
            Ask(), new ChatOptions { StopSequences = ["END"] }, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal("one two ", response.Text);
        Assert.Equal(EdgeChatStopReason.StopSequence, response.GetTurnStatus()!.StopReason);
    }

    [Fact]
    public async Task TextThatMerelyLooksLikeTheStartOfAStopIsStillDelivered()
    {
        // "<|" is held back until it is known not to be a stop, then released.
        using var harness = ClientHarness.Build(script: ["a <|", " b", "."]);

        var response = await harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal("a <| b.", response.Text);
        Assert.Equal(EdgeChatStopReason.Completed, response.GetTurnStatus()!.StopReason);
    }

    [Fact]
    public void TheLongestMatchAtAPositionWins()
    {
        var matcher = new StopSequenceMatcher(EdgeChatOptions.ComposeStopSequences(
            new ChatPreset
            {
                Id = "t",
                Manifest = ChatPresets.Llama32_1BInstructInt4.Manifest,
                Shape = ChatPresets.Llama32_1BInstructInt4.Shape,
                DisplayName = "t",
                StopSequences = ["ab", "abc"],
            },
            options: null,
            perCall: null));

        // "xab" contains the shorter stop, but the longer one could still complete - so it waits.
        Assert.Equal("x", matcher.Push("xab", out var matched));
        Assert.False(matched);

        // "c" completes the longer stop, and that is the one that fires.
        Assert.Equal(string.Empty, matcher.Push("c", out matched));
        Assert.True(matched);
    }

    [Fact]
    public void AShorterStopFiresOnceTheLongerOneCanNoLongerComplete()
    {
        var matcher = new StopSequenceMatcher(["abc", "ab"]);

        Assert.Equal("x", matcher.Push("xab", out var matched));
        Assert.False(matched);

        Assert.Equal(string.Empty, matcher.Push("z", out matched));
        Assert.True(matched);
    }

    [Fact]
    public void TheBufferNeverHoldsMoreThanTheLongestStopMinusOne()
    {
        var matcher = new StopSequenceMatcher(["<|eot_id|>"]);
        var held = 0;

        foreach (var fragment in new[] { "lots of ordinary text ", "<", "|", "eo", "t_", "id", "|", "and more" })
        {
            matcher.Push(fragment, out _);
            held = Math.Max(held, matcher.Pending.Length);
        }

        Assert.True(held <= matcher.Longest - 1);
        Assert.Equal(10, matcher.Longest);
    }

    [Fact]
    public void FinishReleasesTheHeldTextAndReportsAStopThatWasComplete()
    {
        var matcher = new StopSequenceMatcher(["END"]);

        Assert.Equal("text ", matcher.Push("text EN", out var matched));
        Assert.False(matched);

        Assert.Equal("EN", matcher.Finish(out matched));
        Assert.False(matched);

        matcher.Push("done END", out matched);
        Assert.True(matched);
    }

    [Fact]
    public void NoStopSequencesMeansEverythingIsEmittedImmediately()
    {
        var matcher = new StopSequenceMatcher([]);

        Assert.Equal("anything<|", matcher.Push("anything<|", out var matched));
        Assert.False(matched);
        Assert.Equal(0, matcher.Pending.Length);
    }
}
