using System.Text.RegularExpressions;
using Xunit;

namespace Qavren.Edge.Chat.Tests.Tier3;

/// <summary>
/// Plan adjustment 26's fourth assertion, and deliberately NOT a tier-3 fact: a <b>tier-1</b> test
/// that runs on every PR, every host lane and every device lane. It carries no <c>SkipUnless</c>,
/// reads no environment variable and loads no model - it reads the committed <c>corpus.json</c>
/// and guards the invariant that gives the nightly's three answer assertions their meaning.
/// </summary>
/// <remarks>
/// "No other chunk contains the token" is what makes "the answer contains the token" mean <i>the
/// model read the gold chunk</i> rather than <i>the model emitted a common word</i>. An edit that
/// adds "seven" or a bare "7" to any other chunk would make that assertion pass vacuously, forever,
/// with every nightly green. This test turns that PR red in the wave that makes it.
/// </remarks>
public sealed partial class CorpusInvariantTests
{
    /// <summary>Exactly what plan adjustment 26 specifies: <c>seven|\b7\b</c>, case-insensitive.</summary>
    [GeneratedRegex(@"seven|\b7\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RequiredTokenPattern();

    [Fact]
    public void TheCorpusHasExactlyTwentyChunksWithUniqueNonEmptyIdsAndNonEmptyText()
    {
        var corpus = RealModelFacts.LoadCorpus();

        Assert.Equal(20, corpus.Count);
        Assert.All(corpus, chunk => Assert.False(string.IsNullOrWhiteSpace(chunk.Id), "an id is empty"));
        Assert.All(corpus, chunk => Assert.False(string.IsNullOrWhiteSpace(chunk.Text), $"'{chunk.Id}' has no text"));
        Assert.All(corpus, chunk => Assert.False(string.IsNullOrWhiteSpace(chunk.Title), $"'{chunk.Id}' has no title"));

        var duplicates = corpus.GroupBy(c => c.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.True(duplicates.Count == 0, $"duplicate ids: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void TheGoldChunkIsPresentAndCarriesTheGoldSentenceVerbatim()
    {
        var corpus = RealModelFacts.LoadCorpus();

        var gold = Assert.Single(corpus, c => c.Id == RealModelFacts.GoldChunkId);
        Assert.Contains(RealModelFacts.GoldSentence, gold.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactlyOneChunkMatchesSevenOrABareSevenAndItIsTheGoldChunk()
    {
        var corpus = RealModelFacts.LoadCorpus();

        var matching = corpus
            .Where(c => RequiredTokenPattern().IsMatch(c.Title + " " + c.Text))
            .Select(c => c.Id)
            .ToList();

        // The failure message lists EVERY matching id, so an editor sees immediately which chunk
        // they poisoned.
        Assert.True(
            matching.Count == 1 && matching[0] == RealModelFacts.GoldChunkId,
            $"exactly one chunk may match /seven|\\b7\\b/i and it must be '{RealModelFacts.GoldChunkId}'; " +
            $"matching: [{string.Join(", ", matching)}]");
    }

    [Fact]
    public void TheFactsAndThisInvariantReadTheSameQuestionAndTheSameCorpus()
    {
        // By reference, not retyped: the constants above ARE the tier-3 body's constants, so the
        // invariant cannot be checked against one corpus while the nightly answers over another.
        Assert.Equal("How many years does the warranty cover the compressor?", RealModelFacts.RagQuestion);
        Assert.Equal("warranty-compressor", RealModelFacts.GoldChunkId);
        Assert.Equal("the sealed compressor is covered for seven years from the date of purchase", RealModelFacts.GoldSentence);
        Assert.Equal("corpus.json", RealModelFacts.CorpusResourceName);

        // The resource the facts load is the one this assembly embeds, and it parses to 20.
        using var stream = typeof(RealModelFacts).Assembly.GetManifestResourceStream(RealModelFacts.CorpusResourceName);
        Assert.NotNull(stream);
        Assert.Equal(20, RealModelFacts.LoadCorpus().Count);

        // The required-token rule the facts apply is the same one the exclusivity clause guards.
        Assert.True(RealModelFacts.ContainsRequiredToken("It is covered for SEVEN years."));
        Assert.True(RealModelFacts.ContainsRequiredToken("7 years [1]."));
        Assert.False(RealModelFacts.ContainsRequiredToken("It is covered for 17 years."));
        Assert.False(RealModelFacts.ContainsRequiredToken("two years"));
    }
}
