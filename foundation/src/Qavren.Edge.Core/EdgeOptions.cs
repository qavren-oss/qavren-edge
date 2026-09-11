namespace Qavren.Edge;

/// <summary>Options for the Qavren.Edge host itself. Bound as <c>IOptions&lt;EdgeOptions&gt;</c>.</summary>
public sealed class EdgeOptions
{
    /// <summary>Used to build the default data and cache directories. Defaults to the process friendly name.</summary>
    public string AppName { get; set; } = AppDomain.CurrentDomain.FriendlyName;

    /// <summary>
    /// Calls <c>SQLitePCL.raw.FreezeProvider()</c> immediately after installing the native provider.
    /// Defaults to <see langword="true"/>: <c>SqliteConnection</c>'s static constructor reflectively
    /// invokes <c>SQLitePCL.Batteries_V2.Init()</c>, which would otherwise silently replace the provider
    /// if any transitive package ever brings a batteries assembly into the app. Tests set this to
    /// <see langword="false"/> so they can swap providers.
    /// </summary>
    public bool FreezeSqliteProvider { get; set; } = true;

    /// <summary>How many recent lifecycle events the hub keeps for diagnostics.</summary>
    public int LifecycleHistoryCapacity { get; set; } = 64;
}
