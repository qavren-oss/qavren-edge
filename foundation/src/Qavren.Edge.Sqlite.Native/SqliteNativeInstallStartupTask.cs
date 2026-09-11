using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Sqlite.Native;

/// <summary>Startup task order 0. Everything else in the suite depends on this having run.</summary>
/// <remarks>
/// Owns plan adjustment 14: the freeze knob is <see cref="EdgeOptions.FreezeSqliteProvider"/>
/// (default <see langword="true"/>), read here and pushed onto the provider before
/// <see cref="ISqliteNativeProvider.Install"/> runs, so a test that sets it <see langword="false"/>
/// really can swap providers afterwards.
/// </remarks>
public sealed class SqliteNativeInstallStartupTask(
    ISqliteNativeProvider provider,
    IOptions<EdgeOptions> options,
    ILogger<SqliteNativeInstallStartupTask> logger) : IEdgeStartupTask
{
    // CA1848/CA1873: cached delegates. The plan writes these as inline ILogger.LogInformation /
    // LogError calls, which this repo's TreatWarningsAsErrors + latest-recommended analysis level
    // rejects. LoggerMessage.Define keeps the same event ids, levels and message templates.
    private static readonly Action<ILogger, string, string, string, string, string, string, Exception?> s_installed =
        LoggerMessage.Define<string, string, string, string, string, string>(
            LogLevel.Information,
            EdgeEventIds.NativeProviderInstalled,
            "Installed {Provider} from {Path}: sqlite {Sqlite}, vec {Vec}, cipher {Cipher}, build {Build}.");

    private static readonly Action<ILogger, Exception?> s_installFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            EdgeEventIds.NativeProviderFailed,
            "Native provider install failed.");

    /// <inheritdoc />
    public int Order => EdgeStartupOrder.NativeProviderInstall;

    /// <inheritdoc />
    public Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (provider is QedgeSqliteNativeProvider qedge)
            {
                // An explicit FreezeProvider from UseSqliteNative(configure) wins; otherwise the
                // EdgeOptions knob decides. See plan adjustment 14.
                qedge.FreezeProvider ??= options.Value.FreezeSqliteProvider;
            }

            provider.Install();
            var info = provider.Describe();
            s_installed(
                logger,
                info.ProviderName,
                info.ResolvedPath ?? "(unknown)",
                info.SqliteVersion,
                info.VecVersion,
                info.CipherVersion,
                info.BuildSha,
                null);
        }
        catch (EdgeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            s_installFailed(logger, ex);
            throw new EdgeNativeException(
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                provider.LibraryName,
                [],
                "Reference Qavren.Edge.Sqlite.Native or Qavren.Edge.Sqlite.Native.Cipher for this platform.",
                ex);
        }

        return Task.CompletedTask;
    }
}
