// MEVD9001: Microsoft.Extensions.VectorData.ProviderServices is marked experimental. This is the
// ONE file in Qavren.Edge.Ingestion that names a type from it - CollectionModel, obtained through
// EdgeCollectionModelBuilder.BuildDynamic - and it does so to build the CHECK side of error 6011's
// DDL comparison. A project-wide NoWarn would hide a future, real use; a file-scoped suppression
// with the reason on it does not.
#pragma warning disable MEVD9001

using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Qavren.Edge.VectorData;
using Qavren.Edge.VectorData.Internal;
using MEVD = Microsoft.Extensions.VectorData;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// Spec 11.1 step 5: one <see cref="IEdgeStartupTask"/> at order 400 that <b>touches no database</b>.
/// The tokenizer is a vocabulary parse, the generator is resolved but not called, and both sides of
/// the DDL comparison are pure string construction.
/// </summary>
internal sealed class IngestionValidateStartupTask(IServiceProvider services, IngestionRegistry registry)
    : IEdgeStartupTask
{
    /// <inheritdoc />
    public int Order => EdgeIngestionStartupOrder.Validate;

    /// <inheritdoc />
    public Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var registration in registry.All)
        {
            Validate(registration);
        }

        return Task.CompletedTask;
    }

    private void Validate(IngestionRegistration registration)
    {
        var options = registration.Options;

        // 1. The tokenizer. There is deliberately NO chars/4 fallback - that is the prior art's
        //    hidden-truncation bug.
        var tokenizer = services.GetService<IChunkTokenizer>()
            ?? throw IngestionServiceResolution.TokenizerMissing();

        // 2. The generator SP3 itself calls in spec 9.5 step a1. Resolved, never invoked.
        var generator = IngestionServiceResolution.FindGenerator(services, options.StoreName)
            ?? throw IngestionServiceResolution.GeneratorMissing(options.StoreName);

        // 3. The frozen budget. 6003 / 6153 land here, not on the first document.
        var chunking = options.Chunking.Resolve(options.Model, tokenizer);

        // 4. The declared width, when the generator publishes one.
        var dimensions = options.Dimensions ?? options.Model.Dimensions;
        if (generator.GetService(typeof(EmbeddingGeneratorMetadata)) is EmbeddingGeneratorMetadata metadata
            && metadata.DefaultModelDimensions is { } declared
            && declared != dimensions)
        {
            throw new EdgeConfigurationException(
                EdgeErrorCode.IngestionCollectionDimensionMismatch,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The embedding generator declares {0} dimensions; collection '{1}' is declared {2}. The " +
                    "alternative to this check is a collection holding vectors from two spaces.",
                    declared,
                    options.CollectionName,
                    dimensions));
        }

        // 5. The 6011 DDL comparison. Both sides are pure string construction, so this costs
        //    microseconds and runs on every start.
        CompareDdl(registration);

        // Composed the same way the pipeline composes it - from DI plus options.Extractors - so the
        // recipe's extractor set and the run's extractor set can never disagree. Resolving it here
        // also surfaces a duplicate extractor id (6004) at START time rather than on the first
        // document.
        _ = IngestionServiceResolution.BuildRegistry(services, options);

        registration.Tokenizer = tokenizer;
        registration.Chunking = chunking;
        registration.Recipe = BuildRecipe(registration, tokenizer, chunking, dimensions);
    }

    private void CompareDdl(IngestionRegistration registration)
    {
        var options = registration.Options;

        var checkSide = new EdgeVectorSchema(
            new EdgeCollectionModelBuilder(options.CollectionName)
                .BuildDynamic(registration.Definition, IngestionSchemaOnlyGenerator.Instance),
            options.CollectionName,
            CollectionShapeProjection.ToStoreOptions(
                registration.CollectionOptions, options.FullTextRemoveDiacritics),
            registration.CollectionOptions.AlwaysCreateFullTextIndex);

        // Spec 12's vectorTable / fullTextTable keys. Taken from the CHECK side, which is pure
        // string construction and exists whether or not an SP2 store is registered; when one is,
        // the comparison below proves the two sides agree, so there is no second truth to pick.
        registration.DataTable = checkSide.DataTable;
        registration.VectorTable = checkSide.VectorTable;
        registration.FullTextTable = checkSide.FullTextTable;

        var expected = checkSide.BuildCreateSql();

        var store = options.StoreName is { } storeName
            ? services.GetKeyedService<MEVD.VectorStore>(storeName)
            : services.GetService<MEVD.VectorStore>();

        if (store is not EdgeVectorStore edge)
        {
            // No SP2 store is registered yet, or it is not the Edge one. Nothing to compare
            // against, and the pipeline will fail loudly the first time it needs one.
            return;
        }

        var collection = edge.GetDynamicCollection(options.CollectionName, registration.Definition);
        if (collection.GetService(typeof(EdgeVectorSchema)) is not EdgeVectorSchema runtime)
        {
            return;
        }

        var actual = runtime.BuildCreateSql();
        for (var i = 0; i < Math.Max(expected.Count, actual.Count); i++)
        {
            var left = i < expected.Count ? expected[i] : "(absent)";
            var right = i < actual.Count ? actual[i] : "(absent)";
            if (string.Equals(left, right, StringComparison.Ordinal))
            {
                continue;
            }

            throw new EdgeConfigurationException(
                EdgeErrorCode.IngestionCollectionSchemaMismatch,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The DDL AddIngestion registered for collection '{0}' differs from the DDL the runtime store " +
                    "would emit.{4}Migration: {1}{4}Runtime:   {2}{4}Remediation: {3}",
                    options.CollectionName,
                    left,
                    right,
                    "pass the same shaping values to AddIngestion's ConfigureCollection that you passed to " +
                    "AddVectorStore, and set IngestionOptions.FullTextRemoveDiacritics to match.",
                    Environment.NewLine));
        }
    }

    private static IngestionRecipe BuildRecipe(
        IngestionRegistration registration,
        IChunkTokenizer tokenizer,
        ResolvedChunkOptions chunking,
        int dimensions)
    {
        var options = registration.Options;

        return new IngestionRecipe(
            IngestionRecipe.CurrentSchemaVersion,
            options.Model.Id,
            dimensions,
            options.Model.Pooling,
            options.Model.DocumentPrefix,
            options.Model.QueryPrefix,
            tokenizer.Id,
            tokenizer.MaxSequenceLength,
            tokenizer.SpecialTokenOverhead,
            options.ChunkerId,
            ChunkerRecipeVersion(options.ChunkerId),
            chunking,
            // Spec 9.2: the SELECTED extractor, never the registry. The selection is per document,
            // so what is frozen here is the extractor-INDEPENDENT baseline and the runner
            // substitutes the real fingerprint once an extractor is chosen (RecipeComposition).
            RecipeComposition.Baseline,
            options.DistanceFunction);
    }

    /// <summary>
    /// The chunker half of the recipe. <c>ChunkerIds.Auto</c> can select either built-in chunker
    /// per document, so its recipe version composes both: bumping either one dirties the corpus,
    /// which is the behaviour a version exists to produce.
    /// </summary>
    private static int ChunkerRecipeVersion(string chunkerId) => chunkerId switch
    {
        ChunkerIds.Plain => ChunkerFactory.Resolve(ChunkerIds.Plain, null).Version,
        ChunkerIds.TokenWindow => ChunkerFactory.Resolve(ChunkerIds.TokenWindow, null).Version,
        ChunkerIds.MarkdownHeading => ChunkerFactory
            .Resolve(ChunkerIds.MarkdownHeading, IngestionMediaTypes.Markdown).Version,
        _ => (ChunkerFactory.Resolve(ChunkerIds.Plain, null).Version * 1000)
            + ChunkerFactory.Resolve(ChunkerIds.MarkdownHeading, IngestionMediaTypes.Markdown).Version,
    };
}
