namespace Qavren.Edge;

/// <summary>Stable, programmatically handleable error identity for every <see cref="EdgeException"/>.</summary>
public enum EdgeErrorCode
{
    /// <summary>No error code was set. The default enum value; never assigned by a thrown <see cref="EdgeException"/>.</summary>
    Unknown = 0,

    /// <summary>
    /// Two databases were registered under the same name — either two <c>AddSqlite</c> calls collided,
    /// or <c>AddMigration</c> for a name was called before its matching <c>AddSqlite</c>.
    /// </summary>
    /// <remarks>Give each database a distinct name, and call <c>AddSqlite</c> before adding migrations against it.</remarks>
    DuplicateDatabaseName = 1001,

    /// <summary>No SQLite native provider was registered before the <c>Edge</c> builder tried to open a database.</summary>
    /// <remarks>
    /// Reference <c>Qavren.Edge.Sqlite.Native</c> and call <c>UseSqliteNative()</c>, or reference
    /// <c>Qavren.Edge.Sqlite.Native.Cipher</c> and call <c>UseSqliteNativeCipher()</c>.
    /// </remarks>
    NoNativeProviderRegistered = 1002,

    /// <summary>More than one SQLite native provider is registered at once, so the builder cannot tell which one should back the database.</summary>
    /// <remarks>Reference exactly one of <c>Qavren.Edge.Sqlite.Native</c> or <c>Qavren.Edge.Sqlite.Native.Cipher</c> — never both in the same app.</remarks>
    MultipleNativeProvidersRegistered = 1003,

    /// <summary>
    /// A database has an encryption key configured but the registered native provider has no cipher codec,
    /// or a rekey was attempted on a database that is not encrypted.
    /// </summary>
    /// <remarks>Reference <c>Qavren.Edge.Sqlite.Native.Cipher</c> and call <c>UseSqliteNativeCipher()</c> instead of <c>UseSqliteNative()</c> whenever a key is configured.</remarks>
    EncryptionKeyWithoutCipherProvider = 1004,

    /// <summary>A cipher-capable native provider is registered for a database whose configuration requires an encryption key, but none was supplied.</summary>
    /// <remarks>Set the key on the database's options (<c>AddSqlite(o =&gt; o.Key = ...)</c>) before it is opened.</remarks>
    EncryptionKeyMissing = 1005,

    /// <summary>The native SQLite library (<c>qedge_sqlite3</c> / <c>qedge_sqlcipher</c>) could not be loaded for the current runtime identifier.</summary>
    /// <remarks>
    /// Raised as <see cref="EdgeNativeException"/>; inspect its <c>ProbedPaths</c> and <c>RuntimeIdentifier</c>
    /// to confirm the native package's output for that RID was actually staged beside the app binary.
    /// </remarks>
    NativeLoadFailed = 2001,

    /// <summary>The native library loaded, but a startup verification check against it failed — the binary is present but is not the one Qavren.Edge expects.</summary>
    /// <remarks>Confirm the native binary was not swapped, truncated, or built from a mismatched <c>native/versions.json</c> pin; reinstall or rebuild the native package for the target RID.</remarks>
    NativeVerificationFailed = 2002,

    /// <summary>A migration threw while running. The database is left at the last successfully applied <c>user_version</c>; the failed migration was not partially committed.</summary>
    /// <remarks>Fix the migration named in the exception message and rerun; the database remains usable at its prior version in the meantime.</remarks>
    MigrationFailed = 3001,

    /// <summary>The same migration version number was registered twice for one database.</summary>
    /// <remarks>Give each <c>AddMigration</c> call for a database a unique, ascending version number.</remarks>
    MigrationVersionConflict = 3002,

    /// <summary>A SQLCipher database refused the key it was opened with.</summary>
    /// <remarks>Confirm the key is correct, or that the file being opened is actually a SQLCipher database and not a plain SQLite file.</remarks>
    DatabaseKeyRejected = 4001,

    // ---- Sub-project 2: 5000-5299. SP1's 1001-4001 above are untouched. ----

    // Qavren.Edge.Onnx - runtime and sessions
    /// <summary>Another library created the ORT environment first. Logged, never thrown.</summary>
    OnnxEnvironmentAlreadyCreated = 5001,

