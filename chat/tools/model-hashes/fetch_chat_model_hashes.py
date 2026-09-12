#!/usr/bin/env python3
"""Regenerate ChatPresets.g.cs and GenAiConfigFixtures.g.cs from Hugging Face.

NEVER a build step, and CI never runs it. Run by hand, then commit both emitted files.

    uv venv --python 3.14 .venv
    uv pip install --python .venv/Scripts/python.exe -r requirements.txt
    .venv/Scripts/python.exe fetch_chat_model_hashes.py \
        --out ../../src/Qavren.Edge.Chat.Onnx/ChatPresets.g.cs \
        --fixtures ../../tests/fixtures/GenAiConfigFixtures.g.cs

Three mechanics, all inherited from sub-project 2's fetch_preset_hashes.py and all of them things
that bite silently if got wrong:

  * `HuggingFaceRevision` is a full commit SHA, never "main". A moving revision silently changes
    the weights, and weights from two revisions are not the same model.
  * A `paths-info` `oid` is the SHA-256 only when the file is LFS-backed. model.onnx,
    model.onnx.data and tokenizer.json carry an `lfs: { oid, size }` sub-object holding a 64-hex
    digest; genai_config.json, tokenizer_config.json and chat_template.jinja are plain git blobs
    whose top-level `oid` is a 40-hex git SHA-1. Baking a SHA-1 into a manifest fails every
    provisioning verify at runtime with a hash nobody can debug, so a non-LFS file is downloaded
    and hashed here instead -- 9 KB in total across both presets, paid once, by hand.
  * ONE `POST /api/models/{repo}/paths-info/{rev}` per repo, never one HTTP request per file. The
    anonymous API bucket is 500 per five minutes; the resolver bucket is 3,000.

Nothing about the decoder geometry is hand-typed: every ChatModelShape value and every sampling
default is read out of the repo's own genai_config.json at the pinned commit (plan adjustment 15 --
the shipped `search` block, not ChatPreset's own type defaults). The prose in the XML doc comments
IS literal here, because most of it is device measurement no API reports; the numbers inside it
that ARE derivable are re-derived and asserted against the prose before anything is written, so a
republished config makes the comment loud rather than quietly wrong.

The second output (plan adjustment 24) is the tier-1 fixture pair: both genai_config.json bodies
verbatim as C# raw-string consts beside their SHA-256 and byte count. The script already downloads
them in order to hash them, so emitting them here is what stops a hand-retyped config from drifting
away from the file the provisioner verifies on a user's device.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import re
import urllib.request

API = "https://huggingface.co/api/models"
RESOLVE = "https://huggingface.co/{repo}/resolve/{rev}/{path}"
UA = {"User-Agent": "qavren-edge-chat-hashes/1"}

# The date the pinned revisions below were fetched. A constant, not date.today(): the verify for
# this script is a byte-identical regeneration, and a clock in the header would break it tomorrow.
# Bump it deliberately, in the same commit that repins a revision.
GENERATED_ON = "2026-09-11"

GENAI_CONFIG = "genai_config.json"

# (path, OnnxModelFileRole). Order is the emitted order. GraphFile names the decoder .onnx, not
# genai_config.json: that is what keys the content-addressed directory on the WEIGHTS (spec 5).
FILES: list[tuple[str, str]] = [
    ("model.onnx", "Graph"),
    ("model.onnx.data", "GraphExternalData"),
    (GENAI_CONFIG, "Auxiliary"),
    ("tokenizer.json", "Auxiliary"),
    ("tokenizer_config.json", "Auxiliary"),
    ("chat_template.jinja", "Auxiliary"),
]

PRESETS: list[dict] = [
    {
        "field": "Llama32_1BInstructInt4",
        "fixture": "Llama",
        "model_id": "llama-3.2-1b-instruct-int4",
        "display_name": "Llama 3.2 1B Instruct (int4)",
        "repo": "Arm/llama-3-2-1b-instruct-onnx-genai-int4-kquantlast-emb-int8-vivo-x300",
        "rev": "333c515af8b9355011c0b295c4356c9c24b463ff",
        # The Llama 3.2 Community Licence is not on the SPDX list, so it takes SPDX's own
        # convention for a licence that is not: LicenseRef-, never a bare LLAMA-3.2-Community,
        # which is not a valid SPDX expression at all (spec 6.5).
        "spdx": "LicenseRef-LLAMA-3.2-Community",
        "license_uri": "https://huggingface.co/meta-llama/Llama-3.2-1B-Instruct/blob/main/LICENSE.txt",
        "stop_sequences": ["<|eot_id|>", "<|eom_id|>", "<|end_of_text|>"],
        "default_max_context_tokens": 4096,
        "default_max_output_tokens": 512,
        "measured_lines": [
            "            MeasuredPeakBytes = 1_342_177_280L,   // 1280 MiB",
            '            MeasuredOn = "vivo X300, Android 16, ORT 1.27.0 CPU EP, 4 threads",',
        ],
        "doc": [
            "/// <summary>",
            "/// <c>Arm/llama-3-2-1b-instruct-onnx-genai-int4-kquantlast-emb-int8-vivo-x300</c>.",
            "/// 1.241 GB decimal on disk across six files; 1167.52 MiB of resident weights; context 4096 as",
            "/// shipped (the publisher patched it down from Llama's stock 131072 so the KV cache fits a",
            "/// phone). Geometry: 16 layers, 8 KV heads, head size 64, vocab 128256 - <b>32 KiB of KV per",
            "/// token</b>. Measured on a vivo X300 (Android 16, ORT 1.27.0 CPU EP, 4 threads): 30.564 tok/s",
            "/// decode, TTFT 950 ms, peak RSS 1280 MiB, model load 1.61 s, MMLU 5-shot 44.2%.",
            "/// <b>No iOS figure exists for this or any other ORT GenAI model.</b>",
            "/// </summary>",
        ],
        # Every one of these is re-derived from what the Hub just returned and must appear in the
        # doc comment above. A republished config that moves a number makes the prose loud.
        "doc_claims": [
            "{layers} layers, {kv_heads} KV heads, head size {head_size}, vocab {vocab}",
            "{kv_kib} KiB of KV per token",
            "context {context} as shipped",
            "{total_gb} GB decimal on disk across six files",
            "{weights_mib} MiB of resident weights",
        ],
    },
    {
        "field": "Qwen3_600MInt4",
        "fixture": "Qwen",
        "model_id": "qwen3-0.6b-int4",
        "display_name": "Qwen3 0.6B (int4)",
        "repo": "Arm/qwen3-0-6b-onnx-genai-int4-kquantlast-emb-int4",
        "rev": "c1d7bbbbb20630eef24c00d8ad18250bd57b232c",
        "spdx": "Apache-2.0",
        "license_uri": "https://www.apache.org/licenses/LICENSE-2.0",
        "stop_sequences": ["<|im_end|>", "<|endoftext|>"],
        "default_max_context_tokens": 4096,
        "default_max_output_tokens": 512,
        # The only published throughput for this preset is AWS Graviton, and it is deliberately NOT
        # in MeasuredPeakBytes: the budget reads that field alone and has no way to discount a
        # measurement by where it was taken, so a server peak entering it would silently become a
        # phone budget.
        "measured_lines": [
            "            MeasuredPeakBytes = null,",
            '            MeasuredOn = "AWS Graviton g4 (published 70.4 tok/s, peak 838 MB decimal) - a server "',
            '                       + "measurement, recorded here and deliberately NOT in MeasuredPeakBytes",',
        ],
        "doc": [
            "/// <summary>",
            "/// <c>Arm/qwen3-0-6b-onnx-genai-int4-kquantlast-emb-int4</c>. 495 MB decimal on disk across six",
            "/// files; 461.25 MiB of resident weights; Apache-2.0 - the preset for an app that cannot take",
            "/// the Llama terms, and the model the nightly lane uses.",
            "/// <para>",
            "/// <b>Smaller on disk is not smaller in memory, and here are the numbers.</b> 28 layers, 8 KV",
            "/// heads, head size 128 - <b>112 KiB of KV per token</b>, 3.5x this catalogue's larger preset,",
            "/// against a third of the weights. At 4096 tokens its KV cache alone is 448 MiB. Its declared",
            "/// <c>context_length</c> is <b>40960</b>, so a generator built with no <c>max_length</c> would",
            "/// allocate <b>4480 MiB</b> of KV cache - which is exactly the jetsam scenario the memory budget",
            "/// and the belt-and-braces <c>search.max_length</c> exist to prevent.",
            "/// </para>",
            "/// <para>",
            "/// Its only published throughput is AWS Graviton (70.4 tok/s, peak 838 MB decimal), and that",
            "/// figure is deliberately <b>not</b> in <c>MeasuredPeakBytes</c>: the budget reads that field",
            "/// alone and has no way to discount a measurement by where it was taken, so a server peak",
            "/// entering it would silently become a phone budget.",
            "/// </para>",
            "/// </summary>",
        ],
        "doc_claims": [
            "{layers} layers, {kv_heads} KV heads, head size {head_size}",
            "{kv_kib} KiB of KV per token",
            "<c>context_length</c> is <b>{context}</b>",
            "{total_mb} MB decimal on disk across six files",
            "{weights_mib} MiB of resident weights",
            "At 4096 tokens its KV cache alone is {kv_at_4096_mib} MiB",
            "allocate <b>{kv_at_context_mib} MiB</b> of KV cache",
        ],
    },
]

HEX64 = re.compile(r"^[0-9a-f]{64}$")
HEX40 = re.compile(r"^[0-9a-f]{40}$")


# --------------------------------------------------------------------------------------------
# Hub access
# --------------------------------------------------------------------------------------------

def paths_info(repo: str, rev: str, paths: list[str]) -> dict[str, dict]:
    """ONE POST per repo. Never one request per file -- wrong rate-limit bucket."""
    body = json.dumps({"paths": paths}).encode("utf-8")
    request = urllib.request.Request(
        f"{API}/{repo}/paths-info/{rev}",
        data=body,
        headers={"Content-Type": "application/json", **UA},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=60) as response:
        entries = json.load(response)
    found = {e["path"]: e for e in entries}
    missing = [p for p in paths if p not in found]
    if missing:
        raise SystemExit(f"{repo}@{rev}: paths-info returned nothing for {missing}")
    return found


def fetch(repo: str, rev: str, path: str) -> bytes:
    url = RESOLVE.format(repo=repo, rev=rev, path=path)
    with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=300) as response:
        return response.read()


def digest(repo: str, rev: str, entry: dict) -> tuple[int, str, bytes | None]:
    """(size, sha256, body-or-None). LFS: straight from lfs.oid. Plain blob: download and hash."""
    lfs = entry.get("lfs")
    if lfs:
        oid = lfs["oid"]
        if not HEX64.fullmatch(oid):
            raise SystemExit(f"{entry['path']}: lfs.oid is not a sha256: {oid}")
        return int(lfs["size"]), oid, None
    payload = fetch(repo, rev, entry["path"])
    # A truncated body that hashes cleanly is the one failure this check catches.
    if len(payload) != entry["size"]:
        raise SystemExit(
            f"{entry['path']}: downloaded {len(payload)} bytes, paths-info said {entry['size']}")
    return len(payload), hashlib.sha256(payload).hexdigest(), payload


# --------------------------------------------------------------------------------------------
# Formatting
# --------------------------------------------------------------------------------------------

def group(value: int) -> str:
    """C# digit separators: 1224094720 -> 1_224_094_720. Used for every byte count."""
    return f"{value:_d}"


