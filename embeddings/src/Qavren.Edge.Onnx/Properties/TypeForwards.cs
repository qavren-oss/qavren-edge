using System.Runtime.CompilerServices;

// ADR 0005 (embeddings/docs/adr/0005-absorb-iedgemodelpaths-into-sp1-at-1-0.md): at 1.0,
// IEdgeModelPaths physically moved to Qavren.Edge.Core. This forward keeps every existing
// `Qavren.Edge.Onnx.IEdgeModelPaths` reference resolving without a consumer recompile — source-
// and binary-compatible. The forwarded type's namespace and name must match the moved
// declaration exactly (foundation/src/Qavren.Edge.Core/IEdgeModelPaths.cs) or the forward fails.
[assembly: TypeForwardedTo(typeof(Qavren.Edge.Onnx.IEdgeModelPaths))]
