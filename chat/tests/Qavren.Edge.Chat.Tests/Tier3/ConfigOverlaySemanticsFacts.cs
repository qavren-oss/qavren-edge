using Microsoft.ML.OnnxRuntimeGenAI;
using Xunit;
using GenAiModel = Microsoft.ML.OnnxRuntimeGenAI.Model;

namespace Qavren.Edge.Chat.Tests.Tier3;

/// <summary>
/// Spec 19 item 12, as a named test rather than a maybe (plan adjustment 28): load the Qwen
/// preset's real <c>Config</c>, call <c>Overlay</c> with exactly one <c>{"search":{"max_length"}}</c>
/// document, build a <c>GeneratorParams</c> from it and read the four shipped search values back.
/// </summary>
/// <remarks>
/// It asserts <b>nothing</b> about which answer comes back - both deep-merge and replace are legal,
/// and spec 9.1 composes one document precisely so neither can hurt - and fails only if the read
/// itself throws. Whichever way it lands is written into ADR 0009's revisit note and open risk 7.
/// </remarks>
[Collection(RealModelCollectionDefinition.Name)]
public sealed class ConfigOverlaySemanticsFacts
{
    /// <summary>The one-key overlay the item specifies.</summary>
    private const string Overlay = """{"search":{"max_length":2048}}""";

    // The Qwen preset's shipped search block, measured in Environment ground truth.
    private const double ShippedTemperature = 0.6;
    private const double ShippedTopK = 20;
    private const double ShippedTopP = 0.95;
    private const bool ShippedDoSample = true;

    [Fact(
        Skip = ChatModelAvailable.SkipReason,
        SkipUnless = nameof(ChatModelAvailable.Yes),
        SkipType = typeof(ChatModelAvailable))]
    public void OverlayMergeSemanticsAreReadBackAndRecordedNotAsserted()
    {
        using var config = new Config(ChatModelAvailable.StagedDirectory);

        // Exactly once.
        config.Overlay(Overlay);

        using var model = new GenAiModel(config);
        using var parameters = new GeneratorParams(model);

        var doSample = parameters.GetSearchBool("do_sample");
        var temperature = parameters.GetSearchNumber("temperature");
        var topK = parameters.GetSearchNumber("top_k");
        var topP = parameters.GetSearchNumber("top_p");
        var maxLength = parameters.GetSearchNumber("max_length");

        var survived = doSample == ShippedDoSample
            && Near(temperature, ShippedTemperature)
            && Near(topK, ShippedTopK)
            && Near(topP, ShippedTopP);

        JobSummary.Record("tier3-overlay", "do_sample", doSample);
        JobSummary.Record("tier3-overlay", "temperature", temperature);
        JobSummary.Record("tier3-overlay", "top_k", topK);
        JobSummary.Record("tier3-overlay", "top_p", topP);
        JobSummary.Record("tier3-overlay", "max_length", maxLength);
        JobSummary.Record("tier3-overlay", "overlayMergeSemantics", survived ? "deep-merge" : "replace");
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-6;
}
