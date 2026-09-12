using Qavren.Edge.Chat.Tests.Fixtures;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Parses <b>both real config shapes</b> - the bodies <c>fetch_chat_model_hashes.py</c> downloaded
/// at the pinned revisions, guarded by <c>GenAiConfigFixtureTests</c> - then every malformed variant
/// the failure catalogue names, then the cross-check that turns "somebody repointed the preset at a
/// different export" into a message rather than a wrong memory budget.
/// </summary>
public class ChatModelShapeTests
{
    private static readonly ChatPreset Llama = ChatPresets.Llama32_1BInstructInt4;
    private static readonly ChatPreset Qwen = ChatPresets.Qwen3_600MInt4;

    /// <summary>A hand-written minimal config. Not a fixture: these are inputs for the failure arms.</summary>
    private const string Minimal = """
        {
          "model": {
            "type": "llama",
            "context_length": 4096,
            "vocab_size": 128256,
            "eos_token_id": 128001,
            "decoder": {
              "filename": "model.onnx",
              "head_size": 64,
              "num_hidden_layers": 16,
              "num_key_value_heads": 8
            }
          }
        }
        """;

    // ---- both real shapes ----------------------------------------------------------------------

    [Fact]
    public void TheLlamaConfigParsesToTheGeometryThePresetDeclares()
    {
        var shape = ChatModelShape.FromGenAiConfig(GenAiConfigFixtures.LlamaConfigJson, Llama.Shape.WeightsBytes);

        Assert.Equal("llama", shape.ModelType);
        Assert.Equal(4096, shape.ContextLength);
        Assert.Equal(128_256, shape.VocabSize);
        Assert.Equal(16, shape.NumHiddenLayers);
        Assert.Equal(8, shape.NumKeyValueHeads);
        Assert.Equal(64, shape.HeadSize);
        Assert.Null(shape.SlidingWindow);
        Assert.Equal("model.onnx", shape.DecoderFileName);
        Assert.Equal(2, shape.KvCacheBytesPerElement);
        Assert.Equal(32_768L, shape.KvCacheBytesPerToken);
    }

    [Fact]
    public void TheQwenConfigParsesAndItsDeclaredContextIsFortyThousandNineHundredAndSixty()
    {
        var shape = ChatModelShape.FromGenAiConfig(GenAiConfigFixtures.QwenConfigJson, Qwen.Shape.WeightsBytes);

        Assert.Equal("qwen3", shape.ModelType);
        Assert.Equal(40_960, shape.ContextLength);
        Assert.Equal(151_936, shape.VocabSize);
        Assert.Equal(28, shape.NumHiddenLayers);
        Assert.Equal(8, shape.NumKeyValueHeads);
        Assert.Equal(128, shape.HeadSize);
        Assert.Null(shape.SlidingWindow);
        Assert.Equal("model.onnx", shape.DecoderFileName);
        Assert.Equal(114_688L, shape.KvCacheBytesPerToken);
    }

    [Fact]
    public void QwensDeclaredContextWouldAllocateFourThousandFourHundredAndEightyMebibytesOfKvUncapped()
    {
        // The jetsam scenario, priced. A generator built with no max_length allocates this much,
        // which is why the clamp in the load path is not a nicety.
        var shape = ChatModelShape.FromGenAiConfig(GenAiConfigFixtures.QwenConfigJson, Qwen.Shape.WeightsBytes);

        Assert.Equal(4_697_620_480L, shape.KvCacheBytes(shape.ContextLength));
    }

    [Fact]
    public void BothParsedShapesAgreeWithTheGeneratedCatalogue()
    {
        var llama = ChatModelShape.FromGenAiConfig(GenAiConfigFixtures.LlamaConfigJson, Llama.Shape.WeightsBytes);
        var qwen = ChatModelShape.FromGenAiConfig(GenAiConfigFixtures.QwenConfigJson, Qwen.Shape.WeightsBytes);

        Assert.Null(ChatModelShape.DescribeMismatch(Llama.Id, Llama.Shape, llama));
        Assert.Null(ChatModelShape.DescribeMismatch(Qwen.Id, Qwen.Shape, qwen));
    }

