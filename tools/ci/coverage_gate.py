#!/usr/bin/env python3
"""Coverage gate (US-P1-PLT-03 AC2, llm_guidelines.md §7.2).

Merges Cobertura reports from every test project (a line counts as covered if any report hit it) and fails
when a gated assembly falls below its threshold, or (with --total-threshold) when all gated assemblies together
fall below theirs. Gated by default: every `*.Domain` assembly at 80 % lines.

Only hand-written code counts: files the compiler generates (`*.g.cs`, `*.generated.cs`, anything under `obj/`,
such as the OpenAPI XML-comment support every Api assembly gets) and EF Core migrations (`Migrations/`) are left
out with --exclude-files, so a percentage says how much of what people wrote is tested.

An assembly that no test loads does not appear in any report at all. With --solution, every gated project
listed in the solution must appear (under its <AssemblyName> when its project file sets one); a missing one
counts as 0 % (untested) and fails the gate.

Usage: coverage_gate.py [--pattern REGEX] [--threshold PCT] [--total-threshold PCT] [--exclude-files REGEX]
                        [--solution Karry.slnx] [--summary FILE] report.cobertura.xml ...
Exit code 0 = gate holds, 1 = below threshold, 2 = usage error.
"""
from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path, PurePosixPath

# Compiler output (source generators, obj/) and EF Core migrations (scaffolded by `dotnet ef`; running them in every
# test database would otherwise lift the total with code nobody wrote).
GENERATED = r"(\.g\.cs|\.generated\.cs)$|(^|[\\/])(obj|Migrations)[\\/]"


@dataclass(frozen=True)
class AssemblyCoverage:
    name: str
    covered: int
    total: int

    @property
    def percent(self) -> float:
        return 100.0 if self.total == 0 else 100.0 * self.covered / self.total


def merge(reports: list[str], pattern: re.Pattern[str], exclude: re.Pattern[str] | None = None) -> list[AssemblyCoverage]:
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
                if exclude is not None and exclude.search(filename):
                    continue
                for line in cls.iter("line"):
                    key = (filename, int(line.get("number", "0")))
                    hit = int(line.get("hits", "0")) > 0
                    lines[name][key] = lines[name].get(key, False) or hit
    return [
        AssemblyCoverage(name, sum(1 for hit in hits.values() if hit), len(hits))
        for name, hits in sorted(lines.items())
    ]


def assembly_name(project: Path) -> str:
    """The assembly a project builds: its <AssemblyName> when set (e.g. karry-ledger), else the file stem."""
    if project.is_file():
        root = ET.parse(project).getroot()  # nosemgrep: python.lang.security.use-defused-xml-parse.use-defused-xml-parse
        for element in root.iter("AssemblyName"):
            if element.text and element.text.strip():
                return element.text.strip()
    return project.stem


def solution_projects(solution: str, pattern: re.Pattern[str]) -> list[str]:
    """Assembly names of the projects in a .slnx solution that match the gated pattern."""
    root = ET.parse(solution).getroot()  # nosemgrep: python.lang.security.use-defused-xml-parse.use-defused-xml-parse
    base = Path(solution).parent
    names = (assembly_name(base / PurePosixPath(p.get("Path", "").replace("\\", "/"))) for p in root.iter("Project"))
    return sorted(name for name in names if pattern.search(name))


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("reports", nargs="+")
    parser.add_argument("--pattern", default=r"\.Domain$", help="regex on assembly names to gate")
    parser.add_argument("--threshold", type=float, default=80.0, help="minimum line coverage of each assembly, in percent")
    parser.add_argument("--total-threshold", type=float, help="minimum line coverage of all gated assemblies together, in percent")
    parser.add_argument("--exclude-files", default=GENERATED, help="regex on source file paths left out (default: generated code; '' keeps all)")
    parser.add_argument("--summary", help="append a Markdown table to this file (e.g. $GITHUB_STEP_SUMMARY)")
    parser.add_argument("--solution", help="require every matching project of this .slnx to appear in the reports")
    args = parser.parse_args(argv)

    pattern = re.compile(args.pattern)
    exclude = re.compile(args.exclude_files) if args.exclude_files else None
    results = merge(args.reports, pattern, exclude)
    if args.solution:
        reported = {r.name for r in results}
        # Not loaded by any test: no report line at all. Count as entirely uncovered (1 line, 0 covered).
        results += [AssemblyCoverage(name, 0, 1) for name in solution_projects(args.solution, pattern) if name not in reported]
        results.sort(key=lambda r: r.name)
    failing = [r for r in results if r.percent < args.threshold]
    total = AssemblyCoverage("all gated assemblies", sum(r.covered for r in results), sum(r.total for r in results))
    total_fails = args.total_threshold is not None and total.percent < args.total_threshold

    table = ["| Assembly | Lines covered | Coverage | Gate |", "|---|---|---|---|"]
    for r in results:
        verdict = "❌" if r in failing else "✅"
        lines = f"{r.covered}/{r.total}" if not (r.covered == 0 and r.total == 1 and args.solution) else "not exercised by any test"
        table.append(f"| {r.name} | {lines} | {r.percent:.1f} % | {verdict} ≥ {args.threshold:g} % |")
    if not results:
        table.append(f"| (no assembly matches `{args.pattern}` yet) | | | ✅ |")
    elif args.total_threshold is not None:
        verdict = "❌" if total_fails else "✅"
        table.append(f"| **{total.name}** | {total.covered}/{total.total} | **{total.percent:.1f} %** | {verdict} ≥ {args.total_threshold:g} % |")
    text = "\n".join(["### Coverage gate", "", *table, ""])
    print(text)
    if args.summary:
        with open(args.summary, "a", encoding="utf-8") as fh:
            fh.write(text + "\n")

    if failing:
        names = ", ".join(f"{r.name} ({r.percent:.1f} %)" for r in failing)
        print(f"Coverage gate FAILED: {names} below {args.threshold:g} %", file=sys.stderr)
    if total_fails:
        print(f"Coverage gate FAILED: all gated assemblies together {total.percent:.1f} % below {args.total_threshold:g} %", file=sys.stderr)
    return 1 if failing or total_fails else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
