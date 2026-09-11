namespace Qavren.Edge.Onnx;

/// <summary>
/// <c>Context.NoBackupFilesDir/qavren-edge/{models,ort-cache}</c>. Auto-excluded from Auto Backup
/// with no consumer manifest edit, which is the whole point: <c>Context.FilesDir</c> - SP1's
/// <c>IEdgePaths.Data</c> - has a 25 MB per-app backup quota, so one 23 MB model there consumes
/// essentially all of it and an fp32 one triggers <c>onQuotaExceeded()</c> and stops backing up the
/// user's actual SQLite database.
/// </summary>
internal sealed class AndroidEdgeModelPaths : IEdgeModelPaths
{
    private const string RootName = "qavren-edge";

    /// <inheritdoc />
    public string Models => Ensure("models");

    /// <inheritdoc />
    public string OrtCache => Ensure("ort-cache");

    private static string Ensure(string leaf)
    {
        // Never cached across accesses: Application.Context is re-read every time, exactly as SP1's
        // MauiEdgePaths re-reads FileSystem.Current.
        var noBackup = Application.Context.NoBackupFilesDir?.AbsolutePath
            ?? throw new EdgeOnnxException(
                EdgeErrorCode.OnnxUnsupportedRuntime,
                "Android reported no NoBackupFilesDir for this application context.");

        var directory = Path.Combine(noBackup, RootName, leaf);
        Directory.CreateDirectory(directory);
        return directory;
    }
}
