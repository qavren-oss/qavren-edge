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

Remediation: Call `CreateCollectionIfNotExistsAsync` first, or check the
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
