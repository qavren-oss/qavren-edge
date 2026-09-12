using System.Text.Json;
using System.Text.Json.Nodes;

namespace Qavren.Edge.Chat.Internal;

/// <summary>
/// Composes the <b>single</b> document handed to <c>Config.Overlay(json)</c>.
/// </summary>
/// <remarks>
/// <b>Exactly one <c>Overlay</c> call, with the merge done here in managed code.</b> Nothing in the
/// researched native surface says whether <c>Overlay</c> deep-merges into the existing config or
/// replaces the named section, and the difference is load-bearing twice over: two sequential calls
/// compose only if it merges, and a <c>{"search":{...}}</c> overlay preserves the model's shipped
/// <c>do_sample</c> / <c>temperature</c> / <c>top_k</c> / <c>top_p</c> only if it merges
/// <i>within</i> a section. Sub-project 4 will not bet the memory cap on undocumented semantics, so
/// it re-emits the model's own <c>search</c> block, merges the consumer's overlay over it, writes
/// the budget's answer into <c>search.max_length</c> last, and hands the native API one document.
/// <para>
/// Writing <c>max_length</c> into the config at all is the belt rather than the braces: both
/// shipped presets set <c>past_present_share_buffer: true</c>, which selects the <b>static</b> KV
/// path where the cache is allocated once to <c>max_length</c> - so a config carrying the budget's
/// answer is capped even on a turn that never reaches <c>GeneratorParams</c>.
/// </para>
/// </remarks>
internal static class GenAiConfigOverlay
{
    /// <summary>The search key sub-project 4 owns and no caller may set.</summary>
    public const string MaxLengthKey = "max_length";

    private const string SearchSection = "search";
    private const string ProviderOptionsKey = "provider_options";
    private const string ProvidersKey = "providers";
    private const string CpuProvider = "cpu";

    /// <summary>
    /// Whether this compilation is a mobile target framework, which is what makes a non-CPU
    /// execution provider a refusal rather than a choice.
    /// </summary>
    /// <remarks>
    /// A property rather than a bare <c>#if</c> at the call site so the refusal is testable from
    /// the host lane: <see cref="Compose"/> takes the answer as a parameter and the load path
    /// passes this.
    /// </remarks>
    public static bool IsMobileTargetFramework =>
#if ANDROID || IOS || MACCATALYST
        true;
#else
        false;
#endif

    /// <summary>Composes the one overlay document.</summary>
    /// <param name="modelConfigJson">The provisioned <c>genai_config.json</c>'s text.</param>
    /// <param name="consumerOverlayJson"><c>EdgeChatOptions.ConfigOverlayJson</c>, or null.</param>
    /// <param name="resolvedContextTokens">The budget's answer. It wins over everything.</param>
    /// <param name="mobileTargetFramework">
    /// Whether a non-CPU provider in <paramref name="consumerOverlayJson"/> is a refusal.
    /// </param>
    /// <returns>One JSON object, ready for a single <c>Config.Overlay</c> call.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="modelConfigJson"/> is null.</exception>
    /// <exception cref="EdgeChatException">
    /// <see cref="EdgeErrorCode.ChatExecutionProviderUnsupported"/> (7009) when the consumer's
    /// overlay names a non-CPU provider on a mobile target framework, or
    /// <see cref="EdgeErrorCode.ChatConfigurationInvalid"/> (7007) when the overlay is not a JSON
    /// object.
    /// </exception>
    public static string Compose(
        string modelConfigJson,
        string? consumerOverlayJson,
        int resolvedContextTokens,
        bool mobileTargetFramework)
    {
        ArgumentNullException.ThrowIfNull(modelConfigJson);

        var composed = new JsonObject();

        // (a) The model's own search block, re-emitted verbatim, so its shipped sampling survives
        //     whichever way the native call behaves.
        if (ParseObject(modelConfigJson, "genai_config.json") is { } model
            && model[SearchSection] is JsonObject modelSearch)
        {
            composed[SearchSection] = modelSearch.DeepClone();
        }

        // (b) The consumer's overlay, merged OVER the model's.
        if (!string.IsNullOrWhiteSpace(consumerOverlayJson))
        {
            var overlay = ParseObject(consumerOverlayJson, "EdgeChatOptions.ConfigOverlayJson")
                ?? throw Invalid(
                    "EdgeChatOptions.ConfigOverlayJson must be a JSON object; a scalar or an array " +
                    "cannot be merged into a genai_config.json.");

            if (mobileTargetFramework && FindNonCpuProvider(overlay) is { } provider)
            {
                throw new EdgeChatException(
                    EdgeErrorCode.ChatExecutionProviderUnsupported,
                    "EdgeChatOptions.ConfigOverlayJson names the execution provider " +
                    $"'{provider}', and ORT GenAI ships no such provider on this mobile target " +
                    "framework - there is no CoreML and no NNAPI provider in any shipped build. " +
                    "CPU is the only answer here.")
                {
                    Remediation =
                        "Remove the provider from ConfigOverlayJson. On every platform this suite " +
                        "ships to the correct answer is CPU, which is why there is no typed " +
                        "execution-provider policy to set instead.",
                };
            }

            Merge(composed, overlay);
        }

        // (c) Sub-project 4's value wins, which is the whole point of composing the document here.
        var search = composed[SearchSection] as JsonObject;
        if (search is null)
        {
            search = [];
            composed[SearchSection] = search;
        }

        search[MaxLengthKey] = resolvedContextTokens;

        return composed.ToJsonString();
    }

