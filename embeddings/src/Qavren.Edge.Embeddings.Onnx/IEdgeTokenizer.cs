using System.Buffers;

namespace Qavren.Edge.Embeddings.Onnx;

/// <summary>Which tokenizer family a preset's tokenizer asset belongs to.</summary>
public enum EdgeTokenizerKind
{
    /// <summary>A WordPiece <c>vocab.txt</c>, which is every preset SP2 ships.</summary>
    WordPieceVocabTxt = 0,

    /// <summary>
    /// A Unigram <c>tokenizer.json</c>. Present so the multilingual shape is ready when the
    /// library is; raises <see cref="EdgeErrorCode.TokenizerKindUnsupported"/> today (ADR 0006).
    /// </summary>
    UnigramTokenizerJson = 1,
}

/// <summary>How a batch's <c>last_hidden_state</c> collapses into one vector per input.</summary>
public enum EmbeddingPooling
{
    /// <summary>The masked mean over real tokens.</summary>
    Mean = 0,

    /// <summary>Row zero, the <c>[CLS]</c> position.</summary>
    Cls = 1,
}

/// <summary>Which of a preset's two prefixes an instance applies.</summary>
public enum EmbeddingInputKind
{
    /// <summary>The document side. What the unkeyed generator is.</summary>
    Document = 0,

    /// <summary>The query side, reached through <see cref="EdgeEmbeddings.QueryServiceKey"/>.</summary>
    Query = 1,
}

/// <summary>What happens to an input longer than the preset's maximum sequence length.</summary>
public enum EmbeddingTruncation
{
    /// <summary>Truncate and log <c>EmbeddingInputTruncated</c> (701). The default.</summary>
    Truncate = 0,

    /// <summary>Raise <see cref="EdgeErrorCode.EmbeddingInputTooLong"/> (5105).</summary>
    Throw = 1,
}

