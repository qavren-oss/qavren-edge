# Qavren.Edge.Sqlite.Native

The suite's own SQLite native library with `sqlite-vec` compiled in, for every supported RID:
`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `maccatalyst-x64`,
`maccatalyst-arm64`, `android-arm64`, `android-x64`, `android-arm` (16 KB page aligned), plus the
iOS and Mac Catalyst xcframework. Plain build, no encryption.

```
dotnet add package Qavren.Edge.Sqlite.Native
```

```csharp
services.AddQavrenEdge(edge => edge
    .AddSqlite(o => o.DatabaseName = "notes.db")
    .UseSqliteNative());
```

Which library loaded, from where, and which SQLite and sqlite-vec versions it carries are all
inspectable through the suite's diagnostics. Referencing this package and
`Qavren.Edge.Sqlite.Native.Cipher` in one app is a configuration error caught at startup.

Sub-project README: [foundation/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/foundation/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
