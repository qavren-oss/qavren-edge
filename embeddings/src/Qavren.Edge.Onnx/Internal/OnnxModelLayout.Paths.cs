namespace Qavren.Edge.Onnx.Internal;

/// <summary>
/// The path rules of the model layout: where a manifest's directory is, where one of its files
/// lands, what a download in flight is called, and how much room the volume holding it has left.
/// </summary>
/// <remarks>
/// <b>This file is compiled twice.</b> <c>Qavren.Edge.Onnx</c> owns it and
/// <c>Qavren.Edge.Chat.Onnx</c> links it (a <c>Compile Include</c> in its project file), because
/// the chat provisioner has to answer "is it there, and how much is left to fetch?" from a stat,
/// before the store is asked for anything. A linked source file rather than an
/// <c>InternalsVisibleTo</c> grant: the two packages version independently on nuget.org, and a
/// grant would turn every member here into a binary contract between them that no package
/// validation checks. Everything in this half is therefore a pure function of its arguments and
/// touches nothing but <see cref="System.IO"/>.
/// </remarks>
internal static partial class OnnxModelLayout
{
    /// <summary>What a file is called until its digest has been checked.</summary>
    public const string PartSuffix = ".part";

    /// <summary>The manifest entry ORT is handed.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The graph file entry.</returns>
    public static OnnxModelFile Graph(OnnxModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        foreach (var file in manifest.Files)
        {
            if (string.Equals(file.RelativePath, manifest.GraphFile, StringComparison.Ordinal))
            {
                return file;
            }
        }

        throw new ArgumentException(
            $"Manifest '{manifest.ModelId}' names GraphFile '{manifest.GraphFile}', which is not one of its Files. " +
            "The graph must be listed like every other file, because its declared SHA-256 is what the model " +
            "directory and the CoreML cache key are named after.",
            nameof(manifest));
    }

    /// <summary>The first 16 hex characters of the graph's digest: the content-addressed directory name.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The 16-character directory segment.</returns>
    public static string Sha16(OnnxModelManifest manifest)
    {
        var sha = Graph(manifest).Sha256;
        return sha.Length <= 16 ? sha : sha[..16];
    }

    /// <summary><c>&lt;root&gt;/&lt;modelId&gt;</c>: everything one model id owns under a root.</summary>
    /// <param name="root">The model root, or the ORT cache root.</param>
    /// <param name="modelId">The model id.</param>
    /// <returns>The absolute directory.</returns>
    /// <exception cref="ArgumentException">The id resolves to the root itself or to somewhere outside it.</exception>
    public static string ModelRoot(string root, string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        return Contained(root, Path.Combine(root, modelId), "Model id", modelId);
    }

    /// <summary><c>&lt;Models&gt;/&lt;modelId&gt;/&lt;sha16&gt;</c>.</summary>
    /// <param name="models">The model root.</param>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The absolute model directory.</returns>
    /// <exception cref="ArgumentException">The manifest's model id resolves outside the model root.</exception>
    public static string DirectoryFor(string models, OnnxModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Path.Combine(ModelRoot(models, manifest.ModelId), Sha16(manifest));
    }

    /// <summary>The absolute path of one manifest-relative file.</summary>
    /// <param name="directory">The model directory.</param>
    /// <param name="relativePath">The manifest-relative path, forward-slashed.</param>
    /// <returns>The absolute path.</returns>
    /// <exception cref="ArgumentException">The path is rooted, or climbs out of the directory.</exception>
    public static string FilePath(string directory, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        return Contained(
            directory,
            Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            "Manifest path",
            relativePath);
    }

    /// <summary>Where a file's bytes are staged until they have been verified.</summary>
    /// <param name="destination">The file's final absolute path.</param>
    /// <returns>The staging path beside it, so the final rename never crosses a volume.</returns>
    public static string PartPath(string destination) => destination + PartSuffix;

    /// <summary>
    /// How many of a file's bytes are on disk already, without hashing anything: the whole file once
    /// it is in place at its declared length, otherwise whatever its staging file holds.
    /// </summary>
    /// <param name="directory">The model directory.</param>
    /// <param name="file">The manifest entry.</param>
    /// <returns>Between zero and <see cref="OnnxModelFile.SizeBytes"/>.</returns>
    public static long BytesOnDisk(string directory, OnnxModelFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var destination = FilePath(directory, file.RelativePath);
        var placed = new FileInfo(destination);
        if (placed.Exists && placed.Length == file.SizeBytes)
        {
            return file.SizeBytes;
        }

        var staged = new FileInfo(PartPath(destination));
        return staged.Exists ? Math.Min(staged.Length, file.SizeBytes) : 0;
    }

    /// <summary>Free bytes on the volume that holds a directory, or null for "unknown".</summary>
    /// <param name="directory">The directory. It need not exist yet.</param>
    /// <returns>The free byte count, or null when the platform will not say.</returns>
    /// <remarks>
    /// The volume is found from the directory itself, never from <see cref="Path.GetPathRoot(string)"/>
    /// of it. On every Unix that root is <c>/</c> whatever the path, and <c>/</c> is the read-only
    /// system image on Android and the sealed system volume on Apple platforms - not the data
    /// partition the models are written to. <see cref="DriveInfo"/> stats the path it is given there,
    /// and on Windows takes the drive letter off it.
    /// </remarks>
    public static long? FreeSpace(string directory)
    {
        try
        {
            // statfs wants a path that exists, and the model directory is created on first provisioning.
            var existing = Path.GetFullPath(directory);
            while (!Directory.Exists(existing))
            {
                var parent = Path.GetDirectoryName(existing);
                if (string.IsNullOrEmpty(parent))
                {
                    return null;
                }

                existing = parent;
            }

            return new DriveInfo(existing).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            // Unknown is never a refusal: a platform that will not report free space is not a
            // platform that is out of space.
            return null;
        }
    }

    /// <summary>
    /// Refuses a path that is not strictly inside its root. A model id or a manifest path is data -
    /// an app may well read manifests from a catalogue it downloaded - and <c>..</c> or a rooted path
    /// in either one would otherwise have provisioning write, and removal recursively delete,
    /// wherever it pointed.
    /// </summary>
    private static string Contained(string root, string candidate, string what, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var fullRoot = Path.GetFullPath(root);
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidate);

        // Ordinal on purpose: both sides were normalised from the same root string a moment ago.
        if (full.Length > prefix.Length && full.StartsWith(prefix, StringComparison.Ordinal))
        {
            return candidate;
        }

        throw new ArgumentException(
            $"{what} '{value}' does not resolve to a location inside '{root}'. Model ids and manifest paths " +
            "are relative names: no rooted paths, and no '..' segments that climb out.",
            nameof(candidate));
    }
}
