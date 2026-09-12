using Xunit;

namespace Qavren.Edge.Rag.Tests;

/// <summary>
/// Spec 7's <see cref="RetrievalScoreKind"/> exists to stop one bug: the vector lane returns a
/// distance and the hybrid lane returns an RRF score, so reading one as the other inverts every
/// ranking silently. These tests name the inversion, because that is what they are here to prevent.
/// </summary>
public class RagPromptsRankTests
{
    private static IReadOnlyList<RagSource> WithKind(RetrievalScoreKind kind) =>
    [
        new RagSource("high", "high") { Score = 0.9d, ScoreKind = kind },
        new RagSource("low", "low") { Score = 0.1d, ScoreKind = kind },
        new RagSource("mid", "mid") { Score = 0.5d, ScoreKind = kind },
    ];

    private static string[] Ids(IReadOnlyList<RagSource> sources) => [.. sources.Select(s => s.Id)];

    [Fact]
    public void ADistanceListComesOutAscendingBecauseLowerIsBetter()
    {
        var ranked = RagPrompts.Rank(WithKind(RetrievalScoreKind.Distance));

        Assert.Equal(["low", "mid", "high"], Ids(ranked));
    }

    [Fact]
    public void ARelevanceListComesOutDescendingBecauseHigherIsBetter()
    {
        var ranked = RagPrompts.Rank(WithKind(RetrievalScoreKind.Relevance));

        Assert.Equal(["high", "mid", "low"], Ids(ranked));
    }

    [Fact]
    public void TheSameScoresProduceOppositeOrdersUnderTheTwoKindsWhichIsTheInversionScoreKindPrevents()
    {
        var distance = Ids(RagPrompts.Rank(WithKind(RetrievalScoreKind.Distance)));
        var relevance = Ids(RagPrompts.Rank(WithKind(RetrievalScoreKind.Relevance)));

        Assert.NotEqual(distance, relevance);
        for (var i = 0; i < distance.Length; i++)
        {
            Assert.Equal(distance[i], relevance[relevance.Length - 1 - i]);
        }
    }

    [Fact]
    public void RankModifiesNoScoreAndRescalesNothing()
    {
        var input = WithKind(RetrievalScoreKind.Relevance);
        var before = input.ToDictionary(s => s.Id, s => s.Score);

        var ranked = RagPrompts.Rank(input);

        Assert.Equal(input.Count, ranked.Count);
        foreach (var source in ranked)
        {
            Assert.Equal(before[source.Id], source.Score);
        }

        // Same instances, reordered - Rank hands back what the retriever stamped.
        Assert.All(ranked, source => Assert.Contains(input, original => ReferenceEquals(original, source)));
    }

    [Fact]
    public void RankLeavesOrdinalAloneBecauseItIsAPositionInTheBlockAndNotAPropertyOfTheSource()
    {
        var ranked = RagPrompts.Rank(WithKind(RetrievalScoreKind.Distance));

        Assert.All(ranked, source => Assert.Equal(0, source.Ordinal));
    }

    [Fact]
    public void EqualScoresKeepTheRetrieversOrderBecauseTheSortIsStable()
    {
        IReadOnlyList<RagSource> tied =
        [
            new RagSource("first", "a") { Score = 0.5d, ScoreKind = RetrievalScoreKind.Relevance },
            new RagSource("second", "b") { Score = 0.5d, ScoreKind = RetrievalScoreKind.Relevance },
            new RagSource("third", "c") { Score = 0.5d, ScoreKind = RetrievalScoreKind.Relevance },
        ];

        Assert.Equal(["first", "second", "third"], Ids(RagPrompts.Rank(tied)));
    }
}
