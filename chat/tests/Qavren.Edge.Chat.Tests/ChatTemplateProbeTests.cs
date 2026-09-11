using Microsoft.Extensions.Logging;
using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Chat.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Spec sections 9.5 and 15.3: 7101's raise site.
/// </summary>
/// <remarks>
/// The probe exists because <c>TokenizerImpl::LoadChatTemplate</c> returns <c>kOrtxOK</c> with only
/// a <b>warning</b> when minja cannot parse the template - so the failure would otherwise surface on
/// the first real <c>ApplyChatTemplate</c> call, in the middle of a user's first message.
/// </remarks>
public class ChatTemplateProbeTests
{
    private const string PresetId = "llama-3.2-1b-instruct-int4";

    private static string Renders(string messagesJson, bool addGenerationPrompt) =>
        $"<|begin_of_text|>{messagesJson}{(addGenerationPrompt ? "<|assistant|>" : string.Empty)}";

    private static string Fails(string messagesJson, bool addGenerationPrompt) =>
        throw new InvalidOperationException("minja: unsupported template construct");

    [Fact]
    public void AWorkingTemplateProbesCleanAndLogs950()
    {
        var logger = new RecordingLogger();

        var result = ChatTemplateProbe.Probe(Renders, PresetId, requireChatTemplate: true, hasConsumerFormatter: false, logger);

        Assert.True(result.Supported);
        Assert.Equal(ChatTemplateProbe.ModelTemplateFormatter, result.PromptFormatter);
        Assert.True(logger.Logged(EdgeChatEventIds.ChatTemplateProbed));
        Assert.False(logger.Logged(EdgeChatEventIds.ChatTemplateUnsupported));
    }