    /// <summary>Every execution provider, including the CPU fallback, failed to append, or the native ONNX Runtime library itself is missing.</summary>
    /// <remarks>
    /// Carries the RID, the probed path and every execution-provider attempt in <see cref="EdgeNativeException"/>'s shape;
    /// confirm the ONNX Runtime native for the current RID is present and loadable.
    /// </remarks>
    OnnxSessionCreationFailed = 5002,

    /// <summary>The loaded graph declares an input the caller cannot supply.</summary>
    /// <remarks>The exception lists both the graph's declared input names and the names Qavren.Edge can produce; check for a mismatched model file or preset id.</remarks>
    OnnxModelSignatureMismatch = 5003,

    /// <summary>An execution provider listed in the session policy's <c>Required</c> set failed to append, and the policy does not allow falling through to CPU for that provider.</summary>
    /// <remarks>Either remove the provider from <c>Required</c>, or fix why it cannot append (a missing native asset, or an OS version below the provider's floor).</remarks>
    OnnxExecutionProviderRequired = 5004,

    /// <summary>The device's available memory is below the configured budget for the preset being loaded, checked before <c>new InferenceSession</c> runs.</summary>
    /// <remarks>Carries both the required and available byte counts; use a smaller preset (an int8 variant), free memory before loading, or raise the configured memory budget.</remarks>
    OnnxInsufficientMemory = 5005,

    /// <summary>osx-x64 and any RID with no ORT native.</summary>
    OnnxUnsupportedRuntime = 5006,

    /// <summary>RequireStaticInputShapes set with no FreeDimensionOverrides.</summary>
    OnnxStaticShapesUnpinned = 5007,

    // Qavren.Edge.Onnx - model provisioning
    /// <summary>The requested model id was never declared by any registered model source.</summary>
    /// <remarks>Register the model before requesting it; check for a typo'd model or preset id.</remarks>
    ModelNotRegistered = 5051,

    /// <summary>The model is registered, but its files have not yet been downloaded or staged to local storage.</summary>
    /// <remarks>Provision the model explicitly, or let first-use auto-provisioning run, before using it.</remarks>
    ModelNotProvisioned = 5052,

    /// <summary>Downloading the model's bytes failed — network failure, a non-2xx/206 response, or a server that ignored a <c>Range</c> request and forced a restart that then also failed.</summary>
    /// <remarks>Check connectivity to the model source and retry; a server that consistently ignores <c>Range</c> restarts the download from scratch every time by design.</remarks>
    ModelDownloadFailed = 5053,

    /// <summary>The SHA-256 of a downloaded or staged file does not match the manifest, even after the one automatic retry.</summary>
    /// <remarks>Delete the cached file and re-download; confirm the manifest's recorded hash still matches the current revision of the source.</remarks>
    ModelHashMismatch = 5054,

    /// <summary>A file the model's manifest requires (for example <c>vocab.txt</c>) is not present on disk at load time.</summary>
    /// <remarks>Re-provision the model bundle; confirm the model root directory was not partially deleted or moved.</remarks>
    ModelAssetMissing = 5055,

    /// <summary>Free disk space is short of the remaining bytes to download plus a safety margin, checked before the download starts.</summary>
    /// <remarks>Free disk space, or choose a smaller preset (an int8 variant).</remarks>
    ModelInsufficientDiskSpace = 5056,

    // Qavren.Edge.Embeddings.Onnx
    /// <summary>A file the tokenizer needs (for example <c>vocab.txt</c>) is missing for the preset being loaded.</summary>
    /// <remarks>Re-provision the model/tokenizer bundle; verify the configured model root path.</remarks>
    TokenizerAssetMissing = 5101,

    /// <summary>The requested tokenizer kind is not implemented yet — today this is any multilingual preset that needs a Unigram/SentencePiece tokenizer.</summary>
    /// <remarks>Use one of the shipped WordPiece/BERT-tokenized presets until multilingual support lands (see ADR 0006).</remarks>
    TokenizerKindUnsupported = 5102,

    /// <summary>The embedding generator's actual output dimension does not match the dimension declared elsewhere — a preset's stated dimension, or a <c>DefaultModelDimensions</c> override.</summary>
    /// <remarks>Confirm the configured preset and any dimension override agree with the preset's real output size.</remarks>
    EmbeddingDimensionMismatch = 5103,