    /// <summary>
    /// Refuses <c>max_length</c> in <c>EdgeChatOptions.SearchOptions</c>, whatever its type.
    /// </summary>
    /// <param name="searchOptions">The raw search-option escape hatch.</param>
    /// <exception cref="EdgeChatException">
    /// <see cref="EdgeErrorCode.ChatOptionUnsupported"/> (7108).
    /// </exception>
    /// <remarks>
    /// Checked at <b>load</b> rather than on the first turn: it is the memory cap, the whole gate
    /// depends on the generator getting the value the budget chose, and a typo in the escape hatch
    /// must not survive until a user is waiting for an answer.
    /// </remarks>
    public static void ThrowIfSearchOptionsDeclareMaxLength(IDictionary<string, object>? searchOptions)
    {
        if (searchOptions is null || !searchOptions.TryGetValue(MaxLengthKey, out var value))
        {
            return;
        }

        throw new EdgeChatException(
            EdgeErrorCode.ChatOptionUnsupported,
            $"EdgeChatOptions.SearchOptions declares '{MaxLengthKey}' " +
            $"(as {value?.GetType().Name ?? "null"}), which sub-project 4 owns: it is the KV " +
            "cache's memory cap and it is set from the memory budget's resolved context.")
        {
            Remediation =
                "Set EdgeChatOptions.MaxContextTokens instead - that is the supported way to ask " +
                "for a different context, and it turns the budget from a cap into a named refusal.",
        };
    }

    private static JsonObject? ParseObject(string json, string what)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw Invalid(FormattableString.Invariant($"{what} is not valid JSON: {ex.Message}"), ex);
        }
    }

    private static EdgeChatException Invalid(string message, Exception? inner = null) =>
        new(EdgeErrorCode.ChatConfigurationInvalid, message, inner)
        {
            Remediation =
                "Fix the JSON, or clear EdgeChatOptions.ConfigOverlayJson - the model's own " +
                "genai_config.json is already a complete configuration.",
        };

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject nested && target[key] is JsonObject existing)
            {
                Merge(existing, nested);
                continue;
            }

            target[key] = value?.DeepClone();
        }
    }

    /// <summary>
    /// The first non-CPU provider named anywhere in the overlay, or null.
    /// </summary>
    /// <remarks>
    /// Two spellings are recognised because GenAI accepts both: a <c>provider_options</c> array of
    /// single-key objects, and a <c>providers</c> array of names. Anything else is left to the
    /// native parser, which is the honest boundary - this check exists to turn the two spellings a
    /// consumer actually writes into a named refusal, not to re-implement the config schema.
    /// </remarks>
    private static string? FindNonCpuProvider(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                {
                    if (string.Equals(key, ProviderOptionsKey, StringComparison.Ordinal)
                        && value is JsonArray options)
                    {
                        foreach (var entry in options)
                        {
                            if (entry is JsonObject named)
                            {
                                foreach (var (provider, _) in named)
                                {
                                    if (!IsCpu(provider))
                                    {
                                        return provider;
                                    }
                                }
                            }
                            else if (entry?.GetValueKind() == JsonValueKind.String
                                && entry.GetValue<string>() is { } name
                                && !IsCpu(name))
                            {
                                return name;
                            }
                        }

                        continue;
                    }

                    if (string.Equals(key, ProvidersKey, StringComparison.Ordinal)
                        && value is JsonArray providers)
                    {
                        foreach (var entry in providers)
                        {
                            if (entry?.GetValueKind() == JsonValueKind.String
                                && entry.GetValue<string>() is { } name
                                && !IsCpu(name))
                            {
                                return name;
                            }
                        }

                        continue;
                    }

                    if (FindNonCpuProvider(value) is { } found)
                    {
                        return found;
                    }
                }

                return null;

            case JsonArray a:
                foreach (var entry in a)
                {
                    if (FindNonCpuProvider(entry) is { } found)
                    {
                        return found;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    private static bool IsCpu(string provider) =>
        string.Equals(provider, CpuProvider, StringComparison.OrdinalIgnoreCase);
}