    // ---- eos_token_id, which is an int in some exports and an array in others --------------------

    [Fact]
    public void AScalarEosTokenIdParses()
        => Assert.Equal("llama", ChatModelShape.FromGenAiConfig(Minimal, 1).ModelType);

    [Fact]
    public void AnArrayEosTokenIdParsesToo()
    {
        var json = Minimal.Replace("\"eos_token_id\": 128001", "\"eos_token_id\": [128001, 128008, 128009]", StringComparison.Ordinal);

        Assert.Equal("llama", ChatModelShape.FromGenAiConfig(json, 1).ModelType);
    }

    [Fact]
    public void AnAbsentEosTokenIdParsesBecauseNothingOnTheGeometryPathReadsIt()
    {
        var json = Minimal.Replace("\"eos_token_id\": 128001,", string.Empty, StringComparison.Ordinal);

        Assert.Equal("llama", ChatModelShape.FromGenAiConfig(json, 1).ModelType);
    }

    [Fact]
    public void AnEosTokenIdThatIsNeitherNamesItselfInTheFailure()
    {
        var json = Minimal.Replace("\"eos_token_id\": 128001", "\"eos_token_id\": \"eot\"", StringComparison.Ordinal);

        var ex = Assert.Throws<EdgeChatException>(() => ChatModelShape.FromGenAiConfig(json, 1));

        Assert.Equal(EdgeErrorCode.ChatConfigurationInvalid, ex.Code);
        Assert.Contains("model.eos_token_id", ex.Message, StringComparison.Ordinal);
    }

    // ---- every malformed variant names the offending field ---------------------------------------

