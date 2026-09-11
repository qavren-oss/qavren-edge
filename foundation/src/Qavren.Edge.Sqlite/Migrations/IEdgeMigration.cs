using Microsoft.Data.Sqlite;

namespace Qavren.Edge.Sqlite;

/// <summary>
/// A forward-only schema change. Registration is explicit and AOT-safe: there is no assembly scanning,
/// and there are no down-migrations.
/// </summary>
public interface IEdgeMigration
{
    /// <summary>Unique and ascending across a database. Stored in <c>PRAGMA user_version</c>.</summary>
    int Version { get; }

    /// <summary>A human-readable name, used in logs and in <see cref="EdgeMigrationException"/>.</summary>
    string Name { get; }

    /// <summary>Applies the change. Runs inside a transaction the migrator owns.</summary>
    Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken);
}
