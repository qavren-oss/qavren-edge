using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;
using Xunit;

namespace Qavren.Edge.Core.Tests;

public class DiagnosticsTests
{
    private sealed class FakeContributor : IEdgeDiagnosticsContributor
    {
        public string ComponentName => "Fake";

        public string? ComponentVersion => "1.2.3";

        public IReadOnlyDictionary<string, string?> Describe()
            => new Dictionary<string, string?>(StringComparer.Ordinal) { ["answer"] = "42", ["missing"] = null };
    }

    [Fact]
    public async Task Report_ContainsComponentsPathsStartupAndLifecycle()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(_ => { });
        services.AddSingleton<IEdgeDiagnosticsContributor, FakeContributor>();
        using var sp = services.BuildServiceProvider();

        await sp.GetRequiredService<IEdgeHost>().EnsureStartedAsync(TestContext.Current.CancellationToken);
        await sp.GetRequiredService<IEdgeLifecycle>().RaiseResumedAsync(TestContext.Current.CancellationToken);

        var report = sp.GetRequiredService<IEdgeDiagnostics>().Report();

        var component = Assert.Single(report.Components);
        Assert.Equal("Fake", component.Name);
        Assert.Equal("1.2.3", component.Version);
        Assert.Equal("42", component.Details["answer"]);
        Assert.True(report.Paths.ContainsKey("Data"));
        Assert.True(report.Paths.ContainsKey("Cache"));
        Assert.Contains(report.Lifecycle, r => r.Kind == EdgeLifecycleEventKind.Resumed);
    }

    [Fact]
    public async Task Report_RendersAsTextAndAsJson()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(_ => { });
        services.AddSingleton<IEdgeDiagnosticsContributor, FakeContributor>();
        using var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<IEdgeHost>().EnsureStartedAsync(TestContext.Current.CancellationToken);

        var diagnostics = sp.GetRequiredService<IEdgeDiagnostics>();
        var report = diagnostics.Report();

        var text = EdgeDiagnosticsRenderer.ToText(report);
        var json = EdgeDiagnosticsRenderer.ToJson(report);

        Assert.Contains("Fake", text, StringComparison.Ordinal);
        Assert.StartsWith("{", json.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("\"components\"", json, StringComparison.Ordinal);
    }
}
