# Qavren.Edge error codes

Every `EdgeException` (and `EdgeVectorStoreException`, which derives from
`Microsoft.Extensions.VectorData.VectorStoreException` instead but still carries
an `EdgeErrorCode`) sets `HelpLink` to
`https://github.com/qavren-oss/qavren-edge/blob/main/foundation/docs/errors.md#<code>` —
the bare numeric code, so the heading for each entry below is the number alone
and nothing else. Codes are grouped by the sub-project and range that
allocated them; the ranges themselves never overlap and are never reused.

- **1001–4999** — sub-project 1 (foundation): `Qavren.Edge.Core`, `Qavren.Edge.Sqlite`.
  Only 1001–4001 are allocated so far.
- **5000–5299** — sub-project 2 (embeddings + vector store): `Qavren.Edge.Onnx`,
  `Qavren.Edge.Embeddings.Onnx`, `Qavren.Edge.VectorData`.
- **6000–6299** — sub-project 3 (ingestion): `Qavren.Edge.Ingestion` and its
  `.Pdf`, `.OpenXml`, `.Onnx` and `.DataIngestion` satellites. Only 6001–6011,
  6051–6055, 6101–6107, 6151–6155 and 6201–6208 are allocated so far.
- **7000–7299** — sub-project 4 (chat + RAG): `Qavren.Edge.Chat.Onnx`,
  `Qavren.Edge.Rag`. Only 7001–7009, 7051–7053, 7101–7108 and
  7201–7204 are allocated so far.

## Sub-project 1 — `Qavren.Edge.Core` / `Qavren.Edge.Sqlite` (1001–4001)

## 1001

**DuplicateDatabaseName**

Meaning: Two databases were registered under the same name — either two
`AddSqlite(name, ...)` calls collided, or `AddMigration` for a name was called
before the matching `AddSqlite` for that same name.

Remediation: Give each database a distinct name (`AddSqlite("corpus", ...)`),
and call `AddSqlite` for a name before adding migrations against it.

## 1002

**NoNativeProviderRegistered**

Meaning: No SQLite native provider was registered before the `Edge` builder
tried to open a database.

Remediation: Reference `Qavren.Edge.Sqlite.Native` and call `UseSqliteNative()`,
or reference `Qavren.Edge.Sqlite.Native.Cipher` and call
`UseSqliteNativeCipher()`.

## 1003

**MultipleNativeProvidersRegistered**

Meaning: More than one SQLite native provider is registered at once (for
example both `Qavren.Edge.Sqlite.Native` and `Qavren.Edge.Sqlite.Native.Cipher`),
so the builder cannot tell which one should back the database.

Remediation: Reference exactly one of `Qavren.Edge.Sqlite.Native` or
`Qavren.Edge.Sqlite.Native.Cipher` — never both in the same app.

## 1004

**EncryptionKeyWithoutCipherProvider**

Meaning: A database has an encryption key configured, but the registered
native provider has no cipher codec — or the reverse: a rekey was attempted
on a database that isn't encrypted.

Remediation: Reference `Qavren.Edge.Sqlite.Native.Cipher` and call
`UseSqliteNativeCipher()` instead of `UseSqliteNative()` whenever a key is
configured; only rekey a database that is already encrypted.

## 1005

**EncryptionKeyMissing**

Meaning: A cipher-capable native provider is registered for a database whose
configuration requires an encryption key, but none was supplied.

