# 9. PdfPig and the Apache-2.0 split

Date: 2026-09-11

## Status

Accepted

## Context

Suite decision 11 fixes MIT as the licence for every first-party package in
this suite. PdfPig — the PDF text-extraction library sub-project 3 needs — is
Apache-2.0, not MIT.

Every alternative fails a hard constraint before licence even enters the
comparison:

- **Docnet.Core 2.6.0** ships no `android`, `ios` or `maccatalyst` natives and
  is three years stale.
- **PDFsharp 6.2.4** has no text-extraction member at all.
- **iText 9.7.0** is AGPL — a stricter copyleft than the problem it would
  solve.
- **Syncfusion** and **Telerik** are commercial.

The platform "fast path" alternative — `PdfKit.PdfDocument.Text` on Apple,
`PdfRenderer.Page.TextContents` on Android API 35+ — was also considered and
rejected: it buys two extra code paths and two extra test matrices, and
Windows has no first-party PDF text extractor at all, so PdfPig would remain
the fallback there regardless. Taking it everywhere is simpler than taking it
conditionally.

## Decision

Take the Apache-2.0 dependency, but isolate it: PdfPig is referenced only by
the opt-in `Qavren.Edge.Ingestion.Pdf` satellite, never by the core. The
satellite's README states the Apache-2.0 term in its first paragraph, so the
licence is visible before a consumer's first line of code.

`UglyToad.PdfPig.DocumentLayoutAnalysis.Export` is never referenced. Its
Alto/PageXml exporters use `XmlSerializer` and carry
`RequiresUnreferencedCode`, and they are the only trim hazard anywhere in the
package — avoiding that one namespace keeps the satellite fully trimmable.

## Consequences

A Markdown-only, PDF-free consumer carries neither the Apache-2.0 term nor
the ~5.7 MB of embedded CMap/ICC resources PdfPig's `net9.0` assets bring —
the isolation is complete rather than a licence note buried in a shared
package. A consumer who does opt in accepts one call
(`AddPdfExtractor()`) and one licence, spelled out where they will read it.

The platform-fast-path alternative is left on the table rather than closed
off: if a future measurement shows PdfPig's per-page cost is unacceptable on
a specific platform, that platform can add its own extractor without
touching this decision for the others.
