using Microsoft.Extensions.AI;

namespace Qavren.Edge.Rag;

/// <summary>Which part of the conversation becomes the retrieval query.</summary>
public enum RetrievalQuerySource
{
    /// <summary>The newest user message alone. The default.</summary>
    LastUserMessage,

    /// <summary>Every user message, joined - a broader query for a follow-up that lost its subject.</summary>
    AllUserMessages,
}

/// <summary>Everything the RAG middleware lets a caller change.</summary>
public sealed class RagOptions
{
    /// <summary>How many sources to retrieve. Default 5.</summary>
    public int Top { get; set; } = 5;

    /// <summary>Budget for the retrieved-context block alone, not the whole prompt. Default 1024.</summary>
    public int MaxContextTokens { get; set; } = 1024;

    /// <summary>Per-source clamp, with a visible <c>…(truncated)</c> suffix. Default 1200 characters.</summary>
    public int MaxCharsPerSource { get; set; } = 1200;

    /// <summary>Which messages become the query. Default <see cref="RetrievalQuerySource.LastUserMessage"/>.</summary>
    public RetrievalQuerySource QuerySource { get; set; } = RetrievalQuerySource.LastUserMessage;

    /// <summary>Null uses a stop-worded word split capped at eight terms. Returning empty degrades to pure vector search rather than emitting a malformed FTS MATCH.</summary>
    public Func<string, IReadOnlyCollection<string>>? KeywordExtractor { get; set; }

    /// <summary>
    /// Null uses a chars/4 estimate. Pass the chat model's own counter - reachable as
    /// <c>chatClient.GetService&lt;Tokenizer&gt;()</c> - for exactness against the model that will
    /// actually read the block.
    /// </summary>
    public Func<string, int>? TokenCounter { get; set; }

    /// <summary>The instruction prefix rendered above the numbered sources.</summary>
    public string ContextPrompt { get; set; } = RagPrompts.DefaultContextPrompt;

    /// <summary>The citation instruction rendered below the numbered sources.</summary>
    public string CitationsPrompt { get; set; } = RagPrompts.DefaultCitationsPrompt;

    /// <summary>Replaces both prompts and the whole block layout.</summary>
    public Func<IReadOnlyList<RagSource>, RagOptions, string>? ContextFormatter { get; set; }

    /// <summary>
    /// Default <c>ChatRole.User</c>, and <b>never</b> <c>System</c>: retrieved text is untrusted
    /// input and must not reach the model as instruction. MEAI's own <c>IChatClient</c>
    /// documentation says the application is responsible for prompt injection unless an
    /// implementation documents safeguards; sub-project 4 documents that it has none, and that this
    /// role choice plus the explicit instruction prefix is the whole of its posture.
    /// </summary>
    public ChatRole ContextRole { get; set; } = ChatRole.User;

    /// <summary>Resolve <c>[n]</c> markers into <c>CitationAnnotation</c>s. Default true.</summary>
    public bool EmitCitations { get; set; } = true;

    /// <summary>Publish the source list and the grounded flag on the response. Default true.</summary>
    public bool AttachSourcesToResponse { get; set; } = true;

    /// <summary>
    /// Default true: a retrieval failure is logged and the turn continues <b>ungrounded</b>, marked
    /// as such, rather than failing. That is what Agent Framework's <c>TextSearchProvider</c> does,
    /// and the difference here is that the failure also reaches <c>IEdgeDiagnostics</c>. False
    /// rethrows as <see cref="EdgeErrorCode.RagRetrievalFailed"/>.
    /// </summary>
    public bool ContinueOnRetrievalFailure { get; set; } = true;

    /// <summary>Answer "nothing in the sources matched" without calling the model when retrieval returned nothing. Default true.</summary>
    public bool ShortCircuitOnNoContext { get; set; } = true;

    /// <summary>Return false to skip retrieval for a turn - a greeting, a follow-up that needs no sources.</summary>
    public Func<IEnumerable<ChatMessage>, ChatOptions?, bool>? ShouldRetrieve { get; set; }

    /// <summary>
    /// The <see cref="KeywordExtractor"/> default: split on non-letters, drop a small stop list,
    /// keep three-plus characters, cap at eight, preserve first-seen order and de-duplicate
    /// case-insensitively.
    /// </summary>
    internal static IReadOnlyCollection<string> ExtractKeywords(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var keywords = new List<string>(MaxKeywords);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var range in SplitOnNonLetters(query))
        {
            var term = query[range];
            if (term.Length < 3 || StopWords.Contains(term) || !seen.Add(term))
            {
                continue;
            }

            keywords.Add(term);
            if (keywords.Count == MaxKeywords)
            {
                break;
            }
        }

        return keywords;
    }

    private const int MaxKeywords = 8;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "but", "not", "you", "your", "was", "were", "with", "that",
        "this", "these", "those", "from", "into", "how", "what", "when", "where", "which", "who",
        "why", "can", "does", "did", "has", "have", "had", "its", "it's", "our", "out", "about",
    };

    private static List<Range> SplitOnNonLetters(string query)
    {
        var ranges = new List<Range>();
        var start = -1;

        for (var i = 0; i < query.Length; i++)
        {
            if (char.IsLetter(query[i]))
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else if (start >= 0)
            {
                ranges.Add(new Range(start, i));
                start = -1;
            }
        }

        if (start >= 0)
        {
            ranges.Add(new Range(start, query.Length));
        }

        return ranges;
    }
}

/// <summary>Tuning for the no-LLM extractive floor.</summary>
public sealed class ExtractiveChatOptions
{
    /// <summary>How many of the highest-ranked sources the answer quotes. Default 3.</summary>
    public int MaxSources { get; set; } = 3;

    /// <summary>Per-source clamp for the quoted text. Default 600 characters.</summary>
    public int MaxCharsPerSource { get; set; } = 600;

    /// <summary>What to answer with no sources at all.</summary>
    public string NoResultsAnswer { get; set; } = RagPrompts.DefaultNoContextAnswer;
}
