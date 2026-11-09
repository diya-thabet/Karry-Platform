#!/usr/bin/env python3
"""Shift data generator (Phase 2, P2.0): realistic shift entries with meter readings and known anomalies.

N sites × M assets × D days of three 8-hour shifts (06–14, 14–22, 22–06, the night shift crossing midnight). Each
entry has a start and an end reading of the asset's meter (hours or kilometres, unit always explicit, decimals as
strings), device times, a photo hash per reading, the operator and the site's shared tablet.

A configurable share of entries carries one anomaly, chosen among the kinds the field rules must detect:
- `continuity_gap`   start ≠ the previous entry's end, with no meter event (RULE-FO-01);
- `negative_delta`   end < start (RULE-FO-02);
- `oversized_delta`  an hour meter advancing more than the shift length + 0.5 h (RULE-FO-02);
- `clock_skew`       the tablet's clock off by more than 10 minutes (RULE-FO-03);
- `reused_photo`     the end photo is the hash of an earlier photo (RULE-FO-06);
- `missing_end`      the shift was never closed (RULE-FO-08).
`expected_flags` is the ground truth a rule engine must find. `edge_cases` lists what it must NOT flag:
`crosses_midnight`, and `meter_replacement` (a recorded meter event, so the reset to a low reading is legitimate).

Readings chain from the previous entry's recorded end, so each anomaly shows on its own entry only.
The output is deterministic from the seed (same arguments → same bytes), so a failing test can be replayed.

Usage: generate_shifts.py [--seed 1] [--sites 2] [--assets 5] [--days 7] [--start 2026-11-02]
                          [--anomaly-rate 0.05] [--anomalies kind,kind] [--replacement-rate 0.005] [--out FILE]
"""
from __future__ import annotations

import argparse
import json
import random
import sys
import uuid
from datetime import date, datetime, time, timedelta, timezone
from decimal import Decimal
from pathlib import Path

ANOMALIES = ("continuity_gap", "negative_delta", "oversized_delta", "clock_skew", "reused_photo", "missing_end")
SHIFTS = (("morning", time(6)), ("afternoon", time(14)), ("night", time(22)))
SHIFT_HOURS = 8
SKEW_LIMIT = timedelta(minutes=10)  # RULE-FO-03
TENTH = Decimal("0.1")


class Generator:
    def __init__(self, seed: int) -> None:
        self.rng = random.Random(seed)

    def id(self) -> str:
        return str(uuid.UUID(int=self.rng.getrandbits(128), version=7))

    def sha256(self) -> str:
        return f"sha256:{self.rng.getrandbits(256):064x}"

    def amount(self, low: float, high: float) -> Decimal:
        return Decimal(str(round(self.rng.uniform(low, high), 1)))


def iso(moment: datetime) -> str:
    return moment.isoformat(timespec="seconds")


