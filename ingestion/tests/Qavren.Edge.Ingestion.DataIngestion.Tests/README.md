# Qavren.Edge.Ingestion.DataIngestion.Tests

The MEDI conformance suite (spec 14.4, plan task 6.3). `ConformanceTests` builds a **real**
`Microsoft.Extensions.DataIngestion.IngestionPipeline<string>` over `EdgeChunkerMediAdapter` and
`EdgeVectorStoreMediWriter`, runs it against SP1's native SQLite (vec0 + FTS5) and SP2's store, and
asserts the rows land in SP3's collection and come back through SP2's `SearchAsync`. That is the
only proof the ecosystem claim is true rather than aspirational.

## What this project restores, and why

The **shipped** `Qavren.Edge.Ingestion.DataIngestion` package references
`Microsoft.Extensions.DataIngestion.Abstractions` only, which has zero dependencies on net8.0+.
`IngestionPipeline<T>` lives in the **implementation** package, `Microsoft.Extensions.DataIngestion`,
so this test project — and only this test project — restores it, together with its transitive
graph. None of that reaches a consumer's app.

## The reader, and the package this project must never reference

MEDI's own Markdown reader ships in `Microsoft.Extensions.DataIngestion.Markdig`, which depends on
`Markdig.Signed`. The core, `Qavren.Edge.Ingestion`, depends on `Markdig` 1.3.2. Those are two
package ids that emit the **same assembly name**, `Markdig.dll`. Two ids producing one assembly name
in one build graph is `MSB3277`, which `TreatWarningsAsErrors` turns into a build failure — and the
alternatives (a silent last-writer-wins copy, or a binding mismatch at runtime) are worse than the
failure.

So this project does **not** reference `Microsoft.Extensions.DataIngestion.Markdig`. `IngestionDocumentReader`
has one abstract member, `ReadAsync(Stream, string, string, CancellationToken)`, and
`MarkdownExtractorReader` in `TestFakes.cs` implements it in fifteen lines over SP3's own
`MarkdownExtractor`, feeding the pipeline through `EdgeDocumentConverter.ToMedi`. That exercises
everything the claim needs — MEDI's pipeline type, MEDI's chunker contract, MEDI's writer contract,
and the converter in both directions — while keeping `Markdig.Signed` out of every graph in this
repository.

**If a future test genuinely needs MEDI's own Markdig reader, it cannot live in a project that also
references `Qavren.Edge.Ingestion`.** It would need a separate test project that references only
`Qavren.Edge.Ingestion.DataIngestion` (which brings the Abstractions, not the core's Markdig) plus
the MEDI Markdig package — a new project, not a package addition to this one. No CI assertion guards
against the absent package; the repo-wide `MSB3277`-as-error setting is the guard, and the task's
verify step reads the build output for it explicitly.

## Running

```powershell
dotnet run --project ingestion\tests\Qavren.Edge.Ingestion.DataIngestion.Tests\Qavren.Edge.Ingestion.DataIngestion.Tests.csproj -c Release -p:TargetFrameworks=net10.0
```

`net10.0` alone; host-only, never on a device lane.