    /// <summary>The requested preset id does not match any of the shipped <c>EmbeddingPresets</c>.</summary>
    /// <remarks>Check the spelling of the preset constant; only the four shipped presets exist in v1.</remarks>
    EmbeddingPresetNotFound = 5104,

    /// <summary>The input text, once tokenized, exceeds the sequence length the generator is configured to accept.</summary>
    /// <remarks>Shorten the input or chunk it into smaller pieces before embedding.</remarks>
    EmbeddingInputTooLong = 5105,

    // Qavren.Edge.VectorData
    /// <summary>The requested collection name has no matching table.</summary>
    /// <remarks>Call <c>EnsureCollectionExistsAsync</c> first, or check the collection name for a typo.</remarks>
    VectorCollectionNotFound = 5201,

    /// <summary>The <c>[VectorStoreKey]</c> property's CLR type is not one the provider supports.</summary>
    /// <remarks>Use one of the four supported key types — <c>int</c>, <c>long</c>, <c>string</c> or <c>Guid</c>.</remarks>
    UnsupportedKeyType = 5202,

    /// <summary>A data or vector property's CLR type has no SQL mapping in this provider.</summary>
    /// <remarks>Change the property's type to a supported one, or correct its <c>[VectorStoreData]</c>/<c>[VectorStoreVector]</c> attribute.</remarks>
    UnsupportedPropertyType = 5203,

    /// <summary>The <c>DistanceFunction</c> set on <c>[VectorStoreVector]</c> is not one <c>vec0</c> supports.</summary>
    /// <remarks>Use one of the three supported values — <c>CosineDistance</c> (the default), <c>EuclideanDistance</c> or <c>ManhattanDistance</c>.</remarks>
    UnsupportedDistanceFunction = 5204,

    /// <summary>The embedding generator's dimension does not match the vector property's declared dimension.</summary>
    /// <remarks>Make <c>[VectorStoreVector(dims)]</c> match the configured embedding generator's actual output dimension.</remarks>
    VectorDimensionMismatch = 5205,

    /// <summary><c>HybridSearchAsync</c> was called on a record type with no <c>[VectorStoreData(IsFullTextIndexed = true)]</c> property.</summary>
    /// <remarks>Mark at least one text property <c>IsFullTextIndexed = true</c>, or call <c>VectorSearchAsync</c> instead of hybrid search.</remarks>
    FullTextPropertyMissing = 5206,

    /// <summary>A <c>string</c>-source vector property needs an <c>IEmbeddingGenerator</c>, and DI resolution (including the non-generic fallback) found none.</summary>
    /// <remarks>Call <c>AddOnnxEmbeddings</c> (or otherwise register a matching <c>IEmbeddingGenerator</c>) before <c>AddVectorStore</c>.</remarks>
    EmbeddingGeneratorMissing = 5207,

    /// <summary>The record type declares more than one <c>[VectorStoreVector]</c> property; v1 supports exactly one.</summary>
    /// <remarks>Keep exactly one vector property per record type.</remarks>
    MultipleVectorPropertiesUnsupported = 5208,

    /// <summary>A vector property was declared nullable — rejected at model build time rather than left as a runtime null-means-skip trap on upsert.</summary>
    /// <remarks>Make the vector property's type non-nullable.</remarks>
    NullableVectorProperty = 5209,

    /// <summary><c>top + Skip</c> exceeds <c>SQLITE_VEC_VEC0_K_MAX</c>, <c>vec0</c>'s own cap on <c>k</c> for a single KNN query. Raised before the query reaches SQLite.</summary>
    /// <remarks>Reduce <c>top</c> and/or <c>Skip</c> so their sum stays within <c>vec0</c>'s <c>k</c> limit (4096).</remarks>
    KnnLimitExceeded = 5210,

    /// <summary>The SQLite build in use predates the version the <c>vec0</c>/FTS5 features this provider depends on require.</summary>
    /// <remarks>Use the bundled <c>qedge_sqlite3</c> native (<c>Qavren.Edge.Sqlite.Native</c>) rather than an external or OS-provided SQLite.</remarks>
    SqliteVersionTooOld = 5211,

