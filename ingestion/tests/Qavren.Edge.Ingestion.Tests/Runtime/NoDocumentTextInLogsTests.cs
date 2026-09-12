using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// Spec 12's logging rule, asserted rather than asserted-about: <b>no document text is ever
/// logged</b> — ids, paths, offsets and counts only. Every log line emitted during an ingest of a
/// Unicode document is captured and fails the test if it carries a substring of the file.
/// </summary>
public sealed class NoDocumentTextInLogsTests
{
    private const string Secret = "Zażółć gęślą jaźń — a sentence no log line should ever carry";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_document_text_reaches_the_logs_on_an_indexed_document()
    {
        using var host = await IngestionTestHost.StartAsync();
        var source = new RecordingSource().Add("unicode.txt", Secret);

        await host.Pipeline.RunAsync(source, cancellationToken: Token);

        AssertNoLeak(host);
    }

    [Fact]
    public async Task No_document_text_reaches_the_logs_on_a_failed_document()
    {
        // The failure path is where text leaks: an exception message that carried the document is
        // the easiest way to lose this property, so it is asserted on both paths.
        using var host = await IngestionTestHost.StartAsync(o =>
            o.Extractors.Add(new ThrowingExtractor(new InvalidOperationException("boom"))));
        var source = new RecordingSource().Add("unicode.txt", Secret);

        await host.Pipeline.RunAsync(source, cancellationToken: Token);

        AssertNoLeak(host);
    }

    private static void AssertNoLeak(IngestionTestHost host)
    {
        foreach (var word in Secret.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length < 5)
            {
                continue;
            }

            Assert.DoesNotContain(host.Logs.Lines, line => line.Contains(word, StringComparison.Ordinal));
        }
    }
}
