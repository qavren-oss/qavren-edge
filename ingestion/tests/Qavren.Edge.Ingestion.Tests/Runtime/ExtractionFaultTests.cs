using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Runtime;

/// <summary>
/// Error 6102, and the split that makes it worth having: an extractor that knows what went wrong
/// says so and its code arrives unchanged; an extractor that does not gets one honest code rather
/// than a leaked exception type.
/// </summary>
public sealed class ExtractionFaultTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_bare_exception_out_of_an_extractor_becomes_6102_and_the_run_completes()
    {
        var original = new InvalidOperationException("boom");
        using var host = await IngestionTestHost.StartAsync(o => o.Extractors.Add(new ThrowingExtractor(original)));
        var source = new RecordingSource().Add("a.txt", "alpha");

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
        var failed = Assert.Single(result.Failures);
        Assert.Equal(IngestionDocumentOutcome.Failed, failed.Outcome);
        Assert.Equal(EdgeErrorCode.ExtractionFailed, failed.Failure!.Code);
        Assert.Equal("throwing", failed.Failure.ExtractorId);
    }

    [Theory]
    [InlineData(EdgeErrorCode.DocumentEncrypted)]
    [InlineData(EdgeErrorCode.DocumentMalformed)]
    [InlineData(EdgeErrorCode.DocumentEncodingUndecodable)]
    [InlineData(EdgeErrorCode.DocumentPageBudgetExceeded)]
    public async Task A_typed_extraction_fault_arrives_unchanged_rather_than_re_wrapped_as_6102(
        EdgeErrorCode code)
    {
        var typed = new EdgeExtractionException(code, "diagnosed") { ExtractorName = "throwing" };
        using var host = await IngestionTestHost.StartAsync(o => o.Extractors.Add(new ThrowingExtractor(typed)));
        var source = new RecordingSource().Add("a.txt", "alpha");

        var result = await host.Pipeline.RunAsync(source, cancellationToken: Token);

        var failed = Assert.Single(result.Failures);
        Assert.Equal(code, failed.Failure!.Code);
    }

    [Fact]
    public async Task Event_907_is_logged_for_the_failed_document()
    {
        using var host = await IngestionTestHost.StartAsync(o =>
            o.Extractors.Add(new ThrowingExtractor(new InvalidOperationException("boom"))));
        var source = new RecordingSource().Add("a.txt", "alpha");

        await host.Pipeline.RunAsync(source, cancellationToken: Token);

        Assert.Contains(host.Logs.Lines, line => line.Contains("a.txt", StringComparison.Ordinal));
    }
}
