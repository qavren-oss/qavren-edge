using System.Globalization;
using MEVD = Microsoft.Extensions.VectorData;

namespace Qavren.Edge.VectorData;

/// <summary>
/// The fixed vocabulary MEVD's <c>VectorStoreException.OperationName</c> is drawn from. It
/// matches the OpenTelemetry operation names upstream MEVD providers emit, so a trace reads the
/// same either side of a migration.
/// </summary>
public static class EdgeVectorStoreOperations
{
    /// <summary>Creating a collection and its sidecars.</summary>
    public const string CreateCollection = "CreateCollection";

    /// <summary>Dropping a collection and its sidecars.</summary>
    public const string DeleteCollection = "DeleteCollection";

    /// <summary>Reading records by key or by filter.</summary>
    public const string Get = "Get";

    /// <summary>Writing records.</summary>
    public const string Upsert = "Upsert";

    /// <summary>Deleting records.</summary>
    public const string Delete = "Delete";

    /// <summary>A vec0 KNN search.</summary>
    public const string VectorSearch = "VectorSearch";

    /// <summary>A vec0 + FTS5 reciprocal-rank-fusion search.</summary>
    public const string HybridSearch = "HybridSearch";

    /// <summary>Enumerating the data tables in the database.</summary>
    public const string ListCollectionNames = "ListCollectionNames";
}

/// <summary>
/// Derives from MEVD's <see cref="MEVD.VectorStoreException"/>, <b>not</b> from
/// <c>EdgeException</c> - the one deliberate break in the suite's hierarchy. The MEVD conformance
/// suite asserts the MEVD type, and Semantic Kernel, Agent Framework and generic retry middleware
/// all catch it; an Edge-only hierarchy would be invisible to every one of them. It still carries
/// the <see cref="EdgeErrorCode"/> and sets the docs <see cref="Exception.HelpLink"/> explicitly,
/// so Qavren's error contract holds on both sides of the line.
/// <para>
/// Note .NET MEVD 10.x has no <c>VectorStoreOperationException</c>; that type exists only in
/// Semantic Kernel's Python SDK.
/// </para>
/// </summary>
public sealed class EdgeVectorStoreException : MEVD.VectorStoreException
{
    private const string DocsBase =
        "https://github.com/qavren-oss/qavren-edge/blob/main/foundation/docs/errors.md#";

    /// <summary>The OpenTelemetry <c>db.system.name</c> value, matching upstream's SQLite connector.</summary>
    public const string SqliteSystemName = "sqlite";

    /// <summary>Creates the exception.</summary>
    /// <param name="code">The Qavren error code.</param>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause, usually a <c>SqliteException</c>.</param>
    public EdgeVectorStoreException(EdgeErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        VectorStoreSystemName = SqliteSystemName;
        HelpLink = DocsBase + ((int)code).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The Qavren error code, carried across the hierarchy break.</summary>
    public EdgeErrorCode Code { get; }
}

/// <summary>
/// Raised while <b>building</b> the collection model or validating the schema - always before any
/// SQL runs.
/// </summary>
public sealed class EdgeVectorModelException : EdgeException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">The Qavren error code.</param>
    /// <param name="collectionName">The collection whose model was being built.</param>
    /// <param name="propertyName">The offending property, when the fault has one.</param>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause, if any.</param>
    public EdgeVectorModelException(
        EdgeErrorCode code,
        string collectionName,
        string? propertyName,
        string message,
        Exception? innerException = null)
        : base(code, message, innerException)
    {
        CollectionName = collectionName;
        PropertyName = propertyName;
    }

    /// <summary>The collection whose model was being built.</summary>
    public string CollectionName { get; }

    /// <summary>The offending property, when the fault has one.</summary>
    public string? PropertyName { get; }
}
