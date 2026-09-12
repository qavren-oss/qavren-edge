using Microsoft.Data.Sqlite;
using Qavren.Edge.Sqlite;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// Spec 6's three state tables, as one forward-only migration. It carries
/// <see cref="IngestionSchema.BuildStateSql"/>'s statements and runs inside the transaction SP1's
/// migrator owns, opening none of its own. There are no down-migrations in this suite.
/// </summary>
/// <param name="version">
/// <c>AddIngestion</c>'s <c>migrationVersion + 1</c> — the collection claims N, the state claims
/// N + 1 (plan adjustment 2).
/// </param>
/// <param name="tablePrefix">Validated against <c>^[A-Za-z_][A-Za-z0-9_]*$</c> by the caller.</param>
internal sealed class IngestionStateMigration(int version, string tablePrefix) : IEdgeMigration
{
    private readonly IReadOnlyList<string> _statements = IngestionSchema.BuildStateSql(tablePrefix);

    /// <inheritdoc />
    public int Version { get; } = version;

    /// <inheritdoc />
    public string Name { get; } = $"IngestionState({tablePrefix})";

    /// <inheritdoc />
    public async Task UpAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        foreach (var statement in _statements)
        {
            await connection.ExecuteAsync(statement, parameters: null, cancellationToken).ConfigureAwait(false);
        }

        await connection.ExecuteAsync(
            $"""INSERT OR REPLACE INTO "{tablePrefix}_meta"("key","value") VALUES($k,$v)""",
            [
                new SqliteParameter("$k", IngestionSchema.MetaSchemaVersionKey),
                new SqliteParameter("$v", IngestionSchema.StateSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ],
            cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
            $"""INSERT OR IGNORE INTO "{tablePrefix}_meta"("key","value") VALUES($k,$v)""",
            [
                new SqliteParameter("$k", IngestionSchema.MetaHashAlgorithmKey),
                new SqliteParameter("$v", ContentHash.AlgorithmId),
            ],
            cancellationToken).ConfigureAwait(false);
    }
}
