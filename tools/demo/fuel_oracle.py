"""The fuel golden answers (Phase 3, P3.0): consumption, baselines, anomalies and tank reconciliation, computed plainly.

This is the **oracle** the Fuel module's engine (P3.10, P3.11) is checked against: written independently, as a
spreadsheet would compute it, with no shortcut and no shared code with the engine. Every rule follows the documents:

- Consumption interval (US-P3-FUEL-06, ADR-0047): for each refuel of an asset after its first, the litres of that refuel
  divided by the usage since the previous refuel (ΔH in hours or ΔKM). The refuel tops up what the interval burnt.
- Baseline (US-P3-FUEL-07 AC1): median and MAD of the asset's previous intervals (at most the last 20). With fewer than 8,
  the **type baseline**: median and MAD of the previous intervals of all assets of the same type.
- Anomaly (RULE-FUEL-05, computational engines §10.6): deviation (r − median) / median > 18 % **and**
  robust z = 0.6745 × (r − median) / MAD > 3.5. MAD below 1 % of the median is raised to 1 % of the median (no division
  by zero on very regular machines; recorded in ADR-0047). An interval with no usage is not a rate (RULE-FUEL-06 instead).
- Reconciliation (US-P3-FUEL-04, ADR-0046): per storage and day, opening (previous closing dip) + deliveries − dispenses
  − closing dip = variance; variance % = variance ÷ (dispenses of the day), or ÷ opening when nothing was dispensed.
  RULE-FUEL-04 when |variance| exceeds the larger of 0.5 % of that base and the **dip uncertainty**: what one dipstick
  step (0.1 cm) is worth in litres at the opening and at the closing level, added. A loss smaller than the instrument can
  measure cannot be told from a reading error; flagging it would raise false alerts on quiet days.
- Delivery check (RULE-FUEL-01): delivered litres vs (dip after − dip before), flagged when the gap is > 1 % of delivered.

All arithmetic is Decimal. Litres read from a dip are rounded to 0.01 L at the reading (as a person writes them down);
everything after is exact on those values. Rates are written to 0.001 and percentages to 0.01.
"""
from __future__ import annotations

from decimal import ROUND_HALF_EVEN, Decimal

WINDOW = 20
MINIMUM = 8
THRESHOLD = Decimal("0.18")
Z_LIMIT = Decimal("3.5")
Z_FACTOR = Decimal("0.6745")
MAD_FLOOR = Decimal("0.01")
TANK_TOLERANCE_PCT = Decimal("0.5")
DIP_STEP_CM = Decimal("0.1")
DELIVERY_TOLERANCE_PCT = Decimal("1")


def median(values: list[Decimal]) -> Decimal:
    ordered = sorted(values)
    middle = len(ordered) // 2
    if len(ordered) % 2:
        return ordered[middle]
    return (ordered[middle - 1] + ordered[middle]) / 2


def mad(values: list[Decimal]) -> Decimal:
    centre = median(values)
    return median([abs(value - centre) for value in values])


def litres_at(table: list[tuple[Decimal, Decimal]], dip_cm: Decimal) -> Decimal:
    """Calibration table (cm → litres, increasing): linear interpolation; outside the table is an error."""
    if dip_cm < table[0][0] or dip_cm > table[-1][0]:
        raise ValueError(f"dip {dip_cm} cm outside the calibration table")
    for (cm_low, litres_low), (cm_high, litres_high) in zip(table, table[1:]):
        if cm_low <= dip_cm <= cm_high:
            share = (dip_cm - cm_low) / (cm_high - cm_low)
            return litres_low + share * (litres_high - litres_low)
    return table[-1][1]


def dip_step_litres(table: list[tuple[Decimal, Decimal]], dip_cm: Decimal) -> Decimal:
    """What one dipstick step is worth in litres at this level (the table's slope there)."""
    upper = min(dip_cm + DIP_STEP_CM, table[-1][0])
    lower = max(upper - DIP_STEP_CM, table[0][0])
    return litres_at(table, upper) - litres_at(table, lower)


def q(value: Decimal, places: str) -> str:
    return str(value.quantize(Decimal(places), rounding=ROUND_HALF_EVEN))


