using Microsoft.ML.OnnxRuntimeGenAI;

namespace Qavren.Edge.Chat.Internal;

/// <summary>
/// <see cref="IChatPromptContext"/> over one turn's session. What a consumer's
/// <see cref="ChatPromptFormatter"/> may reach - the template and a token count - and not the raw
/// tokenizer, because the decode loop owns the gate.
/// </summary>
/// <param name="session">The turn's session.</param>
/// <param name="presetId">The preset, for the 7101 message.</param>
/// <param name="shape">The loaded model's geometry.</param>
/// <param name="resolvedContextTokens">The context the budget allowed.</param>
internal sealed class ChatPromptContext(
    IChatModelSession session,
    string presetId,
    ChatModelShape shape,
    int resolvedContextTokens) : IChatPromptContext
{
    /// <inheritdoc />
    public ChatModelShape Shape { get; } = shape;

    /// <inheritdoc />
    public int ResolvedContextTokens { get; } = resolvedContextTokens;

    /// <inheritdoc />
    public string ApplyChatTemplate(string messagesJson, bool addGenerationPrompt)
    {
        ArgumentNullException.ThrowIfNull(messagesJson);

        try
        {
            return session.ApplyChatTemplate(messagesJson, addGenerationPrompt);
        }
        catch (OnnxRuntimeGenAIException ex)
        {
            throw TemplateUnsupported(presetId, ex);
        }
        catch (InvalidOperationException ex)
        {
            throw TemplateUnsupported(presetId, ex);
        }
    }

    /// <inheritdoc />
    public int CountTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length == 0 ? 0 : session.Encode(text).Length;
    }

    /// <summary>Spec section 15.3's 7101 row, raised from the turn rather than the warm-up.</summary>
    /// <param name="presetId">The preset.</param>
    /// <param name="inner">What minja threw.</param>
    /// <returns>The exception to throw.</returns>
    public static EdgeChatException TemplateUnsupported(string presetId, Exception inner) =>
        new(
            EdgeErrorCode.ChatTemplateUnsupported,
            $"minja could not render the chat template of '{presetId}' for this turn: {inner.Message}",
            inner)
        {
            PresetId = presetId,
            Remediation =
                "Set EdgeChatOptions.PromptFormatter to format prompts yourself, or set " +
                "EdgeChatOptions.RequireChatTemplate = false to accept the role-prefixed " +
                "fallback, whose output quality is materially worse.",
        };
}
