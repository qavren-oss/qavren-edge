using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Embeddings.Onnx.Internal;
using Qavren.Edge.Embeddings.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Embeddings.Tests;

/// <summary>
/// Spec 16.1's tokenizer tier, over the committed 64-entry <c>TinyModels.VocabTxt</c>. Its first
/// five entries are <c>[PAD] [UNK] [CLS] [SEP] [MASK]</c>, so the special ids are 0, 1, 2 and 3 -
/// and they are asserted BY ID rather than by presence, because a special-token name absent from
/// the vocabulary does not throw: it silently produces a sequence with no marker.
/// </summary>
public class TokenizerTests
{
    private const int Pad = 0;
    private const int Unknown = 1;
    private const int Classification = 2;
    private const int Separator = 3;

    [Fact]
    public void TheVocabularySizeIsTheLineCountOfVocabTxt()
    {
        using var tokenizer = TestVocabulary.Tokenizer();

        Assert.Equal(TestVocabulary.Size, tokenizer.VocabularySize);
        Assert.Equal(EdgeTokenizerKind.WordPieceVocabTxt, tokenizer.Kind);
    }

    [Fact]
    public void EncodeBracketsEveryInputWithClsAndSepAssertedById()
    {
        using var tokenizer = TestVocabulary.Tokenizer();
        Span<int> ids = stackalloc int[16];

        var count = tokenizer.Encode("water damage".AsSpan(), maxTokens: 16, ids, out _);

        Assert.Equal(4, count);
        Assert.Equal(Classification, ids[0]);
        Assert.Equal(Separator, ids[count - 1]);
    }

    [Fact]
    public void PadTokenIdIsThePadEntryAndPaddedPositionsCarryIt()
    {
        using var tokenizer = TestVocabulary.Tokenizer();

        Assert.Equal(Pad, tokenizer.PadTokenId);

        var batch = tokenizer.EncodeBatch(["water", "water damage note"], maxSequenceLength: 8, buckets: [8]);

        Assert.Equal(8, batch.SequenceLength);
        Assert.Equal(2, batch.BatchSize);

        // Row 0 is [CLS] water [SEP] then five pads; the mask marks exactly the three real tokens.
        Assert.Equal(3, batch.TokenCounts[0]);
        Assert.Equal(Pad, (int)batch.InputIds[3]);
        Assert.Equal([1L, 1L, 1L, 0L, 0L, 0L, 0L, 0L], batch.AttentionMask[..8]);
        Assert.Equal([1L, 1L, 1L, 1L, 1L, 0L, 0L, 0L], batch.AttentionMask[8..16]);
    }

    [Fact]
    public void TruncationIsInclusiveOfTheSpecialTokens()
    {
        using var tokenizer = TestVocabulary.Tokenizer();
        Span<int> ids = stackalloc int[4];

        // The truncating overload accounts for the two specials itself: it delegates with
        // maxTokenCount - 2, so maxTokens: 4 yields at most 4 ids INCLUDING [CLS] and [SEP].
        var count = tokenizer.Encode("water damage note search".AsSpan(), maxTokens: 4, ids, out var consumed);

        Assert.Equal(4, count);
        Assert.Equal(Classification, ids[0]);
        Assert.Equal(Separator, ids[3]);
        Assert.True(consumed < "water damage note search".Length);
    }

    [Fact]
    public void AMaxTokenCountBelowTwoLeavesNoRoomForTheSpecialsAndEncodesNothing()
    {
        using var tokenizer = TestVocabulary.Tokenizer();
        Span<int> ids = stackalloc int[2];

        Assert.Equal(0, tokenizer.Encode("water".AsSpan(), maxTokens: 1, ids, out _));
    }

    [Fact]
    public void EncodeRefusesADestinationShorterThanMaxTokens()
    {
        using var tokenizer = TestVocabulary.Tokenizer();

        var tooShort = new int[2];

        var ex = Record.Exception(() => tokenizer.Encode("water".AsSpan(), maxTokens: 8, tooShort, out _));

        Assert.IsType<ArgumentException>(ex);
    }

    [Fact]
    public void AnOverLongInputIsFlaggedTruncatedAndAShortOneIsNot()
    {
        using var tokenizer = TestVocabulary.Tokenizer(maxSequenceLength: 4);

        var batch = tokenizer.EncodeBatch(
            ["water", "water damage note search query document"],
            maxSequenceLength: 4,
            buckets: [4]);

        Assert.False(batch.Truncated[0]);
        Assert.True(batch.Truncated[1]);
    }

    [Fact]
    public void AWordOutsideTheVocabularyBecomesTheUnknownId()
    {
        using var tokenizer = TestVocabulary.Tokenizer();
        Span<int> ids = stackalloc int[8];

        var count = tokenizer.Encode("zzzzzz".AsSpan(), maxTokens: 8, ids, out _);

        Assert.Equal(3, count);
        Assert.Equal(Unknown, ids[1]);
    }

