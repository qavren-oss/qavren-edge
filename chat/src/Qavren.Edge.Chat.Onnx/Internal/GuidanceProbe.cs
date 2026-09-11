using Microsoft.Extensions.Logging;

namespace Qavren.Edge.Chat.Internal;

/// <summary>Generates at most <paramref name="maxTokens"/> tokens under a guidance constraint.</summary>
/// <param name="guidanceType">The guidance type, always <c>"json_schema"</c> here.</param>
/// <param name="guidanceData">The schema.</param>
/// <param name="maxTokens">The token cap.</param>
/// <returns>The decoded output.</returns>
/// <remarks>
/// A delegate rather than a <c>Model</c> so the probe's own logic is a tier-1 unit test: the real
/// load path passes a closure over the loaded model and the tests pass a lambda.
/// </remarks>
internal delegate string GuidanceProbeRunner(string guidanceType, string guidanceData, int maxTokens);

/// <summary>
/// Spec section 9.6's probe. It asserts <b>output shape</b> and never the absence of an exception,
/// and that is the whole design.
/// </summary>
/// <remarks>
/// At v0.15.2 <c>CreateGuidanceLogitsProcessor</c> on a <c>USE_GUIDANCE=OFF</c> build returns
/// <c>nullptr</c> after an optional log line and <b>does not throw</b>, so an exception-based probe
/// would report success on a build that enforces nothing.
/// <para>
/// <c>SetGuidance</c> is called with <b>two</b> arguments (plan adjustment 3). Its real signature is
/// <c>SetGuidance(string type, string data, bool enableFFTokens = false)</c>, and the third
/// parameter is left at its default deliberately: fast-forward tokens change what is emitted, and a
/// positive control that changes the thing it measures is not a control.
/// </para>
/// </remarks>
internal static class GuidanceProbe
{
    /// <summary>The guidance type both this probe and the turn pipeline use.</summary>
    public const string GuidanceType = "json_schema";

    /// <summary>A schema with exactly one legal completion, so a match is unambiguous.</summary>
    public const string SchemaJson = "{\"type\":\"string\",\"const\":\"qedge\"}";

    /// <summary>The only string that schema permits.</summary>
    public const string ExpectedOutput = "qedge";

    /// <summary>Eight tokens is more than enough for a five-character constant.</summary>
    public const int MaxTokens = 8;

    private static readonly Action<ILogger, string, EdgeGuidanceProbeResult, Exception?> s_probed =
        LoggerMessage.Define<string, EdgeGuidanceProbeResult>(
            LogLevel.Information,
            new EventId(EdgeChatEventIds.GuidanceProbed, nameof(EdgeChatEventIds.GuidanceProbed)),
            "Constrained decoding probed for {PresetId}: {Guidance}. NotEnforced means not proven " +
            "enforced, never proven absent.");

    // Spec 14.4: the decoded eight tokens are model output, so Trace and never above.
    private static readonly Action<ILogger, string, Exception?> s_decoded =
        LoggerMessage.Define<string>(
            LogLevel.Trace,
            new EventId(EdgeChatEventIds.GuidanceProbed, nameof(EdgeChatEventIds.GuidanceProbed)),
            "Guidance probe decoded: {Decoded}");

    /// <summary>Runs the probe once per model.</summary>
    /// <param name="run">The generation seam.</param>
    /// <param name="presetId">The preset, for the log.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>
    /// <see cref="EdgeGuidanceProbeResult.Enforced"/> when it ran and matched,
    /// <see cref="EdgeGuidanceProbeResult.NotEnforced"/> when it ran and did not, and
    /// <see cref="EdgeGuidanceProbeResult.ProbeFailed"/> when it threw.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> is null.</exception>
    public static EdgeGuidanceProbeResult Probe(GuidanceProbeRunner run, string presetId, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(logger);

        string decoded;
        try
        {
            decoded = run(GuidanceType, SchemaJson, MaxTokens);
        }
#pragma warning disable CA1031 // A probe that threw is a probe result, not a load failure.
        catch (Exception)
#pragma warning restore CA1031
        {
            s_probed(logger, presetId, EdgeGuidanceProbeResult.ProbeFailed, null);
            return EdgeGuidanceProbeResult.ProbeFailed;
        }

        s_decoded(logger, decoded ?? string.Empty, null);

        var verdict = Matches(decoded)
            ? EdgeGuidanceProbeResult.Enforced
            : EdgeGuidanceProbeResult.NotEnforced;

        s_probed(logger, presetId, verdict, null);
        return verdict;
    }

    /// <summary>
    /// Whether the decoded output is the one string the schema permits.
    /// </summary>
    /// <param name="decoded">The probe's decoded output.</param>
    /// <returns><see langword="true"/> when the constraint was demonstrably applied.</returns>
    /// <remarks>
    /// Surrounding whitespace and the JSON string quotes are stripped before comparing, because a
    /// conforming generator may emit either <c>qedge</c> or <c>"qedge"</c> depending on how the
    /// schema is projected, and neither is evidence that the constraint was ignored.
    /// </remarks>
    public static bool Matches(string? decoded)
    {
        if (decoded is null)
        {
            return false;
        }

        var text = decoded.Trim().Trim('"');
        return string.Equals(text, ExpectedOutput, StringComparison.Ordinal);
    }
}
