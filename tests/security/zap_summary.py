#!/usr/bin/env python3
"""Judges the ZAP reports of tests/security/zap.sh (hardening plan H-W1).

Prints one table of every finding (target, risk, rule, where) and fails when a finding is high, or medium without an
entry in the accepted list (`<plugin id> TAB <reason>`; the reason is mandatory, so every exception is explained).
Low and informational findings are listed for the reader and never fail the run.
"""
import json
import pathlib
import sys

RISK = {"3": "High", "2": "Medium", "1": "Low", "0": "Info"}


def accepted(path):
    entries = {}
    for line in pathlib.Path(path).read_text(encoding="utf-8").splitlines():
        if not line.strip() or line.startswith("#"):
            continue
        plugin, _, reason = line.partition("\t")
        if not reason.strip():
            raise SystemExit(f"{path}: plugin {plugin} is accepted without a reason")
        entries[plugin.strip()] = reason.strip()
    return entries


def findings(results):
    for report in sorted(pathlib.Path(results).glob("*.json")):
        for site in json.loads(report.read_text(encoding="utf-8")).get("site", []):
            for alert in site.get("alerts", []):
                yield report.stem, alert


def main(argv):
    results, accepted_path = argv
    allowed = accepted(accepted_path)
    failures = 0
    rows = []
    for target, alert in findings(results):
        risk = alert["riskcode"]
        plugin = alert["pluginid"]
        where = alert.get("instances", [{}])[0].get("uri", "")
        verdict = "ok"
        if risk == "3" or (risk == "2" and plugin not in allowed):
            verdict = "FAIL"
            failures += 1
        elif plugin in allowed:
            verdict = "accepted"
        rows.append((target, RISK.get(risk, risk), plugin, alert["alert"], alert.get("count", ""), where, verdict))
    rows.sort(key=lambda r: (r[6] != "FAIL", r[1] not in ("High", "Medium"), r[0], r[2]))
    print("| Target | Risk | Rule | Finding | Count | Example | Verdict |")
    print("|---|---|---|---|---|---|---|")
    for row in rows:
        print("| " + " | ".join(str(cell) for cell in row) + " |")
    if not rows:
        print("| — | — | — | no findings | — | — | ok |")
    print(f"\n{failures} finding(s) fail the run" if failures else "\nno high or unaccepted medium finding")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