def count(value: int) -> str:
    """Token and vocabulary counts: separators only once they stop reading as one number."""
    return group(value) if value >= 10_000 else str(value)


def single(value: float) -> str:
    """A C# float literal for a JSON number: 0.6 -> 0.6f, 0.95 -> 0.95f."""
    text = repr(float(value))
    if text.endswith(".0"):
        text = text[:-2]
    return f"{text}f"


def flatten(doc: list[str]) -> str:
    """The doc comment as one line of prose, so a claim can be matched across a wrap."""
    stripped = [line.removeprefix("///").strip() for line in doc]
    return re.sub(r"\s+", " ", " ".join(part for part in stripped if part))


def raw_string(body: str) -> list[str]:
    """A C# raw-string literal whose value is exactly `body`.

    The newline immediately before the closing delimiter is not part of the value, and both
    delimiters sit in column 0 so no indentation is stripped from the content.
    """
    return ['"""', *body.split("\n"), '""";']


# --------------------------------------------------------------------------------------------
# Emit
# --------------------------------------------------------------------------------------------

def resolve(preset: dict) -> dict:
    repo, rev = preset["repo"], preset["rev"]
    if rev == "main" or not HEX40.fullmatch(rev):
        raise SystemExit(f"{repo}: revision must be a full commit SHA, got {rev!r}")

    info = paths_info(repo, rev, [path for path, _ in FILES])
    files: list[tuple[str, str, int, str]] = []
    config_body: bytes | None = None
    for path, role in FILES:
        size, sha, body = digest(repo, rev, info[path])
        if HEX40.fullmatch(sha):
            raise SystemExit(
                f"{repo}: {path} digest {sha} is 40 hex -- that is a git SHA-1 where a SHA-256 "
                f"belongs, and it would fail every provisioning verify at runtime")
        if not HEX64.fullmatch(sha):
            raise SystemExit(f"{repo}: {path} digest is not a sha256: {sha}")
        files.append((path, role, size, sha))
        if path == GENAI_CONFIG:
            config_body = body if body is not None else fetch(repo, rev, path)

    assert config_body is not None
    if config_body[:3] == b"\xef\xbb\xbf":
        raise SystemExit(f"{repo}: {GENAI_CONFIG} starts with a UTF-8 BOM")
    text = config_body.decode("utf-8")  # raises on anything that is not valid UTF-8
    if "\r" in text:
        raise SystemExit(
            f"{repo}: {GENAI_CONFIG} contains CR -- a raw-string literal would stop being "
            f"byte-faithful across a checkout that normalises line endings")
    if '"""' in text:
        raise SystemExit(f'{repo}: {GENAI_CONFIG} contains a """ sequence')

    config = json.loads(text)
    model, decoder, search = config["model"], config["model"]["decoder"], config["search"]

    weights = sum(s for _, role, s, _ in files if role in ("Graph", "GraphExternalData"))
    total = sum(s for _, _, s, _ in files)
    if weights == total:
        raise SystemExit(f"{repo}: WeightsBytes equals TotalSizeBytes -- the auxiliary files are "
                         f"being counted as resident weights")

    layers = decoder["num_hidden_layers"]
    kv_heads = decoder["num_key_value_heads"]
    head_size = decoder["head_size"]
    context = model["context_length"]
    kv_per_token = layers * kv_heads * head_size * 2 * 2  # key + value, 2 bytes per element

    claims = {
        "layers": layers,
        "kv_heads": kv_heads,
        "head_size": head_size,
        "vocab": model["vocab_size"],
        "context": context,
        "kv_kib": kv_per_token // 1024,
        "kv_at_4096_mib": f"{kv_per_token * 4096 / 2**20:g}",
        "kv_at_context_mib": f"{kv_per_token * context / 2**20:g}",
        "weights_mib": f"{weights / 2**20:.2f}",
        "total_gb": f"{total / 1e9:.3f}",
        "total_mb": f"{total / 1e6:.0f}",
    }
    prose = flatten(preset["doc"])
    for template in preset["doc_claims"]:
        claim = template.format(**claims)
        if claim not in prose:
            raise SystemExit(
                f"{repo}: the doc comment no longer says {claim!r} -- the published config moved, "
                f"so fix the prose in this script rather than shipping a comment that lies")

    return {
        "preset": preset,
        "files": files,
        "weights": weights,
        "config_text": text,
        "config_sha": next(sha for path, _, _, sha in files if path == GENAI_CONFIG),
        "config_size": next(size for path, _, size, _ in files if path == GENAI_CONFIG),
        "shape": {
            "ModelType": model["type"],
            "ContextLength": context,
            "VocabSize": model["vocab_size"],
            "NumHiddenLayers": layers,
            "NumKeyValueHeads": kv_heads,
            "HeadSize": head_size,
            "SlidingWindow": decoder.get("sliding_window"),
            "DecoderFileName": decoder["filename"],
        },
        # Plan adjustment 15: the model's own shipped sampling values, not ChatPreset's type
        # defaults. A preset that silently samples differently from the publisher's own
        # genai_config.json is a quality regression nobody will attribute.
        "search": {
            "temperature": search["temperature"],
            "top_p": search["top_p"],
            "top_k": search["top_k"],
        },
    }


