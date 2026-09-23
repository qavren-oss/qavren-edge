using System.Globalization;

namespace Qavren.Edge;

/// <summary>Base type for every fault Qavren.Edge raises deliberately.</summary>
public abstract class EdgeException : Exception
{
    private const string DocsBase =
        "https://github.com/qavren-oss/qavren-edge/blob/main/foundation/docs/errors.md#";

    /// <summary>Sets <see cref="Code"/> and derives <see cref="Exception.HelpLink"/> from it.</summary>
    /// <param name="code">The stable error identity for this fault.</param>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    protected EdgeException(EdgeErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        HelpLink = DocsBase + ((int)code).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The stable, programmatically handleable identity of this fault.</summary>
    public EdgeErrorCode Code { get; }
}

/// <summary>Bad wiring: detected either when the container is built or by startup task 0.</summary>
public sealed class EdgeConfigurationException : EdgeException
{
    /// <summary>Creates the exception with the given <paramref name="code"/> and <paramref name="message"/>.</summary>
    /// <param name="code">The stable error identity for this fault.</param>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public EdgeConfigurationException(EdgeErrorCode code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }
}

/// <summary>The native SQLite library could not be loaded or failed verification.</summary>
public sealed class EdgeNativeException : EdgeException
{
    /// <summary>Creates the exception, always with <see cref="EdgeErrorCode.NativeLoadFailed"/>.</summary>
    /// <param name="runtimeIdentifier">The runtime identifier the native library was probed for.</param>
    /// <param name="libraryName">The native library's file name.</param>
    /// <param name="probedPaths">Every path that was probed and rejected.</param>
    /// <param name="remediation">A short, actionable next step, folded into the exception message.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
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

    /// <summary>The runtime identifier the native library was probed for.</summary>
    public string RuntimeIdentifier { get; }

    /// <summary>The native library's file name.</summary>
    public string LibraryName { get; }

    /// <summary>Every path that was probed and rejected.</summary>
    public IReadOnlyList<string> ProbedPaths { get; }

    /// <summary>A short, actionable next step.</summary>
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
    /// <summary>Creates the exception, always with <see cref="EdgeErrorCode.MigrationFailed"/>.</summary>
    /// <param name="version">The migration's version number.</param>
    /// <param name="name">The migration's name.</param>
    /// <param name="innerException">The exception the migration itself threw.</param>
    public EdgeMigrationException(int version, string name, Exception? innerException = null)
        : base(EdgeErrorCode.MigrationFailed,
               $"Migration {version.ToString(CultureInfo.InvariantCulture)} '{name}' failed.",
               innerException)
    {
        Version = version;
        Name = name;
    }

    /// <summary>The migration's version number.</summary>
    public int Version { get; }

    /// <summary>The migration's name.</summary>
    public string Name { get; }
}

/// <summary>An encrypted database refused the supplied key.</summary>
public sealed class EdgeDatabaseKeyException : EdgeException
{
    /// <summary>Creates the exception, always with <see cref="EdgeErrorCode.DatabaseKeyRejected"/>.</summary>
    /// <param name="databaseName">The name the database was registered under.</param>
    /// <param name="innerException">The underlying SQLite exception, if any.</param>
    public EdgeDatabaseKeyException(string databaseName, Exception? innerException = null)
        : base(EdgeErrorCode.DatabaseKeyRejected,
               $"Database '{databaseName}' could not be opened with the supplied key. " +
               "Either the key is wrong or the file is not a SQLCipher database.",
               innerException)
    {
        DatabaseName = databaseName;
    }

    /// <summary>The name the database was registered under.</summary>
    public string DatabaseName { get; }
}