    /// <summary>A <c>SqliteException</c> surfaced during a store operation and was wrapped so callers get one exception type across every operation.</summary>
    /// <remarks>Inspect the wrapped <c>SqliteException</c> (<c>InnerException</c>) for the underlying SQL error.</remarks>
    VectorStoreOperationFailed = 5212,

    /// <summary>A property claims the reserved "_rowid" storage name.</summary>
    ReservedColumnName = 5213,

    // ---- Sub-project 3: 6000-6299. SP1's 1001-4001 and SP2's 5001-5213 are untouched. ----

    // 6000-6049 configuration and runner
    /// <summary>No <c>IChunkTokenizer</c> is resolvable at startup — there is deliberately no chars/4 fallback.</summary>
    /// <remarks>Call <c>AddOnnxIngestion()</c> to use the tokenizer, or register one with <c>UseChunkTokenizer(...)</c> / <c>EdgeTokenCounter.CreateWordPiece(...)</c>.</remarks>
    TokenCounterMissing = 6001,

    /// <summary><c>RunAsync</c>, <c>PruneAsync</c>, <c>GetStatusAsync</c>, <c>RemoveSourceAsync</c> or <c>RemoveDocumentAsync</c> named a <c>collectionName</c> that no <c>AddIngestion</c> call configured.</summary>
    /// <remarks>Call <c>AddIngestion</c> for that collection, or pass <see langword="null"/> to use the single configured one.</remarks>
    IngestionCollectionNotConfigured = 6002,

    /// <summary>The resolved chunk budget arithmetic is invalid, checked at startup once the resolved <c>IChunkTokenizer</c> is known.</summary>
    /// <remarks>Adjust <c>ChunkOptions</c> (<c>OverlapTokens</c>, <c>MinTokens</c>, <c>HeadingPathTokenBudget</c>) or the model profile so the overhead, overlap and minimum token settings stay within the model's max sequence length.</remarks>
    IngestionChunkBudgetInvalid = 6003,

    /// <summary>Two <c>IDocumentExtractor</c>s registered in the <c>DocumentExtractorRegistry</c> composition declared the same <c>Id</c>.</summary>
    /// <remarks>Give each extractor a distinct <c>Id</c>, or remove the duplicate registration.</remarks>
    IngestionDuplicateExtractorId = 6004,

    /// <summary>A tokenizer-independent ingestion option failed validation at registration.</summary>
    /// <remarks>The exception message names the offending value(s) — adjust <c>IngestionOptions</c> accordingly before the next start.</remarks>
    IngestionOptionsInvalid = 6005,

    /// <summary>The embedding generator's <c>EmbeddingGeneratorMetadata.DefaultModelDimensions</c> disagrees with the collection's declared dimensions.</summary>
    /// <remarks>Use a generator whose dimensions match the collection, or reconfigure the collection to match the generator.</remarks>
    IngestionCollectionDimensionMismatch = 6006,

    /// <summary>A second <c>AddIngestion</c> call was made for the same collection.</summary>
    /// <remarks>Call <c>AddIngestion</c> once per collection; remove the duplicate call.</remarks>
    IngestionMigrationVersionConflict = 6007,

    /// <summary><c>IngestionPipeline.RunAsync</c> was called while a run is already active on the same pipeline.</summary>
    /// <remarks>Await the active run's <c>IngestionRunResult</c> before starting another on the same pipeline, or use a separate pipeline/collection.</remarks>
    IngestionRunAlreadyActive = 6008,

    /// <summary>The recipe hash (chunking and extraction configuration) differs from the one the state store last recorded, and <c>StrictRecipe</c> is set.</summary>
    /// <remarks>With the default <c>StrictRecipe = false</c> this is never thrown — affected documents are silently re-indexed and logged instead; with <c>StrictRecipe = true</c>, restore the prior configuration or accept the new recipe and re-run.</remarks>
    IngestionRecipeChanged = 6009,   // StrictRecipe only

    /// <summary><c>AbortAfterConsecutiveErrors</c> (20) consecutive document failures were reached in one run.</summary>
    /// <remarks>Inspect the inner exception and the per-document failures in <c>IngestionRunResult.Documents</c> — twenty in a row usually means a systemic problem, not one bad file.</remarks>
    IngestionRunAborted = 6010,

