using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Qavren.Edge.Rag;

/// <summary>The two property keys the pipeline talks over, the pin key, and the marker resolver.</summary>
public static partial class RagCitations
{
    /// <summary>
    /// Carries <c>IReadOnlyList&lt;RagSource&gt;</c>, ranked, clamped and <c>Ordinal</c>-stamped.
    /// <para>
    /// <b>It is used on both sides of the call, and that is the specified channel by which an
    /// inner client receives the structured sources.</b> Before invoking the inner client,
    /// <c>RagChatClient</c> clones the caller's <c>ChatOptions</c> and sets this key on the
    /// clone's <c>AdditionalProperties</c>; after the turn it sets the same key on the
    /// <c>ChatResponse</c>. An inner client that wants the sources reads the request-side entry
    /// and never re-parses the rendered text block, which exists for the <i>model</i> to read.
    /// <c>ExtractiveChatClient</c> is the one in-box client that does so.
    /// </para>
    /// <para>
    /// The clone matters: MEAI's <c>IChatClient</c> contract permits an implementation to mutate
    /// the <c>ChatOptions</c> it is handed, so consumers must not share one across concurrent
    /// calls - which means this client must not write into the caller's instance either.
    /// <c>ChatOptions.Clone()</c> is public and shallow-copies <c>AdditionalProperties</c> into a
    /// new dictionary, which is exactly the isolation needed.
    /// </para>
    /// <para>
    /// A leaf that does not know this key ignores it: <c>EdgeChatClient</c> forwards
    /// <c>AdditionalProperties</c> to <c>GeneratorParams.SetSearchOption</c> only for
    /// <c>bool</c> and <c>double</c> values and skips everything else, so a <c>RagSource</c> list
    /// on the request is inert rather than a failure. A third-party leaf that inspected every
    /// property would see a documented, publicly-typed value.
    /// </para>
    /// </summary>
    public const string SourcesPropertyKey = "qavren.edge.rag.sources";

    /// <summary>Response-side only. Whether the answer was produced from retrieved sources.</summary>
    public const string GroundedPropertyKey = "qavren.edge.rag.grounded";

    /// <summary>
    /// Set to <c>true</c> on the <c>AdditionalProperties</c> of the retrieved-context
    /// <c>ChatMessage</c> this middleware injects, marking it <b>pinned</b>: a history reducer must
    /// never evict it.
    /// <para>
    /// It exists because the injected block breaks the shape a turn-based reducer assumes. The
    /// block is a second <c>ChatRole.User</c> message sitting immediately before the real user
    /// message, so the tail of the list is user-then-user rather than the user/assistant pairs
    /// <c>EdgeChatTokenBudgetReducer</c> evicts in - and evicting the grounding the whole recipe
    /// exists to supply, to make room for history, would be the worst possible trade.
    /// </para>
    /// <para>
    /// <b>The literal is duplicated in <c>Qavren.Edge.Chat.Onnx</c> on purpose.</b>
    /// <c>Qavren.Edge.Rag</c> does not reference that package and must not, so the two cannot share
    /// a constant; the chat package's <c>ChatHistoryOptions.PinnedMessageKeys</c> defaults to this
    /// exact string and a tier-1 test asserts the two literals are equal. That is the same
    /// deliberate duplication, with the same asserted-equality guard, that sub-project 2 already
    /// uses for <c>EdgeVectorData.QueryGeneratorServiceKey</c> and
    /// <c>EdgeEmbeddings.QueryServiceKey</c>. A reducer that does not know the key simply sees an
    /// ordinary user message - it degrades to the old behaviour rather than breaking.
    /// </para>
    /// </summary>
    public const string ContextMessagePropertyKey = "qavren.edge.rag.context";

