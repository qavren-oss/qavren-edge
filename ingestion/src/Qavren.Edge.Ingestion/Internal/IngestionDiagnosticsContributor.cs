using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Sqlite;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// Spec 12's key list, minus <c>lastFtsMergePages</c> and <c>lastFtsMergeMs</c> (plan adjustment 2:
/// SP3's collection is registered through SP2's own <c>AddVectorCollectionMigration</c>, so SP2's
/// bounded FTS5 merge already covers this sidecar and SP3 runs none of its own).
/// <para>
/// Every key spec 12 names is emitted on every report, with a null or a placeholder value when the
/// fact is not yet established — an omitted key reads as a zero, and a zero here is a claim.
/// </para>
/// <para>
/// <see cref="Describe"/> is synchronous and cannot await, so the five count keys are synchronous
/// counting queries. They are emitted <b>only</b> when
/// <see cref="IngestionOptions.IncludeCountsInDiagnostics"/> is on; when off they are present with
/// the value <c>"(disabled)"</c>, <b>never omitted</b>. Nothing else in this report opens a
/// connection: the run-scoped keys are read off the registration the runner stamps.
/// </para>
/// </summary>
internal sealed class IngestionDiagnosticsContributor(
    IServiceProvider services, IngestionRegistry registry, IngestionRunControl control)
    : IEdgeDiagnosticsContributor
{
    private const string Disabled = "(disabled)";
    private const string Default = "(default)";
    private const string None = "(none)";
    private const string NoneYet = "(none yet)";

    private static readonly FixedIngestionThrottle FallbackThrottle = new();

    /// <inheritdoc />
    public string ComponentName => "Qavren.Edge.Ingestion";

    /// <inheritdoc />
    public string? ComponentVersion =>
        typeof(IngestionDiagnosticsContributor).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string?> Describe()
    {
        var details = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var registration in registry.All)
        {
            // One collection is the overwhelming case and its keys are spec 12's verbatim. A second
            // AddIngestion prefixes every key with its collection name rather than letting the two
            // registrations overwrite each other's readings.
            var prefix = registry.All.Count == 1 ? string.Empty : registration.CollectionName + ".";
            DescribeCollection(details, prefix, registration);
        }

        return details;
    }

    private void DescribeCollection(
        Dictionary<string, string?> details, string prefix, IngestionRegistration registration)
    {
        var options = registration.Options;
        var chunking = registration.Chunking;
        var tokenizer = registration.Tokenizer;

        // ---- The collection's shape.
        details[prefix + "collection"] = options.CollectionName;
        details[prefix + "database"] = options.DatabaseName ?? Default;
        details[prefix + "store"] = options.StoreName ?? Default;
        details[prefix + "dimensions"] = Number(options.Dimensions ?? options.Model.Dimensions);
        details[prefix + "distanceFunction"] = options.DistanceFunction;
        details[prefix + "fullTextIndexed"] = options.FullTextIndexed.ToString(CultureInfo.InvariantCulture);
        details[prefix + "vectorTable"] = registration.VectorTable;
        details[prefix + "fullTextTable"] = registration.FullTextTable ?? None;

        // ---- The recipe and everything that composes it.
        // Spec 9.5 / spec 12: tokensEmbeddedSource says WHICH source is in force - the generator's
        // published Usage.InputTokenCount, or SP3's own CountTokens sum. A constant here would make
        // the reader guess the very thing the key exists to settle.
        details[prefix + "tokensEmbeddedSource"] = registration.TokensEmbeddedSource ?? NoneYet;
        details[prefix + "recipeHash"] = registration.Recipe?.Hash;
        details[prefix + "modelProfileId"] = options.Model.Id;
        details[prefix + "documentPrefixTokens"] = Number(chunking?.DocumentPrefixTokens);
        details[prefix + "chunkerId"] = options.ChunkerId;
        details[prefix + "maxTokens"] = Number(chunking?.MaxTokens);
        details[prefix + "overlapTokens"] = Number(chunking?.OverlapTokens);
        details[prefix + "minTokens"] = Number(chunking?.MinTokens);
        details[prefix + "headingPathTokenBudget"] = Number(chunking?.HeadingPathTokenBudget);
        details[prefix + "tokenizerId"] = tokenizer?.Id;
        details[prefix + "tokenizerMaxSequenceLength"] = Number(tokenizer?.MaxSequenceLength);
        details[prefix + "specialTokenOverhead"] = Number(chunking?.SpecialTokenOverhead);
        details[prefix + "extractors"] = Extractors(options);

        // ---- The write path's knobs, as they stand right now.
        details[prefix + "throttle"] = (services.GetService<IIngestionThrottle>() ?? FallbackThrottle).Name;
        details[prefix + "lastThrottleReason"] = registration.LastThrottleReason ?? NoneYet;
        details[prefix + "writeBatchSize"] = Number(options.WriteBatchSize);
        details[prefix + "effectiveWriteBatchSize"] = Number(control.ApplyShrink(options.WriteBatchSize));
        details[prefix + "maxDocumentBytes"] = options.MaxDocumentBytes.ToString(CultureInfo.InvariantCulture);
        details[prefix + "stateSchemaVersion"] = Number(IngestionSchema.StateSchemaVersion);
        details[prefix + "hashAlgorithm"] = ContentHash.AlgorithmId;

        // ---- The run in flight.
        var active = registration.ActiveProgress;
        details[prefix + "activeRunId"] = active?.RunId ?? None;
        details[prefix + "activeRunStage"] = active?.Stage.ToString() ?? None;

        // "Completed" is every document the run is finished with, however it finished: indexed,
        // skipped on spec 9.4's gates, or recorded Failed. Counting only the indexed ones would
        // make a run over an unchanged corpus look stalled at zero.
        details[prefix + "activeRunDocumentsCompleted"] = active is { } progress
            ? Number(progress.DocumentsIndexed + progress.DocumentsSkipped + progress.DocumentsFailed)
            : None;

        // ---- The last run that finished in this process.
        var last = registration.LastRun;
        details[prefix + "lastRunEmbedCalls"] = Number(last?.EmbedCalls);
        details[prefix + "lastRunTokensEmbedded"] = last?.TokensEmbedded.ToString(CultureInfo.InvariantCulture);
        details[prefix + "lastRunId"] = last?.RunId;
        details[prefix + "lastRunOutcome"] = last?.Outcome.ToString();
        details[prefix + "lastRunSuspendReason"] = last?.SuspendReason;
        details[prefix + "lastRunUtc"] = last?.FinishedUtc.ToString("O", CultureInfo.InvariantCulture);
        details[prefix + "lastRunDurationMs"] = last is { } finished
            ? ((long)finished.Duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
            : null;
        details[prefix + "lastRunDocumentsIndexed"] = Number(last?.DocumentsIndexed);
        details[prefix + "lastRunDocumentsSkipped"] = Number(last?.DocumentsSkipped);
        details[prefix + "lastRunDocumentsFailed"] = Number(last?.DocumentsFailed);
        details[prefix + "lastRunChunksAdded"] = Number(last?.ChunksAdded);
        details[prefix + "lastRunChunksRemoved"] = Number(last?.ChunksRemoved);

        // ---- The five counting queries, off by default.
        var counted = options.IncludeCountsInDiagnostics ? Count(registration) : null;
        details[prefix + "documents"] = Number(counted?.Documents) ?? Disabled;
        details[prefix + "chunks"] = counted?.Chunks.ToString(CultureInfo.InvariantCulture) ?? Disabled;
        details[prefix + "failedDocuments"] = Number(counted?.Failed) ?? Disabled;
        details[prefix + "staleDocuments"] = Number(counted?.Stale) ?? Disabled;

        // Spec 12's scheduling rule reads recipeStaleDocuments == 0 && staleDocuments == 0 &&
        // lastRunOutcome == Completed as "nothing known is outstanding". Two of those three terms
        // are keys nobody else emits, so dropping this one would make the rule unreadable.
        details[prefix + "recipeStaleDocuments"] = Number(counted?.RecipeStale) ?? Disabled;
    }

    private string Extractors(IngestionOptions options)
    {
        try
        {
            return IngestionServiceResolution.BuildRegistry(services, options).Describe();
        }
        catch (EdgeException)
        {
            // A duplicate extractor id is 6004 at startup. Diagnostics reports the fault's
            // existence rather than throwing out of a report someone is reading to find it.
            return "(unavailable)";
        }
    }

    private CountedState? Count(IngestionRegistration registration)
    {
        try
        {
            var database = registration.Options.DatabaseName is { } name
                ? services.GetRequiredKeyedService<IEdgeDatabase>(name)
                : services.GetRequiredService<IEdgeDatabase>();

            var store = new IngestionStateStore(database, registration.Options.StateTablePrefix);
            var recipes = registration.Recipe is { } baseline
                ? RecipeComposition.CurrentHashes(
                    baseline, IngestionServiceResolution.BuildRegistry(services, registration.Options))
                : [];
            var latest = store.LatestRunAsync(registration.CollectionName, CancellationToken.None)
                .GetAwaiter().GetResult();
            var counts = store.CountAsync(
                registration.CollectionName, recipes, latest?.RunId, CancellationToken.None)
                .GetAwaiter().GetResult();
            var chunks = store.ChunkCountAsync(
                registration.DataTable ?? registration.CollectionName, CancellationToken.None)
                .GetAwaiter().GetResult();

            return new CountedState(counts.Documents, counts.Failed, counts.Stale, counts.RecipeStale, chunks);
        }
        catch (EdgeException)
        {
            // Diagnostics never fails a report: an un-migrated or absent state table reports
            // "(disabled)" rather than throwing out of Describe().
            return null;
        }
    }

    private static string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private sealed record CountedState(int Documents, int Failed, int Stale, int RecipeStale, long Chunks);
}
