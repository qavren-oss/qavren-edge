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

`golden/` holds twenty JSON files, one per (fixture x chunker configuration). Each is an array of
`{ index, startChar, endChar, tokenCount, headingPath, breadcrumb, text, embedText }` -- **full
text**, because the fixtures are small and a moved boundary should be legible in the diff rather
than hidden behind a changed hash. Every offset is an index into `ExtractedDocument.Text`, the
normalised buffer.

`breadcrumb` and `embedText` are a **declared superset** of the six fields the plan's Task 4.1
Step 6 lists, and the reason is the `.no-breadcrumb` variants: `PrependHeadingPath` changes the
EMBED text and nothing else, so without those two fields goldens 15-17 are byte-identical to their
`auto` counterparts and record none of what their row of that table claims. The six pinned fields
are all still written, in the pinned order.

### The vocabulary they are pinned against

| Fact | Value |
|---|---|
| Vocabulary | `bert-base-uncased` shape, 30,522 entries, 231,508 bytes |
| SHA-256 | `07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3` |
| Resolved from | `%QAVREN_EDGE_VOCAB%`, then `%QAVREN_EDGE_MODEL_DIR%\vocab.txt`, then `%LOCALAPPDATA%\Temp\qedge-model\vocab.txt` |
| Tokenizer | `EdgeTokenCounter.CreateWordPiece(vocab, 256, lowerCase: true)` -> `MlChunkTokenizer`, `SpecialTokenOverhead = 2` |
| Model profile | `all-minilm-l6-v2-int8`, 384 dims, `MaxSequenceLength` 256, mean pooling, no document prefix |
| **The pinned triple** | **`MaxTokens` 222 / `OverlapTokens` 32 / `MinTokens` 27**, with `HeadingPathTokenBudget` 32 |

`256 - 2 - 32 - 0 = 222`; `222 * 15 / 100 = 33 -> 33 / 8 * 8 = 32`; `222 / 8 = 27`. Both rules
truncate; neither rounds to nearest.

**There is no toy-vocabulary fallback.** A vocabulary whose digest is not the one above FAILS the
suite rather than generating against a substitute -- a golden generated that way pins boundaries no
shipped configuration produces. A lane with no vocabulary at all (a device, where there never will
be one) SKIPS the golden class with a printed reason. Re-provision it with one request, digest
checked, from `sentence-transformers/all-MiniLM-L6-v2` at revision
`1110a243fdf4706b3f48f1d95db1a4f5529b4d41`.

### The twenty, by name

Naming is `<fixture-stem>.<chunker-id>[.<variant>].json`, so a diff's file list alone says what
moved. **12 + 5 + 3 = 20.**

Twelve `auto` goldens, the shipped defaults over every text and Markdown fixture:
`empty.auto.json`, `whitespace-only.auto.json`, `three-paragraphs.auto.json`,
`crlf-and-lone-cr.auto.json`, `long-token.auto.json`, `unicode.auto.json`, `bom.auto.json`,
`headings.auto.json`, `fences.auto.json`, `tables-lists.auto.json`, `raw-html.auto.json`,
`giant-heading-section.auto.json`.

Five option-variant goldens under an explicit `markdown-heading`:
`headings.markdown-heading.no-preamble.json`,
`giant-heading-section.markdown-heading.no-preamble.json`,
`headings.markdown-heading.no-breadcrumb.json`,
`tables-lists.markdown-heading.no-breadcrumb.json`,
`giant-heading-section.markdown-heading.no-breadcrumb.json`.

Three explicit `token-window` goldens, the terminal fallback:
`long-token.token-window.json`, `three-paragraphs.token-window.json`,
`giant-heading-section.token-window.json`.

### What a golden does NOT pin, and where that claim lives instead

A golden records what its fixture actually produces at the pinned triple. Four of the twenty
produce less than the plan's table row for them implies, and every one of those claims is asserted
somewhere -- on synthetic input, the way Markdown rule 6 is -- rather than quietly dropped:

| Golden | The row claims | What the fixture actually does | Where the claim is asserted |
|---|---|---|---|
| `long-token.auto.json`, `long-token.token-window.json` | the hard split with no separator to back up to; the terminal fallback | `long-token.txt` is 5,000 `A`s, and WordPiece collapses a 5,000-char word to ONE `[UNK]` (`max_input_chars_per_word`), so it costs 1 token and never reaches a cut | `ChunkerRuleTests.A_run_with_no_separator_splits_hard_and_every_piece_stays_in_budget` and `.The_token_window_is_the_terminal_fallback_and_carries_its_overlap`, over a 1,860-char run with no whitespace anywhere |
| `three-paragraphs.token-window.json` | the sentence-aware nudge inside the 15% look-back | the fixture costs 38 tokens against a 222-token budget: one chunk, no cut | `ChunkerRuleTests.A_cut_is_nudged_back_to_a_sentence_end_inside_the_look_back_window`, plus `SentenceBoundaryTests` on `FindBackwards` directly |
| `giant-heading-section.markdown-heading.no-breadcrumb.json` | the budget freed by the missing breadcrumb changes the split | `HeadingPathTokenBudget` is subtracted from the sequence length in `ChunkOptions.Resolve` whether or not the breadcrumb is prepended, so `PrependHeadingPath` frees no content budget and the boundaries are identical; the `embedText` field is where the flag shows | the `embedText` column of that golden, and `ChunkerRuleTests.PrependHeadingPath_false_removes_the_breadcrumb_from_the_embed_text_only` |

The corpus is frozen -- `manifest.json` pins every fixture's bytes and `FixtureDriftTests` re-checks
them from the embedded resources -- so a fixture is never edited to make a golden say more.

Four pairs are byte-identical, and each identity is the point rather than an oversight:
`empty` and `whitespace-only` are both `[]`; `giant-heading-section`'s `.no-preamble` equals its
`auto` because that document has no preamble, which is exactly what its row asserts; and the
`long-token` and `three-paragraphs` pairs are the degenerate fixtures in the table above.

### Writing one

Goldens are written **only** under `QAVREN_EDGE_WRITE_GOLDEN=1`, and **the writer refuses to
overwrite a file that already exists**:

```powershell
$env:QAVREN_EDGE_WRITE_GOLDEN = '1'
dotnet run --project ingestion/tests/Qavren.Edge.Ingestion.Tests -c Release -f net10.0
Remove-Item Env:\QAVREN_EDGE_WRITE_GOLDEN
```

A run that rewrites a committed golden is the failure that refusal exists for. **Deleting a golden
is the deliberate act**, and regeneration is its own PR with the reason in the body. The files are
embedded resources, so a freshly written golden is compared only on the next build.
