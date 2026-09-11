using System.Runtime.CompilerServices;

// The execution-provider resolver, the session-options factory and the OrtEnv startup task are
// internal on purpose - a consumer configures all three through OnnxSessionOptions and OnnxOptions
// and never touches ORT's SessionOptions directly. Their tests still have to reach them, and
// spec 16.1's assertions are about exactly those internals.
[assembly: InternalsVisibleTo("Qavren.Edge.Onnx.Tests")]
