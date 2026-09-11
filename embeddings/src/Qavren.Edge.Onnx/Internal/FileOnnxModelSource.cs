using System.Diagnostics;
using Qavren.Edge.Onnx.Internal;

namespace Qavren.Edge.Onnx;

/// <summary>
/// Files already on disk, in the model root or an explicit directory. <c>AddOnnx</c> registers this
/// one last, as the fallback every other source falls through to.
/// </summary>
public sealed class FileOnnxModelSource : IOnnxModelSource
{
    private readonly IEdgeModelPaths _paths;
    private readonly string? _directoryOverride;

    /// <summary>Creates the source.</summary>
    /// <param name="paths">The model root.</param>
    /// <param name="directoryOverride">
    /// A staging directory to look in instead of the model root - a side-loaded folder, or a test's
    /// temp directory.
    /// </param>
    public FileOnnxModelSource(IEdgeModelPaths paths, string? directoryOverride = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _directoryOverride = directoryOverride;
    }

    /// <inheritdoc />
    public string Name => "file";

    /// <inheritdoc />
    public bool CanProvide(OnnxModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        foreach (var file in manifest.Files)
        {
            if (Locate(manifest, file) is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public async ValueTask<ProvisionedModel> EnsureAsync(
        OnnxModelManifest manifest,
        IProgress<ModelProvisioningProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var started = Stopwatch.GetTimestamp();
        var directory = OnnxModelLayout.DirectoryFor(_paths.Models, manifest);
        Directory.CreateDirectory(directory);

        var graphSha = OnnxModelLayout.Graph(manifest).Sha256;

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var found = Locate(manifest, file)
                ?? throw new EdgeModelProvisioningException(
                    EdgeErrorCode.ModelAssetMissing,
                    manifest.ModelId,
                    file.RelativePath,
                    $"'{file.RelativePath}' was not found under '{Root(manifest)}' at the declared " +
                    $"length of {file.SizeBytes} bytes.")
                {
                    ExpectedBytes = file.SizeBytes,
                    ExpectedSha256 = file.Sha256,
                };

            var destination = OnnxModelLayout.FilePath(directory, file.RelativePath);
            if (!string.Equals(found, destination, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                var source = new FileStream(found, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await using (source.ConfigureAwait(false))
                {
                    var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    await using (target.ConfigureAwait(false))
                    {
                        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            var actual = await OnnxModelLayout
                .VerifyAsync(destination, file, manifest.ModelId, sourceUri: null, cancellationToken)
                .ConfigureAwait(false);

            if (string.Equals(file.RelativePath, manifest.GraphFile, StringComparison.Ordinal))
            {
                graphSha = actual;
            }

            progress?.Report(new ModelProvisioningProgress(
                manifest.ModelId, file.RelativePath, file.SizeBytes, file.SizeBytes, Resumed: false));
        }

        return new ProvisionedModel(
            manifest.ModelId,
            directory,
            OnnxModelLayout.FilePath(directory, manifest.GraphFile),
            graphSha,
            OnnxModelLayout.Resolve(directory, manifest),
            ModelProvisioningSource.File,
            Stopwatch.GetElapsedTime(started));
    }

    private string Root(OnnxModelManifest manifest)
        => _directoryOverride ?? Path.Combine(_paths.Models, manifest.ModelId);

    private string? Locate(OnnxModelManifest manifest, OnnxModelFile file)
    {
        // Three places, cheapest first: the staging root itself, a modelId subdirectory of it, and
        // the destination the store already writes into (so a hand-placed file is honoured).
        var candidates = new[]
        {
            OnnxModelLayout.FilePath(Root(manifest), file.RelativePath),
            OnnxModelLayout.FilePath(Path.Combine(_directoryOverride ?? _paths.Models, manifest.ModelId), file.RelativePath),
            OnnxModelLayout.FilePath(OnnxModelLayout.DirectoryFor(_paths.Models, manifest), file.RelativePath),
        };

        foreach (var candidate in candidates)
        {
            var info = new FileInfo(candidate);
            if (info.Exists && info.Length == file.SizeBytes)
            {
                return candidate;
            }
        }

        return null;
    }
}