    /// <summary>The migration's collection DDL differs from the runtime store's <c>BuildCreateSql()</c> for the same collection, checked at startup.</summary>
    /// <remarks>Pass the same shaping values to <c>AddIngestion</c>'s <c>ConfigureCollection</c> that were passed to <c>AddVectorStore</c>.</remarks>
    IngestionCollectionSchemaMismatch = 6011,   // migration DDL != the runtime store's DDL

    // 6050-6099 source
    /// <summary>The configured folder is missing or unreadable, thrown before any document is processed.</summary>
    /// <remarks>Verify the path exists and the process has read access before calling <c>RunAsync</c>.</remarks>
    IngestionSourceUnavailable = 6051,

    /// <summary>A document exceeded <c>MaxDocumentBytes</c>.</summary>
    /// <remarks>Raise <c>MaxDocumentBytes</c>, or exclude the oversized document from the source.</remarks>
    IngestionDocumentTooLarge = 6052,

    /// <summary>Opening the document raised <c>IOException</c> / <c>UnauthorizedAccessException</c>, or a non-seekable stream exceeded <c>NonSeekableBufferLimitBytes</c>.</summary>
    /// <remarks>Verify the file still exists and is readable; for a non-seekable source, raise <c>NonSeekableBufferLimitBytes</c> or supply a seekable stream.</remarks>
    IngestionDocumentUnreadable = 6053,

    /// <summary>One <c>DocumentId</c> was yielded twice within the same run.</summary>
    /// <remarks>Make <c>DocumentSourceItem.DocumentId</c> (or the source's id derivation) unique per run.</remarks>
    IngestionDuplicateDocumentId = 6054,

    /// <summary>An <c>IngestionSource</c> factory (<c>Folder</c>, <c>Items</c>) was given an invalid <c>sourceId</c>.</summary>
    /// <remarks>Supply a non-empty, stable <c>sourceId</c> — state rows are keyed on <c>(collection, source_id, document_id)</c>.</remarks>
    IngestionSourceIdInvalid = 6055,

    // 6100-6149 extraction
    /// <summary>No registered <c>IDocumentExtractor</c> claims the document's extension.</summary>
    /// <remarks>Register the matching satellite (<c>AddPdfExtractor()</c>, <c>AddDocxExtractor()</c>), or register a custom <c>IDocumentExtractor</c> for the extension.</remarks>
    ExtractorNotFound = 6101,

    /// <summary>
    /// <c>IDocumentExtractor.ExtractAsync</c> threw something not already covered by <see cref="DocumentEncrypted"/>,
    /// <see cref="DocumentMalformed"/>, <see cref="DocumentEncodingUndecodable"/> or <see cref="DocumentPageBudgetExceeded"/>.
    /// </summary>
    /// <remarks>The document is recorded <c>Failed</c> and the run continues; one bad document fails one document, never a corpus.</remarks>
    ExtractionFailed = 6102,

    /// <summary><c>PdfTextExtractor</c> could not open an encrypted PDF with the configured <c>Passwords</c>.</summary>
    /// <remarks>Add the document's password to <c>PdfExtractorOptions.Passwords</c>, or exclude the document from the source.</remarks>
    DocumentEncrypted = 6103,

    /// <summary><c>PdfTextExtractor</c> or <c>DocxTextExtractor</c> could not parse the document's structure.</summary>
    /// <remarks>Re-export or repair the source document; PdfPig's <c>UseLenientParsing</c> (on by default) already tolerates the common cases.</remarks>
    DocumentMalformed = 6104,

    /// <summary>Every page of a PDF has no text layer — a scanned document.</summary>
    /// <remarks>Run OCR upstream and re-ingest the result, or accept that the document is indexed as metadata only.</remarks>
    DocumentHasNoTextLayer = 6105,

    /// <summary><c>PlainTextExtractor</c> found invalid UTF-8 with <c>StrictUtf8</c> set.</summary>
    /// <remarks>Leave <c>StrictUtf8</c> off (the default) to accept the Latin-1 fallback, or re-save the source file as valid UTF-8.</remarks>
    DocumentEncodingUndecodable = 6106,

    /// <summary>A PDF's cumulative page-parse time passed <c>PageBudget</c>, checked at a page boundary.</summary>
    /// <remarks>Raise <c>PdfExtractorOptions.PageBudget</c>, or exclude the document.</remarks>
    DocumentPageBudgetExceeded = 6107,

