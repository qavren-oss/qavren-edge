namespace Qavren.Edge.Sqlite.Internal;

/// <summary>Build-time bookkeeping so duplicate names and version conflicts fail before the container is built.</summary>
public sealed class SqliteRegistry
{
    /// <summary>The key the unnamed database registers under.</summary>
    public const string DefaultName = "(default)";

    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> _migrationVersions = new(StringComparer.Ordinal);

    /// <summary>Every database name registered so far.</summary>
    public IReadOnlyCollection<string> Names => _names;

    /// <summary>Records a database name, rejecting a second registration of the same one.</summary>
    public void AddDatabase(string name)
    {
        if (!_names.Add(name))
        {
            throw new EdgeConfigurationException(
                EdgeErrorCode.DuplicateDatabaseName,
                $"A database named '{name}' is already registered. Give the second one a name: AddSqlite(\"corpus\", ...).");
        }

        _migrationVersions[name] = [];
    }

    /// <summary>Records a migration version, rejecting a duplicate on the same database.</summary>
    public void AddMigrationVersion(string databaseName, int version, string migrationName)
    {
        if (!_migrationVersions.TryGetValue(databaseName, out var versions))
        {
            throw new EdgeConfigurationException(
                EdgeErrorCode.DuplicateDatabaseName,
                $"AddMigration was called before AddSqlite for database '{databaseName}'.");
        }

        if (!versions.Add(version))
        {
            throw new EdgeConfigurationException(
                EdgeErrorCode.MigrationVersionConflict,
                $"Migration version {version} is registered twice on database '{databaseName}' " +
                $"(second one: '{migrationName}'). Versions must be unique and ascending.");
        }
    }
}
