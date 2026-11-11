using System.Text;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// Two verdicts a run reaches about a document that hashing it again cannot change: it is over
/// <c>MaxDocumentBytes</c>, or no extractor accepts it. Both are recorded once and then skipped
/// without opening the file for as long as the source declares the same size and modified time, and
/// neither is evidence of a failing run. The exception is an unsupported document that still has
/// chunks in the index: whether those are still true is exactly what the hash decides.
/// </summary>
public sealed class RememberedVerdictTests
{
    private const string NothingReadsThis = "application/x-nothing-reads-this";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_oversized_document_is_recorded_once_and_then_skipped_without_being_opened()
    {
        using var host = await IngestionTestHost.StartAsync(o => o.MaxDocumentBytes = 4);
        var source = new RecordingSource().Add("a.txt", "far too long for four bytes");

        var first = await host.Pipeline.RunAsync(source, cancellationToken: Token);
        Assert.Equal(EdgeErrorCode.IngestionDocumentTooLarge, Assert.Single(first.Failures).Failure!.Code);

        host.Time.Advance(TimeSpan.FromSeconds(1));
        var second = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        // It used to fail again on every run, for ever, and be logged as an error each time.
        Assert.Equal(0, second.DocumentsFailed);
        Assert.Equal(1, second.DocumentsSkipped);
        Assert.Empty(second.Failures);
        Assert.False(source.Opens.ContainsKey("a.txt"));

        // Skipped is what the run did. What the document IS has not changed.
        var status = await host.Pipeline.GetStatusAsync(ct: Token);
        Assert.Equal(1, status.FailedCount);
        Assert.Equal(0, status.StaleDocumentCount);

        // Force bypasses every gate, this one included.
        var forced = await host.Pipeline.RunAsync(
            source, options: new IngestionRunOptions { Force = true }, cancellationToken: Token);
        Assert.Equal(EdgeErrorCode.IngestionDocumentTooLarge, Assert.Single(forced.Failures).Failure!.Code);
    }

    [Fact]
    public async Task An_oversized_document_is_judged_again_when_what_the_source_declares_changes()
    {
        using var host = await IngestionTestHost.StartAsync(o => o.MaxDocumentBytes = 8);
        var source = new RecordingSource().Add("a.txt", "far too long for eight bytes");

        await host.Pipeline.RunAsync(source, cancellationToken: Token);

        // Still too large, but a different file: it is said again rather than assumed.
        source.Replace("a.txt", "a different length and still far too long");
        var stillLarge = await host.Pipeline.RunAsync(source, cancellationToken: Token);
        Assert.Equal(EdgeErrorCode.IngestionDocumentTooLarge, Assert.Single(stillLarge.Failures).Failure!.Code);

        source.Replace("a.txt", "fits");
        var shrunk = await host.Pipeline.RunAsync(source, cancellationToken: Token);
        Assert.Empty(shrunk.Failures);
        Assert.Equal(1, shrunk.DocumentsIndexed);
    }

    [Fact]
    public async Task A_ceiling_met_by_the_counted_read_is_not_read_again_until_the_size_or_time_moves()
    {
        using var host = await IngestionTestHost.StartAsync(o => o.MaxDocumentBytes = 16);

        // The source under-declares, so the size gate passes and only the counted read finds out.
        var source = new DeclaringSource("big.txt", Encoding.UTF8.GetBytes(new string('x', 400)), declaredSize: 3);

        var first = await host.Pipeline.RunAsync(source, cancellationToken: Token);
        Assert.Equal(EdgeErrorCode.IngestionDocumentTooLarge, Assert.Single(first.Failures).Failure!.Code);
        Assert.Equal(1, source.Opens);

        var second = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        // Reading sixteen bytes is nothing; reading 32 MiB of every video in a folder on every run
        // to learn the same thing is the cost this removes.
        Assert.Equal(1, source.Opens);
        Assert.Equal(1, second.DocumentsSkipped);
        Assert.Empty(second.Failures);

        source.Modified = source.Modified.AddMinutes(1);
        var third = await host.Pipeline.RunAsync(source, cancellationToken: Token);
        Assert.Equal(2, source.Opens);
        Assert.Equal(EdgeErrorCode.IngestionDocumentTooLarge, Assert.Single(third.Failures).Failure!.Code);
    }

