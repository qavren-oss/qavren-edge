using System.Runtime.CompilerServices;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// The filesystem shape behind <see cref="IngestionSource.Folder"/> and
/// <see cref="IngestionSource.Files"/>. Ordinal-sorted so a run's document order is stable and a
/// golden over a folder is meaningful; <c>SizeBytes</c> and <c>LastModifiedUtc</c> come from
/// <see cref="FileInfo"/> because they are free there.
/// </summary>
internal sealed class FolderIngestionSource : IngestionSource
{
    private readonly string? _root;
    private readonly string _searchPattern;
    private readonly bool _recursive;
    private readonly IReadOnlyList<string>? _paths;

    private FolderIngestionSource(string id, string? root, string searchPattern, bool recursive, IReadOnlyList<string>? paths)
    {
        Id = id;
        _root = root;
        _searchPattern = searchPattern;
        _recursive = recursive;
        _paths = paths;
    }

    public override string Id { get; }

    public static FolderIngestionSource OverRoot(string path, string searchPattern, bool recursive, string? sourceId)
    {
        var root = System.IO.Path.GetFullPath(path);
        var id = ValidateId(sourceId ?? root);

        // 6051 BEFORE any document: a run over a folder that is not there is meaningless, and
        // discovering it at document zero looks like an empty folder rather than a typo.
        if (!Directory.Exists(root))
        {
            throw new EdgeIngestionException(
                EdgeErrorCode.IngestionSourceUnavailable,
                $"Ingestion source root '{root}' does not exist or is not a directory.")
            {
                SourceId = id,
                Remediation = "Check the path, and that the application has permission to read it.",
            };
        }

        return new FolderIngestionSource(id, root, searchPattern, recursive, paths: null);
    }

    public static FolderIngestionSource OverPaths(IEnumerable<string> paths, string sourceId)
    {
        var id = ValidateId(sourceId);
        var materialised = paths.ToArray();

        foreach (var path in materialised)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new EdgeIngestionException(
                    EdgeErrorCode.IngestionSourceUnavailable,
                    "IngestionSource.Files was given a null, empty or whitespace path.")
                {
                    SourceId = id,
                    Remediation = "Every entry must be a filesystem path.",
                };
            }
        }

        return new FolderIngestionSource(id, root: null, searchPattern: "*", recursive: false, materialised);
    }

    public override async IAsyncEnumerable<DocumentSourceItem> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var item in Enumerate())
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }

        // Enumeration is synchronous: Directory.EnumerateFiles has no async form and a FileInfo
        // read is a stat. The await keeps the iterator an async one without pretending otherwise.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private IEnumerable<DocumentSourceItem> Enumerate()
    {
        if (_paths is not null)
        {
            // Files(): ids are the paths, exactly as handed in.
            foreach (var path in _paths)
            {
                yield return Create(path, documentId: path);
            }

            yield break;
        }

        var root = _root!;
        if (!Directory.Exists(root))
        {
            throw new EdgeIngestionException(
                EdgeErrorCode.IngestionSourceUnavailable,
                $"Ingestion source root '{root}' disappeared between construction and enumeration.")
            {
                SourceId = Id,
                Remediation = "Check the path, and that the application has permission to read it.",
            };
        }

        var option = _recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(root, _searchPattern, option).ToArray();
        Array.Sort(files, StringComparer.Ordinal);

        foreach (var file in files)
        {
            yield return Create(file, DocumentIdFor(root, file));
        }
    }

    private static string DocumentIdFor(string root, string file)
    {
        var relative = System.IO.Path.GetRelativePath(root, file);
        return relative.Replace('\\', '/');
    }

    private static DocumentSourceItem Create(string path, string documentId)
    {
        var info = new FileInfo(path);
        long? size = info.Exists ? info.Length : null;
        DateTimeOffset? modified = info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : null;

        return new DocumentSourceItem(
            documentId,
            IngestionMediaTypes.FromExtension(path),
            _ => ValueTask.FromResult<Stream>(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true)),
            Path: path,
            SizeBytes: size,
            LastModifiedUtc: modified);
    }
}
