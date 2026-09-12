# Qavren.Edge.Onnx

ONNX Runtime hosting for the Qavren.Edge suite (sub-project 2): `AddOnnx()`, the session
factory, the execution-provider policy per platform, consent-gated model provisioning with
pinned SHA-256 digests, `UseModelPaths()`, and the resource monitor (`IEdgeResourceMonitor`) the
embedding, ingestion and chat packages pace themselves against.

```
dotnet add package Qavren.Edge.Onnx
```

```csharp
services.AddQavrenEdge(edge => edge
    .AddOnnx()
    .UseModelPaths(p => p.ModelsDirectory = Path.Combine(FileSystem.AppDataDirectory, "models")));
```

`AddOnnxEmbeddings()` and `AddOnnxChat()` call `AddOnnx()` for you; it is idempotent. Nothing
downloads implicitly: a provisioner's `Plan()` reports byte counts and licences before
`ProvisionAsync` moves a byte. Error codes 5000-5299.

Sub-project README: [embeddings/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/embeddings/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
