using System.Globalization;
using Qavren.Edge.Diagnostics;

namespace Qavren.Edge.Sqlite.Internal;

/// <summary>Reports the native provider and every registered database into the diagnostics report.</summary>
public sealed class SqliteDiagnosticsContributor(
    IEnumerable<IEdgeDatabase> databases,
    ISqliteNativeProvider native) : IEdgeDiagnosticsContributor
{
    /// <inheritdoc />
    public string ComponentName => "Qavren.Edge.Sqlite";

    /// <inheritdoc />
    public string? ComponentVersion => typeof(SqliteDiagnosticsContributor).Assembly.GetName().Version?.ToString();

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string?> Describe()
    {
        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["nativeProvider"] = native.Name,
            ["nativeLibrary"] = native.LibraryName,
            ["supportsEncryption"] = native.SupportsEncryption.ToString(CultureInfo.InvariantCulture),
        };

        foreach (var database in databases)
        {
            details[$"db[{database.Name}].path"] = database.Path;
            details[$"db[{database.Name}].encrypted"] = database.IsEncrypted.ToString(CultureInfo.InvariantCulture);
        }

        return details;
    }
}
