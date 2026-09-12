using System.Text;
using Microsoft.ML.OnnxRuntime;
using Qavren.Edge.Diagnostics;
using Xunit;

namespace Qavren.Edge.Chat.Tests.Tier2;

/// <summary>
/// Spec 8.1's first table row, asserted from outside both packages and with no <c>internal</c>
/// reached for: after <c>EnsureStartedAsync</c> on a container built by <c>AddOnnxChat</c>, the
/// <c>Qavren.Edge.Chat.Onnx</c> block reports <c>ortEnvCreatedBeforeGenAi = true</c>, no event 903
/// was logged, and the <c>Qavren.Edge.Onnx</c> block <b>in the same report</b> reports
/// <c>ortEnvironmentPreexisting = false</c>.
/// </summary>
/// <remarks>
/// The fixture ran the one <c>EnsureStartedAsync</c> in the collection; this class only reads. The
/// collection runs after every tier-1 class (<see cref="NativesLastCollectionOrderer"/>), which is
/// what makes the sub-project 2 key a statement about THIS container's order-200 task rather than
/// about whichever test touched ORT first.
/// </remarks>
[Collection(TinyChatModelCollectionDefinition.Name)]
public sealed class OrderingTests(TinyChatModelFixture fixture)
{
    private const string ChatComponent = "Qavren.Edge.Chat.Onnx";
    private const string OnnxComponent = "Qavren.Edge.Onnx";

    [Fact]
    public void TheOrtEnvOrderingRowIsAssertedFromTwoPublicDiagnosticsKeysInOneReport()
    {
        var report = fixture.Diagnostics.Report();

        var chat = Assert.Single(report.Components, c => c.Name == ChatComponent);
        var onnx = Assert.Single(report.Components, c => c.Name == OnnxComponent);

        Assert.Equal("True", chat.Details["ortEnvCreatedBeforeGenAi"]);
        Assert.Equal("False", onnx.Details["ortEnvironmentPreexisting"]);

        // The order-400 task ran after order 200, so it had nothing to warn about.
        Assert.False(fixture.Logs.Logged(EdgeChatEventIds.GenAiEnvironmentOutOfOrder));
        Assert.True(fixture.Logs.Logged(EdgeChatEventIds.GenAiRuntimeInitialized));
        Assert.True(fixture.Logs.Logged(EdgeChatEventIds.GenAiTelemetryDisabled));

        Assert.Equal("True", chat.Details["ogaHandleOwned"]);
        Assert.Equal("True", chat.Details["telemetryDisabled"]);

        // And the managed guard agrees: sub-project 2's order-200 task created OrtEnv.
        Assert.True(OrtEnv.IsCreated);
        Assert.True(fixture.Environment.OrtEnvCreatedBeforeGenAi);
    }

    [Fact]
    public void TheChatDiagnosticsBlockIsLoggedInFullForTheLane()
    {
        // Spec 16.4: the lane logs the full chat block and publishes it as an artifact, which is
        // also how spec 19 item 10's real TotalMem / PhysicalMemory readings accumulate.
        var report = fixture.Diagnostics.Report();
        var chat = Assert.Single(report.Components, c => c.Name == ChatComponent);

        JobSummary.RecordBlock("tier2-diagnostics", Describe(chat));

        Assert.Equal("True", chat.Details["chatSupportedOnThisAbi"]);
        Assert.NotNull(chat.Details["runtimeIdentifier"]);
        Assert.NotNull(chat.Details["availableMemoryKind"]);
        Assert.NotEqual("Unknown", chat.Details["availableMemoryKind"]);
    }

    /// <summary>
    /// Flat text rather than a serializer: this repo's only JSON path is source-generated, and a
    /// lane's value here is the text in the log, not its shape.
    /// </summary>
    private static string Describe(EdgeComponentReport component)
    {
        var text = new StringBuilder();
        text.Append(component.Name).Append(' ').Append(component.Version).AppendLine();
        foreach (var (key, value) in component.Details.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            text.Append("  ").Append(key).Append(" = ").Append(value ?? "(null)").AppendLine();
        }

        return text.ToString();
    }
}
