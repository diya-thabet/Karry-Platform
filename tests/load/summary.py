"""Markdown table of a load run (tests/load/run.sh): the numbers that decide H-L1, TC-P1-TEN-005 and the spike."""
import json
import pathlib
import sys

ROWS = [
    ("devices", "Burst: 500 snapshots in one second", "http_req_duration{scenario:burst}", "burst_pulls_ok"),
    ("devices", "Shift: tablets over a minute, deltas every 2-4 s", "http_req_duration{scenario:shift}", "shift_pulls_ok"),
    ("tenants-mixed", "TC-P1-TEN-005: two companies, mixed reads", "http_req_duration{scenario:mixed}", "answers_checked"),
    ("tenants-spike", "Spike: 0 to 300 users in 10 s", "http_req_duration", "answers_checked"),
]


def main(folder: str) -> None:
    out = pathlib.Path(folder)
    print("| Run | Requests | p95 (ms) | Failed | Leaks / wrong site | Thresholds |")
    print("|---|---|---|---|---|---|")
    for file, label, duration, count in ROWS:
        path = out / f"{file}.json"
        if not path.exists():
            print(f"| {label} | not run | | | | |")
            continue
        metrics = json.loads(path.read_text())["metrics"]
        p95 = metrics.get(duration, {}).get("p(95)")
        failed = metrics.get("http_req_failed", {}).get("value", 0)
        leaks = metrics.get("cross_tenant_leaks", metrics.get("wrong_site", {})).get("count", 0)
        # In k6's summary export a threshold's value says whether it was crossed (true = failed).
        crossed = [name for name, m in metrics.items() for t in (m.get("thresholds") or {}).values() if t is True]
        print(f"| {label} | {metrics.get(count, {}).get('count', 0):,} | {p95:.0f} | {failed:.2%} | {leaks} | {'✅' if not crossed else '❌ ' + ', '.join(crossed)} |")


if __name__ == "__main__":
    main(sys.argv[1])
