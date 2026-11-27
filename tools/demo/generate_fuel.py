#!/usr/bin/env python3
"""Fuel data generator (Phase 3, P3.0): a month of tanks, deliveries, refuels and dips, with known anomalies and answers.

For each site: one fixed tank and one mobile bowser, each with a calibration table (dip cm → litres); a fleet of
machines of three types (loader, excavator, haul truck) with a tank capacity and a typical consumption in litres per
hour; every working day each machine works some hours and is refuelled every one or two days from one of the storages;
deliveries refill a storage when it runs low; every storage is dipped once a day.

Seeded anomalies (the ground truth `expected` lists what the rules must find, and only that):
- `overconsumption`        one machine burns 25 % more from the middle of the month (RULE-FUEL-05);
- `refuel_without_usage`   a refuel with no hours since the previous one (RULE-FUEL-06);
- `frequent_small_refuels` three small refuels of one machine in one shift (RULE-FUEL-08);
- `over_capacity`          a refuel above the machine's tank capacity + 5 % (RULE-FUEL-02);
- `tank_variance`          a day where 150 L are missing from the tank at the dip (RULE-FUEL-04);
- `delivery_variance`      a delivery whose dips show 1.5 % less than the delivery note (RULE-FUEL-01; the short
                           delivery also shows in that day's tank reconciliation, RULE-FUEL-04).

Each seeded entry lists the refuels (`dispense_ids`) and storage days it touches: they, and only they, may be flagged.

The golden answers (consumption intervals, baselines, anomalies, reconciliation per storage and day, delivery checks)
are computed by `fuel_oracle.py`, an independent plain computation, and written under `golden`.

Deterministic from the seed (same arguments → same bytes). Decimals as strings, units explicit.

Usage: generate_fuel.py [--seed 1] [--sites 1] [--machines 8] [--days 30] [--start 2026-11-02] [--out FILE]
"""
from __future__ import annotations

import argparse
import json
import random
import sys
import uuid
from datetime import date, datetime, time, timedelta, timezone
from decimal import ROUND_HALF_EVEN, Decimal
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import fuel_oracle  # noqa: E402

TYPES = {  # litres per hour (typical), machine tank capacity in litres
    "loader": (Decimal("18"), Decimal("400")),
    "excavator": (Decimal("22"), Decimal("500")),
    "haul_truck": (Decimal("30"), Decimal("700")),
}
CENT = Decimal("0.01")
TENTH = Decimal("0.1")


class Generator:
    def __init__(self, seed: int) -> None:
        self.rng = random.Random(seed)

    def id(self) -> str:
        bits = self.rng.getrandbits(128) & ~(0xF << 76) & ~(0x3 << 62)
        return str(uuid.UUID(int=bits | (0x7 << 76) | (0x2 << 62)))

    def sha256(self) -> str:
        return f"sha256:{self.rng.getrandbits(256):064x}"

    def factor(self, spread: float) -> Decimal:
        return Decimal(str(round(self.rng.uniform(1 - spread, 1 + spread), 4)))


def calibration(capacity: Decimal, height_cm: int) -> list[tuple[Decimal, Decimal]]:
    """A horizontal cylinder's shape, approximated by 11 points: slow at the bottom and top, fast in the middle."""
    shape = [0, 0.052, 0.142, 0.252, 0.374, 0.5, 0.626, 0.748, 0.858, 0.948, 1]
    return [(Decimal(height_cm * index) / 10, (capacity * Decimal(str(share))).quantize(CENT)) for index, share in enumerate(shape)]


def dip_for(table: list[tuple[Decimal, Decimal]], litres: Decimal) -> Decimal:
    """The dip (to 0.1 cm) whose litres are closest to `litres`: the inverse of the table, as an attendant would read it."""
    for (cm_low, l_low), (cm_high, l_high) in zip(table, table[1:]):
        if l_low <= litres <= l_high:
            cm = cm_low + (litres - l_low) / (l_high - l_low) * (cm_high - cm_low)
            return cm.quantize(TENTH, rounding=ROUND_HALF_EVEN)
    return table[0][0] if litres < table[0][1] else table[-1][0]


def at(day: date, hour: int, minute: int = 0) -> str:
    return datetime.combine(day, time(hour, minute), tzinfo=timezone.utc).isoformat(timespec="seconds")


