#!/usr/bin/env python3
"""Weighbridge data generator (Phase 3, P3.0): trucks, customers, products and days of two-pass scale tickets.

Each site has a fleet of trucks (plate, hauler, registered tare, legal payload), customers (one blocked), products, and
operating hours 06:00–18:00. Every working day, trucks come in empty (first pass: tare), load, and leave (second pass:
gross); weights are in kilograms, rounded to the scale's 20 kg division; the net is gross − tare; each pass carries an
indicator photo (manual entry, P3: no indicator link). A truck needs at least 40 minutes between two outbound tickets.

Seeded anomalies, once each per site (the ground truth `seeded` lists the ticket each must flag, and only that one):
- `tare_deviation`        the first pass weighs the truck 6 % above its registered tare (RULE-WB-01, > 3 %);
- `overload`              the net is above the truck's legal payload (RULE-WB-02);
- `impossible_turnaround` the same truck leaves loaded 8 minutes after its previous ticket (RULE-WB-03, < 20 min);
- `after_hours`           a ticket at 19:30, outside the site's hours (RULE-WB-04);
- `missing_photo`         a manual weight without an indicator photo (RULE-WB-05);
- `blocked_customer`      a ticket for the blocked customer, issued with an override (RULE-WB-07).
One ticket is reversed (with a reason) and re-issued: no rule fires for a single reversal (RULE-WB-06 is a rate over time).

`golden` holds what `rules()` (a plain, independent implementation of RULE-WB-01 … 05 and 07) flags; the tests check it
equals the seeded ground truth.

Deterministic from the seed. Decimals as strings, units explicit (kg, t).

Usage: generate_weighbridge.py [--seed 1] [--sites 1] [--trucks 20] [--days 5] [--start 2026-11-02] [--out FILE]
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

DIVISION_KG = 20
OPEN, CLOSE = time(6), time(18)
MIN_TURNAROUND = timedelta(minutes=20)  # RULE-WB-03 (tenant setting, catalogue default)
TARE_DEVIATION = Decimal("0.03")  # RULE-WB-01
ANOMALIES = ("tare_deviation", "overload", "impossible_turnaround", "after_hours", "missing_photo", "blocked_customer")
PRODUCTS = (("GR-0-31.5", "Gravel 0/31.5"), ("GR-6-10", "Gravel 6/10"), ("SA-0-4", "Sand 0/4"), ("BL-500", "Rip-rap 300/500"))


class Generator:
    def __init__(self, seed: int) -> None:
        self.rng = random.Random(seed)

    def id(self) -> str:
        bits = self.rng.getrandbits(128) & ~(0xF << 76) & ~(0x3 << 62)
        return str(uuid.UUID(int=bits | (0x7 << 76) | (0x2 << 62)))

    def sha256(self) -> str:
        return f"sha256:{self.rng.getrandbits(256):064x}"

    def kg(self, low: int, high: int) -> int:
        return self.rng.randint(low // DIVISION_KG, high // DIVISION_KG) * DIVISION_KG

    def plate(self) -> str:
        letters = "ABCDEFGHJKLMNPRSTVWXYZ"
        return f"{self.rng.choice(letters)}{self.rng.choice(letters)}-{self.rng.randint(100, 999)}-{self.rng.choice(letters)}{self.rng.choice(letters)}"


def stamp(day: date, minutes: int) -> datetime:
    return datetime.combine(day, time(0), tzinfo=timezone.utc) + timedelta(minutes=minutes)


def tonnes(kg: int) -> str:
    return str((Decimal(kg) / 1000).quantize(Decimal("0.001")))


def generate(*, seed: int = 1, sites: int = 1, trucks: int = 20, days: int = 5, start: date = date(2026, 11, 2)) -> dict:
    g = Generator(seed)
    data: dict = {"seed": seed, "start": start.isoformat(), "days": days, "sites": [], "haulers": [], "trucks": [], "customers": [],
                  "products": [], "tickets": [], "reversals": [], "seeded": []}
    haulers = [{"id": g.id(), "name": name} for name in ("Transports Demo A", "Demo Haulage", "Camions du Nord", "Logistique Sud")]
    data["haulers"] = haulers
    for site_index in range(sites):
        site_id = g.id()
        data["sites"].append({"id": site_id, "code": f"S{site_index + 1:02d}", "opens": OPEN.isoformat(), "closes": CLOSE.isoformat(),
                              "ticket_prefix": f"S{site_index + 1:02d}-"})
        fleet = []
        for _ in range(trucks):
            fleet.append({"id": g.id(), "site_id": site_id, "plate": g.plate(), "hauler_id": g.rng.choice(haulers)["id"],
                          "registered_tare_kg": g.kg(9000, 16000), "max_payload_kg": g.kg(20000, 32000), "axles": g.rng.choice((3, 4, 5))})
        customers = [{"id": g.id(), "site_ids": [site_id], "legal_name": f"Demo Client {index + 1:02d}", "tax_id": f"TX{g.rng.randint(100000, 999999)}",
                      "status": "active", "basis": "t" if index % 3 else "m3_loose"} for index in range(10)]
        customers[-1]["status"] = "blocked"
        customers[-1]["blocked_reason"] = "Unpaid invoices"
        products = [{"id": g.id(), "code": code, "name": name, "unit": "t"} for code, name in PRODUCTS]
        data["trucks"] += fleet
        data["customers"] += customers
        data["products"] += products
        active = [customer for customer in customers if customer["status"] == "active"]

        number = 0
        for offset in range(days):
            day = start + timedelta(days=offset)
            # Each truck's next possible arrival, in minutes from midnight; trips continue until closing.
            next_free = {truck["id"]: 6 * 60 + g.rng.randint(0, 90) for truck in fleet}
            for truck in fleet:
                while next_free[truck["id"]] + 30 < 18 * 60:
                    first_at = next_free[truck["id"]]
                    second_at = first_at + g.rng.randint(15, 30)
                    tare = truck["registered_tare_kg"] + g.kg(-200, 200)
                    net = g.kg(int(truck["max_payload_kg"] * 0.6), int(truck["max_payload_kg"] * 0.97))
                    number += 1
                    data["tickets"].append(ticket(g, site_id, number, truck, g.rng.choice(active), g.rng.choice(products),
                                                  stamp(day, first_at), stamp(day, second_at), tare, tare + net))
                    next_free[truck["id"]] = second_at + g.rng.randint(45, 120)

        site_tickets = [item for item in data["tickets"] if item["site_id"] == site_id]
        day0 = start
        last = start + timedelta(days=days - 1)

        def seed_ticket(kind: str, item: dict) -> None:
            data["seeded"].append({"kind": kind, "site_id": site_id, "ticket_id": item["id"]})

        # tare_deviation: the first pass 6 % above the registered tare.
        victim = site_tickets[3]
        truck = next(t for t in fleet if t["id"] == victim["truck_id"])
        heavier = round(truck["registered_tare_kg"] * 1.06 / DIVISION_KG) * DIVISION_KG
        set_weights(victim, heavier, heavier + victim["net_kg"])
        seed_ticket("tare_deviation", victim)
        # overload: net 8 % above the legal payload.
        victim = site_tickets[7]
        truck = next(t for t in fleet if t["id"] == victim["truck_id"])
        over = round(truck["max_payload_kg"] * 1.08 / DIVISION_KG) * DIVISION_KG
        set_weights(victim, victim["first_pass"]["weight_kg"], victim["first_pass"]["weight_kg"] + over)
        seed_ticket("overload", victim)
        # impossible turnaround: a second outbound ticket 8 minutes after a truck's previous one.
        base = site_tickets[11]
        truck = next(t for t in fleet if t["id"] == base["truck_id"])
        leave = datetime.fromisoformat(base["second_pass"]["at"])
        number += 1
        quick = ticket(g, site_id, number, truck, g.rng.choice(active), g.rng.choice(products), leave + timedelta(minutes=2),
                       leave + timedelta(minutes=8), base["first_pass"]["weight_kg"], base["first_pass"]["weight_kg"] + g.kg(15000, 18000))
        data["tickets"].append(quick)
        seed_ticket("impossible_turnaround", quick)
        # after hours: 19:30 on the last day.
        truck = fleet[0]
        number += 1
        late = ticket(g, site_id, number, truck, g.rng.choice(active), g.rng.choice(products), stamp(last, 19 * 60 + 15), stamp(last, 19 * 60 + 30),
                      truck["registered_tare_kg"], truck["registered_tare_kg"] + g.kg(15000, 18000))
        data["tickets"].append(late)
        seed_ticket("after_hours", late)
        # missing photo on a second pass.
        victim = site_tickets[15]
        victim["second_pass"]["photo"] = None
        seed_ticket("missing_photo", victim)
        # blocked customer, with the site manager's override.
        victim = site_tickets[19]
        victim["customer_id"] = customers[-1]["id"]
        victim["override"] = {"by": "site_manager", "reason": "Cash payment on site"}
        seed_ticket("blocked_customer", victim)
        # A reversal and its corrected ticket: wrong product chosen.
        original = site_tickets[23]
        data["reversals"].append({"id": g.id(), "site_id": site_id, "original_ticket_id": original["id"], "reason": "Wrong product",
                                  "weighmaster_signed": True, "controller_countersigned": True, "at": original["second_pass"]["at"]})
        del day0

    data["golden"] = rules(data)
    return data


def ticket(g: Generator, site_id: str, number: int, truck: dict, customer: dict, product: dict, first_at: datetime, second_at: datetime,
           first_kg: int, second_kg: int) -> dict:
    item = {"id": g.id(), "site_id": site_id, "number": number, "direction": "outbound", "truck_id": truck["id"],
            "driver": f"Driver {g.rng.randint(1, 60):02d}", "customer_id": customer["id"], "product_id": product["id"],
            "first_pass": {"at": first_at.isoformat(timespec="seconds"), "weight_kg": first_kg, "source": "MANUAL", "photo": g.sha256()},
            "second_pass": {"at": second_at.isoformat(timespec="seconds"), "weight_kg": second_kg, "source": "MANUAL", "photo": g.sha256()}}
    set_weights(item, first_kg, second_kg)
    return item


def set_weights(item: dict, tare_kg: int, gross_kg: int) -> None:
    item["first_pass"]["weight_kg"] = tare_kg
    item["second_pass"]["weight_kg"] = gross_kg
    item["net_kg"] = gross_kg - tare_kg
    item["net_t"] = tonnes(gross_kg - tare_kg)


def rules(data: dict) -> dict:
    """RULE-WB-01 … 05 and 07, written plainly: the reference the Weighbridge module's rules are checked against."""
    trucks = {truck["id"]: truck for truck in data["trucks"]}
    customers = {customer["id"]: customer for customer in data["customers"]}
    sites = {site["id"]: site for site in data["sites"]}
    flags: list[dict] = []
    previous_leave: dict[str, datetime] = {}
    for item in sorted(data["tickets"], key=lambda entry: entry["second_pass"]["at"]):
        truck = trucks[item["truck_id"]]
        tare = Decimal(item["first_pass"]["weight_kg"])
        registered = Decimal(truck["registered_tare_kg"])
        if abs(tare - registered) / registered > TARE_DEVIATION:
            flags.append({"rule": "RULE-WB-01", "ticket_id": item["id"]})
        if item["net_kg"] > truck["max_payload_kg"]:
            flags.append({"rule": "RULE-WB-02", "ticket_id": item["id"]})
        leave = datetime.fromisoformat(item["second_pass"]["at"])
        before = previous_leave.get(truck["id"])
        if before is not None and leave - before < MIN_TURNAROUND:
            flags.append({"rule": "RULE-WB-03", "ticket_id": item["id"]})
        previous_leave[truck["id"]] = leave
        site = sites[item["site_id"]]
        opens, closes = time.fromisoformat(site["opens"]), time.fromisoformat(site["closes"])
        if not opens <= leave.time() <= closes:
            flags.append({"rule": "RULE-WB-04", "ticket_id": item["id"]})
        if item["first_pass"]["photo"] is None or item["second_pass"]["photo"] is None:
            flags.append({"rule": "RULE-WB-05", "ticket_id": item["id"]})
        if customers[item["customer_id"]]["status"] == "blocked":
            flags.append({"rule": "RULE-WB-07", "ticket_id": item["id"]})
    return {"flags": flags}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--sites", type=int, default=1)
    parser.add_argument("--trucks", type=int, default=20)
    parser.add_argument("--days", type=int, default=5)
    parser.add_argument("--start", type=date.fromisoformat, default=date(2026, 11, 2))
    parser.add_argument("--out", type=Path, help="write here instead of standard output")
    args = parser.parse_args(argv)
    text = json.dumps(generate(seed=args.seed, sites=args.sites, trucks=args.trucks, days=args.days, start=args.start),
                      indent=2, ensure_ascii=False) + "\n"
    if args.out:
        args.out.write_text(text, encoding="utf-8", newline="
")
    else:
        sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
