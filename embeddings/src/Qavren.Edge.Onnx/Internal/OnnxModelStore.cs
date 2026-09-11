using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Qavren.Edge.Onnx.Internal;

/// <summary>
/// The on-disk layout every model source writes into, and the marker every source's output is
/// described by. It lives beside the store rather than in its own file because the store is the
/// only writer of the marker and the sources are the only writers of the files - splitting the two
/// halves of one invariant across two files is how they drift.
/// </summary>
internal static class OnnxModelLayout
{
    /// <summary>The provisioning marker's filename.</summary>
    public const string MarkerFileName = ".qavren-model.json";

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

    /// <summary><c>&lt;Models&gt;/&lt;modelId&gt;/&lt;sha16&gt;</c>.</summary>
    /// <param name="models">The model root.</param>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The absolute model directory.</returns>
    public static string DirectoryFor(string models, OnnxModelManifest manifest)
        => Path.Combine(models, manifest.ModelId, Sha16(manifest));

    /// <summary>The absolute path of one manifest-relative file.</summary>
    /// <param name="directory">The model directory.</param>
    /// <param name="relativePath">The manifest-relative path, forward-slashed.</param>
    /// <returns>The absolute path.</returns>
    public static string FilePath(string directory, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>Whether every file exists at exactly its declared length.</summary>
    /// <param name="directory">The model directory.</param>
    /// <param name="manifest">The manifest.</param>
    /// <returns>True when every length matches.</returns>
    public static bool LengthsMatch(string directory, OnnxModelManifest manifest)
    {
        foreach (var file in manifest.Files)
        {
            var info = new FileInfo(FilePath(directory, file.RelativePath));
            if (!info.Exists || info.Length != file.SizeBytes)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Manifest-relative path to absolute path, for every file.</summary>
    /// <param name="directory">The model directory.</param>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The resolved map.</returns>
    public static Dictionary<string, string> Resolve(string directory, OnnxModelManifest manifest)
    {
        var resolved = new Dictionary<string, string>(manifest.Files.Count, StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            resolved[file.RelativePath] = FilePath(directory, file.RelativePath);
        }

        return resolved;
    }

    /// <summary>
    /// Hashes the file on disk and compares it to the manifest. The digest is always taken from
    /// the completed file, never from an in-flight buffer.
    /// </summary>
    /// <param name="path">The absolute path to verify.</param>
    /// <param name="file">The manifest entry.</param>
    /// <param name="modelId">The model id, for the exception.</param>
    /// <param name="sourceUri">Where the bytes came from, when they came from somewhere.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The lowercase-hex digest.</returns>
    public static async Task<string> VerifyAsync(
        string path,
        OnnxModelFile file,
        string modelId,
        Uri? sourceUri,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        var actual = await HashAsync(path, cancellationToken).ConfigureAwait(false);
        if (string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return actual;
        }

        var length = new FileInfo(path).Length;
        throw new EdgeModelProvisioningException(
            EdgeErrorCode.ModelHashMismatch,
            modelId,
            file.RelativePath,
            $"'{file.RelativePath}' hashes to {actual} but the manifest declares {file.Sha256}. " +
            "The file was deleted rather than used: an inference session created over the wrong bytes " +
            "produces vectors that are silently incomparable with every vector already in the store.")
        {
            ExpectedSha256 = file.Sha256,
            ActualSha256 = actual,
            ExpectedBytes = file.SizeBytes,
            ActualBytes = length,
            SourceUri = sourceUri,
        };
    }

    /// <summary>SHA-256 of a file on disk, lowercase hex.</summary>
    /// <param name="path">The absolute path.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The lowercase-hex digest.</returns>
    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexStringLower(digest);
        }
    }
}

/// <summary>One file, as the provisioning marker records it.</summary>
/// <param name="RelativePath">The manifest-relative path.</param>
/// <param name="Role">The file's role.</param>
/// <param name="SizeBytes">The declared size.</param>
/// <param name="Sha256">The declared digest.</param>
/// <param name="Path">The absolute path it landed at.</param>
internal sealed record ModelMarkerFile(
    string RelativePath,
    OnnxModelFileRole Role,
    long SizeBytes,
    string Sha256,
    string Path);

/// <summary>
/// <c>&lt;modelId&gt;/&lt;sha16&gt;/.qavren-model.json</c>: each file's expected size and SHA-256,
/// the resolved paths, the total bytes and the timestamp.
/// </summary>
internal sealed record ModelMarker
{
    /// <summary>The manifest's model id.</summary>
    public required string ModelId { get; init; }

