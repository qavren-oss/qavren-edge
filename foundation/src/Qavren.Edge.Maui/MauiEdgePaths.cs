namespace Qavren.Edge.Maui;

/// <summary>
/// <see cref="IEdgePaths"/> over MAUI's <see cref="FileSystem"/>.
/// </summary>
/// <remarks>
/// The values are read on every access, never cached to settings: on iOS the sandbox path contains
/// an application GUID segment that changes across clean builds and reinstalls, so a persisted
/// absolute path is a guaranteed "the database disappeared after an update" bug.
/// </remarks>
public sealed class MauiEdgePaths : IEdgePaths
{
    /// <inheritdoc />
    public string Data => FileSystem.Current.AppDataDirectory;

    /// <inheritdoc />
    public string Cache => FileSystem.Current.CacheDirectory;
}
