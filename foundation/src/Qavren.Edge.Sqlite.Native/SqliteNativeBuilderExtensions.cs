using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Sqlite.Native;

/// <summary>Registers <see cref="QedgeSqliteNativeProvider"/> on an <see cref="EdgeBuilder"/>.</summary>
public static class SqliteNativeBuilderExtensions
{
    /// <summary>
    /// Registers the plain (unencrypted) native SQLite provider. Calling this together with
    /// <c>UseSqliteNativeCipher()</c> is a configuration error that faults startup when the first
    /// database is opened.
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
}