def generate(*, seed: int = 1, sites: int = 2, assets: int = 5, days: int = 7, start: date = date(2026, 11, 2),
             anomaly_rate: float = 0.05, anomalies: tuple[str, ...] = ANOMALIES,
             replacement_rate: float = 0.005) -> dict:
    unknown = set(anomalies) - set(ANOMALIES)
    if unknown:
        raise ValueError(f"unknown anomalies: {', '.join(sorted(unknown))}")
    if not 0 <= anomaly_rate <= 1 or not 0 <= replacement_rate <= 1:
        raise ValueError("rates are between 0 and 1")
    if min(sites, assets, days) < 1:
        raise ValueError("sites, assets and days are at least 1")

    g = Generator(seed)
    zone = timezone.utc  # the pilot sites are on UTC+00:00; the offset is written out in every timestamp
    out_sites, out_assets, entries, events, photos = [], [], [], [], []

    for s in range(sites):
        site = {"id": g.id(), "name": f"Site {s + 1}", "deviceId": g.id(),
                "operators": [g.id() for _ in range(len(SHIFTS) * 2)]}
        out_sites.append(site)
        for a in range(assets):
            hours = a % 3 != 2  # two in three assets have an hour meter, the others an odometer
            asset = {"id": g.id(), "siteId": site["id"], "code": f"S{s + 1}-A{a + 1:02d}",
                     "meter": {"unit": "h" if hours else "km",
                               "initial": str(g.amount(500, 20000) if hours else g.amount(10000, 300000))}}
            out_assets.append(asset)
            meter = Decimal(asset["meter"]["initial"])
            kinds = tuple(k for k in anomalies if hours or k != "oversized_delta")  # only hours have a shift limit

            for d in range(days):
                for n, (shift, begins) in enumerate(SHIFTS):
                    started = datetime.combine(start + timedelta(days=d), begins, zone)
                    ended = started + timedelta(hours=SHIFT_HOURS)
                    kind = g.rng.choice(kinds) if kinds and g.rng.random() < anomaly_rate else None
                    edge = ["crosses_midnight"] if ended.date() != started.date() else []

                    if g.rng.random() < replacement_rate:
                        new = g.amount(0, 5)
                        events.append({"id": g.id(), "assetId": asset["id"], "type": "meter_replacement",
                                       "at": iso(started - timedelta(minutes=30)), "unit": asset["meter"]["unit"],
                                       "oldFinal": str(meter), "newInitial": str(new)})
                        meter = new
                        edge.append("meter_replacement")

                    first = meter
                    if kind == "continuity_gap":
                        first = meter + g.amount(0.5, 20 if hours else 200)
                    if hours:
                        delta = (g.amount(SHIFT_HOURS + 1, SHIFT_HOURS + 6) if kind == "oversized_delta"
                                 else g.amount(4, SHIFT_HOURS))
                    else:
                        delta = g.amount(20, 120)
                    if kind == "negative_delta":
                        delta = -g.amount(0.5, 10)
                    last = first + delta

                    skew = timedelta(seconds=g.rng.randint(-90, 90))
                    if kind == "clock_skew":
                        skew = timedelta(minutes=g.rng.randint(11, 120) * g.rng.choice((-1, 1)))

                    start_photo = g.sha256()
                    end_photo = g.rng.choice(photos) if kind == "reused_photo" and photos else g.sha256()
                    if kind == "reused_photo" and not photos:
                        kind = None  # nothing to reuse yet: the very first entry stays clean
                    photos.append(start_photo)

                    entry = {
                        "id": g.id(), "siteId": site["id"], "assetId": asset["id"], "deviceId": site["deviceId"],
                        "operatorId": site["operators"][(d * len(SHIFTS) + n) % len(site["operators"])],
                        "shift": shift, "date": (start + timedelta(days=d)).isoformat(),
                        "unit": asset["meter"]["unit"],
                        "start": {"reading": str(first), "at": iso(started), "deviceTime": iso(started + skew),
                                  "photo": start_photo},
                        "end": None,
                        "expected_flags": [kind] if kind else [],
                        "edge_cases": edge,
                    }
                    if kind == "missing_end":
                        meter = first + g.amount(4, SHIFT_HOURS) if hours else first + g.amount(20, 120)
                    else:
                        entry["end"] = {"reading": str(last), "at": iso(ended), "deviceTime": iso(ended + skew),
                                        "photo": end_photo}
                        photos.append(end_photo)
                        meter = last
                    entries.append(entry)

    return {"meta": {"generator": "tools/demo/generate_shifts.py", "seed": seed, "sites": sites,
                     "assets": assets, "days": days, "start": start.isoformat(), "anomalyRate": anomaly_rate,
                     "anomalies": list(anomalies), "replacementRate": replacement_rate,
                     "shiftHours": SHIFT_HOURS, "skewLimitMinutes": SKEW_LIMIT.seconds // 60},
            "sites": out_sites, "assets": out_assets, "meterEvents": events, "entries": entries}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--sites", type=int, default=2)
    parser.add_argument("--assets", type=int, default=5)
    parser.add_argument("--days", type=int, default=7)
    parser.add_argument("--start", type=date.fromisoformat, default=date(2026, 11, 2))
    parser.add_argument("--anomaly-rate", type=float, default=0.05)
    parser.add_argument("--anomalies", default=",".join(ANOMALIES), help="comma-separated kinds to draw from")
    parser.add_argument("--replacement-rate", type=float, default=0.005)
    parser.add_argument("--out", type=Path, help="write here instead of standard output")
    args = parser.parse_args(argv)

    try:
        data = generate(seed=args.seed, sites=args.sites, assets=args.assets, days=args.days, start=args.start,
                        anomaly_rate=args.anomaly_rate,
                        anomalies=tuple(k for k in args.anomalies.split(",") if k),
                        replacement_rate=args.replacement_rate)
    except ValueError as error:
        parser.error(str(error))
    text = json.dumps(data, indent=1, sort_keys=True) + "\n"
    if args.out:
        args.out.write_text(text, encoding="utf-8")
        flagged = sum(1 for e in data["entries"] if e["expected_flags"])
        print(f"{len(data['entries'])} entries, {flagged} with an anomaly → {args.out}", file=sys.stderr)
    else:
        sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
