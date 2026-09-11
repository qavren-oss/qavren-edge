using Microsoft.Extensions.AI;
using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Chat.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Spec section 6.6's precedence rule - per-call, then this client, then the preset - and the two
/// deliberately opposite typing rules for the two dictionaries.
/// </summary>
public class PrecedenceTests
{
    private static ChatMessage[] Ask() => [new ChatMessage(ChatRole.User, "hi")];

    private static async Task<IReadOnlyList<KeyValuePair<string, object>>> BuiltWithAsync(
        ClientHarness harness,
        ChatOptions? options)
    {
        await harness.Client.GetResponseAsync(Ask(), options, TestContext.Current.CancellationToken).ConfigureAwait(true);
        return harness.Session.SearchOptions[0];
    }

    // ---- the table ----------------------------------------------------------------------------------------

    public static TheoryData<string, string> Knobs() => new()
    {
        { "temperature", "Temperature" },
        { "top_p", "TopP" },
        { "top_k", "TopK" },
    };

    private static void SetPerCall(ChatOptions options, string knob, float value)
    {
        switch (knob)
        {
            case "Temperature":
                options.Temperature = value;
                break;
            case "TopP":
                options.TopP = value;
                break;
            case "TopK":
                options.TopK = (int)value;
                break;
        }
    }

    private static void SetEdge(EdgeChatOptions options, string knob, float value)
    {
        switch (knob)
        {
            case "Temperature":
                options.Temperature = value;
                break;
            case "TopP":
                options.TopP = value;
                break;
            case "TopK":
                options.TopK = (int)value;
                break;
        }
    }

    private static double PresetDefault(ChatPreset preset, string knob) => knob switch
    {
        "Temperature" => preset.DefaultTemperature,
        "TopP" => preset.DefaultTopP,
        _ => preset.DefaultTopK,
    };

    [Theory]
    [MemberData(nameof(Knobs))]
    public async Task APerCallValueBeatsTheClientValueWhichBeatsThePresetDefault(string searchKey, string knob)
    {
        using var harness = ClientHarness.Build(o => SetEdge(o, knob, 3));
        var perCall = new ChatOptions();
        SetPerCall(perCall, knob, 7);

        var built = await BuiltWithAsync(harness, perCall).ConfigureAwait(true);

        Assert.Equal(7, FakeChatSession.Number(built, searchKey));
    }

    [Theory]
    [MemberData(nameof(Knobs))]
    public async Task ANullPerCallValueFallsThroughToTheClientValue(string searchKey, string knob)
    {
        using var harness = ClientHarness.Build(o => SetEdge(o, knob, 3));

        var built = await BuiltWithAsync(harness, new ChatOptions()).ConfigureAwait(true);

        Assert.Equal(3, FakeChatSession.Number(built, searchKey));
    }

    [Theory]
    [MemberData(nameof(Knobs))]
    public async Task ANullAtBothLayersFallsThroughToThePresetDefaultRatherThanZeroing(string searchKey, string knob)
    {
        using var harness = ClientHarness.Build();

        var built = await BuiltWithAsync(harness, options: null).ConfigureAwait(true);

        Assert.Equal(PresetDefault(harness.Options.Preset, knob), FakeChatSession.Number(built, searchKey), precision: 5);
    }

