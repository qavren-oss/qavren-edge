# 11. Mirror MEDI's shape rather than depend on it

Date: 2026-09-11

## Status

Accepted

## Context

`Microsoft.Extensions.DataIngestion` (MEDI) is the natural upstream analogue
of this package, but it is not stable enough to build on directly: fourteen
prereleases in eleven months, `<Stage>preview</Stage>` in its own metadata,
abstractions that are abstract classes rather than interfaces, chunks with no
ordinal, offset, content hash or token count, `Guid.NewGuid()`-generated
chunk keys with no content addressing (see ADR 0010), and a
delete-and-replace "incremental" mode that re-embeds every chunk of a
document whose content never changed.

Depending on it directly would import all of that instability — and its own
transitive closure, measured at twelve packages, not the eight the spec
assumed — into this suite's stable core.

## Decision

Match MEDI's verbs (`IngestionPipeline`-shaped calls, the same conceptual
stages) without depending on its implementation. The MEDI **shim**
(`Qavren.Edge.Ingestion.DataIngestion`) ships as its own separate prerelease
package, referencing only the zero-dependency
`Microsoft.Extensions.DataIngestion.Abstractions`. The MEDI **implementation**
package, `Microsoft.Extensions.DataIngestion`, is referenced by exactly one
place in the whole repository: the conformance test project, which needs the
concrete `IngestionPipeline<T>` type the shim does not ship.

## Consequences

The "we are ecosystem-compatible" claim is tested — the conformance test
exercises the shim against MEDI's own abstractions — rather than aspirational
prose. `Markdig.Signed` (MEDI's own signed fork of Markdig, pulled in only by
`Microsoft.Extensions.DataIngestion.Markdig`) never enters any dependency
graph in this repository, because that package is never referenced; two
package ids shipping one assembly name in the same graph would otherwise be
an `MSB3277` conflict that `TreatWarningsAsErrors` turns into a build
failure.

If a future test genuinely needs MEDI's own Markdig reader, that need is met
by a **new test project** that does not reference `Qavren.Edge.Ingestion` —
not by adding the implementation package to an existing one. This decision
is revisited when MEDI ships a stable release; until then the core is never
blocked on MEDI's release cadence.
