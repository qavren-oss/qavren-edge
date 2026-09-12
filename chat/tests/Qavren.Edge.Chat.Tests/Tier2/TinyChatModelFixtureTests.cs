using Qavren.Edge.Chat.Tests.Fixtures;
using Xunit;

namespace Qavren.Edge.Chat.Tests.Tier2;

/// <summary>
/// Plan adjustment 25: the byte counts, "loud". CI never runs <c>make_tiny_chat_model.py</c>, so
/// this is the <b>only</b> thing in the repo that notices an onnx, torch, transformers or builder
/// upgrade moving the serialised bytes - Task 1.3's regeneration diff fires only on a box where
/// somebody re-ran the generator by hand.
/// </summary>
/// <remarks>
/// Deliberately NOT in the tier-2 collection and deliberately touching no native: it materialises
/// into its own temp directory, so it stays green - and stays informative - on a lane where the
/// natives themselves are broken. It runs on every PR, every host and every device lane.
/// </remarks>
public sealed class TinyChatModelFixtureTests : IDisposable
{
    /// <summary>Spec 16.2's hard cap on the generated source.</summary>
    private const long SourceCapBytes = 1024L * 1024;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "qedge-chat-fixture-bytes", Guid.NewGuid().ToString("N"));

    /// <summary>The six files, each with the length the generator emitted beside its base64.</summary>
    public static TheoryData<string, int> Files => new()
    {
        { TinyChatModel.GenAiConfigJsonFileName, TinyChatModel.GenAiConfigJsonLength },
        { TinyChatModel.ModelOnnxFileName, TinyChatModel.ModelOnnxLength },
        { TinyChatModel.ModelOnnxDataFileName, TinyChatModel.ModelOnnxDataLength },
        { TinyChatModel.TokenizerJsonFileName, TinyChatModel.TokenizerJsonLength },
        { TinyChatModel.TokenizerConfigJsonFileName, TinyChatModel.TokenizerConfigJsonLength },
        { TinyChatModel.ChatTemplateJinjaFileName, TinyChatModel.ChatTemplateJinjaLength },
    };

    [Theory]
    [MemberData(nameof(Files))]
    public void EachMaterialisedFilesOnDiskLengthEqualsTheConstTheGeneratorEmitted(string fileName, int expectedLength)
    {
        TinyChatModel.Materialise(_directory);

        var path = Path.Combine(_directory, fileName);
        Assert.True(File.Exists(path), $"{fileName} was not materialised.");
        Assert.Equal(expectedLength, new FileInfo(path).Length);
    }

    [Fact]
    public void EveryBase64PayloadDecodesWithoutThrowingToItsDeclaredLength()
    {
        Assert.Equal(TinyChatModel.GenAiConfigJsonLength, TinyChatModel.GenAiConfigJsonBytes().Length);
        Assert.Equal(TinyChatModel.ModelOnnxLength, TinyChatModel.ModelOnnxBytes().Length);
        Assert.Equal(TinyChatModel.ModelOnnxDataLength, TinyChatModel.ModelOnnxDataBytes().Length);
        Assert.Equal(TinyChatModel.TokenizerJsonLength, TinyChatModel.TokenizerJsonBytes().Length);
        Assert.Equal(TinyChatModel.TokenizerConfigJsonLength, TinyChatModel.TokenizerConfigJsonBytes().Length);
        Assert.Equal(TinyChatModel.ChatTemplateJinjaLength, TinyChatModel.ChatTemplateJinjaBytes().Length);
    }

    [Fact]
    public void TheSixLengthsSumBelowTheOneMegabyteSourceCap()
    {
        long payload = TinyChatModel.GenAiConfigJsonLength
            + TinyChatModel.ModelOnnxLength
            + TinyChatModel.ModelOnnxDataLength
            + TinyChatModel.TokenizerJsonLength
            + TinyChatModel.TokenizerConfigJsonLength
            + TinyChatModel.ChatTemplateJinjaLength;

        Assert.InRange(payload, 1, SourceCapBytes);

        // The source carries base64, which is 4/3 of the payload; the cap is on the SOURCE.
        long source = TinyChatModel.GenAiConfigJsonBase64.Length
            + TinyChatModel.ModelOnnxBase64.Length
            + TinyChatModel.ModelOnnxDataBase64.Length
            + TinyChatModel.TokenizerJsonBase64.Length
            + TinyChatModel.TokenizerConfigJsonBase64.Length
            + TinyChatModel.ChatTemplateJinjaBase64.Length;

        Assert.InRange(source, payload, SourceCapBytes);
    }

    [Fact]
    public void TheFixtureIsSixFilesAndBothAdditionsAreLoadBearing()
    {
        // Plan adjustment 25's amendment: model.onnx.data carries every weight (the builder saves
        // every initializer externally) and chat_template.jinja carries the template (transformers
        // 5.x splits it out of tokenizer_config.json). A directory without either does not load.
        TinyChatModel.Materialise(_directory);

        Assert.Equal(6, Directory.GetFiles(_directory).Length);
        Assert.True(TinyChatModel.ModelOnnxDataLength > TinyChatModel.ModelOnnxLength,
            "model.onnx.data should hold the weights and dwarf the graph.");
        Assert.True(TinyChatModel.ChatTemplateJinjaLength > 0);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory a virus scanner still has open is not a test failure.
        }
    }
}
