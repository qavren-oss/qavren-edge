using System.Runtime.CompilerServices;

// SP2's idiom: the binding that resolves the preset and the pre-validate startup task are internal
// and are tested directly rather than only through a started host, which would force provisioning.
[assembly: InternalsVisibleTo("Qavren.Edge.Ingestion.Onnx.Tests")]