Remediation: Set the key on that database's options (`AddSqlite(o => o.Key =
...)`) before it is opened.

## 2001

**NativeLoadFailed**

Meaning: The native SQLite library (`qedge_sqlite3` / `qedge_sqlcipher`) could
not be loaded for the current runtime identifier. Raised as
`EdgeNativeException`, which carries the RID, the library name and every path
probed.

Remediation: Inspect `EdgeNativeException.ProbedPaths` and
`RuntimeIdentifier`; confirm the native package's output for that RID was
actually staged beside the app binary.

## 2002

**NativeVerificationFailed**

Meaning: The native library loaded, but a startup verification check against
it failed — the binary is present but is not the one Qavren.Edge expects.

Remediation: Confirm the native binary was not swapped, truncated, or built
from a mismatched `native/versions.json` pin; reinstall or rebuild the native
package for the target RID.

## 3001

**MigrationFailed**

Meaning: A migration threw while running. The database is left at the last
successfully applied `user_version` — the failed migration was not partially
committed.

Remediation: Fix the migration named in the exception message and rerun; the
database remains usable at its prior version in the meantime.

## 3002

**MigrationVersionConflict**

Meaning: The same migration version number was registered twice for one
database.

Remediation: Give each `AddMigration` call for a database a unique, ascending
version number.

## 4001

**DatabaseKeyRejected**

Meaning: A SQLCipher database refused the key it was opened with.

Remediation: Confirm the key is correct, or that the file being opened is
actually a SQLCipher database and not a plain SQLite file.

## Sub-project 2 — `Qavren.Edge.Onnx` (5001–5007): runtime and sessions

## 5001

**OnnxEnvironmentAlreadyCreated**

Meaning: Informational only — never thrown. Another library in the process
already created the shared `OrtEnv` before Qavren.Edge did, so Qavren.Edge
reuses the existing environment instead of creating its own.

Remediation: None required. If you need Qavren.Edge's own environment
options (logging, thread pools) to take effect, initialize Qavren.Edge before
any other ONNX Runtime consumer in the process.

## 5002

**OnnxSessionCreationFailed**

Meaning: Every execution provider — including the CPU fallback — failed to
append, or the native ONNX Runtime library itself is missing. Carries the
RID, the probed path and every execution-provider attempt, in
`EdgeNativeException`'s shape.

Remediation: Check `Attempts` on the exception for the specific provider
failure; confirm the ONNX Runtime native for the current RID is present and
loadable.

## 5003

**OnnxModelSignatureMismatch**

Meaning: The loaded graph declares an input the caller cannot supply — the
exception lists both the graph's declared input names and the names
Qavren.Edge can produce.

Remediation: Confirm the preset's declared tokenizer output
(`input_ids`/`attention_mask`/`token_type_ids`) matches what the graph
actually expects; check for a mismatched model file or preset id.

## 5004

**OnnxExecutionProviderRequired**

Meaning: An execution provider listed in the session policy's `Required` set
failed to append, and the policy does not allow falling through to CPU for
that provider.

Remediation: Either remove the provider from `Required`, or fix why it
cannot append (missing native asset, OS version below the provider's floor).

## 5005

**OnnxInsufficientMemory**

Meaning: The device's available memory is below the configured budget for the
preset being loaded — checked before `new InferenceSession` runs. Carries
both the required and available byte counts.

Remediation: Use a smaller preset (an int8 variant), free memory before
loading, or raise the configured memory budget.

## 5006

**OnnxUnsupportedRuntime**

Meaning: The current runtime identifier has no ONNX Runtime native at all —
today this is exactly `osx-x64`, which ORT 1.30.0 does not ship.

Remediation: Run on a supported RID (`win-x64`, `win-arm64`, `linux-x64`,
`linux-arm64`, `osx-arm64`, `android`, `ios`, `maccatalyst`). There is no
workaround for Intel macOS.

## 5007

**OnnxStaticShapesUnpinned**

Meaning: `RequireStaticInputShapes` was turned on with no
`FreeDimensionOverrides` set. That combination would silently move the whole
graph to CPU (CoreML partitions on symbolic dims at session-creation time),
and nothing would report it — so this is raised before the session is
created instead.

Remediation: Set `OnnxSessionOptions.FreeDimensionOverrides` (or
`OnnxEmbeddingOptions.PinnedSequenceLength`, which sets both the overrides
and the flag) whenever `RequireStaticInputShapes` is on, or leave
`RequireStaticInputShapes` off.

## Sub-project 2 — `Qavren.Edge.Onnx` (5051–5056): model provisioning

## 5051

**ModelNotRegistered**

Meaning: The requested model id was never declared by any registered model
source.

Remediation: Register the model (via the model source that owns it) before
requesting it; check for a typo'd model or preset id.

## 5052

**ModelNotProvisioned**

Meaning: The model is registered, but its files have not yet been downloaded
or staged to local storage.

Remediation: Provision the model (explicitly, or let first-use
auto-provisioning run) before using it.

## 5053

**ModelDownloadFailed**

Meaning: Downloading the model's bytes failed — network failure, a
non-2xx/206 response, or a server that ignored a `Range` request and forced a
restart that then also failed.

Remediation: Check connectivity to the model source and retry; if the server
consistently ignores `Range`, the download restarts from scratch each time by
design.

## 5054

**ModelHashMismatch**

Meaning: The SHA-256 of a downloaded or staged file does not match the
manifest, even after the one automatic retry.

Remediation: Delete the cached file and re-download; confirm the manifest's
recorded hash still matches the current revision of the source.

## 5055

**ModelAssetMissing**

Meaning: A file the model's manifest requires (for example `vocab.txt`) is
not present on disk at load time.

Remediation: Re-provision the model bundle; confirm the model root directory
was not partially deleted or moved.

## 5056

**ModelInsufficientDiskSpace**

Meaning: Free disk space is short of the remaining bytes to download plus a
safety margin — checked before the download starts, not after it fails
partway.

Remediation: Free disk space, or choose a smaller preset (an int8 variant).

## Sub-project 2 — `Qavren.Edge.Embeddings.Onnx` (5101–5105)

## 5101

**TokenizerAssetMissing**

Meaning: A file the tokenizer needs (for example `vocab.txt`) is missing for
the preset being loaded.

Remediation: Re-provision the model/tokenizer bundle; verify the configured
model root path.

## 5102

**TokenizerKindUnsupported**

Meaning: The requested tokenizer kind is not implemented yet — today this is
any multilingual preset that needs a Unigram/SentencePiece tokenizer.
`Microsoft.ML.Tokenizers` 2.0.0 has no `CreateFromTokenizerJson`, and its
`SentencePieceTokenizer.Create` does not produce the fairseq token layout
XLM-R/e5-style graphs expect (see ADR 0006).

Remediation: Use one of the shipped WordPiece/BERT-tokenized presets until
multilingual support lands on `Microsoft.ML.Tokenizers` 3.x (ADR 0006).

## 5103

**EmbeddingDimensionMismatch**

Meaning: The embedding generator's actual output dimension does not match
the dimension declared elsewhere (a preset's stated dimension, or a
`DefaultModelDimensions` override).

Remediation: Confirm the configured preset and any dimension override agree
with the preset's real output size.

## 5104

**EmbeddingPresetNotFound**

Meaning: The requested preset id does not match any of the shipped
`EmbeddingPresets`.

Remediation: Check the spelling of the preset constant; only the four shipped
presets (`MiniLmL6V2Int8`, `MiniLmL6V2Fp32`, `BgeSmallEnV15`,
`NomicEmbedTextV15Int8`) exist in v1.

## 5105

**EmbeddingInputTooLong**

Meaning: The input text, once tokenized, exceeds the sequence length the
generator is configured to accept.

Remediation: Shorten the input or chunk it into smaller pieces before
embedding.

## Sub-project 2 — `Qavren.Edge.VectorData` (5201–5213)

## 5201

**VectorCollectionNotFound**

Meaning: The requested collection name has no matching table.

Remediation: Call `EnsureCollectionExistsAsync` first, or check the
collection name for a typo.

## 5202

**UnsupportedKeyType**

Meaning: The `[VectorStoreKey]` property's CLR type is not one the provider
supports. Enforced twice: on `TKey` in the collection constructor, and again on
the resolved key property in `ValidateKeyProperty`.

Remediation: Use one of the four supported key types — `int`, `long`, `string`
or `Guid`. Auto-generated keys are supported for `Guid` (assigned client-side
with `Guid.CreateVersion7()`) and for `int`/`long` (left to SQLite and read back
with `RETURNING`).

## 5203

**UnsupportedPropertyType**

Meaning: A data or vector property's CLR type has no SQL mapping in this
provider.

Remediation: Change the property's type to a supported one, or correct its
`[VectorStoreData]`/`[VectorStoreVector]` attribute.

## 5204

**UnsupportedDistanceFunction**

Meaning: The `DistanceFunction` set on `[VectorStoreVector]` is not one
`vec0` supports. `CosineSimilarity`, both dot products, `HammingDistance` and
`EuclideanSquaredDistance` all land here; the exception message names the three
that do work.

Remediation: Use one of the three supported values — `CosineDistance` (the
default, mapped to vec0's `cosine`), `EuclideanDistance` (`l2`) or
`ManhattanDistance` (`l1`).

## 5205

**VectorDimensionMismatch**

Meaning: The embedding generator's dimension does not match the vector
property's declared dimension. Raised at collection creation when both are
known, or at first upsert if the generator publishes no metadata up front.

Remediation: Make `[VectorStoreVector(dims)]` match the configured embedding
generator's actual output dimension.

## 5206

**FullTextPropertyMissing**

Meaning: `HybridSearchAsync` was called on a record type with no
`[VectorStoreData(IsFullTextIndexed = true)]` property.

Remediation: Mark at least one text property `IsFullTextIndexed = true`, or
call `VectorSearchAsync` instead of hybrid search.

## 5207

**EmbeddingGeneratorMissing**

Meaning: A `string`-source vector property needs an `IEmbeddingGenerator`,
and DI resolution (including the non-generic fallback) found none.

Remediation: Call `AddOnnxEmbeddings` (or otherwise register a matching
`IEmbeddingGenerator`) before `AddVectorStore`.

## 5208

**MultipleVectorPropertiesUnsupported**

Meaning: The record type declares more than one `[VectorStoreVector]`
property; v1 supports exactly one.

Remediation: Keep exactly one vector property per record type.

## 5209

**NullableVectorProperty**

Meaning: A vector property was declared nullable. This is rejected at model
build time rather than left as a runtime "NULL means don't touch this
column" trap on upsert.

Remediation: Make the vector property's type non-nullable.

## 5210

**KnnLimitExceeded**

Meaning: `top + Skip` exceeds `SQLITE_VEC_VEC0_K_MAX` — `vec0`'s own cap on
`k` for a single KNN query. Raised before the query reaches SQLite.

Remediation: Reduce `top` and/or `Skip` so their sum stays within `vec0`'s
`k` limit (4096).

## 5211

**SqliteVersionTooOld**

Meaning: The SQLite build in use predates the version `vec0`/FTS5 features
this provider depends on require.

Remediation: Use the bundled `qedge_sqlite3` native (`Qavren.Edge.Sqlite.Native`)
rather than an external or OS-provided SQLite.

## 5212

**VectorStoreOperationFailed**

Meaning: A `SqliteException` surfaced during a store operation and was
wrapped so callers get one exception type across every operation.

Remediation: Inspect the wrapped `SqliteException` (`InnerException`) for the
underlying SQL error.

## 5213

**ReservedColumnName**

Meaning: A property's storage name collides with `_rowid`, the name `vec0`
reserves internally. Raised at model build time, naming the offending
property — not as a duplicate-column SQL error the first time the table is
touched.

Remediation: Rename the property, or give it a different
`[VectorStoreData(StorageName = ...)]`, so it does not resolve to `_rowid`.

## 6000–6299 — sub-project 3

### Sub-project 3 — `Qavren.Edge.Ingestion` (6001–6011): configuration and runner

## 6001

**TokenCounterMissing**

Meaning: No `IChunkTokenizer` is resolvable at startup (the order-400 startup
task's tokenizer resolve). There is deliberately no chars/4 fallback — that
is the prior art's hidden-truncation bug.

Remediation: Call `AddOnnxIngestion()` to use SP2's tokenizer, or register
one yourself with `UseChunkTokenizer(...)` / `EdgeTokenCounter.CreateWordPiece(...)`.

## 6002

**IngestionCollectionNotConfigured**

Meaning: `RunAsync`, `PruneAsync`, `GetStatusAsync`, `RemoveSourceAsync` or
`RemoveDocumentAsync` named a `collectionName` that no `AddIngestion` call
configured.

Remediation: Call `AddIngestion` for that collection, or pass `null` to use
the single configured one.

## 6003

**IngestionChunkBudgetInvalid**

Meaning: The resolved chunk budget arithmetic is invalid — checked at
startup, not at registration, because it needs the resolved
`IChunkTokenizer` for `SpecialTokenOverhead` and the `DocumentPrefix`
reserve. The message carries the arithmetic.

Remediation: Adjust `ChunkOptions` (`OverlapTokens`, `MinTokens`,
`HeadingPathTokenBudget`) or the `Model` profile so
`MaxTokens + SpecialTokenOverhead + HeadingPathTokenBudget + DocumentPrefixTokens
<= Model.MaxSequenceLength`, `OverlapTokens < MaxTokens / 2` and
`MinTokens < MaxTokens`.

## 6004

**IngestionDuplicateExtractorId**

Meaning: Two `IDocumentExtractor`s registered in `DocumentExtractorRegistry`
composition declared the same `Id`.

Remediation: Give each extractor a distinct `Id`, or remove the duplicate
registration.

## 6005

**IngestionOptionsInvalid**

Meaning: A tokenizer-independent option failed validation at registration:
an explicitly-set `OverlapTokens >= MaxTokens / 2` or `MinTokens >= MaxTokens`;
a non-positive `HeadingPathTokenBudget`, `WriteBatchSize` or
`DeleteBatchSize`; a `SleepGraceBudget` over 2 s; a `FullTextRemoveDiacritics`
outside 0–2; a `StateTablePrefix` that does not match
`^[A-Za-z_][A-Za-z0-9_]*$`; or a `Model` disagreeing with the resolved preset
(`AddOnnxIngestion`).

Remediation: The message names the offending value(s) — adjust
`IngestionOptions` accordingly before the next start.

## 6006

**IngestionCollectionDimensionMismatch**

Meaning: The embedding generator's
`EmbeddingGeneratorMetadata.DefaultModelDimensions` disagrees with the
collection's declared dimensions (order-400 startup task).

Remediation: Use a generator whose dimensions match the collection, or
reconfigure the collection to match the generator.

## 6007

**IngestionMigrationVersionConflict**

Meaning: A second `AddIngestion` call was made for the same collection.

Remediation: Call `AddIngestion` once per collection; remove the duplicate
call.

## 6008

**IngestionRunAlreadyActive**

Meaning: `IngestionPipeline.RunAsync` was called while a run is already
active on the same pipeline. The message names the active run id.

Remediation: Await the active run's `IngestionRunResult` before starting
another on the same pipeline, or use a separate pipeline/collection.

## 6009

**IngestionRecipeChanged**

Meaning: The recipe hash (chunking and extraction configuration) differs
from the one the state store last recorded. With `StrictRecipe = false` (the
default) this does **not** throw — the affected documents are silently
re-indexed under the new recipe and event 915 is logged once with both
hashes. Only `StrictRecipe = true` throws `IngestionRecipeChanged`, with both
hashes in the message.

Remediation: On the default, no action is needed — the re-index is
automatic and logged. With `StrictRecipe = true`, resolve the drift
deliberately: restore the prior chunking/extraction configuration, or accept
the new recipe and re-run.

## 6010

**IngestionRunAborted**

Meaning: `AbortAfterConsecutiveErrors` (20) consecutive document failures
were reached in one run; the run aborts with the last failure as the inner
exception.

Remediation: Inspect the inner exception and the per-document failures in
`IngestionRunResult.Documents` — twenty in a row usually means a systemic
problem (an unreachable source, a missing extractor satellite, a bad share),
not one bad file.

## 6011

**IngestionCollectionSchemaMismatch**

Meaning: The migration's collection DDL differs from the runtime store's
`BuildCreateSql()` for the same collection, checked at startup by comparing
emitted SQL. Naming the first differing statement.

Remediation: Pass the same shaping values to `AddIngestion`'s
`ConfigureCollection` that you passed to `AddVectorStore` — including
`IngestionOptions.FullTextRemoveDiacritics`, because
`EdgeVectorStoreCollectionOptions.RemoveDiacritics` is `internal` and cannot
otherwise be set to match from outside `Qavren.Edge.VectorData`.

### Sub-project 3 — `Qavren.Edge.Ingestion` (6051–6055): source

## 6051

**IngestionSourceUnavailable**

Meaning: The configured folder is missing or unreadable. Thrown before any
document is processed — the whole run would otherwise be meaningless.

Remediation: Verify the path exists and the process has read access before
calling `RunAsync`.

## 6052

**IngestionDocumentTooLarge**

Meaning: A document exceeded `MaxDocumentBytes`. Recorded `Failed` (event
907). When the source declared a size the file is never opened; otherwise
the hash pass's counted read aborts at the ceiling and no content hash is
stored, so a later shrink is retried automatically.

Remediation: Raise `MaxDocumentBytes`, or exclude the oversized document
from the source.

## 6053

**IngestionDocumentUnreadable**

Meaning: Opening the document raised `IOException` / `UnauthorizedAccessException`,
or a non-seekable stream exceeded `NonSeekableBufferLimitBytes`. Recorded,
not a run failure — a file deleted between enumeration and open is the
common, expected case.

Remediation: Verify the file still exists and is readable; for a
non-seekable source, raise `NonSeekableBufferLimitBytes` or supply a
seekable stream.

## 6054

**IngestionDuplicateDocumentId**

Meaning: One `DocumentId` was yielded twice within the same run, caught by
`IngestionRunner`'s per-run id set. The second occurrence is recorded
`Failed` rather than silently overwriting the first.

Remediation: Make `DocumentSourceItem.DocumentId` (or the source's id
derivation) unique per run — never a value that can repeat, such as an
empty document's front matter.

## 6055

**IngestionSourceIdInvalid**

Meaning: An `IngestionSource` factory (`Folder`, `Items`) was given an
invalid `sourceId`.

Remediation: Supply a non-empty, stable `sourceId` — state rows are keyed on
`(collection, source_id, document_id)`.

### Sub-project 3 — `Qavren.Edge.Ingestion` / `.Pdf` / `.OpenXml` (6101–6107): extraction

## 6101

**ExtractorNotFound**

Meaning: No registered `IDocumentExtractor` claims the document's extension.
Recorded `Unsupported`.

Remediation: Register the matching satellite — call `AddPdfExtractor()` for
`.pdf`, `AddDocxExtractor()` for `.docx` — or register a custom
`IDocumentExtractor` for the extension.

## 6102

**ExtractionFailed**

Meaning: `IDocumentExtractor.ExtractAsync` threw something that is not
already `DocumentEncrypted` (6103), `DocumentMalformed` (6104),
`DocumentEncodingUndecodable` (6106) or `DocumentPageBudgetExceeded` (6107).
Recorded `Failed`, the original exception preserved as inner, `ExtractorId`
set.

Remediation: Read the inner exception for the extractor's own diagnosis; the
document is recorded `Failed` and the run continues — one bad document fails
one document, never a corpus.

## 6103

**DocumentEncrypted**

Meaning: `PdfTextExtractor` could not open an encrypted PDF with the
configured `Passwords`.

Remediation: Add the document's password to `PdfExtractorOptions.Passwords`,
or exclude the document from the source.

## 6104

**DocumentMalformed**

Meaning: `PdfTextExtractor` or `DocxTextExtractor` could not parse the
document's structure — for example a broken cross-reference table.

Remediation: Re-export or repair the source document; PdfPig's
`UseLenientParsing` (on by default) already tolerates the common cases.

## 6105

**DocumentHasNoTextLayer**

Meaning: Every page of a PDF has no text layer (`page.Letters.Count == 0 &&
page.NumberOfImages > 0` on every page) — a scanned document. Recorded
`NoTextLayer`, not an error outcome; its content hash is stored so it is not
re-parsed every run.

Remediation: Run OCR upstream and re-ingest the result, or accept that the
document is indexed as metadata only.

## 6106

**DocumentEncodingUndecodable**

Meaning: `PlainTextExtractor` found invalid UTF-8 with `StrictUtf8` set.
Unreachable on defaults, which fall back to Latin-1 and log event 914.

Remediation: Leave `StrictUtf8` off (the default) to accept the Latin-1
fallback, or re-save the source file as valid UTF-8.

## 6107

**DocumentPageBudgetExceeded**

Meaning: A PDF's cumulative page-parse time passed `PageBudget`, checked at
a page boundary. Pages already parsed are kept, extraction stops there, and
the document is recorded `Failed` naming the page reached (event 924). This
is a between-pages watchdog, not a page-level abort — PdfPig's page API
takes no `CancellationToken`.

Remediation: Raise `PdfExtractorOptions.PageBudget`, or exclude the
document. A single page that never returns is not something `PageBudget`
can defend against — only the consumer's own process-level budget can.

### Sub-project 3 — `Qavren.Edge.Ingestion` (6151–6155): chunking

## 6151

**ChunkExceedsTokenBudget**

Meaning: A chunker emitted a chunk over `MaxTokens`. Thrown in every
configuration — this is an SP3 invariant violation, not a user-data
problem.

Remediation: File an issue; this should never happen on a shipped chunker.
If it follows a custom chunker or tokenizer change, check its pre-yield
token verification.

## 6152

**ChunkContextTooLong**

Meaning: A chunk's heading-path breadcrumb still exceeds
`HeadingPathTokenBudget` after left-truncation.

Remediation: Raise `HeadingPathTokenBudget`, shorten the document's heading
structure, or set `Overflow` to the mode that honours truncation instead of
throwing.

## 6153

**ChunkTokenizerCeilingExceeded**

Meaning: The resolved `IChunkTokenizer.MaxSequenceLength` disagrees with
`ChunkModelProfile.MaxSequenceLength` (`ChunkOptions.Resolve`'s ceiling
assertion).

Remediation: Use a tokenizer and a `ChunkModelProfile` for the same model —
a mismatch means every number the budget resolves is a guess.

## 6154

**ChunkerProducedEmptyChunk**

Meaning: A chunker's pre-yield verification caught an empty chunk about to
be emitted.

Remediation: File an issue; this should never happen on a shipped chunker.
If it follows a custom chunker, check its emission logic.

## 6155

**MarkdownParseFailed**

Meaning: `MarkdownExtractor` (Markdig) failed to parse the document.

Remediation: Validate the Markdown source, or extract it as plain text
instead.

### Sub-project 3 — `Qavren.Edge.Ingestion` (6201–6208): state and writes

## 6201

**IngestionStateMissing**

Meaning: `IngestionStateStore` opened a database whose state tables do not
exist.

Remediation: Call `AddIngestion(migrationVersion)` so the migration creates
the state tables before the first run.

## 6202

**IngestionHashAlgorithmMismatch**

Meaning: The state store's meta row records a hash algorithm different from
the one the running code uses.

Remediation: This suite has no down-migrations — delete the state rows and
re-run to rebuild them under the current algorithm.

## 6203

**IngestionStateSchemaUnsupported**

Meaning: The state store's meta row records a schema version the running
code does not understand (forward-only; no down-migrations).

Remediation: Upgrade to the package version that wrote the state, or delete
the state rows and re-run.

## 6204

**IngestionStateCorrupt**

Meaning: A stored content hash is not the expected 16 bytes, caught by
`ContentHash.FromBlob` or on a state row read.

Remediation: Delete the affected state row(s) and re-run so they are
rebuilt cleanly.

## 6205

**IngestionCheckpointWriteFailed**

Meaning: `ChunkWriter`'s state-write step failed — disk full, or
`SQLITE_BUSY` past `BusyTimeout`. The transaction rolled back, so the
document stays at its previous recorded state, and the run aborts: an
unwritten checkpoint would make the next run believe finished work is still
owed.

Remediation: Free disk space, raise `BusyTimeout`, or reduce write
concurrency against the database, then re-run.

## 6206

**IngestionEmbeddingFailed**

Meaning: SP3's own `GenerateAsync` call (the embed step) threw. The runner
halves the batch, logs event 918, and retries the embed once; a second
failure suspends the run rather than thrashing a device already under
pressure. SP2's `OnnxInsufficientMemory` (5005) surfaces verbatim as the
inner exception when that is the cause.

Remediation: Inspect the inner exception; for `OnnxInsufficientMemory`,
lower `WriteBatchSize` or the effective batch size.

## 6207

**IngestionWriteFailed**

Meaning: SP2's `UpsertAsync` or `DeleteAsync` threw. Reported, not retried;
the original `EdgeVectorStoreException` is preserved as inner. Distinguished
from `IngestionEmbeddingFailed` (6206) by call site, not by exception type.

Remediation: Inspect the inner `EdgeVectorStoreException` for the
underlying store failure.

## 6208

**IngestionEmbeddingGeneratorMissing**

Meaning: No `IEmbeddingGenerator<string, Embedding<float>>` is resolvable,
unkeyed or keyed on `StoreName`, at startup (order-400 startup task). SP3
calls the generator itself, so its absence is a start-time fact, not a
first-document surprise.

Remediation: Call `AddOnnxEmbeddings()` or register another
`IEmbeddingGenerator<string, Embedding<float>>` before `AddIngestion`.

## Sub-project 4 — Qavren.Edge.Chat.Onnx / Qavren.Edge.Rag (7001–7204)

## 7001

**ChatEnvironmentNotStarted**

Meaning: A `Microsoft.ML.OnnxRuntimeGenAI` type was reached before the
order-400 startup task ran, or the order-200 ONNX environment task has not
run at all.

Remediation: Call `AddOnnxChat()` and resolve `IChatClient` from the
container rather than constructing `EdgeChatClient` yourself.

## 7002

**ChatModelLoadFailed**

Meaning: `new Model(...)` or `new Tokenizer(...)` threw an
`OnnxRuntimeGenAIException`, or the load did not finish inside
`LoadTimeout`. Carries the model directory and the inner message preserved.

Remediation: Inspect the inner exception and the model directory for
corruption; re-provision the bundle if the files are damaged, or raise
`LoadTimeout` if the device is simply slow to load.

## 7003

**ChatModelNotRegistered**

Meaning: A preset lookup (`ChatPresets.ById`) was asked for an id that does
not match any of the shipped presets.

Remediation: Check the spelling of the preset constant; only the shipped
`ChatPresets` members exist in v1.

## 7004

**ChatUnsupportedRuntime**

Meaning: The current runtime identifier or Android ABI has no ORT GenAI
native for chat (for example `armeabi-v7a`, which the AAR does not ship).
Raised at startup, naming the RID or ABI and listing the ones that do work
— never a `DllNotFoundException` on the user's first message.

Remediation: Ship chat only for the supported ABIs (`arm64-v8a`, `x86_64`),
or fall back to `AddExtractiveChat` on a runtime GenAI does not cover.

## 7005

**ChatInsufficientMemory**

Meaning: The memory budget refused even at `MinContextTokens`. Carries the
required, available and total byte counts, the budget kind, and the largest
context that would have fit.

Remediation: Names, in order, the smaller preset, a lower
`MaxContextTokens`, and the two iOS entitlements
(`com.apple.developer.kernel.increased-memory-limit` and
`com.apple.developer.kernel.extended-virtual-addressing`) — and states that
`WorkspaceBytes` and `ReserveBytes` are engineering estimates, not measured
figures.

## 7006

**ChatDeviceTooSmall**

Meaning: Total device RAM is below the preset's floor, or the device
reports `IsLowRamDevice`. Carries `TotalMemoryBytes`.

Remediation: `MinTotalMemoryBytes` can be set to null to try anyway, at the
risk of an OS kill.

## 7007

**ChatConfigurationInvalid**

Meaning: `genai_config.json` is missing, unparseable, or incoherent (zero
layers, zero head size, a missing decoder filename). Names the file and the
offending field.

Remediation: Re-provision the model bundle; if the file is present and
still fails, confirm it is a genuine ORT GenAI export rather than a
hand-edited or truncated config.

## 7008

**ChatModelShapeMismatch**

Meaning: The provisioned `genai_config.json` disagrees, field by field,
with the preset's declared `ChatModelShape`. Names the field, the declared
value and the preset's.

Remediation: Re-run `fetch_chat_model_hashes.py`.

## 7009

**ChatExecutionProviderUnsupported**

Meaning: `ConfigOverlayJson` names an execution provider other than CPU on
a mobile TFM. GenAI has no CoreML and no NNAPI provider, so a non-CPU
provider on iOS, Mac Catalyst or Android is refused by name rather than
being silently ignored by the native config parser.

Remediation: Remove the provider override on mobile targets; CPU is the
only execution provider chat supports there in v1 (see ADR 0009).

## 7051

**ChatModelNotProvisioned**

Meaning: The model is absent on disk. Carries the bundle's total byte
count. Never a download.

Remediation: Call `IChatModelProvisioner.Plan()`, show the consent sheet it
feeds, then `ProvisionAsync()`.

## 7052

**ChatInsufficientDiskSpace**

Meaning: Free disk space is short of the bundle total plus a safety
margin — checked before the first byte, not partway through the transfer.

Remediation: Free disk space, or choose the smaller preset.

## 7053

**ChatDownloadNotPermitted**

Meaning: `ChatProvisioningOptions.IsTransferPermitted` returned false
before a connection was opened.

Remediation: Names the hook and the usual policy: wait for an unmetered
network before retrying `ProvisionAsync`.

## 7101

**ChatTemplateUnsupported**

Meaning: minja could not parse the model's chat template, and
`RequireChatTemplate` is set.

Remediation: Set `PromptFormatter` to a formatter minja can parse, or a
hand-written one.

## 7102

**ChatPromptTooLong**

Meaning: The prompt is still over budget after history reduction to the
floor. Carries both numbers.

Remediation: Names `MaxOutputTokens`, `ChatHistoryOptions`, and
`RagOptions.MaxContextTokens`.

## 7103

**ChatGuidanceUnavailable**

Meaning: `ChatOptions.ResponseFormat` is a `ChatResponseFormatJson` and the
guidance policy is `Disabled` — or `RequireNative`/`PreferNative` and the
positive-control probe did not come back `Enforced`. The shipped mobile
natives are built without `USE_GUIDANCE`, and a request against such a
build would otherwise be silently ignored.

Remediation: Set `EdgeGuidancePolicy.PreferNative` to accept an
unconstrained turn instead of a throw, or accept plain-text output.

## 7104

**ChatGenerationFailed**

Meaning: The native decode loop threw mid-decode. The partial text already
streamed stays streamed; the final update reports `StopReason = Error`
before the exception surfaces from the iterator.

Remediation: Inspect the inner exception; retry the turn.

## 7105

**ChatBusy**

Meaning: A second concurrent turn arrived while one was already running,
the gate timed out, the queue is full, or turns are not being accepted —
naming which.

Remediation: States that the GenAI C API is not thread safe, so turns
serialise onto one cached `Generator`; queue the caller's own retry rather
than starting a second turn concurrently.

## 7106

**ChatThermalAbort**

Meaning: Device thermal state is at or above `AbortAt` before a turn
starts. (The same condition reached mid-decode instead completes the
stream with `StopReason = Thermal` rather than throwing.)

Remediation: Wait for the device to cool, or raise `AbortAt`/`ThrottleAt`
if the app's own UX already warns the user.

## 7107

**ChatToolCallingUnsupported**

Meaning: `ChatOptions.Tools` is non-empty, or `ToolMode` requires a call. A
~1B on-device model has no tool calling in v1 (ADR 0012); a required tool
call that can never be emitted is a hard failure, not a footnote.

Remediation: Remove `Tools`/`ToolMode` from the request, or perform tool
calling above this client against a server-hosted model.

## 7108

**ChatOptionUnsupported**

Meaning: A message carries non-`TextContent`, or `SearchOptions` sets
`max_length` directly — naming the offending member. `max_length` is
refused whatever its type, because it is the memory cap the budget owns.

Remediation: Remove the unsupported content type or option from the
request; use `ChatOptions.MaxOutputTokens` and the memory budget instead of
`SearchOptions["max_length"]`.

## 7201

**RagRetrieverMissing**

Meaning: `UseRag()` was added to the pipeline but no `IEdgeRetriever` is
resolvable from the container.

Remediation: Names `AddVectorStoreRetriever`/`AddRetriever` — register one
of them before resolving `IChatClient`.

## 7202

**RagRetrievalFailed**

Meaning: Retrieval threw and `ContinueOnRetrievalFailure` is false, or
`Top + Skip` exceeded `SQLITE_VEC_VEC0_K_MAX` — named before SP2 ever sees
the call.

Remediation: Fix the retriever's collection or query, or lower `Top`/`Skip`
under the vec0 candidate ceiling; leave `ContinueOnRetrievalFailure` at its
default to get an ungrounded answer instead of a throw.

## 7203

**RagCollectionNotSearchable**

Meaning: `RequireHybridSearch` is true and the collection does not
implement `IKeywordHybridSearchable<TRecord>`, so the hybrid lane cannot be
taken and the retriever refuses rather than silently falling back to the
vector lane.

Remediation: Point the retriever at a collection that implements
`IKeywordHybridSearchable<TRecord>`, or leave `RequireHybridSearch` at its
default `false` to accept the vector-lane fallback.

## 7204

**RagContextBudgetTooSmall**

Meaning: `MaxContextTokens` cannot fit even one truncated source.

Remediation: Raise `RagOptions.MaxContextTokens`, or lower
`MaxCharsPerSource` so a single source fits inside the budget.
