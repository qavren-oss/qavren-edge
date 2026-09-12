using System.Diagnostics.CodeAnalysis;

namespace Qavren.Edge.Ingestion;

/// <summary>
/// Turns one source item into one <see cref="ExtractedDocument"/> (spec 7.1). Every extractor opens
/// the source through <see cref="DocumentSourceItem.OpenAsync"/>; <c>File.ReadAllBytes</c> appears
/// nowhere in SP3, and a test asserts it.
/// </summary>
public interface IDocumentExtractor
{
    /// <summary>Stable; feeds the recipe hash.</summary>
    string Id { get; }

    /// <summary>Bump when output changes for identical input.</summary>
    int Version { get; }

    /// <summary>Lower-case, dotted.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>The media types this extractor claims.</summary>
    IReadOnlyList<string> MediaTypes { get; }

    /// <summary>A last word over the registry's media-type and extension match.</summary>
    bool CanExtract(DocumentSourceItem item);

    /// <summary>
    /// Extracts one document. The stream is opened here, not by the caller, and the returned
    /// document's block offsets index <see cref="ExtractedDocument.Text"/>.
    /// </summary>
    ValueTask<ExtractedDocument> ExtractAsync(
        DocumentSourceItem item, ExtractionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves an extractor for one source item. Consumer registrations sit ahead of the built-ins,
/// so <c>.md</c> can be overridden (spec 7.1).
/// </summary>
public interface IDocumentExtractorRegistry
{
    /// <summary>Consumer registrations first, then the built-ins, in registration order.</summary>
    IReadOnlyList<IDocumentExtractor> Extractors { get; }

    /// <summary>Media type first, then extension. False means 6101 for this document.</summary>
    bool TryResolve(DocumentSourceItem item, [MaybeNullWhen(false)] out IDocumentExtractor extractor);

    /// <summary>"text:1, markdown:1, pdf:1" — the diagnostics string, not the recipe (spec 7.1).</summary>
    string Describe();
}
