#!/usr/bin/env python3
"""End-to-end check of local observability (P1.4 "done when"), run by deploy/docker/smoke-test.sh.

Given the correlation ID of a request already sent to Karry.Host, proves through Grafana (its datasource proxy) that:
the trace is in Tempo with the 4 Karry attributes, RED metrics with a module label are in Prometheus, logs are in
Loki, both Karry dashboards are provisioned and every dashboard query is valid PromQL, and the 5 alert rules are
loaded and healthy. Standard library only.

Usage: verify_stack.py <correlation-id> [--grafana http://127.0.0.1:3000] [--timeout 90]
"""
from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

DASHBOARDS = Path(__file__).resolve().parent / "dashboards"
EXPECTED_ALERTS = {
    "KarryApiErrorRateHigh", "KarryApiLatencyP95High", "KarryOutboxLagHigh",
    "KarryDatabaseCpuHigh", "KarryCertificateExpiringSoon",
}
REQUIRED_SPAN_ATTRIBUTES = {"tenant_id", "module", "route", "user_role"}


def get(url: str) -> dict:
    with urllib.request.urlopen(url, timeout=10) as response:  # noqa: S310 - local Grafana only
        return json.load(response)


def eventually(name: str, probe, timeout: float) -> bool:
    """Retries probe() until it returns a truthy value: telemetry is exported in batches (traces 5 s, metrics 10 s)."""
    deadline = time.monotonic() + timeout
    last_error = ""
    while time.monotonic() < deadline:
        try:
            if probe():
                print(f"PASS  {name}")
                return True
        except (urllib.error.URLError, KeyError, ValueError, TimeoutError) as error:
            last_error = f" ({error})"
        time.sleep(3)
    print(f"FAIL  {name}{last_error}", file=sys.stderr)
    return False


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("correlation_id")
    parser.add_argument("--grafana", default="http://127.0.0.1:3000")
    parser.add_argument("--timeout", type=float, default=90)
    args = parser.parse_args()
    proxy = f"{args.grafana}/api/datasources/proxy/uid"

    def prom(query: str) -> dict:
        return get(f"{proxy}/prometheus/api/v1/query?" + urllib.parse.urlencode({"query": query}))

    def trace_has_karry_attributes() -> bool:
        trace = get(f"{proxy}/tempo/api/traces/{args.correlation_id}")
        for batch in trace.get("batches", trace.get("resourceSpans", [])):
            for scope in batch.get("scopeSpans", []):
                for span in scope["spans"]:
                    keys = {attribute["key"] for attribute in span.get("attributes", [])}
                    if span.get("kind") in ("SPAN_KIND_SERVER", 2) and REQUIRED_SPAN_ATTRIBUTES <= keys:
                        return True
        return False

    def red_metrics_by_module() -> bool:
        result = prom('sum by (module) (http_server_request_duration_seconds_count{job="karry-api", module="host"})')
        return bool(result["data"]["result"])

    def logs_in_loki() -> bool:
        query = urllib.parse.urlencode({"query": '{service_name="karry-api"}', "limit": "1"})
        return bool(get(f"{proxy}/loki/loki/api/v1/query_range?{query}")["data"]["result"])

    def dashboards_provisioned() -> bool:
        return all(get(f"{args.grafana}/api/dashboards/uid/{path.stem}")["dashboard"]["uid"] == path.stem
                   for path in DASHBOARDS.glob("*.json"))

    def dashboard_queries_valid() -> bool:
        for path in sorted(DASHBOARDS.glob("*.json")):
            for panel in json.loads(path.read_text())["panels"]:
                for target in panel["targets"]:
                    query = target["expr"].replace("$__rate_interval", "1m").replace("$module", ".*")
                    if prom(query)["status"] != "success":
                        raise ValueError(f"{path.name} panel '{panel['title']}': {query}")
        return True

    def alert_rules_loaded() -> bool:
        groups = get(f"{proxy}/prometheus/api/v1/rules")["data"]["groups"]
        rules = {rule["name"]: rule for group in groups for rule in group["rules"]}
        missing = EXPECTED_ALERTS - rules.keys()
        unhealthy = [name for name in EXPECTED_ALERTS & rules.keys() if rules[name]["health"] not in ("ok", "unknown")]
        if missing or unhealthy:
            raise ValueError(f"missing {sorted(missing)}, unhealthy {unhealthy}")
        return True

    checks = [
        ("trace of the request in Tempo with tenant_id, module, route, user_role", trace_has_karry_attributes),
        ("RED metrics with a module label in Prometheus", red_metrics_by_module),
        ("karry-api logs in Loki", logs_in_loki),
        ("Karry dashboards provisioned in Grafana", dashboards_provisioned),
        ("every dashboard query is valid PromQL", dashboard_queries_valid),
        ("5 alert rules loaded and healthy", alert_rules_loaded),
    ]
    failures = sum(not eventually(name, probe, args.timeout) for name, probe in checks)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
