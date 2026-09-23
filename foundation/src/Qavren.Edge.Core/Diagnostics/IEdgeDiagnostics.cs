using Qavren.Edge.Lifecycle;

namespace Qavren.Edge.Diagnostics;

/// <summary>One component's self-reported identity, contributed via <see cref="IEdgeDiagnosticsContributor"/>.</summary>
/// <param name="Name">The component's name.</param>
/// <param name="Version">The component's version, or <see langword="null"/> when it does not track one.</param>
/// <param name="Details">Free-form key/value diagnostics the component chose to surface.</param>
public sealed record EdgeComponentReport(
    string Name,
    string? Version,
    IReadOnlyDictionary<string, string?> Details);

/// <summary>How one startup task ran, as recorded by the host.</summary>
/// <param name="Name">The startup task's name.</param>
/// <param name="Order">The order the task ran in, relative to the other registered <see cref="Hosting.IEdgeStartupTask"/>s.</param>
/// <param name="Duration">How long the task took.</param>
/// <param name="Error">The task's exception message, or <see langword="null"/> when it succeeded.</param>
public sealed record EdgeStartupTaskReport(
    string Name,
    int Order,
    TimeSpan Duration,
    string? Error);

/// <summary>Everything a support request needs, in one object.</summary>
/// <param name="Components">Every registered <see cref="IEdgeDiagnosticsContributor"/>'s self-report.</param>
/// <param name="Native">Native SQLite provider diagnostics (library name, RID, verification state).</param>
/// <param name="Paths">The resolved <see cref="IEdgePaths"/> locations.</param>
/// <param name="Startup">Every startup task's report, in run order.</param>
/// <param name="Lifecycle">The <see cref="IEdgeLifecycle"/> hub's recent history.</param>
public sealed record EdgeDiagnosticsReport(
    IReadOnlyList<EdgeComponentReport> Components,
    IReadOnlyDictionary<string, string?> Native,
    IReadOnlyDictionary<string, string> Paths,
    IReadOnlyList<EdgeStartupTaskReport> Startup,
    IReadOnlyList<EdgeLifecycleRecord> Lifecycle);

/// <summary>Produces a point-in-time <see cref="EdgeDiagnosticsReport"/> for the whole host.</summary>
public interface IEdgeDiagnostics
{
    /// <summary>Builds and returns the current <see cref="EdgeDiagnosticsReport"/>.</summary>
    EdgeDiagnosticsReport Report();
}

/// <summary>Implemented by any component that wants to appear in <see cref="EdgeDiagnosticsReport.Components"/>.</summary>
public interface IEdgeDiagnosticsContributor
{
    /// <summary>The component's name, as it appears in <see cref="EdgeComponentReport.Name"/>.</summary>
    string ComponentName { get; }

    /// <summary>The component's version, or <see langword="null"/> when it does not track one.</summary>
    string? ComponentVersion { get; }

    /// <summary>Returns the component's free-form diagnostic details.</summary>
    IReadOnlyDictionary<string, string?> Describe();
}
