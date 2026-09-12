namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// Spec 9.2's <b>per-document</b> recipe, composed. <see cref="IngestionRecipe.ExtractorFingerprint"/>
/// is "the SELECTED extractor, <c>pdf:1</c> — not the registry", and spec 7.1 states plainly that
/// <see cref="IDocumentExtractorRegistry.Describe"/> is the DIAGNOSTICS string and not the recipe:
/// hashing the whole set would re-index an entire Markdown corpus because the PDF extractor's
/// version was bumped.
/// <para>
/// The selected extractor is not known until a document is in hand, so what the order-400 startup
/// task freezes on the registration is the extractor-INDEPENDENT baseline, carrying
/// <see cref="Baseline"/> in that one field. Every per-document recipe is that baseline with the
/// selected extractor's fingerprint substituted, and it is the per-document hash that is written to
/// and compared against <c>recipe_hash</c>. The baseline's own hash is what
/// <c>IngestionRunResult.RecipeHash</c>, <c>IngestionStatus.RecipeHash</c> and
/// <c>GetRecipeAsync</c> report, because it is the one recipe identity that does not depend on
/// which file happened to be next.
/// </para>
/// </summary>
internal static class RecipeComposition
{
    /// <summary>The baseline's stand-in fingerprint. Not a valid <c>"{Id}:{Version}"</c>, deliberately.</summary>
    public const string Baseline = "*";

    /// <summary>The fingerprint of a document no extractor accepted, so 6101 is its own recipe.</summary>
    public const string NoExtractor = "(none)";

    /// <summary>Spec 9.2's <c>"{Id}:{Version}"</c>.</summary>
    public static string Fingerprint(IDocumentExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        return $"{extractor.Id}:{extractor.Version}";
    }

    /// <summary>
    /// The baseline with one extractor's fingerprint substituted. A <c>with</c> expression would
    /// copy <see cref="IngestionRecipe"/>'s memoised hash field along with everything else, so the
    /// record is rebuilt member by member instead.
    /// </summary>
    public static IngestionRecipe WithExtractor(IngestionRecipe baseline, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);

        return new IngestionRecipe(
            baseline.SchemaVersion,
            baseline.ModelProfileId,
            baseline.Dimensions,
            baseline.Pooling,
            baseline.DocumentPrefix,
            baseline.QueryPrefix,
            baseline.TokenizerId,
            baseline.TokenizerMaxSequenceLength,
            baseline.SpecialTokenOverhead,
            baseline.ChunkerId,
            baseline.ChunkerVersion,
            baseline.Chunking,
            fingerprint,
            baseline.DistanceFunction);
    }

    /// <summary>
    /// Every recipe hash a document could legitimately carry right now: one per registered
    /// extractor, plus <see cref="NoExtractor"/>. <c>recipeStaleDocuments</c> counts the rows that
    /// are in NONE of them — which is the population a recipe bump dirtied, and which stays correct
    /// now that the fingerprint is per-document rather than per-registry.
    /// </summary>
    public static IReadOnlyList<ContentHash> CurrentHashes(
        IngestionRecipe baseline, IDocumentExtractorRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(registry);

        var hashes = new List<ContentHash>(registry.Extractors.Count + 1)
        {
            WithExtractor(baseline, NoExtractor).HashValue,
        };

        foreach (var extractor in registry.Extractors)
        {
            hashes.Add(WithExtractor(baseline, Fingerprint(extractor)).HashValue);
        }

        return hashes;
    }
}
