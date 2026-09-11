using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// Error 6009, both branches. Which branch a consumer is on is not a detail: a run that throws and
/// a run that silently re-embeds a whole corpus are the two most different outcomes this package
/// has, and both are reachable from one boolean.
/// </summary>
public sealed class RecipeDriftTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_recipe_hash_is_thirty_two_lowercase_hex_and_is_reported_on_every_run()
    {
        using var host = await IngestionTestHost.StartAsync();
        var recipe = await host.Pipeline.GetRecipeAsync(Token);

        Assert.Equal(32, recipe.Hash.Length);
        Assert.Equal(recipe.Hash, recipe.Hash.ToLowerInvariant());

        var result = await host.Pipeline.RunAsync(
            new RecordingSource().Add("a.txt", "alpha"), cancellationToken: Token);
        Assert.Equal(recipe.Hash, result.RecipeHash);
    }

    [Fact]
    public async Task StrictRecipe_throws_6009_on_the_first_drifted_document_before_any_embed_or_write()
    {
        using var host = await IngestionTestHost.StartAsync(o => o.StrictRecipe = true);
        var source = new RecordingSource().Add("a.txt", "alpha").Add("b.txt", "beta");

        await host.Pipeline.RunAsync(source, cancellationToken: Token);
        var stored = await ForceRecipeDriftAsync(host);

        var callsBefore = host.Generator.CallCount;
        var chunksBefore = (await host.Pipeline.GetStatusAsync(ct: Token)).ChunkCount;

        var error = await Assert.ThrowsAsync<EdgeIngestionException>(
            () => host.Pipeline.RunAsync(source, cancellationToken: Token));

        Assert.Equal(EdgeErrorCode.IngestionRecipeChanged, error.Code);
        Assert.Contains(stored, error.Message, StringComparison.Ordinal);

        // BOTH hashes, 32 hex each. The current one is the DOCUMENT's recipe - the baseline with
        // the selected extractor's fingerprint substituted (spec 9.2) - not the baseline itself, so
        // the assertion is on shape and difference rather than on GetRecipeAsync's value.
        var hashes = System.Text.RegularExpressions.Regex
            .Matches(error.Message, "[0-9a-f]{32}")
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(2, hashes.Count);
        Assert.Contains(stored, hashes);

        // No embed and no write happened.
        Assert.Equal(callsBefore, host.Generator.CallCount);
        Assert.Equal(chunksBefore, (await host.Pipeline.GetStatusAsync(ct: Token)).ChunkCount);
    }

    [Fact]
    public async Task The_default_re_indexes_every_drifted_document_and_logs_915_exactly_once()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("a.txt", "alpha").Add("b.txt", "beta");

        await host.Pipeline.RunAsync(source, cancellationToken: Token);
        var stored = await ForceRecipeDriftAsync(host);

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.DocumentsIndexed);

        var drift = host.Logs.Lines.Where(l => l.StartsWith("915|", StringComparison.Ordinal)).ToList();
        Assert.Single(drift);
        Assert.Contains(stored, drift[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_recipe_drift_leaves_the_stale_chunks_replaced_rather_than_duplicated()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("a.txt", "alpha");

        await host.Pipeline.RunAsync(source, cancellationToken: Token);
        await ForceRecipeDriftAsync(host);
        await host.Pipeline.RunAsync(source, cancellationToken: Token);

        var status = await host.Pipeline.GetStatusAsync(ct: Token);
        Assert.Equal(1, status.ChunkCount);
    }

    /// <summary>
    /// Spec 9.2: "Because the extractor fingerprint is per-document rather than per-registry,
    /// bumping <c>PdfTextExtractor.Version</c> dirties PDFs and leaves a Markdown corpus alone."
    /// Registering a second extractor for a format nothing in the corpus uses must therefore
    /// re-index nothing — which is exactly what hashing <c>Registry.Describe()</c> got wrong.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public async Task Registering_or_bumping_an_extractor_for_another_format_re_indexes_nothing(int widgetVersion)
    {
        var root = Path.Combine(Path.GetTempPath(), "qedge-ingestion-tests", Guid.NewGuid().ToString("N"));
        var source = new RecordingSource().Add("a.txt", "alpha").Add("b.txt", "beta");

        try
        {
            using (var first = await IngestionTestHost.StartAsync(root: root))
            {
                var run = await first.Pipeline.RunAsync(source, cancellationToken: Token);
                Assert.Equal(2, run.DocumentsIndexed);
            }

            // Same corpus, same everything, plus one extractor claiming a format the corpus does not
            // contain. If the recipe hashed the registry's SET, both text documents would be dirty
            // and both would be re-embedded - which is the whole reason spec 9.2 pins the
            // fingerprint to the SELECTED extractor.
            using var second = await IngestionTestHost.StartAsync(
                configure: edge => edge.AddDocumentExtractor(new WidgetExtractor(version: widgetVersion)),
                root: root);

            var result = await second.Pipeline.RunAsync(source, cancellationToken: Token);

            Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
            Assert.Equal(2, result.DocumentsSkipped);
            Assert.Equal(0, result.DocumentsIndexed);
            Assert.Equal(0, result.EmbedCalls);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// The composition itself: one fingerprint in, one hash out, and a bump to a fingerprint nothing
    /// selected leaves the selected one's hash alone.
    /// </summary>
    [Fact]
    public async Task The_fingerprint_is_the_only_extractor_input_to_the_hash()
    {
        using var host = await IngestionTestHost.StartAsync();
        var baseline = await host.Pipeline.GetRecipeAsync(Token);

        var text = Internal.RecipeComposition.WithExtractor(baseline, "text:1");
        var pdf = Internal.RecipeComposition.WithExtractor(baseline, "pdf:1");
        var pdfBumped = Internal.RecipeComposition.WithExtractor(baseline, "pdf:2");

        Assert.Equal(text.Hash, Internal.RecipeComposition.WithExtractor(baseline, "text:1").Hash);
        Assert.NotEqual(text.Hash, pdf.Hash);
        Assert.NotEqual(pdf.Hash, pdfBumped.Hash);

        // The baseline stands in for "no extractor selected yet" and is not any real fingerprint.
        Assert.NotEqual(baseline.Hash, text.Hash);
        Assert.Equal("*", baseline.ExtractorFingerprint);
    }

    /// <summary>
    /// Rewrites every stored <c>recipe_hash</c> to a value this build never produces. That is what
    /// a consumer changing <c>MaxTokens</c> does, without needing a second host.
    /// </summary>
    private static async Task<string> ForceRecipeDriftAsync(IngestionTestHost host)
    {
        var drifted = ContentHash.OfText("a recipe this build never produces");
        var connection = await host.Database.OpenConnectionAsync(Token).ConfigureAwait(true);
        await using (connection.ConfigureAwait(true))
        {
            await connection.ExecuteAsync(
                """UPDATE "qedge_ingest_document" SET "recipe_hash" = $h""",
                [new SqliteParameter("$h", drifted.ToBlob())],
                Token).ConfigureAwait(true);
        }

        return drifted.ToHex();
    }
}
