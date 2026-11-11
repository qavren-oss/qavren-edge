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

    private sealed class ThrowingContributor : IEdgeDiagnosticsContributor
    {
        public string ComponentName => "Broken";

        public string? ComponentVersion => "9.9.9";

        public IReadOnlyDictionary<string, string?> Describe()
            => throw new InvalidOperationException("SELECT secret FROM somewhere");
    }

    [Fact]
    public void AContributorThatThrowsBecomesOneUnavailableSectionAndTheRestOfTheReportSurvives()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(_ => { });
        services.AddSingleton<IEdgeDiagnosticsContributor, ThrowingContributor>();
        services.AddSingleton<IEdgeDiagnosticsContributor, FakeContributor>();
        using var sp = services.BuildServiceProvider();

        // The report is what gets asked for when something is already wrong, so the contributor
        // most likely to throw is the one describing the thing that broke.
        var report = sp.GetRequiredService<IEdgeDiagnostics>().Report();

        Assert.Equal(2, report.Components.Count);

        var broken = Assert.Single(report.Components, c => c.Name == "Broken");
        Assert.Equal("unavailable", broken.Details["state"]);
        Assert.Equal(nameof(InvalidOperationException), broken.Details["error"]);

        Assert.Equal("42", Assert.Single(report.Components, c => c.Name == "Fake").Details["answer"]);

        // The type, never the message: this text is pasted into support tickets.
        Assert.DoesNotContain("secret", EdgeDiagnosticsRenderer.ToText(report), StringComparison.Ordinal);
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
