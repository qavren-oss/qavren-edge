#!/usr/bin/env python3
"""Regenerate ingestion/tests/fixtures/corpus/pdf/*.pdf.

NEVER a build step, and CI never runs this. The OUTPUT is committed; run this by hand only when a
fixture's content must change, and put the reason in the PR body. Requires CPython 3.11 or newer
(bytes.__mod__ formatting); this box has 3.14.5. No third-party module, no venv needed.

Every document here is uncompressed 7-bit ASCII so a reviewer can read the diff. The one Flate
path that cannot be spelled in ASCII is flate-content.stream, which the test project deflates at
a fixed CompressionLevel - DeflateStream is deterministic where PdfPig's trailer /ID is not.
"""
import pathlib
import sys

OUT = pathlib.Path(__file__).parent / "corpus" / "pdf"


def ashex(raw: bytes) -> bytes:
    """ASCIIHexDecode-encode, EOD marker included. This is what keeps a PDF 1.5 file 7-bit."""
    return b"".join(b"%02x" % b for b in raw) + b">"


def xref_row(kind: int, field2: int, field3: int) -> bytes:
    """One /W [1 4 2] cross-reference-stream entry, big-endian as the spec requires."""
    return bytes([kind]) + field2.to_bytes(4, "big") + field3.to_bytes(2, "big")


def build(objects: list[bytes], root_obj: int = 1) -> bytes:
    """Assemble numbered objects into a PDF with a correct classic xref table."""
    out = bytearray(b"%PDF-1.4\n")
    offsets = [0]
    for i, body in enumerate(objects, start=1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % i + body + b"\nendobj\n"
    xref_at = len(out)
    out += b"xref\n0 %d\n" % (len(objects) + 1)
    out += b"0000000000 65535 f \n"
    for off in offsets[1:]:
        out += b"%010d 00000 n \n" % off
    out += b"trailer\n<< /Size %d /Root %d 0 R >>\n" % (len(objects) + 1, root_obj)
    out += b"startxref\n%d\n%%%%EOF\n" % xref_at
    return bytes(out)


def simple(content: bytes, pages: int = 1) -> bytes:
    """One Helvetica Type1 font; `pages` pages sharing one content stream."""
    kids = b" ".join(b"%d 0 R" % (4 + i) for i in range(pages))
    objs = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [%s] /Count %d >>" % (kids, pages),
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ]
    stream_obj = 4 + pages
    for _ in range(pages):
        objs.append(
            b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            b"/Resources << /Font << /F1 3 0 R >> >> /Contents %d 0 R >>" % stream_obj
        )
    objs.append(b"<< /Length %d >>\nstream\n" % len(content) + content + b"\nendstream")
    return build(objs)


def text(lines: list[str], x: int = 72, y: int = 720, leading: int = 14) -> bytes:
    body = ["BT", "/F1 12 Tf", "%d TL" % leading, "%d %d Td" % (x, y)]
    for line in lines:
        escaped = line.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")
        body.append("(%s) Tj T*" % escaped)
    body.append("ET")
    return ("\n".join(body) + "\n").encode("ascii")


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True)

    # 1. minimal-text: the reference shape. Five objects, classic xref, Helvetica Type1.
    (OUT / "minimal-text.pdf").write_bytes(
        simple(text(["Hello from a minimal PDF.", "A second line of the same paragraph."]))
    )

    # 2. two-pages: page numbers must reach DocumentBlock.PageNumber and ChunkDraft.Page.
    (OUT / "two-pages.pdf").write_bytes(
        simple(text(["Page content shared by both pages of this fixture."]), pages=2)
    )

    # 3. two-columns: two text runs at different x in the same y band - reading order.
    left = text(["Left column line one.", "Left column line two."], x=72)
    right = text(["Right column line one.", "Right column line two."], x=330)
    (OUT / "two-columns.pdf").write_bytes(simple(left + right))

    # 4. hyphen-linebreak: JoinHyphenatedLineBreaks must rejoin "extra-" + "ordinary".
    (OUT / "hyphen-linebreak.pdf").write_bytes(
        simple(text(["This paragraph contains an extra-", "ordinary hyphenated line break."]))
    )

    # 5. no-text-layer: a filled rectangle and a tiny inline image, no text operators at all.
    #    Must yield IngestionDocumentStatus.NoTextLayer, never an exception. The four image
    #    bytes are the ONLY non-ASCII bytes in the whole corpus, and Step 7 exempts this file.
    rect = (
        b"0.5 0.5 0.5 rg\n72 600 200 120 re f\n"
        b"q 200 0 0 120 72 400 cm\nBI /W 2 /H 2 /CS /G /BPC 8 ID \x00\x40\x80\xc0 EI Q\n"
    )
    (OUT / "no-text-layer.pdf").write_bytes(simple(rect))

    # 6. xref-stream: a PDF 1.5 cross-reference stream plus an object stream, both
    #    ASCIIHexDecode so the file stays reviewable. See build_xref_stream's docstring.
    (OUT / "xref-stream.pdf").write_bytes(build_xref_stream())

    # 7. broken-startxref: minimal-text with startxref pointing past EOF. The error-path test
    #    asserts DocumentMalformed (6104) and that the run continues.
    broken = bytearray((OUT / "minimal-text.pdf").read_bytes())
    i = broken.rindex(b"startxref\n") + len(b"startxref\n")
    j = broken.index(b"\n", i)
    broken[i:j] = b"999999"
    (OUT / "broken-startxref.pdf").write_bytes(bytes(broken))

    # The Flate content stream, deflated by the TEST at a fixed CompressionLevel.
    (OUT / "flate-content.stream").write_bytes(
        text(["This content stream is stored with FlateDecode.", "Two lines, one paragraph."])
    )

    for p in sorted(OUT.iterdir()):
        print("%-28s %6d" % (p.name, p.stat().st_size))
    return 0