    [Fact]
    public async Task MaxOutputTokensFollowsTheSameRuleAndIsTheManagedCounter()
    {
        var script = Enumerable.Range(0, 12).Select(i => $"t{i} ").ToArray();

        using var perCall = ClientHarness.Build(o => o.MaxOutputTokens = 5, script: script);
        var response = await perCall.Client.GetResponseAsync(Ask(), new ChatOptions { MaxOutputTokens = 3 }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(3, response.GetTurnStatus()!.GeneratedTokens);
        Assert.Equal(ChatFinishReason.Length, response.FinishReason);

        using var edge = ClientHarness.Build(o => o.MaxOutputTokens = 5, script: script);
        response = await edge.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(5, response.GetTurnStatus()!.GeneratedTokens);
        Assert.Equal(EdgeChatStopReason.MaxOutputTokens, response.GetTurnStatus()!.StopReason);

        using var preset = ClientHarness.Build(script: script);
        response = await preset.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(12, response.GetTurnStatus()!.GeneratedTokens);
        Assert.Equal(EdgeChatStopReason.Completed, response.GetTurnStatus()!.StopReason);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
    }

    [Fact]
    public async Task SeedAndPresencePenaltyMapToRandomSeedAndRepetitionPenaltyAndTopPForcesDoSample()
    {
        using var harness = ClientHarness.Build();

        var built = await BuiltWithAsync(harness, new ChatOptions { Seed = 42, PresencePenalty = 1.1f }).ConfigureAwait(true);

        Assert.Equal(42, FakeChatSession.Number(built, "random_seed"));
        Assert.Equal(1.1f, FakeChatSession.Number(built, "repetition_penalty"), precision: 5);
        Assert.True(FakeChatSession.Flag(built, "do_sample"));
    }

    // ---- SearchOptions: converted or refused, never dropped ------------------------------------------------

    [Fact]
    public async Task ABoolAndADoubleInSearchOptionsAreApplied()
    {
        using var harness = ClientHarness.Build(o =>
        {
            o.SearchOptions["do_sample"] = false;
            o.SearchOptions["temperature"] = 0.05;
        });

        var built = await BuiltWithAsync(harness, new ChatOptions { Temperature = 0.9f }).ConfigureAwait(true);

        // Applied LAST: the escape hatch beats the per-call value.
        Assert.False(FakeChatSession.Flag(built, "do_sample"));
        Assert.Equal(0.05, FakeChatSession.Number(built, "temperature"));
    }

    [Fact]
    public async Task AnIntAndALosslessLongInSearchOptionsAreConvertedToDoubles()
    {
        using var harness = ClientHarness.Build(o =>
        {
            o.SearchOptions["top_k"] = 5;
            o.SearchOptions["random_seed"] = 1L << 40;
        });

        var built = await BuiltWithAsync(harness, options: null).ConfigureAwait(true);

        Assert.Equal(5, FakeChatSession.Number(built, "top_k"));
        Assert.Equal((double)(1L << 40), FakeChatSession.Number(built, "random_seed"));
    }

    [Fact]
    public async Task ALongPast2To53InSearchOptionsIs7108NamingTheKey()
    {
        using var harness = ClientHarness.Build(o => o.SearchOptions["random_seed"] = (1L << 53) + 1);

        var exception = await Assert.ThrowsAsync<EdgeChatException>(
            () => harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatOptionUnsupported, exception.Code);
        Assert.Contains("random_seed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStringInSearchOptionsIs7108NamingTheKeyAndTheType()
    {
        using var harness = ClientHarness.Build(o => o.SearchOptions["temperature"] = "0.7");

        var exception = await Assert.ThrowsAsync<EdgeChatException>(
            () => harness.Client.GetResponseAsync(Ask(), null, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatOptionUnsupported, exception.Code);
        Assert.Contains("temperature", exception.Message, StringComparison.Ordinal);
        Assert.Contains("System.String", exception.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Session.Generators);
    }

    [Fact]
    public async Task TheSameStringInAdditionalPropertiesIsIgnoredWithoutErrorWhichKeepsARagSourceListInert()
    {
        // The deliberate OPPOSITE of the rule above, asserted side by side so nobody harmonises them.
        using var harness = ClientHarness.Build();
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["temperature"] = "0.7",
                ["qavren.edge.rag.sources"] = new List<string> { "a structured payload" },
                ["do_sample"] = false,
                ["top_p"] = 0.33,
            },
        };

        var built = await BuiltWithAsync(harness, options).ConfigureAwait(true);

        Assert.False(FakeChatSession.Has(built, "qavren.edge.rag.sources"));
        Assert.False(FakeChatSession.Flag(built, "do_sample"));
        Assert.Equal(0.33, FakeChatSession.Number(built, "top_p"));

        // The string was ignored, so temperature is still the preset's.
        Assert.Equal(harness.Options.Preset.DefaultTemperature, FakeChatSession.Number(built, "temperature"), precision: 5);
    }

    [Fact]
    public void ConvertSearchOptionRoundTripsExactlyOrRefuses()
    {
        Assert.Equal(1.5, ChatTurnPipeline.ConvertSearchOption("k", 1.5f));
        Assert.Equal(2.0, ChatTurnPipeline.ConvertSearchOption("k", (short)2));
        Assert.Equal(3.0, ChatTurnPipeline.ConvertSearchOption("k", (byte)3));
        Assert.Equal(0.25, ChatTurnPipeline.ConvertSearchOption("k", 0.25m));
        Assert.Equal(true, ChatTurnPipeline.ConvertSearchOption("k", true));

        // More significant digits than a double holds, so the round trip cannot be exact.
        var lossy = Assert.Throws<EdgeChatException>(() => ChatTurnPipeline.ConvertSearchOption("k", 0.1234567890123456789m));
        Assert.Equal(EdgeErrorCode.ChatOptionUnsupported, lossy.Code);

        var nul = Assert.Throws<EdgeChatException>(() => ChatTurnPipeline.ConvertSearchOption("k", null));
        Assert.Contains("null", nul.Message, StringComparison.Ordinal);

        var enumeration = Assert.Throws<EdgeChatException>(() => ChatTurnPipeline.ConvertSearchOption("k", DayOfWeek.Monday));
        Assert.Contains("DayOfWeek", enumeration.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopSequencesAreAUnionLongestFirstAndThePresetsAreNeverDeleted()
    {
        using var harness = ClientHarness.Build(o => o.StopSequences.Add("\nUser:"));

        await harness.Client.GetResponseAsync(Ask(), new ChatOptions { StopSequences = ["END", "<|eot_id|>"] }, TestContext.Current.CancellationToken).ConfigureAwait(true);

        var composed = EdgeChatOptions.ComposeStopSequences(harness.Options.Preset, harness.Options, ["END", "<|eot_id|>"]);

        Assert.Equal(["<|end_of_text|>", "<|eot_id|>", "<|eom_id|>", "\nUser:", "END"], composed);
    }

    [Fact]
    public async Task SystemPromptAndInstructionsBecomeLeadingSystemMessagesBehindAnExplicitOne()
    {
        using var harness = ClientHarness.Build(o => o.SystemPrompt = "OPTIONS-SYSTEM");
        ChatMessage[] messages =
        [
            new(ChatRole.System, "EXPLICIT-SYSTEM"),
            new(ChatRole.User, "hi"),
        ];

        await harness.Client.GetResponseAsync(messages, new ChatOptions { Instructions = "CALL-INSTRUCTIONS" }, TestContext.Current.CancellationToken).ConfigureAwait(true);

        var rendered = harness.Session.TemplateCalls[0];
        var explicitAt = rendered.IndexOf("EXPLICIT-SYSTEM", StringComparison.Ordinal);
        var optionsAt = rendered.IndexOf("OPTIONS-SYSTEM", StringComparison.Ordinal);
        var callAt = rendered.IndexOf("CALL-INSTRUCTIONS", StringComparison.Ordinal);
        var userAt = rendered.IndexOf("\"role\":\"user\"", StringComparison.Ordinal);

        Assert.True(explicitAt >= 0 && explicitAt < optionsAt && optionsAt < callAt && callAt < userAt);
    }
}
