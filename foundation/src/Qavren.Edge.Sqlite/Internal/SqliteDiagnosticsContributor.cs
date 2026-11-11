using System.Globalization;
using Qavren.Edge.Diagnostics;

namespace Qavren.Edge.Sqlite.Internal;

/// <summary>Reports the native provider and every registered database into the diagnostics report.</summary>
public sealed class SqliteDiagnosticsContributor : IEdgeDiagnosticsContributor
{
    private readonly IEnumerable<IEdgeDatabase> _databases;
    private readonly Func<ISqliteNativeProvider> _native;

    /// <summary>Creates the contributor over an already-resolved native provider.</summary>
    /// <param name="databases">Every registered database.</param>
    /// <param name="native">The single registered native provider.</param>
    public SqliteDiagnosticsContributor(
        IEnumerable<IEdgeDatabase> databases,
        ISqliteNativeProvider native)
        : this(databases, () => native)
    {
    }

    /// <summary>
    /// What <c>AddSqlite</c> registers. The provider is looked for when the report is asked for:
    /// taken as a constructor argument, a missing one made the whole report a container error, in
    /// exactly the situation a report is asked for.
    /// </summary>
    internal SqliteDiagnosticsContributor(
        IEnumerable<IEdgeDatabase> databases,
        Func<ISqliteNativeProvider> native)
    {
        _databases = databases;
        _native = native;
    }

    /// <inheritdoc />
    public string ComponentName => "Qavren.Edge.Sqlite";

    /// <inheritdoc />
    public string? ComponentVersion => typeof(SqliteDiagnosticsContributor).Assembly.GetName().Version?.ToString();

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string?> Describe()
    {
        var details = new Dictionary<string, string?>(StringComparer.Ordinal);

        try
        {
            var native = _native();
            details["nativeProvider"] = native.Name;
            details["nativeLibrary"] = native.LibraryName;
            details["supportsEncryption"] = native.SupportsEncryption.ToString(CultureInfo.InvariantCulture);
        }
        catch (EdgeConfigurationException ex)
        {
            // None registered, or two. The databases below are still worth reporting, and the code
            // is what the startup section's failed task is about.
            details["nativeProvider"] = "(unavailable: " + ex.Code.ToString() + ")";
        }

        foreach (var database in _databases)
        {
            details[$"db[{database.Name}].path"] = database.Path;
            details[$"db[{database.Name}].encrypted"] = database.IsEncrypted.ToString(CultureInfo.InvariantCulture);
        }

        return details;
    }
}
