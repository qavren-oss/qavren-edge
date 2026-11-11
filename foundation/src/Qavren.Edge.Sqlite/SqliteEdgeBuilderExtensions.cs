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

        // Nothing registered here may need the native provider in order to be CONSTRUCTED. The host
        // resolves every startup task before it runs the first, and the diagnostics service resolves
        // every contributor before it reports on one, so a provider taken as a constructor argument
        // made "none registered" a container error out of GetRequiredService<IEdgeHost>() - no
        // startup, no fault on Started, no report. Each of them takes an accessor, or nothing, and
        // the database looks when it is first opened: inside a startup task, where it is a fault.
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEdgeDiagnosticsContributor, SqliteDiagnosticsContributor>(
                sp => new SqliteDiagnosticsContributor(sp.GetServices<IEdgeDatabase>(), () => RequireNative(sp))));
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEdgeLifecycleObserver, SqliteLifecycleObserver>());

        // The host is an accessor for a second reason: the startup tasks below depend on this
        // database, so resolving IEdgeHost here would close a container cycle.
        builder.Services.AddKeyedSingleton<EdgeDatabase>(key, (sp, _) => new EdgeDatabase(
            key,
            sp.GetRequiredService<IOptionsMonitor<SqliteOptions>>().Get(key),
            sp.GetRequiredService<IEdgeHost>,
            sp.GetRequiredService<IEdgePaths>(),
            () => RequireNative(sp),
            sp.GetRequiredService<ILogger<EdgeDatabase>>()));

        builder.Services.AddKeyedSingleton<IEdgeDatabase>(key, (sp, k) => sp.GetRequiredKeyedService<EdgeDatabase>(k));
        builder.Services.AddSingleton<IEdgeDatabase>(new UnkeyedDatabase(key).Resolve);
        KeepTheDefaultDatabaseLast(builder.Services);

        builder.Services.AddSingleton<IEdgeStartupTask>(sp => new SqliteOpenStartupTask(
            sp.GetRequiredKeyedService<EdgeDatabase>(key),
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

    /// <summary>
    /// <c>GetRequiredService&lt;IEdgeDatabase&gt;()</c> returns the LAST unkeyed registration, and
    /// every consumer that takes "no database name" means the unnamed database by it - the vector
    /// store and the ingestion pipeline both resolve that way while their migrations go to
    /// <see cref="SqliteRegistry.DefaultName"/>. So when the unnamed database is registered at all,
    /// its unkeyed registration is kept last, whichever order the <c>AddSqlite</c> calls came in.
    /// Nothing is removed: <c>IEnumerable&lt;IEdgeDatabase&gt;</c> still lists every database.
    /// </summary>
    private static void KeepTheDefaultDatabaseLast(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType == typeof(IEdgeDatabase)
                && !descriptor.IsKeyedService
                && descriptor.ImplementationFactory?.Target is UnkeyedDatabase { IsDefault: true })
            {
                if (i != services.Count - 1)
                {
                    services.RemoveAt(i);
                    services.Add(descriptor);
                }

                return;
            }
        }
    }

    /// <summary>
    /// The unkeyed registration's factory, as an object rather than a lambda so the descriptor of
    /// the unnamed database can be told apart from a named one's without reflecting over a closure.
    /// </summary>
    private sealed class UnkeyedDatabase(string key)
    {
        public bool IsDefault => string.Equals(key, SqliteRegistry.DefaultName, StringComparison.Ordinal);

        public IEdgeDatabase Resolve(IServiceProvider services) => services.GetRequiredKeyedService<IEdgeDatabase>(key);
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