def emit_presets(resolved: list[dict]) -> str:
    lines = [
        "// <auto-generated />",
        f"// Generated by chat/tools/model-hashes/fetch_chat_model_hashes.py on {GENERATED_ON}.",
        "// DO NOT EDIT BY HAND. Re-run the script and commit the diff; a changed digest is a deliberate",
        "// decision about which weights ship, never a merge conflict to resolve.",
        "//",
        "// Every geometry value below is read from the repo's own genai_config.json at the pinned commit,",
        "// and every digest is the git-LFS oid where the file is LFS-backed and a downloaded-and-hashed",
        "// SHA-256 where it is not. No value here is hand-typed.",
        "",
        # OnnxModelManifest / OnnxModelFile / OnnxModelFileRole all live in Qavren.Edge.Onnx.
        # Without this using the generated file does not compile (CS0246, once per TFM).
        "using Qavren.Edge.Onnx;",
        "",
        "namespace Qavren.Edge.Chat;",
        "",
        "public static partial class ChatPresets",
        "{",
    ]
    for item in resolved:
        preset, shape, search = item["preset"], item["shape"], item["search"]
        lines += [f"    {line}" for line in preset["doc"]]
        lines.append(f"    public static ChatPreset {preset['field']} {{ get; }} = new()")
        lines.append("    {")
        lines.append(f"        Id = \"{preset['model_id']}\",")
        lines.append(f"        DisplayName = \"{preset['display_name']}\",")
        lines.append(f"        LicenseUri = new Uri(\"{preset['license_uri']}\"),")
        lines.append("        Manifest = new OnnxModelManifest")
        lines.append("        {")
        lines.append(f"            ModelId = \"{preset['model_id']}\",")
        lines.append(f"            GraphFile = \"{FILES[0][0]}\",")
        lines.append(f"            SpdxLicense = \"{preset['spdx']}\",")
        lines.append(f"            HuggingFaceRepo = \"{preset['repo']}\",")
        lines.append(f"            HuggingFaceRevision = \"{preset['rev']}\",")
        lines.append("            Files =")
        lines.append("            [")
        for path, role, size, sha in item["files"]:
            lines.append(
                f"                new OnnxModelFile(\"{path}\", OnnxModelFileRole.{role}, {group(size)},")
            lines.append(f"                    \"{sha}\"),")
        lines.append("            ],")
        lines.append("        },")
        lines.append("        Shape = new ChatModelShape")
        lines.append("        {")
        lines.append(f"            ModelType = \"{shape['ModelType']}\",")
        lines.append(f"            ContextLength = {count(shape['ContextLength'])},")
        lines.append(f"            VocabSize = {count(shape['VocabSize'])},")
        lines.append(f"            NumHiddenLayers = {shape['NumHiddenLayers']},")
        lines.append(f"            NumKeyValueHeads = {shape['NumKeyValueHeads']},")
        lines.append(f"            HeadSize = {shape['HeadSize']},")
        window = "null" if shape["SlidingWindow"] is None else count(shape["SlidingWindow"])
        lines.append(f"            SlidingWindow = {window},")
        lines.append(f"            DecoderFileName = \"{shape['DecoderFileName']}\",")
        lines.append(f"            WeightsBytes = {group(item['weights'])}L,")
        lines += preset["measured_lines"]
        lines.append("        },")
        stops = ", ".join(f"\"{s}\"" for s in preset["stop_sequences"])
        lines.append(f"        StopSequences = [{stops}],")
        lines.append(f"        DefaultMaxContextTokens = {count(preset['default_max_context_tokens'])},")
        lines.append(f"        DefaultMaxOutputTokens = {count(preset['default_max_output_tokens'])},")
        lines.append(f"        DefaultTemperature = {single(search['temperature'])},")
        lines.append(f"        DefaultTopP = {single(search['top_p'])},")
        lines.append(f"        DefaultTopK = {count(search['top_k'])},")
        lines.append("    };")
        lines.append("")
    fields = ", ".join(item["preset"]["field"] for item in resolved)
    lines.append(f"    private static readonly ChatPreset[] AllPresets = [{fields}];")
    lines.append("}")
    return "\n".join(lines) + "\n"