    /// <summary>The manifest-relative graph path.</summary>
    public required string GraphFile { get; init; }

    /// <summary>The graph's lowercase-hex digest.</summary>
    public required string GraphSha256 { get; init; }

    /// <summary>The sum of every file's declared size.</summary>
    public required long TotalSizeBytes { get; init; }

    /// <summary>When provisioning completed.</summary>
    public required DateTimeOffset ProvisionedAtUtc { get; init; }

    /// <summary>Every file the manifest declared.</summary>
    public required IReadOnlyList<ModelMarkerFile> Files { get; init; }
}

/// <summary>
/// Source-generated, not reflection-based: this package is AOT- and trim-safe, and a
/// <c>JsonSerializer.Serialize(object)</c> call would be the one thing in it that is not.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ModelMarker))]
internal sealed partial class ModelMarkerJsonContext : JsonSerializerContext;

/// <summary>
/// Spec 6.3's store. Owns the layout, the marker, the per-model provisioning lock and the CoreML
/// cache purge; the sources own nothing but the bytes.
/// </summary>
internal sealed class OnnxModelStore : IOnnxModelStore, IDisposable
{
    private static readonly Action<ILogger, string, string, long, Exception?> s_provisioned =
        LoggerMessage.Define<string, string, long>(
            LogLevel.Information,
            new EventId(EdgeAiEventIds.ModelProvisioned, nameof(EdgeAiEventIds.ModelProvisioned)),
            "Model {ModelId} is provisioned from {Source} ({Bytes} bytes) and verified.");

    private static readonly Action<ILogger, string, string, Exception?> s_ortCachePurged =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(EdgeAiEventIds.OrtCachePurged, nameof(EdgeAiEventIds.OrtCachePurged)),
            "Purged the ORT cache subtree for model {ModelId} at {Directory}.");

    private readonly IEdgeModelPaths _paths;
    private readonly IOnnxModelSource[] _sources;
    private readonly ILogger<OnnxModelStore> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProvisionedModel> _provisioned = new(StringComparer.Ordinal);

