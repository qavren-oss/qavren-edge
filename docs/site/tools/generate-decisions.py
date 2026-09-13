"""Regenerate docs/site/decisions/{toc.yml,index.md} from the ADR files in each area.

Run from the repository root after adding or renaming an ADR:
    python docs/site/tools/generate-decisions.py
The site's navbar only nests one level, so Decisions is a section with its own TOC: the
nested area groups render in the sidebar and the overview page lists every record.
"""
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[3]
AREAS = (
    ("foundation", "Hosting core and SQLite"),
    ("embeddings", "Embeddings and the vector store"),
    ("ingestion", "Ingestion"),
    ("chat", "Chat and RAG"),
)


def title(path: pathlib.Path) -> str:
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("# "):
            return line[2:].strip()
    return path.stem


def main() -> None:
    out = ROOT / "docs" / "site" / "decisions"
    out.mkdir(exist_ok=True)
    toc = ["- name: Overview", "  href: index.md"]
    index = [
        "# Architecture decision records",
        "",
        "Each area keeps its decisions as numbered records beside the code, in the format ADR 0001",
        "establishes. A record states the context, the decision and its consequences at the time it",
        "was taken; later records supersede rather than edit. The pages below are the records as they",
        "live in the repository.",
        "",
    ]
    count = 0
    for area, label in AREAS:
        toc += [f"- name: {label}", "  items:"]
        index += [f"## {label}", ""]
        for adr in sorted((ROOT / area / "docs" / "adr").glob("*.md")):
            rel = f"../../../{adr.relative_to(ROOT).as_posix()}"
            toc += [f"    - name: {title(adr)}", f"      href: {rel}"]
            index.append(f"- [{title(adr)}]({rel})")
            count += 1
        index.append("")
    (out / "toc.yml").write_text("\n".join(toc) + "\n", encoding="utf-8", newline="\n")
    (out / "index.md").write_text("\n".join(index), encoding="utf-8", newline="\n")
    print(f"decisions: {count} records across {len(AREAS)} areas")


if __name__ == "__main__":
    main()
