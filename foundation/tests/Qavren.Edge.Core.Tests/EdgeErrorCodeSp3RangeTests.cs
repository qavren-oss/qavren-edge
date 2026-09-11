using Xunit;

namespace Qavren.Edge.Core.Tests;

/// <summary>
/// Guards sub-project 3's additive 6000-6299 block: the range is closed, the set is exactly the
/// thirty-six codes spec 13.1 declares, and nothing below 6000 was renumbered to make room.
/// </summary>
public class EdgeErrorCodeSp3RangeTests
{
    /// <summary>Every code spec 13.1 declares, in declaration order.</summary>
    private static readonly int[] Sp3Codes =
    [
        // 6000-6049 configuration and runner
        6001, 6002, 6003, 6004, 6005, 6006, 6007, 6008, 6009, 6010, 6011,
        // 6050-6099 source
        6051, 6052, 6053, 6054, 6055,
        // 6100-6149 extraction
        6101, 6102, 6103, 6104, 6105, 6106, 6107,
        // 6150-6199 chunking
        6151, 6152, 6153, 6154, 6155,
        // 6200-6249 state and writes
        6201, 6202, 6203, 6204, 6205, 6206, 6207, 6208,
    ];

    private static int[] DeclaredValues()
        => Enum.GetValues<EdgeErrorCode>().Select(v => (int)v).ToArray();

    [Fact]
    public void EveryDeclaredValueIsUnique()
    {
        var values = DeclaredValues();
        Assert.Equal(values.Length, values.Distinct().Count());
    }

    [Fact]
    public void EverySp3ValueIsInsideTheReservedRange()
    {
        foreach (var value in DeclaredValues().Where(v => v is >= 6000 and < 7000))
        {
            Assert.InRange(value, 6000, 6299);
        }
    }

    [Theory]
    // SP1 - 1001-4001.
    [InlineData(EdgeErrorCode.DuplicateDatabaseName, 1001)]
    [InlineData(EdgeErrorCode.NoNativeProviderRegistered, 1002)]
    [InlineData(EdgeErrorCode.MultipleNativeProvidersRegistered, 1003)]
    [InlineData(EdgeErrorCode.EncryptionKeyWithoutCipherProvider, 1004)]
    [InlineData(EdgeErrorCode.EncryptionKeyMissing, 1005)]
    [InlineData(EdgeErrorCode.NativeLoadFailed, 2001)]
    [InlineData(EdgeErrorCode.NativeVerificationFailed, 2002)]
    [InlineData(EdgeErrorCode.MigrationFailed, 3001)]
    [InlineData(EdgeErrorCode.MigrationVersionConflict, 3002)]
    [InlineData(EdgeErrorCode.DatabaseKeyRejected, 4001)]
    // SP2 - the two ends of 5001-5213.
    [InlineData(EdgeErrorCode.OnnxEnvironmentAlreadyCreated, 5001)]
    [InlineData(EdgeErrorCode.ReservedColumnName, 5213)]
    public void PreSp3CodeKeepsItsNumber(EdgeErrorCode code, int expected)
        => Assert.Equal(expected, (int)code);

    [Theory]
    // 6000-6049 configuration and runner
    [InlineData(EdgeErrorCode.TokenCounterMissing, 6001)]
    [InlineData(EdgeErrorCode.IngestionCollectionNotConfigured, 6002)]
    [InlineData(EdgeErrorCode.IngestionChunkBudgetInvalid, 6003)]
    [InlineData(EdgeErrorCode.IngestionDuplicateExtractorId, 6004)]
    [InlineData(EdgeErrorCode.IngestionOptionsInvalid, 6005)]
    [InlineData(EdgeErrorCode.IngestionCollectionDimensionMismatch, 6006)]
    [InlineData(EdgeErrorCode.IngestionMigrationVersionConflict, 6007)]
    [InlineData(EdgeErrorCode.IngestionRunAlreadyActive, 6008)]
    [InlineData(EdgeErrorCode.IngestionRecipeChanged, 6009)]
    [InlineData(EdgeErrorCode.IngestionRunAborted, 6010)]
    [InlineData(EdgeErrorCode.IngestionCollectionSchemaMismatch, 6011)]
    // 6050-6099 source
    [InlineData(EdgeErrorCode.IngestionSourceUnavailable, 6051)]
    [InlineData(EdgeErrorCode.IngestionDocumentTooLarge, 6052)]
    [InlineData(EdgeErrorCode.IngestionDocumentUnreadable, 6053)]
    [InlineData(EdgeErrorCode.IngestionDuplicateDocumentId, 6054)]
    [InlineData(EdgeErrorCode.IngestionSourceIdInvalid, 6055)]
    // 6100-6149 extraction
    [InlineData(EdgeErrorCode.ExtractorNotFound, 6101)]
    [InlineData(EdgeErrorCode.ExtractionFailed, 6102)]
    [InlineData(EdgeErrorCode.DocumentEncrypted, 6103)]
    [InlineData(EdgeErrorCode.DocumentMalformed, 6104)]
    [InlineData(EdgeErrorCode.DocumentHasNoTextLayer, 6105)]
    [InlineData(EdgeErrorCode.DocumentEncodingUndecodable, 6106)]
    [InlineData(EdgeErrorCode.DocumentPageBudgetExceeded, 6107)]
    // 6150-6199 chunking
    [InlineData(EdgeErrorCode.ChunkExceedsTokenBudget, 6151)]
    [InlineData(EdgeErrorCode.ChunkContextTooLong, 6152)]
    [InlineData(EdgeErrorCode.ChunkTokenizerCeilingExceeded, 6153)]
    [InlineData(EdgeErrorCode.ChunkerProducedEmptyChunk, 6154)]
    [InlineData(EdgeErrorCode.MarkdownParseFailed, 6155)]
    // 6200-6249 state and writes
    [InlineData(EdgeErrorCode.IngestionStateMissing, 6201)]
    [InlineData(EdgeErrorCode.IngestionHashAlgorithmMismatch, 6202)]
    [InlineData(EdgeErrorCode.IngestionStateSchemaUnsupported, 6203)]
    [InlineData(EdgeErrorCode.IngestionStateCorrupt, 6204)]
    [InlineData(EdgeErrorCode.IngestionCheckpointWriteFailed, 6205)]
    [InlineData(EdgeErrorCode.IngestionEmbeddingFailed, 6206)]
    [InlineData(EdgeErrorCode.IngestionWriteFailed, 6207)]
    [InlineData(EdgeErrorCode.IngestionEmbeddingGeneratorMissing, 6208)]
    public void Sp3CodeHasItsSpecifiedValue(EdgeErrorCode code, int expected)
        => Assert.Equal(expected, (int)code);

    [Fact]
    public void Sp3SetIsExactlyTheThirtySixSpecifiedCodes()
    {
        Assert.Equal(36, Sp3Codes.Length);

        var declared = DeclaredValues().Where(v => v is >= 6000 and <= 6299).Order().ToArray();
        Assert.Equal(Sp3Codes.Order().ToArray(), declared);
    }
}
