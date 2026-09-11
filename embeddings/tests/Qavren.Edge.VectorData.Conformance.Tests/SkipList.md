# Conformance skip list

`Microsoft.Extensions.VectorData.ConformanceTests` 10.10.0 is the gate for
`Qavren.Edge.VectorData` (spec §3, §16.2). It runs against **both** record paths:
`EdgeVectorStoreCollection<TKey, TRecord>` (the reflecting path, `EdgeBasicModelTests`,
`EdgeFilterTests`, `EdgeDataTypeTests`, …) and `EdgeDynamicVectorStoreCollection` (the dynamic
path, `EdgeDynamicModelTests`, plus the dynamic half that `FilterTests` and `DataTypeTests` run
inside every one of their tests).

This file is the honest record of the gap. Every entry below is also a `Skip =` string on the
overriding member, or an entry in `EdgeTestSuiteImplementationTests.IgnoredTestBases`, so the two
can never drift apart silently: `TestSuiteImplementationTests.All_test_bases_must_be_implemented`
fails the build if a suite is dropped without being listed.

**A skip with no named v1 cut is a bug, not a cut.** Tests that fail because the provider is wrong
are *not* in this file — they are listed in "Known failures" at the bottom and are owed a fix.

---

## The gate surface, in numbers

A green run is not self-describing: a skip is visible in the summary line, but a suite that was
never derived, a property dropped from a fixture's record definition, and a column suppressed
through `UnsupportedDefaultTypes` are all invisible there. Every one of them is counted here.

| Reduction | Count | Visible in the run summary? | Guarded by |
|---|---|---|---|
| Individual tests skipped | **15** | yes, as `Skipped` | the `Skip =` string on each override, one row each below |
| Whole suites not derived | **4** | **no** | `EdgeTestSuiteImplementationTests.IgnoredTestBases` — `All_test_bases_must_be_implemented` fails if a suite is dropped without being listed |
| `FilterRecord` properties dropped by `EdgeFilterFixture` | **2** (`StringArray`, `StringList`) | **no** | `SurfaceReductionTests` — asserts the drop is *exactly* these two and that `SqliteTypeMap` genuinely cannot store either |
| `DefaultRecord` columns suppressed by `EdgeDataTypeFixture` | **3** (`byte`, `decimal`, `string[]`) | **no** | `SurfaceReductionTests` — asserts the list is *exactly* these three and that `SqliteTypeMap` genuinely cannot store any of them |
| Vector property re-declared non-nullable | **1** | **no** | `SurfaceReductionTests` |

`SurfaceReductionTests` is the answer to "how do I know the narrowing is honest?" — it fails if a
reduction widens, if one is removed from the fixture but left documented here, or if a type the
provider *can* store is being suppressed anyway. Five assertions, no I/O, sub-second.

---

## Whole suites not applicable (`IgnoredTestBases`)

| Suite | Cut |
|---|---|
| `ModelTests.MultiVectorModelTests<TKey>` | **Multiple vector properties per collection** (spec §20). `EdgeCollectionModelBuilder` sets `CollectionModelBuildingOptions.SupportsMultipleVectors = false` and raises `EdgeErrorCode.MultipleVectorPropertiesUnsupported`. A second vector property means a second vec0 table and a materially worse hybrid query; splitting the record across two collections is the supported answer. |
| `ModelTests.NoVectorModelTests<TKey>` | **A collection with no vector property.** The schema is one data table plus a vec0 sidecar plus an optional FTS5 sidecar (spec §12.1), so `RequiresAtLeastOneVector` is true. A vectorless collection is a plain SQLite table, which sub-project 1 already provides without this package. |
| `DependencyInjectionTests<TKey>` and `DependencyInjectionTests<TVectorStore, TCollection, TKey, TRecord>` | **MEVD's `IServiceCollection.AddXxxVectorStore(serviceKey, lifetime)` registration shape.** This provider registers through sub-project 1's `EdgeBuilder` — `AddQavrenEdge(edge => edge.AddVectorStore())` — because a store is bound to an `IEdgeDatabase` rather than to a connection string (spec §8, §14.1), and there is no per-collection service registration at all. The delegate signatures the suite requires do not exist here. |

## Individual tests skipped

