using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Rag.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Rag.Tests;

/// <summary>
/// Spec 14.4, plan adjustment 27. "Prompt and completion text are logged at <c>Trace</c> only and
/// never above" is a privacy contract, because a RAG prompt contains the user's private corpus.
/// This is the assertion, not the convention.
/// </summary>
public class LoggingPrivacyTests
{
    private const string Question = "QUESTIONMARKERZZZ";
    private const string Chunk = "CHUNKMARKERZZZ retrieved body";
    private const string Answer = "ANSWERMARKERZZZ [1].";

    private static readonly string[] Private = [Question, Chunk, "ANSWERMARKERZZZ"];

    private static IReadOnlyList<RagSource> Corpus() =>
        [new RagSource("chunk-private", Chunk) { Title = "Private", ScoreKind = RetrievalScoreKind.Distance }];

    private static async Task DriveAsync(IChatClient client, ILoggerFactory factory)
    {
        _ = factory;
        await client
            .GetResponseAsync(
                [new ChatMessage(ChatRole.User, Question)],
                cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task AtTraceThePrivateStringsAppearOnlyInTraceRecords()
    {
        using var factory = new RecordingLoggerFactory(LogLevel.Trace);
        using var inner = new FakeChatClient(Answer);
        using var client = new RagChatClient(inner, new FakeRetriever(Corpus()), null, factory);

        await DriveAsync(client, factory).ConfigureAwait(true);

        var carrying = factory.Records
            .Where(r => Private.Any(p => r.Message.Contains(p, StringComparison.Ordinal)))
            .ToList();

        Assert.NotEmpty(carrying);
        Assert.All(carrying, record => Assert.Equal(LogLevel.Trace, record.Level));
    }

    [Fact]
    public async Task AtDebugNoneOfThePrivateStringsAppearsInAnyRecordAndTheTurnStillLogsItsCounts()
    {
        using var factory = new RecordingLoggerFactory(LogLevel.Debug);
        using var inner = new FakeChatClient(Answer);
        using var client = new RagChatClient(inner, new FakeRetriever(Corpus()), null, factory);

        await DriveAsync(client, factory).ConfigureAwait(true);

        Assert.DoesNotContain(
            factory.Records,
            record => Private.Any(p => record.Message.Contains(p, StringComparison.Ordinal)));

        Assert.Contains(factory.Records, r => r.EventId.Id == EdgeRagEventIds.Retrieved);
        Assert.Contains(factory.Records, r => r.EventId.Id == EdgeRagEventIds.ContextAssembled);
        Assert.Contains(factory.Records, r => r.EventId.Id == EdgeRagEventIds.CitationsAttached);
    }

    [Fact]
    public async Task TheExtractiveFloorObeysTheSameRuleAtTrace()
    {
        using var factory = new RecordingLoggerFactory(LogLevel.Trace);
        using var floor = new ExtractiveChatClient(null, factory);
        using var client = new RagChatClient(floor, new FakeRetriever(Corpus()), null, factory);

        await DriveAsync(client, factory).ConfigureAwait(true);

        var carrying = factory.Records
            .Where(r => r.Message.Contains(Chunk, StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(carrying);
        Assert.All(carrying, record => Assert.Equal(LogLevel.Trace, record.Level));
    }

    [Fact]
    public async Task TheExtractiveFloorLeaksNothingAtDebugEvenThoughItsAnswerIsTheCorpus()
    {
        using var factory = new RecordingLoggerFactory(LogLevel.Debug);
        using var floor = new ExtractiveChatClient(null, factory);
        using var client = new RagChatClient(floor, new FakeRetriever(Corpus()), null, factory);

        await DriveAsync(client, factory).ConfigureAwait(true);

        Assert.DoesNotContain(
            factory.Records,
            record => record.Message.Contains(Chunk, StringComparison.Ordinal));

        Assert.Contains(factory.Records, r => r.EventId.Id == EdgeRagEventIds.ExtractiveAnswer);
    }
}
