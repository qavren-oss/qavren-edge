using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace Qavren.Edge.Ingestion;

/// <summary>
/// The tuple a chunk's vector is a pure function of (spec 9.2). When a document's stored
/// <c>recipe_hash</c> differs from the run's, that document is dirty regardless of its content
/// hash — which is what stops a <c>MaxTokens</c> change from leaving every old vector in place.
/// </summary>
/// <param name="SchemaVersion">The recipe's own shape version. Bump when a field is added.</param>
/// <param name="ModelProfileId">SP2's lower-case preset id, verbatim.</param>
/// <param name="Dimensions">The collection's declared width.</param>
/// <param name="Pooling">The model's pooling mode.</param>
/// <param name="DocumentPrefix">A token RESERVE and a recipe input only; SP3 never writes it.</param>
/// <param name="QueryPrefix">The query-side prefix, for completeness.</param>
/// <param name="TokenizerId">The tokenizer's vocabulary identity.</param>
/// <param name="TokenizerMaxSequenceLength">The tokenizer's ceiling.</param>
/// <param name="SpecialTokenOverhead">Tokens the encoder adds that <c>CountTokens</c> omits.</param>
/// <param name="ChunkerId">The selected chunker.</param>
/// <param name="ChunkerVersion">Its version.</param>
/// <param name="Chunking">The frozen budget, field by field.</param>
/// <param name="ExtractorFingerprint">
/// The SELECTED extractor's <c>"{Id}:{Version}"</c>, never the registry's: hashing the set would
/// re-index an entire Markdown corpus because the PDF extractor's version was bumped. Because the
/// selection is per document, the recipe a run freezes at startup carries the stand-in <c>"*"</c>
/// in this one field, and the recipe actually written to and compared against <c>recipe_hash</c> is
/// that baseline with the chosen extractor's fingerprint substituted. The baseline is what
/// <c>GetRecipeAsync</c>, <c>IngestionRunResult.RecipeHash</c> and <c>IngestionStatus.RecipeHash</c>
/// report, because it is the one recipe identity that does not depend on which file was next.
/// </param>
/// <param name="DistanceFunction">The collection's distance function.</param>
public sealed record IngestionRecipe(
    int SchemaVersion,
    string ModelProfileId,
    int Dimensions,
    string Pooling,
    string? DocumentPrefix,
    string? QueryPrefix,
    string TokenizerId,
    int TokenizerMaxSequenceLength,
    int SpecialTokenOverhead,
    string ChunkerId,
    int ChunkerVersion,
    ResolvedChunkOptions Chunking,
    string ExtractorFingerprint,
    string DistanceFunction)
{
    /// <summary>The version this build writes.</summary>
    public const int CurrentSchemaVersion = 1;

    private string? _hash;

    /// <summary>Thirty-two lowercase hex, 0x1F-separated and order-stable.</summary>
    public string Hash => _hash ??= Compute();

    /// <summary>The hash as a <see cref="ContentHash"/>, which is the persisted form.</summary>
    public ContentHash HashValue =>
        ContentHash.TryParseHex(Hash, out var parsed) ? parsed : ContentHash.Zero;

    private string Compute()
    {
        var hasher = new XxHash128();
        var separator = new[] { ContentHash.Separator };

        void Field(string value)
        {
            hasher.Append(Encoding.UTF8.GetBytes(value));
            hasher.Append(separator);
        }

        void Number(int value) => Field(value.ToString(CultureInfo.InvariantCulture));
        void Flag(bool value) => Field(value ? "1" : "0");

        Number(SchemaVersion);
        Field(ModelProfileId);
        Number(Dimensions);
        Field(Pooling);
        Field(DocumentPrefix ?? "\0");
        Field(QueryPrefix ?? "\0");
        Field(TokenizerId);
        Number(TokenizerMaxSequenceLength);
        Number(SpecialTokenOverhead);
        Field(ChunkerId);
        Number(ChunkerVersion);

        Number(Chunking.MaxTokens);
        Number(Chunking.OverlapTokens);
        Number(Chunking.MinTokens);
        Number(Chunking.HeadingPathTokenBudget);
        Number(Chunking.SpecialTokenOverhead);
        Number(Chunking.DocumentPrefixTokens);
        Field(string.Join(',', Chunking.SplitHeadingLevels));
        Flag(Chunking.PrependHeadingPath);
        Flag(Chunking.IncludePreamble);
        Flag(Chunking.MergeShortSections);
        Flag(Chunking.SentenceAware);
        Flag(Chunking.RepeatTableHeaderRow);
        Field(Chunking.Overflow.ToString());

        Field(ExtractorFingerprint);
        Field(DistanceFunction);

        return new ContentHash(hasher.GetCurrentHashAsUInt128()).ToHex();
    }
}
