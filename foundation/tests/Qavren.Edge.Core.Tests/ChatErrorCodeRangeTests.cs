using Xunit;

namespace Qavren.Edge.Core.Tests;

/// <summary>
/// Sub-project 4 owns 7000-7299. These tests pin that range, pin the SP1 and SP2 ranges below it
/// against renumbering, and check that SP4 stays clear of 6000-6299, which sub-project 3 allocated
/// to its ingestion codes. The SP3 block's exact membership is pinned by EdgeErrorCodeSp3RangeTests.
/// </summary>
public class ChatErrorCodeRangeTests
{
    /// <summary>The complete set of codes defined before SP4, by explicit value. Nothing may move.</summary>
    private static readonly int[] CodesDefinedBeforeSp4 =
    [
        // Sub-project 1: 1001-4001.
        1001, 1002, 1003, 1004, 1005,
        2001, 2002,
        3001, 3002,
        4001,
        // Sub-project 2: 5001-5213.
        5001, 5002, 5003, 5004, 5005, 5006, 5007,
        5051, 5052, 5053, 5054, 5055, 5056,
        5101, 5102, 5103, 5104, 5105,
        5201, 5202, 5203, 5204, 5205, 5206, 5207, 5208, 5209, 5210, 5211, 5212, 5213,
    ];

    [Fact]
    public void CodesBelow5300AreExactlyTheSetSp1AndSp2Defined()
    {
        var actual = Enum.GetValues<EdgeErrorCode>()
            .Select(static v => (int)v)
            .Where(static v => v is >= 1001 and <= 5299)
            .OrderBy(static v => v)
            .ToArray();

        Assert.Equal(CodesDefinedBeforeSp4.OrderBy(static v => v).ToArray(), actual);
    }

    [Fact]
    public void Range6000To6299IsSubProject3sAndIsDisjointFromSp4()
    {
        var sp3 = Enum.GetValues<EdgeErrorCode>()
            .Where(static v => (int)v is >= 6000 and <= 6299)
            .ToArray();
        var sp4 = Enum.GetValues<EdgeErrorCode>()
            .Where(static v => (int)v is >= 7000 and <= 7299)
            .ToArray();

        // 6000-6299 is allocated - sub-project 3's ingestion codes live there.
        Assert.NotEmpty(sp3);
        Assert.All(sp3, static code => Assert.InRange((int)code, 6000, 6299));

        // The two blocks share no member and no numeric value.
        Assert.Empty(sp3.Intersect(sp4));
        Assert.Empty(sp3.Select(static v => (int)v).Intersect(sp4.Select(static v => (int)v)));
    }

    [Theory]
    // Qavren.Edge.Chat.Onnx - runtime and model hosting
    [InlineData(EdgeErrorCode.ChatEnvironmentNotStarted, 7001)]
    [InlineData(EdgeErrorCode.ChatModelLoadFailed, 7002)]
    [InlineData(EdgeErrorCode.ChatModelNotRegistered, 7003)]
    [InlineData(EdgeErrorCode.ChatUnsupportedRuntime, 7004)]
    [InlineData(EdgeErrorCode.ChatInsufficientMemory, 7005)]
    [InlineData(EdgeErrorCode.ChatDeviceTooSmall, 7006)]
    [InlineData(EdgeErrorCode.ChatConfigurationInvalid, 7007)]
    [InlineData(EdgeErrorCode.ChatModelShapeMismatch, 7008)]
    [InlineData(EdgeErrorCode.ChatExecutionProviderUnsupported, 7009)]
    // Qavren.Edge.Chat.Onnx - provisioning
    [InlineData(EdgeErrorCode.ChatModelNotProvisioned, 7051)]
    [InlineData(EdgeErrorCode.ChatInsufficientDiskSpace, 7052)]
    [InlineData(EdgeErrorCode.ChatDownloadNotPermitted, 7053)]
    // Qavren.Edge.Chat.Onnx - generation
    [InlineData(EdgeErrorCode.ChatTemplateUnsupported, 7101)]
    [InlineData(EdgeErrorCode.ChatPromptTooLong, 7102)]
    [InlineData(EdgeErrorCode.ChatGuidanceUnavailable, 7103)]
    [InlineData(EdgeErrorCode.ChatGenerationFailed, 7104)]
    [InlineData(EdgeErrorCode.ChatBusy, 7105)]
    [InlineData(EdgeErrorCode.ChatThermalAbort, 7106)]
    [InlineData(EdgeErrorCode.ChatToolCallingUnsupported, 7107)]
    [InlineData(EdgeErrorCode.ChatOptionUnsupported, 7108)]
    // Qavren.Edge.Rag
    [InlineData(EdgeErrorCode.RagRetrieverMissing, 7201)]
    [InlineData(EdgeErrorCode.RagRetrievalFailed, 7202)]
    [InlineData(EdgeErrorCode.RagCollectionNotSearchable, 7203)]
    [InlineData(EdgeErrorCode.RagContextBudgetTooSmall, 7204)]
    public void Sp4CodeHasItsSpecifiedValue(EdgeErrorCode code, int expected)
        => Assert.Equal(expected, (int)code);

    [Fact]
    public void Sp4RangeHasExactlyTwentyFourMembers()
    {
        var sp4 = Enum.GetValues<EdgeErrorCode>()
            .Where(static v => (int)v is >= 7000 and <= 7299)
            .ToArray();

        Assert.Equal(24, sp4.Length);
    }

    [Fact]
    public void EveryValueIsDistinct()
    {
        var values = Enum.GetValues<EdgeErrorCode>().Select(static v => (int)v).ToArray();

        Assert.Equal(values.Length, values.Distinct().Count());
    }

    [Fact]
    public void Sp4CodeCarriesTheHelpLinkConvention()
    {
        var exception = new EdgeConfigurationException(
            EdgeErrorCode.ChatEnvironmentNotStarted,
            "The chat environment startup task has not run.");

        Assert.EndsWith("#7001", exception.HelpLink, StringComparison.Ordinal);
    }
}
