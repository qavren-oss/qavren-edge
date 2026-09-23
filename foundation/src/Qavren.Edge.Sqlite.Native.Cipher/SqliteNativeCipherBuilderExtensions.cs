using Microsoft.Extensions.DependencyInjection;
using Qavren.Edge.Hosting;

namespace Qavren.Edge.Sqlite.Native.Cipher;

/// <summary>Registers <see cref="QedgeSqlCipherNativeProvider"/> on an <see cref="EdgeBuilder"/>.</summary>
public static class SqliteNativeCipherBuilderExtensions
{
    /// <summary>
    /// Registers the SQLCipher native provider. Calling this together with <c>UseSqliteNative()</c>
    /// is a configuration error caught when a database is resolved.
    /// </summary>
    public static EdgeBuilder UseSqliteNativeCipher(
        this EdgeBuilder builder,
        Action<QedgeSqlCipherNativeProvider>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var provider = new QedgeSqlCipherNativeProvider();
        configure?.Invoke(provider);

        builder.Services.AddSingleton<Qavren.Edge.Sqlite.ISqliteNativeProvider>(provider);
        builder.Services.AddSingleton<IEdgeStartupTask, SqliteNativeInstallStartupTask>();
        return builder;
    }
}