    [Fact]
    public void TheBatchRoundsUpToTheSmallestBucketThatFits()
    {
        using var tokenizer = TestVocabulary.Tokenizer(maxSequenceLength: 64);

        var narrow = tokenizer.EncodeBatch(["water"], maxSequenceLength: 64, buckets: [4, 8, 16]);
        var wide = tokenizer.EncodeBatch(["water damage note search query"], maxSequenceLength: 64, buckets: [4, 8, 16]);

        Assert.Equal(4, narrow.SequenceLength);
        Assert.Equal(8, wide.SequenceLength);
    }

    [Fact]
    public void TokenTypeIdsAreAllZeroForASingleSequenceEncoder()
    {
        using var tokenizer = TestVocabulary.Tokenizer();

        var batch = tokenizer.EncodeBatch(["water", "damage"], maxSequenceLength: 8, buckets: [8]);

        Assert.Equal(16, batch.TensorLength);
        Assert.All(batch.TokenTypeIds[..batch.TensorLength].ToArray(), id => Assert.Equal(0L, id));
    }

    [Fact]
    public void TheThreeTensorBuffersArePooledAndTheirCONTRACTIsTensorLengthNotArrayLength()
    {
        // Spec 11 rents the three long[] from ArrayPool<long>.Shared, and Rent is free to hand back
        // a LONGER array - the shared pool buckets by power of two, so a 16-element batch usually
        // arrives in a 16-element array and a 24-element one never does. Everything downstream
        // windows to TensorLength for exactly that reason, and the window must be clean: a rented
        // buffer carries the previous renter's bytes, so a missing Clear would show up here as a
        // stray 1 in the mask or a stray id in the padding.
        using var tokenizer = TestVocabulary.Tokenizer();

        var batch = tokenizer.EncodeBatch(["water", "water damage note"], maxSequenceLength: 8, buckets: [6]);

        Assert.Equal(12, batch.TensorLength);
        Assert.True(batch.InputIds.Length >= batch.TensorLength);
        Assert.True(batch.AttentionMask.Length >= batch.TensorLength);
        Assert.True(batch.TokenTypeIds.Length >= batch.TensorLength);

        // Row 0 is [CLS] water [SEP] + three pads; row 1 is [CLS] water damage note [SEP] + one.
        Assert.Equal([1L, 1L, 1L, 0L, 0L, 0L], batch.AttentionMask[..6]);
        Assert.Equal([1L, 1L, 1L, 1L, 1L, 0L], batch.AttentionMask[6..12]);
        Assert.Equal([Pad, Pad, Pad], batch.InputIds[3..6].Select(id => (int)id).ToArray());
    }

    [Fact]
    public async Task TheProviderCachesOneTokenizerPerPresetRatherThanOnePerProcess()
    {
        // The keyed AddOnnxEmbeddings(name, ...) overload exists so two presets can coexist in one
        // process. A single-slot cache would hand whichever preset embedded first to the other,
        // applying its MaxSequenceLength and LowerCase to text it was never meant to see: no
        // exception, plausible and wrong vectors.
        var directory = Path.Combine(Path.GetTempPath(), "qedge-tok-" + Guid.NewGuid().ToString("N"));
        var vocabulary = TestVocabulary.WriteTo(directory);

        try
        {
            using var provider = new EdgeTokenizerProvider(new StubModelStore(vocabulary));

            var mini = await provider.GetAsync(
                EmbeddingPresets.MiniLmL6V2Int8,
                TestContext.Current.CancellationToken);
            var bge = await provider.GetAsync(
                EmbeddingPresets.BgeSmallEnV15,
                TestContext.Current.CancellationToken);

            Assert.NotSame(mini, bge);
            Assert.Equal(EmbeddingPresets.MiniLmL6V2Int8.MaxSequenceLength, mini.MaxSequenceLength);
            Assert.Equal(EmbeddingPresets.BgeSmallEnV15.MaxSequenceLength, bge.MaxSequenceLength);

            // And each preset keeps getting its own instance back, by id.
            Assert.Same(
                mini,
                await provider.GetAsync(EmbeddingPresets.MiniLmL6V2Int8, TestContext.Current.CancellationToken));
            Assert.Same(bge, provider.Find(EmbeddingPresets.BgeSmallEnV15.Id));
            Assert.Null(provider.Find(EmbeddingPresets.NomicEmbedTextV15Int8.Id));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void IndexByTokenCountAgreesWithCountTokensOnThePrefixItReturns()
    {
        using var tokenizer = TestVocabulary.Tokenizer();
        const string Text = "water damage note search";

        // CountTokens counts the text itself; the specials are added by Encode, not counted here.
        Assert.Equal(4, tokenizer.CountTokens(Text.AsSpan()));

        var index = tokenizer.IndexByTokenCount(Text, maxTokens: 2, out var tokenCount);

        Assert.Equal(2, tokenCount);
        Assert.True(index > 0);
        Assert.True(index < Text.Length);
        Assert.Equal(2, tokenizer.CountTokens(Text.AsSpan(0, index)));
    }
}
