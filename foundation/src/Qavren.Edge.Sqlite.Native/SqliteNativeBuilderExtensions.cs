using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Sqlite.Native;

/// <summary>Registers <see cref="QedgeSqliteNativeProvider"/> on an <see cref="EdgeBuilder"/>.</summary>
public static class SqliteNativeBuilderExtensions
{
    /// <summary>
    /// Registers the plain (unencrypted) native SQLite provider. Calling this together with
    /// <c>UseSqliteNativeCipher()</c> is a configuration error caught when a database is resolved.
    /// </summary>
    public static EdgeBuilder UseSqliteNative(this EdgeBuilder builder, Action<QedgeSqliteNativeProvider>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var provider = new QedgeSqliteNativeProvider();
        configure?.Invoke(provider);

        builder.Services.AddSingleton<ISqliteNativeProvider>(provider);
        builder.Services.AddSingleton<IEdgeStartupTask, SqliteNativeInstallStartupTask>();
        builder.Services.AddSingleton<IEdgeDiagnosticsContributor>(new NativeDiagnosticsContributor(provider));
        return builder;
    }

    private sealed class NativeDiagnosticsContributor(ISqliteNativeProvider provider) : IEdgeDiagnosticsContributor
    {
        public string ComponentName => "Qavren.Edge.Sqlite.Native";

        public string? ComponentVersion => typeof(NativeDiagnosticsContributor).Assembly.GetName().Version?.ToString();

        public IReadOnlyDictionary<string, string?> Describe()
        {
            try
            {
                var info = provider.Describe();
                return new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["providerName"] = info.ProviderName,
                    ["libraryName"] = info.LibraryName,
                    ["resolvedPath"] = info.ResolvedPath,
                    ["sqliteVersion"] = info.SqliteVersion,
                    ["vecVersion"] = info.VecVersion,
                    ["cipherVersion"] = info.CipherVersion,
                    ["buildSha"] = info.BuildSha,
                };
            }
            catch (InvalidOperationException)
            {
                return new Dictionary<string, string?>(StringComparer.Ordinal) { ["state"] = "not installed" };
            }
        }
    }
}
