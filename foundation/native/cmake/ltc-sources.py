"""Emit a CMake-style ;-separated source list for libtomcrypt from its own makefile.

The list stays *derived* from upstream: `makefile` is read, every `include <file>` it
names is read too (1.18.2 keeps the real `OBJECTS=` list in `makefile_include.mk`), and
every `src/....o` target found in the union is turned back into a `.c` path. Never
hand-write a file list here - a pinned-version bump must not silently drop a source.

Usage: python ltc-sources.py <libtomcrypt-root>
"""
import os
import re
import sys

# Public-key, bignum and the AEAD modes SQLCipher does not use. src/pk and src/math drag in
# libtommath (unresolved ltc_mp and friends); src/headers holds no translation units.
# src/modes/lrw is excluded for the same reason src/encauth is: SQLCipher only ever runs
# AES-256-CBC, and lrw_start() is the one caller of gcm_gf_mult/gcm_shift_table, which live
# under the already-excluded src/encauth/gcm (LNK2019 at link time otherwise).
EXCLUDE_PREFIXES = ("src/pk/", "src/math/", "src/encauth/", "src/headers/", "src/modes/lrw/")

MIN_SOURCES = 50


def read_makefiles(root: str) -> str:
    """Return `makefile` plus every makefile fragment it includes."""
    pending = ["makefile"]
    seen: set[str] = set()
    chunks: list[str] = []

    while pending:
        name = pending.pop(0)
        if name in seen:
            continue
        seen.add(name)
        path = os.path.join(root, name)
        if not os.path.isfile(path):
            continue
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            text = fh.read()
        chunks.append(text)
        for included in re.findall(r"^\s*(?:-)?include\s+(\S+)\s*$", text, re.MULTILINE):
            if "$" not in included:
                pending.append(included)

    return "\n".join(chunks)


def main() -> int:
    root = sys.argv[1]
    text = read_makefiles(root)

    objects = re.findall(r"(src/[A-Za-z0-9_./-]+)\.o\b", text)
    seen, out = set(), []
    for obj in objects:
        rel = obj + ".c"
        if rel.startswith(EXCLUDE_PREFIXES) or rel in seen:
            continue
        # aes_enc.o has no aes_enc.c - it is aes.c rebuilt with -DENCRYPT_ONLY, which the
        # single-DLL build does not need. The isfile guard drops targets like it.
        if not os.path.isfile(os.path.join(root, rel)):
            continue
        seen.add(rel)
        out.append(os.path.join(root, rel).replace("\\", "/"))

    if len(out) < MIN_SOURCES:
        print("ltc-sources: refusing to emit %d sources; makefile parse looks wrong" % len(out),
              file=sys.stderr)
        return 1

    sys.stdout.write(";".join(out))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