def generate(*, seed: int = 1, sites: int = 1, machines: int = 8, days: int = 30, start: date = date(2026, 11, 2)) -> dict:
    g = Generator(seed)
    data: dict = {"seed": seed, "start": start.isoformat(), "days": days, "sites": [], "storages": [], "assets": [],
                  "deliveries": [], "dispenses": [], "dips": [], "seeded": []}
    for site_index in range(sites):
        site_id = g.id()
        data["sites"].append({"id": site_id, "code": f"S{site_index + 1:02d}"})
        tank = {"id": g.id(), "site_id": site_id, "kind": "fixed_tank", "product": "diesel", "capacity_litres": "20000.00",
                "height_cm": 250}
        bowser = {"id": g.id(), "site_id": site_id, "kind": "mobile_bowser", "product": "diesel", "capacity_litres": "4000.00",
                  "height_cm": 120}
        stock: dict[str, Decimal] = {}
        for storage, opening in ((tank, Decimal("14000")), (bowser, Decimal("3000"))):
            table = calibration(Decimal(storage["capacity_litres"]), storage["height_cm"])
            storage["calibration"] = [[str(cm), str(litres)] for cm, litres in table]
            # The opening stock is what the table says for the opening dip, so the first day reconciles exactly.
            opening_dip = dip_for(table, opening)
            storage["opening_dip_cm"] = str(opening_dip)
            storage["opening_litres"] = str(fuel_oracle.litres_at(table, opening_dip).quantize(CENT))
            stock[storage["id"]] = fuel_oracle.litres_at(table, opening_dip)
            data["storages"].append(storage)
        tables = {storage["id"]: [(Decimal(cm), Decimal(litres)) for cm, litres in storage["calibration"]] for storage in (tank, bowser)}

        site_assets = []
        for index in range(machines):
            kind = list(TYPES)[index % len(TYPES)]
            rate, capacity = TYPES[kind]
            asset = {"id": g.id(), "site_id": site_id, "code": f"{kind[:3].upper()}-{site_index + 1}{index + 1:02d}", "type": kind,
                     "tank_capacity_litres": str(capacity), "meter_unit": "h", "meter": Decimal(str(g.rng.randint(2000, 9000))),
                     "rate": rate * g.factor(0.08), "burnt": Decimal(0), "since_refuel": 0}
            site_assets.append(asset)

        heavy = site_assets[0]  # the over-consuming machine
        seeded_days = {"refuel_without_usage": days // 3, "frequent_small_refuels": days // 4, "over_capacity": days // 5,
                       "tank_variance": days // 2 + 1, "delivery_variance": None}
        delivery_variance_done = False

        for offset in range(days):
            day = start + timedelta(days=offset)
            dispensed: dict[str, Decimal] = {tank["id"]: Decimal(0), bowser["id"]: Decimal(0)}
            for asset in site_assets:
                hours = Decimal(str(round(g.rng.uniform(6, 10), 1)))
                rate = asset["rate"] * (Decimal("1.25") if asset is heavy and offset >= days // 2 else Decimal(1))
                asset["meter"] += hours
                asset["burnt"] += hours * rate * g.factor(0.05)
                asset["since_refuel"] += 1
                refuel_today = asset["since_refuel"] >= 2 or asset["burnt"] > Decimal(asset["tank_capacity_litres"]) * Decimal("0.6")
                storage = bowser if asset["type"] != "haul_truck" else tank
                if refuel_today:
                    litres = asset["burnt"].quantize(CENT)
                    data["dispenses"].append(dispense(g, storage, asset, day, 15, litres))
                    dispensed[storage["id"]] += litres
                    asset["burnt"], asset["since_refuel"] = Decimal(0), 0

            # Seeded anomalies, each once, on machines other than the over-consuming one.
            other = site_assets[1]
            if offset == seeded_days["refuel_without_usage"]:
                # A normal top-up at 17:00, then 40 L more at 18:00 with the meter unchanged.
                if other["burnt"] > 0:
                    top_up = other["burnt"].quantize(CENT)
                    data["dispenses"].append(dispense(g, bowser, other, day, 17, top_up))
                    dispensed[bowser["id"]] += top_up
                    other["burnt"], other["since_refuel"] = Decimal(0), 0
                extra = dispense(g, bowser, other, day, 18, Decimal("40.00"))
                data["dispenses"].append(extra)
                dispensed[bowser["id"]] += Decimal("40.00")
                data["seeded"].append({"kind": "refuel_without_usage", "rule": "RULE-FUEL-06", "asset_id": other["id"], "day": day.isoformat(),
                                       "dispense_ids": [extra["id"]]})
            if offset == seeded_days["frequent_small_refuels"]:
                small_ids = []
                for minute in (0, 20, 40):
                    small = Decimal("12.00")
                    asset = site_assets[2]
                    asset["meter"] += Decimal("0.3")
                    small_dispense = dispense(g, bowser, asset, day, 19, small, minute)
                    small_ids.append(small_dispense["id"])
                    data["dispenses"].append(small_dispense)
                    dispensed[bowser["id"]] += small
                # The 36 L went into the machine: its next normal refuel tops up only what is left to burn.
                site_assets[2]["burnt"] = max(Decimal(0), site_assets[2]["burnt"] - Decimal("36.00"))
                data["seeded"].append({"kind": "frequent_small_refuels", "rule": "RULE-FUEL-08", "asset_id": site_assets[2]["id"], "day": day.isoformat(),
                                       "dispense_ids": small_ids, "also": "RULE-FUEL-05 (12 L in 0.3 h is far above the machine's rate)"})
            if offset == seeded_days["over_capacity"]:
                asset = site_assets[3]
                litres = (Decimal(asset["tank_capacity_litres"]) * Decimal("1.12")).quantize(CENT)
                asset["meter"] += Decimal("2.0")
                over = dispense(g, tank, asset, day, 20, litres)
                data["dispenses"].append(over)
                dispensed[tank["id"]] += litres
                # Filled (and more): the machine starts again from a full tank.
                asset["burnt"], asset["since_refuel"] = Decimal(0), 0
                data["seeded"].append({"kind": "over_capacity", "rule": "RULE-FUEL-02", "asset_id": asset["id"], "day": day.isoformat(),
                                       "dispense_ids": [over["id"]], "also": "RULE-FUEL-05 (a tank's worth for 2 h of work)"})

            for storage in (tank, bowser):
                stock[storage["id"]] -= dispensed[storage["id"]]
                # Deliveries: refill a storage that is below 35 % before the evening dip.
                capacity = Decimal(storage["capacity_litres"])
                if stock[storage["id"]] < capacity * Decimal("0.35"):
                    table = tables[storage["id"]]
                    before_cm = dip_for(table, stock[storage["id"]])
                    before = fuel_oracle.litres_at(table, before_cm)
                    ordered = (capacity * Decimal("0.85") - before).quantize(Decimal("1"))
                    received = ordered
                    note = ordered
                    if storage is tank and not delivery_variance_done:
                        received = (ordered * Decimal("0.985")).quantize(CENT)  # 1.5 % short at the dip
                        delivery_variance_done = True
                        data["seeded"].append({"kind": "delivery_variance", "rule": "RULE-FUEL-01", "storage_id": storage["id"], "day": day.isoformat(),
                                               "also": "RULE-FUEL-04 on that storage and day"})
                    after_cm = dip_for(table, before + received)
                    data["deliveries"].append({"id": g.id(), "storage_id": storage["id"], "at": at(day, 17), "supplier": "Demo Fuels",
                                               "delivery_note": f"DN-{g.rng.randint(10000, 99999)}", "ordered_litres": str(ordered),
                                               "delivered_litres": str(note), "dip_before_cm": str(before_cm), "dip_after_cm": str(after_cm),
                                               "photos": [g.sha256(), g.sha256()]})
                    stock[storage["id"]] = fuel_oracle.litres_at(table, after_cm) + (stock[storage["id"]] - before)
                if storage is tank and offset == seeded_days["tank_variance"]:
                    stock[storage["id"]] -= Decimal("150.00")
                    data["seeded"].append({"kind": "tank_variance", "rule": "RULE-FUEL-04", "storage_id": tank["id"], "day": day.isoformat()})
                dip_cm = dip_for(tables[storage["id"]], stock[storage["id"]])
                # The attendant reads the dip; from now on the stock is what the table says for that reading.
                stock[storage["id"]] = fuel_oracle.litres_at(tables[storage["id"]], dip_cm)
                data["dips"].append({"id": g.id(), "storage_id": storage["id"], "at": at(day, 21), "dip_cm": str(dip_cm), "photo": g.sha256()})

        data["seeded"].append({"kind": "overconsumption", "rule": "RULE-FUEL-05", "asset_id": heavy["id"], "from_day": (start + timedelta(days=days // 2)).isoformat()})
        for asset in site_assets:
            data["assets"].append({key: asset[key] for key in ("id", "site_id", "code", "type", "tank_capacity_litres", "meter_unit")})

    data["golden"] = {**fuel_oracle.consumption(data), **fuel_oracle.reconciliation(data)}
    return data


def dispense(g: Generator, storage: dict, asset: dict, day: date, hour: int, litres: Decimal, minute: int = 0) -> dict:
    return {"id": g.id(), "storage_id": storage["id"], "asset_id": asset["id"], "at": at(day, hour, minute), "litres": str(litres),
            "meter": str(asset["meter"].quantize(TENTH)), "meter_unit": "h", "meter_photo": g.sha256(), "totalizer_photos": [g.sha256(), g.sha256()]}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--sites", type=int, default=1)
    parser.add_argument("--machines", type=int, default=8)
    parser.add_argument("--days", type=int, default=30)
    parser.add_argument("--start", type=date.fromisoformat, default=date(2026, 11, 2))
    parser.add_argument("--out", type=Path, help="write here instead of standard output")
    args = parser.parse_args(argv)
    text = json.dumps(generate(seed=args.seed, sites=args.sites, machines=args.machines, days=args.days, start=args.start),
                      indent=2, ensure_ascii=False, default=str) + "\n"
    if args.out:
        args.out.write_text(text, encoding="utf-8", newline="\n")
    else:
        sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
