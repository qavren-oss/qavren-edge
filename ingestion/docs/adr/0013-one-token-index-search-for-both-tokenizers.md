# 13. One token-index search for both tokenizers

Date: 2026-09-11

## Status

Accepted

## Context

The spec designs two separate derivations for `IChunkTokenizer.IndexByTokenCount`:
`MlChunkTokenizer` (the ONNX-free path) calling
`Tokenizer.GetIndexByTokenCount(…, considerNormalization: false)` directly,
and `EdgeChunkTokenizer` (the ONNX satellite) binary-searching `CountTokens`.

The measured behaviour of `Tokenizer.GetIndexByTokenCount`, over `"the quick
brown fox jumps over the lazy dog hello world"` (55 chars, 11 tokens), at
both settings:

| maxTokenCount | default (`considerNormalization: true`) | `considerNormalization: false` |
|---|---|---|
| 1 | index 3, tokenCount 1 | index **55**, tokenCount **1** |
| 2 | index 10, tokenCount 2 | index **55**, tokenCount **1** |
| 3 | index 16, tokenCount 3 | index **55**, tokenCount **1** |
| 5 | index 26, tokenCount 5 | index **55**, tokenCount **1** |
| 8 | index 40, tokenCount 8 | index **55**, tokenCount **1** |
| 11 | index 55, tokenCount 11 | index **55**, tokenCount **1** |

This closes the question with two facts pointing in opposite directions.
First, the spec's diagnosis of the *default* path is correct: at
`considerNormalization: true` (the default), the returned index is into the
*normalised* copy of the text, not the original. For the NFD input
`"café café café hello world"` (29 chars, composed as combining sequences),
the call returned `normalizedText` 26 chars long and `index = 15`;
`original[..15]` lands mid-word — exactly the silent mislocation the spec
warns about. Second, and this is the part the spec gets wrong: its proposed
*remedy* does not work. `considerNormalization: false` does not mean "the
same tokenization, indexed into the original" — it returned the **whole
string** for every budget from 1 to 11 in the table above. A
`MlChunkTokenizer` built on that call would emit the entire document as one
chunk regardless of budget, which `ChunkExceedsTokenBudget` (6151) would then
throw on for any real document — the ONNX-free path would not work at all.

## Decision

Neither of the spec's two derivations is used. Instead, `Qavren.Edge.Ingestion`
ships one `internal static class TokenIndexSearch`, shared by both
`MlChunkTokenizer` and `EdgeChunkTokenizer`: estimate a cut from a running
chars-per-token ratio, snap candidate boundaries to grapheme-cluster
boundaries with `StringInfo`, search within ±25% of the estimate (widening
once if that window fails to bound the answer), and verify the chosen cut
with one final `CountTokens` call. Both tokenizers delegate to it through
their own `CountTokens` implementation — `MlChunkTokenizer`'s over the real
`Tokenizer`, `EdgeChunkTokenizer`'s over SP2's `IEdgeTokenizer.CountTokens`,
which SP2 forwards verbatim and which takes a span rather than returning an
index, so no normalised string can leak through it either way.
`Tokenizer.GetIndexByTokenCount` is not called by either implementation.

## Consequences

The offset contract — `IndexByTokenCount` returns an index into the string as
passed, never into a normalised copy — holds **by construction** on both
paths, because the search only ever measures prefixes of the original text
it was handed. The token count the search settles on is exactly what the
encoder will produce, because it is measured with `CountTokens` at the
tokenizer's own defaults, the same defaults the encoder uses.

No SP2 edit is asked for now or later: verification item 11's proposed
fallback — an additive `IEdgeTokenizer.IndexByTokenCount` overload taking
`considerNormalization`, filed as an SP2 issue — is **withdrawn**, because no
setting of that parameter on the underlying `Tokenizer` API would have
helped (the table above shows `false` is not a usable answer either). The
cost of the shared search is real and bounded: roughly 8–12 `CountTokens`
calls per cut over a bounded span, not an extra pass over the whole text.
Verification item 11 still measures that cost on an ARM64 device, since a
bounded search is not the same claim as a cheap one.
