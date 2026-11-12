using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Onnx.Internal;
using Qavren.Edge.Onnx.Tests.Stubs;
using Qavren.Edge.Tests.Fixtures;
using Xunit;

namespace Qavren.Edge.Onnx.Tests;

/// <summary>
/// Spec 9.1's leased session host, over the tier-1 fixtures. These create REAL ORT CPU sessions:
/// the byte-array constructor is reached through the <c>InternalsVisibleTo</c>-scoped registration
/// hook, which exists precisely because a 533-byte graph makes both of the costs that keep it out
/// of the product surface irrelevant.
/// </summary>
public class SessionHostTests : IDisposable
{
    private const string HiddenStatesModel = "fixture-hidden-states";
    private const string NomicModel = "fixture-nomic-order";

    private static readonly OnnxSessionExpectation HiddenStatesSignature =
        new(["input_ids", "attention_mask"], "last_hidden_state", 4);

    private readonly TempModelPaths _modelPaths = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qedge-host-" + Guid.NewGuid().ToString("N"));

    private sealed class TempPaths(string root) : IEdgePaths
    {
        public string Data { get; } = root;

        public string Cache { get; } = Path.Combine(root, "cache");
    }

    private ServiceProvider Build(Action<EdgeBuilder> configure, StubResourceMonitor? monitor = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEdgePaths>(new TempPaths(_root));

        // Registered before AddQavrenEdge so AddOnnx's TryAddSingleton keeps it: what this machine's
        // GC happens to report must not decide whether a pre-flight test passes.
        if (monitor is not null)
        {
            services.AddSingleton<IEdgeResourceMonitor>(monitor);
        }

        services.AddQavrenEdge(edge =>
        {
            edge.AddOnnx();
            edge.UseModelPaths(_modelPaths);
            configure(edge);
        });

        return services.BuildServiceProvider();
    }

    private ServiceProvider BuildWithHiddenStates(StubResourceMonitor? monitor = null)
        => Build(
            edge => edge.AddOnnxModelFromBytes(
                HiddenStatesModel,
                Convert.FromBase64String(TinyModels.HiddenStates),
                HiddenStatesSignature),
            monitor ?? new StubResourceMonitor());

    [Fact]
    public async Task TheNativeSessionIsBuiltOffTheThreadThatAskedForIt()
    {
        // A thread that is not a pool thread, so nothing scheduled on the pool can land on it. With
        // the graph in memory and the gate free there is nothing to await before the session is
        // built, so it used to be built right here - in an app, on the UI thread - and "loaded"
        // was logged from it before AcquireAsync even returned.
        var loadedOn = new LoadedThreadLogger();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(loadedOn));
        services.AddSingleton<IEdgePaths>(new TempPaths(_root));
        services.AddSingleton<IEdgeResourceMonitor>(new StubResourceMonitor());
        services.AddQavrenEdge(edge =>
        {
            edge.AddOnnx();
            edge.UseModelPaths(_modelPaths);
            edge.AddOnnxModelFromBytes(HiddenStatesModel, Convert.FromBase64String(TinyModels.HiddenStates), HiddenStatesSignature);
        });

        using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<IOnnxSessionHost>();
        await provider.GetRequiredService<IEdgeHost>().EnsureStartedAsync(TestContext.Current.CancellationToken);

