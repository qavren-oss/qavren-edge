using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Onnx;
using Xunit;

namespace Qavren.Edge.Embeddings.Tests;

/// <summary>
/// A table test over the four shipped presets. Everything here is the kind of value a copy-paste
/// while adding a fifth preset would get wrong without any error: dimensions, pooling,
/// <c>PostPoolLayerNorm</c>, prefixes, licence and the four binding names.
/// </summary>
public class PresetCatalogueTests
{
    public static TheoryData<string, int, int, EmbeddingPooling, bool, string> Catalogue => new()
    {
        { "all-minilm-l6-v2-int8", 384, 256, EmbeddingPooling.Mean, false, "Apache-2.0" },
        { "all-minilm-l6-v2-fp32", 384, 256, EmbeddingPooling.Mean, false, "Apache-2.0" },
        { "bge-small-en-v1.5", 384, 512, EmbeddingPooling.Cls, false, "MIT" },
        { "nomic-embed-text-v1.5-int8", 768, 512, EmbeddingPooling.Mean, true, "Apache-2.0" },
    };

    [Theory]
    [MemberData(nameof(Catalogue))]
    public void EachPresetDeclaresTheNumbersItIsSupposedTo(
        string id,
        int dimensions,
        int maxSequenceLength,
        EmbeddingPooling pooling,
        bool postPoolLayerNorm,
        string spdxLicense)
    {
        var preset = EmbeddingPresets.ById(id);

        Assert.Equal(dimensions, preset.Dimensions);
        Assert.Equal(maxSequenceLength, preset.MaxSequenceLength);
        Assert.Equal(pooling, preset.Pooling);
        Assert.Equal(postPoolLayerNorm, preset.PostPoolLayerNorm);
        Assert.Equal(spdxLicense, preset.Manifest.SpdxLicense);
        Assert.True(preset.Normalize);
        Assert.True(preset.LowerCase);
        Assert.Equal(EdgeTokenizerKind.WordPieceVocabTxt, preset.TokenizerKind);
    }

    [Fact]
    public void NomicIsTheOnlyPresetThatLayerNorms()
    {
        var flagged = EmbeddingPresets.All.Where(p => p.PostPoolLayerNorm).Select(p => p.Id).ToArray();

        Assert.Equal([EmbeddingPresets.NomicEmbedTextV15Int8.Id], flagged);
    }

    [Fact]
    public void NomicsLayerNormEpsilonIsPyTorchsDefault()
    {
        Assert.Equal(1e-5f, EmbeddingPresets.NomicEmbedTextV15Int8.LayerNormEpsilon);
    }

    [Fact]
    public void EveryPresetCarriesTheFourDefaultBindingNamesVerbatim()
    {
        // Inputs are bound BY NAME, never by position, and these four names are what all four
        // shipped graphs declare - which is why no preset overrides them. A rename here is a
        // silently wrong binding, not a load failure.
        Assert.All(EmbeddingPresets.All, preset =>
        {
            Assert.Equal("input_ids", preset.InputIdsName);
            Assert.Equal("attention_mask", preset.AttentionMaskName);
            Assert.Equal("token_type_ids", preset.TokenTypeIdsName);
            Assert.Equal("last_hidden_state", preset.OutputName);
        });
    }

    [Fact]
    public void EveryPresetUsesTheDefaultSequenceBuckets()
    {
        Assert.All(EmbeddingPresets.All, preset => Assert.Equal([64, 128, 256, 512], preset.SequenceBuckets));
    }

    [Fact]
    public void OnlyBgeAndNomicDeclarePrefixesAndNomicDeclaresBoth()
    {
        Assert.Null(EmbeddingPresets.MiniLmL6V2Int8.QueryPrefix);
        Assert.Null(EmbeddingPresets.MiniLmL6V2Int8.DocumentPrefix);
        Assert.Null(EmbeddingPresets.MiniLmL6V2Fp32.QueryPrefix);
        Assert.Null(EmbeddingPresets.MiniLmL6V2Fp32.DocumentPrefix);

        // Asserted VERBATIM, like nomic's two below. A prefix is part of what the model was trained
        // to see: BAAI's model card gives this exact sentence, trailing space included, and a
        // reworded or space-trimmed version is a silent retrieval regression with no exception
        // anywhere. NotNull would have passed for any string at all.
        Assert.Equal(
            "Represent this sentence for searching relevant passages: ",
            EmbeddingPresets.BgeSmallEnV15.QueryPrefix);
        Assert.Null(EmbeddingPresets.BgeSmallEnV15.DocumentPrefix);

        Assert.Equal("search_query: ", EmbeddingPresets.NomicEmbedTextV15Int8.QueryPrefix);
        Assert.Equal("search_document: ", EmbeddingPresets.NomicEmbedTextV15Int8.DocumentPrefix);
    }

    [Fact]
    public void EveryPresetPinsAFullCommitShaAndNeverAMovingRevision()
    {
        // A moving revision silently changes the vectors, and vectors written by two revisions of
        // one model are not comparable.
        Assert.All(EmbeddingPresets.All, preset =>
        {
            Assert.NotNull(preset.Manifest.HuggingFaceRevision);
            Assert.Equal(40, preset.Manifest.HuggingFaceRevision!.Length);
            Assert.NotEqual("main", preset.Manifest.HuggingFaceRevision);
        });
    }

    [Fact]
    public void EveryPresetsModelFileAndTokenizerFileAreInItsManifest()
    {
        Assert.All(EmbeddingPresets.All, preset =>
        {
            Assert.Equal(preset.ModelFile, preset.Manifest.GraphFile);
            Assert.Contains(preset.Manifest.Files, f =>
                f.RelativePath == preset.ModelFile && f.Role == OnnxModelFileRole.Graph);
            Assert.Contains(preset.Manifest.Files, f =>
                f.RelativePath == preset.TokenizerFile && f.Role == OnnxModelFileRole.Vocabulary);
        });
    }

    [Fact]
    public void AllFourPresetsShareOneVocabularyDigest()
    {
        var digests = EmbeddingPresets.All
            .Select(p => p.Manifest.Files.Single(f => f.Role == OnnxModelFileRole.Vocabulary).Sha256)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Single(digests);
    }

    [Fact]
    public void ByIdRejectsAnUnknownPresetWithItsOwnErrorCode()
    {
        var ex = Assert.Throws<EdgeEmbeddingException>(() => EmbeddingPresets.ById("not-a-preset"));

        Assert.Equal(EdgeErrorCode.EmbeddingPresetNotFound, ex.Code);
        Assert.Equal("not-a-preset", ex.PresetId);
    }

    [Fact]
    public void TheDefaultPresetIsTheOneTheDocumentationNames()
    {
        Assert.Same(EmbeddingPresets.MiniLmL6V2Int8, new OnnxEmbeddingOptions().Preset);
    }
}