def build_xref_stream() -> bytes:
    """Emit a PDF 1.5 file: a /Type /XRef cross-reference stream plus an object stream, both
    ASCIIHexDecode so the file stays reviewable 7-bit ASCII.

    Written longhand because build() emits a classic xref table. Object layout:

        1  Catalog          plain, type-1 xref entry
        2  Pages            plain, type-1
        3  Page             INSIDE the object stream -> type-2 entry (objstm 6, index 0)
        4  Font             plain, type-1
        5  Content stream   plain, type-1
        6  ObjStm           plain, type-1; holds object 3
        7  XRef stream      plain, type-1, and its own entry points at itself

    There is no trailer dictionary: /Size and /Root live on the XRef stream's own dict, which is
    what a 1.5 file does and what the classic-xref fixtures cannot exercise. /Index is omitted,
    so it defaults to [0 /Size] - every object from 0 to 7, in order, which is the layout below.
    """
    content = text([
        "A PDF 1.5 file whose cross-reference is a stream.",
        "The page dictionary lives in an object stream.",
    ])

    page = (
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        b"/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"
    )
    # An ObjStm's decoded payload is a pair list "objnum offset ..." followed, at byte /First,
    # by the object bodies. One object here, so one pair and an offset of 0.
    pairs = b"3 0\n"
    objstm_plain = pairs + page
    first = len(pairs)

    out = bytearray(b"%PDF-1.5\n")
    offsets: dict[int, int] = {}

    def emit(num: int, body: bytes) -> None:
        offsets[num] = len(out)
        out.extend(b"%d 0 obj\n" % num)
        out.extend(body)
        out.extend(b"\nendobj\n")

    emit(1, b"<< /Type /Catalog /Pages 2 0 R >>")
    emit(2, b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
    emit(4, b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    emit(5, b"<< /Length %d >>\nstream\n" % len(content) + content + b"\nendstream")

    stm = ashex(objstm_plain)
    emit(
        6,
        b"<< /Type /ObjStm /N 1 /First %d /Filter /ASCIIHexDecode /Length %d >>\nstream\n"
        % (first, len(stm))
        + stm
        + b"\nendstream",
    )

    # The XRef stream's own offset is knowable before it is serialised, because it is last - so
    # there is no circularity, only an ordering rule: compute the table, then emit object 7.
    xref_at = len(out)
    table = b"".join(
        [
            xref_row(0, 0, 65535),          # 0: head of the free list
            xref_row(1, offsets[1], 0),
            xref_row(1, offsets[2], 0),
            xref_row(2, 6, 0),              # 3: compressed, in object stream 6 at index 0
            xref_row(1, offsets[4], 0),
            xref_row(1, offsets[5], 0),
            xref_row(1, offsets[6], 0),
            xref_row(1, xref_at, 0),        # 7: this stream, pointing at itself
        ]
    )
    xr = ashex(table)
    emit(
        7,
        b"<< /Type /XRef /Size 8 /W [1 4 2] /Root 1 0 R /Filter /ASCIIHexDecode /Length %d >>\nstream\n"
        % len(xr)
        + xr
        + b"\nendstream",
    )
    out.extend(b"startxref\n%d\n%%%%EOF\n" % xref_at)
    return bytes(out)


if __name__ == "__main__":
    sys.exit(main())