    /// <summary>Creates the store.</summary>
    /// <param name="paths">The model root and the ORT cache root.</param>
    /// <param name="sources">
    /// The registered sources. <c>FileOnnxModelSource</c> is ordered last whatever its registration
    /// index, because it is the fallback and <c>AddOnnx</c> - which registers it - is almost always
    /// called before the source a consumer actually wants probed first.
    /// </param>
    /// <param name="logger">The logger.</param>
    public OnnxModelStore(
        IEdgeModelPaths paths,
        IEnumerable<IOnnxModelSource> sources,
        ILogger<OnnxModelStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _logger = logger;

        var ordered = new List<IOnnxModelSource>();
        var fallbacks = new List<IOnnxModelSource>();
        foreach (var source in sources)
        {
            (source is FileOnnxModelSource ? fallbacks : ordered).Add(source);
        }

        ordered.AddRange(fallbacks);
        _sources = [.. ordered];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ProvisionedModelIds => [.. _provisioned.Keys];

    /// <inheritdoc />
    public ProvisionedModel? TryGet(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return _provisioned.TryGetValue(modelId, out var model) ? model : null;
    }

    /// <inheritdoc />
    public bool IsProvisioned(OnnxModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var directory = OnnxModelLayout.DirectoryFor(_paths.Models, manifest);
        return File.Exists(Path.Combine(directory, OnnxModelLayout.MarkerFileName)) &&
               OnnxModelLayout.LengthsMatch(directory, manifest);
    }

    /// <inheritdoc />
    public async ValueTask<ProvisionedModel> EnsureAsync(
        OnnxModelManifest manifest,
        IProgress<ModelProvisioningProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var gate = _gates.GetOrAdd(manifest.ModelId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = OnnxModelLayout.DirectoryFor(_paths.Models, manifest);

            if (_provisioned.TryGetValue(manifest.ModelId, out var cached) &&
                string.Equals(cached.Directory, directory, StringComparison.Ordinal) &&
                OnnxModelLayout.LengthsMatch(directory, manifest))
            {
                return cached;
            }

            var started = Stopwatch.GetTimestamp();

            // The fast path. Full re-hashing on every launch is a startup tax paid for a case the
            // provisioning-time verify already covers.
            if (File.Exists(Path.Combine(directory, OnnxModelLayout.MarkerFileName)) &&
                OnnxModelLayout.LengthsMatch(directory, manifest))
            {
                var present = new ProvisionedModel(
                    manifest.ModelId,
                    directory,
                    OnnxModelLayout.FilePath(directory, manifest.GraphFile),
                    OnnxModelLayout.Graph(manifest).Sha256,
                    OnnxModelLayout.Resolve(directory, manifest),
                    ModelProvisioningSource.AlreadyPresent,
                    Stopwatch.GetElapsedTime(started));

                _provisioned[manifest.ModelId] = present;
                return present;
            }

            PurgeStaleRevisions(manifest);

            var model = await ProvisionAsync(manifest, directory, progress, cancellationToken).ConfigureAwait(false);
            await WriteMarkerAsync(directory, manifest, model, cancellationToken).ConfigureAwait(false);

            _provisioned[manifest.ModelId] = model;
            s_provisioned(_logger, manifest.ModelId, model.Source.ToString(), manifest.TotalSizeBytes, null);
            return model;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        cancellationToken.ThrowIfCancellationRequested();

        var modelDirectory = Path.Combine(_paths.Models, modelId);
        if (Directory.Exists(modelDirectory))
        {
            Directory.Delete(modelDirectory, recursive: true);
        }

        // The CoreML cache is keyed on <modelId>/<sha16>, and a cache entry that outlives its
        // model is a compile ORT will never be asked for again.
        var cacheDirectory = Path.Combine(_paths.OrtCache, modelId);
        if (Directory.Exists(cacheDirectory))
        {
            Directory.Delete(cacheDirectory, recursive: true);
            s_ortCachePurged(_logger, modelId, cacheDirectory, null);
        }

        _provisioned.TryRemove(modelId, out _);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var gate in _gates.Values)
        {
            gate.Dispose();
        }

        _gates.Clear();
    }

    private async ValueTask<ProvisionedModel> ProvisionAsync(
        OnnxModelManifest manifest,
        string directory,
        IProgress<ModelProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);

        EdgeModelProvisioningException? first = null;
        foreach (var source in _sources)
        {
            if (!source.CanProvide(manifest))
            {
                continue;
            }

            try
            {
                return await source.EnsureAsync(manifest, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (EdgeModelProvisioningException ex)
            {
                // Keep probing: a bundled asset that is absent from this flavour of the app is not
                // a reason to skip the download source registered after it. The FIRST failure is
                // what surfaces, because it is the one the consumer's own ordering says matters.
                first ??= ex;
            }
        }

        throw first ?? new EdgeModelProvisioningException(
            EdgeErrorCode.ModelNotProvisioned,
            manifest.ModelId,
            manifest.GraphFile,
            $"No registered model source can provide '{manifest.ModelId}'. Register one with " +
            "AddHuggingFaceModelSource, AddBundledModelSource or AddModelSource, or place the files " +
            $"under the model root yourself at '{directory}'.");
    }

    private void PurgeStaleRevisions(OnnxModelManifest manifest)
    {
        var keep = OnnxModelLayout.Sha16(manifest);

        PurgeSiblings(Path.Combine(_paths.Models, manifest.ModelId), keep, cache: false, manifest.ModelId);
        PurgeSiblings(Path.Combine(_paths.OrtCache, manifest.ModelId), keep, cache: true, manifest.ModelId);
    }

    private void PurgeSiblings(string parent, string keep, bool cache, string modelId)
    {
        if (!Directory.Exists(parent))
        {
            return;
        }

        foreach (var stale in Directory.EnumerateDirectories(parent))
        {
            if (string.Equals(Path.GetFileName(stale), keep, StringComparison.Ordinal))
            {
                continue;
            }

            Directory.Delete(stale, recursive: true);
            if (cache)
            {
                s_ortCachePurged(_logger, modelId, stale, null);
            }
        }
    }

    private static async Task WriteMarkerAsync(
        string directory,
        OnnxModelManifest manifest,
        ProvisionedModel model,
        CancellationToken cancellationToken)
    {
        var files = new List<ModelMarkerFile>(manifest.Files.Count);
        foreach (var file in manifest.Files)
        {
            files.Add(new ModelMarkerFile(
                file.RelativePath,
                file.Role,
                file.SizeBytes,
                file.Sha256,
                OnnxModelLayout.FilePath(directory, file.RelativePath)));
        }

        var marker = new ModelMarker
        {
            ModelId = manifest.ModelId,
            GraphFile = manifest.GraphFile,
            GraphSha256 = model.GraphSha256,
            TotalSizeBytes = manifest.TotalSizeBytes,
            ProvisionedAtUtc = DateTimeOffset.UtcNow,
            Files = files,
        };

        var path = Path.Combine(directory, OnnxModelLayout.MarkerFileName);
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                marker,
                ModelMarkerJsonContext.Default.ModelMarker,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
