using System.Globalization;

namespace Qavren.Edge;

/// <summary>Base type for every fault Qavren.Edge raises deliberately.</summary>
public abstract class EdgeException : Exception
{
    private const string DocsBase =
        "https://github.com/qavren-oss/qavren-edge/blob/main/foundation/docs/errors.md#";

    protected EdgeException(EdgeErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        HelpLink = DocsBase + ((int)code).ToString(CultureInfo.InvariantCulture);
    }

    public EdgeErrorCode Code { get; }
}

/// <summary>Bad wiring: detected either when the container is built or by startup task 0.</summary>
public sealed class EdgeConfigurationException : EdgeException
{
    public EdgeConfigurationException(EdgeErrorCode code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }
}

/// <summary>The native SQLite library could not be loaded or failed verification.</summary>
public sealed class EdgeNativeException : EdgeException
{
    public EdgeNativeException(
        string runtimeIdentifier,
        string libraryName,
        IReadOnlyList<string> probedPaths,
        string remediation,
        Exception? innerException = null)
        : base(EdgeErrorCode.NativeLoadFailed, Build(runtimeIdentifier, libraryName, probedPaths, remediation), innerException)
    {
        RuntimeIdentifier = runtimeIdentifier;
        LibraryName = libraryName;
        ProbedPaths = probedPaths;
        Remediation = remediation;
    }

    public string RuntimeIdentifier { get; }

    public string LibraryName { get; }

    public IReadOnlyList<string> ProbedPaths { get; }

    public string Remediation { get; }

    private static string Build(string rid, string library, IReadOnlyList<string> probed, string remediation)
    {
        var paths = probed.Count == 0 ? "(none)" : string.Join(Environment.NewLine + "  - ", probed);
        return $"Failed to load native SQLite library '{library}' for runtime identifier '{rid}'." +
               Environment.NewLine + "Probed:" + Environment.NewLine + "  - " + paths +
               Environment.NewLine + "Remediation: " + remediation;
    }
}

/// <summary>A migration failed; the database is left at the last successful user_version.</summary>
public sealed class EdgeMigrationException : EdgeException
{
    public EdgeMigrationException(int version, string name, Exception? innerException = null)
        : base(EdgeErrorCode.MigrationFailed,
               $"Migration {version.ToString(CultureInfo.InvariantCulture)} '{name}' failed.",
               innerException)
    {
        Version = version;
        Name = name;
    }

    public int Version { get; }

    public string Name { get; }
}

/// <summary>An encrypted database refused the supplied key.</summary>
public sealed class EdgeDatabaseKeyException : EdgeException
{
    public EdgeDatabaseKeyException(string databaseName, Exception? innerException = null)
        : base(EdgeErrorCode.DatabaseKeyRejected,
               $"Database '{databaseName}' could not be opened with the supplied key. " +
               "Either the key is wrong or the file is not a SQLCipher database.",
               innerException)
    {
        DatabaseName = databaseName;
    }

    public string DatabaseName { get; }
}
