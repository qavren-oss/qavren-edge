using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Sqlite;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// Spec 12's key list, minus <c>lastFtsMergePages</c> and <c>lastFtsMergeMs</c> (plan adjustment 2).
/// The five count keys are emitted only when <c>IncludeCountsInDiagnostics</c> is on; when off they
/// are present with the value <c>"(disabled)"</c>, <b>never omitted</b>, so nobody reads a missing
/// key as zero.
/// </summary>
public sealed class DiagnosticsContributorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Spec 12 line 2301: five keys, these five, and no others count.</summary>
    private static readonly string[] CountKeys =
    [
        "documents", "chunks", "failedDocuments", "staleDocuments", "recipeStaleDocuments",
    ];

    /// <summary>
    /// Spec 12's list transcribed, minus the two merge keys plan adjustment 2 removed. Transcribed
    /// rather than summarised on purpose: a renamed key is a broken consumer, and the rename is
    /// invisible to every other test in this suite.
    /// </summary>
    private static readonly string[] SpecKeys =
    [
        "collection", "database", "store", "dimensions", "distanceFunction", "fullTextIndexed",
        "vectorTable", "fullTextTable", "tokensEmbeddedSource", "recipeHash", "modelProfileId",
        "documentPrefixTokens", "chunkerId", "maxTokens", "overlapTokens", "minTokens",
        "headingPathTokenBudget", "tokenizerId", "tokenizerMaxSequenceLength", "specialTokenOverhead",
        "extractors", "throttle", "lastThrottleReason", "writeBatchSize", "effectiveWriteBatchSize",
        "maxDocumentBytes", "stateSchemaVersion", "hashAlgorithm", "activeRunId", "activeRunStage",
        "activeRunDocumentsCompleted", "lastRunEmbedCalls", "lastRunTokensEmbedded", "lastRunId",
        "lastRunOutcome", "lastRunSuspendReason", "lastRunUtc", "lastRunDurationMs",
        "lastRunDocumentsIndexed", "lastRunDocumentsSkipped", "lastRunDocumentsFailed",
        "lastRunChunksAdded", "lastRunChunksRemoved",
        .. CountKeys,
    ];

    [Fact]
    public async Task The_contributor_appears_in_the_report_with_its_component_name()
    {
        using var host = await IngestionTestHost.StartAsync();

        var report = host.Services.GetRequiredService<IEdgeDiagnostics>().Report();

        Assert.Contains(report.Components, c => c.Name == "Qavren.Edge.Ingestion");
    }

    [Fact]
    public async Task Every_key_spec_12_names_is_present_before_any_run_and_none_is_invented()
    {
        using var host = await IngestionTestHost.StartAsync();

        var details = Describe(host);

        foreach (var key in SpecKeys)
        {
            Assert.True(details.ContainsKey(key), $"spec 12 names '{key}' and the report omits it");
        }

        // The two keys plan adjustment 2 removed, and no key outside the list.
        Assert.DoesNotContain("lastFtsMergePages", details.Keys);
        Assert.DoesNotContain("lastFtsMergeMs", details.Keys);
        Assert.Empty(details.Keys.Except(SpecKeys, StringComparer.Ordinal));
    }

    [Fact]
    public async Task With_counts_off_every_count_key_is_present_and_reads_disabled()
    {
        using var host = await IngestionTestHost.StartAsync();
        await host.Pipeline.RunAsync(new RecordingSource().Add("a.txt", "alpha"), cancellationToken: Token);

        var details = Describe(host);

        foreach (var key in CountKeys)
        {
            Assert.True(details.ContainsKey(key), $"key '{key}' was omitted rather than disabled");
            Assert.Equal("(disabled)", details[key]);
        }
    }

    [Fact]
    public async Task With_counts_on_the_keys_carry_real_numbers()
    {
        using var host = await IngestionTestHost.StartAsync(o => o.IncludeCountsInDiagnostics = true);
        await host.Pipeline.RunAsync(
            new RecordingSource().Add("a.txt", "alpha").Add("b.txt", "beta"), cancellationToken: Token);

        var details = Describe(host);

        Assert.Equal("2", details["documents"]);
        Assert.Equal("0", details["failedDocuments"]);
        Assert.Equal("2", details["chunks"]);
        Assert.Equal("0", details["staleDocuments"]);
        Assert.Equal("0", details["recipeStaleDocuments"]);
    }

    /// <summary>
    /// Spec 12's scheduling rule is <c>recipeStaleDocuments == 0 &amp;&amp; staleDocuments == 0
    /// &amp;&amp; lastRunOutcome == Completed</c>. Two of those three terms are keys nothing else
    /// emits, so this asserts the one that moves: a recipe bump dirties the corpus and the key says
    /// so, through diagnostics, without a run.
    /// </summary>
    [Fact]
    public async Task RecipeStaleDocuments_counts_the_population_a_recipe_bump_dirtied()
    {
        using var host = await IngestionTestHost.StartAsync(o => o.IncludeCountsInDiagnostics = true);
        await host.Pipeline.RunAsync(
            new RecordingSource().Add("a.txt", "alpha").Add("b.txt", "beta"), cancellationToken: Token);

        Assert.Equal("0", Describe(host)["recipeStaleDocuments"]);

        var connection = await host.Database.OpenConnectionAsync(Token).ConfigureAwait(true);
        await using (connection.ConfigureAwait(true))
        {
            await connection.ExecuteAsync(
                """UPDATE "qedge_ingest_document" SET "recipe_hash" = $h""",
                [new SqliteParameter("$h", ContentHash.OfText("a recipe this build never produces").ToBlob())],
                Token).ConfigureAwait(true);
        }

        var details = Describe(host);
        Assert.Equal("2", details["recipeStaleDocuments"]);
        Assert.Equal("Completed", details["lastRunOutcome"]);
        Assert.Equal("0", details["staleDocuments"]);
    }

    [Fact]
    public async Task The_recipe_and_the_frozen_budget_are_reported()
    {
        using var host = await IngestionTestHost.StartAsync();
        var details = Describe(host);

        Assert.Equal((await host.Pipeline.GetRecipeAsync(Token)).Hash, details["recipeHash"]);
        Assert.Equal("222", details["maxTokens"]);
        Assert.Equal("32", details["overlapTokens"]);
        Assert.Equal("fake-whitespace", details["tokenizerId"]);
        Assert.Equal("256", details["tokenizerMaxSequenceLength"]);
        Assert.Equal("chunks", details["collection"]);
        Assert.Equal("all-minilm-l6-v2-int8", details["modelProfileId"]);
        Assert.Equal("auto", details["chunkerId"]);
        Assert.Equal("xxh128-v1", details["hashAlgorithm"]);
        Assert.Equal("1", details["stateSchemaVersion"]);
        Assert.Equal("fixed", details["throttle"]);
        Assert.Equal("32", details["effectiveWriteBatchSize"]);
        Assert.Contains("text:1", details["extractors"] ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("chunks_vec", details["vectorTable"]);
        Assert.Equal("chunks_fts", details["fullTextTable"]);
    }

    /// <summary>
    /// The <c>lastRun*</c> block is what a scheduler reads to decide whether to submit another
    /// background task, so it has to survive the run rather than be reachable only through the
    /// <see cref="IngestionRunResult"/> the caller already threw away.
    /// </summary>
    [Fact]
    public async Task The_last_runs_counters_are_reported_after_it_finishes()
    {
        using var host = await IngestionTestHost.StartAsync();

        Assert.Equal("(none)", Describe(host)["activeRunId"]);
        Assert.Null(Describe(host)["lastRunId"]);

        var result = await host.Pipeline.RunAsync(
            new RecordingSource().Add("a.txt", "alpha").Add("b.txt", "beta"), cancellationToken: Token);

        var details = Describe(host);

        Assert.Equal(result.RunId, details["lastRunId"]);
        Assert.Equal("Completed", details["lastRunOutcome"]);
        Assert.Null(details["lastRunSuspendReason"]);
        Assert.Equal("2", details["lastRunDocumentsIndexed"]);
        Assert.Equal("0", details["lastRunDocumentsSkipped"]);
        Assert.Equal("0", details["lastRunDocumentsFailed"]);
        Assert.Equal("2", details["lastRunChunksAdded"]);
        Assert.Equal("0", details["lastRunChunksRemoved"]);
        Assert.Equal(
            result.EmbedCalls.ToString(System.Globalization.CultureInfo.InvariantCulture),
            details["lastRunEmbedCalls"]);
        Assert.Equal(
            result.TokensEmbedded.ToString(System.Globalization.CultureInfo.InvariantCulture),
            details["lastRunTokensEmbedded"]);
        Assert.NotNull(details["lastRunUtc"]);
        Assert.NotNull(details["lastRunDurationMs"]);

        // The run is over, so there is no active one to report.
        Assert.Equal("(none)", details["activeRunId"]);
        Assert.Equal("(none)", details["activeRunStage"]);
        Assert.Equal("(none)", details["activeRunDocumentsCompleted"]);
    }

    /// <summary>
    /// Spec 9.5: the key says WHICH source is in force — the generator's published
    /// <c>Usage.InputTokenCount</c> or SP3's own <c>CountTokens</c> sum. A constant here would make
    /// the reader guess the very thing the key exists to settle.
    /// </summary>
    [Fact]
    public async Task TokensEmbeddedSource_names_the_source_actually_used()
    {
        using var host = await IngestionTestHost.StartAsync();

        // Before any window has been embedded there is no fact to report.
        Assert.Equal("(none yet)", Describe(host)["tokensEmbeddedSource"]);

        await host.Pipeline.RunAsync(new RecordingSource().Add("a.txt", "alpha"), cancellationToken: Token);

        // The test host's generator publishes no Usage, so the count came from CountTokens.
        Assert.Equal("counted", Describe(host)["tokensEmbeddedSource"]);
    }

    [Fact]
    public async Task TokensEmbeddedSource_reads_usage_when_the_generator_publishes_one()
    {
        var generator = new RecordingEmbeddingGenerator(publishUsage: true);
        using var host = await IngestionTestHost.StartAsync(configureGenerator: generator);

        await host.Pipeline.RunAsync(new RecordingSource().Add("a.txt", "alpha"), cancellationToken: Token);

        Assert.Equal("usage", Describe(host)["tokensEmbeddedSource"]);
    }

    private static IReadOnlyDictionary<string, string?> Describe(IngestionTestHost host) =>
        host.Services.GetServices<IEdgeDiagnosticsContributor>()
            .First(c => c.ComponentName == "Qavren.Edge.Ingestion")
            .Describe();
}