/// <summary>
/// One padded batch, row-major, ready to wrap in OrtValues.
/// <para>
/// <b>The three <c>long[]</c> are RENTED from <see cref="ArrayPool{T}"/>.<c>Shared</c> by
/// <see cref="IEdgeTokenizer.EncodeBatch"/>, so each one may be LONGER than
/// <see cref="TensorLength"/>.</b> Spec 11 pools them because an ingest batch would otherwise
/// allocate <c>3 * BatchSize * SequenceLength</c> longs per batch on the one path that runs once
/// per document. Every layout contract below is therefore over the FIRST
/// <see cref="TensorLength"/> elements: <c>array.AsMemory(0, TensorLength)</c> is what reaches
/// <c>OrtValue.CreateTensorValueFromMemory</c>, and <c>array.Length</c> carries no meaning.
/// Anything past that window is whatever the previous renter left behind.
/// </para>
/// </summary>
/// <param name="InputIds">
/// Row-major and flat over its first <c>BatchSize * SequenceLength</c> elements: row <i>b</i>
/// occupies <c>[b * SequenceLength, (b+1) * SequenceLength)</c>. That is exactly the layout
/// <c>OrtValue.CreateTensorValueFromMemory</c> expects, so no reshape happens anywhere.
/// <para>
/// <b>The array may be LONGER than <c>BatchSize * SequenceLength</c>.</b> The three buffers a
/// batch from <see cref="IEdgeTokenizer.EncodeBatch"/> carries are rented from
/// <see cref="ArrayPool{T}"/>.<c>Shared</c>, which returns a buffer AT LEAST the requested size.
/// Read <c>BatchSize * SequenceLength</c> elements, never <c>InputIds.Length</c>. Spec 7's
/// declaration of this record annotates the three arrays <c>[BatchSize * SequenceLength]</c>,
/// which describes the WINDOW rather than the allocation; that sentence needs the edit, and the
/// pooling it describes away is mandated by the plan's Step 6.
/// </para>
/// </param>
/// <param name="AttentionMask">
/// 1 for a real token, 0 for padding, same layout and window as <paramref name="InputIds"/>. This
/// is the mask this library SYNTHESISES - never anything <c>Microsoft.ML.Tokenizers</c> returned.
/// There is no attention-mask API in that library, and <c>GetSpecialTokensMask</c> is not one: on
/// its default path it ignores the ids and emits <c>1, 0...0, 1</c>, and on the other it marks
/// special tokens. Either way it is the inverse of what ONNX wants.
/// </param>
/// <param name="TokenTypeIds">All zeros for a single-sequence encoder; same layout and window.</param>
/// <param name="BatchSize">How many inputs this batch carries.</param>
/// <param name="SequenceLength">The padded width every row was padded to.</param>
/// <param name="TokenCounts">
/// Per input, pre-padding, INCLUDING <c>[CLS]</c> and <c>[SEP]</c>. The only source for
/// <c>GeneratedEmbeddings.Usage</c>'s token count: summing <paramref name="AttentionMask"/> gives
/// the same number today and would silently stop doing so the moment anything masked a
/// non-padding position.
/// </param>
/// <param name="Truncated">
/// Per input. Raises <see cref="EdgeErrorCode.EmbeddingInputTooLong"/> (5105) when
/// <see cref="OnnxEmbeddingOptions.Truncation"/> is <see cref="EmbeddingTruncation.Throw"/>, and
/// logs <c>EmbeddingInputTruncated</c> (701) when it is <see cref="EmbeddingTruncation.Truncate"/>
/// - which is the default, so the out-of-the-box behaviour on an over-long input is a logged
/// truncation, never an exception.
/// </param>
#pragma warning disable CA1819 // These three long[] ARE the tensor buffers; copying them per batch is the cost this record exists to avoid.
public sealed record TokenizedBatch(
    long[] InputIds,
    long[] AttentionMask,
    long[] TokenTypeIds,
    int BatchSize,
    int SequenceLength,
    int[] TokenCounts,
    bool[] Truncated)
{
    private int _returned;

    /// <summary>
    /// <c>BatchSize * SequenceLength</c>: how many elements of each of the three buffers carry this
    /// batch. Read this, never <c>InputIds.Length</c> - the buffers are rented and may be longer.
    /// </summary>
    /// <remarks>
    /// INTERNAL rather than public. Spec 7 declares this record with seven members and no eighth,
    /// and the value is derivable from two of them, so exposing it would be an additive public
    /// surface the spec does not carry for no capability a caller lacks. The test assembly sees it
    /// through <c>InternalsVisibleTo</c>.
    /// </remarks>
    internal int TensorLength => BatchSize * SequenceLength;

    /// <summary>
    /// True when the three buffers came from <see cref="ArrayPool{T}"/>.<c>Shared</c> and must be
    /// handed back. Internal because renting is an implementation detail of
    /// <see cref="IEdgeTokenizer.EncodeBatch"/>: a batch a consumer builds by hand owns its own
    /// arrays and is never returned to a pool it did not come from.
    /// </summary>
    internal bool Pooled { get; init; }

    /// <summary>
    /// Returns the three buffers to <see cref="ArrayPool{T}"/>.<c>Shared</c>. Idempotent, and a
    /// no-op when <see cref="Pooled"/> is false. The caller must have disposed every
    /// <c>OrtValue</c> built over them first: <c>CreateTensorValueFromMemory</c> pins the managed
    /// memory for the value's lifetime.
    /// </summary>
    internal void ReturnBuffers()
    {
        if (!Pooled || Interlocked.Exchange(ref _returned, 1) != 0)
        {
            return;
        }

        ArrayPool<long>.Shared.Return(InputIds);
        ArrayPool<long>.Shared.Return(AttentionMask);
        ArrayPool<long>.Shared.Return(TokenTypeIds);
    }
}
#pragma warning restore CA1819

/// <summary>
/// The one abstraction over <c>Microsoft.ML.Tokenizers</c> SP2 exposes. Implementations hold the
/// CONCRETE tokenizer type, never the <c>Tokenizer</c> base: <c>BertTokenizer.EncodeToIds</c> is
/// declared <c>new</c>, so a base-typed field silently drops <c>[CLS]</c> and <c>[SEP]</c>.
/// </summary>
public interface IEdgeTokenizer : IDisposable
{
    /// <summary>Which tokenizer family this instance is.</summary>
    EdgeTokenizerKind Kind { get; }

