# Qavren.Edge.Ingestion.Onnx

The ONNX satellite of `Qavren.Edge.Ingestion`: `AddOnnxIngestion()` wires the embedding
preset's real tokenizer in as the chunk tokenizer (so chunk budgets are counted in the tokens
the model will see) and a throttle over `Qavren.Edge.Onnx`'s resource monitor (so a background
run yields under memory pressure or thermal load).

```
dotnet add package Qavren.Edge.Ingestion.Onnx
```

```csharp
builder.UseQavrenEdge(edge => edge
    .AddOnnxEmbeddings()
    .AddVectorStore()
    .AddIngestion(migrationVersion: 10)
    .AddOnnxIngestion());
```

Without it, ingestion counts tokens with a fast approximation and runs unthrottled.

Sub-project README: [ingestion/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/ingestion/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
