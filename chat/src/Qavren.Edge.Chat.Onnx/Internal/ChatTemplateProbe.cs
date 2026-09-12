using Microsoft.Extensions.Logging;

namespace Qavren.Edge.Chat.Internal;

/// <summary>Renders a messages array through the model's own Jinja template.</summary>
/// <param name="messagesJson">A JSON array of <c>{"role","content"}</c>.</param>
/// <param name="addGenerationPrompt">Whether to append the assistant turn header.</param>
/// <returns>The formatted prompt.</returns>
/// <remarks>
/// A delegate rather than a <c>Tokenizer</c> so the probe is a tier-1 unit test with no natives:
/// the real load path passes <c>tokenizer.ApplyChatTemplate</c> and the tests pass a lambda that
/// throws.
/// </remarks>
internal delegate string ChatTemplateRenderer(string messagesJson, bool addGenerationPrompt);

/// <summary>What the chat-template probe decided.</summary>
/// <param name="Supported">Whether minja rendered the model's template.</param>
/// <param name="PromptFormatter">Which formatter this model's prompts will go through.</param>
/// <param name="Rendered">
/// The rendered probe output, or null when the probe failed. <b>Trace-only</b>: it is text the
/// template owns and the privacy rule in spec section 14.4 applies to it.
/// </param>
/// <param name="Failure">The exception minja raised, or null.</param>
internal readonly record struct ChatTemplateProbeResult(
    bool Supported,
    string PromptFormatter,
    string? Rendered,
    Exception? Failure);

/// <summary>
/// Spec section 9.5's probe. It exists because <c>TokenizerImpl::LoadChatTemplate</c> returns
/// <c>kOrtxOK</c> with only a <b>warning</b> when minja cannot parse the template - so without a
/// probe the failure surfaces on the first real <c>ApplyChatTemplate</c> call, in the middle of a
/// user's first message, and not at load.
/// </summary>
internal static class ChatTemplateProbe
{
    /// <summary>The two-message probe. Small, ordinary, and in both roles a template must handle.</summary>
    public const string ProbeMessagesJson =
        """[{"role":"user","content":"ping"},{"role":"assistant","content":"pong"}]""";

    /// <summary>The formatter name published when the model's own template is used.</summary>
    public const string ModelTemplateFormatter = "model chat_template (minja)";

    /// <summary>The formatter name published when the consumer supplied one.</summary>
    public const string ConsumerFormatter = "EdgeChatOptions.PromptFormatter";

    /// <summary>The formatter name published when the fallback is used.</summary>
    public const string FallbackFormatter = "role-prefixed fallback (materially worse output)";

    private static readonly Action<ILogger, string, Exception?> s_probed =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(EdgeChatEventIds.ChatTemplateProbed, nameof(EdgeChatEventIds.ChatTemplateProbed)),
            "Chat template probed for {PresetId}: minja rendered the model's own template.");

    private static readonly Action<ILogger, string, string, Exception?> s_unsupported =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(EdgeChatEventIds.ChatTemplateUnsupported, nameof(EdgeChatEventIds.ChatTemplateUnsupported)),
            "minja could not parse the chat template of {PresetId}; falling back to {PromptFormatter}. " +
            "Output quality is materially worse. Set EdgeChatOptions.PromptFormatter to take over " +
            "formatting, or RequireChatTemplate to fail startup instead.");

    // Spec 14.4: the rendered probe output is text the model's template owns, so it is Trace and
    // never above, exactly as a prompt is.
    private static readonly Action<ILogger, string, Exception?> s_rendered =
        LoggerMessage.Define<string>(
            LogLevel.Trace,
            new EventId(EdgeChatEventIds.ChatTemplateProbed, nameof(EdgeChatEventIds.ChatTemplateProbed)),
            "Chat template probe rendered: {Rendered}");

    private static readonly Action<ILogger, string, Exception?> s_formatterOverridden =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(EdgeChatEventIds.PromptFormatterOverridden, nameof(EdgeChatEventIds.PromptFormatterOverridden)),
            "EdgeChatOptions.PromptFormatter replaces the chat template for {PresetId}.");

    /// <summary>Runs the probe once and applies <c>EdgeChatOptions.RequireChatTemplate</c>.</summary>
    /// <param name="render">The renderer under test.</param>
    /// <param name="presetId">The preset, for the message and the logs.</param>
    /// <param name="requireChatTemplate">
    /// <c>EdgeChatOptions.RequireChatTemplate</c>. True - the default - turns a probe failure into
    /// <see cref="EdgeErrorCode.ChatTemplateUnsupported"/> (7101) at warm-up.
    /// </param>
    /// <param name="hasConsumerFormatter">
    /// Whether <c>EdgeChatOptions.PromptFormatter</c> is set. A consumer who replaced the whole
    /// step is never refused for a template they are not using.
    /// </param>
    /// <param name="logger">The logger.</param>
    /// <returns>The verdict, and the formatter name the diagnostics block publishes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="render"/> is null.</exception>
    /// <exception cref="EdgeChatException">
    /// <see cref="EdgeErrorCode.ChatTemplateUnsupported"/> (7101), when the probe failed and
    /// <paramref name="requireChatTemplate"/> is set.
    /// </exception>
    public static ChatTemplateProbeResult Probe(
        ChatTemplateRenderer render,
        string presetId,
        bool requireChatTemplate,
        bool hasConsumerFormatter,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(logger);

        string rendered;
        try
        {
            rendered = render(ProbeMessagesJson, addGenerationPrompt: true);
        }
#pragma warning disable CA1031 // The whole point of the probe is that ANY failure is a failure.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Unsupported(presetId, requireChatTemplate, hasConsumerFormatter, logger, ex);
        }

        if (string.IsNullOrEmpty(rendered))
        {
            // A template that renders nothing is a template that cannot format a turn, and minja
            // reports that the same silent way it reports a parse failure.
            return Unsupported(presetId, requireChatTemplate, hasConsumerFormatter, logger, failure: null);
        }

        s_probed(logger, presetId, null);
        s_rendered(logger, rendered, null);

        if (hasConsumerFormatter)
        {
            s_formatterOverridden(logger, presetId, null);
            return new ChatTemplateProbeResult(true, ConsumerFormatter, rendered, null);
        }

        return new ChatTemplateProbeResult(true, ModelTemplateFormatter, rendered, null);
    }

    private static ChatTemplateProbeResult Unsupported(
        string presetId,
        bool requireChatTemplate,
        bool hasConsumerFormatter,
        ILogger logger,
        Exception? failure)
    {
        if (hasConsumerFormatter)
        {
            // The consumer replaced the whole step, so a template nobody will call is not a reason
            // to refuse to start.
            s_formatterOverridden(logger, presetId, null);
            return new ChatTemplateProbeResult(false, ConsumerFormatter, null, failure);
        }

        if (requireChatTemplate)
        {
            throw new EdgeChatException(
                EdgeErrorCode.ChatTemplateUnsupported,
                $"minja could not render the chat template of '{presetId}'. ORT GenAI reports " +
                "this as a warning and returns success at load, so without this probe it would " +
                "surface in the middle of a user's first message.",
                failure)
            {
                PresetId = presetId,
                Remediation =
                    "Set EdgeChatOptions.PromptFormatter to format prompts yourself, or set " +
                    "EdgeChatOptions.RequireChatTemplate = false to accept the role-prefixed " +
                    "fallback, whose output quality is materially worse.",
            };
        }

        s_unsupported(logger, presetId, FallbackFormatter, failure);
        return new ChatTemplateProbeResult(false, FallbackFormatter, null, failure);
    }
}