| Test | Cut |
|---|---|
| `EdgeDistanceFunctionTests.CosineSimilarity` | **vec0 computes distances, not similarities.** `EdgeCollectionModelBuilder.TryMapDistanceFunction` maps exactly three MEVD names onto vec0's three `distance_metric` values: `CosineDistance`→cosine, `EuclideanDistance`→L2, `ManhattanDistance`→L1 (spec §12.1). Anything else is rejected at model build with `EdgeErrorCode.UnsupportedDistanceFunction` rather than silently computed with the wrong metric. |
| `EdgeDistanceFunctionTests.DotProductSimilarity` | Same cut. |
| `EdgeDistanceFunctionTests.NegativeDotProductSimilarity` | Same cut. |
| `EdgeDistanceFunctionTests.EuclideanSquaredDistance` | Same cut. |
| `EdgeDistanceFunctionTests.HammingDistance` | **vec0 `int8` / `bit` vector element types** (spec §20, §12.1). Hamming distance is defined over `bit` vectors. `EdgeCollectionModelBuilder` declares a single `EmbeddingGenerationDispatcher` over `Embedding<float>`, which is the one place that cut is enforced rather than merely documented. |
| `EdgeFilterTests.Contains_over_field_string_array` | **Collection-valued data properties** (`string[]`, `List<string>`) are not in `SqliteTypeMap.SupportedDataTypes` (spec §12.2). Storing one needs a JSON encoding with its own containment operator, or a child table and a join — a real feature with a real API cost and no caller yet. `EdgeFilterFixture` omits `StringArray`/`StringList` from the record definition. **Scalar `Contains` — an inline or captured array tested against a column, i.e. SQL `IN (...)` — is supported and is not skipped.** |
| `EdgeFilterTests.Contains_over_field_string_List` | Same cut. |
| `EdgeFilterTests.Contains_with_Enumerable_Contains` | Same cut (`Enumerable.Contains(r.StringArray, "x")`). |
| `EdgeFilterTests.Contains_with_MemoryExtensions_Contains` | Same cut. |
| `EdgeFilterTests.Contains_with_MemoryExtensions_Contains_with_null_comparer` | Same cut. |
| `EdgeFilterTests.Any_with_Contains_over_inline_string_array` | Same cut. |
| `EdgeFilterTests.Any_with_Contains_over_captured_string_array` | Same cut. |
| `EdgeFilterTests.Any_with_Contains_over_captured_string_list` | Same cut. |
| `EdgeFilterTests.Any_over_List_with_Contains_over_captured_string_array` | Same cut. |
| `EdgeEmbeddingGenerationTests.SearchAsync_with_collection_dependency_injection` | **No per-collection DI registration.** `AddVectorStore` registers `EdgeVectorStore` and MEVD's `VectorStore`; a collection is obtained from the store, and `AddVectorCollectionMigration` registers schema rather than a resolvable `VectorStoreCollection<TKey, TRecord>` service (spec §8, §14.1). `DependencyInjectionCollectionRegistrationDelegates` is therefore empty, and the test is skipped rather than left to iterate an empty array and pass vacuously. |

## Declared unsupported rather than skipped

`DataTypeTests` reads `Fixture.UnsupportedDefaultTypes` when it builds the record definition, so a
type listed there never gets a column and its test is a documented no-op rather than a false pass.
`EdgeDataTypeFixture` lists three, all for the same reason — they are not in
`SqliteTypeMap.SupportedDataTypes` (spec §12.2):

- `byte` and `decimal` — no lossless SQLite affinity that `Microsoft.Data.Sqlite` reads back
  without a converter.
- `string[]` — the collection-valued-property cut above.

`EdgeFilterFixture` likewise declares the suite's vector property **non-nullable**. vec0 overloads
SQL `NULL` on a vector column to mean "no change", so a nullable vector property turns writing a
null vector into a silent no-op; the provider rejects one at model build with
`EdgeErrorCode.NullableVectorProperty` (spec §12.1). The suite's fixture never writes a null
vector, so nothing it asserts changes.

## v1 cuts with no conformance coverage

These are in spec §20 and the suite has no test for them, so they produce no skip. Listed so the
record of the gap is complete:

