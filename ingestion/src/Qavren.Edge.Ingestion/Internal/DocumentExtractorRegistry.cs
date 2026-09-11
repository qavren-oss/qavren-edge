using System.Diagnostics.CodeAnalysis;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// Spec 7.1's registry. Resolves by media type first, then by extension, lower-case and dotted.
/// Consumer-registered extractors sit ahead of the built-ins, so <c>.md</c> can be overridden.
/// </summary>
internal sealed class DocumentExtractorRegistry : IDocumentExtractorRegistry
{
    private readonly IDocumentExtractor[] _extractors;

    /// <param name="consumerExtractors">Registration order preserved; these win.</param>
    /// <param name="builtIns">Null means the core's own pair: text, then markdown.</param>
    /// <exception cref="EdgeConfigurationException">
    /// 6004 when two extractors share an <see cref="IDocumentExtractor.Id"/>, raised AT
    /// REGISTRATION rather than at the first document.
    /// </exception>
    public DocumentExtractorRegistry(
        IEnumerable<IDocumentExtractor>? consumerExtractors = null,
        IEnumerable<IDocumentExtractor>? builtIns = null)
    {
        var ordered = new List<IDocumentExtractor>();
        if (consumerExtractors is not null)
        {
            ordered.AddRange(consumerExtractors);
        }

        ordered.AddRange(builtIns ?? DefaultBuiltIns());

        var seen = new Dictionary<string, IDocumentExtractor>(StringComparer.Ordinal);
        foreach (var extractor in ordered)
        {
            ArgumentNullException.ThrowIfNull(extractor);

            if (!seen.TryAdd(extractor.Id, extractor))
            {
                throw new EdgeConfigurationException(
                    EdgeErrorCode.IngestionDuplicateExtractorId,
                    $"Two document extractors are registered with the id '{extractor.Id}': " +
                    $"{seen[extractor.Id].GetType().FullName} and {extractor.GetType().FullName}. " +
                    "The id feeds the recipe hash, so it has to identify exactly one implementation.");
            }
        }

        _extractors = [.. ordered];
    }

    public IReadOnlyList<IDocumentExtractor> Extractors => _extractors;

    public bool TryResolve(DocumentSourceItem item, [MaybeNullWhen(false)] out IDocumentExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Media type first.
        foreach (var candidate in _extractors)
        {
            if (Matches(candidate.MediaTypes, item.MediaType) && candidate.CanExtract(item))
            {
                extractor = candidate;
                return true;
            }
        }

        // Then extension, lower-case and dotted.
        var extension = IngestionMediaTypes.ExtensionOf(item.Path ?? item.DocumentId);
        if (extension.Length != 0)
        {
            foreach (var candidate in _extractors)
            {
                if (Matches(candidate.Extensions, extension) && candidate.CanExtract(item))
                {
                    extractor = candidate;
                    return true;
                }
            }
        }

        extractor = null;
        return false;
    }

    public string Describe() => string.Join(", ", _extractors.Select(e => $"{e.Id}:{e.Version}"));

    /// <summary>
    /// The per-document 6101 an unresolved item becomes. Not thrown here: spec 7.1 reports it
    /// against the document and lets the run continue.
    /// </summary>
    public IngestionFailure NotFound(DocumentSourceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var extension = IngestionMediaTypes.ExtensionOf(item.Path ?? item.DocumentId);
        var remediation = extension switch
        {
            ".pdf" =>
                "PDF extraction is an opt-in satellite: add a reference to Qavren.Edge.Ingestion.Pdf and call " +
                "AddPdfExtractor() on the builder.",
            ".docx" =>
                "DOCX extraction is an opt-in satellite: add a reference to Qavren.Edge.Ingestion.OpenXml and call " +
                "AddDocxExtractor() on the builder.",
            _ =>
                "Register an IDocumentExtractor for this format with AddDocumentExtractor(), or exclude the file from " +
                "the source's search pattern. Registered extractors: " + Describe() + ".",
        };

        return new IngestionFailure(
            EdgeErrorCode.ExtractorNotFound,
            $"No document extractor accepted '{item.DocumentId}' (media type '{item.MediaType}', extension " +
            $"'{(extension.Length == 0 ? "(none)" : extension)}').",
            ExtractorId: null,
            remediation);
    }

    private static IEnumerable<IDocumentExtractor> DefaultBuiltIns()
    {
        yield return new PlainTextExtractor();
        yield return new MarkdownExtractor();
    }

    private static bool Matches(IReadOnlyList<string> values, string candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
