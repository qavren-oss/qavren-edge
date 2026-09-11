using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Qavren.Edge.VectorData.Internal;
using Qavren.Edge.VectorData.Tests.Records;
using Xunit;

namespace Qavren.Edge.VectorData.Tests;

/// <summary>
/// Every rejection the model builder owns, asserted by its <see cref="EdgeErrorCode"/>. All of them
/// happen while the model is being built - before a connection is opened, let alone a statement run.
/// </summary>
public class CollectionModelTests
{
    [Fact]
    public void AStringSourcePropertyResolvesToAFloat32Embedding()
    {
        // The single-entry EmbeddingGenerationDispatchers list is the mechanical expression of
        // "float32 only": vec0's int8 and bit element types are cut from v1.
        var model = ModelFactory.ModelFor<Note>();

        Assert.Equal(typeof(Embedding<float>), model.VectorProperty.EmbeddingType);
        Assert.Equal(384, model.VectorProperty.Dimensions);
        Assert.Equal(DistanceFunction.CosineDistance, model.VectorProperty.DistanceFunction);
    }

    [Fact]
    public void AGeneratorThatProducesANonFloatEmbeddingFailsAtModelBuild()
    {
        var failure = Record.Exception(
            () => ModelFactory.ModelFor<Note>(generator: new SByteEmbeddingGenerator()));

        Assert.NotNull(failure);
    }

