using System.Runtime.CompilerServices;

// OnnxEmbeddingOptions.ApplyPinning is internal on purpose: a consumer reaches it through
// AddOnnxEmbeddings, never directly, and a setter that mutates three other properties would be
// invisible to a reader of the options object. Spec 16.1's L1 half of the session-factory
// assertion - PinnedSequenceLengthTests - has to call it, so the test assembly sees internals.
[assembly: InternalsVisibleTo("Qavren.Edge.Embeddings.Tests")]
