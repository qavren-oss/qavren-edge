#!/usr/bin/env python3
"""Generate chat/tests/fixtures/TinyChatModel.g.cs - the tier-2 GenAI model fixture.

Run BY HAND, never from a build and never from CI:

    C:\\Users\\steve\\projects\\qavren-edge-sp4\\chat\\tests\\fixtures\\.venv\\Scripts\\python.exe \\
        make_tiny_chat_model.py --out TinyChatModel.g.cs --scratch _scratch

Spec 16.2. A "tokenizer-only fixture" is NOT possible: at v0.15.2 Tokenizer has exactly one
constructor, Tokenizer(Model), and OgaCreateTokenizerFromPath exists only on unreleased main.
Anything that touches real ORT GenAI needs a real model DIRECTORY, so this builds one and the
emitted .cs carries it as base64 - nothing binary is ever committed.

Head size 16 is not arbitrary: on the fp32 CPU path the builder emits GroupQueryAttention with
fused RoPE, and ORT 1.30's group_query_attention_helper.h enforces head_size % 8 == 0 and, when
rotary cos/sin caches are present, head_size % 16 == 0. hidden_size 64 / 4 heads = 16.
"""

import argparse
import base64
import json
import pathlib
import subprocess
import sys

SEED = 20260911
SOURCE_CAP_BYTES = 1024 * 1024          # spec 16.2's 1 MB cap on the generated source
VOCAB_SIZE = 256
TRANSFORMERS_PIN_NOTE = "transformers==5.17.0"   # amend if the 4.57.1 contingency was taken

# --- the model config, hand-authored ----------------------------------------------------------

MODEL_CONFIG = {
    "architectures": ["LlamaForCausalLM"],
    "model_type": "llama",
    "vocab_size": VOCAB_SIZE,
    "hidden_size": 64,
    "intermediate_size": 128,
    "num_hidden_layers": 2,
    "num_attention_heads": 4,           # -> head_size 16
    "num_key_value_heads": 2,
    "max_position_embeddings": 512,
    "rms_norm_eps": 1e-5,
    "rope_theta": 10000.0,
    "tie_word_embeddings": True,
    "torch_dtype": "float32",
    "use_cache": True,
    "bos_token_id": 1,
    "eos_token_id": 2,
    "pad_token_id": 0,
}

# --- the tokenizer ------------------------------------------------------------------------------

def bytes_to_unicode():
    """GPT-2's byte-level alphabet, verbatim in behaviour: a reversible byte -> printable-char map.

    Printable ASCII, Latin-1 letters and the Latin-1 supplement map to themselves; every other byte
    maps to U+0100 + n for a running n. This is the same table tokenizers' ByteLevel components use,
    so a vocabulary built from it is one they can round-trip.
    """
    bs = (list(range(ord("!"), ord("~") + 1))
          + list(range(ord("\u00a1"), ord("\u00ac") + 1))
          + list(range(ord("\u00ae"), ord("\u00ff") + 1)))
    cs = bs[:]
    n = 0
    for b in range(256):
        if b not in bs:
            bs.append(b)
            cs.append(256 + n)
            n += 1
    return {b: chr(c) for b, c in zip(bs, cs)}


def build_tokenizer_json():
    """A 256-entry byte-level BPE with NO merges, ordered so that token id == byte value.

    Ordering matters and is the whole reason this is literal. config.json names pad/bos/eos as ids
    0/1/2; with id == byte value those are the byte-level characters for 0x00, 0x01 and 0x02, which
    ordinary text never encodes to. On a merged or differently-ordered vocabulary those three ids
    would be real text tokens and the fixture would terminate mid-word.
    """
    b2u = bytes_to_unicode()
    vocab = {b2u[b]: b for b in range(256)}
    assert len(vocab) == VOCAB_SIZE, "the byte-level alphabet must be exactly 256 distinct chars"
    return {
        "version": "1.0",
        "truncation": None,
        "padding": None,
        "added_tokens": [],
        "normalizer": None,
        "pre_tokenizer": {
            "type": "ByteLevel",
            "add_prefix_space": False,
            "trim_offsets": True,
            "use_regex": True,
        },
        "post_processor": {
            "type": "ByteLevel",
            "add_prefix_space": True,
            "trim_offsets": False,
            "use_regex": True,
        },
        "decoder": {
            "type": "ByteLevel",
            "add_prefix_space": True,
            "trim_offsets": True,
            "use_regex": True,
        },
        "model": {
            "type": "BPE",
            "dropout": None,
            "unk_token": None,
            "continuing_subword_prefix": None,
            "end_of_word_suffix": None,
            "fuse_unk": False,
            "byte_fallback": False,
            "ignore_merges": True,
            "vocab": vocab,
            "merges": [],
        },
    }


