# Qavren.Edge — embeddings + vector store

`Qavren.Edge.Onnx`, `Qavren.Edge.Embeddings.Onnx` and `Qavren.Edge.VectorData` —
ONNX Runtime hosting, a `Microsoft.Extensions.AI` embedding generator, and a
clean-room `Microsoft.Extensions.VectorData` provider over `vec0` + FTS5 with
reciprocal rank fusion, composed on top of the `Qavren.Edge` foundation
(`foundation/`).

## The four calls

```csharp
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddOnnxEmbeddings(o => o.Preset = EmbeddingPresets.MiniLmL6V2Int8)
    .AddVectorStore()
    .AddVectorCollectionMigration<string, Note>(version: 1, "notes"));
```

```csharp
public sealed class Note
{
    [VectorStoreKey] public string Key { get; set; } = "";
    [VectorStoreData(IsIndexed = true)] public string? Tag { get; set; }
    [VectorStoreData(IsFullTextIndexed = true)] public string Title { get; set; } = "";
    [VectorStoreData(IsFullTextIndexed = true)] public string Body { get; set; } = "";
    [VectorStoreVector(384, DistanceFunction = DistanceFunction.CosineDistance)]
    public string? Embedding => Body;          // string source -> the generator fills it
}

var notes = store.GetCollection<string, Note>("notes");
await notes.UpsertAsync(new Note { Key = "n1", Title = "Roof leak", Body = "…" });
await foreach (var hit in notes.HybridSearchAsync("water damage", ["roof", "leak"], top: 10))
    Console.WriteLine($"{hit.Record.Title} {hit.Score:F4}");
```

`AddOnnxEmbeddings` calls `AddOnnx()` for you; it is idempotent. Nothing above
hands the store a generator explicitly — `AddVectorStore`'s factory resolves
the registered `IEmbeddingGenerator` from the container at collection-resolve
time and uses it wherever a `Note` property's vector source is a `string`
rather than a pre-computed embedding.

## Supported platforms

**Minimum OS versions: Android 24, iOS 15.1, Mac Catalyst 15.1.** These are
ONNX Runtime's own native floors (`default_full_aar_build_settings.json` and
`default_full_apple_framework_build_settings.json`), not a number Qavren
chose — see ADR 0008. They are higher than the foundation package's own
minimums (Android 21, iOS/Mac Catalyst 15.0); an app that adds embeddings
raises its effective floor to match.

**Mac Catalyst is proven in CI as x64 only.** `ci.yml` runs
`device-tests-maccatalyst` and `device-tests-ios` on `macos-15-intel` with
`-r maccatalyst-x64` / `-r iossimulator-x64`. A green lane there proves the
`maccatalyst-x64` RID-graph resolution and exercises the x86_64 slices — it
says nothing about `maccatalyst-arm64` or a real device. A manual arm64 run
on the Mac Mini is the only additional evidence, and it is recorded in the
project vault, not re-derived here.

**`osx-x64` (Intel macOS) is unsupported, not merely untested.** ONNX Runtime
1.30.0 ships no `osx-x64` native at all (verified: its `runtimes/` directory
holds `osx-arm64` only). `Qavren.Edge.Onnx` detects this at startup and
raises `OnnxUnsupportedRuntime` (error 5006) naming the RID, rather than
letting the failure surface later as a `DllNotFoundException` on first embed
call.

## Packaging

**Bundle `MiniLmL6V2Int8` (23 MB) if you bundle a model at all. Download
everything larger.** The size ceilings that drive this, none of them
estimated:

| Platform | Limit | What it means here |
|---|---|---|
| Google Play, AAB base module | 500 MB compressed download | `BgeSmallEnV15` at 133 MB fits with room; two bundled presets does not leave much |
| Google Play, legacy APK | 100 MB | `BgeSmallEnV15` (133 MB) does **not** fit — ship an AAB, or download the model |
| Google Play, any delivery | large-download notice above ~200 MB | a bundled 133 MB model plus the app pushes into the band where users see a warning |
| iOS, cellular install | user is prompted above ~200 MB | a bundled fp32 preset can push an otherwise-small app across this |
| iOS / Mac Catalyst, executable `__TEXT` | **80 MB**, no workaround | ORT's xcframework is a static library force-loaded (`ForceLoad=True`) into the app binary, so its selected slice counts against this cap directly; the model file itself does not, since it is a resource, not code |

On the APK path, trim `$(AndroidSupportedAbis)` to the ABIs you actually
ship — leave the AAB path's ABI set alone; Play already splits it per device.

**No size number of our own is stated here until §19 item 3 is measured** —
the actual `.apk`/`.aab` delta and iOS `__TEXT` delta that adding
`Qavren.Edge.Onnx` costs a real package. That measurement lands in the
project vault first and then here, once it exists. Until then this section
states the platform ceilings and the bundle/download rule, not an estimate.

## Search scores: two different directions

`SearchAsync` (pure vector search) returns a **`vec0` distance** — lower is
better. `HybridSearchAsync` (vector + full-text, combined by reciprocal rank
fusion) returns an **RRF score** — higher is better. `Microsoft.Extensions.VectorData`
has no attribute or convention for declaring which direction a given `Score`
runs, so this is the one thing a caller has to just know rather than
discover from the type system.

## Semantic Kernel caveat

Semantic Kernel's `AsTextEmbeddingGenerationService()` adapter, when wrapping
a plain `Microsoft.Extensions.AI` generator, populates only `EndpointKey` and
`ModelIdKey` from `EmbeddingGeneratorMetadata` — it never sets `DimensionsKey`.
That means `GetDimensions()` on the SK-wrapped service returns `null` for a
Qavren generator, regardless of what `DefaultModelDimensions` reports on the
underlying `IEmbeddingGenerator`. This is upstream SK behaviour, not a gap in
Qavren's generator — recorded here so it is not mistakenly filed as a
Qavren.Edge bug.

## No `CompactAsync` in v1

`vec0` reclaims storage only when an entire chunk empties (roughly 256 rows
at the default chunk size), so a collection with heavy churn (frequent
updates and deletes) grows over time even though most rows are live. There is
no `CompactAsync` API in this version. The remedy is to drop the collection
and rebuild it from source data when reclaiming that space matters.
