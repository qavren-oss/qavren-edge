# Ingestion test fixtures

Every byte under `corpus/` is **committed**. Nothing here is generated at build time, by a test,
or by CI — with two deliberate exceptions, both of which build a *container* around bytes that are
themselves committed:

- `corpus/pdf/flate-content.stream` is deflated by the test at a fixed `CompressionLevel`
  (`DeflateStream` is deterministic; PdfPig's trailer `/ID` is not).
- The eleven `corpus/docx/<name>/` part trees are zipped into an OPC package at test time by
  `DeterministicOpc.Build`. **No `.docx` is committed anywhere in this repository.**

## Why the inputs are committed rather than generated

`PdfDocumentBuilder` writes a random trailer `/ID` pair, so identical content hashes differently on
every build, and `WordprocessingDocument.Create` embeds execution-time zip stamps. A generated
fixture also makes the *input* move whenever the generator library upgrades — at which point a
golden test is measuring PdfPig or the OpenXML SDK rather than the chunker. Committing the bytes
makes the input a constant and the chunker the only variable.

## `corpus/** -text` is load-bearing

`.gitattributes` in this directory disables all end-of-line conversion for `corpus/**` and
`golden/**`. Chunk offsets are character positions into the extracted text. The repository root
sets `* text=auto eol=lf`, but editing tooling on Windows flips LF to CRLF on multi-line edits, and
a single CRLF that reaches a fixture moves every expected boundary — on the Linux CI leg only,
which is the worst possible place to find out. `-text` means what is committed is byte-for-byte
what every platform checks out.

`corpus/text/crlf-and-lone-cr.txt` is the file this rule exists for: it contains CRLF pairs **and**
a lone CR on purpose. Never re-save it in an editor.

## What each fixture is for

### `corpus/text/` — plain text

| File | What it drives |
|---|---|
| `empty.txt` | empty input yields zero chunks and no throw |
| `whitespace-only.txt` | `"   \n\n\t\n"` — no chunk, and in particular no *empty* chunk |
| `three-paragraphs.txt` | paragraph blocks, a sentence-aware cut, and no trailing newline |
| `crlf-and-lone-cr.txt` | CRLF and a lone CR both normalise to LF; offsets are into the normalised buffer |
| `long-token.txt` | 5,000 `A` with no whitespace — the hard split with no separator to back up to |
| `unicode.txt` | NFC and NFD `é`, a ZWJ family emoji, CJK, an RTL run, and `I İ i ı` |
| `bom.txt` | a UTF-8 BOM is stripped and offsets start after it |

### `corpus/markdown/` — Markdig input

| File | What it drives |
|---|---|
| `headings.md` | YAML front matter, a preamble before the first heading, h1–h4, a setext h1, and a `#` line inside a fence |
| `fences.md` | a tilde fence, three backticks nested in a four-tick fence, and an indented code block — three ways a `##` line is not a heading |
| `tables-lists.md` | a pipe table (header + three body rows) for the row-wise table split, plus nested and ordered lists |
| `raw-html.md` | an `HtmlBlock` and inline HTML, both kept as verbatim source substrings |
| `giant-heading-section.md` | one h2 section of forty sentences — must split and keep the breadcrumb on every piece |

### `corpus/pdf/` — seven committed PDFs

All seven are uncompressed, pure 7-bit ASCII so a reviewer can read the diff. The single exemption
is `no-text-layer.pdf`, whose four inline-image bytes are the point of the fixture.

| File | What it drives |
|---|---|
| `minimal-text.pdf` | the reference shape: five objects, classic xref, Helvetica Type1 |
| `two-pages.pdf` | page numbers reaching `DocumentBlock.PageNumber` and `ChunkDraft.Page` |
| `two-columns.pdf` | two text runs at different `x` in one `y` band — reading order |
| `hyphen-linebreak.pdf` | `JoinHyphenatedLineBreaks` rejoining `extra-` + `ordinary` |
| `no-text-layer.pdf` | a rectangle and a tiny inline image, no text operators — `NoTextLayer`, never an exception |
| `xref-stream.pdf` | a PDF 1.5 `/Type /XRef` cross-reference stream with a page dictionary inside an `/Type /ObjStm` — the shape every modern writer emits |
| `broken-startxref.pdf` | `minimal-text.pdf` with `startxref` pointing past EOF |

`flate-content.stream` is the one Flate path that cannot be spelled in ASCII; the test project
deflates it.

### `corpus/docx/` — eleven OPC part trees

Each directory holds `[Content_Types].xml`, `_rels/.rels`, `word/document.xml` and whatever else
that fixture needs; 47 parts in total. The expected extraction output for each tree is pinned by
`DocxExtractorTests`, not by a digest — these are indented XML a reviewer is expected to read.

| Tree | What it proves |
|---|---|
| `headings` | `w:outlineLvl` on the paragraph resolves h1/h2/h3 |
| `run-split` | one sentence across five `w:r` with rsid noise is **one** paragraph block |
| `table` | `TableRow` blocks, pipe-joined, header row first |
| `numbered-list` | `ListItem` blocks, and `w:lvlText` is not rendered into the text |
| `footnotes` | notes included when `IncludeNotes`, appended after the body |
| `header-footer` | running headers and footers are **excluded** by default |
| `hyperlink` | link text survives; the target never becomes body text |
| `textbox` | `w:txbxContent` is reached |
| `empty-body` | a self-closing `<w:body />` yields zero blocks and no throw |
| `unknown-style` | a `pStyle` naming an absent style degrades to a paragraph |
| `localised-style` | the outline-level path, not a `Heading{n}` regex — neither style id nor name contains `Heading` |

## `make_pdf_fixtures.py`

The regeneration path for `corpus/pdf/`. It is **never a build step and CI never runs it**. It needs
nothing but CPython 3.11 or newer — no venv, no third-party module (`requirements.txt` says so and
is otherwise empty).

```
python ingestion/tests/fixtures/make_pdf_fixtures.py
```

Run it by hand only when a fixture's content must change, regenerate `manifest.json` in the same
commit, and **put the reason in the PR body**.

## `manifest.json`

Every file under `corpus/`, with its byte length and SHA-256. It turns "somebody regenerated and
the bytes moved" into a failing test rather than a surprise — `FixtureDriftTests` re-runs the same
check from the embedded resources, so the guard holds on a device too. Regenerate it **only** in a
PR whose body says why the corpus changed.

## `realworld-corpus.json`

Owner input for the real-world document lane (spec §14.5): documents produced by actual Word,
LibreOffice and Acrobat, fetched by pinned SHA-256 by the nightly workflow and **never committed**
— only their URL and digest live here. The `documents` array ships empty, which is a *declared*
unfinished state: the nightly fetch annotates every run while it is empty, and the coverage test
skips with the printed reason "spec 14.5 is owed".

## Golden generation

<!-- Filled by Task 4.1 Step 6 (wave 4): which vocabulary the twenty goldens were generated
     against and its SHA-256, the MiniLM triple 222/32/27 they pin, and the
     QAVREN_EDGE_WRITE_GOLDEN rule. Do not delete this heading; Task 4.1 replaces this comment
     and nothing else in this file. -->
