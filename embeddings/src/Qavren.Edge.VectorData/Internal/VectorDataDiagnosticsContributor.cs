using System.Globalization;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Sqlite;

namespace Qavren.Edge.VectorData.Internal;

/// <summary>
/// Reports the vector-store half of a support request. The component name deliberately contains no
/// "Native", so sub-project 1's <c>EdgeDiagnostics.Report()</c> keeps picking the SQLite native
/// block for <c>EdgeDiagnosticsReport.Native</c>.
/// <para>
/// <c>rowCount</c> / <c>vecRowCount</c> / <c>ftsRowCount</c> appear only when
/// <see cref="EdgeVectorStoreOptions.IncludeRowCountsInDiagnostics"/> is set: counting rows is one
/// query per collection. The first two diverging means an upsert transaction was interrupted, and
/// is the first thing to look at in a bug report.
/// </para>
/// </summary>
internal sealed class VectorDataDiagnosticsContributor : IEdgeDiagnosticsContributor
{
    private readonly IEnumerable<IEdgeDatabase> _databases;
    private readonly EdgeVectorCollectionRegistry _registry;

    /// <summary>Creates the contributor.</summary>
    /// <param name="databases">Every registered database.</param>
    /// <param name="registry">The collections the builder registered.</param>
    public VectorDataDiagnosticsContributor(
        IEnumerable<IEdgeDatabase> databases,
        EdgeVectorCollectionRegistry registry)
    {
        _databases = databases;
        _registry = registry;
    }

    /// <inheritdoc />
    public string ComponentName => "Qavren.Edge.VectorData";

    /// <inheritdoc />
    public string? ComponentVersion =>
        typeof(VectorDataDiagnosticsContributor).Assembly.GetName().Version?.ToString();

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string?> Describe()
    {
        var details = new Dictionary<string, string?>(StringComparer.Ordinal);
        var databases = _databases.ToArray();

        foreach (var database in databases)
        {
            details[$"database[{database.Name}]"] = database.Path;

            var info = TryGetInfo(database);
            details[$"database[{database.Name}].sqliteVersion"] = info?.SqliteVersion;

            // vec_version() returns "v0.1.9" - WITH the leading v, as sub-project 1 documents.
            details[$"database[{database.Name}].vecVersion"] = info?.VecVersion;
        }

        foreach (var registration in _registry.Registrations)
        {
            var prefix = $"collection[{registration.CollectionName}].";
            details[prefix + "database"] = registration.DatabaseName;
            details[prefix + "dataTable"] = registration.DataTable;
            details[prefix + "vecTable"] = registration.VectorTable;
            details[prefix + "ftsTable"] = registration.FullTextTable;
            details[prefix + "dimensions"] = Text(registration.Dimensions);
            details[prefix + "distanceFunction"] = registration.DistanceFunction;
            details[prefix + "chunkSize"] = Text(registration.ChunkSize);
            details[prefix + "ftsTokenizer"] = registration.FullTextTokenizer;
            details[prefix + "keyType"] = registration.KeyType;
            details[prefix + "generator"] = registration.Generator;
            details[prefix + "generatorDimensions"] = registration.GeneratorDimensions is { } g ? Text(g) : null;
            details[prefix + "rrfK"] = Text(registration.RrfK);
            details[prefix + "weights"] =
                registration.VectorWeight.ToString("0.###", CultureInfo.InvariantCulture) + "/" +
                registration.KeywordWeight.ToString("0.###", CultureInfo.InvariantCulture);

            if (!registration.IncludeRowCounts)
            {
                continue;
            }

            var database = databases.FirstOrDefault(d =>
                string.Equals(d.Name, registration.DatabaseName, StringComparison.Ordinal));
            if (database is null)
            {
                continue;
            }

            details[prefix + "rowCount"] = TryCount(database, registration.DataTable);
            details[prefix + "vecRowCount"] = TryCount(database, registration.VectorTable);
            details[prefix + "ftsRowCount"] = registration.FullTextTable is { } fts
                ? TryCount(database, fts)
                : null;
        }

        return details;
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    // Describe() is synchronous by contract, and both calls below are single short statements
    // against an already-open pool. Task.Run keeps them off whatever synchronisation context the
    // caller happens to be on, and any failure degrades to a null value rather than a broken report.
    private static SqliteDatabaseInfo? TryGetInfo(IEdgeDatabase database)
    {
        try
        {
            return Task.Run(() => database.GetInfoAsync(CancellationToken.None)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static string? TryCount(IEdgeDatabase database, string table)
    {
        try
        {
            return Task.Run(async () =>
            {
                var connection = await database.OpenConnectionAsync(CancellationToken.None).ConfigureAwait(false);
                await using (connection.ConfigureAwait(false))
                {
                    var count = await connection
                        .ScalarAsync<long>($"SELECT count(*) FROM \"{table}\"")
                        .ConfigureAwait(false);
                    return count.ToString(CultureInfo.InvariantCulture);
                }
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
