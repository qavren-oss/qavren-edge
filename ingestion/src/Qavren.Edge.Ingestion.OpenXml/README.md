# Qavren.Edge.Ingestion.OpenXml

DOCX text extraction for `Qavren.Edge.Ingestion`, over `DocumentFormat.OpenXml`. MIT like the
core; it is a separate package for size (the dependency is about 14 MB), so a Markdown-only app
never carries it.

```
dotnet add package Qavren.Edge.Ingestion.OpenXml
```

```csharp
builder.UseQavrenEdge(edge => edge
    // ... AddSqlite, AddOnnxEmbeddings, AddVectorStore, AddIngestion ...
    .AddDocxExtractor());
```

Headings become the chunk breadcrumb the same way Markdown headings do.

## Bounds on a hostile document

A DOCX is a zip of XML, and both layers can be built to exhaust whatever reads them. Two bounds
are enforced before the Open XML SDK builds anything from a part. A document over either is
recorded `Failed` with `DocumentMalformed` (6104) and the run continues.

- **Inflated size.** No XML part (`.xml`, `.rels`) may inflate past 256 MiB. The size the package
  declares is checked before anything is opened, and the same ceiling is handed to the SDK as
  `MaxCharactersInPart`, so it also holds while a part is being read.
- **Nesting depth.** No part may nest elements more than 256 deep. The SDK's DOM load recurses
  once per level, and a stack overflow is not an exception a run can record: it ends the process.

Neither is configurable and neither is near a real document. Word's own documented ceiling is
32 MB of text, and ten tables nested inside one another come to about fifty levels.

A part under the size ceiling is still read the way its file size selects: below
`StreamingThresholdBytes` (8 MiB, measured on the `.docx`, not on what it inflates to) the main
part is materialised as a DOM.

Sub-project README: [ingestion/README.md](https://github.com/qavren-oss/qavren-edge/blob/main/ingestion/README.md).
MIT licensed. https://github.com/qavren-oss/qavren-edge
