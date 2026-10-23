#!/usr/bin/env python3
"""Generates Karry's Grafana dashboards (US-P1-OPS-01 AC2) as JSON files next to this script.

Dashboards are code: edit this file, run it, commit both. CI fails if the JSON differs from the generator output
(`--check`), and the local-stack smoke test runs every query against Prometheus.

Metric names are what OpenTelemetry .NET produces after Prometheus OTLP translation (dots → underscores, unit and
`_total` suffixes): verified against the local stack on 2026-10-23.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
PROM = {"type": "prometheus", "uid": "prometheus"}
API = 'job="karry-api", http_route!~"/health/.*", module=~"$module"'


def target(expr: str, legend: str, ref: str = "A") -> dict:
    return {"datasource": PROM, "expr": expr, "legendFormat": legend, "refId": ref}


def panel(pid: int, title: str, description: str, targets: list[dict], unit: str, x: int, y: int,
          w: int = 12, h: int = 8, thresholds: list[tuple[float, str]] | None = None) -> dict:
    steps = [{"color": "green", "value": None}] + [{"color": c, "value": v} for v, c in (thresholds or [])]
    return {
        "id": pid,
        "type": "timeseries",
        "title": title,
        "description": description,
        "datasource": PROM,
        "gridPos": {"x": x, "y": y, "w": w, "h": h},
        "targets": targets,
        "fieldConfig": {
            "defaults": {
                "unit": unit,
                "thresholds": {"mode": "absolute", "steps": steps},
                "custom": {"thresholdsStyle": {"mode": "line" if thresholds else "off"}},
            },
            "overrides": [],
        },
        "options": {"legend": {"displayMode": "table", "placement": "bottom", "calcs": ["mean", "max"]},
                    "tooltip": {"mode": "multi"}},
    }


def dashboard(uid: str, title: str, description: str, panels: list[dict], templating: list[dict]) -> dict:
    return {
        "uid": uid,
        "title": title,
        "description": description,
        "tags": ["karry"],
        "editable": False,
        "schemaVersion": 41,
        "time": {"from": "now-1h", "to": "now"},
        "refresh": "30s",
        "timezone": "utc",
        "templating": {"list": templating},
        "panels": panels,
    }


MODULE_VARIABLE = {
    "name": "module",
    "label": "Module",
    "type": "query",
    "datasource": PROM,
    "query": {"query": 'label_values(http_server_request_duration_seconds_count{job="karry-api"}, module)', "refId": "module"},
    "definition": 'label_values(http_server_request_duration_seconds_count{job="karry-api"}, module)',
    "includeAll": True,
    "multi": True,
    "current": {"text": "All", "value": "$__all"},
    "refresh": 2,
}


def api_red() -> dict:
    rate = f"rate(http_server_request_duration_seconds_count{{{API}}}[$__rate_interval])"
    errors = f'rate(http_server_request_duration_seconds_count{{{API}, http_response_status_code=~"5.."}}[$__rate_interval])'
    buckets = f"rate(http_server_request_duration_seconds_bucket{{{API}}}[$__rate_interval])"
    return dashboard(
        "karry-api-red", "Karry · API (RED per module)",
        "Rate, errors and duration of karry-api per module (health probes excluded). Alerts: KarryApiErrorRateHigh, KarryApiLatencyP95High.",
        [
            panel(1, "Request rate by module", "Requests per second.", [target(f"sum by (module) ({rate})", "{{module}}")], "reqps", 0, 0),
            panel(2, "Error ratio (5xx) by module", "Share of responses with a 5xx status. Alert above 2 % for 5 min.",
                  [target(f"sum by (module) ({errors}) / sum by (module) ({rate})", "{{module}}")], "percentunit", 12, 0,
                  thresholds=[(0.02, "red")]),
            panel(3, "p95 latency by module", "95th percentile of request duration. Alert above 1 s for 10 min.",
                  [target(f"histogram_quantile(0.95, sum by (le, module) ({buckets}))", "{{module}}")], "s", 0, 8,
                  thresholds=[(1, "red")]),
            panel(4, "p95 latency by route (top 10)", "Slowest route templates.",
                  [target(f"topk(10, histogram_quantile(0.95, sum by (le, http_route) ({buckets})))", "{{http_route}}")], "s", 12, 8),
            panel(5, "Responses by status code", "Requests per second by HTTP status.",
                  [target(f"sum by (http_response_status_code) ({rate})", "{{http_response_status_code}}")], "reqps", 0, 16, w=24),
        ],
        [MODULE_VARIABLE],
    )


def platform() -> dict:
    return dashboard(
        "karry-platform", "Karry · Platform (database pool, outbox, runtime)",
        "PostgreSQL connection pool (Npgsql), outbox job lag and .NET runtime of karry-api and karry-workers. Alert: KarryOutboxLagHigh.",
        [
            panel(1, "DB connections by state", "Npgsql pool: connections in use and idle, against the pool maximum.",
                  [target("sum by (service_name, db_client_connection_state) (db_client_connection_count)", "{{service_name}} {{db_client_connection_state}}"),
                   target("sum by (service_name) (db_client_connection_max)", "{{service_name}} max", "B")], "short", 0, 0),
            panel(2, "DB command p95 duration", "95th percentile of database operations.",
                  [target("histogram_quantile(0.95, sum by (le, service_name) (rate(db_client_operation_duration_seconds_bucket[$__rate_interval])))", "{{service_name}}")],
                  "s", 12, 0),
            panel(3, "Outbox lag (oldest pending message)", "Age of the oldest event not yet delivered to its subscribers. Alert above 5 min.",
                  [target("max by (module) (karry_outbox_oldest_pending_age_seconds)", "{{module}}")], "s", 0, 8,
                  thresholds=[(300, "red")]),
            panel(4, "Outbox messages by outcome", "Messages per second: processed, retried, dead_lettered.",
                  [target("sum by (module, outcome) (rate(karry_outbox_messages_total[$__rate_interval]))", "{{module}} {{outcome}}")], "short", 12, 8),
            panel(5, "Process CPU", "CPU cores used by each host.",
                  [target("sum by (service_name) (rate(dotnet_process_cpu_time_seconds_total[$__rate_interval]))", "{{service_name}}")], "short", 0, 16),
            panel(6, "Working set and GC heap", "Process memory and managed heap after the last collection.",
                  [target("sum by (service_name) (dotnet_process_memory_working_set_bytes)", "{{service_name}} working set"),
                   target("sum by (service_name) (dotnet_gc_last_collection_heap_size_bytes)", "{{service_name}} GC heap", "B")], "bytes", 12, 16),
        ],
        [],
    )


def render() -> dict[str, str]:
    return {f"{d['uid']}.json": json.dumps(d, indent=2) + "\n" for d in (api_red(), platform())}


def main() -> int:
    files = render()
    if "--check" in sys.argv:
        stale = [name for name, text in files.items() if not (HERE / name).exists() or (HERE / name).read_text() != text]
        if stale:
            print(f"Dashboards out of date (run {Path(__file__).name}): {', '.join(stale)}", file=sys.stderr)
            return 1
        print(f"{len(files)} dashboards up to date")
        return 0
    for name, text in files.items():
        (HERE / name).write_text(text)
    print(f"wrote {', '.join(files)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