    [Theory]
    [InlineData("\"filename\": \"model.onnx\",", "", "model.decoder.filename")]
    [InlineData("\"num_hidden_layers\": 16", "\"num_hidden_layers\": 0", "model.decoder.num_hidden_layers")]
    [InlineData("\"num_key_value_heads\": 8", "\"num_key_value_heads\": 0", "model.decoder.num_key_value_heads")]
    [InlineData("\"head_size\": 64,", "\"head_size\": 0,", "model.decoder.head_size")]
    [InlineData("\"context_length\": 4096,", "\"context_length\": \"4096\",", "model.context_length")]
    [InlineData("\"vocab_size\": 128256,", "", "model.vocab_size")]
    [InlineData("\"type\": \"llama\",", "", "model.type")]
    public void AMalformedConfigIsSevenThousandAndSevenNamingTheField(string from, string to, string expectedField)
    {
        var json = Minimal.Replace(from, to, StringComparison.Ordinal);

        var ex = Assert.Throws<EdgeChatException>(() => ChatModelShape.FromGenAiConfig(json, 1));

        Assert.Equal(EdgeErrorCode.ChatConfigurationInvalid, ex.Code);
        Assert.Contains("genai_config.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expectedField, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingDecoderBlockNamesItself()
    {
        const string Json = """{"model":{"type":"llama","context_length":4096,"vocab_size":1}}""";

        var ex = Assert.Throws<EdgeChatException>(() => ChatModelShape.FromGenAiConfig(Json, 1));

        Assert.Equal(EdgeErrorCode.ChatConfigurationInvalid, ex.Code);
        Assert.Contains("model.decoder", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingModelBlockNamesItself()
    {
        const string Json = """{"search":{"max_length":4096}}""";

        var ex = Assert.Throws<EdgeChatException>(() => ChatModelShape.FromGenAiConfig(Json, 1));

        Assert.Contains("'model' is missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryFromGenAiConfigReportsTheSameFieldWithoutThrowing()
    {
        var json = Minimal.Replace("\"head_size\": 64,", "\"head_size\": 0,", StringComparison.Ordinal);

        Assert.False(ChatModelShape.TryFromGenAiConfig(json, 1, out var shape, out var error));
        Assert.Null(shape);
        Assert.Contains("model.decoder.head_size", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ASlidingWindowIsReadWhenTheModelDeclaresOne()
    {
        var json = Minimal.Replace("\"head_size\": 64,", "\"head_size\": 64,\n      \"sliding_window\": 1024,", StringComparison.Ordinal);
        var shape = ChatModelShape.FromGenAiConfig(json, 1);

        Assert.Equal(1024, shape.SlidingWindow);
        Assert.Equal(shape.KvCacheBytesPerToken * 1024, shape.KvCacheBytes(4096));
    }

    // ---- the cross-check -------------------------------------------------------------------------

    [Theory]
    [InlineData("model.decoder.num_hidden_layers")]
    [InlineData("model.decoder.num_key_value_heads")]
    [InlineData("model.decoder.head_size")]
    [InlineData("model.context_length")]
    [InlineData("model.vocab_size")]
    [InlineData("model.type")]
    [InlineData("model.decoder.filename")]
    public void ADeliberatelyWrongPresetShapeIsSevenThousandAndEightNamingTheFieldAndBothValues(string field)
    {
        var fromConfig = ChatModelShape.FromGenAiConfig(GenAiConfigFixtures.LlamaConfigJson, Llama.Shape.WeightsBytes);
        var wrong = field switch
        {
            "model.decoder.num_hidden_layers" => Llama.Shape with { NumHiddenLayers = 28 },
            "model.decoder.num_key_value_heads" => Llama.Shape with { NumKeyValueHeads = 4 },
            "model.decoder.head_size" => Llama.Shape with { HeadSize = 128 },
            "model.context_length" => Llama.Shape with { ContextLength = 40_960 },
            "model.vocab_size" => Llama.Shape with { VocabSize = 151_936 },
            "model.type" => Llama.Shape with { ModelType = "qwen3" },
            _ => Llama.Shape with { DecoderFileName = "decoder_model_merged.onnx" },
        };

        var ex = Assert.Throws<EdgeChatException>(() => ChatModelShape.ThrowIfMismatched(Llama.Id, wrong, fromConfig));

        Assert.Equal(EdgeErrorCode.ChatModelShapeMismatch, ex.Code);
        Assert.Contains(field, ex.Message, StringComparison.Ordinal);
        Assert.Contains(Llama.Id, ex.Message, StringComparison.Ordinal);
        Assert.Equal(Llama.Id, ex.PresetId);
    }

    [Fact]
    public void TheCrossCheckDeliberatelyIgnoresKvCacheBytesPerElementBecauseTheConfigCannotStateIt()
    {
        var fromConfig = ChatModelShape.FromGenAiConfig(GenAiConfigFixtures.LlamaConfigJson, Llama.Shape.WeightsBytes);
        var doubled = Llama.Shape with { KvCacheBytesPerElement = 4 };

        // Undetectable at load, by construction: genai_config.json states layers, KV heads and head
        // size and says nothing about the KV dtype. It is the one field the cross-check cannot
        // validate, and the omission is documented rather than accidental.
        Assert.Null(ChatModelShape.DescribeMismatch(Llama.Id, doubled, fromConfig));
        Assert.Equal(65_536L, doubled.KvCacheBytesPerToken);
    }

    [Fact]
    public void TheCrossCheckIgnoresTheMeasurementFieldsToo()
    {
        var fromConfig = ChatModelShape.FromGenAiConfig(GenAiConfigFixtures.LlamaConfigJson, Llama.Shape.WeightsBytes);
        var remeasured = Llama.Shape with { MeasuredPeakBytes = 999L, MeasuredOn = "a different phone" };

        Assert.Null(ChatModelShape.DescribeMismatch(Llama.Id, remeasured, fromConfig));
    }
}
