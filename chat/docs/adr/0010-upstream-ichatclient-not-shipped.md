# 10. The upstream `IChatClient` is not shipped

Date: 2026-09-11

## Status

Accepted

## Context

`Microsoft.ML.OnnxRuntimeGenAI.Managed` 0.15.2 already ships
`OnnxRuntimeGenAIChatClient : IChatClient`. Using it directly was
considered and rejected: measured against the shipped assembly,
`OnnxRuntimeGenAIChatClientOptions` has **exactly** `EnableCaching`,
`PromptFormatter`, `StopSequences` and nothing else; `GeneratorParams` is
the **only** place `SetSearchOption` lives, consumed once at `Generator`
construction; and `Generator` exposes no way to change a search option
after construction. Five defects follow directly from that shape:

1. It **skips `search.max_length` entirely when `EnableCaching` is true**
   (its own source comment: "don't set this if we're caching, since we
   want to be able to generate more tokens later"), so a cached
   conversation's KV cache is allocated against the model's declared
   `context_length` — 131072 on a stock Llama export — removing the only
   KV-cache memory lever the runtime exposes.
2. Because `SetSearchOption` only takes effect at construction, there is
   no API to change `max_length` on a live generator, and no honest way to
   fake one.
3. It keeps exactly one `Generator` with no serialisation around it, so a
   second concurrent turn allocates a **second full KV cache** rather than
   queuing.
4. It checks cancellation only between decoded tokens, so **prefill is
   uncancellable**.
5. It matches stop sequences by **single-decoded-token string equality**,
   so a multi-token stop string never fires.

It additionally swallows a failed `max_length` set silently, and is
compiled against `Microsoft.Extensions.AI.Abstractions` 9.8.0 while this
repo pins 10.10.0 — confirmed harmless by measurement (the 9.8.0-compiled
assembly binds against 10.10.0 with no `TypeLoadException`), but the
mismatch is one more reason not to inherit its choices uncritically.

`GetService(typeof(Model))` on the shipped `IChatClient` (MEAI's standard
escape hatch) already lets any caller reach the underlying `Model`,
`Config`, `Tokenizer` and `Generator` and build the upstream
`OnnxRuntimeGenAIChatClient` themselves in one line, if they want it and
accept its defects.

## Decision

Sub-project 4 wraps ORT GenAI's primitives — `Model`, `Config`,
`Tokenizer`, `Generator` — directly, inside `EdgeChatClient`, rather than
wrapping or subclassing `OnnxRuntimeGenAIChatClient`. Every one of the
charter items (memory budgets, jetsam guards, honest tokens/sec, turn
serialisation, prefill cancellation, multi-token stop matching) requires
being inside the decode loop, which the upstream type does not expose.

## Consequences

`Qavren.Edge.Chat.Onnx` owns its own decode loop, its own KV-cache-aware
`max_length` policy (split into a construction-time memory cap and a
managed per-turn output counter), its own turn gate, and its own stop-
sequence matching — more code than a thin wrapper, but every one of those
is a documented defect in the type this package deliberately does not use.
A consumer who wants the upstream client anyway can still reach it through
`GetService(typeof(Model))`.

**Revisit trigger:** a future `Microsoft.ML.OnnxRuntimeGenAI.Managed`
release ships a `SetSearchOption` that takes effect on a live `Generator`,
or fixes the caching/`max_length` interaction, or serialises concurrent
turns itself. Any one of those would remove the specific reason this ADR
exists for that item, though not necessarily all of them at once.
