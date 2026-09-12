using System.Diagnostics.CodeAnalysis;
using Qavren.Edge.Ingestion.Internal;

namespace Qavren.Edge.Ingestion;

/// <summary>
/// Where a run's documents come from. The abstract shape is two members; the five factories below
/// cover the filesystem shape and the mobile shape (spec 11, 11.3).
/// </summary>
public abstract class IngestionSource
{
    /// <summary>Stable, non-empty. Every state row and every chunk carries it.</summary>
    public abstract string Id { get; }

    /// <summary>Streams the source's items. Enumeration order is the run's document order.</summary>
    public abstract IAsyncEnumerable<DocumentSourceItem> EnumerateAsync(CancellationToken ct = default);

    /// <summary>
    /// FILESYSTEM PATHS ONLY. Ordinal-sorted; document ids are forward-slashed paths relative to
    /// the root. This is the desktop and app-private-storage shape — see spec 11.3 for mobile.
    /// </summary>
    /// <exception cref="EdgeIngestionException">
    /// 6051 when the root is missing or unreadable — raised here, before any document, because a
    /// run over a folder that is not there is meaningless. 6055 for an empty source id.
    /// </exception>
    public static IngestionSource Folder(
        string path, string searchPattern = "*", bool recursive = true, string? sourceId = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchPattern);

        return FolderIngestionSource.OverRoot(path, searchPattern, recursive, sourceId);
    }

    /// <summary>Filesystem paths only, same caveat as <see cref="Folder"/>.</summary>
    public static IngestionSource Files(IEnumerable<string> paths, string sourceId)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return FolderIngestionSource.OverPaths(paths, sourceId);
    }

    /// <summary>
    /// THE MOBILE SHAPE. Opaque handles: a SAF content:// URI, a security-scoped bookmark, a
    /// MAUI FileResult, a blob in app storage. SP3 never interprets the handle; the consumer's
    /// <see cref="DocumentSourceItem.OpenAsync"/> does. See spec 11.3.
    /// </summary>
    public static IngestionSource Items(IEnumerable<DocumentSourceItem> items, string sourceId)
    {
        ArgumentNullException.ThrowIfNull(items);

        return ItemsIngestionSource.Over(items, sourceId);
    }

    /// <summary>Streaming form, for a picker that pages or a provider that enumerates lazily.</summary>
    public static IngestionSource Items(
        Func<CancellationToken, IAsyncEnumerable<DocumentSourceItem>> enumerate, string sourceId)
    {
        ArgumentNullException.ThrowIfNull(enumerate);

        return ItemsIngestionSource.Over(enumerate, sourceId);
    }

    /// <summary>One document, opened by a consumer-supplied delegate.</summary>
    [SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification =
            "Spec 11 declares this factory as IngestionSource.Single and the name is part of the published API. " +
            "'Single' here is a cardinality, not System.Single, and renaming it would break the spec's surface.")]
    public static IngestionSource Single(
        string documentId,
        string mediaType,
        Func<CancellationToken, ValueTask<Stream>> openAsync,
        string sourceId,
        long? sizeBytes = null,
        DateTimeOffset? lastModifiedUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(mediaType);
        ArgumentNullException.ThrowIfNull(openAsync);

        var item = new DocumentSourceItem(documentId, mediaType, openAsync)
        {
            SizeBytes = sizeBytes,
            LastModifiedUtc = lastModifiedUtc,
        };

        return ItemsIngestionSource.Over([item], sourceId);
    }

    /// <summary>
    /// The one validation every source shares. A null, empty or whitespace id is 6055, raised at
    /// construction rather than at the first document.
    /// </summary>
    internal static string ValidateId(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            throw new EdgeIngestionException(
                EdgeErrorCode.IngestionSourceIdInvalid,
                "An ingestion source id must be a non-empty, non-whitespace string.")
            {
                Remediation = "Pass a stable source id - every state row and every stored chunk carries it.",
            };
        }

        return sourceId;
    }
}

/// <summary>One item a source yields. SP3 never interprets <see cref="Path"/>.</summary>
/// <param name="DocumentId">Stable within the source. The state row's key, with the source id.</param>
/// <param name="MediaType">Drives registry resolution before the extension does.</param>
/// <param name="OpenAsync">
/// Called TWICE per document (hash pass, then extraction pass) and so MUST be re-openable.
/// SHOULD return a seekable stream at position 0; a non-seekable one is buffered once below
/// <see cref="ExtractionOptions.NonSeekableBufferLimitBytes"/> and raises 6053 above it (spec 7.1).
/// </param>
/// <param name="Path">The originating path or handle, for diagnostics only.</param>
/// <param name="SizeBytes">Declared size, when the source knows it for free.</param>
/// <param name="LastModifiedUtc">Declared mtime, when the source knows it for free.</param>
/// <param name="Metadata">Extra per-document facts a consumer wants carried through.</param>
public sealed record DocumentSourceItem(
    string DocumentId,
    string MediaType,
    Func<CancellationToken, ValueTask<Stream>> OpenAsync,
    string? Path = null,
    long? SizeBytes = null,
    DateTimeOffset? LastModifiedUtc = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