def emit_fixtures(resolved: list[dict]) -> str:
    lines = [
        "// <auto-generated />",
        f"// Generated by chat/tools/model-hashes/fetch_chat_model_hashes.py on {GENERATED_ON}.",
        "// DO NOT EDIT BY HAND. These are the two presets' genai_config.json bodies EXACTLY as published at",
        "// the pinned revisions - the same bytes ChatPresets.g.cs pins for provisioning. The digests below",
        "// are asserted by a tier-1 test (plan adjustment 24), so a hand-edit that \"tidies\" the JSON turns",
        "// every section 16.1 shape assertion loud instead of silently wrong.",
        "",
        "namespace Qavren.Edge.Chat.Tests.Fixtures;",
        "",
        "internal static class GenAiConfigFixtures",
        "{",
    ]
    for index, item in enumerate(resolved):
        preset = item["preset"]
        name = preset["fixture"]
        if index:
            lines.append("")
        lines.append(
            f"    /// <summary>{preset['repo']} at {preset['rev']}.</summary>")
        literal = raw_string(item["config_text"])
        lines.append(f"    public const string {name}ConfigJson = {literal[0]}")
        lines += literal[1:]
        lines.append(f"    public const string {name}ConfigSha256 = \"{item['config_sha']}\";")
        lines.append(f"    public const int {name}ConfigLength = {group(item['config_size'])};")
    lines.append("}")
    return "\n".join(lines) + "\n"