    /// <summary>How many entries the vocabulary holds.</summary>
    int VocabularySize { get; }

    /// <summary>The longest sequence this tokenizer will emit, special tokens included.</summary>
    int MaxSequenceLength { get; }

    /// <summary>The id padded positions carry.</summary>
    int PadTokenId { get; }

    /// <summary>
    /// Single encode into a caller-owned buffer. Writes ids into <paramref name="destination"/>,
    /// adding the special tokens and truncating so the total never exceeds
    /// <paramref name="maxTokens"/>. Returns the id count; throws
    /// <see cref="ArgumentException"/> when <paramref name="destination"/> is shorter than
    /// <paramref name="maxTokens"/>.
    /// <para>
    /// <b>This is NOT allocation-free on the pinned tokenizer version, and the contract must not
    /// claim otherwise.</b> Every <c>EncodeToIds</c> overload in <c>Microsoft.ML.Tokenizers</c>
    /// 2.0.0 returns a list; the only span-destination members in the library -
    /// <c>BuildInputsWithSpecialTokens</c>, <c>GetSpecialTokensMask</c>,
    /// <c>CreateTokenTypeIdsFromSequences</c> - all take ids that already exist. The
    /// implementation therefore calls the truncating <c>EncodeToIds</c> overload and copies the
    /// result into <paramref name="destination"/>: one short-lived list per input, and no
    /// allocation in the batch assembler, which is where the buffers that actually matter live.
    /// The span signature is kept because it is the shape the assembler wants, because it keeps
    /// the per-input allocation an implementation detail rather than a public one, and because
    /// Tokenizers 3.x may add a span overload this method can then adopt without a surface change.
    /// </para>
    /// </summary>
    /// <param name="text">The text to encode.</param>
    /// <param name="maxTokens">The id ceiling, special tokens included.</param>
    /// <param name="destination">Where the ids are written. At least <paramref name="maxTokens"/> long.</param>
    /// <param name="charsConsumed">How many characters of the normalized text were consumed.</param>
    /// <returns>The number of ids written.</returns>
    int Encode(ReadOnlySpan<char> text, int maxTokens, Span<int> destination, out int charsConsumed);

    /// <summary>
    /// Encodes, truncates, right-pads to the smallest configured bucket that fits the batch
    /// maximum, and synthesises the attention mask.
    /// <para>
    /// The returned batch's three <c>long[]</c> are rented from
    /// <see cref="ArrayPool{T}"/>.<c>Shared</c> and are valid over their first
    /// <see cref="TokenizedBatch.TensorLength"/> elements only. The generator hands them back once
    /// the <c>OrtValue</c>s built over them are disposed; a caller outside this package simply
    /// drops the batch and the GC reclaims the buffers without them ever re-entering the pool.
    /// </para>
    /// </summary>
    /// <param name="texts">The inputs, already prefixed by the caller.</param>
    /// <param name="maxSequenceLength">The preset's ceiling.</param>
    /// <param name="buckets">The preset's sequence buckets, ascending.</param>
    /// <returns>One padded batch.</returns>
    TokenizedBatch EncodeBatch(IReadOnlyList<string> texts, int maxSequenceLength, IReadOnlyList<int> buckets);

    /// <summary>Counts the tokens in <paramref name="text"/>.</summary>
    /// <param name="text">The text to count.</param>
    /// <returns>The token count.</returns>
    int CountTokens(ReadOnlySpan<char> text);

    /// <summary>
    /// Index into <paramref name="text"/> at which <paramref name="maxTokens"/> tokens are
    /// consumed. Exists so SP3's token-window chunker never writes a second token counter.
    /// </summary>
    /// <param name="text">The text to measure.</param>
    /// <param name="maxTokens">The token ceiling.</param>
    /// <param name="tokenCount">How many tokens were actually consumed.</param>
    /// <returns>The character index.</returns>
    int IndexByTokenCount(string text, int maxTokens, out int tokenCount);
}