- Binary-quantization rescore.
- ANN index kinds — `IndexKindTests` only asserts `IndexKind.Flat`, which vec0 is. Any other kind
  is accepted, logged as ignored (event 804) and scanned linearly.
- Matryoshka dimension truncation.
- `CompactAsync`.
- Cursor-based pagination.
- Down-migrations.

---

## Known failures

**None.** Every test the suite runs against this provider either passes or is skipped with its v1
cut named above. A failing conformance test is never recorded here as a cut: skipping one would turn
a provider defect into a documented "feature", which is the one thing this file exists to prevent.

Run of record: `Total: 180, Errors: 0, Failed: 0, Skipped: 15`, exit code 0 (Release, `net10.0`,
isolated `ArtifactsPath`). The skip count matches the fifteen rows in *Individual tests skipped*
above, one row per `Skip =` string.

### Provider changes this suite forced

The first run of this project found eighteen failures, all of them in
`embeddings/src/Qavren.Edge.VectorData` rather than in the suite's wiring. Each was MEVD's
documented provider contract rather than a conformance-suite quirk, and each would have bitten a
real caller in the same way. They are listed here because they are the substance of what this gate
bought, and because the files involved belong to tasks 3.2 and 4.2:

| # | Site | Change |
|---|---|---|
| 1 | `Internal/EdgeFilterTranslator.cs` — `TranslatePredicate` | A bool-typed node in *predicate* position binds as a column before it is treated as a method call. The dynamic path arrives as `Convert(get_Item(dict, "Bool"), bool)`, which previously reached `TranslateMethodCall` and threw; it now emits `"Bool" = 1`, as the typed path already did. |
| 2 | Same file — `TranslatePredicate` (`Not`) and `TranslateComparison` | SQL's three-valued logic dropped rows C# keeps. `NOT (x)` is now `NOT COALESCE(x, 0)`, and `a != b` over a **nullable** column is now `a IS NOT b`. A non-nullable column keeps plain `<>`, where no NULL can arise. |
| 3 | Same file — `TranslateValue` | MEVD's `InvalidOperationException` for a dynamic property name that is not on the model travels unwrapped; the `NotSupportedException` wrap is kept only for the typed "member maps to no column" case, which is a provider gap rather than a caller error. |
| 4 | Same file — `AppendConstant` (was `FormatLiteral`) | Temporal and `Guid` constants are bound, not inlined. `Microsoft.Data.Sqlite` stores a `DateTime` as `2020-01-01 12:30:45`, not as the round-trip `O` form, so an inlined literal never matched a value the provider itself had written. Binding makes the writer's serialiser format the comparand. Text, booleans and numbers stay inline and keep the emitted SQL readable. |
| 5 | `EdgeVectorStoreCollection.cs` — `AssertVectorsReadable` | `IncludeVectors` on a generating model now carries MEVD's own wording, via `VectorDataStrings.IncludeVectorsNotSupportedWithEmbeddingGeneration`; the suite compares it verbatim. |
| 6 | Same file — `ResolveSearchVectorAsync` | Rewritten around MEVD's dispatcher rather than a `string` whitelist, so a custom `TInput` with a generator configured for exactly it now works. The three failure paths match the abstraction's contract: no generator is `NotSupportedException`, an incompatible generator is `InvalidOperationException`, and both messages come from `VectorDataStrings`. |
| 7 | Same file — `HybridSearchCoreAsync` | `GetFullTextDataPropertyOrSingle` is called even when no property was named, so two full-text columns and no `AdditionalProperty` raise `InvalidOperationException` instead of silently ranking on a lane the caller did not ask for. |

One assertion in `Qavren.Edge.VectorData.Tests/FilterTranslatorTests.cs` moved with change 2: the
golden SQL for `NOT` is now `NOT COALESCE("Tag" = 'red', 0)`, and a companion test pins the
null-safe `IS NOT` on a nullable column.

Three hybrid call sites in that same project moved with change 7. `Note` and `TinyNote` each carry
two `IsFullTextIndexed` columns, so those searches now name the lane
(`AdditionalProperty = n => n.Body`) instead of relying on an unqualified MATCH across both. What
they assert — RRF score polarity, FTS5 operator quoting, the spec 4.3 happy path — is unchanged.
