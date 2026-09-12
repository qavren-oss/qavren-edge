using Qavren.Edge.Embeddings.Onnx;
using Xunit;

namespace Qavren.Edge.Ingestion.Onnx.Tests;

/// <summary>
/// Spec 11.2's two guards and the derived budget, on <c>ApplyPreset</c> directly: the 6005 guard
/// fires for each of the five compared fields, a default profile is replaced, and the resolved
/// triple on a MiniLM-int8 generator is 222 / 32 / 27.
/// </summary>
public sealed class ProjectionGuardTests
{
    [Theory]
    [InlineData("Id")]
    [InlineData("Dimensions")]
    [InlineData("MaxSequenceLength")]
    [InlineData("DocumentPrefix")]
    [InlineData("QueryPrefix")]
    public void An_explicit_profile_that_disagrees_with_the_preset_is_6005_naming_both(string field)
    {
        var typed = field switch
        {
            "Id" => ChunkModelProfile.MiniLmL6V2Int8 with { Id = "all-minilm-l6-v2-int8-typo" },
            "Dimensions" => ChunkModelProfile.MiniLmL6V2Int8 with { Dimensions = 512 },
            "MaxSequenceLength" => ChunkModelProfile.MiniLmL6V2Int8 with { MaxSequenceLength = 128 },
            "DocumentPrefix" => ChunkModelProfile.MiniLmL6V2Int8 with { DocumentPrefix = "passage: " },
            "QueryPrefix" => ChunkModelProfile.MiniLmL6V2Int8 with { QueryPrefix = "query: " },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        var options = new IngestionOptions { Model = typed, CollectionName = "chunks" };

        var error = Assert.Throws<EdgeConfigurationException>(
            () => OnnxIngestionBuilderExtensions.ApplyPreset(options, EmbeddingPresets.MiniLmL6V2Int8));

        Assert.Equal(EdgeErrorCode.IngestionOptionsInvalid, error.Code);
        Assert.Contains("IngestionOptions.Model." + field, error.Message, StringComparison.Ordinal);
        Assert.Contains("all-minilm-l6-v2-int8", error.Message, StringComparison.Ordinal);
        Assert.Contains("chunks", error.Message, StringComparison.Ordinal);

        // Never silently overwritten.
        Assert.Same(typed, options.Model);
    }

    [Fact]
    public void A_pooling_difference_alone_is_not_guarded_because_the_spec_lists_five_fields()
    {
        var typed = ChunkModelProfile.MiniLmL6V2Int8 with { Pooling = "Cls" };
        var options = new IngestionOptions { Model = typed };

        OnnxIngestionBuilderExtensions.ApplyPreset(options, EmbeddingPresets.MiniLmL6V2Int8);

        Assert.Same(typed, options.Model);
    }

    [Fact]
    public void An_explicit_profile_that_matches_the_preset_by_value_is_kept_as_typed()
    {
        var typed = new ChunkModelProfile("bge-small-en-v1.5", 384, 512, "Cls",
            QueryPrefix: "Represent this sentence for searching relevant passages: ");
        var options = new IngestionOptions { Model = typed };

        OnnxIngestionBuilderExtensions.ApplyPreset(options, EmbeddingPresets.BgeSmallEnV15);

        Assert.Same(typed, options.Model);
    }

    [Fact]
    public void The_default_profile_is_replaced_by_the_projection_when_the_width_agrees()
    {
        var options = new IngestionOptions();
        Assert.Same(ChunkModelProfile.MiniLmL6V2Int8, options.Model);

        OnnxIngestionBuilderExtensions.ApplyPreset(options, EmbeddingPresets.BgeSmallEnV15);

        Assert.Equal(ChunkModelProfile.BgeSmallEnV15, options.Model);

        // Idempotent: a second application compares the projection to itself.
        OnnxIngestionBuilderExtensions.ApplyPreset(options, EmbeddingPresets.BgeSmallEnV15);
        Assert.Equal(ChunkModelProfile.BgeSmallEnV15, options.Model);
    }

    [Fact]
    public void The_default_profile_with_a_collection_declared_at_the_wrong_width_is_6005()
    {
        // AddIngestion already registered a 384-wide collection from the default profile; nomic
        // is 768. Swapping the profile in would hide the mismatch from 6006.
        var options = new IngestionOptions { CollectionName = "notes" };

        var error = Assert.Throws<EdgeConfigurationException>(
            () => OnnxIngestionBuilderExtensions.ApplyPreset(options, EmbeddingPresets.NomicEmbedTextV15Int8));

        Assert.Equal(EdgeErrorCode.IngestionOptionsInvalid, error.Code);
        Assert.Contains("384", error.Message, StringComparison.Ordinal);
        Assert.Contains("768", error.Message, StringComparison.Ordinal);
        Assert.Contains("notes", error.Message, StringComparison.Ordinal);
        Assert.Same(ChunkModelProfile.MiniLmL6V2Int8, options.Model);
    }

    [Fact]
    public void The_default_profile_with_Dimensions_set_to_the_preset_width_is_replaced()
    {
        var options = new IngestionOptions { Dimensions = 768 };

        OnnxIngestionBuilderExtensions.ApplyPreset(options, EmbeddingPresets.NomicEmbedTextV15Int8);

        Assert.Equal(ChunkModelProfile.NomicEmbedTextV15Int8, options.Model);
    }

    [Fact]
    public void The_derived_triple_on_a_MiniLmL6V2Int8_generator_is_222_32_27()
    {
        var options = new IngestionOptions();
        OnnxIngestionBuilderExtensions.ApplyPreset(options, EmbeddingPresets.MiniLmL6V2Int8);

        var tokenizer = new EdgeChunkTokenizer(new FakeEdgeTokenizer(maxSequenceLength: 256));
        var resolved = options.Chunking.Resolve(options.Model, tokenizer);

        Assert.Equal(222, resolved.MaxTokens);
        Assert.Equal(32, resolved.OverlapTokens);
        Assert.Equal(27, resolved.MinTokens);
        Assert.Equal(2, resolved.SpecialTokenOverhead);
        Assert.Equal(0, resolved.DocumentPrefixTokens);
    }

    [Fact]
    public void The_derived_triple_on_a_bge_small_generator_is_478_64_59()
    {
        var options = new IngestionOptions();
        OnnxIngestionBuilderExtensions.ApplyPreset(options, EmbeddingPresets.BgeSmallEnV15);

        var tokenizer = new EdgeChunkTokenizer(new FakeEdgeTokenizer(maxSequenceLength: 512));
        var resolved = options.Chunking.Resolve(options.Model, tokenizer);

        Assert.Equal(478, resolved.MaxTokens);
        Assert.Equal(64, resolved.OverlapTokens);
        Assert.Equal(59, resolved.MinTokens);
    }

    [Fact]
    public void A_consumer_set_budget_is_not_derived()
    {
        var options = new IngestionOptions();
        options.Chunking.MaxTokens = 200;
        options.Chunking.OverlapTokens = 16;
        options.Chunking.MinTokens = 20;
        OnnxIngestionBuilderExtensions.ApplyPreset(options, EmbeddingPresets.MiniLmL6V2Int8);

        var resolved = options.Chunking.Resolve(options.Model, new EdgeChunkTokenizer(new FakeEdgeTokenizer()));

        Assert.Equal(200, resolved.MaxTokens);
        Assert.Equal(16, resolved.OverlapTokens);
        Assert.Equal(20, resolved.MinTokens);
    }

    [Fact(Skip = Vocabulary.SkipReason, SkipUnless = nameof(Vocabulary.Available), SkipType = typeof(Vocabulary))]
    public void The_derived_triple_over_the_real_vocabulary_is_222_32_27()
    {
        var options = new IngestionOptions();
        OnnxIngestionBuilderExtensions.ApplyPreset(options, EmbeddingPresets.MiniLmL6V2Int8);

        var resolved = options.Chunking.Resolve(options.Model, new EdgeChunkTokenizer(Vocabulary.Edge));

        Assert.Equal(222, resolved.MaxTokens);
        Assert.Equal(32, resolved.OverlapTokens);
        Assert.Equal(27, resolved.MinTokens);
    }
}
