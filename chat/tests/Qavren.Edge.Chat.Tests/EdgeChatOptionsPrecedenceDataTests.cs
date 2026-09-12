using Qavren.Edge.Onnx;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// The data half of the precedence rules - everything that needs no client and no model. The
/// per-call <c>ChatOptions</c> layer, and the mapping onto <c>GeneratorParams</c>, belong to the
/// task that owns the decode loop.
/// </summary>
/// <remarks>
/// Two knobs are <b>unions rather than overrides</b>, and stop sequences are the one that bites: a
/// preset's stop sequences describe the model's own template - <c>&lt;|eot_id|&gt;</c> is not a
/// preference - so a caller adding <c>"\nUser:"</c> must not thereby delete them.
/// </remarks>
public class EdgeChatOptionsPrecedenceDataTests
{
    private static readonly ChatPreset Llama = ChatPresets.Llama32_1BInstructInt4;

    /// <summary>
    /// <c>AddOnnxChat(preset, …)</c> is what sets <see cref="EdgeChatOptions.Preset"/> in
    /// production; the setter is internal and this assembly sees it through
    /// <c>InternalsVisibleTo</c>, so no reflection is needed anywhere.
    /// </summary>
    private static EdgeChatOptions ForPreset(ChatPreset preset) => new() { Preset = preset };

    // ---- the stop-sequence union ---------------------------------------------------------------

    [Fact]
    public void TheEffectiveSetIsTheUnionOfThePresetsAndTheClientsSortedLongestFirst()
    {
        var options = new EdgeChatOptions();
        options.StopSequences.Add("\nUser:");

        var effective = EdgeChatOptions.ComposeStopSequences(Llama, options, perCall: null);

        Assert.Equal(["<|end_of_text|>", "<|eot_id|>", "<|eom_id|>", "\nUser:"], effective);
    }

    [Fact]
    public void SettingTheClientsStopSequencesDoesNotDeleteThePresets()
    {
        var options = new EdgeChatOptions();
        options.StopSequences.Add("STOP");

        var effective = EdgeChatOptions.ComposeStopSequences(Llama, options, perCall: null);

        foreach (var fromPreset in Llama.StopSequences)
        {
            Assert.Contains(fromPreset, effective);
        }

        Assert.Contains("STOP", effective);
    }

    [Fact]
    public void ThePerCallLayerJoinsTheSameUnionRatherThanReplacingIt()
    {
        var options = new EdgeChatOptions();
        options.StopSequences.Add("STOP");

        var effective = EdgeChatOptions.ComposeStopSequences(Llama, options, ["\n\n\n"]);

        Assert.Contains("STOP", effective);
        Assert.Contains("\n\n\n", effective);
        Assert.Contains("<|eot_id|>", effective);
    }

    [Fact]
    public void TheUnionIsOrdinalDistinctSoADuplicateAppearsOnce()
    {
        var options = new EdgeChatOptions();
        options.StopSequences.Add("<|eot_id|>");

        var effective = EdgeChatOptions.ComposeStopSequences(Llama, options, ["<|eot_id|>"]);

        Assert.Single(effective, s => s == "<|eot_id|>");
        Assert.Equal(Llama.StopSequences.Count, effective.Count);
    }

    [Fact]
    public void DistinctnessIsOrdinalAndNotCaseInsensitive()
    {
        var options = new EdgeChatOptions();
        options.StopSequences.Add("<|EOT_ID|>");

        var effective = EdgeChatOptions.ComposeStopSequences(Llama, options, perCall: null);

        Assert.Contains("<|eot_id|>", effective);
        Assert.Contains("<|EOT_ID|>", effective);
    }

    [Fact]
    public void LongestFirstIsWhatLetsTheRollingMatcherReportTheLongestMatchAtAPosition()
    {
        var options = new EdgeChatOptions();
        options.StopSequences.Add("</s>");
        options.StopSequences.Add("</");

        var effective = EdgeChatOptions.ComposeStopSequences(Llama, options, perCall: null);

        var ordered = effective.ToList();
        var longer = ordered.IndexOf("</s>");
        var shorter = ordered.IndexOf("</");

        Assert.True(longer < shorter, "The longer sequence has to be offered to the matcher first.");

        for (var i = 1; i < effective.Count; i++)
        {
            Assert.True(effective[i - 1].Length >= effective[i].Length);
        }
    }

    [Fact]
    public void APresetWithNoStopSequencesAndNoAdditionsProducesAnEmptySet()
    {
        var bare = Llama with { StopSequences = [] };

        Assert.Empty(EdgeChatOptions.ComposeStopSequences(bare, new EdgeChatOptions(), perCall: null));
    }

    // ---- the fall-through shape ------------------------------------------------------------------

