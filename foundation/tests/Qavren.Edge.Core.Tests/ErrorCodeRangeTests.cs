using Xunit;

namespace Qavren.Edge.Core.Tests;

public class ErrorCodeRangeTests
{
    [Fact]
    public void Sp1CodesAreUnchanged()
    {
        Assert.Equal(1001, (int)EdgeErrorCode.DuplicateDatabaseName);
        Assert.Equal(1002, (int)EdgeErrorCode.NoNativeProviderRegistered);
        Assert.Equal(1003, (int)EdgeErrorCode.MultipleNativeProvidersRegistered);
        Assert.Equal(1004, (int)EdgeErrorCode.EncryptionKeyWithoutCipherProvider);
        Assert.Equal(1005, (int)EdgeErrorCode.EncryptionKeyMissing);
        Assert.Equal(2001, (int)EdgeErrorCode.NativeLoadFailed);
        Assert.Equal(2002, (int)EdgeErrorCode.NativeVerificationFailed);
        Assert.Equal(3001, (int)EdgeErrorCode.MigrationFailed);
        Assert.Equal(3002, (int)EdgeErrorCode.MigrationVersionConflict);
        Assert.Equal(4001, (int)EdgeErrorCode.DatabaseKeyRejected);
    }

    [Theory]
    // Qavren.Edge.Onnx - runtime and sessions
    [InlineData(EdgeErrorCode.OnnxEnvironmentAlreadyCreated, 5001)]
    [InlineData(EdgeErrorCode.OnnxSessionCreationFailed, 5002)]
    [InlineData(EdgeErrorCode.OnnxModelSignatureMismatch, 5003)]
    [InlineData(EdgeErrorCode.OnnxExecutionProviderRequired, 5004)]
    [InlineData(EdgeErrorCode.OnnxInsufficientMemory, 5005)]
    [InlineData(EdgeErrorCode.OnnxUnsupportedRuntime, 5006)]
    [InlineData(EdgeErrorCode.OnnxStaticShapesUnpinned, 5007)]
    // Qavren.Edge.Onnx - model provisioning
    [InlineData(EdgeErrorCode.ModelNotRegistered, 5051)]
    [InlineData(EdgeErrorCode.ModelNotProvisioned, 5052)]
    [InlineData(EdgeErrorCode.ModelDownloadFailed, 5053)]
    [InlineData(EdgeErrorCode.ModelHashMismatch, 5054)]
    [InlineData(EdgeErrorCode.ModelAssetMissing, 5055)]
    [InlineData(EdgeErrorCode.ModelInsufficientDiskSpace, 5056)]
    // Qavren.Edge.Embeddings.Onnx
    [InlineData(EdgeErrorCode.TokenizerAssetMissing, 5101)]
    [InlineData(EdgeErrorCode.TokenizerKindUnsupported, 5102)]
    [InlineData(EdgeErrorCode.EmbeddingDimensionMismatch, 5103)]
    [InlineData(EdgeErrorCode.EmbeddingPresetNotFound, 5104)]
    [InlineData(EdgeErrorCode.EmbeddingInputTooLong, 5105)]
    // Qavren.Edge.VectorData
    [InlineData(EdgeErrorCode.VectorCollectionNotFound, 5201)]
    [InlineData(EdgeErrorCode.UnsupportedKeyType, 5202)]
    [InlineData(EdgeErrorCode.UnsupportedPropertyType, 5203)]
    [InlineData(EdgeErrorCode.UnsupportedDistanceFunction, 5204)]
    [InlineData(EdgeErrorCode.VectorDimensionMismatch, 5205)]
    [InlineData(EdgeErrorCode.FullTextPropertyMissing, 5206)]
    [InlineData(EdgeErrorCode.EmbeddingGeneratorMissing, 5207)]
    [InlineData(EdgeErrorCode.MultipleVectorPropertiesUnsupported, 5208)]
    [InlineData(EdgeErrorCode.NullableVectorProperty, 5209)]
    [InlineData(EdgeErrorCode.KnnLimitExceeded, 5210)]
    [InlineData(EdgeErrorCode.SqliteVersionTooOld, 5211)]
    [InlineData(EdgeErrorCode.VectorStoreOperationFailed, 5212)]
    [InlineData(EdgeErrorCode.ReservedColumnName, 5213)]
    public void Sp2CodeHasItsSpecifiedValue(EdgeErrorCode code, int expected)
        => Assert.Equal(expected, (int)code);

    [Fact]
    public void EveryValueIsDistinct()
    {
        var values = Enum.GetValues<EdgeErrorCode>().Select(v => (int)v).ToArray();
        Assert.Equal(values.Length, values.Distinct().Count());
    }
}
