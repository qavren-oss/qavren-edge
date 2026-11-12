using Qavren.Edge.Ingestion.Internal;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// A skipped document's <c>last_run_id</c> is written in batches of
/// <see cref="IngestionRunner.StampBatchSize"/> rather than in a transaction of its own. What has to
/// stay true is that every skip is stamped by the time the run is over - across batch boundaries,
/// with a remainder, and on a run that stops early - because <c>staleDocuments</c> reads it.
/// </summary>
public sealed class BatchedStampTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>More than two batches, and not a multiple of one.</summary>
    private const int Documents = (2 * IngestionRunner.StampBatchSize) + 37;

    private static RecordingSource Corpus()
    {
        var source = new RecordingSource();
        for (var i = 0; i < Documents; i++)
        {
            source.Add($"doc-{i:D4}.txt", $"document number {i}");
        }

        return source;
    }

    [Fact]
    public async Task A_rerun_of_an_unchanged_corpus_stamps_every_document_across_its_batches()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = Corpus();

        await host.Pipeline.RunAsync(source, cancellationToken: Token);
        var rerun = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(Documents, rerun.DocumentsSkipped);
        Assert.Equal(0, (await host.Pipeline.GetStatusAsync(ct: Token)).StaleDocumentCount);

        var rows = await host.State().ListDocumentsAsync(rerun.CollectionName, source.Id, Token);
        Assert.Equal(Documents, rows.Count);
        Assert.All(rows, row => Assert.Equal(rerun.RunId, row.LastRunId));
    }


    [Fact]
    public async Task A_run_that_stops_early_still_stamps_the_documents_it_skipped_before_it_stopped()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = Corpus();
        await host.Pipeline.RunAsync(source, cancellationToken: Token);

        // Stops part-way through the second batch: what it skipped is in a batch nobody filled.
        const int Reached = IngestionRunner.StampBatchSize + 20;
        var partial = await host.Pipeline.RunAsync(
            source,
            options: new IngestionRunOptions { Budget = new IngestionBudget { MaxDocuments = Reached } },
            cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Suspended, partial.Outcome);
        Assert.Equal(Reached, partial.DocumentsSkipped);

        // Every skip it made was written - the full batch AND the twenty in the batch nobody
        // filled - and nothing it never reached was touched.
        var rows = await host.State().ListDocumentsAsync(partial.CollectionName, source.Id, Token);
        Assert.Equal(Reached, rows.Count(row => row.LastRunId == partial.RunId));
        Assert.Equal(Documents - Reached, rows.Count(row => row.LastRunId != partial.RunId));
    }
}
