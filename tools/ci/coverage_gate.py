#!/usr/bin/env python3
"""Coverage gate (US-P1-PLT-03 AC2, llm_guidelines.md §7.2).

Merges Cobertura reports from every test project (a line counts as covered if any report hit it) and fails
when a gated assembly falls below its threshold. Gated by default: every `*.Domain` assembly at 80 % lines.

An assembly that no test loads does not appear in any report at all. With --solution, every gated project
listed in the solution must appear; a missing one counts as 0 % (untested) and fails the gate.

Usage: coverage_gate.py [--pattern REGEX] [--threshold PCT] [--solution Karry.slnx] [--summary FILE] report.cobertura.xml ...
Exit code 0 = gate holds, 1 = below threshold, 2 = usage error.
"""
from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from dataclasses import dataclass
from pathlib import PurePosixPath


@dataclass(frozen=True)
class AssemblyCoverage:
    name: str
    covered: int
    total: int

    @property
    def percent(self) -> float:
        return 100.0 if self.total == 0 else 100.0 * self.covered / self.total


def merge(reports: list[str], pattern: re.Pattern[str]) -> list[AssemblyCoverage]:
    # (assembly, file, line) -> covered by at least one report
    lines: dict[str, dict[tuple[str, int], bool]] = defaultdict(dict)
    for report in reports:
        # Reports are produced by our own test run in the same CI job (trusted input), and Python's expat parser
        # does not resolve external entities; a defusedxml dependency is not worth it here.
        root = ET.parse(report).getroot()  # nosemgrep: python.lang.security.use-defused-xml-parse.use-defused-xml-parse
        for package in root.iter("package"):
            name = package.get("name", "")
            if not pattern.search(name):
                continue
            for cls in package.iter("class"):
                filename = cls.get("filename", "")
                for line in cls.iter("line"):
                    key = (filename, int(line.get("number", "0")))
                    hit = int(line.get("hits", "0")) > 0
                    lines[name][key] = lines[name].get(key, False) or hit
    return [
        AssemblyCoverage(name, sum(1 for hit in hits.values() if hit), len(hits))
        for name, hits in sorted(lines.items())
    ]


def solution_projects(solution: str, pattern: re.Pattern[str]) -> list[str]:
    """Project names (file stems) in a .slnx solution that match the gated pattern."""
    root = ET.parse(solution).getroot()  # nosemgrep: python.lang.security.use-defused-xml-parse.use-defused-xml-parse
    names = (PurePosixPath(p.get("Path", "").replace("\\", "/")).stem for p in root.iter("Project"))
    return sorted(name for name in names if pattern.search(name))


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("reports", nargs="+")
    parser.add_argument("--pattern", default=r"\.Domain$", help="regex on assembly names to gate")
    parser.add_argument("--threshold", type=float, default=80.0, help="minimum line coverage in percent")
    parser.add_argument("--summary", help="append a Markdown table to this file (e.g. $GITHUB_STEP_SUMMARY)")
    parser.add_argument("--solution", help="require every matching project of this .slnx to appear in the reports")
    args = parser.parse_args(argv)

    pattern = re.compile(args.pattern)
    results = merge(args.reports, pattern)
    if args.solution:
        reported = {r.name for r in results}
        # Not loaded by any test: no report line at all. Count as entirely uncovered (1 line, 0 covered).
        results += [AssemblyCoverage(name, 0, 1) for name in solution_projects(args.solution, pattern) if name not in reported]
        results.sort(key=lambda r: r.name)
    failing = [r for r in results if r.percent < args.threshold]

    table = ["| Assembly | Lines covered | Coverage | Gate |", "|---|---|---|---|"]
    for r in results:
        verdict = "❌" if r in failing else "✅"
        lines = f"{r.covered}/{r.total}" if not (r.covered == 0 and r.total == 1 and args.solution) else "not exercised by any test"
        table.append(f"| {r.name} | {lines} | {r.percent:.1f} % | {verdict} ≥ {args.threshold:g} % |")
    if not results:
        table.append(f"| (no assembly matches `{args.pattern}` yet) | | | ✅ |")
    text = "\n".join(["### Coverage gate", "", *table, ""])
    print(text)
    if args.summary:
        with open(args.summary, "a", encoding="utf-8") as fh:
            fh.write(text + "\n")

    if failing:
        names = ", ".join(f"{r.name} ({r.percent:.1f} %)" for r in failing)
        print(f"Coverage gate FAILED: {names} below {args.threshold:g} %", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