    [Fact]
    public async Task Oversized_documents_do_not_count_towards_AbortAfterConsecutiveErrors()
    {
        using var host = await IngestionTestHost.StartAsync(o =>
        {
            o.MaxDocumentBytes = 4;
            o.AbortAfterConsecutiveErrors = 2;
        });
        var source = new RecordingSource()
            .Add("a.txt", "far too long")
            .Add("b.txt", "also too long")
            .Add("c.txt", "and this one");

        // Three in a row against a ceiling of two used to abort the run with 6010.
        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.DocumentsFailed);
    }

    [Fact]
    public async Task An_oversized_document_does_not_end_a_run_of_real_failures_either()
    {
        using var host = await IngestionTestHost.StartAsync(o =>
        {
            o.MaxDocumentBytes = 8;
            o.AbortAfterConsecutiveErrors = 2;
            o.Extractors.Add(new ThrowingExtractor(new InvalidOperationException("boom")));
        });
        var source = new RecordingSource()
            .Add("a.txt", "a")
            .Add("b.txt", "over the ceiling of eight")
            .Add("c.txt", "c");

        // Two extractor failures with a verdict between them are still two in a row: nothing about
        // an oversized file says the extractor has started working.
        var error = await Assert.ThrowsAsync<EdgeIngestionException>(
            () => host.Pipeline.RunAsync(source, cancellationToken: Token));

        // Aborted at c.txt, the second real failure - not at b.txt, which is where counting the
        // oversized file as a failure used to stop the run.
        Assert.Equal(EdgeErrorCode.IngestionRunAborted, error.Code);
        Assert.True(source.Opens.ContainsKey("c.txt"), "the run was aborted before the second real failure");
    }

    [Fact]
    public async Task An_unsupported_document_with_nothing_indexed_is_not_hashed_again()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("blob", "bytes nobody here can read", NothingReadsThis);

        var first = await host.Pipeline.RunAsync(source, cancellationToken: Token);
        Assert.Equal(IngestionDocumentOutcome.Unsupported, Assert.Single(first.Documents).Outcome);
        Assert.Equal(1, source.Opens["blob"]);

        var second = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        // It was already skipped on the hash gate - after being read end to end to get the hash.
        Assert.Equal(1, second.DocumentsSkipped);
        Assert.Equal(1, source.Opens["blob"]);

        // A different size is a different file, and it is looked at again.
        source.Replace("blob", "other bytes, and more of them, that nobody here can read");
        var third = await host.Pipeline.RunAsync(source, cancellationToken: Token);
        Assert.Equal(IngestionDocumentOutcome.Unsupported, Assert.Single(third.Documents).Outcome);
        Assert.Equal(2, source.Opens["blob"]);
    }

    [Fact]
    public async Task An_unsupported_document_that_still_has_chunks_is_hashed_so_changed_bytes_take_them_out()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("note", "alpha beta gamma");
        await host.Pipeline.RunAsync(source, cancellationToken: Token);

        // Its extractor goes away with the bytes unchanged, so the chunks are kept.
        source.Remove("note").Add("note", "alpha beta gamma", NothingReadsThis);
        await host.Pipeline.RunAsync(source, cancellationToken: Token);
        Assert.Equal(1, (await host.Pipeline.GetStatusAsync(ct: Token)).ChunkCount);

        // Same length, same declared time, different bytes. Size and time say "unchanged" and are
        // wrong, which is why a row with chunks behind it is never skipped on them.
        source.Remove("note").Add("note", "gamma beta alpha", NothingReadsThis);
        var third = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(1, third.ChunksRemoved);
        Assert.Equal(0, (await host.Pipeline.GetStatusAsync(ct: Token)).ChunkCount);
    }

    /// <summary>One document whose declared size and time are whatever the test says they are.</summary>
    private sealed class DeclaringSource(string documentId, byte[] bytes, long declaredSize) : IngestionSource
    {
        public int Opens { get; private set; }

        public DateTimeOffset Modified { get; set; } = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);

        public override string Id => "declaring";

#pragma warning disable CS1998
        public override async IAsyncEnumerable<DocumentSourceItem> EnumerateAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new DocumentSourceItem(
                documentId,
                IngestionMediaTypes.PlainText,
                _ =>
                {
                    Opens++;
                    return new ValueTask<Stream>(new MemoryStream(bytes, writable: false));
                })
            {
                SizeBytes = declaredSize,
                LastModifiedUtc = Modified,
            };
        }
#pragma warning restore CS1998
    }
}
