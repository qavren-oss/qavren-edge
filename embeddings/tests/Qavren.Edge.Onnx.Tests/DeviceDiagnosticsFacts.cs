using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Onnx.Internal;
using Qavren.Edge.Tests.Fixtures;
using Xunit;

namespace Qavren.Edge.Onnx.Tests;

/// <summary>
/// Spec 16.4's platform-independent device assertions: the resource monitor reads something real,
/// the nullable pressure latch moves and clears, critical pressure drops a live session, and the
/// execution-provider report is written where the lane's trx can carry it.
/// </summary>
/// <remarks>
/// This file compiles for EVERY TFM, <c>net10.0</c> included - only its
/// <see cref="DeviceFactAttribute"/>s skip on the host. The container is built from the same
/// registrations a consumer uses, so <see cref="IEdgeResourceMonitor"/> is whichever implementation
/// <c>AddOnnx</c> compiled in for this platform (spec 6.4): Android's, Apple's, or the desktop one.
/// That is the whole point - a stub here would prove nothing a host test does not already prove.
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification =
        "xunit ships a CA2007 suppressor for test methods, but it recognises [Fact]/[Theory] " +
        "literally and not a derived attribute, so every await under [DeviceFact] trips the rule " +
        "on the net10.0 lane (the device lanes already NoWarn it in the csproj). Its fix - " +
        "ConfigureAwait(false) in a test body - is what xUnit1030 forbids.")]
public sealed class DeviceDiagnosticsFacts : IDisposable
{
    private const string TinyModelId = "fixture-hidden-states";

    private static readonly OnnxSessionExpectation TinySignature =
        new(["input_ids", "attention_mask"], "last_hidden_state", 4);

