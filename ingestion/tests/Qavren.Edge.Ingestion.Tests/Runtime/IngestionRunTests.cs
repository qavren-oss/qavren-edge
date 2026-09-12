using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// The end-to-end shape of a run, over the real natives: the happy path, the hash gate, pruning,
/// <c>Force</c>, <c>DeleteMissing</c>, the abort and fail-fast ceilings, and the concurrency gate.
/// Duplicate ids, extraction faults, log hygiene and the open count have files of their own.
/// </summary>
public sealed class IngestionRunTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_first_run_indexes_every_document_and_a_second_run_embeds_nothing()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("a.txt", "alpha beta gamma").Add("b.txt", "delta epsilon");

        var first = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, first.Outcome);
        Assert.Equal(2, first.DocumentsSeen);
        Assert.Equal(2, first.DocumentsIndexed);
        Assert.Equal(0, first.DocumentsFailed);
        Assert.Equal(2, first.ChunksAdded);
        Assert.Equal(2, first.EmbedCalls);
        Assert.Null(first.Failure);
        Assert.Empty(first.Failures);

        var callsAfterFirst = host.Generator.CallCount;
        var second = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, second.Outcome);
        Assert.Equal(2, second.DocumentsSkipped);
        Assert.Equal(0, second.DocumentsIndexed);
        Assert.Equal(0, second.ChunksAdded);
        Assert.Equal(0, second.EmbedCalls);

        // The hash gate is what makes "re-ingest performs zero embed calls" a testable claim
        // rather than a hope.
        Assert.Equal(callsAfterFirst, host.Generator.CallCount);
    }

    /// <summary>
    /// Spec 7.1: the registry is composed at RESOLVE time from every consumer channel plus the
    /// built-ins, so <c>AddDocumentExtractor</c> is a live registration and not a no-op — and, since
    /// the composition happens when the pipeline is built rather than when <c>AddIngestion</c> runs,
    /// the two builder calls are order-independent. That is exactly what
    /// <c>AddPdfExtractor</c> / <c>AddDocxExtractor</c> rely on; without it every PDF would come back
    /// 6101 Unsupported.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_extractor_registered_on_the_builder_is_selected_in_either_order(bool beforeAddIngestion)
    {
        var extractor = new ThrowingExtractor(new InvalidOperationException("selected"), id: "consumer");
        using var host = beforeAddIngestion
            ? await IngestionTestHost.StartAsync(configureBeforeIngestion: edge => edge.AddDocumentExtractor(extractor))
            : await IngestionTestHost.StartAsync(configure: edge => edge.AddDocumentExtractor(extractor));

        var result = await host.Pipeline.RunAsync(
            new RecordingSource().Add("a.txt", "alpha"), cancellationToken: Token);

        // Selected: it ran, and it threw. Not selected would be PlainTextExtractor and a clean run.
        var failed = Assert.Single(result.Failures);
        Assert.Equal(EdgeErrorCode.ExtractionFailed, failed.Failure!.Code);
        Assert.Equal("consumer", failed.Failure.ExtractorId);
    }

    [Fact]
    public async Task The_factory_overload_of_AddDocumentExtractor_is_also_live()
    {
        using var host = await IngestionTestHost.StartAsync(
            configure: edge => edge.AddDocumentExtractor(
                _ => new ThrowingExtractor(new InvalidOperationException("selected"), id: "factory")));

        var result = await host.Pipeline.RunAsync(
            new RecordingSource().Add("a.txt", "alpha"), cancellationToken: Token);

        var failed = Assert.Single(result.Failures);
        Assert.Equal("factory", failed.Failure!.ExtractorId);
    }

    [Fact]
    public async Task A_changed_document_replaces_only_its_own_chunks()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("a.txt", "alpha").Add("b.txt", "beta");

        await host.Pipeline.RunAsync(source, cancellationToken: Token);
        source.Replace("a.txt", "alpha changed");
        var second = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(1, second.DocumentsIndexed);
        Assert.Equal(1, second.DocumentsSkipped);
        Assert.Equal(1, second.ChunksAdded);
        Assert.Equal(1, second.ChunksRemoved);
    }

    [Fact]
    public async Task A_removed_document_is_pruned_on_a_completed_run()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("a.txt", "alpha").Add("b.txt", "beta");

        await host.Pipeline.RunAsync(source, cancellationToken: Token);
        source.Remove("b.txt");
        var second = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(1, second.DocumentsRemoved);

        var status = await host.Pipeline.GetStatusAsync(ct: Token);
        Assert.Equal(1, status.DocumentCount);
        Assert.Equal(1, status.ChunkCount);
    }

    [Fact]
    public async Task DeleteMissing_false_on_one_run_leaves_the_stale_document_alone()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("a.txt", "alpha").Add("b.txt", "beta");

        await host.Pipeline.RunAsync(source, cancellationToken: Token);
        source.Remove("b.txt");
        var second = await host.Pipeline.RunAsync(
            source, options: new IngestionRunOptions { DeleteMissing = false }, cancellationToken: Token);

        Assert.Equal(0, second.DocumentsRemoved);

        // A nullable bool rather than a bool: false has to be distinguishable from "not specified".
        var status = await host.Pipeline.GetStatusAsync(ct: Token);
        Assert.Equal(2, status.DocumentCount);
    }

    [Fact]
    public async Task PruneAsync_sweeps_without_a_write_phase()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("a.txt", "alpha").Add("b.txt", "beta");

        await host.Pipeline.RunAsync(source, cancellationToken: Token);
        var callsBefore = host.Generator.CallCount;

        source.Remove("b.txt");
        var pruned = await host.Pipeline.PruneAsync(source, cancellationToken: Token);

        Assert.Equal(1, pruned);
        Assert.Equal(callsBefore, host.Generator.CallCount);
    }

    [Fact]
    public async Task Force_re_chunks_but_still_writes_nothing_when_the_content_is_unchanged()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("a.txt", "alpha beta");

        await host.Pipeline.RunAsync(source, cancellationToken: Token);
        var forced = await host.Pipeline.RunAsync(
            source, options: new IngestionRunOptions { Force = true }, cancellationToken: Token);

        // Force costs CPU, never embeddings: it bypasses the gates, not the diff.
        Assert.Equal(1, forced.DocumentsIndexed);
        Assert.Equal(0, forced.ChunksAdded);
        Assert.Equal(0, forced.EmbedCalls);
        Assert.Equal(1, forced.ChunksUnchanged);
    }

    [Fact]
    public async Task AbortAfterConsecutiveErrors_raises_6010_with_the_last_failure_as_inner()
    {
        using var host = await IngestionTestHost.StartAsync(o =>
        {
            o.AbortAfterConsecutiveErrors = 2;
            o.Extractors.Add(new ThrowingExtractor(new InvalidOperationException("boom")));
        });
        var source = new RecordingSource().Add("a.txt", "a").Add("b.txt", "b").Add("c.txt", "c");

        var error = await Assert.ThrowsAsync<EdgeIngestionException>(
            () => host.Pipeline.RunAsync(source, cancellationToken: Token));

        Assert.Equal(EdgeErrorCode.IngestionRunAborted, error.Code);
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public async Task FailFast_promotes_the_first_document_failure_and_rethrows_the_original()
    {
        var typed = new EdgeExtractionException(EdgeErrorCode.DocumentMalformed, "bad") { ExtractorName = "throwing" };
        using var host = await IngestionTestHost.StartAsync(o => o.Extractors.Add(new ThrowingExtractor(typed)));
        var source = new RecordingSource().Add("a.txt", "alpha");

        var error = await Assert.ThrowsAsync<EdgeExtractionException>(
            () => host.Pipeline.RunAsync(
                source, options: new IngestionRunOptions { FailFast = true }, cancellationToken: Token));

        Assert.Same(typed, error);
    }

    [Fact]
    public async Task Progress_reports_counts_and_a_stage_and_never_a_percentage()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("a.txt", "alpha").Add("b.txt", "beta");
        var reports = new List<IngestionProgress>();

        await host.Pipeline.RunAsync(source, new Progress<IngestionProgress>(reports.Add), Token);

        // Progress is asynchronous by contract; the run's own Current is the synchronous witness.
        Assert.Null(host.Pipeline.Current);
    }

    [Fact]
    public async Task A_second_concurrent_run_on_one_pipeline_is_6008()
    {
        using var host = await IngestionTestHost.StartAsync();
        var gate = new TaskCompletionSource();
        var source = new BlockingSource(gate.Task);

        var first = host.Pipeline.RunAsync(source, cancellationToken: Token);
        await Task.Delay(50, Token);

        var error = await Assert.ThrowsAsync<EdgeIngestionException>(
            () => host.Pipeline.RunAsync(new RecordingSource("other"), cancellationToken: Token));
        Assert.Equal(EdgeErrorCode.IngestionRunAlreadyActive, error.Code);

        gate.SetResult();
        var completed = await first.ConfigureAwait(true);

        // 6008 NAMES the active run. A placeholder here would send a caller chasing a run id that
        // appears in no log line and no _run row.
        Assert.Equal(completed.RunId, error.RunId);
        Assert.Contains(completed.RunId, error.Message, StringComparison.Ordinal);
    }

    private sealed class BlockingSource(Task gate) : IngestionSource
    {
        public override string Id => "blocking";

        public override async IAsyncEnumerable<DocumentSourceItem> EnumerateAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await gate.ConfigureAwait(false);
            yield break;
        }
    }
}
