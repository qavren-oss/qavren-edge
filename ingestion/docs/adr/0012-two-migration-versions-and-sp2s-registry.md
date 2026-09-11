# 12. Two migration versions, and what SP2's registry buys

Date: 2026-09-11

## Status

Accepted

## Context

Verification item 1 asked this plan to weigh two candidate shapes for
`AddIngestion`'s migration against each other and record the reasoning, not
only the outcome — because a later reader who sees only "two versions" will
otherwise "fix" it back to the more obvious-looking single migration.

Both candidate shapes were checked against source and **both resolve**:

- `EdgeCollectionModelBuilder` is public, and `BuildDynamic(definition,
  generator)` is the exact call SP2's own migration path makes — so SP3 can
  build a `CollectionModel` and get collection DDL without reflection.
- A runtime collection's `EdgeVectorSchema` is reachable through
  `collection.GetService(typeof(EdgeVectorSchema))` with no connection opened
  — so SP3 can compare its migration's DDL against the running store's DDL at
  startup.

With both halves resolving, the choice between "one migration that emits
both the collection DDL and the state DDL" and "two migration versions, one
per concern" is a free one, decided on what each buys rather than on which
one is merely possible.

## Decision

`AddIngestion(int migrationVersion, …)` claims **two consecutive** migration
versions: `migrationVersion` (`N`) for the collection, routed through SP2's
own `AddVectorCollectionMigration` definition overload, and `migrationVersion
+ 1` (`N + 1`) for SP3's three state tables, routed through SP1's
`AddMigrations`. The method's signature is unchanged from the one-version
design; only its XML doc, stating plainly that it claims both `N` and `N +
1`, is new.

The two-version shape wins on four counts:

1. It places SP3's collection inside `EdgeVectorCollectionRegistry` — the
   **only** registry SP2's lifecycle observer and diagnostics contributor
   read. SP2 therefore merges SP3's own FTS5 sidecar and reports its tables
   for free. In exchange, SP3's own merge — `FtsMergeBudget`,
   `FtsMergePages`, `FtsMergeMaxIterations`, event 926, and the whole
   `SleepGraceBudget + FtsMergeBudget <= 3 s` sum validation the spec's §12
   describes — is deleted outright: one fewer option surface, one fewer
   event id, on an observer that runs on a platform callback thread where
   every millisecond is scrutinised.
2. `IngestionSchemaOnlyGenerator` (the twelve-line stand-in SP3 needs for the
   model build) never enters the shipped registration path — it exists only
   for the one-off model build, not as a persistent registration.
3. The core names no `Microsoft.Extensions.VectorData.ProviderServices` type
   on the registration path, so `Qavren.Edge.Ingestion` needs no `MEVD9001`
   suppression and carries none of ADR 0007's (SP2's) churn exposure.
4. Version bookkeeping gets **stricter**, not looser: a version clash now
   surfaces as SP1's `MigrationVersionConflict` (3002), the same diagnostic
   every other migration in the suite produces, rather than a bespoke check.

## Consequences

The cost is exactly the one-migration design's headline claim — `AddIngestion`
no longer claims a single version, and a consumer who only skims the method
name might reasonably expect one. The XML doc on `AddIngestion` states the
two-version behaviour explicitly to close that gap.
`IngestionMigrationVersionConflict` (6007) keeps its original meaning
unchanged: a second `AddIngestion` call for the same collection, regardless
of how many versions the first call claimed underneath.