/// <summary>WordPiece construction settings. Every default here changes behaviour.</summary>
/// <remarks>
/// The four special-token strings are the <c>bert-base-uncased</c> spellings and are looked up in
/// the vocabulary BY STRING to get their ids. All four shipped presets share the identical
/// 30,522-entry vocab, so all four resolve to the same four ids. Getting one wrong does not throw:
/// an <c>[UNK]</c>-token name absent from the vocab resolves to no id and the tokenizer emits a
/// sequence with no unknown marker, which is a silent quality bug of exactly the class every
/// <c>required</c> field on <see cref="EmbeddingPreset"/> exists to prevent.
/// </remarks>
public sealed class WordPieceTokenizerOptions
{
    /// <summary>
    /// Lower-case before tokenization. True by default and OVERRIDDEN PER PRESET from
    /// <see cref="EmbeddingPreset.LowerCase"/>, never left at the default: a cased model fed
    /// lower-cased text produces plausible, wrong vectors.
    /// </summary>
    public bool LowerCase { get; set; } = true;

    /// <summary>The longest sequence this tokenizer will emit, special tokens included.</summary>
    public int MaxSequenceLength { get; set; } = 512;

    /// <summary>The unknown-token spelling.</summary>
    public string UnknownToken { get; set; } = "[UNK]";

    /// <summary>The padding-token spelling. Padded positions carry its id.</summary>
    public string PaddingToken { get; set; } = "[PAD]";

    /// <summary>The classification-token spelling, prepended to every sequence.</summary>
    public string ClassificationToken { get; set; } = "[CLS]";

    /// <summary>The separator-token spelling, appended to every sequence.</summary>
    public string SeparatorToken { get; set; } = "[SEP]";

    /// <summary>
    /// Strip non-spacing marks. False by default, which matches <c>bert-base-uncased</c>: it
    /// strips accents through its own normaliser rather than this flag.
    /// </summary>
    public bool RemoveNonSpacingMarks { get; set; }
}

/// <summary>
/// Builds the tokenizer on first use and caches it for the process. A provider rather than a
/// directly-injected <see cref="IEdgeTokenizer"/>, because the vocab file is
/// <c>OnnxModelFileRole.Vocabulary</c> inside the same manifest as the graph, and spec 10 keeps
/// provisioning LAZY: on a first launch there is no vocab path at the moment DI constructs the
/// generator, and DI factories cannot await a download. So the generator takes this, and the first
/// <c>GenerateAsync</c> awaits it - the same call that already awaits
/// <c>IOnnxSessionHost.AcquireAsync</c>, which provisions the same manifest. Construction parses
/// the whole vocab, so it happens exactly once PER PRESET, behind a <c>SemaphoreSlim(1)</c>, and
/// each built instance is reused for the process lifetime. Per preset, not once overall: the keyed
/// <c>AddOnnxEmbeddings(name, ...)</c> overload exists so two presets can coexist in one process,
/// and they differ in <see cref="EmbeddingPreset.LowerCase"/> and
/// <see cref="EmbeddingPreset.MaxSequenceLength"/> - handing one preset's tokenizer to the other is
/// a silent quality bug, never an exception.
/// </summary>
public interface IEdgeTokenizerProvider
{
    /// <summary>Provisions the vocabulary if needed and returns the cached tokenizer.</summary>
    /// <param name="preset">The preset whose manifest carries the vocabulary.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The process-wide tokenizer for this preset.</returns>
    ValueTask<IEdgeTokenizer> GetAsync(EmbeddingPreset preset, CancellationToken cancellationToken = default);

    /// <summary>
    /// Null until the first <see cref="GetAsync"/> completes, and the MOST RECENTLY BUILT tokenizer
    /// once more than one preset is registered. Diagnostics read a tokenizer rather than forcing
    /// provisioning to report a vocab size.
    /// </summary>
    IEdgeTokenizer? Current { get; }
}
