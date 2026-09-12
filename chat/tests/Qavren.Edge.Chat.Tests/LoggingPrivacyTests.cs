using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Chat.Tests.Fakes;
using Qavren.Edge.Onnx;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Spec 14.4, plan adjustment 27 - the chat half. "Prompt and completion text are logged at
/// <c>Trace</c> only and never above" is a privacy contract, because a RAG prompt contains the
/// user's private corpus. This is the assertion, not the convention.
/// </summary>
public class LoggingPrivacyTests
{
    private const string Question = "QUESTIONMARKERZZZ";
    private const string OlderQuestion = "OLDERMARKERZZZ";
    private const string Completion = "ANSWERMARKERZZZ";

    private static readonly string[] Private = [Question, OlderQuestion, Completion];

    /// <summary>
    /// A turn that reduces history (935) and throttles mid-decode (936), so both count-only lines
    /// have to appear beside the text-carrying ones.
    /// </summary>
    private static async Task<ClientHarness> DriveAsync(LogLevel level)
    {
        var harness = ClientHarness.Build(
            o =>
            {
                o.History.MaxTurns = 1;
                o.History.MinimumPreservedMessages = 1;
                o.Thermal.ThrottledTokensPerSecond = 100_000;
            },
            script: [Completion, " and", " more"],
            minimumLogLevel: level);

        harness.Session.OnGeneratorCreated = generator => generator.BeforeGenerate = (_, index) =>
        {
            if (index == 1)
            {
                harness.Monitor.SetSnapshot(new EdgeResourceSnapshot(10_000_000_000L, false, EdgeThermalState.Serious, null, false, null));
                harness.Clock.Advance(TimeSpan.FromSeconds(1));
            }
        };

        ChatMessage[] history =
        [
            new(ChatRole.User, OlderQuestion),
            new(ChatRole.Assistant, "an older answer"),
            new(ChatRole.User, Question),
        ];

        var response = await harness.Client.GetResponseAsync(history, null, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(Completion + " and more", response.Text);
        Assert.Equal(2, response.GetTurnStatus()!.MessagesDropped);
        Assert.True(response.GetTurnStatus()!.ThermalThrottled);

        return harness;
    }

    [Fact]
    public async Task AtTraceThePrivateStringsAppearOnlyInTraceRecords()
    {
        using var harness = await DriveAsync(LogLevel.Trace).ConfigureAwait(true);

        var carrying = harness.Logger.Records
            .Where(r => Private.Any(p => r.Message.Contains(p, StringComparison.Ordinal)))
            .ToList();

        Assert.NotEmpty(carrying);
        Assert.All(carrying, record => Assert.Equal(LogLevel.Trace, record.Level));

        // The two places a RAG block would otherwise leak: the formatted prompt and the reduced
        // message list are both present, and both Trace.
        var prompt = Assert.Single(harness.Logger.Records, r => r.Message.StartsWith("Formatted prompt:", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Trace, prompt.Level);
        Assert.Contains("<user>" + Question + "</user>", prompt.Message, StringComparison.Ordinal);

        var reduced = Assert.Single(harness.Logger.Records, r => r.Message.StartsWith("Reduced messages:", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Trace, reduced.Level);
        Assert.Contains(Question, reduced.Message, StringComparison.Ordinal);

        // The cached conversation text and every decoded token, likewise.
        Assert.All(harness.Logger.Records.Where(r => r.Message.StartsWith("Conversation cache text:", StringComparison.Ordinal)), r => Assert.Equal(LogLevel.Trace, r.Level));
        Assert.All(harness.Logger.Records.Where(r => r.Message.StartsWith("Decoded token", StringComparison.Ordinal)), r => Assert.Equal(LogLevel.Trace, r.Level));
    }

    [Fact]
    public async Task AtDebugNoneOfThePrivateStringsAppearsInAnyRecordWhileTheCountsStillLog()
    {
        using var harness = await DriveAsync(LogLevel.Debug).ConfigureAwait(true);

        Assert.DoesNotContain(
            harness.Logger.Records,
            record => Private.Any(p => record.Message.Contains(p, StringComparison.Ordinal)));
        Assert.DoesNotContain(harness.Logger.Records, r => r.Message.Contains("<user>", StringComparison.Ordinal));

        Assert.True(harness.Logger.Logged(EdgeChatEventIds.TurnStarted));
        Assert.True(harness.Logger.Logged(EdgeChatEventIds.TurnCompleted));
        Assert.True(harness.Logger.Logged(EdgeChatEventIds.HistoryReduced));
        Assert.True(harness.Logger.Logged(EdgeChatEventIds.ThermalThrottled));

        // And what those lines carry is counts, reasons and durations.
        var completed = Assert.Single(harness.Logger.For(EdgeChatEventIds.TurnCompleted));
        Assert.Contains("Completed", completed.Message, StringComparison.Ordinal);
        Assert.Contains("3 token(s)", completed.Message, StringComparison.Ordinal);

        var reduced = Assert.Single(harness.Logger.For(EdgeChatEventIds.HistoryReduced));
        Assert.Contains("2 of 3", reduced.Message, StringComparison.Ordinal);
    }
}
