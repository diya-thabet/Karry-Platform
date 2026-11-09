#!/usr/bin/env python3
"""Threat traceability (hardening plan H-W3): every threat Phase 1 claims to verify is linked to tests that exist.

A threat of the threat model's STRIDE table is *claimed* when its "Verified by" cell names Phase 1 work (`US-P1-…`,
`TC-P1-…`, `P1.n`). Each claimed threat must cite at least one piece of evidence:
- a test case `TC-P1-AREA-NNN` (also written `TC-P1-AREA-001/002` or `TC-P1-AREA-001..004`), found in test code
  (an xUnit `[Trait("TestCase", …)]`, a Vitest or Playwright title, a load or security script);
- a test class written `` `SomethingTests` ``, found as `class SomethingTests` in the C# sources.
Every cited test case and class must exist, so a renamed or deleted test breaks the build instead of the claim.

Usage: threat_traceability.py [--root .] [--summary FILE]
Exit code 0 = traced, 1 = a claim without evidence or a missing test, 2 = usage error (no threat table).
"""
from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

THREAT_MODEL = Path("docs/02-architecture/threat-model.md")
TEST_ROOTS = ("src", "tests")
SKIPPED_DIRS = {"node_modules", "bin", "obj", "dist", ".git", "TestResults"}
TEST_FILE = re.compile(r"(\.test\.tsx?|\.spec\.ts|test_\w+\.py)$")
TEST_DIR = re.compile(r"(Tests|^tests|^e2e)$")  # a test project, or the repository's test folders
CODE = {".cs", ".ts", ".tsx", ".js", ".py", ".sh"}

_ROW = re.compile(r"^\|\s*(T-\d{2})\s*\|(.*)\|\s*$")
_CLAIM = re.compile(r"\b(?:US|TC)-P1-|\bP1\.\d")
_CASE = re.compile(r"TC-P1-([A-Z]+)-(\d{3})((?:/\d{3})*)(?:\.\.(\d{3}))?")
_CLASS = re.compile(r"`([A-Z]\w*Tests)`")
_CASE_IN_CODE = re.compile(r"TC-P1-[A-Z]+-\d{3}")
_CLASS_IN_CODE = re.compile(r"\bclass\s+([A-Z]\w*Tests)\b")


@dataclass
class Threat:
    id: str
    cases: list[str] = field(default_factory=list)
    classes: list[str] = field(default_factory=list)


def expand(match: re.Match[str]) -> list[str]:
    """`TC-P1-AUD-001/002` → 001 and 002; `TC-P1-TEN-001..003` → 001, 002 and 003."""
    area, first, more, last = match.groups()
    numbers = [first, *[n for n in more.split("/") if n]]
    if last:
        numbers = [f"{n:03d}" for n in range(int(first), int(last) + 1)]
    return [f"TC-P1-{area}-{n}" for n in numbers]


def claimed_threats(markdown: str) -> list[Threat] | None:
    """The threats whose last cell names Phase 1 work, with the evidence that cell cites; None without a table."""
    threats: list[Threat] = []
    rows = 0
    for line in markdown.splitlines():
        row = _ROW.match(line)
        if not row:
            continue
        rows += 1
        verified = row.group(2).split("|")[-1]
        if not _CLAIM.search(verified):
            continue
        threat = Threat(row.group(1))
        for match in _CASE.finditer(verified):
            threat.cases.extend(c for c in expand(match) if c not in threat.cases)
        threat.classes = list(dict.fromkeys(_CLASS.findall(verified)))
        threats.append(threat)
    return threats if rows else None


def evidence(root: Path) -> tuple[set[str], set[str]]:
    """The test cases named in test code, and the C# test classes."""
    cases: set[str] = set()
    classes: set[str] = set()
    for top in TEST_ROOTS:
        for path in (root / top).rglob("*"):
            parts = path.relative_to(root).parts
            if not path.is_file() or path.suffix not in CODE or SKIPPED_DIRS.intersection(parts):
                continue
            if not TEST_FILE.search(path.name) and not any(TEST_DIR.search(part) for part in parts[:-1]):
                continue
            text = path.read_text(encoding="utf-8", errors="ignore")
            cases.update(_CASE_IN_CODE.findall(text))
            if path.suffix == ".cs":
                classes.update(_CLASS_IN_CODE.findall(text))
    return cases, classes


def problems(threats: list[Threat], cases: set[str], classes: set[str]) -> list[str]:
    found: list[str] = []
    for threat in threats:
        if not threat.cases and not threat.classes:
            found.append(f"{threat.id}: claimed for Phase 1 but cites no test case (TC-P1-…) or test class (`…Tests`)")
        found += [f"{threat.id}: {case} is cited but no test names it" for case in threat.cases if case not in cases]
        found += [f"{threat.id}: `{name}` is cited but no such test class exists" for name in threat.classes if name not in classes]
    return found


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", type=Path, default=Path("."))
    parser.add_argument("--summary", type=Path, help="append a Markdown report (GitHub step summary)")
    args = parser.parse_args(argv)

    model = args.root / THREAT_MODEL
    threats = claimed_threats(model.read_text(encoding="utf-8")) if model.is_file() else None
    if threats is None:
        print(f"No threat table found in {model}", file=sys.stderr)
        return 2

    cases, classes = evidence(args.root)
    found = problems(threats, cases, classes)
    lines = [f"- **{t.id}**: {', '.join(t.cases + [f'`{c}`' for c in t.classes])}" for t in threats]
    report = "\n".join(["## Threat traceability (H-W3)", "", f"{len(threats)} threats claimed for Phase 1.", "", *lines])
    if found:
        report += "\n\n**Not traced:**\n\n" + "\n".join(f"- {p}" for p in found)
    if args.summary:
        with args.summary.open("a", encoding="utf-8") as summary:
            summary.write(report + "\n")

    for problem in found:
        print(problem, file=sys.stderr)
    print(f"{len(threats)} threats claimed for Phase 1; {len(found)} not traced.")
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main())