    // 6150-6199 chunking
    /// <summary>A chunker emitted a chunk over <c>MaxTokens</c> — an SP3 invariant violation, not a user-data problem.</summary>
    /// <remarks>This should never happen on a shipped chunker; if it follows a custom chunker or tokenizer change, check its pre-yield token verification.</remarks>
    ChunkExceedsTokenBudget = 6151,

    /// <summary>A chunk's heading-path breadcrumb still exceeds <c>HeadingPathTokenBudget</c> after left-truncation.</summary>
    /// <remarks>Raise <c>HeadingPathTokenBudget</c>, shorten the document's heading structure, or set <c>Overflow</c> to the mode that honours truncation instead of throwing.</remarks>
    ChunkContextTooLong = 6152,

    /// <summary>The resolved <c>IChunkTokenizer.MaxSequenceLength</c> disagrees with <c>ChunkModelProfile.MaxSequenceLength</c>.</summary>
    /// <remarks>Use a tokenizer and a <c>ChunkModelProfile</c> for the same model — a mismatch means every number the budget resolves is a guess.</remarks>
    ChunkTokenizerCeilingExceeded = 6153,

    /// <summary>A chunker's pre-yield verification caught an empty chunk about to be emitted.</summary>
    /// <remarks>This should never happen on a shipped chunker; if it follows a custom chunker, check its emission logic.</remarks>
    ChunkerProducedEmptyChunk = 6154,

    /// <summary><c>MarkdownExtractor</c> (Markdig) failed to parse the document.</summary>
    /// <remarks>Validate the Markdown source, or extract it as plain text instead.</remarks>
    MarkdownParseFailed = 6155,

    // 6200-6249 state and writes
    /// <summary><c>IngestionStateStore</c> opened a database whose state tables do not exist.</summary>
    /// <remarks>Call <c>AddIngestion(migrationVersion)</c> so the migration creates the state tables before the first run.</remarks>
    IngestionStateMissing = 6201,

    /// <summary>The state store's meta row records a hash algorithm different from the one the running code uses.</summary>
    /// <remarks>Delete the state rows and re-run to rebuild them under the current algorithm; this suite has no down-migrations.</remarks>
    IngestionHashAlgorithmMismatch = 6202,

    /// <summary>The state store's meta row records a schema version the running code does not understand (forward-only; no down-migrations).</summary>
    /// <remarks>Upgrade to the package version that wrote the state, or delete the state rows and re-run.</remarks>
    IngestionStateSchemaUnsupported = 6203,

    /// <summary>A stored content hash is not the expected 16 bytes.</summary>
    /// <remarks>Delete the affected state row(s) and re-run so they are rebuilt cleanly.</remarks>
    IngestionStateCorrupt = 6204,

    /// <summary><c>ChunkWriter</c>'s state-write step failed — disk full, or <c>SQLITE_BUSY</c> past <c>BusyTimeout</c>.</summary>
    /// <remarks>
    /// The transaction rolls back and the run aborts, so an unwritten checkpoint cannot make the next run believe
    /// finished work is still owed; free disk space, raise <c>BusyTimeout</c>, or reduce write concurrency, then re-run.
    /// </remarks>
    IngestionCheckpointWriteFailed = 6205,

    /// <summary>SP3's own embed step (<c>GenerateAsync</c>) threw.</summary>
    /// <remarks>
    /// The runner halves the batch, retries the embed once, then suspends the run; inspect the inner exception, and for
    /// <see cref="OnnxInsufficientMemory"/> lower <c>WriteBatchSize</c> or the effective batch size.
    /// </remarks>
    IngestionEmbeddingFailed = 6206,   // SP3's own GenerateAsync (spec 9.5 a1)

    /// <summary>SP2's <c>UpsertAsync</c> or <c>DeleteAsync</c> threw.</summary>
    /// <remarks>Reported, not retried; inspect the inner <c>EdgeVectorStoreException</c> for the underlying store failure.</remarks>
    IngestionWriteFailed = 6207,   // SP2's UpsertAsync / DeleteAsync (spec 9.5 a2, b)

