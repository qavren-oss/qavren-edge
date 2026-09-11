# Tier 3 — the real-model lane

- **`QAVREN_EDGE_MODEL_DIR` is what turns this tier on.** `ci.yml`'s nightly `model-tests` job sets
  it to the directory holding `onnx/model_qint8_arm64.onnx` and `vocab.txt`; a developer sets it by
  hand to opt in. Unset, `ModelAvailable.Yes` is false and all three facts skip — evaluated at
  runtime, so a skipped lane loads no model and downloads nothing.
- **Nightly, never on a PR.** The job runs on `schedule:` only and is not in `ci-gate`'s `needs`.
  Every other lane — three host legs and four device lanes — takes the skip path.
- **The cache key is the model's SHA-256**, `4278337fd0ff3c68bfb6291042cad8ab363e1d9fbc43dcb499fe91c871902474`
  for the graph and `07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3` for
  `vocab.txt` — not a URL and not a date, so a moved revision or a poisoned cache is a loud failure
  rather than a silent substitution. `Tier3Host` registers a `FileOnnxModelSource` over that staged
  directory and therefore never registers the Hugging Face source at all: these tests cannot
  download.
- **Regenerating `reference-vectors.json` is a PR of its own.** It is the only committed artifact
  here a reviewer cannot read, so a regeneration carries its reason in the PR body and never rides
  along with a change to the pooling, the tokenizer or a preset. `QAVREN_EDGE_WRITE_REFERENCE`
  refuses to overwrite an existing file — deleting it first is the deliberate act:

```powershell
Remove-Item "C:\Users\steve\projects\qavren-edge-sp2\embeddings\tests\Qavren.Edge.Embeddings.Tests\Tier3\reference-vectors.json"
$env:QAVREN_EDGE_MODEL_DIR = "C:\Users\steve\AppData\Local\Temp\qedge-model"
$env:QAVREN_EDGE_WRITE_REFERENCE = "1"
dotnet run --project "C:\Users\steve\projects\qavren-edge-sp2\embeddings\tests\Qavren.Edge.Embeddings.Tests\Qavren.Edge.Embeddings.Tests.csproj" `
  -c Release -f net10.0 -p:TargetFrameworks=net10.0 `
  -p:ArtifactsPath="D:\Local\Temp\qedge-sp2\w6\t62"
```

Stage the model into `QAVREN_EDGE_MODEL_DIR` first with the same two `curl` fetches and the same
`sha256sum -c` check `model-tests` runs, against HF revision
`1110a243fdf4706b3f48f1d95db1a4f5529b4d41`.