    private readonly TempModelPaths _modelPaths = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "qedge-device-" + Guid.NewGuid().ToString("N"));

    private ServiceProvider? _provider;

    private sealed class TempPaths(string root) : IEdgePaths
    {
        public string Data { get; } = root;

        public string Cache { get; } = Path.Combine(root, "cache");
    }

    /// <summary>
    /// Built lazily and once per test. The 533-byte tier-1 graph reaches ORT through the
    /// <c>InternalsVisibleTo</c>-scoped byte-array hook, so a device lane needs no model download -
    /// spec 16.4 is explicit that these lanes run with none.
    /// </summary>
    private IServiceProvider Services
    {
        get
        {
            if (_provider is null)
            {
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton<IEdgePaths>(new TempPaths(_root));
                services.AddQavrenEdge(edge =>
                {
                    edge.AddOnnx();

                    // The real no-backup roots are asserted by the per-platform facts under
                    // Platforms\**; here the model root is a temp directory so a device run leaves
                    // nothing behind in Application Support or NoBackupFilesDir.
                    edge.UseModelPaths(_modelPaths);
                    edge.AddOnnxModelFromBytes(
                        TinyModelId,
                        Convert.FromBase64String(TinyModels.HiddenStates),
                        TinySignature);
                });

                _provider = services.BuildServiceProvider();
            }

            return _provider;
        }
    }

    [DeviceFact]
    public void TheResourceMonitorReportsAPlausibleMemoryBudget()
    {
        var monitor = Services.GetRequiredService<IEdgeResourceMonitor>();

        var snapshot = monitor.Read();

        Assert.NotNull(snapshot.AvailableMemoryBytes);

        // Between 1 MB and 64 GB. On Apple, os_proc_available_memory() returning 0 means "unknown or
        // already over" and 6.4 treats it as unknown, never as a refusal - so 0 is mapped to null by
        // the monitor and would fail the NotNull above rather than slip through this range.
        Assert.InRange(snapshot.AvailableMemoryBytes!.Value, 1L << 20, 64L << 30);

        // Android reports a thermal status only from API 29 (AndroidResourceMonitor), so below that
        // Unknown is the honest answer and asserting otherwise would be asserting a binding, not a
        // device. Apple always reports one.
        if (!OperatingSystem.IsAndroid() || OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            Assert.NotEqual(EdgeThermalState.Unknown, snapshot.Thermal);
        }
    }

    [DeviceFact]
    public async Task ASimulatedPressureEventMovesTheLatchAndResumedClearsIt()
    {
        var monitor = Services.GetRequiredService<IEdgeResourceMonitor>();
        var lifecycle = Services.GetRequiredService<IEdgeLifecycle>();

        Assert.Null(monitor.LastPressure);                       // null BEFORE any event - spec 6.4

        await lifecycle.RaiseMemoryPressureAsync(
            EdgeMemoryPressure.Moderate, TestContext.Current.CancellationToken);
        Assert.Equal(EdgeMemoryPressure.Moderate, monitor.LastPressure);

        await lifecycle.RaiseResumedAsync(TestContext.Current.CancellationToken);
        Assert.Null(monitor.LastPressure);                       // cleared, not set to Low
    }

    [DeviceFact]
    public async Task CriticalPressureDropsTheSessionAndTheNextAcquireReloadsIt()
    {
        var sessions = Services.GetRequiredService<IOnnxSessionHost>();
        using (await sessions.AcquireAsync(TinyModelId, TestContext.Current.CancellationToken))
        {
        }

        var loadsBefore = sessions.Describe(TinyModelId)!.LoadCount;

        await Services.GetRequiredService<IEdgeLifecycle>()
            .RaiseMemoryPressureAsync(EdgeMemoryPressure.Critical, TestContext.Current.CancellationToken);

        Assert.False(sessions.Describe(TinyModelId)!.IsLoaded);

        using var second = await sessions.AcquireAsync(TinyModelId, TestContext.Current.CancellationToken);
        Assert.Equal(loadsBefore + 1, sessions.Describe(TinyModelId)!.LoadCount);
    }

    [DeviceFact]
    public async Task TheExecutionProviderReportIsPublishedForTheLaneToRecord()
    {
        using var lease = await Services.GetRequiredService<IOnnxSessionHost>()
            .AcquireAsync(TinyModelId, TestContext.Current.CancellationToken);

        var report = lease.Info.ExecutionProviders;

        Assert.NotEmpty(report.Attempts);
        foreach (var attempt in report.Attempts)
        {
            // Accepted == false must always carry a reason. A silently-skipped provider is the one
            // thing this report exists to make impossible.
            Assert.True(
                attempt.Accepted ^ attempt.Failure is not null,
                $"{attempt.Provider}: accepted={attempt.Accepted}, failure={attempt.Failure ?? "<none>"}");
        }

        // Recorded, never asserted: no Assert.Equal(CoreMl, report.Accepted) here or anywhere. Both
        // Apple lanes are macos-15-intel with x64 RIDs, so an EP claim there would be a claim about
        // Rosetta. The lane's trx/JUnit conversion carries this line out of the run.
        TestContext.Current.TestOutputHelper?.WriteLine(Describe(report));
    }

    /// <summary>
    /// Flat text rather than <c>JsonSerializer.Serialize</c>: the reflection-based serializer is not
    /// AOT-safe and this repo's only JSON path is source-generated (<c>ModelMarkerJsonContext</c>).
    /// A device lane's value here is the text in the log, not its shape.
    /// </summary>
    private static string Describe(ExecutionProviderReport report)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"executionProviders.accepted={report.Accepted}");

        foreach (var attempt in report.Attempts)
        {
            text.AppendLine();
            text.Append(
                CultureInfo.InvariantCulture,
                $"  attempt provider={attempt.Provider} accepted={attempt.Accepted} failure={attempt.Failure ?? "<none>"}");

            foreach (var option in attempt.Options.OrderBy(o => o.Key, StringComparer.Ordinal))
            {
                text.Append(CultureInfo.InvariantCulture, $" {option.Key}={option.Value}");
            }
        }

        return text.ToString();
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _modelPaths.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
