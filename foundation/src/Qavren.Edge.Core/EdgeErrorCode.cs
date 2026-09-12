namespace Qavren.Edge;

/// <summary>Stable, programmatically handleable error identity for every <see cref="EdgeException"/>.</summary>
public enum EdgeErrorCode
{
    Unknown = 0,

    DuplicateDatabaseName = 1001,
    NoNativeProviderRegistered = 1002,
    MultipleNativeProvidersRegistered = 1003,
    EncryptionKeyWithoutCipherProvider = 1004,
    EncryptionKeyMissing = 1005,

    NativeLoadFailed = 2001,
    NativeVerificationFailed = 2002,

    MigrationFailed = 3001,
    MigrationVersionConflict = 3002,

    DatabaseKeyRejected = 4001,

    // ---- Sub-project 2: 5000-5299. SP1's 1001-4001 above are untouched. ----

    // Qavren.Edge.Onnx - runtime and sessions
    /// <summary>Another library created the ORT environment first. Logged, never thrown.</summary>
    OnnxEnvironmentAlreadyCreated = 5001,
    OnnxSessionCreationFailed = 5002,
    OnnxModelSignatureMismatch = 5003,
    OnnxExecutionProviderRequired = 5004,
    OnnxInsufficientMemory = 5005,
    /// <summary>osx-x64 and any RID with no ORT native.</summary>
    OnnxUnsupportedRuntime = 5006,
    /// <summary>RequireStaticInputShapes set with no FreeDimensionOverrides.</summary>
    OnnxStaticShapesUnpinned = 5007,

    // Qavren.Edge.Onnx - model provisioning
    ModelNotRegistered = 5051,
    ModelNotProvisioned = 5052,
    ModelDownloadFailed = 5053,
    ModelHashMismatch = 5054,
    ModelAssetMissing = 5055,
    ModelInsufficientDiskSpace = 5056,

    // Qavren.Edge.Embeddings.Onnx
    TokenizerAssetMissing = 5101,
    TokenizerKindUnsupported = 5102,
    EmbeddingDimensionMismatch = 5103,
    EmbeddingPresetNotFound = 5104,
    EmbeddingInputTooLong = 5105,

    // Qavren.Edge.VectorData
    VectorCollectionNotFound = 5201,
    UnsupportedKeyType = 5202,
    UnsupportedPropertyType = 5203,
    UnsupportedDistanceFunction = 5204,
    VectorDimensionMismatch = 5205,
    FullTextPropertyMissing = 5206,
    EmbeddingGeneratorMissing = 5207,
    MultipleVectorPropertiesUnsupported = 5208,
    NullableVectorProperty = 5209,
    KnnLimitExceeded = 5210,
    SqliteVersionTooOld = 5211,
    VectorStoreOperationFailed = 5212,
    /// <summary>A property claims the reserved "_rowid" storage name.</summary>
    ReservedColumnName = 5213,

    // ---- Sub-project 3: 6000-6299. SP1's 1001-4001 and SP2's 5001-5213 are untouched. ----

    // 6000-6049 configuration and runner
    TokenCounterMissing = 6001,
    IngestionCollectionNotConfigured = 6002,
    IngestionChunkBudgetInvalid = 6003,
    IngestionDuplicateExtractorId = 6004,
    IngestionOptionsInvalid = 6005,
    IngestionCollectionDimensionMismatch = 6006,
    IngestionMigrationVersionConflict = 6007,
    IngestionRunAlreadyActive = 6008,
    IngestionRecipeChanged = 6009,   // StrictRecipe only
    IngestionRunAborted = 6010,
    IngestionCollectionSchemaMismatch = 6011,   // migration DDL != the runtime store's DDL

    // 6050-6099 source
    IngestionSourceUnavailable = 6051,
    IngestionDocumentTooLarge = 6052,
    IngestionDocumentUnreadable = 6053,
    IngestionDuplicateDocumentId = 6054,
    IngestionSourceIdInvalid = 6055,

    // 6100-6149 extraction
    ExtractorNotFound = 6101,
    ExtractionFailed = 6102,
    DocumentEncrypted = 6103,
    DocumentMalformed = 6104,
    DocumentHasNoTextLayer = 6105,
    DocumentEncodingUndecodable = 6106,
    DocumentPageBudgetExceeded = 6107,

    // 6150-6199 chunking
    ChunkExceedsTokenBudget = 6151,
    ChunkContextTooLong = 6152,
    ChunkTokenizerCeilingExceeded = 6153,
    ChunkerProducedEmptyChunk = 6154,
    MarkdownParseFailed = 6155,

    // 6200-6249 state and writes
    IngestionStateMissing = 6201,
    IngestionHashAlgorithmMismatch = 6202,
    IngestionStateSchemaUnsupported = 6203,
    IngestionStateCorrupt = 6204,
    IngestionCheckpointWriteFailed = 6205,
    IngestionEmbeddingFailed = 6206,   // SP3's own GenerateAsync (spec 9.5 a1)
    IngestionWriteFailed = 6207,   // SP2's UpsertAsync / DeleteAsync (spec 9.5 a2, b)
    IngestionEmbeddingGeneratorMissing = 6208,   // no IEmbeddingGenerator<string, Embedding<float>>

    // 6250-6299 reserved for sub-project 3.

    // ---- Sub-project 4: 7000-7299. SP3's 6001-6208 above are untouched. ----

    // Qavren.Edge.Chat.Onnx - runtime and model hosting
    ChatEnvironmentNotStarted = 7001,
    ChatModelLoadFailed = 7002,
    ChatModelNotRegistered = 7003,
    /// <summary>No ORT GenAI native for this RID or Android ABI. armeabi-v7a has none.</summary>
    ChatUnsupportedRuntime = 7004,
    ChatInsufficientMemory = 7005,
    ChatDeviceTooSmall = 7006,
    /// <summary>genai_config.json missing, unparseable or incoherent.</summary>
    ChatConfigurationInvalid = 7007,
    /// <summary>The config disagrees with the preset's declared shape.</summary>
    ChatModelShapeMismatch = 7008,
    ChatExecutionProviderUnsupported = 7009,

    // Qavren.Edge.Chat.Onnx - provisioning
    ChatModelNotProvisioned = 7051,
    ChatInsufficientDiskSpace = 7052,
    /// <summary>ChatProvisioningOptions.IsTransferPermitted said no.</summary>
    ChatDownloadNotPermitted = 7053,

    // Qavren.Edge.Chat.Onnx - generation
    ChatTemplateUnsupported = 7101,
    ChatPromptTooLong = 7102,
    ChatGuidanceUnavailable = 7103,
    ChatGenerationFailed = 7104,
    ChatBusy = 7105,
    ChatThermalAbort = 7106,
    ChatToolCallingUnsupported = 7107,
    ChatOptionUnsupported = 7108,

    // Qavren.Edge.Rag
    RagRetrieverMissing = 7201,
    RagRetrievalFailed = 7202,
    RagCollectionNotSearchable = 7203,
    RagContextBudgetTooSmall = 7204,
}
