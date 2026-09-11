using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Onnx.Internal;
using Qavren.Edge.Onnx.Tests.Stubs;
using Xunit;

namespace Qavren.Edge.Onnx.Tests;

/// <summary><c>AddOnnx()</c> idempotence and the <see cref="OnnxOptions"/> defaults that change behaviour.</summary>
public class OnnxRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qedge-onnxreg-" + Guid.NewGuid().ToString("N"));

    private sealed class TempPaths(string root) : IEdgePaths
    {
        public string Data { get; } = root;

        public string Cache { get; } = Path.Combine(root, "cache");
    }

    private ServiceProvider Build(Action<EdgeBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Registered before AddQavrenEdge so SP1's TryAddSingleton keeps it: nothing here should
        // create directories under the real LocalApplicationData.
        services.AddSingleton<IEdgePaths>(new TempPaths(_root));
        services.AddQavrenEdge(configure);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddOnnxTwiceRegistersOneOfEverything()
    {
        using var provider = Build(edge =>
        {
            edge.AddOnnx();
            edge.AddOnnx();
        });

        Assert.Single(provider.GetServices<IEdgeModelPaths>());
        Assert.Single(provider.GetServices<IEdgeResourceMonitor>());
        Assert.Single(provider.GetServices<IEdgeLifecycleObserver>());
        Assert.Single(provider.GetServices<IEdgeDiagnosticsContributor>());

        var startup = Assert.Single(provider.GetServices<IEdgeStartupTask>());
        Assert.Equal(EdgeAiStartupOrder.OnnxEnvironment, startup.Order);
        Assert.Equal(200, startup.Order);
    }

    [Fact]
    public void TheDiagnosticsContributorReportsTheOnnxComponent()
    {
        using var provider = Build(edge => edge.AddOnnx());

        var contributor = Assert.Single(provider.GetServices<IEdgeDiagnosticsContributor>());
        Assert.Equal("Qavren.Edge.Onnx", contributor.ComponentName);

        var details = contributor.Describe();
        Assert.Equal(Path.Combine(_root, "models"), details["modelsDirectory"]);
        Assert.Equal(Path.Combine(_root, "ort-cache"), details["ortCacheDirectory"]);
        Assert.Equal("False", details["unsupportedRuntime"]);
        Assert.Equal("False", details["ortEnvironmentPreexisting"]);
        Assert.Equal("(auto)", details["sharedThreadPool"]);
        Assert.Equal(nameof(EdgeThermalState.Unknown), details["thermalState"]);
    }

    [Fact]
    public void UseModelPathsReplacesTheRegistrationRatherThanAddingASecond()
    {
        var replacement = new StubModelPaths();

        using var provider = Build(edge =>
        {
            edge.AddOnnx();
            edge.UseModelPaths(replacement);
        });

        var resolved = Assert.Single(provider.GetServices<IEdgeModelPaths>());
        Assert.Same(replacement, resolved);
    }

    private sealed class StubModelPaths : IEdgeModelPaths
    {
        public string Models => "/models";

        public string OrtCache => "/ort-cache";
    }

    [Fact]
    public void OnnxOptionsDefaultToWarningSeverityAndTheQavrenLogId()
    {
        using var provider = Build(edge => edge.AddOnnx());

        var options = provider.GetRequiredService<IOptions<OnnxOptions>>().Value;

        Assert.Equal(OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING, options.LogSeverity);
        Assert.Equal("qavren.edge", options.LogId);
        Assert.True(options.BridgeNativeLogging);
        Assert.Null(options.ShareThreadPool);
        Assert.False(options.DisableOrtDllImportResolver);
    }

    [Fact]
    public void TheStartupTaskBuildsCreationOptionsCarryingThoseDefaultsAndALoggingBridge()
    {
        using var provider = Build(edge => edge.AddOnnx());

        var task = Assert.IsType<OnnxEnvironmentStartupTask>(Assert.Single(provider.GetServices<IEdgeStartupTask>()));

        // Never OrtEnv itself: it is a process-wide singleton whose second creation throws, which
        // would make this assertion order-dependent.
        var (creationOptions, hasBridge) = task.BuildCreationOptions();

        Assert.Equal("qavren.edge", creationOptions.logId);
        Assert.Equal(OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING, creationOptions.logLevel);
        Assert.True(hasBridge);
        Assert.NotNull(creationOptions.loggingFunction);
    }

    [Fact]
    public void TurningTheNativeLoggingBridgeOffSuppliesNoLoggingFunction()
    {
        using var provider = Build(edge => edge.AddOnnx(o => o.BridgeNativeLogging = false));

        var task = Assert.IsType<OnnxEnvironmentStartupTask>(Assert.Single(provider.GetServices<IEdgeStartupTask>()));
        var (creationOptions, hasBridge) = task.BuildCreationOptions();

        Assert.False(hasBridge);
        Assert.Null(creationOptions.loggingFunction);
    }

    [Fact]
    public void UseExecutionProviderPolicyConfiguresTheDefaultPolicy()
    {
        using var provider = Build(edge =>
        {
            edge.AddOnnx();
            edge.UseExecutionProviderPolicy(p =>
            {
                p.FallBackToCpu = false;
                p.CoreMl.ProfileComputePlan = true;
            });
        });

        var policy = provider.GetRequiredService<IOptions<OnnxExecutionProviderPolicy>>().Value;

        Assert.False(policy.FallBackToCpu);
        Assert.True(policy.CoreMl.ProfileComputePlan);
    }

    [Fact]
    public async Task TheResourceMonitorRegistrationIsTheOneTheObserverLatchesInto()
    {
        using var provider = Build(edge => edge.AddOnnx());

        var monitor = provider.GetRequiredService<IEdgeResourceMonitor>();
        var observer = Assert.Single(provider.GetServices<IEdgeLifecycleObserver>());

        await observer.OnMemoryPressureAsync(EdgeMemoryPressure.Critical, TestContext.Current.CancellationToken);

        Assert.Equal(EdgeMemoryPressure.Critical, monitor.LastPressure);
    }

    [Fact]
    public void AStubMonitorRegisteredFirstWins()
    {
        var stub = new StubResourceMonitor();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new TempPaths(_root));
        services.AddSingleton<IEdgeResourceMonitor>(stub);
        services.AddQavrenEdge(edge => edge.AddOnnx());

        using var provider = services.BuildServiceProvider();

        Assert.Same(stub, provider.GetRequiredService<IEdgeResourceMonitor>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
