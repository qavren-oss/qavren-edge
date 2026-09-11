using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Qavren.Edge.Ingestion.OpenXml;
using Qavren.Edge.Ingestion.Pdf;
using Qavren.Edge.Sqlite;
using Xunit;

namespace Qavren.Edge.Ingestion.Extractors.Tests;

/// <summary>
/// Spec 11's registration contract for both satellites: idempotent, order-independent relative to
/// <c>AddIngestion</c>, and a hand-built extractor passed to <c>AddDocumentExtractor</c> lands in
/// the same registry. The container is built but never started — the registry is composed at
/// resolve time from descriptors, so no database is opened.
/// </summary>
public class SatelliteRegistrationTests
{
    [Fact]
    public void AddPdfExtractorTwiceRegistersOneExtractorAndReappliesConfigure()
    {
        using var provider = Build(edge => edge
            .AddIngestion(1)
            .AddPdfExtractor(o => o.PageBudget = TimeSpan.FromSeconds(5))
            .AddPdfExtractor(o => o.MaxStackDepth = 7));

        var extractors = provider.GetServices<IDocumentExtractor>().ToList();
        var pdf = Assert.Single(extractors.OfType<PdfTextExtractor>());
        Assert.Same(pdf, Assert.Single(extractors));

        var options = provider.GetRequiredService<PdfExtractorOptions>();
        Assert.Equal(TimeSpan.FromSeconds(5), options.PageBudget);
        Assert.Equal(7, options.MaxStackDepth);

        var registry = provider.GetRequiredService<IDocumentExtractorRegistry>();
        Assert.Equal("pdf:1, text:1, markdown:1", registry.Describe());
    }

    [Fact]
    public void AddDocxExtractorTwiceRegistersOneExtractorAndReappliesConfigure()
    {
        using var provider = Build(edge => edge
            .AddIngestion(1)
            .AddDocxExtractor(o => o.IncludeNotes = false)
            .AddDocxExtractor(o => o.StreamingThresholdBytes = 2L * 1024 * 1024));

        var extractors = provider.GetServices<IDocumentExtractor>().ToList();
        Assert.Single(extractors.OfType<DocxTextExtractor>());
        Assert.Single(extractors);

        var options = provider.GetRequiredService<DocxExtractorOptions>();
        Assert.False(options.IncludeNotes);
        Assert.Equal(2L * 1024 * 1024, options.StreamingThresholdBytes);

        Assert.Equal("docx:1, text:1, markdown:1", provider.GetRequiredService<IDocumentExtractorRegistry>().Describe());
    }

    [Fact]
    public void BothSatellitesRegisterBeforeOrAfterAddIngestion()
    {
        using var after = Build(edge => edge.AddIngestion(1).AddPdfExtractor().AddDocxExtractor());
        using var before = Build(edge => edge.AddPdfExtractor().AddDocxExtractor().AddIngestion(1));

        Assert.Equal("pdf:1, docx:1, text:1, markdown:1", after.GetRequiredService<IDocumentExtractorRegistry>().Describe());
        Assert.Equal("pdf:1, docx:1, text:1, markdown:1", before.GetRequiredService<IDocumentExtractorRegistry>().Describe());
    }

    [Fact]
    public void TheRegistryResolvesAPdfAndADocxToTheSatellites()
    {
        using var provider = Build(edge => edge.AddIngestion(1).AddPdfExtractor().AddDocxExtractor());
        var registry = provider.GetRequiredService<IDocumentExtractorRegistry>();

        Assert.True(registry.TryResolve(Item("report.pdf"), out var pdf));
        Assert.IsType<PdfTextExtractor>(pdf);

        Assert.True(registry.TryResolve(Item("memo.docx"), out var docx));
        Assert.IsType<DocxTextExtractor>(docx);

        // By media type alone, with an extension that says nothing.
        Assert.True(registry.TryResolve(Item("blob", IngestionMediaTypes.Pdf), out var byMediaType));
        Assert.IsType<PdfTextExtractor>(byMediaType);
    }

    [Fact]
    public void AHandBuiltExtractorPassedToAddDocumentExtractorLandsInTheSameRegistry()
    {
        var mine = new PdfTextExtractor(new PdfExtractorOptions { ReadingOrder = PdfReadingOrderMode.Layout });
        using var provider = Build(edge => edge.AddIngestion(1).AddDocumentExtractor(mine));

        var registry = provider.GetRequiredService<IDocumentExtractorRegistry>();
        Assert.True(registry.TryResolve(Item("report.pdf"), out var resolved));
        Assert.Same(mine, resolved);
        Assert.Equal("pdf:1, text:1, markdown:1", registry.Describe());
    }

    [Fact]
    public void AHandBuiltExtractorAndAddPdfExtractorTogetherIsSixZeroZeroFour()
    {
        // Two DIFFERENT registrations sharing the id "pdf" - which is what 6004 is for, and what
        // AddPdfExtractor's idempotence is NOT.
        using var provider = Build(edge => edge.AddIngestion(1).AddDocumentExtractor(new PdfTextExtractor()).AddPdfExtractor());

        var thrown = Assert.Throws<EdgeConfigurationException>(() => provider.GetRequiredService<IDocumentExtractorRegistry>());
        Assert.Equal(EdgeErrorCode.IngestionDuplicateExtractorId, thrown.Code);
    }

    private static ServiceProvider Build(Action<EdgeBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(edge =>
        {
            edge.AddSqlite(o => o.DatabaseName = "registration-tests.db");
            configure(edge);
        });

        return services.BuildServiceProvider();
    }

    private static DocumentSourceItem Item(string documentId, string? mediaType = null) =>
        new(
            documentId,
            mediaType ?? IngestionMediaTypes.FromExtension(documentId),
            _ => ValueTask.FromResult(Stream.Null),
            Path: documentId);
}
