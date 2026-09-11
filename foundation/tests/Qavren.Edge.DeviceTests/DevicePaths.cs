namespace Qavren.Edge.DeviceTests;

/// <summary>Sandbox-relative paths for the device lane. Never cache the absolute string: on iOS
/// the sandbox path carries an application GUID segment that changes across reinstalls.</summary>
public sealed class DevicePaths : IEdgePaths
{
    public string Data => FileSystem.Current.AppDataDirectory;

    public string Cache => FileSystem.Current.CacheDirectory;
}
