using Qavren.Edge.Embeddings.Onnx;
using Xunit;

namespace Qavren.Edge.Ingestion.Onnx.Tests;

/// <summary>
/// Spec 17 item 12, plan adjustment 16: the four core <see cref="ChunkModelProfile"/> constants
/// duplicate six values SP2's <see cref="EmbeddingPresets"/> also declare, and the duplication is
/// what keeps the core ONNX-free. It is tested rather than trusted - <b>twenty-four field
/// comparisons</b>, ordinal, trailing spaces included. If SP2's two OWNER-CONFIRMATION-OWED values
/// move (bge-small's query instruction, nomic's 512 ceiling) this fails loudly, and the core
/// profiles and every affected recipe hash follow.
/// </summary>
public sealed class ProfileDriftTests
{
    private static readonly string[] Fields =
        ["Id", "Dimensions", "MaxSequenceLength", "Pooling", "DocumentPrefix", "QueryPrefix"];

    private static readonly string[] Profiles =
        [nameof(ChunkModelProfile.MiniLmL6V2Int8), nameof(ChunkModelProfile.MiniLmL6V2Fp32),
         nameof(ChunkModelProfile.BgeSmallEnV15), nameof(ChunkModelProfile.NomicEmbedTextV15Int8)];

    /// <summary>Four profiles by six fields: the twenty-four rows.</summary>
    public static TheoryData<string, string> Rows
    {
        get
        {
            var rows = new TheoryData<string, string>();
            foreach (var profile in Profiles)
            {
                foreach (var fieldName in Fields)
                {
                    rows.Add(profile, fieldName);
                }
            }

            return rows;
        }
    }

    [Fact]
    public void The_drift_table_has_exactly_twenty_four_rows()
    {
        Assert.Equal(4, Profiles.Length);
        Assert.Equal(6, Fields.Length);
        Assert.Equal(24, Rows.Count);
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Core_profile_matches_the_SP2_preset_of_the_same_name(string profileName, string field)
    {
        var profile = Profile(profileName);
        var preset = Preset(profileName);

        switch (field)
        {
            case "Id":
                Assert.Equal(preset.Id, profile.Id, StringComparer.Ordinal);
                break;
            case "Dimensions":
                Assert.Equal(preset.Dimensions, profile.Dimensions);
                break;
            case "MaxSequenceLength":
                Assert.Equal(preset.MaxSequenceLength, profile.MaxSequenceLength);
                break;
            case "Pooling":
                Assert.Equal(preset.Pooling.ToString(), profile.Pooling, StringComparer.Ordinal);
                break;
            case "DocumentPrefix":
                Assert.Equal(preset.DocumentPrefix, profile.DocumentPrefix, StringComparer.Ordinal);
                break;
            case "QueryPrefix":
                Assert.Equal(preset.QueryPrefix, profile.QueryPrefix, StringComparer.Ordinal);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown field.");
        }
    }

    [Fact]
    public void The_pinned_strings_are_the_ones_SP2_committed_trailing_spaces_included()
    {
        // Restated literally, so a change on EITHER side is visible in this file's diff.
        Assert.Equal("all-minilm-l6-v2-int8", ChunkModelProfile.MiniLmL6V2Int8.Id, StringComparer.Ordinal);
        Assert.Equal("all-minilm-l6-v2-fp32", ChunkModelProfile.MiniLmL6V2Fp32.Id, StringComparer.Ordinal);
        Assert.Equal("bge-small-en-v1.5", ChunkModelProfile.BgeSmallEnV15.Id, StringComparer.Ordinal);
        Assert.Equal("nomic-embed-text-v1.5-int8", ChunkModelProfile.NomicEmbedTextV15Int8.Id, StringComparer.Ordinal);

        Assert.Null(ChunkModelProfile.MiniLmL6V2Int8.DocumentPrefix);
        Assert.Null(ChunkModelProfile.MiniLmL6V2Int8.QueryPrefix);
        Assert.Null(ChunkModelProfile.MiniLmL6V2Fp32.DocumentPrefix);
        Assert.Null(ChunkModelProfile.MiniLmL6V2Fp32.QueryPrefix);
        Assert.Null(ChunkModelProfile.BgeSmallEnV15.DocumentPrefix);
        Assert.Equal(
            "Represent this sentence for searching relevant passages: ",
            ChunkModelProfile.BgeSmallEnV15.QueryPrefix,
            StringComparer.Ordinal);
        Assert.Equal("search_document: ", ChunkModelProfile.NomicEmbedTextV15Int8.DocumentPrefix, StringComparer.Ordinal);
        Assert.Equal("search_query: ", ChunkModelProfile.NomicEmbedTextV15Int8.QueryPrefix, StringComparer.Ordinal);
        Assert.Equal(512, ChunkModelProfile.NomicEmbedTextV15Int8.MaxSequenceLength);
    }

    [Theory]
    [InlineData(nameof(ChunkModelProfile.MiniLmL6V2Int8))]
    [InlineData(nameof(ChunkModelProfile.MiniLmL6V2Fp32))]
    [InlineData(nameof(ChunkModelProfile.BgeSmallEnV15))]
    [InlineData(nameof(ChunkModelProfile.NomicEmbedTextV15Int8))]
    public void AddOnnxIngestion_projection_round_trips_every_one_of_the_six(string name)
    {
        var preset = Preset(name);
        var projected = OnnxIngestionBuilderExtensions.Project(preset);

        Assert.Equal(preset.Id, projected.Id, StringComparer.Ordinal);
        Assert.Equal(preset.Dimensions, projected.Dimensions);
        Assert.Equal(preset.MaxSequenceLength, projected.MaxSequenceLength);
        Assert.Equal(preset.Pooling.ToString(), projected.Pooling, StringComparer.Ordinal);
        Assert.Equal(preset.DocumentPrefix, projected.DocumentPrefix, StringComparer.Ordinal);
        Assert.Equal(preset.QueryPrefix, projected.QueryPrefix, StringComparer.Ordinal);

        // And it is value-equal to the core constant, which is what lets ApplyPreset's explicit
        // guard pass for a consumer who typed the matching constant by hand.
        Assert.Equal(Profile(name), projected);
    }

    internal static ChunkModelProfile Profile(string name) => name switch
    {
        nameof(ChunkModelProfile.MiniLmL6V2Int8) => ChunkModelProfile.MiniLmL6V2Int8,
        nameof(ChunkModelProfile.MiniLmL6V2Fp32) => ChunkModelProfile.MiniLmL6V2Fp32,
        nameof(ChunkModelProfile.BgeSmallEnV15) => ChunkModelProfile.BgeSmallEnV15,
        nameof(ChunkModelProfile.NomicEmbedTextV15Int8) => ChunkModelProfile.NomicEmbedTextV15Int8,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown profile."),
    };

    internal static EmbeddingPreset Preset(string name) => name switch
    {
        nameof(EmbeddingPresets.MiniLmL6V2Int8) => EmbeddingPresets.MiniLmL6V2Int8,
        nameof(EmbeddingPresets.MiniLmL6V2Fp32) => EmbeddingPresets.MiniLmL6V2Fp32,
        nameof(EmbeddingPresets.BgeSmallEnV15) => EmbeddingPresets.BgeSmallEnV15,
        nameof(EmbeddingPresets.NomicEmbedTextV15Int8) => EmbeddingPresets.NomicEmbedTextV15Int8,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown preset."),
    };
}