    [Fact]
    public void TheRenderedProbeOutputIsTraceOnly()
    {
        // Spec 14.4: the rendered output is text the model's template owns, and the privacy rule
        // that applies to a prompt applies to it. At Debug it is not written at all, while the
        // 950 verdict still is.
        var logger = new RecordingLogger { MinimumLevel = LogLevel.Debug };

        ChatTemplateProbe.Probe(Renders, PresetId, requireChatTemplate: true, hasConsumerFormatter: false, logger);

        Assert.True(logger.Logged(EdgeChatEventIds.ChatTemplateProbed));
        Assert.DoesNotContain(logger.Records, r => r.Message.Contains("<|begin_of_text|>", StringComparison.Ordinal));

        var trace = new RecordingLogger { MinimumLevel = LogLevel.Trace };
        ChatTemplateProbe.Probe(Renders, PresetId, requireChatTemplate: true, hasConsumerFormatter: false, trace);

        var rendered = Assert.Single(
            trace.Records,
            r => r.Message.Contains("<|begin_of_text|>", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Trace, rendered.Level);
    }

    [Fact]
    public void AFailedProbeUnderRequireChatTemplateIs7101NamingTheModelAndPromptFormatter()
    {
        // RequireChatTemplate defaults to TRUE, so this is the arm an app gets by default.
        Assert.True(new EdgeChatOptions { Preset = ChatPresets.Llama32_1BInstructInt4 }.RequireChatTemplate);

        var logger = new RecordingLogger();

        var exception = Assert.Throws<EdgeChatException>(() => ChatTemplateProbe.Probe(
            Fails, PresetId, requireChatTemplate: true, hasConsumerFormatter: false, logger));

        Assert.Equal(EdgeErrorCode.ChatTemplateUnsupported, exception.Code);
        Assert.Equal(PresetId, exception.PresetId);
        Assert.Contains(PresetId, exception.Message, StringComparison.Ordinal);
        Assert.Contains("PromptFormatter", exception.Remediation!, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public void AFailedProbeWithoutRequireChatTemplateLogs951AndStartUpSucceeds()
    {
        var logger = new RecordingLogger();

        var result = ChatTemplateProbe.Probe(
            Fails, PresetId, requireChatTemplate: false, hasConsumerFormatter: false, logger);

        Assert.False(result.Supported);
        Assert.Equal(ChatTemplateProbe.FallbackFormatter, result.PromptFormatter);
        Assert.True(logger.Logged(EdgeChatEventIds.ChatTemplateUnsupported));

        var line = Assert.Single(logger.For(EdgeChatEventIds.ChatTemplateUnsupported));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains(ChatTemplateProbe.FallbackFormatter, line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATemplateThatRendersNothingIsAFailureToo()
    {
        // minja reports a template it cannot use the same silent way it reports one it cannot parse.
        var logger = new RecordingLogger();

        var result = ChatTemplateProbe.Probe(
            static (_, _) => string.Empty, PresetId, requireChatTemplate: false, hasConsumerFormatter: false, logger);

        Assert.False(result.Supported);
    }

    [Fact]
    public void AConsumerFormatterIsNeverRefusedForATemplateItDoesNotUse()
    {
        var logger = new RecordingLogger();

        var result = ChatTemplateProbe.Probe(
            Fails, PresetId, requireChatTemplate: true, hasConsumerFormatter: true, logger);

        Assert.False(result.Supported);
        Assert.Equal(ChatTemplateProbe.ConsumerFormatter, result.PromptFormatter);
        Assert.True(logger.Logged(EdgeChatEventIds.PromptFormatterOverridden));
    }

    [Fact]
    public void TheProbeSendsTwoMessagesInBothRoles()
    {
        string? seen = null;
        ChatTemplateProbe.Probe(
            (messagesJson, _) =>
            {
                seen = messagesJson;
                return "ok";
            },
            PresetId,
            requireChatTemplate: true,
            hasConsumerFormatter: false,
            new RecordingLogger());

        Assert.Equal(ChatTemplateProbe.ProbeMessagesJson, seen);
        Assert.Contains("\"user\"", seen!, StringComparison.Ordinal);
        Assert.Contains("\"assistant\"", seen!, StringComparison.Ordinal);
    }
}

/// <summary>Spec section 9.6: the guidance probe asserts output shape, never absence of a throw.</summary>
public class GuidanceProbeTests
{
    [Fact]
    public void AMatchingOutputIsEnforced()
    {
        var logger = new RecordingLogger();

        var result = GuidanceProbe.Probe(
            static (_, _, _) => GuidanceProbe.ExpectedOutput, "preset", logger);

        Assert.Equal(EdgeGuidanceProbeResult.Enforced, result);
        Assert.True(logger.Logged(EdgeChatEventIds.GuidanceProbed));
    }

    [Fact]
    public void AQuotedMatchIsAlsoEnforced()
        => Assert.Equal(
            EdgeGuidanceProbeResult.Enforced,
            GuidanceProbe.Probe(static (_, _, _) => "\"qedge\"\n", "preset", new RecordingLogger()));

    [Fact]
    public void AnUnconstrainedOutputIsNotEnforcedAndNotAThrow()
    {
        // At v0.15.2 CreateGuidanceLogitsProcessor on a USE_GUIDANCE=OFF build returns nullptr after
        // an optional log line and DOES NOT THROW, so an exception-based probe would report success
        // on a build that enforces nothing. NotEnforced means "not proven enforced".
        Assert.Equal(
            EdgeGuidanceProbeResult.NotEnforced,
            GuidanceProbe.Probe(static (_, _, _) => "Sure! Here is a JSON string.", "preset", new RecordingLogger()));
    }

    [Fact]
    public void AThrowingProbeIsProbeFailedAndNotALoadFailure()
        => Assert.Equal(
            EdgeGuidanceProbeResult.ProbeFailed,
            GuidanceProbe.Probe(
                static (_, _, _) => throw new InvalidOperationException("no guidance in this build"),
                "preset",
                new RecordingLogger()));

    [Fact]
    public void TheProbeUsesASchemaWithExactlyOneLegalCompletionAndEightTokens()
    {
        string? type = null;
        string? data = null;
        var maxTokens = 0;

        GuidanceProbe.Probe(
            (t, d, m) =>
            {
                type = t;
                data = d;
                maxTokens = m;
                return GuidanceProbe.ExpectedOutput;
            },
            "preset",
            new RecordingLogger());

        Assert.Equal("json_schema", type);
        Assert.Equal("{\"type\":\"string\",\"const\":\"qedge\"}", data);
        Assert.Equal(8, maxTokens);
    }

    [Fact]
    public void TheDecodedOutputIsTraceOnly()
    {
        var logger = new RecordingLogger { MinimumLevel = LogLevel.Debug };

        GuidanceProbe.Probe(static (_, _, _) => "qedge", "preset", logger);

        Assert.True(logger.Logged(EdgeChatEventIds.GuidanceProbed));
        Assert.DoesNotContain(
            logger.Records,
            r => r.Message.StartsWith("Guidance probe decoded", StringComparison.Ordinal));
    }
}
