# 7. Accept the `MEVD9001` experimental surface in `Qavren.Edge.VectorData`

Date: 2026-09-11

## Status

Accepted

## Context

`Qavren.Edge.VectorData` is a clean-room `Microsoft.Extensions.VectorData`
provider — it implements the collection model builder, the LINQ→SQL filter
translator, and the SQL emitter that a real MEVD provider needs. Every one of
those building blocks — `CollectionModelBuilder`, `CollectionModelBuildingOptions`,
`ReservedKeyStorageName`, `EmbeddingGenerationDispatcher(s)`,
`FilterTranslatorBase`, `FilterPreprocessingOptions.SupportsParameterization`,
and the rest of the provider-services surface spot-checked in Environment
ground truth — lives in `Microsoft.Extensions.VectorData.ProviderServices`,
and every type in that namespace carries
`[Experimental("MEVD9001")]`. There is no non-experimental way to write a
provider against MEVD 10.10.0; the experimental surface is not an
implementation detail this package chose, it is the only surface a provider
author is given.

## Decision

`Qavren.Edge.VectorData.csproj` carries
`<NoWarn>$(NoWarn);MEVD9001</NoWarn>` so the package compiles without every
call site needing its own `#pragma warning disable`. The
`Microsoft.Extensions.VectorData.ConformanceTests` suite (also pinned at
10.10.0) is the package's primary defense against this surface moving under
it.

## Consequences

A minor version bump of `Microsoft.Extensions.VectorData*` can silently
change or remove members this package depends on without a compiler warning
raising it first — `MEVD9001` is suppressed fleet-wide in this project, so a
breaking rename inside the experimental surface shows up only as a build
error (if the member is gone) or a conformance-suite failure (if the
behaviour changed), never as a warning ahead of either. Any bump to these
packages must run the full conformance suite before being accepted, not just
`dotnet build`.
