# Qavren.Edge.Core

The hosting core of the Qavren.Edge suite (sub-project 1): `AddQavrenEdge()`, the startup host
that runs every registered `IEdgeStartupTask` once and records a single startup fault, the
lifecycle hub, app paths, diagnostics and the `EdgeErrorCode` catalogue. Plain
`Microsoft.Extensions.*` wiring, no framework. Every other Qavren.Edge package registers into
the builder this package defines.

```
dotnet add package Qavren.Edge.Core
```

```csharp
services.AddQavrenEdge(edge =>
{
    // providers register here: edge.AddSqlite(...), .AddOnnxEmbeddings(...), .AddOnnxChat(...)
});
```

You rarely reference this package alone: `Qavren.Edge` (SQLite + native library) or
`Qavren.Edge.Maui` (MAUI lifecycle and paths) pull it in. Error codes 1001-4999 are documented in
[foundation/docs/errors.md](https://github.com/qavren-oss/qavren-edge/blob/main/foundation/docs/errors.md).

Sub-project README: [foundation/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/foundation/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
