using Microsoft.Extensions.AI;
using Qavren.Edge.Rag.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Rag.Tests;

/// <summary>
/// Plan adjustment 23: every 7200-range <c>EdgeErrorCode</c> is raised by a minimal reproduction
/// here, and the key-set guard turns a code added to the enum later red rather than letting it pass
/// silently. The <c>foundation/docs/errors.md</c> anchor test is a different test in a different
/// project - it reads a file, not a throw.
/// </summary>
public class RagErrorCodeCoverageTests
{
    private static readonly IReadOnlyDictionary<EdgeErrorCode, Func<Task>> Reproductions =
        new Dictionary<EdgeErrorCode, Func<Task>>
        {
            [EdgeErrorCode.RagRetrieverMissing] = () =>
            {
                using var leaf = new FakeChatClient("unused");
                new ChatClientBuilder(leaf).UseRag().Build();
                return Task.CompletedTask;
            },

            [EdgeErrorCode.RagRetrievalFailed] = async () =>
            {
                var retriever = new VectorStoreRetriever<string, TestRecord>(
                    new FakeVectorCollection("notes", TestCorpus.Rows()),
                    r => new RagSource(r.Key, r.Text));

                await retriever
                    .RetrieveAsync(
                        "warranty?",
                        new RetrievalRequest { Top = 4097 },
                        TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
            },

            [EdgeErrorCode.RagCollectionNotSearchable] = async () =>
            {
                var retriever = new VectorStoreRetriever<string, TestRecord>(
                    new FakeVectorCollection("notes", TestCorpus.Rows()),
                    r => new RagSource(r.Key, r.Text),
                    o => o.RequireHybridSearch = true);

                await retriever
                    .RetrieveAsync(
                        "warranty?",
                        new RetrievalRequest { Top = 3, Keywords = ["warranty"] },
                        TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
            },

            // The same too-small-budget case RagPromptsFormatTests uses, so the two tests cannot
            // disagree about what raises 7204.
            [EdgeErrorCode.RagContextBudgetTooSmall] = () =>
            {
                RagPrompts.Format(
                    TestCorpus.Two(),
                    new RagOptions { MaxCharsPerSource = 40, MaxContextTokens = 1 });
                return Task.CompletedTask;
            },
        };

    public static TheoryData<EdgeErrorCode> Codes()
    {
        var data = new TheoryData<EdgeErrorCode>();
        foreach (var code in Reproductions.Keys)
        {
            data.Add(code);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public async Task EveryRagErrorCodeIsRaisedByItsMinimalReproduction(EdgeErrorCode code)
    {
        var exception = await Assert
            .ThrowsAsync<EdgeRagException>(async () => await Reproductions[code]().ConfigureAwait(true))
            .ConfigureAwait(true);

        Assert.Equal(code, exception.Code);
    }

    [Fact]
    public void TheKeySetIsExactlyTheAssemblysRangeOfTheEnum()
    {
        var declared = Enum.GetValues<EdgeErrorCode>()
            .Where(c => (int)c is >= 7200 and <= 7299)
            .OrderBy(c => (int)c)
            .ToList();

        var covered = Reproductions.Keys.OrderBy(c => (int)c).ToList();

        Assert.Equal(declared, covered);
    }
}
