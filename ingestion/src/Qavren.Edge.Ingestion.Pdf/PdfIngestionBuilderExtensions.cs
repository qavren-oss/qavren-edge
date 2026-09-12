using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Ingestion.Pdf;

/// <summary>The one builder call <c>Qavren.Edge.Ingestion.Pdf</c> publishes (spec 11).</summary>
public static class PdfIngestionBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="PdfTextExtractor"/> (Id <c>"pdf"</c>) and its
    /// <see cref="PdfExtractorOptions"/> (spec 7.4). Idempotent: a second call re-applies
    /// <paramref name="configure"/> to the same options instance and registers no second extractor,
    /// so it does NOT trip <see cref="EdgeErrorCode.IngestionDuplicateExtractorId"/> (6004) — that
    /// code is for two DIFFERENT registrations sharing an Id. Order-independent relative to
    /// <c>AddIngestion</c>, because the registry is composed at resolve time. A consumer who builds
    /// <c>new PdfTextExtractor(options)</c> by hand and passes it to <c>AddDocumentExtractor</c>
    /// lands in the same registry and bypasses nothing.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="configure">Configures the options; applied on every call, to one instance.</param>
    /// <returns>The builder, for chaining.</returns>
    public static EdgeBuilder AddPdfExtractor(this EdgeBuilder builder, Action<PdfExtractorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The options singleton is the idempotence marker: it is registered as an INSTANCE, so a
        // second call can find it in the descriptor list and re-apply configure to the same object.
        var existing = builder.Services
            .FirstOrDefault(d => d.ServiceType == typeof(PdfExtractorOptions))?
            .ImplementationInstance as PdfExtractorOptions;

        if (existing is not null)
        {
            configure?.Invoke(existing);
            return builder;
        }

        var options = new PdfExtractorOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        // The same DI channel AddDocumentExtractor uses, and the one BuildRegistry reads.
        return builder.AddDocumentExtractor(sp => new PdfTextExtractor(sp.GetRequiredService<PdfExtractorOptions>()));
    }
}
