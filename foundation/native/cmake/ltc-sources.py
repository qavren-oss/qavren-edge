"""Emit a CMake-style ;-separated source list for libtomcrypt from its own makefile.

Usage: python ltc-sources.py <libtomcrypt-root>
"""
import os
import re
import sys

EXCLUDE_PREFIXES = ("src/pk/", "src/math/", "src/encauth/", "src/headers/")

def main() -> int:
    root = sys.argv[1]
    makefile = os.path.join(root, "makefile")
    with open(makefile, "r", encoding="utf-8", errors="replace") as fh:
        text = fh.read()

    objects = re.findall(r"(src/[A-Za-z0-9_./-]+)\.o\b", text)
    seen, out = set(), []
    for obj in objects:
        rel = obj + ".c"
        if rel.startswith(EXCLUDE_PREFIXES) or rel in seen:
            continue
        if not os.path.isfile(os.path.join(root, rel)):
            continue
        seen.add(rel)
        out.append(os.path.join(root, rel).replace("\\", "/"))

    if len(out) < 50:
        print("ltc-sources: refusing to emit %d sources; makefile parse looks wrong" % len(out),
              file=sys.stderr)
        return 1

    sys.stdout.write(";".join(out))
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
