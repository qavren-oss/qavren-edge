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

    /// <summary>Runs whatever a test hands it, as a startup task.</summary>
    private sealed class DelegateTask(int order, Func<CancellationToken, Task> run) : IEdgeStartupTask
    {
        public int Order => order;

        public Task RunAsync(CancellationToken cancellationToken) => run(cancellationToken);
    }

    [Fact]
    public async Task AStartupTaskThatAwaitsStartupDoesNotWaitOnItself()
    {
        var ct = TestContext.Current.CancellationToken;
        var log = new List<string>();
        IEdgeHost? host = null;

        using var sp = Build(s =>
        {
            s.AddSingleton<IEdgeStartupTask>(new RecordingTask(log, "before", 0));

            // What every Qavren.Edge entry point does first - IEdgeDatabase.OpenConnectionAsync, a
            // session acquire, a chat turn. A task that seeded a table therefore waited for a
            // startup that could not finish until the task did.
            s.AddSingleton<IEdgeStartupTask>(new DelegateTask(50, async token =>
            {
                await host!.EnsureStartedAsync(token).ConfigureAwait(false);
                log.Add("re-entered");
            }));

            s.AddSingleton<IEdgeStartupTask>(new RecordingTask(log, "after", 100));
        });

        host = sp.GetRequiredService<IEdgeHost>();

        await host.EnsureStartedAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), ct);

        // Everything ordered before the task had completed when it was let through; nothing after.
        Assert.Equal(["before", "re-entered", "after"], log);
    }

    [Fact]
    public async Task WorkAStartupTaskLeavesRunningGoesBackToWaitingForStartup()
    {
        var ct = TestContext.Current.CancellationToken;
        IEdgeHost? host = null;
        Task? leftRunning = null;
        var letTheStragglerAsk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var letStartupFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var sp = Build(s =>
        {
            // Starts something, does not await it, and returns: the exemption is the task's, on its
            // own call path, for as long as it runs. It is not a pass the work it spawned keeps.
            s.AddSingleton<IEdgeStartupTask>(new DelegateTask(0, _ =>
            {
                leftRunning = Task.Run(
                    async () =>
                    {
                        await letTheStragglerAsk.Task.ConfigureAwait(false);
                        await host!.EnsureStartedAsync(CancellationToken.None).ConfigureAwait(false);
                    },
                    CancellationToken.None);

                return Task.CompletedTask;
            }));

            s.AddSingleton<IEdgeStartupTask>(new DelegateTask(10, _ => letStartupFinish.Task));
        });

        host = sp.GetRequiredService<IEdgeHost>();
        host.Start();

        letTheStragglerAsk.SetResult();
        await Task.Delay(150, ct);

        Assert.False(host.Started.IsCompleted);
        Assert.NotNull(leftRunning);
        Assert.False(leftRunning.IsCompleted);

        letStartupFinish.SetResult();
        await leftRunning.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    [Fact]
    public async Task AStartupTaskIsOnlyLetThroughItsOwnHost()
    {
        var ct = TestContext.Current.CancellationToken;
        var otherMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var other = Build(s => s.AddSingleton<IEdgeStartupTask>(new DelegateTask(0, _ => otherMayFinish.Task)));
        var otherHost = other.GetRequiredService<IEdgeHost>();

        Task? askedTheOtherHost = null;
        using var sp = Build(s => s.AddSingleton<IEdgeStartupTask>(new DelegateTask(0, _ =>
        {
            askedTheOtherHost = otherHost.EnsureStartedAsync(CancellationToken.None).AsTask();
            return Task.CompletedTask;
        })));

        await sp.GetRequiredService<IEdgeHost>().EnsureStartedAsync(ct);

        // Being inside ONE host's startup says nothing about another's.
        Assert.NotNull(askedTheOtherHost);
        Assert.False(askedTheOtherHost.IsCompleted);

        otherMayFinish.SetResult();
        await askedTheOtherHost.WaitAsync(TimeSpan.FromSeconds(30), ct);
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