    /// <summary>
    /// Finds <c>[n]</c> markers in a completed answer and builds one
    /// <c>CitationAnnotation { Title, Url, FileId, Snippet }</c> per distinct marker, each carrying
    /// a <c>TextSpanAnnotatedRegion(StartIndex, EndIndex)</c> per occurrence. A marker with no
    /// matching source is left as plain text, counted, and <b>never</b> fabricated into a citation.
    /// A total function: it does not throw.
    /// </summary>
    /// <param name="answer">
    /// The <b>complete</b> answer text, and the string every returned <c>StartIndex</c> and
    /// <c>EndIndex</c> indexes. On the streaming path that is the concatenation of every text delta
    /// in the response; after aggregation it is <c>ChatResponse.Text</c>. They are the same string,
    /// which is why one set of offsets serves both.
    /// </param>
    /// <param name="sources">
    /// The ranked, clamped, <c>Ordinal</c>-stamped list the block was rendered from. A marker is
    /// matched against <c>RagSource.Ordinal</c>, never against list position, so an unstamped list
    /// resolves nothing rather than citing the wrong chunk.
    /// </param>
    /// <remarks>
    /// <c>EndIndex</c> is exclusive: <c>answer[StartIndex..EndIndex]</c> is exactly the marker text.
    /// Both are always set, so a consumer's <c>EndIndex - StartIndex</c> needs one null check and no
    /// fallback. A marker inside a code fence is treated like any other marker - the scan is
    /// lexical, and that is documented behaviour rather than an oversight.
    /// </remarks>
    public static IReadOnlyList<CitationAnnotation> Build(string answer, IReadOnlyList<RagSource> sources) =>
        Build(answer, sources, out _);

    /// <summary>
    /// <see cref="Build(string, IReadOnlyList{RagSource})"/>, also handing back how many marker
    /// occurrences resolved to no source - the number <c>RagChatClient</c> logs as event 965.
    /// </summary>
    internal static IReadOnlyList<CitationAnnotation> Build(
        string answer, IReadOnlyList<RagSource> sources, out int unresolvedMarkers)
    {
        unresolvedMarkers = 0;

        if (string.IsNullOrEmpty(answer) || sources is null || sources.Count == 0)
        {
            return [];
        }

        Dictionary<int, CitationAnnotation>? byOrdinal = null;
        List<CitationAnnotation>? ordered = null;

        foreach (var match in MarkerRegex().EnumerateMatches(answer))
        {
            var digits = answer.AsSpan(match.Index + 1, match.Length - 2);
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
            {
                // A run of digits too long for an int is not an ordinal anybody stamped.
                unresolvedMarkers++;
                continue;
            }

            byOrdinal ??= [];
            if (!byOrdinal.TryGetValue(ordinal, out var annotation))
            {
                var source = FindByOrdinal(sources, ordinal);
                if (source is null)
                {
                    unresolvedMarkers++;
                    continue;
                }

                annotation = new CitationAnnotation
                {
                    Title = source.Title,
                    Url = source.Uri,
                    FileId = source.Id,
                    Snippet = source.Text,
                };

                byOrdinal[ordinal] = annotation;
                ordered ??= [];
                ordered.Add(annotation);
            }

            annotation.AnnotatedRegions ??= [];
            annotation.AnnotatedRegions.Add(new TextSpanAnnotatedRegion
            {
                StartIndex = match.Index,
                EndIndex = match.Index + match.Length,
            });
        }

        return (IReadOnlyList<CitationAnnotation>?)ordered ?? [];
    }

    /// <summary>
    /// Attaches <paramref name="citations"/> to <paramref name="update"/> by appending one
    /// <c>TextContent(string.Empty)</c> whose <c>Annotations</c> hold them. The carrier is spelled
    /// out as a public helper because an <c>AIAnnotation</c> hangs off an <c>AIContent</c> and the
    /// final update has no text content of its own to hang them on - and because a consumer
    /// writing their own middleware should attach them the same way.
    /// </summary>
    /// <remarks>
    /// The carrier is empty text on purpose: it contributes nothing to <c>ChatResponse.Text</c>, so
    /// the offsets the citations carry still index the same string. An empty
    /// <paramref name="citations"/> list attaches nothing rather than an empty carrier.
    /// </remarks>
    public static void AttachTo(ChatResponseUpdate update, IReadOnlyList<CitationAnnotation> citations)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(citations);

        if (citations.Count == 0)
        {
            return;
        }

        var carrier = new TextContent(string.Empty) { Annotations = [.. citations] };
        update.Contents.Add(carrier);
    }

    private static RagSource? FindByOrdinal(IReadOnlyList<RagSource> sources, int ordinal)
    {
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i].Ordinal == ordinal)
            {
                return sources[i];
            }
        }

        return null;
    }

    [GeneratedRegex(@"\[(\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerRegex();
}
