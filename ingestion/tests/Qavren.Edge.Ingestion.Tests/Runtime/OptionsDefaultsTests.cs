using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// Every default in the plan's "Defaults that change behaviour" headings, asserted, so a silent
/// change to one is a failing test rather than a behaviour change.
/// </summary>
public sealed class OptionsDefaultsTests
{
    [Fact]
    public void IngestionOptions_defaults()
    {
        var o = new IngestionOptions();

        Assert.Equal(ChunkModelProfile.MiniLmL6V2Int8, o.Model);
        Assert.Null(o.Dimensions);
        Assert.Equal("chunks", o.CollectionName);
        Assert.Equal(Microsoft.Extensions.VectorData.DistanceFunction.CosineDistance, o.DistanceFunction);
        Assert.True(o.FullTextIndexed);
        Assert.Equal(2, o.FullTextRemoveDiacritics);
        Assert.Equal("qedge_ingest", o.StateTablePrefix);
        Assert.Equal(ChunkerIds.Auto, o.ChunkerId);
        Assert.Equal(32L * 1024 * 1024, o.MaxDocumentBytes);
        Assert.Equal(32, o.WriteBatchSize);
        Assert.Equal(500, o.DeleteBatchSize);
        Assert.False(o.SkipUnchangedByTimestamp);
        Assert.True(o.DeleteMissingDocuments);
        Assert.True(o.RepairOrdinals);
        Assert.True(o.ContinueOnDocumentError);
        Assert.Equal(20, o.AbortAfterConsecutiveErrors);
        Assert.False(o.StrictRecipe);
        Assert.Equal(ThrottlePauseBehavior.Suspend, o.PauseBehavior);
        Assert.Equal(TimeSpan.FromMilliseconds(750), o.SleepGraceBudget);
        Assert.Equal(TimeSpan.FromSeconds(5), o.StopGraceBudget);
        Assert.Equal(20, o.RunHistoryLimit);
        Assert.False(o.IncludeCountsInDiagnostics);
        Assert.Empty(o.Extractors);
    }

    [Fact]
    public void ChunkOptions_and_ExtractionOptions_defaults()
    {
        var c = new ChunkOptions();
        Assert.Null(c.MaxTokens);
        Assert.Null(c.OverlapTokens);
        Assert.Null(c.MinTokens);
        Assert.Equal(32, c.HeadingPathTokenBudget);
        Assert.Equal([1, 2, 3], c.SplitHeadingLevels);
        Assert.True(c.PrependHeadingPath);
        Assert.True(c.IncludePreamble);
        Assert.True(c.MergeShortSections);
        Assert.True(c.SentenceAware);
        Assert.True(c.RepeatTableHeaderRow);
        Assert.Equal(ChunkOverflow.Split, c.Overflow);

        var e = new ExtractionOptions();
        Assert.Equal(4L * 1024 * 1024, e.NonSeekableBufferLimitBytes);
        Assert.True(e.NormalizeText);
    }

    /// <summary>
    /// The eight <see cref="IngestionRunOptions"/> members. Every one is null-or-false by design,
    /// which is not the same as arbitrary — the plan's Task 5.1 heading says what each means.
    /// </summary>
    [Fact]
    public void IngestionRunOptions_defaults()
    {
        var r = new IngestionRunOptions();

        Assert.Null(r.Budget);
        Assert.Null(r.Progress);
        Assert.False(r.Force);
        Assert.Null(r.DeleteMissing);
        Assert.False(r.FailFast);
        Assert.False(r.ThrowOnCancellation);
        Assert.Null(r.WriteBatchSize);
        Assert.Null(r.Filter);
    }

    /// <summary>
    /// All twelve members of the three presets. <c>Quick</c> is <c>MaxDuration = 20 s</c> and
    /// nothing else; <c>Background</c> is <c>MaxDuration = 5 min</c> and nothing else. Spec 10.2
    /// pinned them no harder than "~20 s" and "~5 min", and a tilde is not a pinnable default.
    /// </summary>
    [Fact]
    public void IngestionBudget_presets_are_literal()
    {
        Assert.Null(IngestionBudget.Unlimited.MaxDuration);
        Assert.Null(IngestionBudget.Unlimited.MaxDocuments);
        Assert.Null(IngestionBudget.Unlimited.MaxChunks);
        Assert.Null(IngestionBudget.Unlimited.MaxTokens);

        Assert.Equal(TimeSpan.FromSeconds(20), IngestionBudget.Quick.MaxDuration);
        Assert.Null(IngestionBudget.Quick.MaxDocuments);
        Assert.Null(IngestionBudget.Quick.MaxChunks);
        Assert.Null(IngestionBudget.Quick.MaxTokens);

        Assert.Equal(TimeSpan.FromMinutes(5), IngestionBudget.Background.MaxDuration);
        Assert.Null(IngestionBudget.Background.MaxDocuments);
        Assert.Null(IngestionBudget.Background.MaxChunks);
        Assert.Null(IngestionBudget.Background.MaxTokens);
    }

    [Fact]
    public void The_chunk_budget_resolves_to_the_pinned_triple()
    {
        var resolved = new ChunkOptions().Resolve(ChunkModelProfile.MiniLmL6V2Int8, new FakeChunkTokenizer());

        Assert.Equal(222, resolved.MaxTokens);
        Assert.Equal(32, resolved.OverlapTokens);
        Assert.Equal(27, resolved.MinTokens);
    }

    [Fact]
    public void FixedIngestionThrottle_never_pauses()
    {
        var decision = new FixedIngestionThrottle()
            .Evaluate(new IngestionThrottleContext(32, 32, 0, 0, TimeSpan.Zero));

        Assert.Equal(32, decision.BatchSize);
        Assert.Equal(TimeSpan.Zero, decision.Delay);
        Assert.False(decision.Pause);
        Assert.Null(decision.Reason);
    }
}