# Three Jinja constructs and nothing else: a for, a dict index, an if. Deliberately minimal - the
# fixture tests MECHANICS, and whether minja parses a REAL preset's template is spec 19 item 6, a
# tier-3 question. A fixture template minja choked on would fail tier 2 for an unrelated reason.
CHAT_TEMPLATE = (
    "{% for message in messages %}"
    "<|{{ message['role'] }}|>\n{{ message['content'] }}\n"
    "{% endfor %}"
    "{% if add_generation_prompt %}<|assistant|>\n{% endif %}"
)


def build_tokenizer_config():
    b2u = bytes_to_unicode()
    return {
        "tokenizer_class": "PreTrainedTokenizerFast",
        "model_max_length": 512,
        "clean_up_tokenization_spaces": False,
        "pad_token": b2u[0],
        "bos_token": b2u[1],
        "eos_token": b2u[2],
        "chat_template": CHAT_TEMPLATE,
    }


# --- the pipeline -------------------------------------------------------------------------------

def write_source_model(scratch: pathlib.Path) -> None:
    import torch
    from transformers import LlamaConfig, LlamaForCausalLM

    scratch.mkdir(parents=True, exist_ok=True)
    (scratch / "config.json").write_text(
        json.dumps(MODEL_CONFIG, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")
    (scratch / "tokenizer.json").write_text(
        json.dumps(build_tokenizer_json(), indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8", newline="\n")
    (scratch / "tokenizer_config.json").write_text(
        json.dumps(build_tokenizer_config(), indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8", newline="\n")

    torch.manual_seed(SEED)
    model = LlamaForCausalLM(LlamaConfig(**MODEL_CONFIG))
    model.eval()
    model.save_pretrained(str(scratch), safe_serialization=True)


def run_builder(scratch: pathlib.Path, out: pathlib.Path) -> None:
    out.mkdir(parents=True, exist_ok=True)
    subprocess.run(
        [sys.executable, "-m", "onnxruntime_genai.models.builder",
         "-m", str(scratch), "-o", str(out), "-p", "fp32", "-e", "cpu"],
        check=True)


def assert_shape(out: pathlib.Path) -> None:
    """If the builder rewrote any of these, every budget assertion downstream is against the wrong
    shape - and it would fail as an arithmetic mismatch three waves later, not here."""
    cfg = json.loads((out / "genai_config.json").read_text(encoding="utf-8"))
    model, decoder = cfg["model"], cfg["model"]["decoder"]
    expected = {
        "context_length": (model["context_length"], 512),
        "vocab_size": (model["vocab_size"], VOCAB_SIZE),
        "head_size": (decoder["head_size"], 16),
        "num_hidden_layers": (decoder["num_hidden_layers"], 2),
        "num_key_value_heads": (decoder["num_key_value_heads"], 2),
    }
    bad = {k: v for k, (v, want) in expected.items() if v != want}
    if bad:
        raise SystemExit(f"the builder rewrote the shape: {bad}; expected "
                         f"{ {k: w for k, (_, w) in expected.items()} }")


# Six files, not the four the plan's draft listed, and both extras are measured rather than
# assumed - a directory missing either one is not loadable:
#
#   model.onnx.data   ORT GenAI 0.15.2's builder calls ir.save(..., size_threshold_bytes=0), so
#                     EVERY initializer goes to external data unconditionally and there is no flag
#                     to turn that off. model.onnx is a 14 KB graph; the 385 KB of weights live
#                     here. Materialising without it raises, verbatim: "External data path
#                     validation failed for initializer: model.layers.0.input_layernorm.weight".
#   chat_template.jinja  transformers 5.x no longer writes `chat_template` into
#                     tokenizer_config.json - the builder's save_processing round-trips the
#                     tokenizer through transformers, which splits the template out into its own
#                     file. Materialising without it loads fine but Tokenizer.ApplyChatTemplate
#                     raises "Empty chat template", which would fail tier 2 for a reason that has
#                     nothing to do with what tier 2 tests.
#
# The four names the plan named are all still here and still carry their Length consts; these two
# are additions, not substitutions.
FILES = [
    ("GenAiConfigJson", "genai_config.json"),
    ("ModelOnnx", "model.onnx"),
    ("ModelOnnxData", "model.onnx.data"),
    ("TokenizerJson", "tokenizer.json"),
    ("TokenizerConfigJson", "tokenizer_config.json"),
    ("ChatTemplateJinja", "chat_template.jinja"),
]

# The builder's save_processing round-trips the tokenizer through transformers, which writes
# genai_config.json, tokenizer.json, tokenizer_config.json and chat_template.jinja with a plain
# text-mode open() and no newline=, so on Windows every LF in them lands on disk as CRLF and on
# Linux it does not. That made the committed base64 a function of the generator's OS: a
# Windows-generated chat_template.jinja renders "<|user|>\r\nping\r\n" through minja, which is
# what the tier-2 template assertions (LF, as CHAT_TEMPLATE above is written) caught on the
# ubuntu and macOS legs while Windows-local runs of the same fixture agreed with themselves.
# Normalise the TEXT files - and only the text files - to LF before base64, so the fixture is
# the same bytes whoever regenerates it. model.onnx and model.onnx.data are binary protobuf and
# tensor data and must never be touched: a CRLF-looking byte pair in them is data.
TEXT_FILES = frozenset({
    "genai_config.json",
    "tokenizer.json",
    "tokenizer_config.json",
    "chat_template.jinja",
})


def emit(out_dir: pathlib.Path, cs_path: pathlib.Path) -> None:
    blobs = {}
    for name, filename in FILES:
        data = (out_dir / filename).read_bytes()
        if filename in TEXT_FILES:
            data = data.replace(b"\r\n", b"\n")
            if b"\r" in data:
                raise SystemExit(f"{filename} carries a lone CR after normalisation; it is not "
                                 f"the LF text file this fixture assumes")
        blobs[name] = (filename, data, base64.b64encode(data).decode("ascii"))

    lines = [
        "// <auto-generated />",
        "// Generated by chat/tests/fixtures/make_tiny_chat_model.py. DO NOT EDIT BY HAND.",
        f"// Seed {SEED}; {TRANSFORMERS_PIN_NOTE}; onnxruntime==1.30.0; onnx==1.22.0.",
        "// Spec 16.2: a tiny but REAL ORT GenAI model directory, carried as base64 so nothing",
        "// binary is committed. The Length consts are plan adjustment 25: CI never runs this",
        "// script, so a test - not a comment - is the only thing that can notice a moved byte",
        "// count after an onnx, torch, transformers or builder upgrade.",
        "// Six files, not four. model.onnx.data is mandatory - the builder saves every initializer",
        "// externally (size_threshold_bytes=0) and a directory without it fails to load;",
        "// chat_template.jinja is mandatory - transformers 5.x splits chat_template out of",
        "// tokenizer_config.json and without it ApplyChatTemplate raises \"Empty chat template\".",
        "",
        "namespace Qavren.Edge.Chat.Tests.Fixtures;",
        "",
        "internal static class TinyChatModel",
        "{",
    ]
    for name, (filename, data, b64) in blobs.items():
        lines += [
            f"    public const string {name}FileName = \"{filename}\";",
            f"    public const int {name}Length = {len(data)};",
            f"    public const string {name}Base64 =",
            f"        \"{b64}\";",
            f"    public static byte[] {name}Bytes() => System.Convert.FromBase64String({name}Base64);",
            "",
        ]
    lines += [
        "    /// <summary>Writes every fixture file into <paramref name=\"directory\"/>; Model(string) needs a directory.</summary>",
        "    public static string Materialise(string directory)",
        "    {",
        "        System.IO.Directory.CreateDirectory(directory);",
    ]
    for name, _ in FILES:
        lines.append(f"        System.IO.File.WriteAllBytes("
                     f"System.IO.Path.Combine(directory, {name}FileName), {name}Bytes());")
    lines += ["        return directory;", "    }", "}", ""]

    text = "\n".join(lines)
    if len(text.encode("utf-8")) > SOURCE_CAP_BYTES:
        raise SystemExit(f"generated source is {len(text.encode('utf-8'))} bytes, over the "
                         f"{SOURCE_CAP_BYTES} cap - take spec 16.2's vendoring contingency")
    cs_path.write_text(text, encoding="utf-8", newline="\n")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--scratch", required=True)
    args = ap.parse_args()

    scratch = pathlib.Path(args.scratch).resolve()
    built = scratch / "genai"
    write_source_model(scratch)
    run_builder(scratch, built)
    assert_shape(built)
    emit(built, pathlib.Path(args.out).resolve())
    print("OK: TinyChatModel.g.cs written")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
