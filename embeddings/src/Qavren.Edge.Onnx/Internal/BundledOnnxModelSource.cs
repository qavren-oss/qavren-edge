using System.Diagnostics;
using Qavren.Edge.Onnx.Internal;

namespace Qavren.Edge.Onnx;

/// <summary>
/// An app-packaged asset, copied out once. The opener is the consumer's - in MAUI that is
/// <c>FileSystem.OpenAppPackageFileAsync</c> - so this package never references MAUI.
/// </summary>
/// <remarks>
/// The copy is unavoidable. On Android a <c>MauiAsset</c> is an <c>AssetManager</c> stream with no
/// path and no length, and spec 9.1 requires a real path: a byte-array session doubles transient
/// memory and degrades ORT's CoreML cache key from the model-URL hash to a hash of graph inputs and
/// node outputs, which collides across quantization variants of one architecture.
/// </remarks>
public sealed class BundledOnnxModelSource : IOnnxModelSource
{
    private readonly IEdgeModelPaths _paths;
    private readonly Func<string, CancellationToken, ValueTask<Stream>> _openAsset;

    /// <summary>Creates the source.</summary>
    /// <param name="paths">The model root.</param>
    /// <param name="openAsset">
    /// Opens an app-package asset by its manifest-relative path. Throwing
    /// <see cref="FileNotFoundException"/> is how "this flavour does not bundle that model" is
    /// spelled; it becomes <see cref="EdgeErrorCode.ModelAssetMissing"/>.
    /// </param>
    public BundledOnnxModelSource(
        IEdgeModelPaths paths,
        Func<string, CancellationToken, ValueTask<Stream>> openAsset)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(openAsset);

        _paths = paths;
        _openAsset = openAsset;
    }

    /// <inheritdoc />
    public string Name => "bundled";

    /// <inheritdoc />
    /// <remarks>
    /// Always true. An asset stream has no length and no path, so the only way to learn whether an
    /// asset exists is to open it - which is work, and belongs in <see cref="EnsureAsync"/>.
    /// </remarks>
    public bool CanProvide(OnnxModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
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

            var destination = OnnxModelLayout.FilePath(directory, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            Stream asset;
            try
            {
                asset = await _openAsset(file.RelativePath, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException ex)
            {
                throw new EdgeModelProvisioningException(
                    EdgeErrorCode.ModelAssetMissing,
                    manifest.ModelId,
                    file.RelativePath,
                    $"The app package has no asset '{file.RelativePath}'. Add it as a MauiAsset, or " +
                    "register a download source instead of bundling this model.",
                    ex)
                {
                    ExpectedBytes = file.SizeBytes,
                    ExpectedSha256 = file.Sha256,
                };
            }

            await using (asset.ConfigureAwait(false))
            {
                var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await using (target.ConfigureAwait(false))
                {
                    await asset.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
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
            ModelProvisioningSource.Bundled,
            Stopwatch.GetElapsedTime(started));
    }
}
