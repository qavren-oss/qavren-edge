using System.Text.RegularExpressions;
using Qavren.Edge.Ingestion.Pdf;
using Qavren.Edge.Ingestion.Tests.Extraction;
using Qavren.Edge.Ingestion.Tests.Runtime;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Integration;

/// <summary>
/// Spec 14.3, first bullet: the committed corpus round-trips through the real natives, and every
/// table agrees about what was written - the data table, the vec0 table, the FTS5 sidecar and the
/// three state tables. Task 7.1 Step 1.
/// </summary>
public sealed partial class CorpusRoundTripTests
{
    /// <summary>The twelve text and Markdown fixtures Task 1.3 committed, by their embedded path.</summary>
    internal static readonly string[] TextCorpus =
    [
        "text/bom.txt",
        "text/crlf-and-lone-cr.txt",
        "text/empty.txt",
        "text/long-token.txt",
        "text/three-paragraphs.txt",
        "text/unicode.txt",
        "text/whitespace-only.txt",
        "markdown/fences.md",
        "markdown/giant-heading-section.md",
        "markdown/headings.md",
        "markdown/raw-html.md",
        "markdown/tables-lists.md",
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex KeyShape { get; }

    internal static IngestionSource Corpus(IEnumerable<string> fixtures, string sourceId = "corpus") =>
        IngestionSource.Items(fixtures.Select(FixtureCorpus.Item).ToList(), sourceId);

    [Fact]
    public async Task The_committed_corpus_round_trips_with_every_table_in_agreement()
    {
        using var host = await IntegrationHost.StartAsync();

        var result = await host.Pipeline.RunAsync(Corpus(TextCorpus), cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
        Assert.Equal(TextCorpus.Length, result.DocumentsSeen);
        Assert.Equal(0, result.DocumentsFailed);
        Assert.True(result.ChunksAdded > TextCorpus.Length, $"expected a multi-chunk corpus, got {result.ChunksAdded}");

        // The key format: spec 9.3's 32 lower-case hex, unique across the collection.
        var keys = await Db.KeysAsync(host.Database);
        Assert.Equal(result.ChunksAdded, keys.Count);
        Assert.All(keys, key => Assert.Matches(KeyShape, key));
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());

        // Row counts on all three collection tables.
        Assert.Equal(result.ChunksAdded, await Db.CountAsync(host.Database, Db.DataTable));
        Assert.Equal(result.ChunksAdded, await Db.CountAsync(host.Database, Db.VectorTable));
        Assert.Equal(result.ChunksAdded, await Db.CountAsync(host.Database, Db.FullTextTable));

        // And on the three state tables: one row per document, one run, two meta keys.
        Assert.Equal(TextCorpus.Length, await Db.CountAsync(host.Database, Db.DocumentTable));
        Assert.Equal(1, await Db.CountAsync(host.Database, Db.RunTable));
        Assert.Equal(2, await Db.CountAsync(host.Database, Db.MetaTable));

        // Every document's stored chunk_count is what the data table actually holds for it, and
        // the two empty fixtures hold nothing.
        foreach (var document in result.Documents)
        {
            var stored = await Db.KeysAsync(host.Database, document.DocumentId);
            var recorded = await Db.DocumentColumnAsync(host.Database, document.DocumentId, "chunk_count");
            Assert.Equal(stored.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), recorded);
        }

        Assert.Empty(await Db.KeysAsync(host.Database, "empty.txt"));
        Assert.Empty(await Db.KeysAsync(host.Database, "whitespace-only.txt"));

        // The FTS5 sidecar is queryable and consistent with its content table.
        await Db.FtsIntegrityCheckAsync(host.Database);
        Assert.True(await Db.FtsMatchCountAsync(host.Database, "paragraph") > 0, "the sidecar indexed nothing");
    }

    /// <summary>
    /// Spec 9.3's key, recomputed independently for a one-chunk plain-text document whose embed
    /// text is its own text: <c>xxh128(source) ⊕ xxh128(document) ⊕ xxh128(embedText) ⊕ 0</c>.
    /// </summary>
    [Fact]
    public async Task A_stored_key_is_the_documented_derivation_of_source_document_and_embed_text()
    {
        using var host = await IntegrationHost.StartAsync();
        var source = new RecordingSource("memory").Add("k.txt", "alpha beta gamma");

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);
        Assert.Equal(1, result.ChunksAdded);

        var expected = ContentHash.Combine(
        [
            ContentHash.OfText("memory"),
            ContentHash.OfText("k.txt"),
            ContentHash.OfText("alpha beta gamma"),
            new ContentHash(UInt128.Zero),
        ]).ToHex();

        var stored = Assert.Single(await Db.KeysAsync(host.Database, "k.txt"));
        Assert.Equal(expected, stored);

        // The embed text the generator saw IS the chunk text - no prefix, no breadcrumb here.
        var input = Assert.Single(Assert.Single(host.Generator.Calls));
        Assert.Equal("alpha beta gamma", input);
    }

    /// <summary>
    /// The PDF half of the corpus, through the real satellite at integration scope: the well-formed
    /// fixtures index, the scanned one is <c>NoTextLayer</c>, and the run does not throw for any.
    /// </summary>
    [Fact]
    public async Task The_pdf_corpus_ingests_through_the_satellite_without_a_run_fault()
    {
        using var host = await IntegrationHost.StartAsync(new IntegrationHostOptions
        {
            AfterIngestion = edge => edge.AddPdfExtractor(),
        });

        string[] pdfs =
        [
            "pdf/minimal-text.pdf",
            "pdf/two-pages.pdf",
            "pdf/hyphen-linebreak.pdf",
            "pdf/two-columns.pdf",
            "pdf/xref-stream.pdf",
            "pdf/no-text-layer.pdf",
            "pdf/broken-startxref.pdf",
        ];

        var result = await host.Pipeline.RunAsync(Corpus(pdfs, "pdfs"), cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
        Assert.Null(result.Failure);
        Assert.Equal(pdfs.Length, result.DocumentsSeen);

        var byId = result.Documents.ToDictionary(d => d.DocumentId, StringComparer.Ordinal);
        Assert.Equal(IngestionDocumentOutcome.Indexed, byId["minimal-text.pdf"].Outcome);
        Assert.Equal(IngestionDocumentOutcome.Indexed, byId["two-pages.pdf"].Outcome);
        Assert.Equal(IngestionDocumentOutcome.Indexed, byId["xref-stream.pdf"].Outcome);
        Assert.Equal(IngestionDocumentOutcome.NoTextLayer, byId["no-text-layer.pdf"].Outcome);
        Assert.Equal("pdf", byId["minimal-text.pdf"].ExtractorId);

        // Every document has a state row whichever way it went, so the next run skips it.
        Assert.Equal(pdfs.Length, await Db.CountAsync(host.Database, Db.DocumentTable));
        var second = await host.Pipeline.RunAsync(Corpus(pdfs, "pdfs"), cancellationToken: Token);
        Assert.Equal(0, second.EmbedCalls);
        Assert.Equal(pdfs.Length, second.DocumentsSkipped);
    }
}
