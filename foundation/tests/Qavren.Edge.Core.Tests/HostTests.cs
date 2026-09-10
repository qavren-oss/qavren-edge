using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;
using Xunit;

namespace Qavren.Edge.Core.Tests;

public class HostTests
{
    private sealed class RecordingTask(List<string> log, string name, int order, Exception? throws = null)
        : IEdgeStartupTask
    {
        public int Order => order;

        public Task RunAsync(CancellationToken cancellationToken)
        {
            log.Add(name);
            return throws is null ? Task.CompletedTask : Task.FromException(throws);
        }
    }

    private static ServiceProvider Build(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(_ => { });
        configure(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddQavrenEdge_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQavrenEdge(_ => { });
        services.AddQavrenEdge(_ => { });

        using var sp = services.BuildServiceProvider();

        Assert.Single(sp.GetServices<IEdgeHost>());
    }

    [Fact]
    public async Task StartupTasks_RunAscendingByOrderThenRegistrationOrder()
    {
        var log = new List<string>();
        using var sp = Build(s =>
        {
            s.AddSingleton<IEdgeStartupTask>(new RecordingTask(log, "hundred", 100));
            s.AddSingleton<IEdgeStartupTask>(new RecordingTask(log, "zero", 0));
            s.AddSingleton<IEdgeStartupTask>(new RecordingTask(log, "ten-a", 10));
            s.AddSingleton<IEdgeStartupTask>(new RecordingTask(log, "ten-b", 10));
        });

        var host = sp.GetRequiredService<IEdgeHost>();
        await host.EnsureStartedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["zero", "ten-a", "ten-b", "hundred"], log);
    }

    [Fact]
    public async Task Start_IsIdempotentAcrossManyCalls()
    {
        var log = new List<string>();
        using var sp = Build(s => s.AddSingleton<IEdgeStartupTask>(new RecordingTask(log, "once", 0)));

        var host = sp.GetRequiredService<IEdgeHost>();
        host.Start();
        host.Start();
        host.Start();
        await host.EnsureStartedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["once"], log);
    }

    [Fact]
    public async Task FirstFailure_FaultsStartedAndIsRethrownByEveryEnsureStarted()
    {
        var boom = new EdgeConfigurationException(EdgeErrorCode.NoNativeProviderRegistered, "no provider");
        var log = new List<string>();
        using var sp = Build(s =>
        {
            s.AddSingleton<IEdgeStartupTask>(new RecordingTask(log, "first", 0, boom));
            s.AddSingleton<IEdgeStartupTask>(new RecordingTask(log, "second", 10));
        });

        var host = sp.GetRequiredService<IEdgeHost>();

        var one = await Assert.ThrowsAsync<EdgeConfigurationException>(
            async () => await host.EnsureStartedAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
        var two = await Assert.ThrowsAsync<EdgeConfigurationException>(
            async () => await host.EnsureStartedAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));

        Assert.Same(boom, one);
        Assert.Same(boom, two);
        Assert.Equal(["first"], log);   // the run stops at the first failure
        Assert.True(host.Started.IsFaulted);
    }
}
