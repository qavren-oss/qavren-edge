# Qavren.Edge.Chat.Onnx

An on-device `Microsoft.Extensions.AI` `IChatClient` over ONNX Runtime GenAI (sub-project 4).
Presets with pinned SHA-256 digests (`ChatPresets.Llama32_1BInstructInt4`,
`ChatPresets.Qwen3_600MInt4`), consent-gated provisioning, a KV-cache-aware context budget
with a ladder for small phones, thermally paced streaming, cooperative termination under memory
pressure, a conversation cache, and history reduction with pinning. Error codes 7000-7299.

```
dotnet add package Qavren.Edge.Chat.Onnx
```

```csharp
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .AddOnnxChat(ChatPresets.Llama32_1BInstructInt4));

var chat = sp.GetRequiredService<IChatClient>();
await foreach (var update in chat.GetStreamingResponseAsync("summarise my week"))
    Console.Write(update.Text);
```

There is no default preset and nothing downloads implicitly. Turns serialise behind one gate per
model (the GenAI C API is not thread safe); a fifth queued turn is refused with `ChatBusy`. On iOS,
a multi-GB model needs two entitlements in the consuming app:
`com.apple.developer.kernel.increased-memory-limit` and
`com.apple.developer.kernel.extended-virtual-addressing`. No Windows TFM: GenAI ships no Windows
platform asset (the `net10.0` build covers desktop).

Sub-project README: [chat/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/chat/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
