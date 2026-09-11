using Qavren.Edge.Lifecycle;

namespace Qavren.Edge.Diagnostics;

public sealed record EdgeComponentReport(
    string Name,
    string? Version,
    IReadOnlyDictionary<string, string?> Details);

public sealed record EdgeStartupTaskReport(
    string Name,
    int Order,
    TimeSpan Duration,
    string? Error);

/// <summary>Everything a support request needs, in one object.</summary>
public sealed record EdgeDiagnosticsReport(
    IReadOnlyList<EdgeComponentReport> Components,
    IReadOnlyDictionary<string, string?> Native,
    IReadOnlyDictionary<string, string> Paths,
    IReadOnlyList<EdgeStartupTaskReport> Startup,
    IReadOnlyList<EdgeLifecycleRecord> Lifecycle);

public interface IEdgeDiagnostics
{
    EdgeDiagnosticsReport Report();
}

/// <summary>Implemented by any component that wants to appear in <see cref="EdgeDiagnosticsReport.Components"/>.</summary>
public interface IEdgeDiagnosticsContributor
{
    string ComponentName { get; }

    string? ComponentVersion { get; }

    IReadOnlyDictionary<string, string?> Describe();
}
