using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Microsoft.Extensions.VectorData.ProviderServices;
using Qavren.Edge.Sqlite.Vec;

namespace Qavren.Edge.VectorData.Internal;

/// <summary>
/// Maps a record type onto the three-table schema of spec 12.1. Every rejection here happens at
/// model-build time, before a connection is opened, and carries an <see cref="EdgeErrorCode"/>.
/// </summary>
public sealed class EdgeCollectionModelBuilder : CollectionModelBuilder
{
    /// <summary>
    /// The rowid alias every collection is keyed on. Not configurable: it appears in
    /// <c>content_rowid='_rowid'</c>, in four trigger bodies and in every query, and a configurable
    /// one would buy nothing but a second thing to get wrong.
    /// </summary>
    public const string RowIdColumn = "_rowid";

    private static readonly EventId IndexKindIgnoredEvent = new(804, "IndexKindIgnored");

    // Spec 14.4: every call site in the 600-899 range goes through LoggerMessage.Define. The
    // event id is a literal rather than EdgeAiEventIds.IndexKindIgnored because that constant
    // lives in Qavren.Edge.Onnx, which this package deliberately does not reference (spec 2
    // decision 2). CollectionModelTests.AnIndexKindIsAcceptedAndIgnoredButNeverSilently
    // pins this literal to EdgeAiEventIds.IndexKindIgnored - that test project is the only place
    // in the suite where both are visible at once.
    private static readonly Action<ILogger, string, string, string, Exception?> s_indexKindIgnored =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            IndexKindIgnoredEvent,
            "Vector property '{Property}' of collection '{Collection}' requests IndexKind '{IndexKind}'. " +
            "vec0 is brute-force and flat; the index kind is ignored.");

    private readonly string _collectionName;
    private readonly ILogger? _logger;

    /// <summary>Creates the builder for one collection.</summary>
    /// <param name="collectionName">Named in every <see cref="EdgeVectorModelException"/> this builder raises.</param>
    /// <param name="logger">Receives the ignored-<c>IndexKind</c> warning (event 804).</param>
    public EdgeCollectionModelBuilder(string collectionName, ILogger? logger = null)
        // CollectionModelBuildingOptions.ReservedKeyStorageName is deliberately NOT set to
        // "_rowid". MEVD does not read that option as "reserve this name against the key"; it reads
        // it as "the key's storage name IS this", and sets KeyProperty.StorageName to it - verified
        // against Microsoft.Extensions.VectorData.Abstractions 10.10.0. Setting it here would
        // rename the key column to "_rowid" and emit a CREATE TABLE with that column twice, which
        // is the exact failure the reservation exists to prevent. The reservation is enforced in
        // ValidateProperty instead, over key, data AND vector properties - stricter than the option
        // ever was, since the option only covers the key.
        : base(new CollectionModelBuildingOptions
        {
            SupportsMultipleVectors = false,
            RequiresAtLeastOneVector = true,
        })
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        _collectionName = collectionName;
        _logger = logger;
    }

    /// <summary>
    /// Float32 and nothing else. This single-entry list is the seam MEVD uses to decide which
    /// <see cref="Embedding{T}"/> subtype a configured generator may produce: it is what makes a
    /// <see cref="string"/> source property resolve to <see cref="Embedding{T}"/> of
    /// <see cref="float"/>, and what makes a generator producing <c>Embedding&lt;sbyte&gt;</c> or
    /// <c>BinaryEmbedding</c> fail at model build with a named error rather than at the vec0
    /// insert. vec0's <c>int8</c> and <c>bit</c> element types are cut from v1, and this is the one
    /// place that cut is enforced rather than merely documented.
    /// </summary>
    protected override IReadOnlyList<EmbeddingGenerationDispatcher> EmbeddingGenerationDispatchers { get; } =
        [EmbeddingGenerationDispatcher.Create<Embedding<float>>()];

    /// <summary>Maps an MEVD distance function onto a vec0 <c>distance_metric</c>.</summary>
    /// <param name="distanceFunction">The MEVD distance function, or null for the default.</param>
    /// <param name="metric">The vec0 metric.</param>
    /// <returns><see langword="true"/> when vec0 can compute this distance.</returns>
    public static bool TryMapDistanceFunction(string? distanceFunction, out VecMetric metric)
    {
        switch (distanceFunction)
        {
            case null:
            case "":
            case DistanceFunction.CosineDistance:
                metric = VecMetric.Cosine;
                return true;
            case DistanceFunction.EuclideanDistance:
                metric = VecMetric.L2;
                return true;
            case DistanceFunction.ManhattanDistance:
                metric = VecMetric.L1;
                return true;
            default:
                metric = VecMetric.Cosine;
                return false;
        }
    }

    /// <inheritdoc />
    protected override bool IsDataPropertyTypeValid(Type type, [NotNullWhen(false)] out string? supportedTypes)
    {
        if (SqliteTypeMap.IsSupportedDataType(type))
        {
            supportedTypes = null;
            return true;
        }

        supportedTypes = SqliteTypeMap.SupportedDataTypes;
        return false;
    }

    /// <inheritdoc />
    protected override bool IsVectorPropertyTypeValid(Type type, [NotNullWhen(false)] out string? supportedTypes)
    {
        if (SqliteTypeMap.IsSupportedVectorType(type))
        {
            supportedTypes = null;
            return true;
        }

        supportedTypes = SqliteTypeMap.SupportedVectorTypes;
        return false;
    }

    /// <inheritdoc />
    protected override void ValidateKeyProperty(KeyPropertyModel keyProperty)
    {
        ArgumentNullException.ThrowIfNull(keyProperty);

        if (!SqliteTypeMap.IsSupportedKeyType(keyProperty.Type))
        {
            throw new EdgeVectorModelException(
                EdgeErrorCode.UnsupportedKeyType,
                _collectionName,
                keyProperty.ModelName,
                $"Key property '{keyProperty.ModelName}' has type '{keyProperty.Type}', which this provider cannot store. " +
                $"Supported key types: {SqliteTypeMap.SupportedKeyTypes}.");
        }

        base.ValidateKeyProperty(keyProperty);
    }

    /// <inheritdoc />
    protected override bool SupportsKeyAutoGeneration(Type keyPropertyType) =>
        SqliteTypeMap.IsAutoGeneratableKeyType(keyPropertyType);

    /// <inheritdoc />
    protected override void ValidateProperty(PropertyModel propertyModel, VectorStoreCollectionDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(propertyModel);

        ValidateNotReserved(propertyModel, definition);

        switch (propertyModel)
        {
            case VectorPropertyModel vector:
                ValidateVectorProperty(vector);
                break;

            case DataPropertyModel data when !SqliteTypeMap.IsSupportedDataType(data.Type):
                throw new EdgeVectorModelException(
                    EdgeErrorCode.UnsupportedPropertyType,
                    _collectionName,
                    data.ModelName,
                    $"Data property '{data.ModelName}' has type '{data.Type}', which this provider cannot store. " +
                    $"Supported types: {SqliteTypeMap.SupportedDataTypes}.");

            default:
                break;
        }

        base.ValidateProperty(propertyModel, definition);
    }

    /// <inheritdoc />
    protected override void Validate(Type? type, VectorStoreCollectionDefinition? definition)
    {
        if (VectorProperties.Count > 1)
        {
            throw new EdgeVectorModelException(
                EdgeErrorCode.MultipleVectorPropertiesUnsupported,
                _collectionName,
                VectorProperties[1].ModelName,
                $"This provider stores exactly one vector per collection, and the record declares {VectorProperties.Count.ToString(CultureInfo.InvariantCulture)} " +
                "(a second vector property means a second vec0 table and a materially worse hybrid query). " +
                "Split the record across two collections instead.");
        }

        base.Validate(type, definition);
    }

    private void ValidateVectorProperty(VectorPropertyModel vector)
    {
        if (Nullable.GetUnderlyingType(vector.Type) is not null)
        {
            throw new EdgeVectorModelException(
                EdgeErrorCode.NullableVectorProperty,
                _collectionName,
                vector.ModelName,
                $"Vector property '{vector.ModelName}' is nullable. vec0 overloads SQL NULL on a vector column to mean " +
                "'no change', so writing a null vector is a silent no-op rather than an error. Declare the property " +
                "non-nullable so the trap is unreachable.");
        }

        if (!TryMapDistanceFunction(vector.DistanceFunction, out _))
        {
            throw new EdgeVectorModelException(
                EdgeErrorCode.UnsupportedDistanceFunction,
                _collectionName,
                vector.ModelName,
                $"Vector property '{vector.ModelName}' asks for distance function '{vector.DistanceFunction}', which vec0 cannot compute. " +
                $"Supported: {DistanceFunction.CosineDistance}, {DistanceFunction.EuclideanDistance}, {DistanceFunction.ManhattanDistance}.");
        }

        if (!string.IsNullOrEmpty(vector.IndexKind) && vector.IndexKind != IndexKind.Flat)
        {
            // Accepted and ignored, but never silently: vec0 is brute-force and flat, and scanning
            // linearly while reporting an HNSW index is worse than saying so.
            if (_logger is not null)
            {
                s_indexKindIgnored(_logger, vector.ModelName, _collectionName, vector.IndexKind, null);
            }
        }
    }

    private void ValidateNotReserved(PropertyModel propertyModel, VectorStoreCollectionDefinition? definition)
    {
        // The check is against the name the USER asked for, not against PropertyModel.StorageName:
        // MEVD may itself assign a reserved storage name, and a provider that re-reads its own
        // assignment would reject the very model it just built.
        var requested = DeclaredStorageName(propertyModel, definition) ?? propertyModel.ModelName;

        if (string.Equals(requested, RowIdColumn, StringComparison.OrdinalIgnoreCase))
        {
            throw new EdgeVectorModelException(
                EdgeErrorCode.ReservedColumnName,
                _collectionName,
                propertyModel.ModelName,
                $"Property '{propertyModel.ModelName}' claims the storage name '{RowIdColumn}', which this provider reserves for the " +
                "rowid alias every collection is keyed on. Rename the property or give it a different StorageName.");
        }
    }

    private static string? DeclaredStorageName(PropertyModel propertyModel, VectorStoreCollectionDefinition? definition)
    {
        var fromDefinition = definition?.Properties?
            .FirstOrDefault(p => string.Equals(p.Name, propertyModel.ModelName, StringComparison.Ordinal))?
            .StorageName;

        if (!string.IsNullOrEmpty(fromDefinition))
        {
            return fromDefinition;
        }

        var clrProperty = propertyModel.PropertyInfo;
        if (clrProperty is null)
        {
            return null;
        }

        return clrProperty.GetCustomAttribute<VectorStoreKeyAttribute>()?.StorageName
            ?? clrProperty.GetCustomAttribute<VectorStoreDataAttribute>()?.StorageName
            ?? clrProperty.GetCustomAttribute<VectorStoreVectorAttribute>()?.StorageName;
    }
}
