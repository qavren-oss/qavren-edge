using Xunit;

namespace Qavren.Edge.Onnx.Tests;

/// <summary>
/// Spec 16.4's Android-only assertion: the model root is the real <c>NoBackupFilesDir</c>, which is
/// what keeps a 23 MB re-derivable model out of Auto Backup's 25 MB per-app quota without asking a
/// consumer to edit their manifest.
/// </summary>
/// <remarks>
/// Compiled only for <c>net10.0-android</c> (the <c>Platforms\**</c> compile-item guard in this
/// project's csproj) and executed only on a device or emulator.
/// </remarks>
public sealed class AndroidDeviceFacts
{
    [DeviceFact]
    public void TheModelRootIsUnderNoBackupFilesDir()
    {
        var paths = new AndroidEdgeModelPaths();
        var expected = Path.Combine(
            Application.Context.NoBackupFilesDir!.AbsolutePath, "qavren-edge", "models");

        Assert.Equal(expected, paths.Models);
        Assert.True(Directory.Exists(paths.Models));

        // The sibling ORT cache is ours to purge and must sit under the same no-backup root: a
        // CoreML/XNNPACK artefact in FilesDir would be backed up and restored onto a device that
        // cannot use it.
        Assert.Equal(
            Path.Combine(Application.Context.NoBackupFilesDir!.AbsolutePath, "qavren-edge", "ort-cache"),
            paths.OrtCache);
        Assert.True(Directory.Exists(paths.OrtCache));
    }
}