    /// <summary>No <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> is resolvable, unkeyed or keyed on <c>StoreName</c>, at startup.</summary>
    /// <remarks>Call <c>AddOnnxEmbeddings()</c> or register another matching generator before <c>AddIngestion</c>.</remarks>
    IngestionEmbeddingGeneratorMissing = 6208,   // no IEmbeddingGenerator<string, Embedding<float>>

    // 6250-6299 reserved for sub-project 3.

    // ---- Sub-project 4: 7000-7299. SP3's 6001-6208 above are untouched. ----

    // Qavren.Edge.Chat.Onnx - runtime and model hosting
    /// <summary>A <c>Microsoft.ML.OnnxRuntimeGenAI</c> type was reached before the startup task that starts it ran.</summary>
    /// <remarks>Call <c>AddOnnxChat()</c> and resolve <c>IChatClient</c> from the container rather than constructing <c>EdgeChatClient</c> directly.</remarks>
    ChatEnvironmentNotStarted = 7001,

    /// <summary><c>new Model(...)</c> or <c>new Tokenizer(...)</c> threw an <c>OnnxRuntimeGenAIException</c>, or the load did not finish inside <c>LoadTimeout</c>.</summary>
    /// <remarks>Carries the model directory and the inner message; re-provision the bundle if the files are damaged, or raise <c>LoadTimeout</c> if the device is simply slow to load.</remarks>
    ChatModelLoadFailed = 7002,

    /// <summary>A preset lookup (<c>ChatPresets.ById</c>) was asked for an id that does not match any of the shipped presets.</summary>
    /// <remarks>Check the spelling of the preset constant; only the shipped <c>ChatPresets</c> members exist in v1.</remarks>
    ChatModelNotRegistered = 7003,

    /// <summary>No ORT GenAI native for this RID or Android ABI. armeabi-v7a has none.</summary>
    ChatUnsupportedRuntime = 7004,

    /// <summary>The memory budget refused even at <c>MinContextTokens</c>.</summary>
    /// <remarks>Carries the required, available and total byte counts and the largest context that would have fit; try a smaller preset, a lower <c>MaxContextTokens</c>, or the iOS increased-memory entitlements.</remarks>
    ChatInsufficientMemory = 7005,

    /// <summary>Total device RAM is below the preset's floor, or the device reports <c>IsLowRamDevice</c>.</summary>
    /// <remarks><c>MinTotalMemoryBytes</c> can be set to <see langword="null"/> to try anyway, at the risk of an OS kill.</remarks>
    ChatDeviceTooSmall = 7006,

    /// <summary>genai_config.json missing, unparseable or incoherent.</summary>
    ChatConfigurationInvalid = 7007,

    /// <summary>The config disagrees with the preset's declared shape.</summary>
    ChatModelShapeMismatch = 7008,

    /// <summary><c>ConfigOverlayJson</c> names an execution provider other than CPU on a mobile TFM, where GenAI has no CoreML and no NNAPI provider.</summary>
    /// <remarks>Remove the provider override on mobile targets; CPU is the only execution provider chat supports there in v1 (see ADR 0009).</remarks>
    ChatExecutionProviderUnsupported = 7009,

    // Qavren.Edge.Chat.Onnx - provisioning
    /// <summary>The model is absent on disk.</summary>
    /// <remarks>Carries the bundle's total byte count; call <c>IChatModelProvisioner.Plan()</c>, show the consent sheet it feeds, then <c>ProvisionAsync()</c>.</remarks>
    ChatModelNotProvisioned = 7051,

    /// <summary>Free disk space is short of the bundle total plus a safety margin, checked before the first byte.</summary>
    /// <remarks>Free disk space, or choose the smaller preset.</remarks>
    ChatInsufficientDiskSpace = 7052,

    /// <summary>ChatProvisioningOptions.IsTransferPermitted said no.</summary>
    ChatDownloadNotPermitted = 7053,

    // Qavren.Edge.Chat.Onnx - generation
    /// <summary>minja could not parse the model's chat template, and <c>RequireChatTemplate</c> is set.</summary>
    /// <remarks>Set <c>PromptFormatter</c> to a formatter minja can parse, or a hand-written one.</remarks>
    ChatTemplateUnsupported = 7101,

    /// <summary>The prompt is still over budget after history reduction to the floor.</summary>
    /// <remarks>Carries both numbers; adjust <c>MaxOutputTokens</c>, <c>ChatHistoryOptions</c>, or <c>RagOptions.MaxContextTokens</c>.</remarks>
    ChatPromptTooLong = 7102,

