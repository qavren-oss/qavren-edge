using Microsoft.Extensions.AI;
using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Embeddings.Tests.Fakes;
using Qavren.Edge.Onnx;
using Xunit;

namespace Qavren.Edge.Embeddings.Tests;

/// <summary>
/// <see cref="OnnxEmbeddingGenerator"/> over the tier-1 fixtures. These create REAL ORT CPU
/// sessions through the <c>InternalsVisibleTo</c>-scoped byte-array registration hook.
/// <para>
/// The inputs are chosen, not arbitrary: the fixture graph gathers rows out of a 16 x 4 embedding
/// table, so every token id a test produces has to be below 16. <c>b</c>, <c>c</c>, <c>d</c> and
/// <c>e</c> are single-letter vocabulary entries at ids 6-9, and any word outside the 64-entry
/// fixture vocabulary is <c>[UNK]</c> at id 1. <c>a</c> is deliberately avoided: it appears twice
/// in the fixture vocabulary, so its id depends on which entry wins.
/// </para>
/// </summary>
public class GeneratorTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EveryEmbeddingCarriesModelIdAndCreatedAtAndUsageCountsUnpaddedTokens()
    {
        var clock = new FixedTimeProvider(Noon);
        using var fixture = GeneratorFixture.CreateOverHiddenStatesFixture(clock);

        // Two inputs of different lengths, so the padded width and the real token count differ and
        // a Usage computed from BatchSize * SequenceLength would be visibly wrong rather than
        // plausible. Neither word is in the fixture vocabulary, so each is one [UNK].
        var result = await fixture.Generator.GenerateAsync(
            ["alpha", "alpha beta gamma"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Equal(fixture.Generator.Info.ModelId, e.ModelId));
        Assert.All(result, e => Assert.Equal(clock.GetUtcNow(), e.CreatedAt));

        // 1 + [CLS] + [SEP] = 3, and 3 + [CLS] + [SEP] = 5. Unpadded: the batch was padded to 8
        // per row, so a padded count would report 16 and a per-row max would report 10.
        Assert.Equal(8L, result.Usage!.InputTokenCount);
        Assert.Null(result.Usage.OutputTokenCount);
        Assert.Null(result.Usage.TotalTokenCount);
    }

    [Fact]
    public async Task AdditionalPropertiesAndRawRepresentationFactoryAreIgnoredRatherThanHonouredOrRejected()
    {
        using var fixture = GeneratorFixture.CreateOverHiddenStatesFixture(TimeProvider.System);
        var invoked = false;

        var options = new EmbeddingGenerationOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["temperature"] = 0.7f },
            RawRepresentationFactory = _ =>
            {
                invoked = true;
                return new object();
            },
        };

        var result = await fixture.Generator.GenerateAsync(
            ["alpha"],
            options,
            TestContext.Current.CancellationToken);

        // Spec 11: "AdditionalProperties and RawRepresentationFactory are ignored." Ignored means
        // exactly this - no throw, no behaviour change, and the factory is never called. A raw
        // representation here would be a SessionOptions and three OrtValues disposed before this
        // method returns; handing a consumer disposed native memory is worse than handing them
        // none. MEAI 10.10.0's Embedding carries no RawRepresentation member to put one on, which
        // is why nothing is asserted about one.
        Assert.Single(result);
        Assert.False(invoked);
        Assert.Null(result[0].AdditionalProperties);
    }

    [Fact]
    public async Task OneEmbeddingPerInputInInputOrderAcrossARaggedBatch()
    {
        using var fixture = GeneratorFixture.CreateOverHiddenStatesFixture(
            TimeProvider.System,
            configure: o => o.MaxBatchSize = 1);

        string[] inputs = ["b", "c", "d e"];

        var batched = await fixture.Generator.GenerateAsync(inputs, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, batched.Count);

        // Three inputs at MaxBatchSize 1 is three Runs. The results are appended in the order the
        // inputs arrived, never in completion order.
        for (var i = 0; i < inputs.Length; i++)
        {
            var single = await fixture.Generator.GenerateAsync(
                [inputs[i]],
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(single[0].Vector.ToArray(), batched[i].Vector.ToArray());
        }

        // And the three are genuinely different vectors, so "in order" is a claim with teeth.
        Assert.NotEqual(batched[0].Vector.ToArray(), batched[1].Vector.ToArray());
        Assert.NotEqual(batched[1].Vector.ToArray(), batched[2].Vector.ToArray());
    }

    [Fact]
    public async Task AnEmptyInputSequenceReturnsAnEmptyCollectionWithoutTouchingOrt()
    {
        using var fixture = GeneratorFixture.CreateOverHiddenStatesFixture(TimeProvider.System);

        var result = await fixture.Generator.GenerateAsync([], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result);

        // No session was ever created, which is the strongest available statement of "ORT was not
        // touched": Describe returns null until the first load.
        Assert.Null(fixture.Host.Describe("fixture-hidden-states"));
        Assert.Empty(fixture.Tokenizer.Seen);
    }

    [Fact]
    public async Task EightConcurrentCallsAllSucceed()
    {
        using var fixture = GeneratorFixture.CreateOverHiddenStatesFixture(TimeProvider.System);

        var callers = new Task<GeneratedEmbeddings<Embedding<float>>>[8];
        for (var i = 0; i < callers.Length; i++)
        {
            callers[i] = Task.Run(
                () => fixture.Generator.GenerateAsync(["b", "c"], cancellationToken: TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken);
        }

        var results = await Task.WhenAll(callers);

        Assert.All(results, r => Assert.Equal(2, r.Count));
        Assert.All(results, r => Assert.Equal(results[0][0].Vector.ToArray(), r[0].Vector.ToArray()));
        Assert.Equal(1, fixture.Host.Describe("fixture-hidden-states")!.LoadCount);
    }

    [Fact]
    public async Task GetServiceReturnsTheDocumentedSequence()
    {
        using var fixture = GeneratorFixture.CreateOverHiddenStatesFixture(TimeProvider.System);
        var generator = fixture.Generator;

        _ = await generator.GenerateAsync(["b"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(generator, generator.GetService(typeof(OnnxEmbeddingGenerator)));
        Assert.Same(generator, generator.GetService(typeof(IEmbeddingGenerator<string, Embedding<float>>)));
        Assert.Same(generator, generator.GetService(typeof(IEmbeddingGenerator)));
        Assert.IsType<EmbeddingGeneratorMetadata>(generator.GetService(typeof(EmbeddingGeneratorMetadata)));
        Assert.IsType<OnnxEmbeddingGeneratorInfo>(generator.GetService(typeof(OnnxEmbeddingGeneratorInfo)));
        Assert.IsType<EmbeddingPreset>(generator.GetService(typeof(EmbeddingPreset)));
        Assert.IsType<OnnxSessionInfo>(generator.GetService(typeof(OnnxSessionInfo)));
        Assert.IsType<RecordingTokenizer>(generator.GetService(typeof(IEdgeTokenizer)));
        Assert.Null(generator.GetService(typeof(string)));
    }

    [Fact]
    public async Task GetServiceAndInfoReadTHISGeneratorsTokenizerNotTheProvidersMostRecentlyBuiltOne()
    {
        // The failure this pins: an app calls AddOnnxEmbeddings() (MiniLM, 256 tokens) plus
        // AddOnnxEmbeddings("bge", ... BgeSmallEnV15, 512), and the bge generator embeds first. One
        // EdgeTokenizerProvider serves both, and its Current property is documented as the MOST
        // RECENTLY BUILT tokenizer - so a generator that read Current would hand a caller the other
        // registration's tokenizer and report the other registration's vocabulary. No exception,
        // plausible and wrong.
        using var fixture = GeneratorFixture.CreateOverHiddenStatesFixture(TimeProvider.System);
        var generator = fixture.Generator;

        _ = await generator.GenerateAsync(["b"], cancellationToken: TestContext.Current.CancellationToken);

        var mine = Assert.IsType<RecordingTokenizer>(generator.GetService(typeof(IEdgeTokenizer)));
        var mineVocabSize = generator.Info.VocabularySize;

        using var foreign = new ForeignTokenizer(vocabularySize: mineVocabSize + 4242);
        fixture.Tokenizers.PretendAnotherRegistrationEmbedded(foreign);

        Assert.Same(foreign, fixture.Tokenizers.Current);
        Assert.Same(mine, generator.GetService(typeof(IEdgeTokenizer)));
        Assert.Equal(mineVocabSize, generator.Info.VocabularySize);

        // And the number the diagnostics block reads off this generator moves with neither.
        Assert.Equal(mineVocabSize, generator.Snapshot().VocabularySize);
    }

    [Fact]
    public void BeforeTheFirstEmbedThisGeneratorHasNOTokenizerEvenWhenAnotherRegistrationDoes()
    {
        using var fixture = GeneratorFixture.CreateOverHiddenStatesFixture(TimeProvider.System);

        using var foreign = new ForeignTokenizer(vocabularySize: 30_522);
        fixture.Tokenizers.PretendAnotherRegistrationEmbedded(foreign);

        // Null is the honest answer. Handing back the other preset's tokenizer would apply that
        // preset's MaxSequenceLength and LowerCase to this preset's text.
        Assert.Null(fixture.Generator.GetService(typeof(IEdgeTokenizer)));
        Assert.Equal(0, fixture.Generator.Info.VocabularySize);
    }

    [Fact]
    public void TheQuerySiblingIsReturnedForBothTheNonGenericAndTheClosedGenericServiceType()
    {
        using var fixture = GeneratorFixture.CreateOverHiddenStatesFixture(TimeProvider.System);
        var generator = fixture.Generator;

        // The vector store asks with the non-generic type; a direct caller usually asks with the
        // closed one. Both have to work, and both have to hand back the SAME sibling.
        var viaNonGeneric = generator.GetService(typeof(IEmbeddingGenerator), EdgeEmbeddings.QueryServiceKey);
        var viaGeneric = generator.GetService(
            typeof(IEmbeddingGenerator<string, Embedding<float>>),
            EdgeEmbeddings.QueryServiceKey);

        Assert.NotNull(viaNonGeneric);
        Assert.Same(viaNonGeneric, viaGeneric);
        Assert.NotSame(generator, viaNonGeneric);
        Assert.Equal(EmbeddingInputKind.Query, ((OnnxEmbeddingGenerator)viaNonGeneric).InputKind);

        // An unrecognised key resolves nothing rather than falling through to the unkeyed answers.
        Assert.Null(generator.GetService(typeof(IEmbeddingGenerator), "not-a-key"));
    }

    [Fact]
    public async Task TheUnkeyedGeneratorAppliesNoPrefixAndTheQuerySiblingAppliesTheQueryPrefix()
    {
        // A stand-in for bge's real prefix. It has to be out-of-vocabulary text: the fixture graph
        // gathers rows from a 16 x 4 table, and bge's own "Represent this sentence for searching
        // relevant passages: " tokenizes "searching" to the in-vocabulary ids 46 and 57, which are
        // past the end of that table. The SHIPPED prefix strings are asserted verbatim in
        // PresetCatalogueTests; what this test proves is which instance applies which one.
        const string prefix = "zzzz yyyy ";
        var preset = GeneratorFixture.Preset("fixture-hidden-states", queryPrefix: prefix);
        using var fixture = GeneratorFixture.CreateOverHiddenStatesFixture(TimeProvider.System, preset);

        _ = await fixture.Generator.GenerateAsync(["b"], cancellationToken: TestContext.Current.CancellationToken);

        var sibling = (OnnxEmbeddingGenerator)fixture.Generator
            .GetService(typeof(IEmbeddingGenerator), EdgeEmbeddings.QueryServiceKey)!;
        _ = await sibling.GenerateAsync(["b"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, fixture.Tokenizer.Seen.Count);
        Assert.Equal("b", fixture.Tokenizer.Seen[0]);
        Assert.Equal(prefix + "b", fixture.Tokenizer.Seen[1]);
    }

    [Fact]
    public async Task ADimensionsOptionOtherThanThePresetsThrowsNamingTheFixedWidth()
    {
        using var fixture = GeneratorFixture.CreateOverHiddenStatesFixture(TimeProvider.System);

        var ex = await Assert.ThrowsAsync<EdgeEmbeddingException>(() =>
            fixture.Generator.GenerateAsync(
                ["b"],
                new EmbeddingGenerationOptions { Dimensions = 8 },
                TestContext.Current.CancellationToken));

        Assert.Equal(EdgeErrorCode.EmbeddingDimensionMismatch, ex.Code);
        Assert.Equal(4, ex.Expected);
        Assert.Equal(8, ex.Actual);
        Assert.Contains("4-dimension", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSamePresetsDimensionsIsAccepted()
    {
        using var fixture = GeneratorFixture.CreateOverHiddenStatesFixture(TimeProvider.System);

        var result = await fixture.Generator.GenerateAsync(
            ["b"],
            new EmbeddingGenerationOptions { Dimensions = 4 },
            TestContext.Current.CancellationToken);

        Assert.Single(result);
    }

    [Fact]
    public async Task AMismatchedModelIdThrowsRatherThanBeingIgnored()
    {
        using var fixture = GeneratorFixture.CreateOverHiddenStatesFixture(TimeProvider.System);

        var ex = await Assert.ThrowsAsync<EdgeEmbeddingException>(() =>
            fixture.Generator.GenerateAsync(
                ["b"],
                new EmbeddingGenerationOptions { ModelId = "some-other-model" },
                TestContext.Current.CancellationToken));

        Assert.Contains("some-other-model", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BindingByNameSurvivesNomicsReversedInputOrder()
    {
        // nomic's graph declares (input_ids, token_type_ids, attention_mask) while MiniLM declares
        // (input_ids, attention_mask, token_type_ids). A positional binding would feed the mask in
        // as token-type ids and produce a plausible, wrong vector - never an exception.
        using var reference = GeneratorFixture.CreateOverHiddenStatesFixture(TimeProvider.System);
        using var reversed = GeneratorFixture.CreateOverNomicInputOrderFixture(TimeProvider.System);

        var expected = await reference.Generator.GenerateAsync(
            ["d e"],
            cancellationToken: TestContext.Current.CancellationToken);
        var actual = await reversed.Generator.GenerateAsync(
            ["d e"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected[0].Vector.ToArray(), actual[0].Vector.ToArray());
    }

    [Fact]
    public async Task AMaskedPositionIsNotPooledIntoTheVector()
    {
        // Padding an input to a wider bucket must not move its vector: that is the whole claim the
        // synthesised attention mask makes.
        using var narrow = GeneratorFixture.Create(
            Qavren.Edge.Tests.Fixtures.TinyModels.HiddenStates,
            ["input_ids", "attention_mask"],
            GeneratorFixture.Preset("fixture-hidden-states") with { SequenceBuckets = [4] },
            TimeProvider.System,
            configure: null);

        using var wide = GeneratorFixture.CreateOverHiddenStatesFixture(TimeProvider.System);

        var atFour = await narrow.Generator.GenerateAsync(["b"], cancellationToken: TestContext.Current.CancellationToken);
        var atEight = await wide.Generator.GenerateAsync(["b"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(atFour[0].Vector.ToArray(), atEight[0].Vector.ToArray());
    }

    [Fact]
    public async Task TruncationThrowWhenAnInputIsTooLong()
    {
        using var fixture = GeneratorFixture.CreateOverHiddenStatesFixture(
            TimeProvider.System,
            configure: o => o.Truncation = EmbeddingTruncation.Throw);

        var ex = await Assert.ThrowsAsync<EdgeEmbeddingException>(() =>
            fixture.Generator.GenerateAsync(
                ["b c d e b c d e b c d e"],
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(EdgeErrorCode.EmbeddingInputTooLong, ex.Code);
    }

    [Fact]
    public async Task TruncateIsTheDefaultAndLogsRatherThanThrowing()
    {
        using var fixture = GeneratorFixture.CreateOverHiddenStatesFixture(TimeProvider.System);

        var result = await fixture.Generator.GenerateAsync(
            ["b c d e b c d e b c d e"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal(4, result[0].Vector.Length);
    }

    [Fact]
    public async Task AGraphThatPoolsInItsOwnDeclaredOutputIsUsedVerbatim()
    {
        // TinyModels.MeanPool pools INSIDE the graph and declares a rank-2 [batch, dim] "embedding"
        // output. No shipped preset does that - spec 11 says all four emit last_hidden_state and
        // "pooling is always ours", and PresetCatalogueTests pins the four output names - so this
        // path is an extension for a consumer's own in-graph-pooled model, not a spec behaviour.
        // It is pinned here so it cannot be an accident: with no sequence axis left, preset.Pooling
        // is NOT applied (the preset below asks for Cls and gets the graph's mean), while
        // PostPoolLayerNorm and Normalize still run.
        var pooledInGraph = GeneratorFixture.Preset("fixture-mean-pool", pooling: EmbeddingPooling.Cls)
            with
            { OutputName = "embedding" };

        using var graph = GeneratorFixture.Create(
            Qavren.Edge.Tests.Fixtures.TinyModels.MeanPool,
            ["input_ids", "attention_mask"],
            pooledInGraph,
            TimeProvider.System,
            configure: null);

        using var ourMean = GeneratorFixture.CreateOverHiddenStatesFixture(TimeProvider.System);
        using var ourCls = GeneratorFixture.Create(
            Qavren.Edge.Tests.Fixtures.TinyModels.HiddenStates,
            ["input_ids", "attention_mask"],
            GeneratorFixture.Preset("fixture-hidden-states", pooling: EmbeddingPooling.Cls),
            TimeProvider.System,
            configure: null);

        var fromGraph = await graph.Generator.GenerateAsync(["b"], cancellationToken: TestContext.Current.CancellationToken);
        var fromMean = await ourMean.Generator.GenerateAsync(["b"], cancellationToken: TestContext.Current.CancellationToken);
        var fromCls = await ourCls.Generator.GenerateAsync(["b"], cancellationToken: TestContext.Current.CancellationToken);

        var graphVector = fromGraph[0].Vector.ToArray();
        var meanVector = fromMean[0].Vector.ToArray();

        for (var i = 0; i < graphVector.Length; i++)
        {
            Assert.Equal(meanVector[i], graphVector[i], 5);
        }

        Assert.NotEqual(fromCls[0].Vector.ToArray(), graphVector);
    }

    [Fact]
    public async Task ALatchedModeratePressureHalvesTheEffectiveBatchSize()
    {
        using var fixture = GeneratorFixture.CreateOverHiddenStatesFixture(
            TimeProvider.System,
            configure: o => o.MaxBatchSize = 4);

        fixture.Resources.SetPressure(Qavren.Edge.Lifecycle.EdgeMemoryPressure.Moderate);

        var result = await fixture.Generator.GenerateAsync(
            ["b", "c", "d", "e"],
            cancellationToken: TestContext.Current.CancellationToken);

        // Four inputs at an effective batch of 2 is two Runs, and the output order must not move.
        Assert.Equal(4, result.Count);

        var single = await fixture.Generator.GenerateAsync(["d"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(single[0].Vector.ToArray(), result[2].Vector.ToArray());

        Assert.Equal(2, fixture.Generator.Snapshot().EffectiveBatchSize);
    }

    [Fact]
    public async Task CriticalPressureDoesNotShrinkTheBatch_ThatReactionBelongsToL0()
    {
        // Spec 11 names a LATCHED MODERATE as the halving trigger and gives Critical a different
        // reaction entirely: the L0 lifecycle observer drops sessions (DropOnMemoryPressure).
        // Halving here as well would be a second, undocumented policy layered on the first.
        using var fixture = GeneratorFixture.CreateOverHiddenStatesFixture(
            TimeProvider.System,
            configure: o => o.MaxBatchSize = 4);

        fixture.Resources.SetPressure(Qavren.Edge.Lifecycle.EdgeMemoryPressure.Critical);

        var result = await fixture.Generator.GenerateAsync(
            ["b", "c", "d", "e"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, result.Count);
        Assert.Equal(4, fixture.Generator.Snapshot().EffectiveBatchSize);
    }

    [Fact]
    public async Task ShrinkBatchUnderMemoryPressureFalseKeepsTheConfiguredBatchUnderModerate()
    {
        using var fixture = GeneratorFixture.CreateOverHiddenStatesFixture(
            TimeProvider.System,
            configure: o =>
            {
                o.MaxBatchSize = 4;
                o.ShrinkBatchUnderMemoryPressure = false;
            });

        fixture.Resources.SetPressure(Qavren.Edge.Lifecycle.EdgeMemoryPressure.Moderate);

        _ = await fixture.Generator.GenerateAsync(
            ["b", "c", "d", "e"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, fixture.Generator.Snapshot().EffectiveBatchSize);
    }
}
