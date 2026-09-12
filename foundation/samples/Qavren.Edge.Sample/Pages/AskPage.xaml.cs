using System.Text;
using Microsoft.Extensions.AI;
using Qavren.Edge.Chat;
using Qavren.Edge.Rag;

namespace Qavren.Edge.Sample.Pages;

/// <summary>
/// SP4 spec 18's Ask page: the RAG recipe over the Search page's notes. Question in, streamed
/// grounded answer out, the numbered sources listed beside it, and each <c>[n]</c> in the answer
/// resolving to its <see cref="CitationAnnotation"/>. A toggle swaps <see cref="EdgeChatClient"/>
/// for <see cref="ExtractiveChatClient"/> IN PLACE, so a reader can watch the no-LLM floor answer
/// the same question through the same <c>UseRag()</c> pipeline.
/// </summary>
/// <remarks>
/// Spec 4.3's rule, followed literally: citations arrive ONCE, on the final update, and their
/// spans index the ACCUMULATED answer, not any single update - so this page accumulates first and
/// indexes its own buffer. <c>TextSpanAnnotatedRegion.StartIndex</c>/<c>EndIndex</c> are
/// <c>int?</c> on the shipped MEAI surface (plan adjustment 9); <c>RagCitations.Build</c> always
/// sets both, and the null check below is what makes the slice compile against the type rather
/// than a guarantee the type does not carry.
/// </remarks>
public partial class AskPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly IChatClient _modelPipeline;
    private IChatClient? _extractivePipeline;
    private bool _busy;

    public AskPage(IServiceProvider services, IChatClient chat)
    {
        InitializeComponent();
        _services = services;

        // MauiProgram registered this as AddOnnxChat(..., pipeline: chat => chat.UseRag()), so it
        // is already RagChatClient over EdgeChatClient over the model.
        _modelPipeline = chat;
    }

    /// <summary>
    /// The floor: the same <c>UseRag()</c> over <see cref="ExtractiveChatClient"/>, built from the
    /// app's own provider so it resolves the same <see cref="IEdgeRetriever"/> the model path
    /// uses. Exactly what <c>AddExtractiveChat(pipeline: chat => chat.UseRag())</c> registers for
    /// an app that ships no weights at all (spec 4.3) - constructed by hand here only because the
    /// sample keeps both clients alive to swap between them.
    /// </summary>
    private IChatClient ExtractivePipeline =>
        _extractivePipeline ??= new ChatClientBuilder(new ExtractiveChatClient())
            .UseRag()
            .Build(_services);

    private async void OnAsk(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var question = string.IsNullOrWhiteSpace(QuestionEntry.Text)
            ? "what happened after the storm?"
            : QuestionEntry.Text.Trim();

        var extractive = ExtractiveSwitch.IsToggled;
        var client = extractive ? ExtractivePipeline : _modelPipeline;

        _busy = true;
        AskButton.IsEnabled = false;
        StatusLabel.Text = extractive ? "Asking the extractive floor..." : "Asking the model...";
        AnswerLabel.FormattedText = null;
        AnswerLabel.Text = "";
        CitationDetailLabel.Text = "Tap a [n] marker in the answer.";
        CitationList.Clear();
        SourceList.Clear();

        var answer = new StringBuilder();
        var citations = new List<CitationAnnotation>();
        IReadOnlyList<RagSource>? sources = null;
        bool? grounded = null;
        ChatTurnStatus? status = null;

        try
        {
            await foreach (var update in client.GetStreamingResponseAsync(question))
            {
                answer.Append(update.Text);
                AnswerLabel.Text = answer.ToString();

                citations.AddRange(update.Contents
                    .SelectMany(c => c.Annotations ?? [])
                    .OfType<CitationAnnotation>());

                if (update.AdditionalProperties is { } props)
                {
                    if (props.TryGetValue(RagCitations.SourcesPropertyKey, out var s)
                        && s is IReadOnlyList<RagSource> list)
                    {
                        sources = list;
                    }

                    if (props.TryGetValue(RagCitations.GroundedPropertyKey, out var g) && g is bool flag)
                    {
                        grounded = flag;
                    }
                }

                status = update.GetTurnStatus() ?? status;
            }

            var text = answer.ToString();
            RenderAnswer(text, citations);
            RenderCitations(text, citations);
            RenderSources(sources);

            var via = extractive ? "ExtractiveChatClient (no model)" : "EdgeChatClient";
            var line = $"Answered via {via} | grounded: {(grounded is { } gr ? gr : "unknown")} | " +
                       $"{citations.Count} citation(s) over {sources?.Count ?? 0} source(s)";
            if (status is not null)
            {
                line += $" | stop: {status.StopReason} | {status.TokensPerSecond:F1} tok/s | " +
                        $"first token {status.TimeToFirstToken.TotalMilliseconds:F0} ms | context {status.ContextTokens}";
            }

            StatusLabel.Text = line;
        }
        catch (EdgeException ex)
        {
            // ChatModelNotProvisioned (7051) is the one a reader hits first: the Chat tab's
            // consent sheet is where the download lives. Flip the toggle to see the floor answer
            // with no model at all.
            AnswerLabel.Text = ex is EdgeChatException { Remediation: { } remediation }
                ? $"{ex.Code} ({(int)ex.Code}): {ex.Message}\n\nRemediation: {remediation}"
                : $"{ex.Code} ({(int)ex.Code}): {ex.Message}";
            StatusLabel.Text = extractive
                ? "The extractive floor failed."
                : "The model path failed - provision the model on the Chat tab, or toggle the extractive floor.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AnswerLabel.Text = $"{ex.GetType().Name}: {ex.Message}";
            StatusLabel.Text = "Failed.";
        }
        finally
        {
            _busy = false;
            AskButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// The answer with every <c>[n]</c> marker rendered as a tappable span that resolves to its
    /// citation - the spans are sliced from the accumulated answer at the offsets the annotation
    /// carries, which is the whole point of the spec 4.3 accumulate-then-index rule.
    /// </summary>
    private void RenderAnswer(string text, List<CitationAnnotation> citations)
    {
        var regions = new List<(int Start, int End, CitationAnnotation Citation)>();
        foreach (var citation in citations)
        {
            foreach (var region in (citation.AnnotatedRegions ?? []).OfType<TextSpanAnnotatedRegion>())
            {
                if (region.StartIndex is not { } start || region.EndIndex is not { } end)
                {
                    continue;
                }

                if (start >= 0 && end <= text.Length && start < end)
                {
                    regions.Add((start, end, citation));
                }
            }
        }

        if (regions.Count == 0)
        {
            AnswerLabel.Text = text;
            return;
        }

        regions.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        var formatted = new FormattedString();
        var cursor = 0;
        foreach (var (start, end, citation) in regions)
        {
            if (start < cursor)
            {
                continue; // overlapping regions cannot happen from RagCitations.Build; skip rather than throw
            }

            if (start > cursor)
            {
                formatted.Spans.Add(new Span { Text = text[cursor..start] });
            }

            var marker = new Span
            {
                Text = text[start..end],
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#512BD4"),
            };

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => CitationDetailLabel.Text = DescribeCitation(text[start..end], citation);
            marker.GestureRecognizers.Add(tap);
            formatted.Spans.Add(marker);

            cursor = end;
        }

        if (cursor < text.Length)
        {
            formatted.Spans.Add(new Span { Text = text[cursor..] });
        }

        AnswerLabel.FormattedText = formatted;
    }

    private void RenderCitations(string text, List<CitationAnnotation> citations)
    {
        if (citations.Count == 0)
        {
            CitationList.Add(new Label { Text = "(none - the answer carried no [n] marker that resolved to a source)", FontSize = 12 });
            return;
        }

        foreach (var citation in citations)
        {
            var spans = (citation.AnnotatedRegions ?? []).OfType<TextSpanAnnotatedRegion>().ToArray();
            var marker = spans.Length > 0
                && spans[0].StartIndex is { } s
                && spans[0].EndIndex is { } e
                && s >= 0 && e <= text.Length && s < e
                    ? text[s..e]
                    : "[?]";

            CitationList.Add(new Label
            {
                Text = $"{marker} {citation.Title ?? citation.FileId} - cited {spans.Length} time(s) at " +
                       string.Join(", ", spans.Select(r => $"{r.StartIndex}..{r.EndIndex}")),
                FontSize = 12,
                LineBreakMode = LineBreakMode.WordWrap,
            });
        }
    }

    private void RenderSources(IReadOnlyList<RagSource>? sources)
    {
        if (sources is null || sources.Count == 0)
        {
            SourceList.Add(new Label { Text = "(none - nothing was retrieved; seed notes on the Search tab)", FontSize = 12 });
            return;
        }

        foreach (var source in sources.OrderBy(s => s.Ordinal))
        {
            SourceList.Add(new Label
            {
                Text = $"[{source.Ordinal}] {source.Title ?? source.Id} " +
                       $"({source.ScoreKind} {source.Score:F4}{(source.Uri is { } uri ? $", {uri}" : "")})\n{source.Text}",
                FontSize = 12,
                LineBreakMode = LineBreakMode.WordWrap,
            });
        }
    }

    private static string DescribeCitation(string marker, CitationAnnotation citation)
    {
        var sb = new StringBuilder();
        sb.Append(marker).Append(' ').Append(citation.Title ?? citation.FileId ?? "(untitled)");
        if (citation.Url is { } url)
        {
            sb.Append(" - ").Append(url);
        }

        if (citation.Snippet is { Length: > 0 } snippet)
        {
            sb.Append('\n').Append(snippet);
        }

        return sb.ToString();
    }
}
