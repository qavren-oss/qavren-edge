using System.Runtime.CompilerServices;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>A sub-directory the walk did not enter.</summary>
/// <param name="Path">Its full path.</param>
/// <param name="DocumentIdPrefix">
/// The prefix every document id under it carries: its forward-slashed path relative to the root,
/// with a trailing slash.
/// </param>
/// <param name="Error">
/// Why it could not be listed, or <see langword="null"/> when it was not entered because it is a
/// symbolic link or a junction.
/// </param>
internal sealed record SkippedDirectory(string Path, string DocumentIdPrefix, Exception? Error);

/// <summary>
/// The three questions the walk asks of the filesystem, as a seam: a directory that refuses to be
/// listed cannot be staged the same way on every platform a test lane runs on, and a link cannot be
/// created at all without a privilege a build agent may not hold.
/// </summary>
/// <param name="Files">The files directly inside a directory that match a search pattern.</param>
/// <param name="Directories">The directories directly inside a directory.</param>
/// <param name="IsLink">True when a directory is a symbolic link or a junction.</param>
internal sealed record FolderReads(
    Func<string, string, IEnumerable<string>> Files,
    Func<string, IEnumerable<string>> Directories,
    Func<string, bool> IsLink)
{
    /// <summary>The real filesystem.</summary>
    public static FolderReads FileSystem { get; } = new(
        static (directory, pattern) => Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly),
        static directory => Directory.EnumerateDirectories(directory),
        IsLinkedDirectory);

    /// <summary>
    /// A link, not merely a reparse point. Cloud-storage placeholders - a OneDrive folder that has
    /// not been hydrated - carry the reparse attribute too and are ordinary directories to anything
    /// that reads them; <see cref="FileSystemInfo.LinkTarget"/> is non-null only for a symbolic
    /// link or a junction, which are the two that can point somewhere else.
    /// </summary>
    private static bool IsLinkedDirectory(string directory)
    {
        var info = new DirectoryInfo(directory);
        return (info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null;
    }
}

/// <summary>
/// The filesystem shape behind <see cref="IngestionSource.Folder"/> and
/// <see cref="IngestionSource.Files"/>. Ordinal-sorted so a run's document order is stable and a
/// golden over a folder is meaningful; <c>SizeBytes</c> and <c>LastModifiedUtc</c> come from
/// <see cref="FileInfo"/> because they are free there.
/// </summary>
/// <remarks>
/// <para>
/// The tree is walked a directory at a time rather than handed to
/// <c>Directory.EnumerateFiles(..., AllDirectories)</c>, for two things that call cannot do. It
/// follows every symbolic link and junction it meets, so a link back up the tree is a walk that
/// ends in a path-too-long fault after indexing the same files under dozens of ids, and a link out
/// of the tree indexes documents nobody chose. And it stops at the first directory it cannot list,
/// which turned one locked sub-folder into a failed run.
/// </para>
/// <para>
/// So: a sub-directory that is a link is NOT entered, and one that cannot be listed is skipped;
/// both are reported to whoever is enumerating. The root is entered whatever it is - it was named
/// outright - and a failure to list the ROOT still propagates, because a run over a folder that
/// cannot be read is meaningless. A symbolic link to a FILE is still yielded and still opens its
/// target, as it always has: it cannot loop, and it is one document somebody put in the folder.
/// </para>
/// </remarks>
internal sealed class FolderIngestionSource : IngestionSource
{
    private readonly string? _root;
    private readonly string _searchPattern;
    private readonly bool _recursive;
    private readonly IReadOnlyList<string>? _paths;
    private readonly FolderReads _reads;

    private FolderIngestionSource(
        string id, string? root, string searchPattern, bool recursive, IReadOnlyList<string>? paths, FolderReads reads)
    {
        Id = id;
        _root = root;
        _searchPattern = searchPattern;
        _recursive = recursive;
        _paths = paths;
        _reads = reads;
    }

    public override string Id { get; }

    public static FolderIngestionSource OverRoot(
        string path, string searchPattern, bool recursive, string? sourceId, FolderReads? reads = null)
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

        return new FolderIngestionSource(id, root, searchPattern, recursive, paths: null, reads ?? FolderReads.FileSystem);
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

        return new FolderIngestionSource(
            id, root: null, searchPattern: "*", recursive: false, materialised, FolderReads.FileSystem);
    }

    public override IAsyncEnumerable<DocumentSourceItem> EnumerateAsync(CancellationToken ct = default) =>
        EnumerateAsync(skipped: null, ct);

    /// <summary>
    /// The enumeration, telling <paramref name="skipped"/> about each sub-directory it did not
    /// enter. The runner listens: it logs them, and it keeps the documents under an UNREADABLE one
    /// out of the sweep, because not being able to look is not evidence they are gone.
    /// </summary>
    internal async IAsyncEnumerable<DocumentSourceItem> EnumerateAsync(
        Action<SkippedDirectory>? skipped, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var item in Enumerate(skipped))
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }

        // Enumeration is synchronous: Directory.EnumerateFiles has no async form and a FileInfo
        // read is a stat. The await keeps the iterator an async one without pretending otherwise.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private IEnumerable<DocumentSourceItem> Enumerate(Action<SkippedDirectory>? skipped)
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

        var files = Walk(root, skipped);
        files.Sort(StringComparer.Ordinal);

        foreach (var file in files)
        {
            yield return Create(file, DocumentIdFor(root, file));
        }
    }

    private List<string> Walk(string root, Action<SkippedDirectory>? skipped)
    {
        // Directory.EnumerateFiles lets a pattern carry a directory part ("drafts/*.md") and moves
        // it onto the path before it starts. The same split here keeps such a pattern meaning what
        // it meant.
        var start = root;
        var pattern = _searchPattern;
        var directoryPart = System.IO.Path.GetDirectoryName(pattern);
        if (!string.IsNullOrEmpty(directoryPart))
        {
            start = System.IO.Path.Join(root, directoryPart);
            pattern = pattern[(directoryPart.Length + 1)..];
        }

        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(start);

        while (pending.TryPop(out var directory))
        {
            string[] found;
            string[] children;
            try
            {
                found = [.. _reads.Files(directory, pattern)];
                children = _recursive ? [.. _reads.Directories(directory)] : [];
            }
            catch (Exception ex) when (directory != start && ex is UnauthorizedAccessException or IOException)
            {
                // Refused, or gone since its parent was listed. Either way this run did not see
                // what is in it.
                skipped?.Invoke(new SkippedDirectory(directory, DocumentIdFor(root, directory) + "/", ex));
                continue;
            }

            files.AddRange(found);

            foreach (var child in children)
            {
                bool link;
                try
                {
                    link = _reads.IsLink(child);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    skipped?.Invoke(new SkippedDirectory(child, DocumentIdFor(root, child) + "/", ex));
                    continue;
                }

                if (link)
                {
                    skipped?.Invoke(new SkippedDirectory(child, DocumentIdFor(root, child) + "/", Error: null));
                    continue;
                }

                pending.Push(child);
            }
        }

        return files;
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