    [Fact]
    public void AnUnsupportedDataPropertyTypeIsRejected()
    {
        var ex = Assert.Throws<EdgeVectorModelException>(
            () => ModelFactory.ModelFor<UnsupportedDataTypeRecord>());

        Assert.Equal(EdgeErrorCode.UnsupportedPropertyType, ex.Code);
        Assert.Equal("Link", ex.PropertyName);
        Assert.Contains("byte[]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsupportedKeyTypeIsRejected()
    {
        var ex = Assert.Throws<EdgeVectorModelException>(
            () => ModelFactory.ModelFor<UnsupportedKeyTypeRecord>(keyType: typeof(double)));

        Assert.Equal(EdgeErrorCode.UnsupportedKeyType, ex.Code);
        Assert.Equal("Key", ex.PropertyName);
        Assert.Contains(SqliteTypeMap.SupportedKeyTypes, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(int))]
    [InlineData(typeof(long))]
    [InlineData(typeof(string))]
    [InlineData(typeof(Guid))]
    public void TheFourSupportedKeyTypesAreAccepted(Type keyType) =>
        Assert.True(SqliteTypeMap.IsSupportedKeyType(keyType));

    [Theory]
    [InlineData(typeof(Guid), true)]
    [InlineData(typeof(int), true)]
    [InlineData(typeof(long), true)]
    [InlineData(typeof(string), false)]
    public void KeyAutoGenerationIsSupportedForTheOrderedAndTheRowIdTypes(Type keyType, bool expected) =>
        Assert.Equal(expected, SqliteTypeMap.IsAutoGeneratableKeyType(keyType));

    [Fact]
    public void AnUnsupportedDistanceFunctionIsRejectedNamingTheThreeThatWork()
    {
        var ex = Assert.Throws<EdgeVectorModelException>(
            () => ModelFactory.ModelFor<UnsupportedDistanceRecord>());

        Assert.Equal(EdgeErrorCode.UnsupportedDistanceFunction, ex.Code);
        Assert.Equal("Embedding", ex.PropertyName);
        Assert.Contains(DistanceFunction.CosineDistance, ex.Message, StringComparison.Ordinal);
        Assert.Contains(DistanceFunction.EuclideanDistance, ex.Message, StringComparison.Ordinal);
        Assert.Contains(DistanceFunction.ManhattanDistance, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, VecMetricName.Cosine)]
    [InlineData(DistanceFunction.CosineDistance, VecMetricName.Cosine)]
    [InlineData(DistanceFunction.EuclideanDistance, VecMetricName.L2)]
    [InlineData(DistanceFunction.ManhattanDistance, VecMetricName.L1)]
    public void TheThreeSupportedDistanceFunctionsMapOntoVec0Metrics(string? distanceFunction, VecMetricName expected)
    {
        Assert.True(EdgeCollectionModelBuilder.TryMapDistanceFunction(distanceFunction, out var metric));
        Assert.Equal((int)expected, (int)metric);
    }

    [Theory]
    [InlineData(DistanceFunction.CosineSimilarity)]
    [InlineData(DistanceFunction.DotProductSimilarity)]
    [InlineData(DistanceFunction.NegativeDotProductSimilarity)]
    [InlineData(DistanceFunction.HammingDistance)]
    [InlineData(DistanceFunction.EuclideanSquaredDistance)]
    public void EveryOtherDistanceFunctionIsRefused(string distanceFunction) =>
        Assert.False(EdgeCollectionModelBuilder.TryMapDistanceFunction(distanceFunction, out _));

    [Fact]
    public void ANullableVectorPropertyIsRejectedBecauseVec0ReadsNullAsNoChange()
    {
        var ex = Assert.Throws<EdgeVectorModelException>(
            () => ModelFactory.ModelFor<NullableVectorRecord>());

        Assert.Equal(EdgeErrorCode.NullableVectorProperty, ex.Code);
        Assert.Equal("Embedding", ex.PropertyName);
        Assert.Contains("no change", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondVectorPropertyIsRejectedRatherThanSilentlyPicked()
    {
        var ex = Assert.Throws<EdgeVectorModelException>(
            () => ModelFactory.ModelFor<TwoVectorRecord>());

        Assert.Equal(EdgeErrorCode.MultipleVectorPropertiesUnsupported, ex.Code);
    }

    [Fact]
    public void APropertyCalledRowIdIsRejectedBeforeItReachesSql()
    {
        var ex = Assert.Throws<EdgeVectorModelException>(
            () => ModelFactory.ModelFor<RowIdNamedRecord>());

        Assert.Equal(EdgeErrorCode.ReservedColumnName, ex.Code);
        Assert.Equal("_rowid", ex.PropertyName);
        Assert.Contains("_rowid", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APropertyThatAsksForTheRowIdStorageNameIsRejectedNamingTheProperty()
    {
        var ex = Assert.Throws<EdgeVectorModelException>(
            () => ModelFactory.ModelFor<RowIdStorageNamedRecord>());

        Assert.Equal(EdgeErrorCode.ReservedColumnName, ex.Code);
        Assert.Equal("Label", ex.PropertyName);
    }

    [Fact]
    public void AKeyThatAsksForTheRowIdStorageNameIsRejectedToo()
    {
        // The reservation covers key, data AND vector properties. MEVD's
        // ReservedKeyStorageName option covers only the key - and by assigning the name rather than
        // refusing it, which is why it is not used.
        var ex = Assert.Throws<EdgeVectorModelException>(
            () => ModelFactory.ModelFor<RowIdKeyRecord>());

        Assert.Equal(EdgeErrorCode.ReservedColumnName, ex.Code);
        Assert.Equal("Key", ex.PropertyName);
    }

    [Fact]
    public void AnIndexKindIsAcceptedAndIgnoredButNeverSilently()
    {
        var logger = new CapturingLogger();
        var model = ModelFactory.ModelFor<HnswRecord>(logger: logger);

        Assert.Equal(IndexKind.Hnsw, model.VectorProperty.IndexKind);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(804, entry.EventId.Id);
        Assert.Contains("brute-force and flat", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACollectionWithNoIndexKindLogsNothing()
    {
        var logger = new CapturingLogger();
        _ = ModelFactory.ModelFor<Note>(logger: logger);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void TheModelExceptionCarriesTheCollectionNameAndTheDocsHelpLink()
    {
        var ex = Assert.Throws<EdgeVectorModelException>(
            () => ModelFactory.ModelFor<NullableVectorRecord>("widgets"));

        Assert.Equal("widgets", ex.CollectionName);
        Assert.Equal(
            "https://github.com/qavren-oss/qavren-edge/blob/main/foundation/docs/errors.md#5209",
            ex.HelpLink);
    }

    [Fact]
    public void TheStoreExceptionBreaksTheHierarchyOnPurposeAndStillCarriesTheCode()
    {
        var ex = new EdgeVectorStoreException(EdgeErrorCode.VectorStoreOperationFailed, "boom");

        Assert.IsAssignableFrom<VectorStoreException>(ex);
        Assert.IsNotAssignableFrom<EdgeException>(ex);
        Assert.Equal(EdgeErrorCode.VectorStoreOperationFailed, ex.Code);
        Assert.Equal("sqlite", ex.VectorStoreSystemName);
        Assert.Equal(
            "https://github.com/qavren-oss/qavren-edge/blob/main/foundation/docs/errors.md#5212",
            ex.HelpLink);
    }

    /// <summary>Mirrors <c>VecMetric</c> so the theory data stays a compile-time constant.</summary>
    public enum VecMetricName
    {
        /// <summary>vec0 <c>l2</c>.</summary>
        L2 = 0,

        /// <summary>vec0 <c>l1</c>.</summary>
        L1 = 1,

        /// <summary>vec0 <c>cosine</c>.</summary>
        Cosine = 2,
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message);

    private sealed class CapturingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception)));
        }
    }

    private sealed class SByteEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<sbyte>>
    {
        public Task<GeneratedEmbeddings<Embedding<sbyte>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(values);
            return Task.FromResult(new GeneratedEmbeddings<Embedding<sbyte>>(
                values.Select(_ => new Embedding<sbyte>(new sbyte[4]))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
        }
    }
}