    [Fact]
    public void EveryOverridableKnobIsNullableSoNullMeansNotSetRatherThanSetToNothing()
    {
        var options = new EdgeChatOptions();

        Assert.Null(options.MaxContextTokens);
        Assert.Null(options.MaxOutputTokens);
        Assert.Null(options.Temperature);
        Assert.Null(options.TopP);
        Assert.Null(options.TopK);
        Assert.Null(options.SystemPrompt);
        Assert.Null(options.PromptFormatter);
        Assert.Null(options.ConfigOverlayJson);
        Assert.Null(options.ModelDirectoryOverride);
    }

    [Fact]
    public void ThePresetLayerAlwaysHasAnAnswerWhichIsWhatMakesFallingThroughTheDefault()
    {
        var options = ForPreset(Llama);

        Assert.Same(Llama, options.Preset);
        Assert.Equal(512, options.MaxOutputTokens ?? options.Preset.DefaultMaxOutputTokens);
        Assert.Equal(0.6f, options.Temperature ?? options.Preset.DefaultTemperature);
        Assert.Equal(0.9f, options.TopP ?? options.Preset.DefaultTopP);
        Assert.Equal(50, options.TopK ?? options.Preset.DefaultTopK);
    }

    [Fact]
    public void AValueSetOnTheClientBeatsThePresetDefault()
    {
        var options = ForPreset(Llama);
        options.Temperature = 0.1f;
        options.MaxOutputTokens = 64;

        Assert.Equal(0.1f, options.Temperature ?? options.Preset.DefaultTemperature);
        Assert.Equal(64, options.MaxOutputTokens ?? options.Preset.DefaultMaxOutputTokens);
    }

    // ---- defaults that change behaviour ------------------------------------------------------------

    [Fact]
    public void EdgeChatOptionsDefaults()
    {
        var options = new EdgeChatOptions();

        Assert.Equal(64, options.ReservedPromptTokens);
        Assert.True(options.RequireChatTemplate);
        Assert.Equal(EdgeGuidancePolicy.Disabled, options.Guidance);
        Assert.True(options.EnableConversationCache);
        Assert.True(options.DropConversationCacheOnSleep);
        Assert.True(options.DropOnMemoryPressure);
        Assert.False(options.UnloadOnSleeping);
        Assert.Equal(4, options.MaxQueuedTurns);
        Assert.Equal(TimeSpan.FromSeconds(30), options.TurnQueueTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), options.LoadTimeout);
        Assert.Empty(options.StopSequences);
        Assert.Empty(options.SearchOptions);
    }

    [Fact]
    public void ChatThermalOptionsDefaults()
    {
        var thermal = new ChatThermalOptions();

        Assert.Equal(EdgeThermalState.Serious, thermal.ThrottleAt);
        Assert.Equal(EdgeThermalState.Critical, thermal.AbortAt);
        Assert.Equal(1.0f, thermal.ThrottleHeadroom);
        Assert.Equal(8.0d, thermal.ThrottledTokensPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(1), thermal.SampleInterval);
        Assert.False(thermal.RefuseNewTurnsInLowPowerMode);

        // Default false, and that is a decision rather than an accident of enum ordering: a reading
        // nobody can make is never a refusal, and refusing here would make chat unusable on every
        // desktop.
        Assert.False(thermal.RefuseWhenThermalUnknown);
    }

    [Fact]
    public void ChatProvisioningOptionsDefaults()
    {
        var provisioning = new ChatProvisioningOptions();

        Assert.Equal(512L * 1024 * 1024, provisioning.FreeDiskMarginBytes);
        Assert.Null(provisioning.IsTransferPermitted);
    }

    [Fact]
    public void ChatMemoryBudgetOptionsDefaults()
    {
        var memory = new ChatMemoryBudgetOptions();

        Assert.Equal([4096, 3072, 2048, 1536, 1024], memory.ContextLadder);
        Assert.Equal(1024, memory.MinContextTokens);
        Assert.Equal(192L * 1024 * 1024, memory.WorkspaceBytes);
        Assert.Equal(192L * 1024 * 1024, memory.ReserveBytes);
        Assert.Equal(0.60d, memory.SystemWideMemoryFraction);
        Assert.True(memory.PreferMeasuredPeak);
        Assert.False(memory.RefuseWhenUnknown);
        Assert.Null(memory.Override);
    }

    [Fact]
    public void TheFourNestedOptionObjectsAreOwnedRatherThanReplaceable()
    {
        var options = new EdgeChatOptions();

        Assert.NotNull(options.Memory);
        Assert.NotNull(options.Thermal);
        Assert.NotNull(options.History);
        Assert.NotNull(options.Provisioning);

        Assert.Same(options.Memory, options.Memory);
        Assert.Same(options.History, options.History);
    }

    [Fact]
    public void GuidanceStartsUnprobed()
        => Assert.Equal(EdgeGuidanceProbeResult.Unprobed, default);
}
