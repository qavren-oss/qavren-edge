# Qavren.Edge.Maui

The .NET MAUI bridge for Qavren.Edge: `UseQavrenEdge()` on `MauiAppBuilder`, the platform
lifecycle bridge (Android, iOS, Mac Catalyst, Windows) that feeds the suite's suspend/resume and
memory-pressure signals, and `FileSystem`-backed app paths so databases and models land in the
right per-platform directory.

```
dotnet add package Qavren.Edge.Maui
```

```csharp
// MauiProgram.cs
builder.UseQavrenEdge(edge => edge
    .UseSqliteNative()
    .AddSqlite(o => o.DatabaseName = "notes.db"));
```

`UseQavrenEdge` is `AddQavrenEdge` plus the MAUI wiring; use one or the other, not both. MAUI
has no hosted-service loop, so background work is triggered from the lifecycle events this
package raises, not from `IHostedService`.

Sub-project README: [foundation/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/foundation/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
