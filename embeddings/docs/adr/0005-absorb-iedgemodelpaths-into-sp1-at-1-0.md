# 5. `IEdgeModelPaths` lives in `Qavren.Edge.Onnx` for now, absorbed into SP1 at 1.0

Date: 2026-09-11

## Status

Accepted

## Context

Model bundles (23–137 MB) need a storage location that is neither
`IEdgePaths.Data` nor `IEdgePaths.Cache`. `IEdgePaths.Data` is backed up on
all three platforms; Android's Auto Backup quota is 25 MB per app, so a
single int8 model consumes essentially the whole quota and a fp32 one
triggers `onQuotaExceeded()`, which stops the user's real database from being
backed up at all. `IEdgePaths.Cache` can be purged by the OS mid-session,
turning a purge into an unplanned re-download during use.

`IEdgeModelPaths` is the interface that names the correct location instead —
durable, excluded from cloud backup, never OS-purged, created on first
access (`Context.NoBackupFilesDir` on Android, `Library/Application
Support/qavren-edge` with `NSURLIsExcludedFromBackupKey` set on Apple
platforms).

§5 of the spec is explicit that SP2 makes exactly two edits to SP1's merged
surface (`EdgeErrorCode`, `FtsTable`) and no others — `IEdgePaths` itself is
not one of them. Adding `IEdgeModelPaths` as a new member of `IEdgePaths`, or
as a new interface in `Qavren.Edge.Core`, would both count as touching SP1
outside that boundary.

## Decision

`IEdgeModelPaths` and its default implementation `DefaultEdgeModelPaths` are
declared in `Qavren.Edge.Onnx` (§6.2), constructed from an injected
`IEdgePaths`, for SP2's v1. At SP1's 1.0, `IEdgeModelPaths` moves to
`Qavren.Edge.Core` with a `[TypeForwardedTo]` left behind in
`Qavren.Edge.Onnx`, so no consumer needs to recompile against the moved type.

## Consequences

Until the 1.0 move happens, `Qavren.Edge.Onnx` is the only package that
declares model-root semantics, even though the concept ("a durable,
non-backed-up, non-purged directory") is not specific to embeddings and would
naturally belong beside `IEdgePaths` in the foundation package.

The type-forward at 1.0 is a source- and binary-compatible move: existing
callers referencing `Qavren.Edge.Onnx.IEdgeModelPaths` keep resolving after
the type physically moves to `Qavren.Edge.Core`. This ADR is the record that
the move is intentional and already decided, not a future open question.
