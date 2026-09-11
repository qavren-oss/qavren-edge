using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Ingestion.OpenXml;

/// <summary>The one builder call <c>Qavren.Edge.Ingestion.OpenXml</c> publishes (spec 11).</summary>
public static class OpenXmlIngestionBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="DocxTextExtractor"/> (Id <c>"docx"</c>) and its
    /// <see cref="DocxExtractorOptions"/> (spec 7.5). Idempotent and order-independent on the same
    /// terms as <c>AddPdfExtractor</c>: a second call re-applies <paramref name="configure"/> to the
    /// same options instance and registers no second extractor, and the registry is composed at
    /// resolve time so this may precede or follow <c>AddIngestion</c>.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="configure">Configures the options; applied on every call, to one instance.</param>
    /// <returns>The builder, for chaining.</returns>
    public static EdgeBuilder AddDocxExtractor(this EdgeBuilder builder, Action<DocxExtractorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var existing = builder.Services
            .FirstOrDefault(d => d.ServiceType == typeof(DocxExtractorOptions))?
            .ImplementationInstance as DocxExtractorOptions;

        if (existing is not null)
        {
            configure?.Invoke(existing);
            return builder;
        }

        var options = new DocxExtractorOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        return builder.AddDocumentExtractor(sp => new DocxTextExtractor(sp.GetRequiredService<DocxExtractorOptions>()));
    }
}
