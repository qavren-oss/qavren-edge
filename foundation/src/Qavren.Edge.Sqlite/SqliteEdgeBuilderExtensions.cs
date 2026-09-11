using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Sqlite.Internal;

namespace Qavren.Edge.Sqlite;

/// <summary>Registers SQLite databases and their migrations on the Qavren.Edge builder.</summary>
public static class SqliteEdgeBuilderExtensions
{
    private static SqliteRegistry Registry(EdgeBuilder builder)
    {
        var existing = builder.Services
            .FirstOrDefault(d => d.ServiceType == typeof(SqliteRegistry))?.ImplementationInstance as SqliteRegistry;
        if (existing is not null)
        {
            return existing;
        }

        var registry = new SqliteRegistry();
        builder.Services.AddSingleton(registry);
        return registry;
    }

    /// <summary>Registers the unnamed (default) database.</summary>
    public static EdgeBuilder AddSqlite(this EdgeBuilder builder, Action<SqliteOptions>? configure = null)
        => builder.AddSqlite(null, configure);

    /// <summary>Registers a database; <paramref name="name"/> keys the resolved services.</summary>
    public static EdgeBuilder AddSqlite(this EdgeBuilder builder, string? name, Action<SqliteOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var key = name ?? SqliteRegistry.DefaultName;
        Registry(builder).AddDatabase(key);

        builder.Services.Configure<SqliteOptions>(key, o => configure?.Invoke(o));

        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEdgeDiagnosticsContributor, SqliteDiagnosticsContributor>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEdgeLifecycleObserver, SqliteLifecycleObserver>());

        // The host and the provider are passed as accessors: the startup tasks below depend on this
        // database, so resolving IEdgeHost here would close a container cycle, and resolving the
        // native provider here would turn a missing provider into an unresolvable IEdgeDatabase
        // instead of the startup fault the contract promises.
        builder.Services.AddKeyedSingleton<EdgeDatabase>(key, (sp, _) => new EdgeDatabase(
            key,
            sp.GetRequiredService<IOptionsMonitor<SqliteOptions>>().Get(key),
            sp.GetRequiredService<IEdgeHost>,
            sp.GetRequiredService<IEdgePaths>(),
            () => RequireNative(sp),
            sp.GetRequiredService<ILogger<EdgeDatabase>>()));

        builder.Services.AddKeyedSingleton<IEdgeDatabase>(key, (sp, k) => sp.GetRequiredKeyedService<EdgeDatabase>(k));
        builder.Services.AddSingleton<IEdgeDatabase>(sp => sp.GetRequiredKeyedService<IEdgeDatabase>(key));

        builder.Services.AddSingleton<IEdgeStartupTask>(sp => new SqliteOpenStartupTask(
            sp.GetRequiredKeyedService<EdgeDatabase>(key),
            RequireNative(sp),
            sp.GetRequiredService<ILogger<SqliteOpenStartupTask>>()));

        builder.Services.AddSingleton<IEdgeStartupTask>(sp => new EdgeMigrator(
            sp.GetRequiredKeyedService<EdgeDatabase>(key),
            [.. sp.GetKeyedServices<IEdgeMigration>(key)],
            sp.GetRequiredService<ILogger<EdgeMigrator>>()));

        return builder;
    }

    /// <summary>Explicit, AOT-safe migration registration. There is no assembly scanning.</summary>
    public static EdgeBuilder AddMigration<TMigration>(this EdgeBuilder builder, string? databaseName = null)
        where TMigration : class, IEdgeMigration, new()
    {
        ArgumentNullException.ThrowIfNull(builder);

        var key = databaseName ?? SqliteRegistry.DefaultName;
        var migration = new TMigration();
        Registry(builder).AddMigrationVersion(key, migration.Version, migration.Name);
        builder.Services.AddKeyedSingleton<IEdgeMigration>(key, migration);
        return builder;
    }

    /// <summary>Registers a hand-built list of migrations against one database.</summary>
    public static EdgeBuilder AddMigrations(
        this EdgeBuilder builder,
        IEnumerable<IEdgeMigration> migrations,
        string? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(migrations);

        var key = databaseName ?? SqliteRegistry.DefaultName;
        var registry = Registry(builder);
        foreach (var migration in migrations)
        {
            registry.AddMigrationVersion(key, migration.Version, migration.Name);
            builder.Services.AddKeyedSingleton<IEdgeMigration>(key, migration);
        }

        return builder;
    }

    private static ISqliteNativeProvider RequireNative(IServiceProvider services)
    {
        var providers = services.GetServices<ISqliteNativeProvider>().ToArray();
        return providers.Length switch
        {
            1 => providers[0],
            0 => throw new EdgeConfigurationException(
                EdgeErrorCode.NoNativeProviderRegistered,
                "No SQLite native provider is registered. Reference Qavren.Edge.Sqlite.Native and call " +
                "UseSqliteNative(), or reference Qavren.Edge.Sqlite.Native.Cipher and call UseSqliteNativeCipher()."),
            _ => throw new EdgeConfigurationException(
                EdgeErrorCode.MultipleNativeProvidersRegistered,
                "More than one SQLite native provider is registered: " +
                string.Join(", ", providers.Select(p => p.Name)) +
                ". Reference exactly one of Qavren.Edge.Sqlite.Native and Qavren.Edge.Sqlite.Native.Cipher."),
        };
    }
}
