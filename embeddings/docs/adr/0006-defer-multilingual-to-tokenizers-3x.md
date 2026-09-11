# 6. Defer multilingual presets to `Microsoft.ML.Tokenizers` 3.x

Date: 2026-09-11

## Status

Accepted

## Context

Multilingual embedding graphs (XLM-R and e5-family models) need a
SentencePiece/Unigram tokenizer that emits token ids in the fairseq layout
those graphs were trained against: `<s>` = 0, `<pad>` = 1, `</s>` = 2,
`<unk>` = 3, and `<mask>` at the top of the vocabulary (250001 for XLM-R).

`Microsoft.ML.Tokenizers` 2.0.0 — the version this plan otherwise pins
throughout — cannot produce that layout. Two facts were verified directly
against the shipped assembly rather than assumed from the changelog:

- `CreateFromTokenizerJson` does not exist in 2.0.0 at all — the symbol does
  not occur anywhere in the assembly.
- `SentencePieceTokenizer.Create` emits **raw SentencePiece piece indices**,
  not the fairseq-remapped ids the graphs expect.

The failure mode this would create is the worst kind: silent. A raw
SentencePiece id fed into a fairseq-trained embedding graph is still a valid
input tensor — it produces a plausible-looking embedding vector that is
simply wrong, with no exception, no NaN, and no obvious symptom short of a
similarity-search quality regression a consumer would have no reason to
trace back here.

## Decision

Multilingual presets are not shipped in v1. `EdgeTokenizerKind` already
carries a `UnigramTokenizerJson` member for this exact future case, and any
attempt to construct a tokenizer of that kind throws `TokenizerKindUnsupported`
(5102), naming the `Microsoft.ML.Tokenizers` 3.x requirement explicitly in
the exception message so the failure is loud and immediate rather than a
silently wrong vector.

## Consequences

No multilingual preset ships until `Microsoft.ML.Tokenizers` 3.x is
available and confirmed (by the same kind of assembly-level check performed
here) to expose either `CreateFromTokenizerJson` or a fairseq-layout-correct
`SentencePieceTokenizer`. The enum member and the throw site mean adding
multilingual support later is additive — no `EdgeTokenizerKind` renumbering,
no breaking change to the tokenizer abstraction — once the underlying
library can actually do it correctly.
