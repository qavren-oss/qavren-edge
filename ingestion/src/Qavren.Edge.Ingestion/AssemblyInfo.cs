using System.Runtime.CompilerServices;

// The test assembly, matching SP2's idiom: TokenIndexSearch is internal and is tested directly
// rather than only through a chunker.
[assembly: InternalsVisibleTo("Qavren.Edge.Ingestion.Tests")]

// Qavren.Edge.Ingestion.Onnx's EdgeChunkTokenizer delegates to Internal.TokenIndexSearch, which is
// the ONE implementation of IChunkTokenizer.IndexByTokenCount (plan adjustment 1, ADR 0013). The
// grant is issued HERE, in wave 2, and not in wave 6 where the satellite is written: a wave-6 edit
// to this file would be a write to Qavren.Edge.Ingestion while Tasks 6.1 and 6.3 compile it, and
// -p:ArtifactsPath separates outputs, not sources. The core is frozen from the wave-5 close on.
[assembly: InternalsVisibleTo("Qavren.Edge.Ingestion.Onnx")]
