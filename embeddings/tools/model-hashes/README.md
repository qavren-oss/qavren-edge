# Preset model hashes

Regenerates `embeddings/src/Qavren.Edge.Embeddings.Onnx/EmbeddingPresets.g.cs` — the pinned
Hugging Face manifests (repo, revision, per-file size and SHA-256, SPDX licence) behind the four
`EmbeddingPresets`.

```powershell
$uv = "C:\Users\steve\.local\bin\uv.exe"
& $uv venv --python 3.14 .venv
& $uv pip install --python .venv\Scripts\python.exe -r requirements.txt
& .venv\Scripts\python.exe fetch_preset_hashes.py --out ..\..\src\Qavren.Edge.Embeddings.Onnx\EmbeddingPresets.g.cs
```

**This script is never a build step and CI never runs it.** It is run by hand, and its output —
`EmbeddingPresets.g.cs` — is committed. The runtime reads the committed constants and never asks
the Hub what a hash should be. The `.venv/` is gitignored; the generated `.cs` is not.

**A revision is a full commit SHA, never `main`.** A moving revision silently changes the vectors
an app produces, and nothing in the type system or the test suite would notice — the digests would
simply be regenerated against different weights the next time somebody ran this script. Re-run it
before a release and diff: a changed digest is a deliberate decision about vectors, never a merge
conflict to resolve.
