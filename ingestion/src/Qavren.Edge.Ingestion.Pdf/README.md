# Qavren.Edge.Ingestion.Pdf

PDF text extraction for `Qavren.Edge.Ingestion`, over [PdfPig](https://github.com/UglyToad/PdfPig)
0.1.16. **PdfPig is licensed under Apache-2.0**, which is why this extractor ships as a separate,
opt-in package rather than inside the MIT core: a consumer who never indexes a PDF never takes the
Apache-2.0 term, and one who does takes it knowingly. The notice is reproduced in the repository's
`THIRD-PARTY-NOTICES.md`.

## Registering it

```csharp
services.AddQavrenEdge(edge => edge
    .AddSqlite()
    .AddVectorStore()
    .AddIngestion(migrationVersion: 10)
    .AddPdfExtractor(o => o.PageBudget = TimeSpan.FromSeconds(45)));
```

`AddPdfExtractor` registers `PdfTextExtractor` (`Id = "pdf"`, `Version = 1`) for `.pdf` and
`application/pdf`. It is idempotent — a second call re-applies `configure` to the same
`PdfExtractorOptions` instance and registers no second extractor — and order-independent relative
to `AddIngestion`. A consumer who constructs `new PdfTextExtractor(options)` and passes it to
`AddDocumentExtractor` lands in the same registry.

## What it does

- Opens the document through `PdfDocument.Open(Stream, ParsingOptions)` — never the path overload,
  which reads the whole file into a byte array first. A 50 MB scan stays on disk.
- Enumerates `GetPages()` lazily: one page's letters are live at a time; each page's paragraphs are
  appended to the text buffer with their page number and the page is released.
- Reading order is `ContentOrderTextExtractor` by default (`PdfReadingOrderMode.ContentOrder`).
  `PdfReadingOrderMode.Layout` opts into the Docstrum + unsupervised reading-order pipeline for
  multi-column pages; it is materially more expensive per page and runs single-threaded here.
- Rejoins `extra-` / `ordinary` across a line break (`JoinHyphenatedLineBreaks`, on by default).
- A page with no letters and at least one image has no text layer. A document with no text-layer
  page at all comes back with `HasTextLayer = false`; the pipeline records it `NoTextLayer` with its
  content hash stored, so it is not re-parsed on every run. That is an outcome, not an error.
- Encrypted documents are tried against `PdfExtractorOptions.Passwords` (after the empty user
  password) and fail with `DocumentEncrypted` (6103) when none opens them. A malformed document is
  `DocumentMalformed` (6104) with PdfPig's exception preserved as the inner exception. One bad PDF
  fails one document, never a corpus.

## `SkipMissingFonts` defaults to `true`

On a font-name miss PdfPig otherwise reads and parses the name table of every file in the system
font directory, a multi-second stall on Android the first time such a PDF appears. The flag is on
by default so that never happens on a phone. What it costs in extraction quality on fonts that
would have been found is not yet measured (spec §17 item 6); until it is, the default stands, and a
consumer who wants the lookup sets `o.SkipMissingFonts = false`.

## `PageBudget` is a watchdog, not a timeout — read this

`PdfExtractorOptions.PageBudget` (20 s) is a **cumulative** budget checked **between pages**. At
each page boundary the extractor compares the time spent so far against the budget; when it is
passed, extraction stops there, every page already parsed is kept, the document is recorded
`Failed` with `DocumentPageBudgetExceeded` (6107) naming the last page parsed, and event 924 is
logged.

It does **not** interrupt a page that has started. PdfPig's per-page surface is fully synchronous
and accepts no `CancellationToken`: `GetPages()`, `Page.Letters`, `ContentOrderTextExtractor.GetText`
and the layout pipeline all run to completion or not at all. Abandoning a wedged page would need a
worker thread, and abandoning that thread mid-parse leaves the `PdfDocument` and its stream in an
undefined state — so this package does not do it and does not pretend to. The budget bounds the
realistic case, a 400-page document of individually reasonable but cumulatively slow pages eating a
run that was given twenty seconds. A single page that never returns can only be bounded by the
consumer's own process-level budget.

Cancellation is checked at the same page boundary.

## Trimming and AOT

PdfPig declares `IsTrimmable` and `IsAotCompatible`. This package never references
`UglyToad.PdfPig.DocumentLayoutAnalysis.Export`, whose Alto/PageXml exporters use `XmlSerializer`
and are the package's only trim hazard.
