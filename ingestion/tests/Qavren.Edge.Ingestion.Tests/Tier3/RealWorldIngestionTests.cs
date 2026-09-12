using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Qavren.Edge.Ingestion.OpenXml;
using Qavren.Edge.Ingestion.Pdf;
using Qavren.Edge.Ingestion.Tests.Integration;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Tier3;

/// <summary>
/// Spec 14.5's real-world document lane. Tier 3, guarded on <c>QAVREN_EDGE_TIER3</c> at runtime:
/// for each manifest entry, read <c>$QAVREN_EDGE_DOCS_DIR/&lt;id&gt;</c>, skip that entry with a
/// printed reason if the fetch step did not produce it, and otherwise assert only that its
/// <c>phrase</c> is in the extracted text and that the pipeline does not throw. Nothing is
/// committed and no digest is re-checked here - the fetch step already did that.
/// </summary>
public sealed class RealWorldIngestionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact(
        Skip = "QAVREN_EDGE_TIER3 not set",
        SkipUnless = nameof(Tier3Available.Yes),
        SkipType = typeof(Tier3Available))]
    public async Task Every_fetched_real_world_document_extracts_its_phrase_and_ingests_without_throwing()
    {
        var entries = RealWorldCorpus.Load();
        Assert.SkipWhen(entries.Count == 0, RealWorldCorpus.OwedReason);

        var directory = Tier3Available.DocumentsDirectory;
        Assert.SkipWhen(directory is null, "QAVREN_EDGE_DOCS_DIR is not set; the fetch step did not run.");

        using var host = await IntegrationHost.StartAsync(new IntegrationHostOptions
        {
            AfterIngestion = edge => edge.AddPdfExtractor().AddDocxExtractor(),
        });
        var registry = host.Services.GetRequiredService<IDocumentExtractorRegistry>();
        var output = TestContext.Current.TestOutputHelper;

        var asserted = 0;
        foreach (var entry in entries)
        {
            var path = Path.Combine(directory!, entry.Id!);
            if (!File.Exists(path))
            {
                output?.WriteLine($"skipped '{entry.Id}': the fetch step did not produce '{path}'.");
                continue;
            }

            // The phrase, from the extracted text: the same extractor the pipeline would select.
            var item = new DocumentSourceItem(
                entry.Id!,
                IngestionMediaTypes.FromExtension(entry.Id!),
                _ => new ValueTask<Stream>(File.OpenRead(path)),
                Path: path,
                SizeBytes: new FileInfo(path).Length);

            Assert.True(
                registry.TryResolve(item, out var extractor),
                $"'{entry.Id}': no extractor accepts it. The manifest id must carry the file's extension.");

            var context = new ExtractionContext("realworld", "chunks", 32L * 1024 * 1024, new ExtractionOptions(), NullLogger.Instance);
            var document = await extractor.ExtractAsync(item, context, Token);

            Assert.True(
                document.Text.Contains(entry.Phrase!, StringComparison.Ordinal),
                $"'{entry.Id}' ({entry.Producer}): extracted text does not contain the phrase '{entry.Phrase}'.");

            // And the pipeline does not throw, and records no failure for it.
            var result = await host.Pipeline.RunAsync(
                IngestionSource.Items([item], "realworld-" + entry.Producer), cancellationToken: Token);

            Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
            Assert.Empty(result.Failures);
            output?.WriteLine($"'{entry.Id}' ({entry.Producer}): {result.ChunksAdded} chunks, phrase found.");
            asserted++;
        }

        Assert.SkipWhen(asserted == 0, "no manifest entry was fetched into QAVREN_EDGE_DOCS_DIR; nothing to assert.");
    }
}
