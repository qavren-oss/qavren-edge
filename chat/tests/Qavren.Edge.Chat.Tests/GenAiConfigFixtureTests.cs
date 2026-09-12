using System.Security.Cryptography;
using System.Text;
using Qavren.Edge.Chat.Tests.Fixtures;
using Qavren.Edge.Onnx;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// The guard that makes every shape assertion mean something (plan adjustment 24).
/// <c>GenAiConfigFixtures.g.cs</c> is written by <c>fetch_chat_model_hashes.py</c> and never hand
/// typed: a body that was "tidied", re-indented, or updated from a newer revision without re-running
/// the generator would make every assertion in <c>ChatModelShapeTests</c> assert the wrong thing,
/// silently and forever. It fails here instead.
/// </summary>
/// <remarks>
/// The digests are the same two <c>ChatPresets.g.cs</c> pins for provisioning, which is the point:
/// the fixture the shape parser is tested against and the file the provisioner will verify on a
/// user's device cannot drift apart.
/// </remarks>
public class GenAiConfigFixtureTests
{
    private const string GenAiConfigFileName = "genai_config.json";

    private static string Sha256OfNormalisedBody(string body)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body.ReplaceLineEndings("\n"))));

    private static OnnxModelFile ConfigFile(ChatPreset preset)
        => preset.Manifest.Files.Single(f =>
            string.Equals(f.RelativePath, GenAiConfigFileName, StringComparison.Ordinal));

    [Fact]
    public void TheLlamaFixtureHashesToTheDigestTheGeneratorRecorded()
        => Assert.Equal(GenAiConfigFixtures.LlamaConfigSha256, Sha256OfNormalisedBody(GenAiConfigFixtures.LlamaConfigJson));

    [Fact]
    public void TheQwenFixtureHashesToTheDigestTheGeneratorRecorded()
        => Assert.Equal(GenAiConfigFixtures.QwenConfigSha256, Sha256OfNormalisedBody(GenAiConfigFixtures.QwenConfigJson));

    [Fact]
    public void TheLlamaFixtureDigestIsTheOneChatPresetsPinsForProvisioning()
    {
        Assert.Equal("359d47db9f4e3626bd51a4b5ccf839489dfbe099e46e039dc63c0ca0118f4f1c", GenAiConfigFixtures.LlamaConfigSha256);
        Assert.Equal(GenAiConfigFixtures.LlamaConfigSha256, ConfigFile(ChatPresets.Llama32_1BInstructInt4).Sha256);
    }

    [Fact]
    public void TheQwenFixtureDigestIsTheOneChatPresetsPinsForProvisioning()
    {
        Assert.Equal("67348085a5b994a6b6d94e56e395e675d8ef4732c3c46ff75de30a541489fd3f", GenAiConfigFixtures.QwenConfigSha256);
        Assert.Equal(GenAiConfigFixtures.QwenConfigSha256, ConfigFile(ChatPresets.Qwen3_600MInt4).Sha256);
    }

    [Fact]
    public void TheNormalisedBodiesAreFifteenHundredAndThirtySixAndFifteenHundredAndTwentyBytes()
    {
        Assert.Equal(1_536, Encoding.UTF8.GetByteCount(GenAiConfigFixtures.LlamaConfigJson.ReplaceLineEndings("\n")));
        Assert.Equal(1_520, Encoding.UTF8.GetByteCount(GenAiConfigFixtures.QwenConfigJson.ReplaceLineEndings("\n")));
    }

    [Fact]
    public void TheGeneratedLengthConstantsAgreeWithTheManifestsSizes()
    {
        Assert.Equal(1_536, GenAiConfigFixtures.LlamaConfigLength);
        Assert.Equal(1_520, GenAiConfigFixtures.QwenConfigLength);

        Assert.Equal(GenAiConfigFixtures.LlamaConfigLength, ConfigFile(ChatPresets.Llama32_1BInstructInt4).SizeBytes);
        Assert.Equal(GenAiConfigFixtures.QwenConfigLength, ConfigFile(ChatPresets.Qwen3_600MInt4).SizeBytes);
    }

    [Fact]
    public void NeitherFixtureCarriesACarriageReturnBecauseARawStringLiteralHasToBeByteFaithful()
    {
        Assert.DoesNotContain("\r", GenAiConfigFixtures.LlamaConfigJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", GenAiConfigFixtures.QwenConfigJson, StringComparison.Ordinal);
    }
}
