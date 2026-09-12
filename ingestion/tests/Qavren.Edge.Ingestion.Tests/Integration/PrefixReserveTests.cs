using Qavren.Edge.Ingestion.Tests.Extraction;
using Qavren.Edge.Ingestion.Tests.Runtime;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Integration;

/// <summary>
/// Spec 8.3 under the one profile that carries BOTH prefixes. The generator sees the embed text
/// without any prefix - the double-prefix guard, since <c>OnnxEmbeddingGenerator.ApplyPrefix</c>
/// is the one that writes it - and the reserve is real: <c>MaxTokens</c> resolves to
/// <c>512 - 2 - 32 - CountTokens("search_document: ")</c>, not 478, which is the guard on the
/// encoder overrun a missing reserve would cause. Task 7.1 Step 5.
/// </summary>
public sealed class PrefixReserveTests
{
    private const string DocumentPrefix = "search_document: ";
    private const string QueryPrefix = "search_query: ";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_generator_never_sees_a_prefix_and_the_reserve_is_subtracted_from_the_budget()
    {
        var profile = ChunkModelProfile.NomicEmbedTextV15Int8;
        var tokenizer = new FakeChunkTokenizer(maxSequenceLength: profile.MaxSequenceLength);
        using var host = await IntegrationHost.StartAsync(new IntegrationHostOptions
        {
            Ingestion = o => o.Model = profile,
            Tokenizer = tokenizer,
            Generator = new RecordingEmbeddingGenerator(dimensions: profile.Dimensions),
        });

        // The reserve. The whitespace tokenizer counts "search_document: " as one token, so the
        // budget is 477, and it is NOT the 478 a profile without a prefix reserve would resolve to.
        var recipe = await host.Pipeline.GetRecipeAsync(Token);
        var prefixTokens = tokenizer.CountTokens(DocumentPrefix.AsSpan());
        Assert.Equal(1, prefixTokens);
        Assert.Equal(512 - 2 - 32 - prefixTokens, recipe.Chunking.MaxTokens);
        Assert.NotEqual(478, recipe.Chunking.MaxTokens);
        Assert.Equal(prefixTokens, recipe.Chunking.DocumentPrefixTokens);
        Assert.Equal(DocumentPrefix, recipe.DocumentPrefix);
        Assert.Equal(QueryPrefix, recipe.QueryPrefix);

        // A Markdown fixture, so breadcrumbs are in play, plus plain text, so bare chunk text is too.
        var source = IngestionSource.Items(
            [
                FixtureCorpus.Item("markdown/headings.md"),
                FixtureCorpus.Item("markdown/tables-lists.md"),
                FixtureCorpus.Item("text/three-paragraphs.txt"),
            ],
            "corpus");

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);
        Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
        Assert.True(result.ChunksAdded > 3, $"expected several chunks, got {result.ChunksAdded}");

        var stored = await Db.ChunksAsync(host.Database);
        var inputs = host.Generator.Calls.SelectMany(c => c).ToList();
        Assert.Equal(result.ChunksAdded, inputs.Count);

        var sawBreadcrumb = false;
        foreach (var input in inputs)
        {
            // Never a prefix - the document prefix is a reserve and a recipe input only, and the
            // query prefix is never applied to a stored chunk at all.
            Assert.False(input.StartsWith(DocumentPrefix, StringComparison.Ordinal), input);
            Assert.False(input.StartsWith(QueryPrefix, StringComparison.Ordinal), input);

            // Every embed text starts with the breadcrumb (heading_path as stored) or the chunk text.
            var startsWithText = stored.Any(c => input.StartsWith(c.Text, StringComparison.Ordinal));
            var startsWithBreadcrumb = stored.Any(c =>
                c.HeadingPath.Length > 0 && input.StartsWith(c.HeadingPath, StringComparison.Ordinal));
            sawBreadcrumb |= startsWithBreadcrumb && !startsWithText;
            Assert.True(startsWithText || startsWithBreadcrumb, "embed text starts with neither the chunk text nor a breadcrumb: " + input);
        }

        Assert.True(sawBreadcrumb, "no embed text carried a breadcrumb, so the Markdown half of the guard never ran");
    }
}