def consumption(data: dict) -> dict:
    """Intervals, baselines and RULE-FUEL-05 anomalies, in refuel order per asset."""
    asset_type = {asset["id"]: asset["type"] for asset in data["assets"]}
    by_asset: dict[str, list[dict]] = {}
    for dispense in sorted(data["dispenses"], key=lambda item: item["at"]):
        if dispense.get("asset_id"):
            by_asset.setdefault(dispense["asset_id"], []).append(dispense)

    # Every interval of every asset, in time order, so a type baseline only uses what was known before.
    intervals: list[dict] = []
    for asset_id, refuels in by_asset.items():
        for previous, current in zip(refuels, refuels[1:]):
            usage = Decimal(current["meter"]) - Decimal(previous["meter"])
            interval = {"asset_id": asset_id, "type": asset_type[asset_id], "dispense_id": current["id"], "at": current["at"],
                        "litres": Decimal(current["litres"]), "usage": usage}
            if usage > 0:
                interval["rate"] = interval["litres"] / usage
            intervals.append(interval)
    intervals.sort(key=lambda item: item["at"])

    out: list[dict] = []
    for index, interval in enumerate(intervals):
        if "rate" not in interval:
            out.append({"dispense_id": interval["dispense_id"], "asset_id": interval["asset_id"], "usage": q(interval["usage"], "0.1"),
                        "rate": None, "baseline": None, "anomaly": False})
            continue
        earlier = [other for other in intervals[:index] if "rate" in other]
        own = [other["rate"] for other in earlier if other["asset_id"] == interval["asset_id"]][-WINDOW:]
        if len(own) >= MINIMUM:
            sample, source = own, "asset"
        else:
            sample = [other["rate"] for other in earlier if other["type"] == interval["type"]][-WINDOW:]
            source = "type"
        entry = {"dispense_id": interval["dispense_id"], "asset_id": interval["asset_id"], "usage": q(interval["usage"], "0.1"),
                 "rate": q(interval["rate"], "0.001"), "baseline": None, "anomaly": False}
        if len(sample) >= MINIMUM:
            centre = median(sample)
            spread = max(mad(sample), centre * MAD_FLOOR)
            deviation = (interval["rate"] - centre) / centre
            z = Z_FACTOR * (interval["rate"] - centre) / spread
            entry["baseline"] = {"source": source, "median": q(centre, "0.001"), "mad": q(spread, "0.001"), "samples": len(sample)}
            entry["deviation_pct"] = q(deviation * 100, "0.01")
            entry["z"] = q(z, "0.01")
            entry["anomaly"] = deviation > THRESHOLD and z > Z_LIMIT
        out.append(entry)
    return {"intervals": out}


def reconciliation(data: dict) -> dict:
    """Per storage and day: opening + deliveries − dispenses − closing = variance (RULE-FUEL-04); delivery checks (RULE-FUEL-01)."""
    tables = {storage["id"]: [(Decimal(cm), Decimal(litres)) for cm, litres in storage["calibration"]] for storage in data["storages"]}
    days: list[dict] = []
    for storage in data["storages"]:
        dips = sorted((dip for dip in data["dips"] if dip["storage_id"] == storage["id"]), key=lambda item: item["at"])
        opening = Decimal(storage["opening_litres"]).quantize(Decimal("0.01"), rounding=ROUND_HALF_EVEN)
        opening_cm = Decimal(storage["opening_dip_cm"])
        for dip in dips:
            day = dip["at"][:10]
            delivered = sum((Decimal(item["delivered_litres"]) for item in data["deliveries"]
                             if item["storage_id"] == storage["id"] and item["at"][:10] == day), Decimal(0))
            dispensed = sum((Decimal(item["litres"]) for item in data["dispenses"]
                             if item["storage_id"] == storage["id"] and item["at"][:10] == day), Decimal(0))
            table = tables[storage["id"]]
            closing_cm = Decimal(dip["dip_cm"])
            # A dip's litres are rounded to 0.01 L when read; every figure after is exact on those, so the
            # report's figures add up to the digit (opening + delivered − dispensed − closing = variance).
            closing = litres_at(table, closing_cm).quantize(Decimal("0.01"), rounding=ROUND_HALF_EVEN)
            variance = opening + delivered - dispensed - closing
            base = dispensed if dispensed > 0 else opening
            pct = variance / base * 100 if base > 0 else Decimal(0)
            uncertainty = dip_step_litres(table, opening_cm) + dip_step_litres(table, closing_cm)
            tolerance = max(base * TANK_TOLERANCE_PCT / 100, uncertainty)
            days.append({"storage_id": storage["id"], "day": day, "opening": q(opening, "0.01"), "delivered": q(delivered, "0.01"),
                         "dispensed": q(dispensed, "0.01"), "closing": q(closing, "0.01"), "variance": q(variance, "0.01"),
                         "variance_pct": q(pct, "0.01"), "tolerance": q(tolerance, "0.01"), "flag": abs(variance) > tolerance})
            opening, opening_cm = closing, closing_cm

    deliveries: list[dict] = []
    for delivery in data["deliveries"]:
        table = tables[delivery["storage_id"]]
        measured = litres_at(table, Decimal(delivery["dip_after_cm"])) - litres_at(table, Decimal(delivery["dip_before_cm"]))
        delivered = Decimal(delivery["delivered_litres"])
        gap_pct = (delivered - measured) / delivered * 100
        deliveries.append({"delivery_id": delivery["id"], "measured": q(measured, "0.01"), "gap_pct": q(gap_pct, "0.01"),
                           "flag": abs(gap_pct) > DELIVERY_TOLERANCE_PCT})
    return {"days": days, "deliveries": deliveries}
