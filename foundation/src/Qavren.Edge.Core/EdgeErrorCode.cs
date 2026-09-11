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

    // ---- Sub-project 4: 7000-7299. 6000-6299 is reserved for sub-project 3 and stays unallocated. ----

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
