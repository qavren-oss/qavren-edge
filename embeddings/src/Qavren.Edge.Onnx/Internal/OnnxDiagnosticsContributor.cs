using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Qavren.Edge.Diagnostics;

namespace Qavren.Edge.Onnx.Internal;

/// <summary>
/// Spec 14.3's ONNX block. The component name deliberately does NOT contain "Native", so SP1's
/// <c>EdgeDiagnostics.Report()</c> keeps picking the SQLite native block for
/// <c>EdgeDiagnosticsReport.Native</c>.
/// </summary>
/// <remarks>
/// The session host and the model store are resolved lazily out of <see cref="IServiceProvider"/>
/// for the same reason the lifecycle observer does it: the session host depends on
/// <c>IEdgeHost</c>, and a constructor dependency here would risk a cycle the moment anything on
/// the host's own construction path asked for diagnostics.
/// </remarks>
internal sealed class OnnxDiagnosticsContributor(
    IEdgeModelPaths modelPaths,
    IEdgeResourceMonitor monitor,
    OnnxEnvironmentState state,
    IOptions<OnnxOptions> options,
    IServiceProvider services) : IEdgeDiagnosticsContributor
{
    /// <inheritdoc />
    public string ComponentName => "Qavren.Edge.Onnx";

    /// <inheritdoc />
    public string? ComponentVersion
        => typeof(OnnxDiagnosticsContributor).Assembly.GetName().Version?.ToString();

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string?> Describe()
    {
        var runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        var snapshot = monitor.Read();
        var ortAssembly = typeof(OrtEnv).Assembly;

        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ortVersion"] = ortAssembly.GetName().Version?.ToString(),
            ["ortManagedAsset"] = ortAssembly
                .GetCustomAttributes(typeof(TargetFrameworkAttribute), inherit: false)
                .OfType<TargetFrameworkAttribute>()
                .FirstOrDefault()?.FrameworkName,
            ["runtimeIdentifier"] = runtimeIdentifier,
            ["unsupportedRuntime"] = (!ExecutionProviderPolicyResolver.IsRuntimeSupported(runtimeIdentifier))
                .ToString(CultureInfo.InvariantCulture),
            ["ortEnvironmentPreexisting"] = state.EnvironmentPreexisting.ToString(CultureInfo.InvariantCulture),
            ["sharedThreadPool"] = options.Value.ShareThreadPool?.ToString(CultureInfo.InvariantCulture) ?? "(auto)",
            ["modelsDirectory"] = Safe(() => modelPaths.Models),
            ["modelsDirectoryExcludedFromBackup"] = ExcludedFromBackup(),
            ["ortCacheDirectory"] = Safe(() => modelPaths.OrtCache),
            ["ortCacheBytes"] = Safe(() => DirectoryBytes(modelPaths.OrtCache).ToString(CultureInfo.InvariantCulture)),
            ["availableMemoryBytes"] = snapshot.AvailableMemoryBytes?.ToString(CultureInfo.InvariantCulture),
            ["isLowMemory"] = snapshot.IsLowMemory?.ToString(CultureInfo.InvariantCulture),
            ["thermalState"] = snapshot.Thermal.ToString(),
            ["thermalHeadroom"] = snapshot.ThermalHeadroom?.ToString(CultureInfo.InvariantCulture),
            ["isLowPowerMode"] = snapshot.IsLowPowerMode?.ToString(CultureInfo.InvariantCulture),
            ["lastMemoryPressure"] = snapshot.LastPressure?.ToString(),
        };

        var store = services.GetService<IOnnxModelStore>();
        if (store is not null)
        {
            details["provisionedModels"] = string.Join(", ", store.ProvisionedModelIds);
        }

        var host = services.GetService<IOnnxSessionHost>();
        if (host is not null)
        {
            foreach (var session in host.Sessions)
            {
                var prefix = $"session[{session.ModelId}].";
                details[prefix + "graphPath"] = session.GraphPath;
                details[prefix + "sha256"] = session.GraphSha256;
                details[prefix + "executionProviderAccepted"] = session.ExecutionProviders.Accepted.ToString();
                details[prefix + "executionProviderAttempts"] = string.Join(
                    ", ",
                    session.ExecutionProviders.Attempts.Select(
                        a => a.Accepted ? a.Provider.ToString() : $"{a.Provider}(skipped)"));
                details[prefix + "inputNames"] = string.Join(", ", session.Signature.InputNames);
                details[prefix + "outputNames"] = string.Join(", ", session.Signature.OutputNames);
                details[prefix + "loadMs"] = session.LoadDuration.TotalMilliseconds
                    .ToString("F1", CultureInfo.InvariantCulture);
                details[prefix + "loadCount"] = session.LoadCount.ToString(CultureInfo.InvariantCulture);
                details[prefix + "leases"] = session.ActiveLeases.ToString(CultureInfo.InvariantCulture);
                details[prefix + "loaded"] = session.IsLoaded.ToString(CultureInfo.InvariantCulture);
            }
        }

        return details;
    }

    private static string ExcludedFromBackup()
        => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst()
            ? bool.TrueString
            // Desktop has no cloud-backup exclusion to claim, and claiming "false" would read as a
            // defect rather than as "the question does not apply here".
            : "(n/a)";

    private static long DirectoryBytes(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            total += new FileInfo(file).Length;
        }

        return total;
    }

    private static string? Safe(Func<string> read)
    {
        try
        {
            return read();
        }
#pragma warning disable CA1031 // A diagnostics report must never be the thing that throws.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return $"(unavailable: {ex.GetType().Name})";
        }
    }
}