def write(path: str, text: str) -> None:
    out = pathlib.Path(path)
    out.parent.mkdir(parents=True, exist_ok=True)
    # newline="\n" is load-bearing, not cosmetic. The default translates "\n" to os.linesep, so on
    # Windows this would emit CRLF while .gitattributes (`* text=auto eol=lf`) checks the file out
    # as LF -- and the byte-identity verify would fail on every fresh clone.
    out.write_text(text, encoding="utf-8", newline="\n")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", required=True, help="path to ChatPresets.g.cs")
    parser.add_argument("--fixtures", required=True, help="path to GenAiConfigFixtures.g.cs")
    args = parser.parse_args()

    resolved = [resolve(preset) for preset in PRESETS]

    write(args.out, emit_presets(resolved))
    write(args.fixtures, emit_fixtures(resolved))

    for item in resolved:
        preset = item["preset"]
        print(f"{preset['field']}  {preset['repo']}@{preset['rev']}")
        for path, role, size, sha in item["files"]:
            print(f"  {path:<22} {role:<18} {size:>13} {sha}")
        print(f"  {'WeightsBytes':<22} {'':<18} {item['weights']:>13}")
    print(f"wrote {args.out}")
    print(f"wrote {args.fixtures}")


if __name__ == "__main__":
    main()
