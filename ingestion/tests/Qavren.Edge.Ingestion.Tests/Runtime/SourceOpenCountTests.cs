using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// Spec 9.4: <c>OpenAsync</c> is called exactly <b>twice</b> per indexed document — the hash pass
/// and the extraction pass. Re-filed here from Task 3.1: wave 3 could only prove the extraction
/// half, because the hash pass did not exist until the runner did.
/// </summary>
public sealed class SourceOpenCountTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpenAsync_is_called_exactly_twice_per_indexed_document()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("a.txt", "alpha beta");

        await host.Pipeline.RunAsync(source, cancellationToken: Token);

        // Not three, and never one.
        Assert.Equal(2, source.Opens["a.txt"]);
    }

    [Fact]
    public async Task The_hash_gate_opens_once_and_never_reaches_the_extractor()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("a.txt", "alpha beta");

        await host.Pipeline.RunAsync(source, cancellationToken: Token);
        await host.Pipeline.RunAsync(source, cancellationToken: Token);

        // Two on the first run, one more on the second: the hash gate opened the file, matched, and
        // skipped the decode, the parse, the chunk and the embed.
        Assert.Equal(3, source.Opens["a.txt"]);
    }

    [Fact]
    public async Task A_document_over_the_declared_ceiling_is_never_opened_at_all()
    {
        using var host = await IngestionTestHost.StartAsync(o => o.MaxDocumentBytes = 4);
        var source = new RecordingSource().Add("a.txt", "far too long for four bytes");

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        var failed = Assert.Single(result.Failures);
        Assert.Equal(EdgeErrorCode.IngestionDocumentTooLarge, failed.Failure!.Code);
        Assert.False(source.Opens.ContainsKey("a.txt"));
    }
}
