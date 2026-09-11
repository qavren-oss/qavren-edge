using System.Runtime.CompilerServices;

// The execution-provider resolver, the session-options factory and the OrtEnv startup task are
// internal on purpose - a consumer configures all three through OnnxSessionOptions and OnnxOptions
// and never touches ORT's SessionOptions directly. Their tests still have to reach them, and
// spec 16.1's assertions are about exactly those internals.
[assembly: InternalsVisibleTo("Qavren.Edge.Onnx.Tests")]

// The tier-1 fixtures are 533-831-byte base64 graphs created with new InferenceSession(byte[]).
// That constructor is deliberately absent from IOnnxSessionHost (spec 9.1) so no consumer can take
// the path that doubles transient memory and weakens the CoreML cache key; both costs are measured
// against model size and vanish at this size. The hook is internal and stays internal.
[assembly: InternalsVisibleTo("Qavren.Edge.Embeddings.Tests")]
