# 12. No tool calling in v1

Date: 2026-09-11

## Status

Accepted

## Context

Prior art in this space kept tool calling survivable only by constraining
the model's output to a grammar that guarantees well-formed tool-call
syntax. ADR 0011 records that constrained decoding (`USE_GUIDANCE`) is
compiled out of every shipped mobile native — exactly the mechanism that
made grammar-constrained tool calling reliable elsewhere. Without it, a
~1B-parameter on-device model emits malformed tool calls often enough to
violate this suite's "never looks broken" rule: a caller cannot tell a
genuinely-declined tool call from a truncated or garbled one.

`Microsoft.Extensions.AI.FunctionInvokingChatClient` (`UseFunctionInvocation`)
sits above any `IChatClient` and parses whatever tool-call shape the inner
client produces. Layered above `EdgeChatClient` it would compile and run,
but it would be parsing unconstrained free text as if it were a
structured tool call.

## Decision

`Qavren.Edge.Chat.Onnx` does not support tool calling in v1.
`ChatOptions.Tools` non-empty, or a `ToolMode` that requires a call, is
refused with `ChatToolCallingUnsupported` (7107) — a required tool call
that can never be reliably emitted is a hard failure, not a footnote.
`chat/README.md` states plainly that `UseFunctionInvocation` above this
client is inert.

## Consequences

An app that wants tool calling against on-device chat must wait for a
model/runtime combination that ships constrained decoding on mobile
(ADR 0011's revisit trigger), or route tool-calling turns to a
server-hosted `IChatClient` instead. RAG (`UseRag()`) is unaffected: it is
a `DelegatingChatClient` that injects context as a plain user message, not
a tool call.

**Revisit trigger:** ADR 0011's revisit trigger fires (a mobile native
ships with `USE_GUIDANCE`), **and** the guidance-constrained schema surface
is rich enough to express a tool-call grammar reliably. Guidance alone is
necessary but not sufficient — this ADR should be re-opened only once both
hold.