    /// <summary>
    /// <c>ChatOptions.ResponseFormat</c> is a <c>ChatResponseFormatJson</c> and the guidance policy is <c>Disabled</c>,
    /// or <c>RequireNative</c>/<c>PreferNative</c> and the positive-control probe did not come back <c>Enforced</c>.
    /// </summary>
    /// <remarks>
    /// The shipped mobile natives are built without <c>USE_GUIDANCE</c>; set <c>EdgeGuidancePolicy.PreferNative</c>
    /// to accept an unconstrained turn instead of a throw, or accept plain-text output.
    /// </remarks>
    ChatGuidanceUnavailable = 7103,

    /// <summary>The native decode loop threw mid-decode.</summary>
    /// <remarks>Partial text already streamed stays streamed; the final update reports <c>StopReason = Error</c> before the exception surfaces. Inspect the inner exception and retry the turn.</remarks>
    ChatGenerationFailed = 7104,

    /// <summary>A second concurrent turn arrived while one was already running, the gate timed out, the queue is full, or turns are not being accepted.</summary>
    /// <remarks>The GenAI C API is not thread safe, so turns serialise onto one cached <c>Generator</c>; queue the caller's own retry rather than starting a second turn concurrently.</remarks>
    ChatBusy = 7105,

    /// <summary>Device thermal state is at or above <c>AbortAt</c> before a turn starts.</summary>
    /// <remarks>The same condition reached mid-decode instead completes the stream with <c>StopReason = Thermal</c>; wait for the device to cool, or raise <c>AbortAt</c>/<c>ThrottleAt</c> if the app's own UX already warns the user.</remarks>
    ChatThermalAbort = 7106,

    /// <summary><c>ChatOptions.Tools</c> is non-empty, or <c>ToolMode</c> requires a call — unsupported on a ~1B on-device model in v1 (see ADR 0012).</summary>
    /// <remarks>Remove <c>Tools</c>/<c>ToolMode</c> from the request, or perform tool calling above this client against a server-hosted model.</remarks>
    ChatToolCallingUnsupported = 7107,

    /// <summary>A message carries non-<c>TextContent</c>, or <c>SearchOptions</c> sets <c>max_length</c> directly.</summary>
    /// <remarks><c>max_length</c> is refused whatever its type, because it is the memory cap the budget owns; use <c>ChatOptions.MaxOutputTokens</c> and the memory budget instead.</remarks>
    ChatOptionUnsupported = 7108,

    // Qavren.Edge.Rag
    /// <summary><c>UseRag()</c> was added to the pipeline but no <c>IEdgeRetriever</c> is resolvable from the container.</summary>
    /// <remarks>Register one with <c>AddVectorStoreRetriever</c>/<c>AddRetriever</c> before resolving <c>IChatClient</c>.</remarks>
    RagRetrieverMissing = 7201,

    /// <summary>Retrieval threw and <c>ContinueOnRetrievalFailure</c> is false, or <c>Top + Skip</c> exceeded <c>SQLITE_VEC_VEC0_K_MAX</c>.</summary>
    /// <remarks>Fix the retriever's collection or query, or lower <c>Top</c>/<c>Skip</c> under the vec0 candidate ceiling; leave <c>ContinueOnRetrievalFailure</c> at its default to get an ungrounded answer instead of a throw.</remarks>
    RagRetrievalFailed = 7202,

    /// <summary><c>RequireHybridSearch</c> is true and the collection does not implement <c>IKeywordHybridSearchable&lt;TRecord&gt;</c>.</summary>
    /// <remarks>Point the retriever at a collection that implements <c>IKeywordHybridSearchable&lt;TRecord&gt;</c>, or leave <c>RequireHybridSearch</c> at its default <see langword="false"/> to accept the vector-lane fallback.</remarks>
    RagCollectionNotSearchable = 7203,

    /// <summary><c>MaxContextTokens</c> cannot fit even one truncated source.</summary>
    /// <remarks>Raise <c>RagOptions.MaxContextTokens</c>, or lower <c>MaxCharsPerSource</c> so a single source fits inside the budget.</remarks>
    RagContextBudgetTooSmall = 7204,
}
