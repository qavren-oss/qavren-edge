using Qavren.Edge.Onnx;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// The generated catalogue's table, guarded. <c>ChatPresets.g.cs</c> is written by
/// <c>fetch_chat_model_hashes.py</c> from each repo's own <c>genai_config.json</c> at a pinned
/// commit, so nothing here is hand-typed - and these are the invariants an edit to the generator, or
/// a hand-edit to its output, has to keep.
/// </summary>
public class PresetCatalogueTests
{
    public static TheoryData<string> PresetIds() => ["llama-3.2-1b-instruct-int4", "qwen3-0.6b-int4"];

    private static bool IsLowerHex(string value, int length)
    {
        if (value.Length != length)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    [Fact]
    public void TheCatalogueHoldsExactlyTwoPresets()
    {
        Assert.Equal(2, ChatPresets.All.Count);
        Assert.Contains(ChatPresets.Llama32_1BInstructInt4, ChatPresets.All);
        Assert.Contains(ChatPresets.Qwen3_600MInt4, ChatPresets.All);
    }

    [Theory]
    [MemberData(nameof(PresetIds))]
    public void ByIdRoundTrips(string id)
        => Assert.Equal(id, ChatPresets.ById(id).Id);

    [Fact]
    public void AnUnknownIdIsSevenThousandAndThreeNamingWhatTheCatalogueDoesHold()
    {
        var ex = Assert.Throws<EdgeChatException>(() => ChatPresets.ById("phi-4-mini"));

        Assert.Equal(EdgeErrorCode.ChatModelNotRegistered, ex.Code);
        Assert.Equal("phi-4-mini", ex.PresetId);
        Assert.Contains("llama-3.2-1b-instruct-int4", ex.Message, StringComparison.Ordinal);
        Assert.Contains("qwen3-0.6b-int4", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(PresetIds))]
    public void TheRevisionIsAFullCommitShaAndNeverMain(string id)
    {
        var revision = ChatPresets.ById(id).Manifest.HuggingFaceRevision;

        Assert.NotNull(revision);
        Assert.NotEqual("main", revision);
        Assert.True(IsLowerHex(revision, 40), $"'{revision}' is not a 40-character lowercase commit sha.");
    }

    [Theory]
    [MemberData(nameof(PresetIds))]
    public void EveryFileCarriesALowercaseSixtyFourCharacterDigest(string id)
    {
        var manifest = ChatPresets.ById(id).Manifest;

        Assert.Equal(6, manifest.Files.Count);
        foreach (var file in manifest.Files)
        {
            Assert.True(IsLowerHex(file.Sha256, 64), $"'{file.RelativePath}' carries '{file.Sha256}'.");
            Assert.True(file.SizeBytes > 0);
        }
    }

    [Theory]
    [MemberData(nameof(PresetIds))]
    public void GraphFileNamesTheDecoderOnnxAndNotTheGenAiConfig(string id)
    {
        var manifest = ChatPresets.ById(id).Manifest;

        Assert.Equal("model.onnx", manifest.GraphFile);
        Assert.NotEqual("genai_config.json", manifest.GraphFile);
        Assert.Equal(OnnxModelFileRole.Graph, manifest.Files.Single(f => f.RelativePath == manifest.GraphFile).Role);
    }

    [Theory]
    [MemberData(nameof(PresetIds))]
    public void WeightsBytesIsTheGraphPlusItsExternalDataAndNothingElse(string id)
    {
        var preset = ChatPresets.ById(id);
        var weights = preset.Manifest.Files
            .Where(f => f.Role is OnnxModelFileRole.Graph or OnnxModelFileRole.GraphExternalData)
            .Sum(f => f.SizeBytes);

        Assert.Equal(weights, preset.Shape.WeightsBytes);

        // A 17 MB tokenizer.json is not resident weights, and counting it would over-report by tens
        // of megabytes inside a user-visible refusal message.
        Assert.True(
            preset.Shape.WeightsBytes < preset.Manifest.TotalSizeBytes,
            "WeightsBytes must be strictly below the bundle total.");
    }

    [Theory]
    [MemberData(nameof(PresetIds))]
    public void TheEffectiveContextIsFourThousandAndNinetySixForBothWhichIsTheClampThatMatters(string id)
    {
        var preset = ChatPresets.ById(id);

        // Qwen declares 40960. Without this clamp a generator built from its declared context would
        // allocate 4480 MiB of KV cache on a phone.
        Assert.Equal(4096, Math.Min(preset.DefaultMaxContextTokens, preset.Shape.ContextLength));
    }

    [Fact]
    public void QwensDeclaredContextIsTheOneThatNeedsClampingAndLlamasIsAlreadyFourThousandAndNinetySix()
    {
        Assert.Equal(40_960, ChatPresets.Qwen3_600MInt4.Shape.ContextLength);
        Assert.Equal(4096, ChatPresets.Llama32_1BInstructInt4.Shape.ContextLength);
    }

    [Fact]
    public void EachPresetShipsThePublishersOwnSamplingDefaultsAndNotTheTypeDefaults()
    {
        // Plan adjustment 15. A preset that silently sampled differently from the publisher's own
        // genai_config.json would be a quality regression nobody would attribute.
        Assert.Equal(0.6f, ChatPresets.Llama32_1BInstructInt4.DefaultTemperature);
        Assert.Equal(0.9f, ChatPresets.Llama32_1BInstructInt4.DefaultTopP);
        Assert.Equal(50, ChatPresets.Llama32_1BInstructInt4.DefaultTopK);

        Assert.Equal(0.6f, ChatPresets.Qwen3_600MInt4.DefaultTemperature);
        Assert.Equal(0.95f, ChatPresets.Qwen3_600MInt4.DefaultTopP);
        Assert.Equal(20, ChatPresets.Qwen3_600MInt4.DefaultTopK);
    }

    [Fact]
    public void ChatPresetsOwnDefaultsStayAsDeclaredForAPresetSomebodyWritesByHand()
    {
        var byHand = new ChatPreset
        {
            Id = "by-hand",
            DisplayName = "By hand",
            Manifest = ChatPresets.Qwen3_600MInt4.Manifest,
            Shape = ChatPresets.Qwen3_600MInt4.Shape,
        };

        Assert.Equal("genai_config.json", byHand.GenAiConfigFile);
        Assert.Empty(byHand.StopSequences);
        Assert.Equal(4096, byHand.DefaultMaxContextTokens);
        Assert.Equal(512, byHand.DefaultMaxOutputTokens);
        Assert.Equal(0.7f, byHand.DefaultTemperature);
        Assert.Equal(0.9f, byHand.DefaultTopP);
        Assert.Equal(50, byHand.DefaultTopK);
    }

    [Fact]
    public void TheTwoLicencesAreDifferentWhichIsWhyThereIsNoDefaultPreset()
    {
        Assert.Equal("LicenseRef-LLAMA-3.2-Community", ChatPresets.Llama32_1BInstructInt4.Manifest.SpdxLicense);
        Assert.Equal("Apache-2.0", ChatPresets.Qwen3_600MInt4.Manifest.SpdxLicense);
        Assert.NotNull(ChatPresets.Llama32_1BInstructInt4.LicenseUri);
        Assert.NotNull(ChatPresets.Qwen3_600MInt4.LicenseUri);
    }

    [Fact]
    public void QwensServerMeasurementIsRecordedButDeliberatelyNotInTheBudget()
    {
        // There is no field, rule or option that discounts a measurement by where it was taken, so
        // a Graviton peak entering MeasuredPeakBytes would silently become a phone budget.
        Assert.Null(ChatPresets.Qwen3_600MInt4.Shape.MeasuredPeakBytes);
        Assert.NotNull(ChatPresets.Qwen3_600MInt4.Shape.MeasuredOn);

        Assert.Equal(1_342_177_280L, ChatPresets.Llama32_1BInstructInt4.Shape.MeasuredPeakBytes);
        Assert.Contains("vivo X300", ChatPresets.Llama32_1BInstructInt4.Shape.MeasuredOn!, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPresetsBundleTotalIsTheSumOfItsSixFiles()
    {
        foreach (var preset in ChatPresets.All)
        {
            var sum = preset.Manifest.Files.Sum(f => f.SizeBytes);
            Assert.Equal(sum, preset.Manifest.TotalSizeBytes);
        }

        Assert.Equal(1_241_451_982L, ChatPresets.Llama32_1BInstructInt4.Manifest.TotalSizeBytes);
        Assert.Equal(495_088_583L, ChatPresets.Qwen3_600MInt4.Manifest.TotalSizeBytes);
    }
}