        var callerThread = 0;
        Task<OnnxSessionLease>? acquire = null;
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            acquire = host.AcquireAsync(HiddenStatesModel, TestContext.Current.CancellationToken).AsTask();
        });
        caller.Start();
        caller.Join();

        using var lease = await acquire!.ConfigureAwait(true);

        Assert.NotEqual(0, loadedOn.Thread);
        Assert.NotEqual(callerThread, loadedOn.Thread);
        Assert.True(lease.Info.IsLoaded);
    }

    [Fact]
    public async Task ACancelThatLandsWhileTheSessionLoadsIsACancelNotASessionCreationFailure()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // The pre-flight reads the monitor after the gate and before ORT builds anything: cancelling
        // there sets ORT's load-cancellation flag before the build, and ORT throws. That throw used
        // to come back as OnnxSessionCreationFailed - a broken model - for a load the caller ended.
        var monitor = new StubResourceMonitor { OnRead = cts.Cancel };
        using var provider = BuildWithHiddenStates(monitor);
        var host = provider.GetRequiredService<IOnnxSessionHost>();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.AcquireAsync(HiddenStatesModel, cts.Token).AsTask());

        Assert.Equal(cts.Token, ex.CancellationToken);

        // And the slot is not left half-loaded: the next caller gets a session.
        monitor.OnRead = null;
        using var lease = await host.AcquireAsync(HiddenStatesModel, TestContext.Current.CancellationToken);
        Assert.True(lease.Info.IsLoaded);
    }

    /// <summary>Records which thread logged "session loaded".</summary>
    private sealed class LoadedThreadLogger : ILoggerProvider, ILogger
    {
        private int _thread;

        public int Thread => Volatile.Read(ref _thread);

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == EdgeAiEventIds.SessionLoaded)
            {
                Volatile.Write(ref _thread, Environment.CurrentManagedThreadId);
            }
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void IrVersionAboveOrtsCeilingFailsWithTheMessageAUserActuallyHits()
    {
        var bytes = Convert.FromBase64String(TinyModels.IrVersion14);

        var ex = Assert.ThrowsAny<Exception>(() => OnnxSessionHost.CreateSessionForTests(bytes));

        Assert.Contains("Unsupported model IR version: 14", ex.Message, StringComparison.Ordinal);
        Assert.Contains("max supported IR version: 13", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoLeasesShareOneSessionAndTheCountFallsAsTheyReturn()
    {
        using var provider = BuildWithHiddenStates();
        var host = provider.GetRequiredService<IOnnxSessionHost>();

        using var first = await host.AcquireAsync(HiddenStatesModel, TestContext.Current.CancellationToken);
        var second = await host.AcquireAsync(HiddenStatesModel, TestContext.Current.CancellationToken);

        Assert.Same(first.Session, second.Session);
        Assert.Equal(2, host.Describe(HiddenStatesModel)!.ActiveLeases);
        Assert.Equal(1, host.Describe(HiddenStatesModel)!.LoadCount);

        second.Dispose();
        Assert.Equal(1, host.Describe(HiddenStatesModel)!.ActiveLeases);

        // Idempotent: a second Dispose must not decrement a second time.
        second.Dispose();
        Assert.Equal(1, host.Describe(HiddenStatesModel)!.ActiveLeases);
    }

    [Fact]
    public async Task ConcurrentFirstCallersProduceOneSession()
    {
        using var provider = BuildWithHiddenStates();
        var host = provider.GetRequiredService<IOnnxSessionHost>();

        var callers = new Task<OnnxSessionLease>[8];
        for (var i = 0; i < callers.Length; i++)
        {
            callers[i] = Task.Run(
                () => host.AcquireAsync(HiddenStatesModel, TestContext.Current.CancellationToken).AsTask(),
                TestContext.Current.CancellationToken);
        }

        var leases = await Task.WhenAll(callers);
        try
        {
            Assert.All(leases, lease => Assert.Same(leases[0].Session, lease.Session));
            Assert.Equal(1, host.Describe(HiddenStatesModel)!.LoadCount);
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    [Fact]
    public async Task ADropWhileLeasedKeepsTheSessionAliveUntilTheLastLeaseReturns()
    {
        using var provider = BuildWithHiddenStates();
        var host = provider.GetRequiredService<IOnnxSessionHost>();

        var lease = await host.AcquireAsync(HiddenStatesModel, TestContext.Current.CancellationToken);

        var released = await host.DropAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, released);

        // Still usable: disposing a session under an in-flight Run is a native access violation, so
        // the lease is what makes that impossible.
        Assert.Contains("input_ids", lease.Session.InputNames);
        Assert.True(host.Describe(HiddenStatesModel)!.IsLoaded);

        lease.Dispose();

        Assert.False(host.Describe(HiddenStatesModel)!.IsLoaded);
        Assert.Equal(0, host.Describe(HiddenStatesModel)!.ActiveLeases);
    }

    [Fact]
    public async Task DisposingTheHostWhileLeasedKeepsTheSessionAliveUntilTheLastLeaseReturns()
    {
        var provider = BuildWithHiddenStates();
        var host = provider.GetRequiredService<IOnnxSessionHost>();

        var lease = await host.AcquireAsync(HiddenStatesModel, TestContext.Current.CancellationToken);

        // The container going away with an embedding still in flight. Dispose used to free the
        // session there and then, whatever was borrowed - DropAsync's discipline, skipped on the
        // one path nobody can retry.
        await provider.DisposeAsync();

        var marked = host.Describe(HiddenStatesModel)!;
        Assert.True(marked.IsLoaded);
        Assert.Equal(1, marked.ActiveLeases);
        Assert.Contains("input_ids", lease.Session.InputNames);

        lease.Dispose();

        Assert.False(host.Describe(HiddenStatesModel)!.IsLoaded);
        Assert.Equal(0, host.Describe(HiddenStatesModel)!.ActiveLeases);

        // And a disposed host does not quietly load another session nothing would ever free.
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => host.AcquireAsync(HiddenStatesModel, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task AnIdleSessionIsDisposedByDropAndTheNextAcquireReloads()
    {
        using var provider = BuildWithHiddenStates();
        var host = provider.GetRequiredService<IOnnxSessionHost>();

        using (await host.AcquireAsync(HiddenStatesModel, TestContext.Current.CancellationToken))
        {
        }

        Assert.Equal(1, await host.DropAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(host.Describe(HiddenStatesModel)!.IsLoaded);

        using var reloaded = await host.AcquireAsync(HiddenStatesModel, TestContext.Current.CancellationToken);

        Assert.True(reloaded.Info.IsLoaded);
        Assert.Equal(2, reloaded.Info.LoadCount);
    }

    [Fact]
    public async Task MemoryPressureCriticalDropsTheSessionAndTheNextAcquireReloadsIt()
    {
        using var provider = BuildWithHiddenStates();
        var host = provider.GetRequiredService<IOnnxSessionHost>();

        using (await host.AcquireAsync(HiddenStatesModel, TestContext.Current.CancellationToken))
        {
        }

        var observer = Assert.Single(provider.GetServices<IEdgeLifecycleObserver>());
        await observer.OnMemoryPressureAsync(EdgeMemoryPressure.Critical, TestContext.Current.CancellationToken);

        Assert.False(host.Describe(HiddenStatesModel)!.IsLoaded);

        using var reloaded = await host.AcquireAsync(HiddenStatesModel, TestContext.Current.CancellationToken);
        Assert.Equal(2, reloaded.Info.LoadCount);
    }

    [Fact]
    public async Task ASessionPinnedAgainstMemoryPressureSurvivesCriticalAndDiesOnStopping()
    {
        using var provider = Build(
            edge => edge.AddOnnxModelFromBytes(
                HiddenStatesModel,
                Convert.FromBase64String(TinyModels.HiddenStates),
                HiddenStatesSignature,
                o => o.DropOnMemoryPressure = false),
            new StubResourceMonitor());

        var host = provider.GetRequiredService<IOnnxSessionHost>();
        using (await host.AcquireAsync(HiddenStatesModel, TestContext.Current.CancellationToken))
        {
        }

        Assert.Equal(0, await host.DropAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(host.Describe(HiddenStatesModel)!.IsLoaded);

        Assert.Equal(1, await host.DropAsync(includePinned: true, TestContext.Current.CancellationToken));
        Assert.False(host.Describe(HiddenStatesModel)!.IsLoaded);
    }

    [Fact]
    public async Task AShortMemoryReadingRefusesTheSessionWithTheNumbersAndARemediation()
    {
        var monitor = new StubResourceMonitor
        {
            Next = new EdgeResourceSnapshot(
                AvailableMemoryBytes: 1024,
                IsLowMemory: true,
                EdgeThermalState.Unknown,
                ThermalHeadroom: null,
                IsLowPowerMode: null,
                LastPressure: null),
        };

        using var provider = BuildWithHiddenStates(monitor);
        var host = provider.GetRequiredService<IOnnxSessionHost>();

        var ex = await Assert.ThrowsAsync<EdgeOnnxException>(
            () => host.AcquireAsync(HiddenStatesModel, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(EdgeErrorCode.OnnxInsufficientMemory, ex.Code);
        Assert.Equal(HiddenStatesModel, ex.ModelId);
        Assert.Equal(1024, ex.AvailableBytes);
        Assert.NotNull(ex.RequiredBytes);
        Assert.True(ex.RequiredBytes > 1024);
        Assert.Contains("int8", ex.Remediation, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public async Task AnUnknownMemoryReadingSkipsTheCheckAndIsNeverARefusal(long? available)
    {
        var monitor = new StubResourceMonitor
        {
            Next = new EdgeResourceSnapshot(
                available,
                IsLowMemory: null,
                EdgeThermalState.Unknown,
                ThermalHeadroom: null,
                IsLowPowerMode: null,
                LastPressure: null),
        };

        using var provider = BuildWithHiddenStates(monitor);
        var host = provider.GetRequiredService<IOnnxSessionHost>();

        // Apple's os_proc_available_memory() returns 0 for "unknown or already over"; a reading
        // nobody can make is never used to refuse.
        using var lease = await host.AcquireAsync(HiddenStatesModel, TestContext.Current.CancellationToken);

        Assert.True(lease.Info.IsLoaded);
    }

    [Fact]
    public async Task AZeroHeadroomFactorDisablesThePreflightEntirely()
    {
        var monitor = new StubResourceMonitor
        {
            Next = new EdgeResourceSnapshot(
                AvailableMemoryBytes: 1,
                IsLowMemory: true,
                EdgeThermalState.Unknown,
                ThermalHeadroom: null,
                IsLowPowerMode: null,
                LastPressure: null),
        };

        using var provider = Build(
            edge => edge.AddOnnxModelFromBytes(
                HiddenStatesModel,
                Convert.FromBase64String(TinyModels.HiddenStates),
                HiddenStatesSignature,
                o => o.MemoryHeadroomFactor = 0),
            monitor);

        var host = provider.GetRequiredService<IOnnxSessionHost>();
        using var lease = await host.AcquireAsync(HiddenStatesModel, TestContext.Current.CancellationToken);

        Assert.True(lease.Info.IsLoaded);
    }

    [Fact]
    public async Task SignatureValidationMatchesByNameNotByPosition()
    {
        using var provider = Build(
            edge => edge.AddOnnxModelFromBytes(
                NomicModel,
                Convert.FromBase64String(TinyModels.NomicInputOrder),

                // nomic's export declares input_ids, token_type_ids, attention_mask IN THAT ORDER.
                // Naming them in a different order must still validate; a positional match would
                // feed the mask as token-type ids and silently change the vectors.
                new OnnxSessionExpectation(
                    ["input_ids", "attention_mask", "token_type_ids"], "last_hidden_state", 4)),
            new StubResourceMonitor());

        var host = provider.GetRequiredService<IOnnxSessionHost>();
        using var lease = await host.AcquireAsync(NomicModel, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["input_ids", "token_type_ids", "attention_mask"],
            lease.Info.Signature.InputNames);
        Assert.Contains("last_hidden_state", lease.Info.Signature.OutputNames);
    }

    [Fact]
    public async Task AnInputThePresetNamesButTheGraphDoesNotDeclareListsBothNameSets()
    {
        using var provider = Build(
            edge => edge.AddOnnxModelFromBytes(
                NomicModel,
                Convert.FromBase64String(TinyModels.NomicInputOrder),
                new OnnxSessionExpectation(
                    ["input_ids", "attention_mask", "token_type_ids", "inputs_embeds"],
                    "last_hidden_state",
                    4)),
            new StubResourceMonitor());

        var host = provider.GetRequiredService<IOnnxSessionHost>();

        var ex = await Assert.ThrowsAsync<EdgeOnnxException>(
            () => host.AcquireAsync(NomicModel, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(EdgeErrorCode.OnnxModelSignatureMismatch, ex.Code);
        Assert.Contains("inputs_embeds", ex.Message, StringComparison.Ordinal);
        Assert.Contains("input_ids", ex.Message, StringComparison.Ordinal);
        Assert.Contains("token_type_ids", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGraphInputNothingCanSupplyIsAlsoAMismatch()
    {
        using var provider = Build(
            edge => edge.AddOnnxModelFromBytes(
                NomicModel,
                Convert.FromBase64String(TinyModels.NomicInputOrder),

                // ORT requires EVERY declared input to be fed, so an unrecognised one can never be
                // satisfied - and finding that out at Run time is finding it out too late.
                new OnnxSessionExpectation(["input_ids", "attention_mask"], "last_hidden_state", 4)),
            new StubResourceMonitor());

        var host = provider.GetRequiredService<IOnnxSessionHost>();

        var ex = await Assert.ThrowsAsync<EdgeOnnxException>(
            () => host.AcquireAsync(NomicModel, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(EdgeErrorCode.OnnxModelSignatureMismatch, ex.Code);
        Assert.Contains("token_type_ids", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnregisteredModelIdIsModelNotRegistered()
    {
        using var provider = BuildWithHiddenStates();
        var host = provider.GetRequiredService<IOnnxSessionHost>();

        var ex = await Assert.ThrowsAsync<EdgeOnnxException>(
            () => host.AcquireAsync("nobody", TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(EdgeErrorCode.ModelNotRegistered, ex.Code);
        Assert.Equal("nobody", ex.ModelId);
    }

    [Fact]
    public void ThePinnedShapeIsPartOfTheSlotKeyAndTheDefaultShapeIsNot()
    {
        var dynamicShape = new OnnxSessionOptions();
        Assert.Equal("m", OnnxSessionHost.KeyFor("m", dynamicShape));

        var pinned = new OnnxSessionOptions();
        pinned.FreeDimensionOverrides["sequence_length"] = 128;
        pinned.FreeDimensionOverrides["batch_size"] = 1;

        var other = new OnnxSessionOptions();
        other.FreeDimensionOverrides["sequence_length"] = 256;
        other.FreeDimensionOverrides["batch_size"] = 1;

        // Canonical, so two dictionaries that differ only in insertion order are one key...
        var reordered = new OnnxSessionOptions();
        reordered.FreeDimensionOverrides["batch_size"] = 1;
        reordered.FreeDimensionOverrides["sequence_length"] = 128;

        Assert.Equal(OnnxSessionHost.KeyFor("m", pinned), OnnxSessionHost.KeyFor("m", reordered));

        // ...and two different pinned shapes are two different sessions, because the shape is baked
        // into the session and the second caller must not be handed the first one's.
        Assert.NotEqual(OnnxSessionHost.KeyFor("m", pinned), OnnxSessionHost.KeyFor("m", other));
        Assert.NotEqual("m", OnnxSessionHost.KeyFor("m", pinned));
    }

    [Fact]
    public async Task DiagnosticsCarryThePerSessionKeys()
    {
        using var provider = BuildWithHiddenStates();
        var host = provider.GetRequiredService<IOnnxSessionHost>();

        using var lease = await host.AcquireAsync(HiddenStatesModel, TestContext.Current.CancellationToken);

        var contributor = Assert.Single(provider.GetServices<Qavren.Edge.Diagnostics.IEdgeDiagnosticsContributor>());
        var details = contributor.Describe();

        var prefix = $"session[{HiddenStatesModel}].";
        Assert.Equal("True", details[prefix + "loaded"]);
        Assert.Equal("1", details[prefix + "leases"]);
        Assert.Equal("1", details[prefix + "loadCount"]);
        Assert.Equal("input_ids, attention_mask", details[prefix + "inputNames"]);
        Assert.Equal("last_hidden_state", details[prefix + "outputNames"]);
        Assert.NotNull(details[prefix + "sha256"]);
        Assert.True(details.ContainsKey("provisionedModels"));
    }

    public void Dispose()
    {
        _modelPaths.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
