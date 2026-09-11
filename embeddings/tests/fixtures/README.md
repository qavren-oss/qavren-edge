# Tier-1 ONNX test fixtures

Six tiny ONNX graphs and a 64-entry `vocab.txt`, generated once by hand and committed as
base64 `const string`s in `TinyModels.g.cs`. Tier 1 downloads nothing and ships no binary
into git (design §16.1).

## Regeneration

Three lines, from this directory. `uv` is spelled absolutely because
`C:\Users\steve\.local\bin` is on the interactive `PATH` but not guaranteed on anyone else's.

```powershell
& "C:\Users\steve\.local\bin\uv.exe" venv --python 3.14 .venv
& "C:\Users\steve\.local\bin\uv.exe" pip install --python .venv\Scripts\python.exe -r requirements.txt
& .venv\Scripts\python.exe make_tiny_model.py --out TinyModels.g.cs
```

The ambient `C:\Python314` interpreter carries onnxruntime 1.27.0 — the wrong runtime. The
pinned venv exists for exactly that reason; never run the script against the ambient
interpreter.

## What is committed and what is not

| Path | Committed? |
| --- | --- |
| `make_tiny_model.py`, `requirements.txt`, this README | yes |
| `TinyModels.g.cs` (generated) | **yes** — it is the fixture |
| `.venv/`, `_scratch/` (any `.onnx` written for debugging) | no, gitignored |

`TinyModels.g.cs` is **linked**, never copied, into every project that consumes it:
`<Compile Include="..\fixtures\TinyModels.g.cs" Link="Fixtures\TinyModels.g.cs" />`. One
task owns the file; no project holds a copy.

**`make_tiny_model.py` is never a build step and CI never runs it.** It is documentation and
a regeneration path. That is why the byte counts below are asserted rather than assumed: an
onnx, protobuf or numpy upgrade that moves the serialised bytes is otherwise invisible until
somebody reruns the script.

## Measured sizes (2026-09-11, onnx 1.22.0 / numpy 2.5.3 / protobuf 7.36.1)

| Const | Bytes | Base64 chars | What it is for |
| --- | ---: | ---: | --- |
| `HiddenStates` | 533 | 712 | single `Gather`, `last_hidden_state[batch, seq, dims]` — the shape every real preset has, and the only one that puts `EmbeddingPooler` in the path |
| `MeanPool` | 772 | 1032 | in-graph masked mean (`Gather → Cast → Unsqueeze → Mul → ReduceSum/ReduceSum → Div`) — proves the mask/tensor plumbing against real ORT with no Qavren code involved |
| `ClsPool` | 553 | 740 | in-graph CLS pool, `embedding[batch, dims]` |
| `UnusedTokenTypeIds` | 592 | 792 | declares `token_type_ids` and never consumes it; ORT still demands it in the feed, which proves the generator feeds every **declared** input |
| `NomicInputOrder` | 592 | 792 | inputs declared in nomic's order; same values only if binding is by name |
| `IrVersion14` | 533 | 712 | error path: ORT rejects it with `Unsupported model IR version: 14, max supported IR version: 13` |
| `VocabTxt` | 215 | 288 | 64-entry `vocab.txt` |

The embedding table is hand-computable: row *t* = `[t, t+0.5, t+0.25, t+0.75]`, vocab 16 × dim 4.
With `input_ids = [[3, 1, 9]]` and `attention_mask = [[1, 1, 0]]`, ORT returns:

- `HiddenStates` → `[[[3, 3.5, 3.25, 3.75], [1, 1.5, 1.25, 1.75], [9, 9.5, 9.25, 9.75]]]`
- `MeanPool` → `[[2.0, 2.5, 2.25, 2.75]]` (rows 3 and 1 averaged, row 9 masked out)
- `ClsPool` → `[[3.0, 3.5, 3.25, 3.75]]`

## Pins that are not negotiable

- `model.ir_version = 10`, opset 17, both set **explicitly**. onnx 1.22's `IR_VERSION` is 13,
  which is exactly ORT 1.30.0's ceiling — the first onnx release that raises it would silently
  break every fixture that trusted the default.
- `model.producer_name = ""` keeps the bytes stable across onnx upgrades.
- `onnxruntime` is pinned to the .NET pin (1.30.0) so a fixture that loads here loads there.
- `numpy` is pinned because it serialises the `Gather` initializer whose byte count is asserted.
- The IR-14 fixture **skips** `onnx.checker.check_model`: onnx 1.22.0's checker refuses an
  `ir_version` above its own (13). Being refused is the fixture's whole job — by ORT, not by
  the builder.
- `ReduceSum` and `Unsqueeze` take axes as a second **input** from opset 13, while `ReduceMean`
  takes axes as an **attribute** through opset 17. Both conventions live at opset 17, so the
  script never relies on a default.
