#!/usr/bin/env python3
"""Bundle budget of the field app (llm_guidelines.md §13, non-functional requirements).

The JavaScript a tablet downloads before the first screen, gzipped, must stay under the budget (250 KB): the entry
script of `index.html` and every module it preloads. Chunks loaded later (a language's catalog, a screen opened on
demand) do not count: the service worker fetches them in the background.

Usage: bundle_budget.py DIST_DIR [--max-kb 250] [--summary FILE]
Exit code 0 = within budget, 1 = over budget, 2 = usage error (no index.html, no script).
"""
from __future__ import annotations

import argparse
import gzip
import re
import sys
from html.parser import HTMLParser
from pathlib import Path


class _InitialScripts(HTMLParser):
    """The module entry scripts and the modules they preload, in document order."""

    def __init__(self) -> None:
        super().__init__()
        self.paths: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = dict(attrs)
        if tag == "script" and values.get("type") == "module" and values.get("src"):
            self.paths.append(values["src"] or "")
        elif tag == "link" and values.get("rel") == "modulepreload" and values.get("href"):
            self.paths.append(values["href"] or "")


def initial_scripts(dist: Path) -> list[Path]:
    parser = _InitialScripts()
    parser.feed((dist / "index.html").read_text(encoding="utf-8"))
    return [dist / re.sub(r"^/", "", path) for path in dict.fromkeys(parser.paths)]


def gzipped_size(path: Path) -> int:
    return len(gzip.compress(path.read_bytes(), compresslevel=9))


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("dist", type=Path)
    parser.add_argument("--max-kb", type=float, default=250)
    parser.add_argument("--summary", type=Path, help="append a Markdown line (GitHub step summary)")
    args = parser.parse_args(argv)
    if not (args.dist / "index.html").is_file():
        print(f"{args.dist}/index.html not found: build the app first", file=sys.stderr)
        return 2

    scripts = initial_scripts(args.dist)
    if not scripts:
        print("index.html loads no module script", file=sys.stderr)
        return 2

    total = sum(gzipped_size(script) for script in scripts) / 1024
    within = total <= args.max_kb
    line = f"{'✅' if within else '❌'} initial JS {total:.1f} KB gzipped (budget {args.max_kb:g} KB), {len(scripts)} file(s)"
    print(line)
    if args.summary:
        with args.summary.open("a", encoding="utf-8") as summary:
            summary.write(f"{line}\n")
    return 0 if within else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
