#!/usr/bin/env python3
"""Merge guard: the only way the agent merges a pull request (merge rule D29).

Until GitHub branch protection is available (needs GitHub Pro on a private repository), this script enforces
the same rules client-side:
  1. every required check is present on the PR head and has passed (waits while checks are running);
  2. a PR touching a sensitive area is merged only with --owner-approved (the owner said so explicitly);
  3. squash merge, branch deleted.

Usage: merge_guard.py <pr-number> [--owner-approved] [--dry-run]
"""
from __future__ import annotations

import argparse
import fnmatch
import json
import subprocess
import sys
import time

REQUIRED_CHECKS = ("tooling", "backend", "architecture", "frontend", "local-stack", "security", "links and anchors")

# Sensitive areas (CODEOWNERS, sub-phases §2.3): Tenancy, Identity, Trust, Billing, Sync, ledger migrations, CI security.
SENSITIVE_PATTERNS = (
    "src/backend/Modules/Tenancy/*",
    "src/backend/Modules/Identity/*",
    "src/backend/Modules/Trust/*",
    "src/backend/Modules/Billing/*",
    "src/backend/Karry.BuildingBlocks/*",
    "src/frontend/packages/offline/*",
    "src/frontend/packages/crypto/*",
    "*/Migrations/*ledger*",
    ".github/workflows/*",
    ".github/CODEOWNERS",
    "tools/ci/*",
    "deploy/*",
    "renovate.json",
)

PASSING = {"SUCCESS", "NEUTRAL", "SKIPPED"}
PENDING = {"PENDING", "QUEUED", "IN_PROGRESS", "EXPECTED", "WAITING", "REQUESTED"}


def sensitive_files(paths: list[str]) -> list[str]:
    return [p for p in paths if any(fnmatch.fnmatch(p, pattern) for pattern in SENSITIVE_PATTERNS)]


def evaluate_checks(checks: list[dict[str, str]]) -> tuple[str, list[str]]:
    """Return ("pass" | "pending" | "fail", reasons) for the required checks.

    `checks` items have "name" and "state" (gh pr checks --json name,state). If a check ran several times,
    any failure counts as failure.
    """
    by_name: dict[str, list[str]] = {}
    for check in checks:
        by_name.setdefault(check["name"], []).append(check["state"].upper())

    failures, pending = [], []
    for name in REQUIRED_CHECKS:
        states = by_name.get(name)
        if not states:
            pending.append(f"{name}: not reported yet")
        elif any(s not in PASSING | PENDING for s in states):
            failures.append(f"{name}: {', '.join(states)}")
        elif any(s in PENDING for s in states):
            pending.append(f"{name}: {', '.join(states)}")
    # Any other failing check (e.g. spikes) also blocks.
    for name, states in by_name.items():
        if name not in REQUIRED_CHECKS and any(s not in PASSING | PENDING for s in states):
            failures.append(f"{name}: {', '.join(states)}")

    if failures:
        return "fail", failures
    if pending:
        return "pending", pending
    return "pass", []


def gh(*args: str) -> str:
    return subprocess.run(["gh", *args], check=True, capture_output=True, text=True).stdout


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("pr", type=int)
    parser.add_argument("--owner-approved", action="store_true", help="the owner explicitly approved this PR")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--timeout", type=int, default=1800, help="seconds to wait for checks")
    args = parser.parse_args(argv)

    files = json.loads(gh("pr", "view", str(args.pr), "--json", "files"))["files"]
    touched = sensitive_files([f["path"] for f in files])
    if touched and not args.owner_approved:
        print(f"REFUSED: PR #{args.pr} touches sensitive areas and needs the owner's explicit approval:", file=sys.stderr)
        for path in touched:
            print(f"  {path}", file=sys.stderr)
        return 3

    deadline = time.monotonic() + args.timeout
    while True:
        checks = json.loads(gh("pr", "checks", str(args.pr), "--json", "name,state") or "[]")
        verdict, reasons = evaluate_checks(checks)
        if verdict == "pass":
            break
        if verdict == "fail" or time.monotonic() > deadline:
            print(f"REFUSED: PR #{args.pr} checks are not green ({verdict}):", file=sys.stderr)
            for reason in reasons:
                print(f"  {reason}", file=sys.stderr)
            return 1
        time.sleep(20)

    print(f"PR #{args.pr}: all required checks passed{' (owner-approved sensitive change)' if touched else ''}.")
    if args.dry_run:
        return 0
    title = json.loads(gh("pr", "view", str(args.pr), "--json", "title"))["title"]
    gh("pr", "merge", str(args.pr), "--squash", "--delete-branch", "--subject", f"{title} (#{args.pr})")
    print(f"PR #{args.pr} merged.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
