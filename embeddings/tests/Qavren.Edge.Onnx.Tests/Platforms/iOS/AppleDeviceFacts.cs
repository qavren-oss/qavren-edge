using Xunit;

namespace Qavren.Edge.Onnx.Tests;

/// <summary>
/// Spec 16.4's Apple-only assertion: the model root lands in the real Application Support directory
/// and the backup-exclusion flag READS BACK set. Setting <c>NSURLIsExcludedFromBackupKey</c> and
/// never re-reading it is how a directory ends up in iCloud with a passing test.
/// </summary>
/// <remarks>
/// Compiled only for <c>net10.0-ios</c> and <c>net10.0-maccatalyst</c> (the <c>Platforms\**</c>
/// compile-item guard in this project's csproj). No CoreML compile-cache TIMING assertion lives
/// here, deliberately: these lanes create sessions from the 533-byte fixture, which has neither a
/// meaningful compile cost nor the strong <c>ModelCacheDirectory</c> key, so a measurement would be
/// noise dressed as evidence (spec 16.4).
/// </remarks>
public sealed class AppleDeviceFacts
{
    [DeviceFact]
    public void TheModelRootIsInsideApplicationSupportAndReadsBackExcludedFromBackup()
    {
        var paths = new AppleEdgeModelPaths();

        var models = paths.Models;

        Assert.Contains("/Library/Application Support/qavren-edge", models, StringComparison.Ordinal);
        Assert.True(Directory.Exists(models));

        // The read-back. NSUrl.TryGetResource is the only thing that proves the attribute survived
        // directory creation - which is why AppleEdgeModelPaths sets it AFTER creating, not before.
        using var url = NSUrl.FromFilename(models);
        Assert.True(
            url.TryGetResource(NSUrl.IsExcludedFromBackupKey, out var value, out var error),
            error?.LocalizedDescription);
        Assert.True(Assert.IsType<NSNumber>(value).BoolValue);

        // Same for the sibling ORT cache: it is ours to purge, and it must never be backed up either.
        using var cacheUrl = NSUrl.FromFilename(paths.OrtCache);
        Assert.True(cacheUrl.TryGetResource(NSUrl.IsExcludedFromBackupKey, out var cacheValue, out _));
        Assert.True(Assert.IsType<NSNumber>(cacheValue).BoolValue);
    }
}
