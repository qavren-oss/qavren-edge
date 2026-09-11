namespace Qavren.Edge.Onnx;

/// <summary>
/// <c>Library/Application Support/qavren-edge/{models,ort-cache}</c>, with
/// <c>NSURLIsExcludedFromBackupKey</c> set on each directory AFTER creation.
/// </summary>
/// <remarks>
/// Application Support rather than Documents (which iCloud backs up and the Files app exposes) and
/// rather than Caches (which the OS may purge mid-session, converting a purge into a re-download).
/// The absolute path is never cached across launches: the iOS sandbox path carries an app GUID
/// that changes on every clean install, which is the same reason SP1's <c>MauiEdgePaths</c> reads
/// <c>FileSystem.Current</c> each time.
/// </remarks>
internal sealed class AppleEdgeModelPaths : IEdgeModelPaths
{
    private const string RootName = "qavren-edge";

    /// <inheritdoc />
    public string Models => Ensure("models");

    /// <inheritdoc />
    public string OrtCache => Ensure("ort-cache");

    private static string Ensure(string leaf)
    {
        var support = NSSearchPath.GetDirectories(
            NSSearchPathDirectory.ApplicationSupportDirectory,
            NSSearchPathDomain.User,
            expandTilde: true);

        if (support.Length == 0)
        {
            throw new EdgeOnnxException(
                EdgeErrorCode.OnnxUnsupportedRuntime,
                "This process has no Application Support directory, so Qavren.Edge.Onnx has nowhere " +
                "durable and backup-excluded to put a model.");
        }

        var directory = Path.Combine(support[0], RootName, leaf);
        Directory.CreateDirectory(directory);
        ExcludeFromBackup(directory);
        return directory;
    }

    private static void ExcludeFromBackup(string directory)
    {
        // AFTER creation, by contract: NSURLIsExcludedFromBackupKey is a resource value on an
        // existing file-system object and setting it on a path that does not exist yet is a no-op
        // that reports success.
        using var url = NSUrl.FromFilename(directory);
        url.SetResource(NSUrl.IsExcludedFromBackupKey, NSNumber.FromBoolean(true), out var error);

        if (error is not null)
        {
            throw new EdgeOnnxException(
                EdgeErrorCode.OnnxUnsupportedRuntime,
                $"Could not exclude '{directory}' from iCloud backup: {error.LocalizedDescription}. " +
                "A 23-137 MB re-derivable model must not enter the user's backup.")
            {
                Remediation = "Check the app's sandbox entitlements and that Application Support is writable.",
            };
        }
    }
}
