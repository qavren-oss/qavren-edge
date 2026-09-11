#!/usr/bin/env python3
"""Regenerate the tier-1 ONNX test fixtures for Qavren.Edge.Embeddings.

NEVER a build step. Run by hand, then commit the emitted TinyModels.g.cs.

    uv venv --python 3.14 .venv
    uv pip install --python .venv/Scripts/python.exe -r requirements.txt
    .venv/Scripts/python.exe make_tiny_model.py --out TinyModels.g.cs

Two pins are non-negotiable:
  * model.ir_version = 10 -- onnx 1.22's IR_VERSION is 13, which is exactly ORT 1.30.0's
    ceiling; the first onnx release that raises it would silently break every fixture that
    trusted the default.
  * model.producer_name = "" -- keeps the bytes stable across onnx upgrades.

Two per-operator traps this script encodes:
  * ReduceSum takes axes as a second INPUT from opset 13.
  * Unsqueeze takes axes as a second INPUT from opset 13.
  (ReduceMean, by contrast, takes axes as an ATTRIBUTE through opset 17 and as an input from
  18 -- both conventions live at opset 17, so nothing here relies on a default.)
"""

from __future__ import annotations

import argparse
import base64
import pathlib

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper

OPSET = 17
IR_VERSION = 10
VOCAB = 16
DIM = 4


def embedding_table() -> np.ndarray:
    """Row t = [t, t+0.5, t+0.25, t+0.75]. Hand-computable on purpose."""
    rows = [[float(t), t + 0.5, t + 0.25, t + 0.75] for t in range(VOCAB)]
    return np.array(rows, dtype=np.float32)


def _seq_input(name: str):
    return helper.make_tensor_value_info(
        name, TensorProto.INT64, ["batch_size", "sequence_length"])


def _hidden_nodes():
    return [helper.make_node(
        "Gather", ["emb", "input_ids"], ["last_hidden_state"], axis=0)], []


def _mean_nodes():
    axes1 = numpy_helper.from_array(np.array([1], dtype=np.int64), "axes1")
    axes2 = numpy_helper.from_array(np.array([2], dtype=np.int64), "axes2")
    nodes = [
        helper.make_node("Gather", ["emb", "input_ids"], ["h"], axis=0),
        helper.make_node("Cast", ["attention_mask"], ["maskf"], to=TensorProto.FLOAT),
        helper.make_node("Unsqueeze", ["maskf", "axes2"], ["mask3"]),
        helper.make_node("Mul", ["h", "mask3"], ["masked"]),
        helper.make_node("ReduceSum", ["masked", "axes1"], ["total"], keepdims=0),
        helper.make_node("ReduceSum", ["mask3", "axes1"], ["denom"], keepdims=0),
        helper.make_node("Div", ["total", "denom"], ["embedding"]),
    ]
    return nodes, [axes1, axes2]


def _cls_nodes():
    zero = numpy_helper.from_array(np.array(0, dtype=np.int64), "zero")
    nodes = [
        helper.make_node("Gather", ["emb", "input_ids"], ["h"], axis=0),
        helper.make_node("Gather", ["h", "zero"], ["embedding"], axis=1),
    ]
    return nodes, [zero]


_BUILDERS = {"hidden": _hidden_nodes, "mean": _mean_nodes, "cls": _cls_nodes}


def build(kind: str, inputs: list[str], ir_version: int = IR_VERSION) -> bytes:
    emb = numpy_helper.from_array(embedding_table(), "emb")
    nodes, extra = _BUILDERS[kind]()
    output = (
        helper.make_tensor_value_info(
            "last_hidden_state", TensorProto.FLOAT,
            ["batch_size", "sequence_length", "dims"])
        if kind == "hidden"
        else helper.make_tensor_value_info(
            "embedding", TensorProto.FLOAT, ["batch_size", "dims"])
    )
    graph = helper.make_graph(
        nodes, "tiny", [_seq_input(n) for n in inputs], [output], [emb, *extra])
    model = helper.make_model(
        graph, opset_imports=[helper.make_operatorsetid("", OPSET)])
    model.ir_version = ir_version
    model.producer_name = ""
    if ir_version <= onnx.IR_VERSION:
        # onnx 1.22.0's checker refuses an ir_version above its own (13). The IR-14 fixture
        # exists precisely to be refused by ORT, so it skips the checker rather than letting
        # the checker decide it cannot be built.
        onnx.checker.check_model(model)
    return model.SerializeToString()


VOCAB_TOKENS = (
    ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]"]
    + [chr(c) for c in range(ord("a"), ord("z") + 1)]
    + [str(d) for d in range(10)]
    + ["roof", "leak", "water", "damage", "note", "search", "query", "document",
       "the", "a", "of", "and", "to", "in", "is", "it", "##s", "##ing", "##ed",
       "##er", "##ly", "##tion", "edge"]
)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    fixtures = {
        "HiddenStates": build("hidden", ["input_ids", "attention_mask"]),
        "MeanPool": build("mean", ["input_ids", "attention_mask"]),
        "ClsPool": build("cls", ["input_ids", "attention_mask"]),
        "UnusedTokenTypeIds": build(
            "hidden", ["input_ids", "attention_mask", "token_type_ids"]),
        "NomicInputOrder": build(
            "hidden", ["input_ids", "token_type_ids", "attention_mask"]),
        "IrVersion14": build("hidden", ["input_ids", "attention_mask"], ir_version=14),
    }
    vocab = ("\n".join(VOCAB_TOKENS) + "\n").encode("utf-8")

    lines = [
        "// <auto-generated>",
        "// Regenerate with embeddings/tests/fixtures/make_tiny_model.py. NEVER a build step.",
        "// </auto-generated>",
        "",
        "namespace Qavren.Edge.Tests.Fixtures;",
        "",
        "/// <summary>Tier-1 ONNX fixtures, base64 so git holds no binary.</summary>",
        "internal static class TinyModels",
        "{",
    ]
    for name, payload in fixtures.items():
        lines.append(f"    /// <summary>{len(payload)} bytes.</summary>")
        lines.append(f"    public const string {name} =")
        lines.append(f'        "{base64.b64encode(payload).decode()}";')
        lines.append("")
    lines.append(
        f"    /// <summary>{len(VOCAB_TOKENS)}-entry vocab.txt, {len(vocab)} bytes.</summary>")
    lines.append("    public const string VocabTxt =")
    lines.append(f'        "{base64.b64encode(vocab).decode()}";')
    lines.append("}")

    out = pathlib.Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    # newline="\n" is load-bearing, not cosmetic. The default translates "\n" to os.linesep,
    # so on Windows this file would be written CRLF while .gitattributes stores it LF -- a
    # phantom diff on every regeneration, and the size drift-check's "(?m)...=[ \t]*$" anchor
    # does not match before a "\r".
    out.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    for name, payload in fixtures.items():
        print(f"{name}: {len(payload)} bytes, "
              f"{len(base64.b64encode(payload))} base64 chars")
    print(f"vocab.txt: {len(vocab)} bytes, {len(base64.b64encode(vocab))} base64 chars")


if __name__ == "__main__":
    main()
