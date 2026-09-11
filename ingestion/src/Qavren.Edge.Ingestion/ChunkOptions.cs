using System.Globalization;

namespace Qavren.Edge.Ingestion;

/// <summary>Policy for an INPUT unit larger than the budget. Distinct from 6151 (spec 8.2).</summary>
public enum ChunkOverflow
{
    /// <summary>The cascade: fall to the token window. The default.</summary>
    Split,

    /// <summary>Cut to budget and log event 921.</summary>
    Truncate,

    /// <summary>Raise <see cref="EdgeErrorCode.ChunkContextTooLong"/> (6152).</summary>
    Throw,
}

/// <summary>The unresolved chunk budget. <see cref="Resolve"/> freezes it against a tokenizer.</summary>
public sealed class ChunkOptions
{
    /// <summary>Null resolves per spec 8.1. Setting it explicitly opts out of the derivation.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>Null resolves to <c>max(8, (MaxTokens * 15 / 100) / 8 * 8)</c>.</summary>
    public int? OverlapTokens { get; set; }

    /// <summary>Null resolves to <c>max(16, MaxTokens / 8)</c>.</summary>
    public int? MinTokens { get; set; }

    /// <summary>Reserved out of the model ceiling for the rendered breadcrumb.</summary>
    public int HeadingPathTokenBudget { get; set; } = 32;

    /// <summary>H1-H3 by default, so H4+ stay inside their parent section.</summary>
    public IReadOnlyList<int> SplitHeadingLevels { get; set; } = [1, 2, 3];

    /// <summary>Prepend the breadcrumb to the EMBED text. Never to the stored text (spec 8.3).</summary>
    public bool PrependHeadingPath { get; set; } = true;

    /// <summary>Content before the first heading becomes a chunk. The prior art dropped it.</summary>
    public bool IncludePreamble { get; set; } = true;

    /// <summary>A section under <see cref="MinTokens"/> merges forward (event 911).</summary>
    public bool MergeShortSections { get; set; } = true;

    /// <summary>Nudge every cut backwards to a sentence boundary inside a 15% look-back window.</summary>
    public bool SentenceAware { get; set; } = true;

    /// <summary>Re-emit a table's header row on every piece of a row-wise split.</summary>
    public bool RepeatTableHeaderRow { get; set; } = true;

    /// <summary>What to do with an INPUT unit bigger than the budget.</summary>
    public ChunkOverflow Overflow { get; set; } = ChunkOverflow.Split;

    /// <summary>
    /// Needs the tokenizer, so it runs in the order-400 startup task and not at builder time
    /// (spec 8.1). Throws <see cref="EdgeErrorCode.IngestionChunkBudgetInvalid"/> (6003) with the
    /// arithmetic in the message, or <see cref="EdgeErrorCode.ChunkTokenizerCeilingExceeded"/>
    /// (6153) when the tokenizer and the profile disagree about the ceiling.
    /// </summary>
    public ResolvedChunkOptions Resolve(ChunkModelProfile model, IChunkTokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tokenizer);

        if (tokenizer.MaxSequenceLength != model.MaxSequenceLength)
        {
            throw new EdgeChunkingException(
                EdgeErrorCode.ChunkTokenizerCeilingExceeded,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Tokenizer '{0}' reports MaxSequenceLength {1} but model profile '{2}' declares {3}.",
                    tokenizer.Id,
                    tokenizer.MaxSequenceLength,
                    model.Id,
                    model.MaxSequenceLength))
            {
                BudgetTokens = model.MaxSequenceLength,
                RequiredTokens = tokenizer.MaxSequenceLength,
                Remediation = "Register the tokenizer that matches the model profile, or correct the profile's MaxSequenceLength.",
            };
        }

        var overhead = tokenizer.SpecialTokenOverhead;
        var prefixTokens = string.IsNullOrEmpty(model.DocumentPrefix)
            ? 0
            : tokenizer.CountTokens(model.DocumentPrefix.AsSpan());

        var maxTokens = MaxTokens
            ?? (model.MaxSequenceLength - overhead - HeadingPathTokenBudget - prefixTokens);

        // Both rules TRUNCATE; neither rounds to nearest (spec 8.1).
        var overlap = OverlapTokens ?? Math.Max(8, maxTokens * 15 / 100 / 8 * 8);
        var min = MinTokens ?? Math.Max(16, maxTokens / 8);

        if (HeadingPathTokenBudget <= 0)
        {
            throw Invalid($"HeadingPathTokenBudget is {HeadingPathTokenBudget}; it must be positive.");
        }

        if (maxTokens <= 0 || maxTokens + overhead + HeadingPathTokenBudget + prefixTokens > model.MaxSequenceLength)
        {
            throw Invalid(string.Format(
                CultureInfo.InvariantCulture,
                "MaxTokens {0} + SpecialTokenOverhead {1} + HeadingPathTokenBudget {2} + DocumentPrefixTokens {3} = {4}, which must be in (0, {5}].",
                maxTokens,
                overhead,
                HeadingPathTokenBudget,
                prefixTokens,
                maxTokens + overhead + HeadingPathTokenBudget + prefixTokens,
                model.MaxSequenceLength));
        }

        if (overlap < 0 || overlap >= maxTokens / 2)
        {
            throw Invalid(string.Format(
                CultureInfo.InvariantCulture,
                "OverlapTokens {0} must satisfy 0 <= overlap < MaxTokens / 2 = {1}.",
                overlap,
                maxTokens / 2));
        }

        if (min <= 0 || min >= maxTokens)
        {
            throw Invalid(string.Format(
                CultureInfo.InvariantCulture,
                "MinTokens {0} must satisfy 0 < min < MaxTokens = {1}.",
                min,
                maxTokens));
        }

        return new ResolvedChunkOptions(
            maxTokens,
            overlap,
            min,
            HeadingPathTokenBudget,
            overhead,
            prefixTokens,
            [.. SplitHeadingLevels],
            PrependHeadingPath,
            IncludePreamble,
            MergeShortSections,
            SentenceAware,
            RepeatTableHeaderRow,
            Overflow);
    }

    private static EdgeIngestionException Invalid(string message) =>
        new(EdgeErrorCode.IngestionChunkBudgetInvalid, message)
        {
            Remediation = "Correct IngestionOptions.Chunking, or leave the knob null and let spec 8.1 derive it.",
        };
}

/// <summary>The frozen budget. Carried verbatim by the recipe, so every field is in the hash.</summary>
public sealed record ResolvedChunkOptions(
    int MaxTokens,
    int OverlapTokens,
    int MinTokens,
    int HeadingPathTokenBudget,
    int SpecialTokenOverhead,
    int DocumentPrefixTokens,
    IReadOnlyList<int> SplitHeadingLevels,
    bool PrependHeadingPath,
    bool IncludePreamble,
    bool MergeShortSections,
    bool SentenceAware,
    bool RepeatTableHeaderRow,
    ChunkOverflow Overflow);
