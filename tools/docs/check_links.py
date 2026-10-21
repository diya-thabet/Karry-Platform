#!/usr/bin/env python3
"""Validate relative Markdown links and #anchors in the repository docs.

Usage: python3 tools/docs/check_links.py [repo_root]
Exit code 1 if any link target or anchor is missing.
"""
import os
import re
import sys

ROOT = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else ".")
LINK = re.compile(r"(?<!!)\[[^\]]*\]\(([^)\s]+)\)")
SKIP_DIRS = {".git", "node_modules", "bin", "obj", ".venv"}


def slugify(heading: str) -> str:
    s = heading.strip().lower()
    s = re.sub(r"[^\w\- ]", "", s, flags=re.UNICODE)
    return s.replace(" ", "-")


def anchors(path: str) -> set:
    found, counts, fence = set(), {}, False
    for line in open(path, encoding="utf-8"):
        if line.startswith("```"):
            fence = not fence
        if fence:
            continue
        m = re.match(r"^#{1,6} (.+?)\s*$", line)
        if m:
            base = slugify(m.group(1))
            n = counts.get(base, 0)
            found.add(base if n == 0 else f"{base}-{n}")
            counts[base] = n + 1
    return found


def md_files():
    for d, dirs, files in os.walk(ROOT):
        dirs[:] = [x for x in dirs if x not in SKIP_DIRS]
        for f in files:
            if f.endswith(".md"):
                yield os.path.join(d, f)


def main() -> int:
    errors, cache = [], {}
    for path in md_files():
        fence = False
        for no, line in enumerate(open(path, encoding="utf-8"), 1):
            if line.startswith("```"):
                fence = not fence
            if fence:
                continue
            for target in LINK.findall(re.sub(r"`[^`]*`", "", line)):  # ignore inline code
                if re.match(r"^[a-z]+:", target):  # http:, https:, mailto:
                    continue
                file_part, _, anchor = target.partition("#")
                dest = os.path.normpath(os.path.join(os.path.dirname(path), file_part)) if file_part else path
                rel = os.path.relpath(path, ROOT)
                if not os.path.exists(dest):
                    errors.append(f"{rel}:{no}: missing target {target}")
                    continue
                if anchor and dest.endswith(".md"):
                    if dest not in cache:
                        cache[dest] = anchors(dest)
                    if anchor not in cache[dest]:
                        errors.append(f"{rel}:{no}: missing anchor #{anchor} in {os.path.relpath(dest, ROOT)}")
    for e in errors:
        print(e)
    print(f"{len(errors)} broken link(s)")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
